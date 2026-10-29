using System.Globalization;
using Keel.Core;
using Keel.Quality;
using Keel.Web.Services;

namespace Keel.Web.Components.Pages.Quality;

/// <summary>UI wording, routes and small helpers of the quality pages.</summary>
public static class QualityUi
{
    public static string Href(string kind, string number) => AwarenessKinds.Path(kind, number);
    public static string ListHref(string kind) => QualityKinds.ListHref(kind);
    public static string NewHref(string kind) => "quality/new/" + kind;

    /// <summary>The route segment of a kind's list page ("ncrs", "capas", "deviations") and back.</summary>
    public static string Segment(string kind) => kind switch { QualityKinds.Ncr => "ncrs", QualityKinds.Capa => "capas", _ => "deviations" };
    public static string? KindOfSegment(string? segment) => segment switch { "ncrs" => QualityKinds.Ncr, "capas" => QualityKinds.Capa, "deviations" => QualityKinds.Deviation, _ => null };

    public static string PluralLabel(string kind) => kind switch { QualityKinds.Ncr => "Nonconformance reports", QualityKinds.Capa => "Corrective actions", _ => "Deviations and waivers" };
    public static string SingularLower(string kind) => kind switch { QualityKinds.Ncr => "nonconformance report", QualityKinds.Capa => "corrective action", _ => "deviation or waiver" };
    public static string IconOf(string kind) => kind;

    public static string StateClass(string state) => "state-" + state.ToLowerInvariant();

    public static string Message<T>(Outcome<T> outcome) => UiText.Message(outcome);

    public static string DueText(DateOnly? due, bool overdue) =>
        due is null ? "No due date" : (overdue ? "Overdue since " : "Due ") + due.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Date(DateOnly? d) => d is null ? "None" : d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Quantity(decimal q, string unit) => q.ToString("0.####", CultureInfo.InvariantCulture) + " " + unit;

    public static string StatusLabel(string status) => status == "InProgress" ? "In progress" : status;

    public static string ScopeText(ScopeView s) => DeviationService.DescribeScope(s.Revision, s.SerialFrom, s.SerialTo, s.Serials, s.Lots, s.MaxQuantity);

    public static string LinkLabel(string kind) => LinkKinds.Label(kind);

    /// <summary>The page of a link target (relative), or null for a placeholder.</summary>
    public static string? LinkHref(LinkView l) => l.Redacted ? null : l.Href;

    public static DateOnly? ParseDate(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;
}
