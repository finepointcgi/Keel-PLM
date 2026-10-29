using System.Globalization;

namespace Keel.Web.Services.Visuals;

/// <summary>Where each arrow key leads from a node: along an edge to the right or left, or to the previous or next node of the same column.</summary>
public sealed record NodeNavigation(IReadOnlyList<string> Right, IReadOnlyList<string> Left, string? Up, string? Down);

/// <summary>Shared geometry of the impact map: node sizes and outlines per kind, label shortening, keyboard navigation. Used by the live component and the SVG export.</summary>
public static class GraphGeometry
{
    public const double NodeWidth = 176;
    public const double NodeHeight = 56;
    public const int LabelChars = 24;

    public static (double W, double H) Size(GraphNodeKind kind) => kind switch
    {
        GraphNodeKind.Change => (196, 64),
        GraphNodeKind.More => (176, 44),
        GraphNodeKind.Restricted => (160, 44),
        _ => (NodeWidth, NodeHeight),
    };

    public static string Shorten(string? text, int max = LabelChars)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");
    }

    private static string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// The outline: change = rounded rectangle with a double border (drawn by the caller), part and assembly = rectangle, document = rectangle with a cut corner,
    /// manufacturing BOM = hexagon, conflict = octagon, restricted = rectangle (hatched), more = pill. Shape is part of the legend, so no meaning is carried by colour alone.
    /// </summary>
    public static string Outline(GraphNodeKind kind, double x, double y, double w, double h)
    {
        var (r, b) = (x + w, y + h);
        switch (kind)
        {
            case GraphNodeKind.Document:
                return $"M{F(x)} {F(y)} H{F(r - 14)} L{F(r)} {F(y + 14)} V{F(b)} H{F(x)} Z";
            case GraphNodeKind.Mbom:
            {
                var c = 12;
                return $"M{F(x + c)} {F(y)} H{F(r - c)} L{F(r)} {F(y + h / 2)} L{F(r - c)} {F(b)} H{F(x + c)} L{F(x)} {F(y + h / 2)} Z";
            }
            case GraphNodeKind.Conflict:
            {
                var c = 12;
                return $"M{F(x + c)} {F(y)} H{F(r - c)} L{F(r)} {F(y + c)} V{F(b - c)} L{F(r - c)} {F(b)} H{F(x + c)} L{F(x)} {F(b - c)} V{F(y + c)} Z";
            }
            case GraphNodeKind.More:
            {
                var c = h / 2;
                return $"M{F(x + c)} {F(y)} H{F(r - c)} A{F(c)} {F(c)} 0 0 1 {F(r - c)} {F(b)} H{F(x + c)} A{F(c)} {F(c)} 0 0 1 {F(x + c)} {F(y)} Z";
            }
            case GraphNodeKind.Change:
            {
                var c = 10;
                return $"M{F(x + c)} {F(y)} H{F(r - c)} Q{F(r)} {F(y)} {F(r)} {F(y + c)} V{F(b - c)} Q{F(r)} {F(b)} {F(r - c)} {F(b)} H{F(x + c)} Q{F(x)} {F(b)} {F(x)} {F(b - c)} V{F(y + c)} Q{F(x)} {F(y)} {F(x + c)} {F(y)} Z";
            }
            default:
                return $"M{F(x)} {F(y)} H{F(r)} V{F(b)} H{F(x)} Z";
        }
    }

    public static string IconFor(GraphNodeKind kind) => kind switch
    {
        GraphNodeKind.Change => "change",
        GraphNodeKind.Affected => "part",
        GraphNodeKind.Parent => "part",
        GraphNodeKind.Document => "document",
        GraphNodeKind.Mbom => "mbom",
        GraphNodeKind.Conflict => "warning",
        GraphNodeKind.Restricted => "lock",
        _ => "plus",
    };

    public static string KindName(GraphNodeKind kind) => kind switch
    {
        GraphNodeKind.Change => "Change",
        GraphNodeKind.Affected => "Affected part",
        GraphNodeKind.Parent => "Where-used assembly",
        GraphNodeKind.Document => "Linked document",
        GraphNodeKind.Mbom => "Stale manufacturing BOM",
        GraphNodeKind.Conflict => "Conflicting open change",
        GraphNodeKind.Restricted => "Restricted item",
        _ => "More levels",
    };

    /// <summary>
    /// Arrow-key neighbours. Right follows edges to a later column and Left to an earlier one (each ordered top to bottom); Up and Down move inside the column.
    /// Because the order inside a column is the layout order, the "logical order" of focus is column by column, top to bottom.
    /// </summary>
    public static IReadOnlyDictionary<string, NodeNavigation> Navigation(GraphLayout layout)
    {
        var byId = layout.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var columns = layout.Nodes.GroupBy(n => n.Layer).ToDictionary(g => g.Key, g => g.OrderBy(n => n.Row).ToList());
        var result = new Dictionary<string, NodeNavigation>(StringComparer.Ordinal);
        foreach (var n in layout.Nodes)
        {
            var right = layout.Edges.Where(e => e.From == n.Id && byId[e.To].Layer > n.Layer).Select(e => byId[e.To]).Concat(
                layout.Edges.Where(e => e.To == n.Id && byId[e.From].Layer > n.Layer).Select(e => byId[e.From]))
                .OrderBy(x => x.Layer).ThenBy(x => x.Row).Select(x => x.Id).Distinct().ToList();
            var left = layout.Edges.Where(e => e.To == n.Id && byId[e.From].Layer < n.Layer).Select(e => byId[e.From]).Concat(
                layout.Edges.Where(e => e.From == n.Id && byId[e.To].Layer < n.Layer).Select(e => byId[e.To]))
                .OrderBy(x => x.Layer).ThenBy(x => x.Row).Select(x => x.Id).Distinct().ToList();
            var col = columns[n.Layer];
            result[n.Id] = new NodeNavigation(right, left, n.Row > 0 ? col[n.Row - 1].Id : null, n.Row + 1 < col.Count ? col[n.Row + 1].Id : null);
        }
        return result;
    }

    /// <summary>Node ids in focus order: column by column, top to bottom.</summary>
    public static IReadOnlyList<string> FocusOrder(GraphLayout layout) =>
        [.. layout.Nodes.OrderBy(n => n.Layer).ThenBy(n => n.Row).Select(n => n.Id)];
}
