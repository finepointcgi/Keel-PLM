using Keel.Bom;
using Keel.Core;
using Keel.Core.Reports;
using Keel.Core.Variants;

namespace Keel.Api.Infrastructure.Reports;

/// <summary>The resolved EBOM or MBOM for export (ADR-0044): the structure services' own read-filtered explosions, with the variant chosen through the Keel.Core port.</summary>
public sealed class BomExchangeSource(BomService bom, MbomService mbom, IVariantSelectionSource variants) : IBomExchangeSource
{
    private static string Describe(string spec, string? variant, string? configuration) =>
        string.Join(", ", new[] { spec switch { "latest" => "latest revisions", "effectivity" => "as designed for one unit", _ => "latest released revisions" }, variant is null ? null : $"variant {variant}", configuration }.Where(s => !string.IsNullOrEmpty(s)));

    public async Task<Outcome<BomExchangeResult>> ResolveAsync(Subject subject, BomExchangeRequest request, string? correlationId, CancellationToken ct = default)
    {
        var number = (request.Number ?? "").Trim();
        if (number.Length is 0 or > 64) return Outcome<BomExchangeResult>.Invalid(["Give the number of the part or MBOM."]);
        if (request.Depth is < 1 or > BomLimits.MaxDepth) return Outcome<BomExchangeResult>.Invalid([$"Depth must be 1 to {BomLimits.MaxDepth}."]);
        EffectivityRequest? eff = null;
        if (request.Spec == "effectivity")
        {
            if (request.Date is { } date) eff = EffectivityRequest.ForDate(date);
            else if (request.Context is not null && request.Serial is { } serial) eff = EffectivityRequest.ForSerial(request.Context, serial);
            else if (request.Context is not null && request.Lot is { } lot) eff = EffectivityRequest.ForLot(request.Context, lot);
            else return Outcome<BomExchangeResult>.Invalid(["Effectivity needs a date, or a context with a serial or lot number."]);
        }
        else if (request.Spec is not ("released" or "latest")) return Outcome<BomExchangeResult>.Invalid(["Spec must be released, latest or effectivity."]);

        if (request.Kind == BomExchangeKinds.Mbom)
        {
            if (request.Spec == "effectivity" && eff is null) return Outcome<BomExchangeResult>.Invalid(["Effectivity needs a unit."]);
            VariantSelection? sel = null;
            if (!string.IsNullOrWhiteSpace(request.Variant))
            {
                var d = await mbom.GetAsync(subject, number, null, correlationId, ct);
                if (!d.IsOk) return new Outcome<BomExchangeResult>(d.Kind, null, d.Errors);
                var v = await variants.ResolveAsync(subject, d.Value!.Summary.PartNumber, request.Variant!, correlationId, ct);
                if (!v.IsOk) return new Outcome<BomExchangeResult>(v.Kind, null, v.Errors);
                sel = v.Value;
            }
            var r = await mbom.ExplodeAsync(subject, new MbomQuery(number, LatestChildren: request.Spec == "latest", Depth: request.Depth, Unit: request.Spec == "effectivity" ? eff : null, Variant: sel), correlationId, ct);
            if (!r.IsOk) return new Outcome<BomExchangeResult>(r.Kind, null, r.Errors);
            var x = r.Value!;
            return Outcome<BomExchangeResult>.Ok(new BomExchangeResult(x.Number, BomExchangeKinds.Mbom, $"{x.PartNumber} {x.PartName}, plant {x.Plant}, {Describe(request.Spec, request.Variant, x.Configuration)}",
                [.. x.Lines.Select(l => new BomExchangeLine(l.Level, l.Path, l.FindNumber, l.Redacted ? null : l.Number, l.Name, l.ChildMbomRevision, l.ChildMbomStateLabel, l.Quantity, l.Unit, l.EffectivityText, null,
                    l.Notes, l.ReferenceDesignators, l.Redacted, l.Banner, l.Markings))], x.Banner, x.RedactedCount));
        }
        if (request.Kind != BomExchangeKinds.Ebom) return Outcome<BomExchangeResult>.Invalid(["Kind must be ebom or mbom."]);
        VariantSelection? selection = null;
        if (!string.IsNullOrWhiteSpace(request.Variant))
        {
            var v = await variants.ResolveAsync(subject, number, request.Variant!, correlationId, ct);
            if (!v.IsOk) return new Outcome<BomExchangeResult>(v.Kind, null, v.Errors);
            selection = v.Value;
        }
        var spec = request.Spec switch { "latest" => BomConfigSpec.Latest, "effectivity" => BomConfigSpec.Effectivity, _ => BomConfigSpec.LatestReleased };
        var e = await bom.ExplodeAsync(subject, new BomQuery(number, Spec: spec, Depth: request.Depth, Effectivity: eff, Variant: selection), correlationId, ct);
        if (!e.IsOk) return new Outcome<BomExchangeResult>(e.Kind, null, e.Errors);
        var b = e.Value!;
        return Outcome<BomExchangeResult>.Ok(new BomExchangeResult(b.RootNumber, BomExchangeKinds.Ebom, $"{b.RootName}, revision {b.RootRevision}, {Describe(request.Spec, request.Variant, b.Configuration)}",
            [.. b.Lines.Select(l => new BomExchangeLine(l.Level, l.Path, l.FindNumber, l.Redacted ? null : l.Number, l.Name, l.Revision, l.StateLabel ?? l.State, l.Quantity, l.Unit, l.EffectivityText,
                l.Redacted ? null : l.Condition, l.Notes, l.ReferenceDesignators, l.Redacted, l.Banner, l.Markings))], b.Banner, b.RedactedCount));
    }
}
