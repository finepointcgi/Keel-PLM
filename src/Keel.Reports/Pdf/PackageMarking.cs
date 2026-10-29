using Keel.Core;

namespace Keel.Reports.Pdf;

/// <summary>
/// The marking of a package that holds several objects: the aggregate banner (the union of every CUI category and export control and the most restrictive
/// distribution statement over ALL included objects) and every distinct notice any of them requires (ADR-0044). The notice texts come from the host
/// (the same <c>MarkingStatements.Notices</c> that stamps downloads), so a package never words a marking differently from a single file.
/// </summary>
public sealed record PackageMarking(string Banner, IReadOnlyList<string> Notices, IReadOnlyList<string> Programs)
{
    public static PackageMarking For(IReadOnlyCollection<Markings> all, Func<Markings, IReadOnlyList<string>> notices)
    {
        if (all.Count == 0) throw new ArgumentException("A package includes at least one marked object.", nameof(all));
        var distinct = all.Distinct().ToList();
        var programs = distinct.Select(m => m.Program).Where(p => p is not null).Select(p => p!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var banner = Markings.AggregateBanner(distinct);
        if (programs.Count > 0) banner += " · PROGRAM " + string.Join(", ", programs);
        // Most restrictive first, so the statement a reader meets first is the strictest.
        var lines = new List<string>();
        foreach (var m in distinct.OrderByDescending(m => m.DistributionStatement).ThenByDescending(m => m.IsExportControlled).ThenBy(m => m.BannerText(), StringComparer.Ordinal))
            foreach (var n in notices(m))
                if (!lines.Contains(n)) lines.Add(n);
        return new PackageMarking(banner, lines, programs);
    }
}
