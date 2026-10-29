using System.Globalization;
using Keel.Core;
using Keel.Programs;
using Keel.Web.Services;

namespace Keel.Web.Components.Pages.Programs;

/// <summary>UI wording, routes and small helpers of the program pages.</summary>
public static class ProgramUi
{
    public static string Href(string code) => AwarenessKinds.Path(AwarenessKinds.Program, code);
    public static string GateHref(string number) => AwarenessKinds.Path(AwarenessKinds.Gate, number);
    public static string Date(DateOnly? d) => d is null ? "None" : d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Message<T>(Outcome<T> outcome) => UiText.Message(outcome);
    public static string StateClass(string status) => "state-" + status.ToLowerInvariant();
    public static string KindLabel(string kind) => kind.ToLowerInvariant();
    public static string ReadinessClass(string r) => r switch { Readiness.Ready => "state-released", Readiness.NotVisible => "state-obsolete", Readiness.Ended => "state-obsolete", _ => "state-underreview" };
    public static string Slip(int days) => days > 0 ? $"{days} day{(days == 1 ? "" : "s")} late" : days < 0 ? $"{-days} day{(days == -1 ? "" : "s")} early" : "On plan";
    public static string Percent(int p) => p.ToString(CultureInfo.InvariantCulture) + "%";
}
