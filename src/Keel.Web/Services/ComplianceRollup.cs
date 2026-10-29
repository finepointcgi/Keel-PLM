using Keel.Bom;
using Keel.Compliance;
using Keel.Core;

namespace Keel.Web.Services;

/// <summary>
/// The compliance roll-up of an assembly. Reads the structure through the normal BOM read paths (<see cref="BomService"/> for an engineering BOM, <see cref="MbomService"/> for a
/// manufacturing BOM), which redact every line the caller may not read, and hands the readable lines to <see cref="ComplianceReportService"/>, which reads the declarations
/// again under the caller's own read filter. Lives in the web assembly because it composes three modules, which do not reference each other (the REST layer uses the same class).
/// </summary>
public sealed class ComplianceRollupService(BomService bom, MbomService mboms, ComplianceReportService reports)
{
    public const string Ebom = "ebom";
    public const string Mbom = "mbom";

    public async Task<Outcome<ReportRequest>> StructureAsync(Subject subject, string mode, string number, IReadOnlyDictionary<string, int>? versions, DateOnly? asOf, string? correlationId, CancellationToken ct = default)
    {
        var lines = new List<StructureLine>();
        string rootNumber, rootName, banner;
        if (mode == Ebom)
        {
            var exploded = await bom.ExplodeAsync(subject, new BomQuery(number.Trim()), correlationId, ct);
            if (!exploded.IsOk) return Fail<BomExplosion, ReportRequest>(exploded);
            var x = exploded.Value!;
            (rootNumber, rootName, banner) = (x.RootNumber, x.RootName, x.Banner);
            lines.Add(new StructureLine(0, 0, "", x.RootNumber, x.RootName, x.RootRevision, 1m, "EA", x.Lines.Any(l => !l.Redacted)));
            foreach (var l in x.Lines.Where(l => !l.Redacted && !l.Circular && l.Number is not null))
                lines.Add(new StructureLine(l.Level, l.FindNumber, l.Path, l.Number!, l.Name ?? "", l.Revision, l.ExtendedQuantity, l.Unit, l.HasChildren));
        }
        else if (mode == Mbom)
        {
            var exploded = await mboms.ExplodeAsync(subject, new MbomQuery(number.Trim()), correlationId, ct);
            if (!exploded.IsOk) return Fail<MbomExplosion, ReportRequest>(exploded);
            var x = exploded.Value!;
            (rootNumber, rootName, banner) = (x.PartNumber, x.PartName, x.Banner);
            lines.Add(new StructureLine(0, 0, "", x.PartNumber, x.PartName, x.Revision, 1m, "EA", x.Lines.Any(l => !l.Redacted)));
            foreach (var l in x.Lines.Where(l => !l.Redacted && !l.Circular && l.Number is not null))
                lines.Add(new StructureLine(l.Level, l.FindNumber, l.Path, l.Number!, l.Name ?? "", null, l.ExtendedQuantity, l.Unit, l.HasChildren));
        }
        else return Outcome<ReportRequest>.Invalid(["The mode is ebom or mbom."]);
        _ = rootNumber;
        return Outcome<ReportRequest>.Ok(new ReportRequest(mode, number.Trim(), rootName, banner, lines, versions, asOf));
    }

    public async Task<Outcome<ComplianceReport>> ReportAsync(Subject subject, string mode, string number, IReadOnlyDictionary<string, int>? versions, DateOnly? asOf, string? correlationId, CancellationToken ct = default)
    {
        var structure = await StructureAsync(subject, mode, number, versions, asOf, correlationId, ct);
        return structure.IsOk ? await reports.BuildAsync(subject, structure.Value!, correlationId, ct) : Fail<ReportRequest, ComplianceReport>(structure);
    }

    public async Task<Outcome<ExportFile>> ExportAsync(Subject subject, string mode, string number, string format, IReadOnlyDictionary<string, int>? versions, DateOnly? asOf, string? correlationId, CancellationToken ct = default)
    {
        var structure = await StructureAsync(subject, mode, number, versions, asOf, correlationId, ct);
        return structure.IsOk ? await reports.ExportAsync(subject, structure.Value!, format, correlationId, ct) : Fail<ReportRequest, ExportFile>(structure);
    }

    private static Outcome<TOut> Fail<TIn, TOut>(Outcome<TIn> o) => new(o.Kind, default, o.Errors);
}
