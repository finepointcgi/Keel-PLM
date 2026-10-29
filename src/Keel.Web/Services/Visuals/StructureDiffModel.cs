using System.Globalization;
using Keel.Bom;

namespace Keel.Web.Services.Visuals;

/// <summary>
/// One row of the two-pane structure diff. <see cref="Kind"/> null means "no change": an unchanged assembly that only holds changed lines (identified by its find number path
/// alone, because the comparison does not return unchanged lines) or an unchanged pair of the EBOM/MBOM view. Restricted lines carry no number, name or link.
/// </summary>
public sealed record DiffRow(
    string Id, int Depth, string Path, BomChangeKind? Kind, bool Redacted, string Label, string? Sublabel, string? Href,
    string Left, string Right, string Summary, string? Banner, bool HasChildren)
{
    public bool IsChange => Kind is not null;
}

public sealed record DiffModel(string Title, string LeftTitle, string RightTitle, IReadOnlyList<DiffRow> Rows, int Added, int Removed, int Changed, int Unchanged, string Banner)
{
    public int ChangeCount => Added + Removed + Changed;
}

/// <summary>
/// Turns the results of the existing comparison services into rows for the visual diff (ADR-0029): iteration against iteration (<see cref="BomComparison"/>), baseline or
/// unit against another (<see cref="DeepComparison"/>), MBOM against MBOM and EBOM against MBOM. Nothing is queried: the rows are exactly the lines the services returned,
/// restricted ones included as placeholders, and the unchanged lines stay a count because the services do not return them.
/// </summary>
public static class StructureDiffModel
{
    private const string Arrow = "→";

    public static string Kind(BomChangeKind k) => k switch { BomChangeKind.Added => "Added", BomChangeKind.Removed => "Removed", _ => "Changed" };

    private static string Q(decimal d) => d.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Change(string label, string? a, string? b) => a == b ? "" : a is null ? $"{label} {b}" : b is null ? $"{label} {a}" : $"{label} {a} {Arrow} {b}";

    public static DiffModel FromShallow(BomComparison c)
    {
        var rows = new List<DiffRow>();
        var n = 0;
        foreach (var l in c.Lines)
        {
            var id = "r" + (n++).ToString(CultureInfo.InvariantCulture);
            var (left, right) = (l.FromQuantity is { } fq ? $"Qty {Q(fq)} {l.FromUnit} · find {l.FromFindNumber}" : "", l.ToQuantity is { } tq ? $"Qty {Q(tq)} {l.ToUnit} · find {l.ToFindNumber}" : "");
            var summary = l.Kind switch
            {
                BomChangeKind.Added => "Added",
                BomChangeKind.Removed => "Removed",
                _ => Summary([Change("Qty", l.FromQuantity is { } a ? Q(a) : null, l.ToQuantity is { } b ? Q(b) : null), Change("Find", l.FromFindNumber?.ToString(CultureInfo.InvariantCulture), l.ToFindNumber?.ToString(CultureInfo.InvariantCulture)),
                    Change("Unit", l.FromUnit, l.ToUnit), .. Others(l.Changes)]),
            };
            rows.Add(new DiffRow(id, 0, id, l.Kind, l.Redacted, l.Redacted ? "Restricted item" : l.Number ?? "", l.Redacted ? null : l.Name, l.Redacted ? null : $"parts/{Uri.EscapeDataString(l.Number!)}",
                l.Kind == BomChangeKind.Added ? "" : left, l.Kind == BomChangeKind.Removed ? "" : right, summary, l.Banner, false));
        }
        return Model($"{c.Number} {c.From} {Arrow} {c.To}", c.From, c.To, rows, c.Lines.Count(l => l.Kind == BomChangeKind.Added), c.Lines.Count(l => l.Kind == BomChangeKind.Removed),
            c.Lines.Count(l => l.Kind == BomChangeKind.Changed), c.Unchanged, c.Banner);
    }

