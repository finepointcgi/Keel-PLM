using System.Globalization;
using System.Text;
using Keel.Core;
using static Keel.Bom.BomService;

namespace Keel.Bom;

/// <summary>
/// Compares two flattened structures (baseline against baseline, a baseline against the current structure, one unit against another).
/// A line is identified by the chain of parts from the root (not by its find number, which may change), so a part that moved to another parent
/// is one removal and one addition. Several lines of one part under one parent match in order. Restricted lines match only on the same hidden
/// part under the same parent; a revision change of a restricted part is reported as "Restricted item changed" and nothing more (CFG-8).
/// </summary>
internal static class BomDeepDiff
{
    private sealed record Node(BomLine Line, LineIdentity? Identity, string Key);

    private static List<Node> Index(IReadOnlyList<BomLine> lines, IReadOnlyList<LineIdentity?> identities)
    {
        var nodes = new List<Node>(lines.Count);
        var keyByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var identity = i < identities.Count ? identities[i] : null;
            var cut = line.Path.LastIndexOf('.');
            var parentKey = cut < 0 ? "" : keyByPath.GetValueOrDefault(line.Path[..cut], "");
            var token = line.Redacted
                ? $"h{line.FindNumber.ToString(CultureInfo.InvariantCulture)}:{identity?.MasterId:N}"
                : line.MasterId?.ToString("N") ?? "?";
            var baseKey = parentKey + "/" + token;
            var n = seen.GetValueOrDefault(baseKey);
            seen[baseKey] = n + 1;
            var key = baseKey + "#" + n.ToString(CultureInfo.InvariantCulture);
            keyByPath[line.Path] = key;
            nodes.Add(new Node(line, identity, key));
        }
        return nodes;
    }

    private static string? Version(BomLine l) => l.Revision is null ? null : $"{l.Revision}.{l.Iteration}";

    public static (IReadOnlyList<DeepDiffLine> Lines, int Unchanged) Compute(
        IReadOnlyList<BomLine> from, IReadOnlyList<LineIdentity?> fromIds, IReadOnlyList<BomLine> to, IReadOnlyList<LineIdentity?> toIds)
    {
        var a = Index(from, fromIds);
        var b = Index(to, toIds);
        var byKey = new Dictionary<string, Node>(b.Count, StringComparer.Ordinal);
        foreach (var n in b) byKey[n.Key] = n;
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<DeepDiffLine>();
        var unchanged = 0;

        foreach (var x in a)
        {
            if (!byKey.TryGetValue(x.Key, out var y))
            {
                lines.Add(Line(BomChangeKind.Removed, x.Line, x.Line, [], x));
                continue;
            }
            matched.Add(x.Key);
            var changes = new List<string>();
            var redacted = x.Line.Redacted;
            if (redacted)
            {
                if (x.Identity is { } xi && y.Identity is { } yi && (xi.RevisionId != yi.RevisionId || xi.IterationId != yi.IterationId))
                    changes.Add("Restricted item changed");
            }
            else if (Version(x.Line) != Version(y.Line)) changes.Add($"Revision {Version(x.Line) ?? "none"} to {Version(y.Line) ?? "none"}");
            if (x.Line.FindNumber != y.Line.FindNumber) changes.Add($"Find number {x.Line.FindNumber} to {y.Line.FindNumber}");
            if (x.Line.Quantity != y.Line.Quantity) changes.Add($"Quantity {Fmt(x.Line.Quantity)} to {Fmt(y.Line.Quantity)}");
            if (x.Line.Unit != y.Line.Unit) changes.Add($"Unit {x.Line.Unit} to {y.Line.Unit}");
            if ((x.Line.EffectivityText ?? "") != (y.Line.EffectivityText ?? "")) changes.Add($"Effectivity {x.Line.EffectivityText ?? "all units"} to {y.Line.EffectivityText ?? "all units"}");
            if (!redacted)
            {
                if (string.Join(' ', x.Line.ReferenceDesignators) != string.Join(' ', y.Line.ReferenceDesignators)) changes.Add("Reference designators changed");
                if ((x.Line.Notes ?? "") != (y.Line.Notes ?? "")) changes.Add("Notes changed");
            }
            if (changes.Count == 0) { unchanged++; continue; }
            lines.Add(Line(BomChangeKind.Changed, x.Line, y.Line, changes, y));
        }
        foreach (var y in b)
            if (!matched.Contains(y.Key)) lines.Add(Line(BomChangeKind.Added, y.Line, y.Line, [], y));

        var ordered = lines.OrderBy(l => PathKey(l.Path), PathComparer.Instance).ThenBy(l => l.Kind).ToList();
        return (ordered, unchanged);
    }

    private static DeepDiffLine Line(BomChangeKind kind, BomLine from, BomLine to, IReadOnlyList<string> changes, Node shown)
    {
        var l = shown.Line;
        var redacted = l.Redacted;
        return new DeepDiffLine(kind, redacted, l.Level, l.Path, l.Number, l.Name,
            kind == BomChangeKind.Added ? null : Version(from), kind == BomChangeKind.Removed ? null : Version(to),
            kind == BomChangeKind.Added ? null : from.FindNumber, kind == BomChangeKind.Removed ? null : to.FindNumber,
            kind == BomChangeKind.Added ? null : from.Quantity, kind == BomChangeKind.Removed ? null : to.Quantity,
            l.Unit, changes, l.Banner, l.Markings);
    }

    /// <summary>Path text to numbers for natural ordering: "10.30~1.20" sorts as 10, 30.1, 20.</summary>
    private static (int, int)[] PathKey(string path) =>
        [.. path.Split('.').Select(s =>
        {
            var seg = s.Split('~');
            return (int.TryParse(seg[0], NumberStyles.None, CultureInfo.InvariantCulture, out var f) ? f : 0,
                    seg.Length > 1 && int.TryParse(seg[1], NumberStyles.None, CultureInfo.InvariantCulture, out var alt) ? alt : 0);
        })];

    private sealed class PathComparer : IComparer<(int, int)[]>
    {
        public static readonly PathComparer Instance = new();

        public int Compare((int, int)[]? x, (int, int)[]? y)
        {
            x ??= [];
            y ??= [];
            for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
            {
                var c = x[i].CompareTo(y[i]);
                if (c != 0) return c;
            }
            return x.Length.CompareTo(y.Length);
        }
    }

    private static string Fmt(decimal d) => d.ToString("0.######", CultureInfo.InvariantCulture);

    public static DeepComparison Build(string title, string fromLabel, string toLabel, IReadOnlyList<DeepDiffLine> lines, int unchanged, string banner) =>
        new(title, fromLabel, toLabel, lines, unchanged,
            lines.Count(l => l.Kind == BomChangeKind.Added), lines.Count(l => l.Kind == BomChangeKind.Removed), lines.Count(l => l.Kind == BomChangeKind.Changed), banner);
}

