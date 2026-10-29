using System.Globalization;
using Keel.Compliance;

namespace Keel.Web.Components.Pages.Compliance;

/// <summary>Wording, classes and links for the compliance pages.</summary>
public static class ComplianceUi
{
    public static string PartHref(string number) => $"parts/{Uri.EscapeDataString(number)}?tab=compliance";
    public static string DeclarationHref(string number) => $"compliance/declarations/{Uri.EscapeDataString(number)}";
    public static string ListHref(string code) => $"compliance/lists/{Uri.EscapeDataString(code)}";
    public static string VersionHref(string code, int version) => $"compliance/lists/{Uri.EscapeDataString(code)}/versions/{version.ToString(CultureInfo.InvariantCulture)}";
    public static string SubstanceHref(string number) => $"compliance/substances/{Uri.EscapeDataString(number)}";
    public static string RollupHref(string mode, string number) => $"compliance?mode={mode}&number={Uri.EscapeDataString(number)}";
    public static string ExportHref(string mode, string number, string format) => $"downloads/compliance/export?mode={mode}&number={Uri.EscapeDataString(number)}&format={format}";

    public static string Date(DateOnly? d) => d is { } v ? v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
    public static string Num(decimal d) => ComplianceRules.Num(d);

    public static string StatusClass(string status) => status switch
    {
        DeclarationStatuses.Verified => "state-approved",
        DeclarationStatuses.Declared => "state-inwork",
        DeclarationStatuses.NonCompliant => "kind-blocked",
        _ => "state-obsolete",
    };

    public static string StandingClass(string standing) => standing switch
    {
        Standings.Compliant or Standings.Exempt => "state-approved",
        Standings.NonCompliant => "kind-blocked",
        Standings.Gap or Standings.Unverified => "state-underreview",
        _ => "state-obsolete",
    };

    public static string AssemblyClass(string standing) => standing switch
    {
        Standings.AssemblyCompliant or Standings.AssemblyExempt => "state-approved",
        Standings.AssemblyNonCompliant => "kind-blocked",
        Standings.AssemblyIncomplete => "state-underreview",
        _ => "state-obsolete",
    };

    public static string ResultClass(string result) => result switch
    {
        EvaluationResults.Compliant or EvaluationResults.Exempt => "state-approved",
        EvaluationResults.NonCompliant => "kind-blocked",
        _ => "state-underreview",
    };

    public static string VersionClass(string status) => status switch
    {
        VersionStates.Published => "state-approved",
        VersionStates.InReview => "state-underreview",
        VersionStates.Draft => "state-inwork",
        _ => "state-obsolete",
    };

    public static string Countdown(DeclarationSummary d) => d.Status == DeclarationStatuses.Withdrawn ? "withdrawn"
        : d.DaysToExpiry < 0 ? $"expired {-d.DaysToExpiry} day{(d.DaysToExpiry == -1 ? "" : "s")} ago"
        : $"{d.DaysToExpiry} day{(d.DaysToExpiry == 1 ? "" : "s")} left";

    /// <summary>The words for what a finding says about one line.</summary>
    public static string FindingText(FindingView f) =>
        $"{f.SubstanceName} ({f.CasNumber}): {Num(f.Percent)} % against a threshold of {Num(f.ThresholdPct)} %. {f.ReasonLabel}{(f.ExemptionCode is null ? "" : $" ({f.ExemptionCode})")}.";
}
