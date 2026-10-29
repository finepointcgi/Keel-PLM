using System.Globalization;
using Keel.Requirements;

namespace Keel.Web.Services;

/// <summary>Wording and CSS classes for the requirements pages. No rules: those are in <c>RequirementService</c> and <c>RequirementRules</c>.</summary>
public static class RequirementUiText
{
    public static string Href(string number, string? tab = null) => $"requirements/{Uri.EscapeDataString(number)}{(tab is null ? "" : "?tab=" + tab)}";

    public static string StateClass(string state) => "state-" + state.ToLowerInvariant();

    public static string VerificationClass(string state) => state switch
    {
        VerificationStates.Verified => "state-released",
        VerificationStates.Waived => "state-inwork",
        VerificationStates.Stale => "sev-warning",
        VerificationStates.Failed => "sev-error",
        _ => "",
    };

    public static string StatusClass(string status) => status switch
    {
        ReqStatuses.Approved => "state-approved",
        ReqStatuses.Draft => "state-draft",
        ReqStatuses.Deferred => "state-underreview",
        _ => "state-obsolete",
    };

    public static string ObjectHref(string kind, string number) => kind switch
    {
        LinkTargetKinds.Part => "parts/" + Uri.EscapeDataString(number),
        LinkTargetKinds.Document => "documents/" + Uri.EscapeDataString(number),
        LinkTargetKinds.Change => "changes/" + Uri.EscapeDataString(number),
        _ => "requirements/" + Uri.EscapeDataString(number),
    };

    public static string Count(int n, string singular, string plural) => string.Create(CultureInfo.InvariantCulture, $"{n} {(n == 1 ? singular : plural)}");

    public static string Percent(int part, int whole) => whole == 0 ? "0 %" : (100.0 * part / whole).ToString("0", CultureInfo.InvariantCulture) + " %";

    /// <summary>Where an e-signature step-up returns to (this specification, the same tab).</summary>
    public static string StepUpUrl(string number, string tab) => "account/step-up?returnUrl=" + Uri.EscapeDataString("/" + Href(number, tab));
}
