using System.Globalization;
using Keel.Bom;
using Keel.Core;

namespace Keel.Web.Services.Visuals;

public enum TreemapMetric { ExtendedQuantity, DescendantCount }

public enum TreemapColour { State, Markings }

/// <summary>One part occurrence of the exploded structure. A restricted line has no number, name, state or markings: only its find number, quantity and unit, which the BOM service already exposes (A4).</summary>
public sealed class TmNode
{
    public required string Path { get; init; }
    public required string Label { get; init; }
    public string? Sublabel { get; init; }
    public bool Redacted { get; init; }
    public bool Unresolved { get; init; }
    public string? Number { get; init; }
    public string? State { get; init; }
    public string? StateLabel { get; init; }
    public string? Banner { get; init; }
    public string MarkingClass { get; init; } = "Restricted";
    public Markings? Markings { get; init; }
    public decimal Quantity { get; init; }
    public decimal ExtendedQuantity { get; init; }
    public string Unit { get; init; } = "";
    public int FindNumber { get; init; }
    public int Level { get; init; }
    public TmNode? Parent { get; set; }
    public List<TmNode> Children { get; } = [];
    public bool IsLeaf => Children.Count == 0;

    private double _quantityValue = -1, _countValue = -1;

    /// <summary>The area the node asks for: extended quantity (an assembly adds up what it contains) or the number of parts below it (a part counts as 1).</summary>
    public double Value(TreemapMetric metric)
    {
        if (metric == TreemapMetric.DescendantCount)
        {
            if (_countValue < 0) _countValue = Children.Count == 0 ? 1 : Children.Sum(c => c.Value(metric));
            return _countValue;
        }
        if (_quantityValue < 0) _quantityValue = Children.Count == 0 ? Math.Max((double)ExtendedQuantity, 0.000001) : Children.Sum(c => c.Value(metric));
        return _quantityValue;
    }

    public int Descendants => Children.Sum(c => 1 + c.Descendants);
}

public sealed record TmTile(TmNode? Node, string Id, double X, double Y, double Width, double Height, int Depth, string? ParentId, bool IsOthers, int OthersCount, bool OthersHasRestricted, double Value, double Share)
{
    public bool Drillable => Node is { Redacted: false, IsLeaf: false };
}

public sealed record TreemapLayoutResult(IReadOnlyList<TmTile> Tiles, int TileCount, bool Folded);

/// <summary>
/// Builds the treemap tree from the flat, depth-first explosion the BOM service returns (ADR-0029). The tree holds exactly the lines the service returned: nothing is
/// queried here, so a part the caller may not read is only the placeholder line it already was. Restricted sibling lines are merged into ONE block per parent,
/// labelled "Restricted" and sized by the sum of their quantities (or 1 each for the count metric); the block does not say how many lines it holds.
/// </summary>
public static class BomTreemapModel
{
    public const string RestrictedLabel = "Restricted";
    public const string RootPath = "";

    public static TmNode Build(string rootNumber, string rootName, IReadOnlyList<BomLine> lines)
    {
        var root = new TmNode { Path = RootPath, Label = rootNumber, Sublabel = rootName, Level = 0, MarkingClass = "Root" };
        var byPath = new Dictionary<string, TmNode>(lines.Count, StringComparer.Ordinal) { [RootPath] = root };
        var restricted = new Dictionary<string, (decimal Qty, decimal Ext, string Unit, int Find, int Level)>(StringComparer.Ordinal);
        var order = new List<(string ParentPath, TmNode Node)>();

        foreach (var line in lines)
        {
            var cut = line.Path.LastIndexOf('.');
            var parentPath = cut < 0 ? RootPath : line.Path[..cut];
            if (!byPath.TryGetValue(parentPath, out var parent)) continue; // the explosion is depth-first: a missing parent means the line was cut off by a depth limit
            if (line.Redacted)
            {
                var key = parentPath;
                var (q, e, u, f, lv) = restricted.TryGetValue(key, out var cur) ? cur : (0m, 0m, line.Unit, line.FindNumber, line.Level);
                restricted[key] = (q + line.Quantity, e + line.ExtendedQuantity, u == line.Unit ? u : "mixed", Math.Min(f, line.FindNumber), lv);
                continue;
            }
            // Two lines can share a path when effectivity splits a find number: the first keeps it, the others get a suffix so every tile id is unique.
            var path = line.Path;
            for (var dup = 1; byPath.ContainsKey(path); dup++) path = $"{line.Path}#{dup.ToString(CultureInfo.InvariantCulture)}";
            var node = new TmNode
            {
                Path = path,
                Label = line.Number ?? line.Path,
                Sublabel = line.Name,
                Unresolved = line.Unresolved,
                Number = line.Number,
                State = line.State,
                StateLabel = line.Circular ? "Circular reference" : line.StateLabel,
                Banner = line.Banner,
                MarkingClass = ClassOf(line.Markings),
                Markings = line.Markings,
                Quantity = line.Quantity,
                ExtendedQuantity = line.ExtendedQuantity,
                Unit = line.Unit,
                FindNumber = line.FindNumber,
                Level = line.Level,
                Parent = parent,
            };
            parent.Children.Add(node);
            byPath[path] = node;
            order.Add((parentPath, node));
        }

        foreach (var (parentPath, (qty, ext, unit, find, level)) in restricted.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var parent = byPath[parentPath];
            parent.Children.Add(new TmNode
            {
                Path = (parentPath.Length == 0 ? "" : parentPath + ".") + "~restricted",
                Label = RestrictedLabel,
                Redacted = true,
                Quantity = qty,
                ExtendedQuantity = ext,
                Unit = unit,
                FindNumber = find,
                Level = level,
                Parent = parent,
            });
        }
        return root;
    }

