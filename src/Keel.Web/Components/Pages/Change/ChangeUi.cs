using Keel.Change;
using Keel.Core;
using Keel.Web.Services;

namespace Keel.Web.Components.Pages.Change;

/// <summary>UI wording and links for change management pages.</summary>
public static class ChangeUi
{
    public static string Href(string number) => $"changes/{Uri.EscapeDataString(number)}";

    public static string PartHref(string key, string? revision = null) =>
        $"parts/{Uri.EscapeDataString(key)}{(revision is null ? "" : $"?revision={Uri.EscapeDataString(revision)}")}";

    public static string StateClass(string state) => "state-" + state.ToLowerInvariant();

    public static string ActionLabel(string action) => action switch
    {
        ChangeActions.Revise => "Revise",
        ChangeActions.Obsolete => "Obsolete",
        _ => "Reference only",
    };

    public static string TypeName(string type) => ChangeTypes.Label(type);

    public static string NewHref(string type) => $"changes/new/{type.ToLowerInvariant()}";

    /// <summary>The workflow object type for a change type ("ecr" or "ecn"), or null when the type is not reviewed.</summary>
    public static string? ReviewObjectType(string type) => type is ChangeTypes.Ecr or ChangeTypes.Ecn ? ChangeTypes.ObjectTypeOf(type) : null;

    public static string Message<T>(Outcome<T> outcome) => UiText.Message(outcome);

    public static string DueText(DateOnly? due, bool overdue) =>
        due is null ? "No due date" : (overdue ? "Overdue since " : "Due ") + due.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
