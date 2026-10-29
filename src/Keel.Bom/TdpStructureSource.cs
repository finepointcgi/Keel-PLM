using Keel.Core;

namespace Keel.Bom;

/// <summary>
/// Gives the Technical Data Package builder (Keel.Integration) the structure of a part revision or a baseline through the existing explosion, as the
/// exporting subject: lines the subject cannot read come back counted, never named (A4), and the builder refuses an incomplete package.
/// </summary>
internal sealed class TdpStructureSource(BomService bom, BaselineService baselines) : ITdpStructureSource
{
    public async Task<Outcome<TdpStructure>> GetPartStructureAsync(Subject subject, string number, string? revision, string? correlationId, CancellationToken ct, Keel.Core.Variants.VariantSelection? variant = null) =>
        Map(await bom.ExplodeAsync(subject, new BomQuery(number, revision, null, BomConfigSpec.LatestReleased, Variant: variant), correlationId, ct));

    public async Task<Outcome<TdpStructure>> GetBaselineStructureAsync(Subject subject, string baselineNumber, string? correlationId, CancellationToken ct)
    {
        var b = await baselines.GetAsync(subject, baselineNumber, correlationId, ct);
        if (!b.IsOk) return new Outcome<TdpStructure>(b.Kind, default, b.Errors);
        if (b.Value!.Summary.RootNumber is not { } root) return Outcome<TdpStructure>.NotFound();
        return Map(await bom.ExplodeAsync(subject, new BomQuery(root, Spec: BomConfigSpec.Baseline, Baseline: baselineNumber), correlationId, ct));
    }

    public async Task<Outcome<TdpBaseline>> GetBaselineAsync(Subject subject, string baselineNumber, string? correlationId, CancellationToken ct)
    {
        var b = await baselines.GetAsync(subject, baselineNumber, correlationId, ct);
        if (!b.IsOk) return new Outcome<TdpBaseline>(b.Kind, default, b.Errors);
        var s = b.Value!.Summary;
        if (s.RootNumber is null || s.RootVersion is null) return Outcome<TdpBaseline>.NotFound();
        var docs = await baselines.GetDocumentsAsync(subject, baselineNumber, correlationId, ct);
        if (!docs.IsOk) return new Outcome<TdpBaseline>(docs.Kind, default, docs.Errors);
        var refs = docs.Value!.Rows.Where(d => !d.Redacted && d.Number is not null && d.Revision is not null).Select(d => new TdpDocumentRef(d.Number!, d.Revision!, d.LinkType, null)).ToList();
        // The root version is "B.2": the revision is the part before the iteration.
        var rootRevision = s.RootVersion.Contains('.', StringComparison.Ordinal) ? s.RootVersion[..s.RootVersion.IndexOf('.', StringComparison.Ordinal)] : s.RootVersion;
        return Outcome<TdpBaseline>.Ok(new TdpBaseline(s.Number, s.Name, s.Type, s.State, s.RootNumber, rootRevision, s.Milestone, b.Value.ContentSha256, s.Markings, refs,
            docs.Value.Rows.Count(d => d.Redacted)));
    }

    private static Outcome<TdpStructure> Map(Outcome<BomExplosion> o)
    {
        if (!o.IsOk) return new Outcome<TdpStructure>(o.Kind, default, o.Errors);
        var e = o.Value!;
        var lines = e.Lines.Where(l => !l.Redacted && !l.Unresolved && l.Number is not null && l.Markings is not null)
            .Select(l => new TdpStructureLine(l.Level, l.Path, l.FindNumber, l.Quantity, l.ExtendedQuantity, l.Unit, l.Number!, l.Name ?? "", l.PartType ?? "", l.Revision, l.Iteration, l.StateLabel,
                l.ReferenceDesignators, l.Notes, l.EffectivityText, false, l.Markings!)).ToList();
        // Unresolved lines have no released revision to document; they are counted so the package is refused rather than silently incomplete.
        return Outcome<TdpStructure>.Ok(new TdpStructure(e.RootNumber, e.RootRevision, e.Configuration ?? e.Spec.ToString(), lines, e.RedactedCount,
            e.Lines.Count(l => l.Unresolved || l.Circular)));
    }
}
