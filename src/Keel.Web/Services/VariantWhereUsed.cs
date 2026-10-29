using Keel.Bom;
using Keel.Core;
using Keel.Core.Variants;
using Keel.Variants;

namespace Keel.Web.Services;

/// <summary>One variant of a product and how it uses a part: how many lines hold it and their summed extended quantity.</summary>
public sealed record VariantUse(string Variant, string State, int Occurrences, decimal ExtendedQuantity);

/// <summary>A product that contains the part, with the variants that use it. <see cref="VariantsTotal"/> counts every non-retired variant of the product (readable, like the product).</summary>
public sealed record ProductUse(string Product, string ProductName, string Banner, Markings Markings, int Occurrences, IReadOnlyList<VariantUse> Variants, int VariantsTotal);

public sealed record PartVariantUsage(string Number, IReadOnlyList<ProductUse> Products, string Banner);

/// <summary>
/// "Which variants use this part" (ADR-0039): the where-used of the part finds the products above it; for each product the caller may read that has an option model, the
/// 150% structure is exploded ONCE through the normal read filter and each variant is decided from the conditions on the lines down to the part. Only products the caller
/// can read appear, and a part the caller cannot read answers as missing, so neither a variant name nor a variant count of anything restricted is disclosed. Composes the BOM
/// and variants services (a module must not reference another).
/// </summary>
public sealed class VariantWhereUsedService(BomService bom, VariantService variants)
{
    public const int MaxProducts = 25;

    public async Task<Outcome<PartVariantUsage>> ForPartAsync(Subject subject, string number, BomConfigSpec spec, string? correlationId, CancellationToken ct = default)
    {
        if (spec is not (BomConfigSpec.Latest or BomConfigSpec.LatestReleased)) return Outcome<PartVariantUsage>.Invalid(["Use Latest or LatestReleased."]);
        var used = await bom.WhereUsedAsync(subject, number, BomLimits.MaxDepth, correlationId, ct);
        if (!used.IsOk) return new Outcome<PartVariantUsage>(used.Kind, default, used.Errors);
        var parents = used.Value!.Rows.Where(r => !r.Redacted && r.ParentNumber is not null).Select(r => r.ParentNumber!).Distinct(StringComparer.Ordinal).Take(500).ToList();
        var products = await variants.ListProductsAsync(subject, null, parents, null, MaxProducts, ct);

        var result = new List<ProductUse>();
        var markings = new List<Markings>();
        var parsed = new Dictionary<string, OptionExpr?>(StringComparer.Ordinal);
        foreach (var product in products.Rows)
        {
            var explosion = await bom.ExplodeAsync(subject, new BomQuery(product.Number, Spec: spec), correlationId, ct);
            if (!explosion.IsOk) continue;
            var lines = explosion.Value!.Lines;
            var hits = lines.Where(l => !l.Redacted && l.Number == number).ToList();
            if (hits.Count == 0) continue;
            var model = await variants.GetModelAsync(subject, product.Number, correlationId, ct);
            if (!model.IsOk) continue;
            var chains = BomService.ConditionChains(lines);
            var rows = new List<VariantUse>();
            foreach (var v in model.Value!.Variants.Where(v => v.State != VariantStates.Retired).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            {
                var usedBy = hits.Where(h => chains[h.LineId].All(text =>
                {
                    if (!parsed.TryGetValue(text, out var e)) parsed[text] = e = OptionExpressions.Parse(text).Expression;
                    return e is not null && e.Evaluate(v.Selection);
                })).ToList();
                if (usedBy.Count > 0) rows.Add(new VariantUse(v.Name, v.State, usedBy.Count, usedBy.Sum(h => h.ExtendedQuantity)));
            }
            result.Add(new ProductUse(product.Number, product.Name, product.Banner, product.Markings, hits.Count, rows, model.Value.Variants.Count(v => v.State != VariantStates.Retired)));
            markings.Add(product.Markings);
        }
        var banner = BannerMerge.Union(used.Value.Banner, Markings.AggregateBanner(markings));
        return Outcome<PartVariantUsage>.Ok(new PartVariantUsage(number, result, banner));
    }
}
