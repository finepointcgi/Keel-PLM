using Keel.Process;

namespace Keel.Web.Components.Pages.Process;

public static class ProcessUi
{
    public static string PlanHref(string number) => "process/plans/" + Uri.EscapeDataString(number);
    public static string ToolHref(string number) => "process/tools/" + Uri.EscapeDataString(number);
    public static string ShopFloorHref(string number, string? routing = null) =>
        $"process/plans/{Uri.EscapeDataString(number)}/shop-floor" + (routing is null ? "" : "?routing=" + Uri.EscapeDataString(routing));

    public static string StateClass(string state) => state switch
    {
        PlanStates.Released => "state-released", PlanStates.UnderReview => "state-underreview", PlanStates.Obsolete => "state-obsolete", _ => "state-inwork",
    };

    public static string CalibrationClass(string state) => state switch { "overdue" or "never" => "state-obsolete", "due_soon" => "state-underreview", "valid" => "state-released", _ => "" };

    public static string SeverityLabel(string severity) => severity switch { Severities.Error => "Error", Severities.Warning => "Warning", _ => "Note" };

    public static string Minutes(decimal? m) => m is { } v ? ProcessRules.Fmt(v) : "";
}