    public static DiffModel FromMbom(MbomComparison c)
    {
        var rows = new List<DiffRow>();
        var n = 0;
        foreach (var l in c.Lines)
        {
            var id = "r" + (n++).ToString(CultureInfo.InvariantCulture);
            var summary = l.Kind switch { BomChangeKind.Added => "Added", BomChangeKind.Removed => "Removed", _ => Summary(l.Changes.Select(Arrows)) };
            rows.Add(new DiffRow(id, 0, id, l.Kind, l.Redacted, l.Redacted ? "Restricted item" : l.Number ?? "", l.Redacted ? null : l.Name, l.Redacted ? null : $"parts/{Uri.EscapeDataString(l.Number!)}",
                l.Kind == BomChangeKind.Added ? "" : $"Find {l.FromFind}", l.Kind == BomChangeKind.Removed ? "" : $"Find {l.ToFind}", summary, l.Banner, false));
        }
        return Model($"{c.Number} {c.From} {Arrow} {c.To}", c.From, c.To, rows, c.Lines.Count(l => l.Kind == BomChangeKind.Added), c.Lines.Count(l => l.Kind == BomChangeKind.Removed),
            c.Lines.Count(l => l.Kind == BomChangeKind.Changed), c.Unchanged, c.Banner);
    }

    /// <summary>Baseline against baseline or current, unit against unit. Ancestors that the comparison did not list are context rows without a name.</summary>
    public static DiffModel FromDeep(DeepComparison c)
    {
        var tree = new Dictionary<string, List<string>> { [""] = [] };
        var lines = new Dictionary<string, DeepDiffLine>(StringComparer.Ordinal);
        var attached = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in c.Lines)
        {
            // Path of the line; an unlikely repeat of a path (one find number split by effectivity) keeps its own row.
            var path = l.Path;
            for (var dup = 1; lines.ContainsKey(path); dup++) path = $"{l.Path}#{dup.ToString(CultureInfo.InvariantCulture)}";
            lines[path] = l;
            var parent = ParentOf(l.Path);
            // Ensure every ancestor exists, outermost first.
            var chain = new List<string>();
            for (var p = parent; p.Length > 0; p = ParentOf(p)) chain.Add(p);
            chain.Reverse();
            var above = "";
            foreach (var a in chain)
            {
                if (!tree.ContainsKey(a)) tree[a] = [];
                if (attached.Add(a)) tree[above].Add(a);
                above = a;
            }
            if (!tree.ContainsKey(path)) tree[path] = [];
            if (attached.Add(path)) tree[parent].Add(path);
        }

