using System.Globalization;

namespace Keel.Web.Services.Visuals;

/// <summary>A node to place: its layer (column, left to right) and its size. Order of the input is the initial order inside a layer.</summary>
public sealed record LayoutNodeInput(string Id, int Layer, double Width, double Height);

public sealed record LayoutEdgeInput(string From, string To);

public sealed record PlacedNode(string Id, int Layer, int Row, double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double MidY => Y + Height / 2;
}

/// <summary>An edge from the right side of its source to the left side of its target, as a cubic curve.</summary>
public sealed record PlacedEdge(string From, string To, double X1, double Y1, double X2, double Y2)
{
    public string Path
    {
        get
        {
            var mid = (X1 + X2) / 2;
            return string.Create(CultureInfo.InvariantCulture, $"M{X1:0.##} {Y1:0.##} C{mid:0.##} {Y1:0.##} {mid:0.##} {Y2:0.##} {X2:0.##} {Y2:0.##}");
        }
    }
}

public sealed record GraphLayout(IReadOnlyList<PlacedNode> Nodes, IReadOnlyList<PlacedEdge> Edges, double Width, double Height, int Crossings);

public sealed record LayoutOptions(double Padding = 24, double LayerGap = 84, double RowGap = 14, int Sweeps = 6);

/// <summary>
/// Layered left-to-right graph layout (UX-3, ADR-0029): the caller assigns each node a layer; this orders the nodes inside each layer to cut edge crossings
/// (barycenter sweeps down and up, keeping the best order seen) and then places them in columns with no overlap. Pure and deterministic: the same input always
/// gives the same output (ties are broken by the input position and then the id). Edges whose ends are unknown are ignored; a node in a layer on its own keeps
/// its input position. Cost is about O(sweeps x (nodes log nodes + edges)) plus one crossing count per sweep, which stays well inside 300 ms for 500 nodes.
/// </summary>
public static class LayeredLayout
{
    public static GraphLayout Compute(IReadOnlyList<LayoutNodeInput> nodes, IReadOnlyList<LayoutEdgeInput> edges, LayoutOptions? options = null)
    {
        options ??= new LayoutOptions();
        if (nodes.Count == 0) return new GraphLayout([], [], options.Padding * 2, options.Padding * 2, 0);

        var byId = new Dictionary<string, int>(nodes.Count, StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++)
            if (!byId.TryAdd(nodes[i].Id, i)) throw new ArgumentException($"Duplicate node id '{nodes[i].Id}'.", nameof(nodes));

        var layers = nodes.Select(n => n.Layer).Distinct().Order().ToArray();
        var layerIndex = layers.Select((l, i) => (l, i)).ToDictionary(x => x.l, x => x.i);
        var columns = layers.Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < nodes.Count; i++) columns[layerIndex[nodes[i].Layer]].Add(i);

        var valid = new List<(int From, int To)>();
        var seen = new HashSet<(int, int)>();
        foreach (var e in edges)
            if (byId.TryGetValue(e.From, out var f) && byId.TryGetValue(e.To, out var t) && f != t && seen.Add((f, t))) valid.Add((f, t));
        var neighbours = new List<int>[nodes.Count];
        for (var i = 0; i < nodes.Count; i++) neighbours[i] = [];
        foreach (var (f, t) in valid) { neighbours[f].Add(t); neighbours[t].Add(f); }

        var position = new double[nodes.Count]; // normalised 0..1 position inside its column
        void Refresh()
        {
            foreach (var col in columns)
                for (var r = 0; r < col.Count; r++) position[col[r]] = col.Count == 1 ? 0.5 : (double)r / (col.Count - 1);
        }
        Refresh();

