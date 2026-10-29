using Keel.Web.Services.Visuals;

namespace Keel.Web.Components.Visuals;

/// <summary>
/// Which fill class and which pattern a treemap tile gets. Every state and every marking class has BOTH a colour (token) and a pattern, so the picture is readable in
/// black and white and by people who cannot tell the colours apart (ADR-0029). Pattern names are those defined by <see cref="DiagramViewport"/>; null means a plain fill.
/// </summary>
public static class TreemapStyle
{
    public static (string Class, string? Pattern) For(TmNode node, TreemapColour mode)
    {
        if (node.Redacted) return ("restricted", "hatch");
        if (mode == TreemapColour.Markings)
            return node.MarkingClass switch
            {
                "ITAR" => ("itar", "cross"),
                "EAR" => ("ear", "hatch"),
                "CUI" => ("cui", "dots"),
                _ => ("unmarked", null),
            };
        if (node.Unresolved) return ("unresolved", "cross");
        return (node.State ?? "") switch
        {
            "Released" => ("released", null),
            "InWork" => ("inwork", "dots"),
            "UnderReview" => ("underreview", "horiz"),
            "Obsolete" => ("obsolete", "vert"),
            _ => ("other", "horiz"),
        };
    }

    public static IReadOnlyList<LegendItem> Legend(TreemapColour mode) => mode == TreemapColour.Markings
        ?
        [
            new("ITAR (crosshatch)", "itar", LegendItem.Rect, "cross"),
            new("EAR, not EAR99 (diagonal lines)", "ear", LegendItem.Rect, "hatch"),
            new("CUI (dots)", "cui", LegendItem.Rect, "dots"),
            new("No export or CUI marking (plain)", "unmarked", LegendItem.Rect),
            new("Restricted, you may not see it (diagonal lines)", "restricted", LegendItem.Rect, "hatch"),
        ]
        :
        [
            new("Released (plain)", "released", LegendItem.Rect),
            new("In work (dots)", "inwork", LegendItem.Rect, "dots"),
            new("Under review (horizontal lines)", "underreview", LegendItem.Rect, "horiz"),
            new("Obsolete (vertical lines)", "obsolete", LegendItem.Rect, "vert"),
            new("No released revision (crosshatch)", "unresolved", LegendItem.Rect, "cross"),
            new("Restricted, you may not see it (diagonal lines)", "restricted", LegendItem.Rect, "hatch"),
        ];
}
