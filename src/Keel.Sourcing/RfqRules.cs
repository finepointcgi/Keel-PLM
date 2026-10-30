using System.Globalization;

namespace Keel.Sourcing;

public static partial class SourcingRules
{
    // ASSUMPTION(A76): the price at a quantity is the tier with the largest quantity not above it (none below the smallest tier); quotes in different currencies are never ranked.
    public const int MaxTiers = 10;
    public const int MaxQuoteTextLength = 500;

    // ---------------------------------------------------------------- quotes

    /// <summary>The checks of what a quote line adds to the single price: tiers, a currency for any price, notes and exceptions.</summary>
    public static IReadOnlyList<string> ValidateQuoteExtras(QuoteLine q)
    {
        var errors = new List<string>();
        var tiers = q.Tiers ?? [];
        if (tiers.Count > MaxTiers) errors.Add($"{q.Part}: at most {MaxTiers} price tiers.");
        for (var i = 0; i < tiers.Count; i++)
        {
            if (tiers[i].Quantity <= 0) errors.Add($"{q.Part}: tier {i + 1}: the quantity must be positive.");
            if (tiers[i].UnitPrice < 0) errors.Add($"{q.Part}: tier {i + 1}: the price cannot be negative.");
            if (i > 0 && tiers[i].Quantity <= tiers[i - 1].Quantity) errors.Add($"{q.Part}: tier {i + 1}: quantities must increase.");
        }
        if ((q.UnitPrice is not null || tiers.Count > 0) && string.IsNullOrWhiteSpace(q.Currency)) errors.Add($"{q.Part}: a price needs a currency such as USD.");
        if (q.Note is { Length: > MaxQuoteTextLength }) errors.Add($"{q.Part}: the note is at most {MaxQuoteTextLength} characters.");
        if (q.Exception is { Length: > MaxQuoteTextLength }) errors.Add($"{q.Part}: the exception is at most {MaxQuoteTextLength} characters.");
        return errors;
    }

    /// <summary>
    /// The unit price of a quote line at a quantity. With tiers, the tier with the largest quantity that is not above the asked quantity decides; when the asked quantity is
    /// below the smallest tier the line has no price at that quantity (null: the quote does not cover it). Without tiers the single price applies. Null when nothing is priced.
    /// </summary>
    public static decimal? PriceAt(QuoteLine line, decimal quantity)
    {
        if (line.Tiers is { Count: > 0 } tiers)
        {
            decimal? price = null;
            foreach (var t in tiers.OrderBy(t => t.Quantity))
                if (t.Quantity <= quantity) price = t.UnitPrice;
            return price;
        }
        return line.UnitPrice;
    }

    /// <summary>True when the quote has tiers and the asked quantity is below its smallest one.</summary>
    public static bool BelowSmallestTier(QuoteLine line, decimal quantity) => line.Tiers is { Count: > 0 } tiers && tiers.Min(t => t.Quantity) > quantity;

    // ---------------------------------------------------------------- comparison

    /// <summary>One supplier's answer to one line, as the comparison shows it.</summary>
    public sealed record CompareCell(
        string SupplierNumber, bool Quoted, decimal? UnitPrice, string? Currency, decimal? ExtendedPrice, int? LeadTimeDays, decimal? Moq, string? Note, string? Exception,
        bool BelowSmallestTier, bool? MeetsNeedBy, bool Lowest, bool Fastest, bool Awarded, IReadOnlyList<PriceBreak> Tiers);

    /// <summary>
    /// Builds the cells of one line. A price is compared only among quotes in the same currency (no conversion rate is known): when the quoted currencies differ, nobody is
    /// marked lowest. The shortest lead time is marked among quotes that give one. <paramref name="today"/> decides whether an order placed today meets the need-by date.
    /// </summary>
    public static IReadOnlyList<CompareCell> CompareLine(
        decimal quantity, DateOnly? needBy, DateOnly today, IReadOnlyList<(string Supplier, QuoteLine? Line)> quotes, string? awardedSupplier)
    {
        var priced = quotes.Select(q => (q.Supplier, q.Line, Price: q.Line is null ? null : PriceAt(q.Line, quantity))).ToList();
        var currencies = priced.Where(p => p.Price is not null).Select(p => p.Line!.Currency?.ToUpperInvariant()).Distinct().ToList();
        decimal? lowest = currencies.Count == 1 ? priced.Where(p => p.Price is not null).Min(p => p.Price) : null;
        var leads = priced.Where(p => p.Line?.LeadTimeDays is not null).Select(p => p.Line!.LeadTimeDays!.Value).ToList();
        int? shortest = leads.Count > 0 ? leads.Min() : null;
        var cells = new List<CompareCell>();
        foreach (var (supplier, line, price) in priced)
        {
            if (line is null)
            {
                cells.Add(new CompareCell(supplier, false, null, null, null, null, null, null, null, false, null, false, false, supplier == awardedSupplier, []));
                continue;
            }
            bool? meets = needBy is { } nb && line.LeadTimeDays is { } lead ? today.AddDays(lead) <= nb : null;
            cells.Add(new CompareCell(supplier, true, price, line.Currency?.ToUpperInvariant(),
                price is null ? null : Math.Round(price.Value * quantity, 2, MidpointRounding.AwayFromZero), line.LeadTimeDays, line.Moq, line.Note, line.Exception, BelowSmallestTier(line, quantity), meets,
                lowest is not null && price == lowest, shortest is not null && line.LeadTimeDays == shortest, supplier == awardedSupplier, line.Tiers ?? []));
        }
        return cells;
    }

    public static string Money(decimal value, string? currency) => value.ToString("0.00####", CultureInfo.InvariantCulture) + (currency is null ? "" : " " + currency);
}
