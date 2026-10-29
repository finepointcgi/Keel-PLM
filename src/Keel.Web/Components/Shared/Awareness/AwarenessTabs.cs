namespace Keel.Web.Components.Shared.Awareness;

/// <summary>The two tabs every object page gets in UX-4: "Activity" (the object's story from the audit log) and "Comments" (with the number of live comments on the label).</summary>
public static class AwarenessTabs
{
    public const string Activity = "activity";
    public const string Comments = "comments";

    public static readonly string[] Ids = [Activity, Comments];

    /// <summary>The page's own tabs followed by Activity and Comments.</summary>
    public static IReadOnlyList<TabInfo> Append(IReadOnlyList<TabInfo> own, int? commentCount) =>
        [.. own, new TabInfo(Activity, "Activity"), new TabInfo(Comments, "Comments", commentCount)];
}