/// <summary>CSV of a deep comparison. The banner is the first and last line; formulas are neutralized like every export.</summary>
public static class DeepDiffCsv
{
    public static string Render(DeepComparison c)
    {
        var sb = new StringBuilder(1 << 16);
        sb.Append(BomCsv.Cell(c.Banner)).Append("\r\n");
        sb.Append(BomCsv.Cell($"{c.Title}: {c.FromLabel} to {c.ToLabel}")).Append("\r\n");
        sb.Append("Change,Level,Path,Part number,Name,From version,To version,From find,To find,From quantity,To quantity,Unit,Details,Markings\r\n");
        foreach (var l in c.Lines)
        {
            sb.Append(l.Kind).Append(',')
              .Append(l.Level.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(BomCsv.Cell(l.Path)).Append(',')
              .Append(BomCsv.Cell(l.Redacted ? BomCsv.RestrictedLabel : l.Number)).Append(',')
              .Append(BomCsv.Cell(l.Name)).Append(',')
              .Append(BomCsv.Cell(l.FromVersion)).Append(',')
              .Append(BomCsv.Cell(l.ToVersion)).Append(',')
              .Append(l.FromFind?.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(l.ToFind?.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(l.FromQuantity?.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
              .Append(l.ToQuantity?.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
              .Append(BomCsv.Cell(l.Unit)).Append(',')
              .Append(BomCsv.Cell(string.Join("; ", l.Changes))).Append(',')
              .Append(BomCsv.Cell(l.Banner)).Append("\r\n");
        }
        sb.Append(BomCsv.Cell(c.Banner)).Append("\r\n");
        return sb.ToString();
    }
}