        var best = columns.Select(c => c.ToList()).ToArray();
        var bestCrossings = Crossings(columns, valid, layerIndex, nodes);
        for (var sweep = 0; sweep < options.Sweeps && bestCrossings > 0; sweep++)
        {
            var down = sweep % 2 == 0;
            for (var step = 1; step < columns.Length; step++)
            {
                var c = down ? step : columns.Length - 1 - step;
                var reference = down ? c - 1 : c + 1;
                Reorder(columns[c], reference, layerIndex, nodes, neighbours, position);
                Refresh();
            }
            var crossings = Crossings(columns, valid, layerIndex, nodes);
            if (crossings < bestCrossings) { bestCrossings = crossings; best = columns.Select(c => c.ToList()).ToArray(); }
        }
        for (var c = 0; c < columns.Length; c++) columns[c] = best[c];

        // Coordinates: columns left to right, each column centred on the tallest one.
        var widths = columns.Select(col => col.Max(i => nodes[i].Width)).ToArray();
        var heights = columns.Select(col => col.Sum(i => nodes[i].Height) + options.RowGap * (col.Count - 1)).ToArray();
        var tallest = heights.Max();
        var placed = new PlacedNode[nodes.Count];
        var x = options.Padding;
        for (var c = 0; c < columns.Length; c++)
        {
            var y = options.Padding + (tallest - heights[c]) / 2;
            for (var r = 0; r < columns[c].Count; r++)
            {
                var n = nodes[columns[c][r]];
                placed[columns[c][r]] = new PlacedNode(n.Id, n.Layer, r, x, y, n.Width, n.Height);
                y += n.Height + options.RowGap;
            }
            x += widths[c] + options.LayerGap;
        }

        var placedEdges = valid.Select(e =>
        {
            var (from, to) = (placed[e.From], placed[e.To]);
            var forward = from.X <= to.X;
            return new PlacedEdge(from.Id, to.Id, forward ? from.Right : from.X, from.MidY, forward ? to.X : to.Right, to.MidY);
        }).ToList();

        return new GraphLayout(placed, placedEdges, x - options.LayerGap + options.Padding, tallest + options.Padding * 2, bestCrossings);
    }

    private static void Reorder(List<int> column, int reference, Dictionary<int, int> layerIndex, IReadOnlyList<LayoutNodeInput> nodes, List<int>[] neighbours, double[] position)
    {
        var referenceLayer = reference;
        var keys = new Dictionary<int, double>(column.Count);
        for (var r = 0; r < column.Count; r++)
        {
            var node = column[r];
            double sum = 0;
            var count = 0;
            foreach (var m in neighbours[node])
                if (layerIndex[nodes[m].Layer] == referenceLayer) { sum += position[m]; count++; }
            keys[node] = count == 0 ? position[node] : sum / count;
        }
        var current = column.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        column.Sort((a, b) =>
        {
            var c = keys[a].CompareTo(keys[b]);
            if (c != 0) return c;
            c = current[a].CompareTo(current[b]);
            return c != 0 ? c : string.CompareOrdinal(nodes[a].Id, nodes[b].Id);
        });
    }

    /// <summary>Crossings between edges whose ends sit in the same pair of columns (the only ones the ordering can change).</summary>
    internal static int Crossings(List<int>[] columns, List<(int From, int To)> edges, Dictionary<int, int> layerIndex, IReadOnlyList<LayoutNodeInput> nodes)
    {
        var row = new int[nodes.Count];
        foreach (var col in columns) for (var r = 0; r < col.Count; r++) row[col[r]] = r;
        var groups = new Dictionary<(int, int), List<(int A, int B)>>();
        foreach (var (f, t) in edges)
        {
            var (lf, lt) = (layerIndex[nodes[f].Layer], layerIndex[nodes[t].Layer]);
            var (a, b) = lf <= lt ? (f, t) : (t, f);
            var key = (Math.Min(lf, lt), Math.Max(lf, lt));
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add((row[a], row[b]));
        }
        var total = 0;
        foreach (var list in groups.Values)
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                    if ((list[i].A - list[j].A) * (list[i].B - list[j].B) < 0) total++;
        return total;
    }
}