    public static string ClassOf(Markings? m) => m is null ? "Restricted"
        : m.ExportJurisdiction == ExportJurisdiction.Itar ? "ITAR"
        : m.ExportJurisdiction == ExportJurisdiction.Ear && m.Eccn != Markings.Ear99 ? "EAR"
        : m.IsCui ? "CUI"
        : "Unmarked";

    /// <summary>The node with this path (depth-first search; a click needs it once, so no index is kept).</summary>
    public static TmNode? Find(TmNode root, string path)
    {
        if (path.Length == 0) return root;
        var stack = new Stack<TmNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Path == path) return node;
            foreach (var c in node.Children) stack.Push(c);
        }
        return null;
    }

    /// <summary>
    /// Lays out the children of <paramref name="focus"/> in the rectangle: one level of tiles, and inside each tile large enough one more level of its own children, until
    /// <paramref name="maxTiles"/> tiles are used. Siblings beyond the budget are folded into one "others" tile that counts the readable items it holds (VIS-4).
    /// </summary>
    public static TreemapLayoutResult Layout(TmNode focus, double width, double height, TreemapMetric metric, int maxTiles = 400)
    {
        var tiles = new List<TmTile>();
        var folded = false;
        var total = Math.Max(focus.Value(metric), double.Epsilon);

        var first = PlaceLevel(focus, 0, 0, width, height, 1, null, Math.Min(maxTiles, 300), out var firstFolded);
        folded |= firstFolded;
        tiles.AddRange(first);
        // Nest one more level inside the biggest tiles while the budget lasts.
        foreach (var tile in first.OrderByDescending(t => t.Width * t.Height).ThenBy(t => t.Id, StringComparer.Ordinal))
        {
            var remaining = maxTiles - tiles.Count;
            if (remaining < 2) break;
            if (!tile.Drillable || tile.Width < 96 || tile.Height < 72) continue;
            var inner = PlaceLevel(tile.Node!, tile.X + 3, tile.Y + 19, tile.Width - 6, tile.Height - 22, 2, tile.Id, Math.Min(remaining, 80), out var innerFolded);
            folded |= innerFolded;
            tiles.AddRange(inner);
        }
        return new TreemapLayoutResult(tiles, tiles.Count, folded);

        List<TmTile> PlaceLevel(TmNode parent, double x, double y, double w, double h, int depth, string? parentId, int budget, out bool didFold)
        {
            didFold = false;
            var byId = parent.Children.ToDictionary(c => c.Path, StringComparer.Ordinal);
            var items = parent.Children.Select(c => new TreemapItem(c.Path, c.Value(metric))).ToList();
            var limited = SquarifiedTreemap.Limit(items, Math.Max(2, budget));
            var all = limited.Others is null ? limited.Items : [.. limited.Items, limited.Others];
            didFold = limited.Others is not null;
            var restrictedInOthers = didFold && limited.Others is not null && parent.Children.Any(c => c.Redacted && !limited.Items.Any(i => i.Id == c.Path));
            var placed = SquarifiedTreemap.Layout(all, x, y, w, h);
            var result = new List<TmTile>(placed.Count);
            foreach (var t in placed)
            {
                if (t.Id == SquarifiedTreemap.OthersId)
                {
                    var readable = limited.OthersCount - (restrictedInOthers ? parent.Children.Count(c => c.Redacted && !limited.Items.Any(i => i.Id == c.Path)) : 0);
                    result.Add(new TmTile(null, parent.Path + "~others", t.X, t.Y, t.Width, t.Height, depth, parentId, true, readable, restrictedInOthers, t.Value, t.Value / total));
                }
                else result.Add(new TmTile(byId[t.Id], t.Id, t.X, t.Y, t.Width, t.Height, depth, parentId, false, 0, false, t.Value, t.Value / total));
            }
            return result;
        }
    }

    public static string FormatQty(decimal d) => d.ToString("0.######", CultureInfo.InvariantCulture);
}
