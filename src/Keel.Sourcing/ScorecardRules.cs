using System.Globalization;
using Keel.Core;

namespace Keel.Sourcing;

public sealed record ScorecardComponentView(string Key, string Label, int Weight, bool Available, decimal? Score, string Measure);

public sealed record ScorecardPoint(DateOnly AsOf, decimal? Score);

/// <summary>A supplier scorecard as of a date: the score, how it was made and the monthly trend. Carries counts and rates only, never the title or text of a record. <see cref="Banner"/> is the aggregate marking of the records the counts come from.</summary>
public sealed record ScorecardView(
    DateOnly AsOf, decimal? Score, string Band, IReadOnlyList<ScorecardComponentView> Components, IReadOnlyList<ScorecardPoint> Trend, IReadOnlyList<string> Formula, int FormulaVersion,
    string Banner = "UNCLASSIFIED");

public sealed record ScorecardPublicationView(Guid Id, DateTimeOffset PublishedAt, string PublishedBy, DateOnly AsOf, decimal? Score);

/// <summary>The scorecard as the buyer sees it. <see cref="NotCounted"/> is the number of linked quality records that were cancelled or that the viewer may not read.</summary>
public sealed record SupplierScorecard(string SupplierNumber, string SupplierName, string Banner, ScorecardView Card, int NotCounted, ScorecardPublicationView? Published, bool CanPublish);

/// <summary>What a supplier sees in the portal: the scorecard its buyer published, frozen at the time of publication.</summary>
public sealed record PortalScorecard(ScorecardView Card, DateTimeOffset PublishedAt);

/// <summary>An issued request as the responsiveness measure sees it.</summary>
public sealed record RequestOutcome(DateOnly IssuedOn, DateOnly? DueOn, DateOnly? FirstAnsweredOn);

public sealed record CertificationFact(DateOnly IssuedOn, DateOnly ExpiresOn, bool Revoked);

/// <summary>
/// The supplier scorecard formula (version 1, ADR-0054, assumption A77). Pure: given the facts and a date it returns the same card every time. Each component is a score from 0 to
/// 100; the card is their weighted average over the components that can be measured (a component with no basis is shown as not measured and its weight is left out, never counted as
/// 0 or 100). Everything is computed as of a date from the dates recorded, so the trend is the same measure as the card.
/// </summary>
public static class ScorecardRules
{
    // ASSUMPTION(A77): the weights, point values, windows and bands of formula version 1 are proposals, written on the page in plain words.
    public const int FormulaVersion = 1;
    public const int WindowMonths = 12;
    public const int TrendMonths = 12;

    public const string Ncr = "ncr", Capa = "capa", Rfq = "rfq", Certs = "certs", Delivery = "delivery";
    public static readonly IReadOnlyDictionary<string, int> Weights = new Dictionary<string, int> { [Ncr] = 40, [Capa] = 10, [Rfq] = 20, [Certs] = 20, [Delivery] = 10 };
    private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
    {
        [Ncr] = "Nonconformances", [Capa] = "Corrective actions", [Rfq] = "Responsiveness to requests", [Certs] = "Certifications", [Delivery] = "On-time delivery",
    };

    /// <summary>Points per nonconformance by severity (a minor one costs 10 points of that component, a major one 30, a critical one 80).</summary>
    public static int SeverityPoints(string? severity) => severity switch { "Critical" => 8, "Major" => 3, _ => 1 };

    public static readonly IReadOnlyList<string> Formula =
    [
        "Score = the weighted average of the measured components (0 to 100). A component without a basis is not measured and its weight is left out.",
        "Nonconformances (weight 40): 100 minus 10 for each point, over the nonconformances opened in the last 12 months and linked to the supplier. A minor one is 1 point, a major one 3, a critical one 8. Not below 0.",
        "Corrective actions (weight 10): 100 minus 25 for each linked corrective action that is open on the date. Not below 0.",
        "Responsiveness to requests (weight 20): the share of requests issued to the supplier in the last 12 months, that are due or answered, which it answered on or before the due date (a decline is an answer).",
        "Certifications (weight 20): the share of the supplier's certifications issued on or before the date that are not revoked and not expired on the date.",
        "On-time delivery (weight 10): not measured. Keel records no deliveries or receipts yet, so this component is shown but never counted.",
        "Nonconformances and corrective actions are measured only when at least one quality record is linked to the supplier. Cancelled records and records you may not read are not counted.",
        "Bands: 85 and above is good, 65 to under 85 is to watch, under 65 is poor.",
    ];

    public static string Band(decimal? score) => score is null ? "Not enough data" : score >= 85 ? "Good" : score >= 65 ? "Watch" : "Poor";

