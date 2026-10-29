namespace Keel.Web.Components.Visuals;

/// <summary>The choices of the comparison pages: the existing table, or the two-pane picture of the same result (UX-3).</summary>
public static class DiffViews
{
    public static readonly IReadOnlyList<ViewOption> Options = [new("table", "Table"), new("visual", "Side by side")];
}
