using Keel.Core;
using Keel.Web.Services;
using Keel.Workflow;
using Microsoft.Extensions.Localization;

namespace Keel.Web.Components.Pages.Workflow;

/// <summary>What the signer chose in the sign panel.</summary>
public sealed record SignChoice(string Code, string? Comment);

/// <summary>What the submitter chose in the start-review dialog.</summary>
public sealed record StartSelection(string TemplateId, IReadOnlyDictionary<string, string[]> Participants, string? Comment);

/// <summary>UI wording for workflow reason codes and outcomes. Falls back to the shared reason texts.</summary>
public static class WorkflowText
{
    private static readonly Dictionary<string, string> Reasons = new(StringComparer.Ordinal)
    {
        ["task.not_eligible"] = "You are no longer eligible to see or sign this item.",
        ["task.not_assignee"] = "This task is assigned to someone else.",
        ["task.not_open"] = "This task has already been completed or skipped.",
        ["sod.submitter"] = "You submitted this review, so you cannot review or approve it (separation of duties).",
        ["instance.not_active"] = "This workflow is no longer active.",
        ["object.state_changed"] = "The object is no longer under review at the iteration this workflow covers.",
        ["workflow.cancel_not_starter_or_admin"] = "Only the person who started the workflow, or an administrator, can cancel it.",
        ["stepup.required"] = "Sign in again to confirm it is you.",
    };

    public static string Reason(string code) => Reasons.TryGetValue(code, out var text) ? text : UiText.Reason(code);

    /// <summary>The reason in the person's language; the workflow's own codes first, then the shared ones (resource keys <c>Reason.&lt;code&gt;</c>).</summary>
    public static string Reason(string code, IStringLocalizer common) =>
        Reasons.TryGetValue(code, out var text) ? Keel.Web.Localization.LocalizerExtensions.Or(common, "Reason." + code, text) : UiText.Reason(code, common);

    public static string Message<T>(Outcome<T> o, IStringLocalizer common) => o.Kind switch
    {
        OutcomeKind.Forbidden => string.Join(" ", o.Errors.Select(e => Reason(e, common))),
        OutcomeKind.StepUpRequired => Reason("stepup.required", common),
        _ => UiText.Message(o, common),
    };

    public static string Message<T>(Outcome<T> o) => o.Kind switch
    {
        OutcomeKind.Forbidden => string.Join(" ", o.Errors.Select(Reason)),
        OutcomeKind.StepUpRequired => Reason("stepup.required"),
        _ => UiText.Message(o),
    };

    public static string Role(string role) => role switch
    {
        Roles.Viewer => "viewer or higher",
        Roles.Author => "author",
        Roles.Approver => "approver",
        Roles.Admin => "administrator",
        _ => role,
    };

    public static string Status(string status) => status switch
    {
        InstanceStatus.Approved => "Released",
        _ => status,
    };

    public static string Due(DateTimeOffset? due, bool overdue) => due is null ? "No due date" : (overdue ? "Overdue since " : "Due ") + UiText.Utc(due.Value) + " UTC";

    /// <summary>The due date in the person's language and date order (still shown in UTC).</summary>
    public static string Due(DateTimeOffset? due, bool overdue, IStringLocalizer common, Keel.Web.Localization.UiLanguage language) =>
        due is null ? common["Due.None"].Value : common[overdue ? "Due.Overdue" : "Due.On", language.Utc(due.Value)].Value;

    /// <summary>Link to an object's own page, when its module has one.</summary>
    public static string? ObjectLink(string objectType, string key) => objectType switch
    {
        "part" => $"parts/{Uri.EscapeDataString(key)}",
        "document" => $"documents/{Uri.EscapeDataString(key)}", // 7.6: documents are reviewed through the same engine
        "ecr" or "ecn" => $"changes/{Uri.EscapeDataString(key)}", // 1.6: change requests and change notices
        "baseline" => $"baselines/{Uri.EscapeDataString(key)}", // 2.1: baselines established by signatures
        "ncr" or "capa" or "deviation" => AwarenessKinds.Path(objectType, key), // 8.1: quality records reviewed through the same engine
        "gate" => AwarenessKinds.Path("gate", key), // 8.8: gate reviews approved by signatures
        "reqspec" => $"requirements/{Uri.EscapeDataString(key)}", // 8.3: requirement specifications released by signatures
        "supplier" => $"suppliers/{Uri.EscapeDataString(key)}", // 8.2: supplier approval
        "process_plan" => $"process/plans/{Uri.EscapeDataString(key)}", // 8.7: process plan release
        "source_entry" => $"sourcing/entries/{Uri.EscapeDataString(key)}", // 8.2: approved list entries
        "retention_schedule" => $"records/schedules/{key.TrimStart('v')}", // 8.6: retention schedule publication
        "legal_hold" => $"records/holds/{Uri.EscapeDataString(key)}", // 8.6: legal hold release
        "disposition" => $"records/disposition/{Uri.EscapeDataString(key)}", // 8.6: disposition approval
        "reg_list_version" => TryListVersionHref(key), // 8.5: regulatory list versions (key is "{list code}-{version}")
        _ => null,
    };

    private static string? TryListVersionHref(string key)
    {
        var dash = key.LastIndexOf('-');
        return dash > 0 && int.TryParse(key.AsSpan(dash + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? $"compliance/lists/{Uri.EscapeDataString(key[..dash])}/versions/{v}" : null;
    }

    public static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " UTC";
}