    /// <summary>The month ends of the trend (oldest first); the newest point is the date itself.</summary>
    public static IReadOnlyList<DateOnly> TrendDates(DateOnly today)
    {
        var list = new List<DateOnly>();
        for (var i = TrendMonths - 1; i >= 1; i--)
        {
            var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-i);
            list.Add(new DateOnly(first.Year, first.Month, DateTime.DaysInMonth(first.Year, first.Month)));
        }
        list.Add(today);
        return list;
    }

    public static ScorecardView Compute(
        DateOnly asOf, IReadOnlyList<SupplierQualityFact> facts, int linkedQualityRecords, IReadOnlyList<RequestOutcome> requests, IReadOnlyList<CertificationFact> certifications, DateOnly today)
    {
        var components = Components(asOf, facts, linkedQualityRecords, requests, certifications);
        var trend = TrendDates(today).Select(d => new ScorecardPoint(d, Score(Components(d, facts, linkedQualityRecords, requests, certifications)))).ToList();
        var score = Score(components);
        return new ScorecardView(asOf, score, Band(score), components, trend, Formula, FormulaVersion);
    }

    public static decimal? Score(IReadOnlyList<ScorecardComponentView> components)
    {
        var measured = components.Where(c => c.Available && c.Score is not null).ToList();
        var weight = measured.Sum(c => c.Weight);
        return weight == 0 ? null : Math.Round(measured.Sum(c => c.Weight * c.Score!.Value) / weight, 1, MidpointRounding.AwayFromZero);
    }

    public static IReadOnlyList<ScorecardComponentView> Components(
        DateOnly asOf, IReadOnlyList<SupplierQualityFact> facts, int linkedQualityRecords, IReadOnlyList<RequestOutcome> requests, IReadOnlyList<CertificationFact> certifications)
    {
        var start = asOf.AddMonths(-WindowMonths);
        var counted = facts.Where(f => !f.Cancelled).ToList();
        var list = new List<ScorecardComponentView>();

        if (linkedQualityRecords == 0)
        {
            list.Add(Unmeasured(Ncr, "No quality record is linked to this supplier."));
            list.Add(Unmeasured(Capa, "No quality record is linked to this supplier."));
        }
        else
        {
            var ncrs = counted.Where(f => f.Kind == "ncr" && Day(f.OpenedAt) <= asOf && Day(f.OpenedAt) > start).ToList();
            var points = ncrs.Sum(f => SeverityPoints(f.Severity));
            var minor = ncrs.Count(f => SeverityPoints(f.Severity) == 1);
            var major = ncrs.Count(f => f.Severity == "Major");
            var critical = ncrs.Count(f => f.Severity == "Critical");
            list.Add(new ScorecardComponentView(Ncr, Labels[Ncr], Weights[Ncr], true, Math.Max(0, 100 - 10m * points),
                ncrs.Count == 0 ? "No nonconformance in the last 12 months."
                    : $"{ncrs.Count} in the last 12 months ({critical} critical, {major} major, {minor} minor), {points.ToString(CultureInfo.InvariantCulture)} points."));
            var open = counted.Count(f => f.Kind == "capa" && Day(f.OpenedAt) <= asOf && (f.ClosedAt is null || Day(f.ClosedAt.Value) > asOf));
            list.Add(new ScorecardComponentView(Capa, Labels[Capa], Weights[Capa], true, Math.Max(0, 100 - 25m * open),
                open == 0 ? "No corrective action is open." : $"{open} corrective action{(open == 1 ? " is" : "s are")} open."));
        }

        var inWindow = requests.Where(r => r.IssuedOn <= asOf && r.IssuedOn > start && ((r.DueOn is { } due && due <= asOf) || (r.FirstAnsweredOn is { } a && a <= asOf))).ToList();
        if (inWindow.Count == 0) list.Add(Unmeasured(Rfq, "No request issued to this supplier in the last 12 months is due or answered yet."));
        else
        {
            var onTime = inWindow.Count(r => r.FirstAnsweredOn is { } a && a <= asOf && (r.DueOn is null || a <= r.DueOn));
            var days = inWindow.Where(r => r.FirstAnsweredOn is { } a && a <= asOf).Select(r => r.FirstAnsweredOn!.Value.DayNumber - r.IssuedOn.DayNumber).ToList();
            list.Add(new ScorecardComponentView(Rfq, Labels[Rfq], Weights[Rfq], true, Math.Round(100m * onTime / inWindow.Count, 1, MidpointRounding.AwayFromZero),
                $"{onTime} of {inWindow.Count} request{(inWindow.Count == 1 ? "" : "s")} answered on time" + (days.Count > 0 ? $"; first answer after {days.Average():0.#} days on average." : "; none answered.")));
        }

        var certs = certifications.Where(c => c.IssuedOn <= asOf).ToList();
        if (certs.Count == 0) list.Add(Unmeasured(Certs, "The supplier has no certification on record."));
        else
        {
            var valid = certs.Count(c => !c.Revoked && c.ExpiresOn >= asOf);
            list.Add(new ScorecardComponentView(Certs, Labels[Certs], Weights[Certs], true, Math.Round(100m * valid / certs.Count, 1, MidpointRounding.AwayFromZero),
                $"{valid} of {certs.Count} certification{(certs.Count == 1 ? " is" : "s are")} valid."));
        }

        list.Add(Unmeasured(Delivery, "Keel records no deliveries yet."));
        return list;
    }

    private static ScorecardComponentView Unmeasured(string key, string reason) => new(key, Labels[key], Weights[key], false, null, "Not measured: " + reason);

    private static DateOnly Day(DateTimeOffset t) => DateOnly.FromDateTime(t.UtcDateTime);
}
