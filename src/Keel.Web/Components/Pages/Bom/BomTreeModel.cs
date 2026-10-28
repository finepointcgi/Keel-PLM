using Keel.Bom;
using Keel.Core;

namespace Keel.Web.Components.Pages.Bom;

/// <summary>
/// The visible rows of the BOM tree grid as a flat, depth-first list (the server flattens; ADR-0014). A row is expanded when the
/// row after it is deeper, so the list alone is the state: expanding inserts one lazily loaded level, collapsing removes the subtree.
/// </summary>
public static class BomTreeModel
{
    public const int RowHeight = 32;

    public static bool IsExpanded(IReadOnlyList<BomLine> rows, int index) =>
        index + 1 < rows.Count && rows[index + 1].Level > rows[index].Level;

    public static bool CanExpand(BomLine line) => line is { Redacted: false, Circular: false, HasChildren: true };

    /// <summary>Rows after <paramref name="index"/> that belong to its subtree.</summary>
    public static int SubtreeSize(IReadOnlyList<BomLine> rows, int index)
    {
        var n = 0;
        while (index + 1 + n < rows.Count && rows[index + 1 + n].Level > rows[index].Level) n++;
        return n;
    }

    public static List<BomLine> Expand(IReadOnlyList<BomLine> rows, int index, IReadOnlyList<BomLine> children)
    {
        var list = new List<BomLine>(rows.Count + children.Count);
        list.AddRange(rows.Take(index + 1));
        list.AddRange(children);
        list.AddRange(rows.Skip(index + 1));
        return list;
    }

    public static List<BomLine> Collapse(IReadOnlyList<BomLine> rows, int index)
    {
        var drop = SubtreeSize(rows, index);
        return [.. rows.Take(index + 1), .. rows.Skip(index + 1 + drop)];
    }

    /// <summary>Banner for the page: the displayed part plus every readable displayed row. Restricted rows carry no markings.</summary>
    public static string Banner(Markings? root, IEnumerable<BomLine> rows) =>
        Markings.AggregateBanner(rows.Where(r => r.Markings is not null).Select(r => r.Markings!).Distinct().Concat(root is null ? [] : [root]));
}
