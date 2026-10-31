namespace Keel.Web.Components.Shared.Awareness;

/// <summary>The two tabs every object page gets in UX-4: "Activity" (the object's story from the audit log) and "Comments" (with the number of live comments on the label).</summary>
public static class AwarenessTabs
{
    public const string Activity = "activity";
    public const string Comments = "comments";

    public static readonly string[] Ids = [Activity, Comments];

    /// <summary>The page's own tabs followed by Activity and Comments.</summary>
    /// <param name="activityGroup">The shown title of the tab group Activity joins (the page's "Audit trail"), or null to keep it a tab of its own.</param>
    public static IReadOnlyList<TabInfo> Append(IReadOnlyList<TabInfo> own, int? commentCount, string? activityGroup = null) =>
        [.. own, new TabInfo(Activity, "Activity", Group: activityGroup), new TabInfo(Comments, "Comments", commentCount)];
}
