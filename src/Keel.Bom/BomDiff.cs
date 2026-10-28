using System.Globalization;
using Keel.Core;

namespace Keel.Bom;

/// <summary>
/// Compares the direct lines of two iterations. Lines are matched on (part, find number) first, then on part alone (a renumber).
/// Lines the caller cannot read are matched on find number only and only when the hidden part is the same, so the diff never
/// reveals anything about a hidden part except that its line was added, removed or its quantity or unit changed.
/// </summary>
internal static class BomDiff
{
    public sealed record Side(
        Guid MasterId, int Find, decimal Quantity, string Unit, string Designators, string? Notes,
        bool Readable, string? Number, string? Name, string? Banner, Markings? Markings);

    public static (IReadOnlyList<BomDiffLine> Lines, int Unchanged) Compute(IReadOnlyList<Side> from, IReadOnlyList<Side> to)
    {
        var fromMatch = new int[from.Count];
        var toMatch = new int[to.Count];
        Array.Fill(fromMatch, -1);
        Array.Fill(toMatch, -1);

        for (var i = 0; i < from.Count; i++)
            for (var j = 0; j < to.Count; j++)
                if (toMatch[j] < 0 && from[i].MasterId == to[j].MasterId && from[i].Find == to[j].Find && from[i].Readable == to[j].Readable)
                {
                    (fromMatch[i], toMatch[j]) = (j, i);
                    break;
                }
        for (var i = 0; i < from.Count; i++)
        {
            if (fromMatch[i] >= 0 || !from[i].Readable) continue;
            for (var j = 0; j < to.Count; j++)
                if (toMatch[j] < 0 && to[j].Readable && from[i].MasterId == to[j].MasterId)
                {
                    (fromMatch[i], toMatch[j]) = (j, i);
                    break;
                }
        }

        var lines = new List<BomDiffLine>();
        var unchanged = 0;
        for (var i = 0; i < from.Count; i++)
        {
            var a = from[i];
            if (fromMatch[i] < 0)
            {
                lines.Add(new BomDiffLine(BomChangeKind.Removed, !a.Readable, a.Number, a.Name, a.Find, null, a.Quantity, null, a.Unit, null, [], a.Banner, a.Markings));
                continue;
            }
            var b = to[fromMatch[i]];
            var changes = new List<string>();
            if (a.Find != b.Find) changes.Add($"Find number {a.Find} to {b.Find}");
            if (a.Quantity != b.Quantity) changes.Add($"Quantity {Fmt(a.Quantity)} to {Fmt(b.Quantity)}");
            if (a.Unit != b.Unit) changes.Add($"Unit {a.Unit} to {b.Unit}");
            if (a.Readable)
            {
                if (a.Designators != b.Designators) changes.Add("Reference designators changed");
                if ((a.Notes ?? "") != (b.Notes ?? "")) changes.Add("Notes changed");
            }
            if (changes.Count == 0) { unchanged++; continue; }
            lines.Add(new BomDiffLine(BomChangeKind.Changed, !a.Readable, a.Number, a.Name, a.Find, b.Find, a.Quantity, b.Quantity, a.Unit, b.Unit, changes, b.Banner, b.Markings));
        }
        for (var j = 0; j < to.Count; j++)
            if (toMatch[j] < 0)
            {
                var b = to[j];
                lines.Add(new BomDiffLine(BomChangeKind.Added, !b.Readable, b.Number, b.Name, null, b.Find, null, b.Quantity, null, b.Unit, [], b.Banner, b.Markings));
            }
        var ordered = lines.OrderBy(l => l.ToFindNumber ?? l.FromFindNumber).ThenBy(l => l.Kind).ToList();
        return (ordered, unchanged);
    }

    private static string Fmt(decimal d) => d.ToString("0.######", CultureInfo.InvariantCulture);
}
