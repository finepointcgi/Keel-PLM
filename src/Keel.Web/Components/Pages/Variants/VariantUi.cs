using Keel.Core;
using Keel.Variants;

namespace Keel.Web.Components.Pages.Variants;

/// <summary>Wording, classes and links for the options and variants pages (ADR-0039).</summary>
public static class VariantUi
{
    public static string ProductHref(string number) => $"options/{Uri.EscapeDataString(number)}";
    public static string CompareHref(string number, string? left = null, string? right = null) =>
        $"options/{Uri.EscapeDataString(number)}/compare" + (left is null ? "" : $"?left={Uri.EscapeDataString(left)}&right={Uri.EscapeDataString(right ?? "")}");
    public static string StructureHref(string number, string variant) => $"parts/{Uri.EscapeDataString(number)}?variant={Uri.EscapeDataString(variant)}#bom";
    public static string UsedByHref(string part) => $"parts/{Uri.EscapeDataString(part)}/variants";

    public static string StateClass(string state) => state switch
    {
        VariantStates.Released => "state-approved",
        VariantStates.Draft => "state-inwork",
        _ => "state-obsolete",
    };

    public static string KindLabel(RuleKind kind) => kind switch
    {
        RuleKind.Requires => "Needs",
        RuleKind.Excludes => "Rules out",
        _ => "Cannot be combined",
    };

    public static string SeverityLabel(FindingSeverity s) => s switch { FindingSeverity.Error => "Error", FindingSeverity.Warning => "Warning", _ => "Note" };

    public static string SeverityClass(FindingSeverity s) => s switch { FindingSeverity.Error => "state-obsolete", FindingSeverity.Warning => "state-underreview", _ => "state-inwork" };

    /// <summary>The choices of a variant in words: "Engine: Turbo, Payload: Radar".</summary>
    public static string Choices(ModelView model, VariantView variant) =>
        variant.Selection.Count == 0
            ? "No options chosen"
            : string.Join(", ", variant.Selection.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k =>
            {
                var f = model.Families.FirstOrDefault(x => x.Key == k.Key);
                var v = f?.Values.FirstOrDefault(x => x.Key == k.Value);
                return $"{f?.Name ?? k.Key}: {v?.Name ?? k.Value}";
            }));

    public static string Message<T>(Outcome<T> o) => Keel.Web.Services.UiText.Message(o);
}