        var rows = new List<DiffRow>();
        void Walk(string path, int depth)
        {
            foreach (var child in tree[path])
            {
                var hasKids = tree[child].Count > 0;
                if (lines.TryGetValue(child, out var l))
                {
                    var version = Change("Rev", l.FromVersion, l.ToVersion);
                    var qty = l.FromQuantity is { } a && l.ToQuantity is { } b && a != b ? $"Qty {Q(a)} {Arrow} {Q(b)}" : "";
                    var summary = l.Kind switch
                    {
                        BomChangeKind.Added => "Added",
                        BomChangeKind.Removed => "Removed",
                        _ => Summary([qty, version, .. Others(l.Changes)]),
                    };
                    rows.Add(new DiffRow(child, depth, child, l.Kind, l.Redacted, l.Redacted ? "Restricted item" : l.Number ?? "", l.Redacted ? null : l.Name,
                        l.Redacted ? null : $"parts/{Uri.EscapeDataString(l.Number!)}",
                        l.Kind == BomChangeKind.Added ? "" : Side(l.FromQuantity, l.Unit, l.FromVersion, l.FromFind),
                        l.Kind == BomChangeKind.Removed ? "" : Side(l.ToQuantity, l.Unit, l.ToVersion, l.ToFind), summary, l.Banner, hasKids));
                }
                else rows.Add(new DiffRow(child, depth, child, null, false, "Unchanged assembly", $"find {LastSegment(child)}", null, "", "", "Contains changes", null, hasKids));
                if (hasKids) Walk(child, depth + 1);
            }
        }
        Walk("", 0);
        return Model(c.Title, c.FromLabel, c.ToLabel, rows, c.Added, c.Removed, c.Changed, c.Unchanged, c.Banner);
    }

    /// <summary>EBOM lines against the MBOM lines that implement them: a row per group joined by links, EBOM-only and MBOM-only rows marked as removed and added.</summary>
    public static DiffModel FromSideBySide(SideBySide v)
    {
        var rows = new List<DiffRow>();
        var added = 0; var removed = 0; var changed = 0; var same = 0;
        var n = 0;
        foreach (var r in v.Rows)
        {
            var id = "r" + (n++).ToString(CultureInfo.InvariantCulture);
            BomChangeKind? kind = r.State switch { "ebom_only" => BomChangeKind.Removed, "mbom_only" => BomChangeKind.Added, "linked" => (r.LinkStatus is null or LinkStatuses.Matched ? null : BomChangeKind.Changed), _ => BomChangeKind.Changed };
            switch (kind) { case BomChangeKind.Added: added++; break; case BomChangeKind.Removed: removed++; break; case BomChangeKind.Changed: changed++; break; default: same++; break; }
            var first = r.Ebom.Count > 0 ? r.Ebom[0] : r.Mbom.Count > 0 ? r.Mbom[0] : null;
            var restricted = first is { Redacted: true };
            var summary = r.State switch
            {
                "ebom_only" => "Not in the manufacturing BOM",
                "mbom_only" => "Manufacturing only",
                "linked" when r.LinkStatus is { } s && s != LinkStatuses.Matched => s.Replace('_', ' '),
                "linked" => "Linked",
                _ => "No EBOM source",
            };
            rows.Add(new DiffRow(id, 0, id, kind, restricted, restricted ? "Restricted item" : first?.Number ?? "", restricted ? null : first?.Name, restricted || first?.Number is null ? null : $"parts/{Uri.EscapeDataString(first.Number)}",
                string.Join("; ", r.Ebom.Select(Cell)), string.Join("; ", r.Mbom.Select(Cell)), summary, null, false));
        }
        return Model($"EBOM {v.EbomSource} {Arrow} MBOM {v.MbomLabel}", "EBOM", "MBOM", rows, added, removed, changed, same, v.Banner);

        static string Cell(SideLine l) => l.Redacted ? $"Find {l.FindNumber} · Qty {Q(l.Quantity)} {l.Unit}" : $"Find {l.FindNumber} · {l.Number} · Qty {Q(l.Quantity)} {l.Unit}";
    }

    private static DiffModel Model(string title, string left, string right, List<DiffRow> rows, int added, int removed, int changed, int unchanged, string banner) =>
        new(title, left, right, rows, added, removed, changed, unchanged, banner);

    private static string Side(decimal? qty, string? unit, string? version, int? find) =>
        string.Join(" · ", new[] { qty is { } q ? $"Qty {Q(q)} {unit}".TrimEnd() : null, version is null ? null : $"Rev {version}", find is { } f ? $"find {f.ToString(CultureInfo.InvariantCulture)}" : null }.Where(x => x is not null));

    private static string Summary(IEnumerable<string> parts) => string.Join("; ", parts.Where(p => p.Length > 0)) is { Length: > 0 } s ? s : "Changed";

    /// <summary>Service wording "Quantity 2 to 3" shown as "Qty 2 → 3"; other details are kept as written.</summary>
    private static string Arrows(string text) =>
        text.StartsWith("Quantity ", StringComparison.Ordinal) ? "Qty " + text["Quantity ".Length..].Replace(" to ", $" {Arrow} ", StringComparison.Ordinal)
        : text.StartsWith("Revision ", StringComparison.Ordinal) ? "Rev " + text["Revision ".Length..].Replace(" to ", $" {Arrow} ", StringComparison.Ordinal)
        : text.Replace(" to ", $" {Arrow} ", StringComparison.Ordinal);

    /// <summary>Details that the numeric columns above do not already say (quantity, revision, find and unit are rendered from their values).</summary>
    private static IEnumerable<string> Others(IReadOnlyList<string> changes) =>
        changes.Where(c => !c.StartsWith("Quantity ", StringComparison.Ordinal) && !c.StartsWith("Revision ", StringComparison.Ordinal)
                           && !c.StartsWith("Find number ", StringComparison.Ordinal) && !c.StartsWith("Unit ", StringComparison.Ordinal)).Select(Arrows);

    private static string ParentOf(string path)
    {
        var hash = path.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0) path = path[..hash];
        var cut = path.LastIndexOf('.');
        return cut < 0 ? "" : path[..cut];
    }

    private static string LastSegment(string path)
    {
        var cut = path.LastIndexOf('.');
        return cut < 0 ? path : path[(cut + 1)..];
    }
}
