using System.Globalization;
using Keel.Bom;
using Keel.Core;

namespace Keel.Web.Services;

/// <summary>Editable state of the "which EBOM" selector (EBOM-to-MBOM derivation and reconciliation). Parsing and validation of the values are shared with the service.</summary>
public sealed class EbomSpecForm
{
    public string Kind { get; set; } = nameof(EbomSpecKind.LatestReleased);
    public string Baseline { get; set; } = "";
    public string Iteration { get; set; } = "";
    public UnitForm Unit { get; } = new();

    public EbomSpecKind SpecKind => Enum.TryParse<EbomSpecKind>(Kind, out var k) ? k : EbomSpecKind.LatestReleased;

    /// <summary>The "same as the last reconciliation" choice: no spec is sent and the service uses the spec of the previous run (else the latest released EBOM).</summary>
    public bool IsDefault => Kind.Length == 0;

    /// <summary>The selection, or an error in words. Range checks beyond syntax are the service's.</summary>
    public (EbomSpec? Spec, string? Error) ToSpec()
    {
        if (IsDefault) return (null, null);
        var kind = SpecKind;
        switch (kind)
        {
            case EbomSpecKind.Effectivity:
                var unit = Unit.ToRequest();
                return unit.Error is not null ? (null, unit.Error) : (new EbomSpec(kind, unit.Request), null);
            case EbomSpecKind.Baseline:
                return string.IsNullOrWhiteSpace(Baseline) ? (null, "Name the baseline.") : (new EbomSpec(kind, Baseline: Baseline.Trim().ToUpperInvariant()), null);
            case EbomSpecKind.Iteration:
                return MbomRules.IterationLabel.IsMatch(Iteration.Trim().ToUpperInvariant()) ? (new EbomSpec(kind, IterationLabel: Iteration.Trim().ToUpperInvariant()), null) : (null, "Write the version like B.1.");
            default:
                return (new EbomSpec(kind), null);
        }
    }
}

/// <summary>Wording and CSS classes for the MBOM pages. No rules: those are in <c>MbomService</c> and <c>ReconcileEngine</c>.</summary>
public static class MbomUiText
{
    public static string SeverityClass(string severity) => severity switch { FindingSeverities.Error => "sev-error", FindingSeverities.Warning => "sev-warning", _ => "sev-info" };

    public static string SeverityLabel(string severity) => severity switch { FindingSeverities.Error => "Errors", FindingSeverities.Warning => "Warnings", _ => "Notes" };

    public static string StatusClass(string? status) => status switch
    {
        ReconcileStatuses.Reconciled => "state-released",
        ReconcileStatuses.WithExceptions => "state-underreview",
        ReconcileStatuses.NotReconciled => "sev-error",
        ReconcileStatuses.Stale => "sev-warning",
        _ => "",
    };

    public static string StateClass(string state) => "state-" + state.ToLowerInvariant();

    public static string RowState(string state) => state switch
    {
        "linked" => "Linked",
        "ebom_only" => "EBOM line not in the MBOM",
        "mbom_only" => "Manufacturing only",
        _ => "No EBOM source",
    };

    public static string LinkText(LinkView l) =>
        $"{l.EbomFindNumber.ToString(CultureInfo.InvariantCulture)} {l.Status.Replace('_', ' ')}";

    public static string Href(string number) => $"mboms/{Uri.EscapeDataString(number)}";

    public static string Qty(decimal d) => MbomText.Qty(d);

    public static string Percent(decimal? scrap) => scrap is { } s ? (s * 100).ToString("0.##", CultureInfo.InvariantCulture) + " %" : "";

    public static string ProposalKind(string kind) => kind switch
    {
        ProposalKinds.AddLine => "Add line",
        ProposalKinds.RemoveLine => "Remove line",
        ProposalKinds.ChangeQuantity => "Change quantity",
        ProposalKinds.ChangeUnit => "Change unit",
        ProposalKinds.ChangeEffectivity => "Change effectivity",
        ProposalKinds.ReviewAdjusted => "Review: declared adjustment",
        ProposalKinds.ReviewSubstituted => "Review: substitution",
        _ => kind,
    };
}
