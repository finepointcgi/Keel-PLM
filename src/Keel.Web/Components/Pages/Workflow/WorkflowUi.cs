using Keel.Core;
using Keel.Web.Services;
using Keel.Workflow;

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

    /// <summary>Link to an object's own page, when its module has one.</summary>
    public static string? ObjectLink(string objectType, string key) => objectType == "part" ? $"parts/{Uri.EscapeDataString(key)}" : null;

    public static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " UTC";
}
