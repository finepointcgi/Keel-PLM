using System.Globalization;
using Keel.Bom;
using Keel.Core;
using Keel.Sourcing;

namespace Keel.Api.Infrastructure.Programs;

/// <summary>
/// The material cost of an end item for the programs module (ADR-0049, A60): the structure services' own read-filtered explosion of the latest released structure, priced from the
/// sourcing lists through the sourcing service (also read-filtered). Only leaf parts are priced (an assembly's price is the sum of its parts), quantities are the extended
/// quantities of the explosion times the build quantity. A price comes from an active, effective list entry in USD: a preferred entry beats an approved one, the cheapest of the
/// better group wins; alternates and disapproved entries are never used. The price break used is the highest whose quantity does not exceed the quantity needed, or the first break
/// when the quantity is below it (said in <see cref="MaterialPartCost.Basis"/>). Nothing is converted between currencies.
/// </summary>
public sealed class ProgramMaterialSource(BomService bom, SourcingService sourcing) : IProgramMaterialSource
{
    public const string Currency = "USD";

    public async Task<Outcome<MaterialEstimate>> EstimateAsync(Subject subject, string partNumber, decimal quantity, DateOnly asOf, string? correlationId, CancellationToken ct = default)
    {
        if (quantity <= 0) return Outcome<MaterialEstimate>.Invalid(["The quantity must be above zero."]);
        var explosion = await bom.ExplodeAsync(subject, new BomQuery(partNumber, Spec: BomConfigSpec.LatestReleased), correlationId, ct);
        if (!explosion.IsOk) return new Outcome<MaterialEstimate>(explosion.Kind, null, explosion.Errors);
        var x = explosion.Value!;

        var leaves = x.Lines.Where(l => !l.Redacted && !l.HasChildren && !l.Circular).ToList();
        var restricted = x.Lines.Count(l => l.Redacted);
        var targets = new List<(string Number, string Name, decimal Quantity, string Unit, bool Unresolved)>();
        if (x.Lines.Count == 0) targets.Add((x.RootNumber, x.RootName, 1m, "EA", false));
        else
            targets.AddRange(leaves.GroupBy(l => (l.Number ?? "", l.Unit)).Select(g => (g.Key.Item1, g.First().Name ?? "", g.Sum(l => l.ExtendedQuantity), g.Key.Unit, g.Any(l => l.Unresolved))));

        var lines = new List<MaterialPartCost>();
        var markings = new List<Markings>();
        decimal total = 0;
        var priced = 0;
        foreach (var t in targets.OrderBy(t => t.Number, StringComparer.Ordinal))
        {
            var needed = t.Quantity * quantity;
            if (t.Unresolved) { lines.Add(new MaterialPartCost(t.Number, t.Name, needed, t.Unit, null, null, "No released revision")); continue; }
            var s = await sourcing.GetPartSourcingAsync(subject, t.Number, correlationId, ct);
            if (!s.IsOk) { lines.Add(new MaterialPartCost(t.Number, t.Name, needed, t.Unit, null, null, "No readable price list")); continue; }
            var (price, entry, basis) = Price(s.Value!, needed);
            if (price is null) { lines.Add(new MaterialPartCost(t.Number, t.Name, needed, t.Unit, null, null, basis)); continue; }
            total += price.Value * needed;
            priced++;
            lines.Add(new MaterialPartCost(t.Number, t.Name, needed, t.Unit, price, entry, basis));
        }
        foreach (var l in leaves) if (l.Markings is { } m) markings.Add(m);
        return Outcome<MaterialEstimate>.Ok(new MaterialEstimate(x.RootNumber, x.RootName, quantity, decimal.Round(total, 2, MidpointRounding.AwayFromZero), priced, lines.Count - priced, restricted, lines, markings));
    }

    internal static (decimal? Price, string? Entry, string Basis) Price(PartSourcing s, decimal needed)
    {
        var usable = s.Entries.Where(e => e.ApprovalState == EntryApprovalStates.Active && e.Effective && e.Currency == Currency && e.PriceBreaks.Count > 0).ToList();
        var pool = usable.Where(e => e.Status == EntryStatuses.Preferred).ToList();
        if (pool.Count == 0) pool = [.. usable.Where(e => e.Status == EntryStatuses.Approved)];
        if (pool.Count == 0) return (null, null, usable.Count == 0 && s.Entries.Any(e => e.Currency is not null && e.Currency != Currency) ? "Priced in another currency" : "No preferred or approved price");
        var best = pool.Select(e => (Entry: e, Break: BreakFor(e.PriceBreaks, needed))).OrderBy(p => p.Break.Price.UnitPrice).ThenBy(p => p.Entry.Number, StringComparer.Ordinal).First();
        var basis = best.Break.Below
            ? $"{best.Entry.StatusLabel} entry, below the first price break ({best.Break.Price.Quantity.ToString("0.####", CultureInfo.InvariantCulture)})"
            : $"{best.Entry.StatusLabel} entry, price break from {best.Break.Price.Quantity.ToString("0.####", CultureInfo.InvariantCulture)}";
        return (best.Break.Price.UnitPrice, best.Entry.Number, basis);
    }

    private static (PriceBreak Price, bool Below) BreakFor(IReadOnlyList<PriceBreak> breaks, decimal needed)
    {
        var ordered = breaks.OrderBy(b => b.Quantity).ToList();
        var hit = ordered.LastOrDefault(b => b.Quantity <= needed);
        return hit is null ? (ordered[0], true) : (hit, false);
    }
}
