using Keel.Compliance;

namespace Keel.Web.Services.Visuals;

/// <summary>One line of the structure as a treemap node. An assembly's standing is the worst standing of the purchased parts below it.</summary>
public sealed class CmpNode
{
    public required string Path { get; init; }
    public required string Label { get; init; }
    public string? Sublabel { get; init; }
    public string Standing { get; set; } = Standings.NotRequired;
    public decimal Quantity { get; init; }
    public string Unit { get; init; } = "";
    public string? Revision { get; init; }
    public CmpNode? Parent { get; set; }
    public List<CmpNode> Children { get; } = [];
    public bool IsLeaf => Children.Count == 0;

    private double _quantity = -1, _count = -1;

    /// <summary>The area of the node: extended quantity (an assembly adds up what it contains) or the number of parts below it.</summary>
    public double Value(bool countMetric)
    {
        if (countMetric)
        {
            if (_count < 0) _count = Children.Count == 0 ? 1 : Children.Sum(c => c.Value(true));
            return _count;
        }
        if (_quantity < 0) _quantity = Children.Count == 0 ? Math.Max((double)Quantity, 0.000001) : Children.Sum(c => c.Value(false));
        return _quantity;
    }

    public int Descendants => Children.Sum(c => 1 + c.Descendants);
}

public sealed record CmpTile(CmpNode Node, string Id, double X, double Y, double Width, double Height, double Value, double Share)
{
    public bool Drillable => Node.Children.Count > 0;
}

/// <summary>
/// The structure of a compliance report as a treemap tree (ADR-0029, ADR-0040). It is built only from the lines of the report, which holds only what the caller may read,
/// so nothing about hidden parts can appear. Each node's standing is for ONE regulation; an assembly takes the worst standing below it (non-compliant, then no
/// declaration, expired, unknown, exemption, compliant).
/// </summary>
public static class ComplianceTreemapModel
{
    private static readonly string[] WorstFirst =
        [Standings.NonCompliant, Standings.Gap, Standings.Expired, Standings.Unverified, Standings.Exempt, Standings.Compliant];

    public static int Rank(string standing) { var i = Array.IndexOf(WorstFirst, standing); return i < 0 ? WorstFirst.Length : i; }

    public static CmpNode Build(ComplianceReport report, string regulation)
    {
        var root = new CmpNode { Path = "", Label = report.Number, Sublabel = report.Name };
        var byPath = new Dictionary<string, CmpNode>(StringComparer.Ordinal) { [""] = root };
        foreach (var line in report.Lines.Where(l => l.Path.Length > 0).OrderBy(l => l.Level).ThenBy(l => l.Path, StringComparer.Ordinal))
        {
            var cut = line.Path.LastIndexOf('.');
            var parentPath = cut < 0 ? "" : line.Path[..cut];
            var parent = byPath.GetValueOrDefault(parentPath) ?? root; // a parent the caller may not read is not in the report: its readable children hang from the root
            var path = line.Path;
            for (var dup = 1; byPath.ContainsKey(path); dup++) path = $"{line.Path}#{dup.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var node = new CmpNode
            {
                Path = path, Label = line.Number, Sublabel = line.Name, Quantity = line.Quantity, Unit = line.Unit, Revision = line.Revision, Parent = parent,
                Standing = line.Required && line.Standings.TryGetValue(regulation, out var s) ? s.Standing : Standings.NotRequired,
            };
            parent.Children.Add(node);
            byPath[path] = node;
        }
        // A root without readable lines is a part itself.
        if (root.Children.Count == 0 && report.Lines.FirstOrDefault(l => l.Path.Length == 0) is { Required: true } self && self.Standings.TryGetValue(regulation, out var own)) root.Standing = own.Standing;
        Aggregate(root);
        return root;
    }

    private static string Aggregate(CmpNode n)
    {
        if (n.Children.Count == 0) return n.Standing;
        var worst = Standings.NotRequired;
        foreach (var c in n.Children)
        {
            var s = Aggregate(c);
            if (s != Standings.NotRequired && (worst == Standings.NotRequired || Rank(s) < Rank(worst))) worst = s;
        }
        n.Standing = worst;
        return worst;
    }

    public static CmpNode? Find(CmpNode root, string path)
    {
        if (path.Length == 0) return root;
        var stack = new Stack<CmpNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.Path == path) return n;
            foreach (var c in n.Children) stack.Push(c);
        }
        return null;
    }

    /// <summary>One level of tiles for the children of <paramref name="focus"/>; more than <paramref name="maxTiles"/> children are limited to the largest ones (the rest stay in the table).</summary>
    public static IReadOnlyList<CmpTile> Layout(CmpNode focus, double width, double height, bool countMetric, int maxTiles = 300)
    {
        var total = Math.Max(focus.Value(countMetric), double.Epsilon);
        var children = focus.Children.OrderByDescending(c => c.Value(countMetric)).ThenBy(c => c.Path, StringComparer.Ordinal).Take(maxTiles).ToList();
        var byId = children.ToDictionary(c => c.Path, StringComparer.Ordinal);
        var placed = SquarifiedTreemap.Layout([.. children.Select(c => new TreemapItem(c.Path, c.Value(countMetric)))], 0, 0, width, height);
        return [.. placed.Select(t => new CmpTile(byId[t.Id], t.Id, t.X, t.Y, t.Width, t.Height, t.Value, t.Value / total))];
    }

    /// <summary>The fill class and the hatching of a standing. Every standing has BOTH a colour and a pattern, so the picture reads in black and white (ADR-0029).</summary>
    public static (string Class, string? Pattern) Style(string standing) => standing switch
    {
        Standings.Compliant => ("released", null),
        Standings.Exempt => ("released", "dots"),
        Standings.NonCompliant => ("unresolved", "cross"),
        Standings.Gap => ("underreview", "horiz"),
        Standings.Expired => ("obsolete", "vert"),
        Standings.Unverified => ("inwork", "hatch"),
        _ => ("other", null),
    };

    public static string Badge(string standing) => standing switch
    {
        Standings.Compliant or Standings.Exempt => "state-approved",
        Standings.NonCompliant => "kind-blocked",
        Standings.Gap or Standings.Unverified => "state-underreview",
        _ => "state-obsolete",
    };
}
