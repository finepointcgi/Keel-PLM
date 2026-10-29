using Keel.Bom;
using Keel.Core;
using Keel.Sourcing;

namespace Keel.Web.Services;

/// <summary>One readable line of a structure with its sourcing standing. <see cref="Required"/> is false for lines that are not purchased (made in house, consumed, phantom, assemblies).</summary>
public sealed record CoverageLine(
    int Level, int FindNumber, string Path, string Number, string Name, string? Revision, decimal Quantity, string Unit, string? SupplyType, bool Required, string Bucket, int Sources);

/// <summary>
/// The BOM-level sourcing coverage report. It is built only from what the caller can read: lines the caller may not read (redacted by the structure) are left out entirely
/// and are not counted, so neither the lines nor the totals tell anything about hidden parts.
/// </summary>
public sealed record CoverageReport(
    string Mode, string Number, string Name, IReadOnlyList<CoverageLine> Lines, int Covered, int Conditional, int NotCovered, int NotRequired, string Banner)
{
    public int Required => Covered + Conditional + NotCovered;
    public bool Complete => Required > 0 && NotCovered == 0 && Conditional == 0;
}

/// <summary>
/// Joins a structure (EBOM through <see cref="BomService"/>, MBOM through <see cref="MbomService"/>) with the sourcing standing of its parts (<see cref="SourcingService"/>).
/// In an EBOM a purchased part is a leaf whose source is Buy; in an MBOM a line is purchased when its supply type is Buy (Make, Consume and phantom lines are not).
/// Lives in the web assembly because it composes three modules, which do not reference each other (the REST layer uses the same class).
/// </summary>
public sealed class SourcingCoverageService(BomService bom, MbomService mboms, SourcingService sourcing)
{
    public const string Ebom = "ebom";
    public const string Mbom = "mbom";

    public async Task<Outcome<CoverageReport>> ReportAsync(Subject subject, string mode, string number, string? correlationId, CancellationToken ct = default)
    {
        var lines = new List<(int Level, int Find, string Path, string Number, string Name, string? Revision, decimal Qty, string Unit, string? SupplyType, bool Leaf, bool Phantom)>();
        string name, banner;
        if (mode == Ebom)
        {
            var exploded = await bom.ExplodeAsync(subject, new BomQuery(number.Trim()), correlationId, ct);
            if (!exploded.IsOk) return Convert(exploded);
            var x = exploded.Value!;
            (name, banner) = (x.RootName, x.Banner);
            foreach (var l in x.Lines.Where(l => !l.Redacted && !l.Circular && !l.Unresolved && l.Number is not null))
                lines.Add((l.Level, l.FindNumber, l.Path, l.Number!, l.Name ?? "", l.Revision, l.ExtendedQuantity, l.Unit, null, !l.HasChildren, false));
        }
        else if (mode == Mbom)
        {
            var exploded = await mboms.ExplodeAsync(subject, new MbomQuery(number.Trim()), correlationId, ct);
            if (!exploded.IsOk) return Convert(exploded);
            var x = exploded.Value!;
            (name, banner) = (x.PartName, x.Banner);
            foreach (var l in x.Lines.Where(l => !l.Redacted && !l.Circular && l.Number is not null))
                lines.Add((l.Level, l.FindNumber, l.Path, l.Number!, l.Name ?? "", null, l.ExtendedQuantity, l.Unit, l.SupplyType, !l.HasChildren, l.Phantom));
        }
        else return Outcome<CoverageReport>.Invalid(["The mode is ebom or mbom."]);

        var revisions = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var l in lines) revisions.TryAdd(l.Number, l.Revision);
        var standing = revisions.Count == 0
            ? new Dictionary<string, PartCoverage>()
            : (await sourcing.CoverageAsync(subject, revisions, correlationId, ct)).Value!.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var result = new List<CoverageLine>();
        foreach (var l in lines)
        {
            // A part that the sourcing read does not return is one the caller cannot read after all: left out, never counted.
            if (!standing.TryGetValue(l.Number, out var s)) continue;
            var required = mode == Ebom
                ? l.Leaf && string.Equals(s.Source, "Buy", StringComparison.Ordinal)
                : string.Equals(l.SupplyType, "Buy", StringComparison.OrdinalIgnoreCase) && !l.Phantom && l.Leaf;
            result.Add(new CoverageLine(l.Level, l.Find, l.Path, l.Number, l.Name, l.Revision, l.Qty, l.Unit, l.SupplyType, required, s.Bucket, s.Sources));
        }
        var req = result.Where(r => r.Required).ToList();
        return Outcome<CoverageReport>.Ok(new CoverageReport(mode, number.Trim(), name, result,
            req.Count(r => r.Bucket == CoverageBuckets.Covered), req.Count(r => r.Bucket == CoverageBuckets.Conditional), req.Count(r => r.Bucket == CoverageBuckets.NotCovered),
            result.Count(r => !r.Required), banner));
    }

    private static Outcome<CoverageReport> Convert<T>(Outcome<T> o) => new(o.Kind, default, o.Errors);
}
