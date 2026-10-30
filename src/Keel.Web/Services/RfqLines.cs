using System.Globalization;
using Keel.Sourcing;

namespace Keel.Web.Services;

/// <summary>
/// The text form of the lines of a new request ("P-000001 100 2026-12-01": part number, quantity, optional need-by date) and the lines taken from a bill of materials.
/// Plain functions: the page owns the form, this owns the parsing so it can be tested.
/// </summary>
public static class RfqLines
{
    public static (IReadOnlyList<RequestPartInput> Lines, string? Error) Parse(string text)
    {
        var parts = new List<RequestPartInput>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = line.Split([' ', ',', '\t', ';'], StringSplitOptions.RemoveEmptyEntries);
            if (bits.Length is < 2 or > 3 || !decimal.TryParse(bits[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
                return ([], $"'{line}' is not a line. Write the part number, then the quantity, then (if you want) the date it is needed, for example P-000001 100 2026-12-01.");
            DateOnly? needBy = null;
            if (bits.Length == 3)
            {
                if (!DateOnly.TryParseExact(bits[2], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return ([], $"'{bits[2]}' is not a date. Write the need-by date as year-month-day, for example 2026-12-01.");
                needBy = d;
            }
            parts.Add(new RequestPartInput(bits[0], qty, null, null, needBy));
        }
        return (parts, null);
    }

    /// <summary>Price tiers written as "100:2.50, 500:2.10" (from this quantity, this price each). Empty text is no tiers.</summary>
    public static (IReadOnlyList<PriceBreak>? Tiers, string? Error) ParseTiers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        var list = new List<PriceBreak>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(':', StringSplitOptions.TrimEntries);
            if (bits.Length != 2 || !decimal.TryParse(bits[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var q) || !decimal.TryParse(bits[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var p))
                return (null, $"'{part}' is not a price tier. Write quantity:price, for example 100:2.50.");
            list.Add(new PriceBreak(q, p));
        }
        return (list, null);
    }

    public static string Format(IEnumerable<RequestPartInput> lines) =>
        string.Join('\n', lines.Select(l => $"{l.PartNumber} {l.Quantity.ToString("0.####", CultureInfo.InvariantCulture)}{(l.NeedBy is { } d ? " " + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "")}"));

    /// <summary>The purchased lines of a structure (what coverage calls required), the same part on several lines added up, times the number of assemblies. Ordered by part number.</summary>
    public static IReadOnlyList<RequestPartInput> FromCoverage(CoverageReport report, decimal assemblies, string? needBy = null)
    {
        DateOnly? date = DateOnly.TryParseExact(needBy, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        return [.. report.Lines.Where(l => l.Required).GroupBy(l => l.Number, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new RequestPartInput(g.Key, g.Sum(l => l.Quantity) * assemblies, g.First().Unit, null, date))];
    }
}
