using System.Globalization;
using Keel.Core;

namespace Keel.Bom;

public static class MbomStates
{
    public const string InWork = "InWork";
    public const string UnderReview = "UnderReview";
    public const string Released = "Released";
    public const string Obsolete = "Obsolete";
}

public static class MbomLimits
{
    /// <summary>ASSUMPTION (MBOM-9): an MBOM iteration owns at most this many lines, like an EBOM parent.</summary>
    public const int MaxLines = BomLimits.MaxChildrenPerParent;
    /// <summary>A multi-level generate creates at most this many MBOMs in one transaction.</summary>
    public const int MaxGenerated = 200;
    public const int MaxReasonLength = 500;
    public const int MaxOperationRef = 60;
    public const decimal MaxScrap = 10m;
    public const int MaxFindingsShown = 5000;
    public const int MaxExplosionLines = BomLimits.MaxExplosionLines;
}

public static class LinkStatuses
{
    public const string Matched = "matched";
    public const string QuantityAdjusted = "quantity_adjusted";
    public const string Substituted = "substituted";

    public static readonly IReadOnlyList<string> All = [Matched, QuantityAdjusted, Substituted];
}

/// <summary>How the EBOM to derive from or reconcile against is chosen (the BOM module's configuration specs, plus an explicit iteration).</summary>
public enum EbomSpecKind { Latest, LatestReleased, Effectivity, Baseline, Iteration }

/// <summary>
/// Selects one iteration of a part's EBOM: <c>Latest</c> (latest revision, latest iteration), <c>LatestReleased</c>, <c>Effectivity</c> (the revision serving the
/// unit, lines applying to it), <c>Baseline</c> (the iteration the baseline pinned for the part) or an explicit <c>Iteration</c> such as "B.1".
/// </summary>
public sealed record EbomSpec(EbomSpecKind Kind = EbomSpecKind.LatestReleased, EffectivityRequest? Unit = null, string? Baseline = null, string? IterationLabel = null)
{
    public static readonly EbomSpec Released = new(EbomSpecKind.LatestReleased);

    public string Describe() => Kind switch
    {
        EbomSpecKind.Latest => "Latest",
        EbomSpecKind.LatestReleased => "Latest released",
        EbomSpecKind.Effectivity => $"As of {Unit?.Describe()}",
        EbomSpecKind.Baseline => $"Baseline {Baseline}",
        _ => $"Iteration {IterationLabel}",
    };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(Kind)) errors.Add("Unknown EBOM selection.");
        if (Kind == EbomSpecKind.Effectivity)
        {
            if (Unit is null) errors.Add("Name the serial number, lot or date to use the EBOM for.");
            else errors.AddRange(Unit.Validate());
        }
        if (Kind == EbomSpecKind.Baseline && string.IsNullOrWhiteSpace(Baseline)) errors.Add("Name the baseline.");
        if (Kind == EbomSpecKind.Iteration && !MbomRules.IterationLabel.IsMatch(IterationLabel ?? "")) errors.Add("An iteration is written like B.1 (revision, dot, iteration).");
        return errors;
    }
}

// ---------------------------------------------------------------- plants and lists

public sealed record PlantInfo(string Code, string Name, bool IsActive, bool ReleaseAllowsWarnings, int SortOrder);
public sealed record SupplyTypeInfo(string Code, string Label, bool IsActive, int SortOrder, bool IsSystem);
public sealed record SavePlantCommand(string Code, string Name, bool IsActive = true, bool ReleaseAllowsWarnings = true, int SortOrder = 100);
public sealed record SaveSupplyTypeCommand(string Code, string Label, bool IsActive = true, int SortOrder = 100);

// ---------------------------------------------------------------- header, search, detail

public sealed record MbomSummary(
    Guid Id, string Number, string PartNumber, string PartName, string Plant, string Revision, int Iteration, string State, string StateLabel,
    string? DerivedFrom, string? ReconciliationStatus, string? CheckedOutBy, string Banner, Markings Markings);

public sealed record MbomSearch(string? Query = null, string? Plant = null, string? State = null, string? PartNumber = null, int Limit = 50, int Offset = 0);

public sealed record MbomSearchResult(IReadOnlyList<MbomSummary> Items, long Total, string PageBanner);

public sealed record MbomRevisionInfo(Guid Id, string Revision, string State, string StateLabel, bool IsLatest, int LatestIteration, DateTimeOffset StateChangedAt, string StateChangedBy);

public sealed record MbomIterationInfo(
    Guid Id, int Iteration, string Label, string? Comment, string? DerivedFrom, string? DerivedSpec, int LineCount, int LinkCount, string ContentSha256, DateTimeOffset CreatedAt, string CreatedBy);

public sealed record MbomCheckoutInfo(string UserId, string? UserName, DateTimeOffset At, bool IsMine, string? DerivedFrom, string? DerivedSpec);

public sealed record MbomTransitionInfo(string To, string Label, bool Allowed, string? Reason);

public sealed record MbomPermissions(bool CanModify, bool CanAdminister, IReadOnlyList<string> ModifyDeniedReasons);

public sealed record ReconciliationBrief(
    Guid Id, string Status, DateTimeOffset RunAt, string RunBy, string Spec, int Errors, int Warnings, int Infos, decimal CoveragePct, bool FreshForRelease, string? NotFreshReason);

public sealed record MbomDetail(
    MbomSummary Summary, string Description, PlantInfo Plant, string PartState,
    IReadOnlyList<MbomRevisionInfo> Revisions, MbomRevisionInfo Displayed, IReadOnlyList<MbomIterationInfo> Iterations, MbomIterationInfo Iteration,
    MbomCheckoutInfo? Checkout, IReadOnlyList<MbomTransitionInfo> Transitions, MbomPermissions Permissions, ReconciliationBrief? LastRun,
    /// <summary>Why this MBOM cannot be submitted for review now (reconciliation missing, stale or with errors); null when it can.</summary>
    string? ReleaseBlocker);

public sealed record CreateMbomCommand(string PartNumber, string Plant, Markings? Markings = null, string? Description = null, bool ConfirmRaiseMarkings = false);

public sealed record GenerateMbomCommand(
    string PartNumber, string Plant, EbomSpec? Spec = null, bool MultiLevel = false, Markings? Markings = null, bool ConfirmRaiseMarkings = false);

/// <summary>One MBOM created by a generate, with what was copied.</summary>
public sealed record GeneratedMbom(string MbomNumber, string PartNumber, string Plant, string DerivedFrom, int Lines, bool Created, string? Note);

public sealed record GenerateResult(IReadOnlyList<GeneratedMbom> Items, IReadOnlyList<string> Notices);

// ---------------------------------------------------------------- lines and links

public sealed record LinkView(Guid EbomUsageId, int EbomFindNumber, string Status, string? Reason, string? EbomChildNumber);

/// <summary>A line of an MBOM iteration or of the caller's working copy. A line whose part the caller may not read keeps find number, quantity and unit only (A4).</summary>
public sealed record MbomLineView(
    Guid Id, int FindNumber, int Alt, bool Redacted, Guid? ChildMasterId, string? ChildNumber, string? ChildName, string? ChildPartType, string? ChildSource,
    decimal Quantity, string Unit, decimal? ScrapFactor, bool Phantom, string? SupplyType, string? OperationReference, bool MbomOnly,
    IReadOnlyList<string> ReferenceDesignators, string? Notes, EffectivityRange? Effectivity, IReadOnlyList<LinkView> Links, string? Banner, Markings? Markings)
{
    public string? EffectivityText => Effectivity?.Describe();
}

public sealed record MbomLines(string Number, string Revision, int Iteration, bool IsWorkingCopy, string? DerivedFrom, IReadOnlyList<MbomLineView> Lines, string Banner, int MaxLines);

public sealed record LinkInput(Guid EbomUsageId, string Status = LinkStatuses.Matched, string? Reason = null);

public sealed record AddMbomLineCommand(
    string ChildNumber, decimal Quantity, string? Unit = null, int? FindNumber = null, IReadOnlyList<string>? ReferenceDesignators = null,
    decimal? ScrapFactor = null, bool Phantom = false, string? SupplyType = null, string? OperationReference = null, bool MbomOnly = false,
    string? Notes = null, EffectivityInput? Effectivity = null);

/// <summary><see cref="Effectivity"/> null leaves the effectivity as it is; <see cref="ClearEffectivity"/> removes it.</summary>
public sealed record UpdateMbomLineCommand(
    decimal Quantity, string Unit, int FindNumber, IReadOnlyList<string>? ReferenceDesignators = null, decimal? ScrapFactor = null, bool Phantom = false,
    string? SupplyType = null, string? OperationReference = null, bool MbomOnly = false, string? Notes = null, EffectivityInput? Effectivity = null, bool ClearEffectivity = false);

public sealed record RemovedMbomLine(Guid Id, int FindNumber, int Remaining);

/// <summary>An EBOM line of the derivation source, for linking: which MBOM lines of the working copy already implement it.</summary>
public sealed record EbomLineChoice(Guid UsageId, int FindNumber, int Alt, bool Redacted, string? ChildNumber, string? ChildName, decimal Quantity, string Unit, EffectivityRange? Effectivity, int LinkedBy)
{
    public string? EffectivityText => Effectivity?.Describe();
}

public sealed record EbomLineChoices(string Source, IReadOnlyList<EbomLineChoice> Lines, string Banner);

// ---------------------------------------------------------------- regeneration proposal

public static class ProposalKinds
{
    public const string AddLine = "add_line";
    public const string RemoveLine = "remove_line";
    public const string ChangeQuantity = "change_quantity";
    public const string ChangeUnit = "change_unit";
    public const string ChangeEffectivity = "change_effectivity";
    public const string ReviewAdjusted = "review_adjusted";
    public const string ReviewSubstituted = "review_substituted";
}

/// <summary>
/// One proposed change to an MBOM after its EBOM moved on. Accepting an item applies it to the new iteration; review items are information the
/// engineer resolves by hand. Items about parts the caller cannot read are never produced: the proposal is refused instead.
/// </summary>
public sealed record ProposalItem(
    string Id, string Kind, int? MbomFind, int? EbomFind, string? ChildNumber, string? ChildName, string Summary, bool Actionable);

public sealed record RegenerationProposal(
    string MbomNumber, string FromDerived, string ToEbom, bool EbomChanged, IReadOnlyList<ProposalItem> Items, int PreservedManualLines, int RelinkedLines, string Banner, string SpecText);

public sealed record RegenerationResult(string MbomNumber, bool CreatedIteration, string Label, int Applied, int Skipped, IReadOnlyList<string> Notices);

// ---------------------------------------------------------------- reconciliation

public static class FindingCodes
{
    public const string EbomLineMissing = "ebom_line_missing";
    public const string MbomLineUnexplained = "mbom_line_unexplained";
    public const string MbomOnlyLine = "mbom_only_line";
    public const string MbomOnlyWithLinks = "mbom_only_with_links";
    public const string QuantityDifference = "quantity_difference";
    public const string QuantityAdjusted = "quantity_adjusted";
    public const string UnitMismatch = "unit_mismatch";
    public const string LinkChildMismatch = "link_child_mismatch";
    public const string Substitution = "substitution";
    public const string SubstitutionSamePart = "substitution_same_part";
    public const string EffectivityGap = "effectivity_gap";
    public const string EffectivityExcess = "effectivity_excess";
    public const string EffectivityDimension = "effectivity_dimension";
    public const string RevisionDrift = "revision_drift";
    public const string NotDerived = "not_derived";
    public const string ChildMbomMissing = "child_mbom_missing";
    public const string ChildMbomUnreleased = "child_mbom_unreleased";
}

public static class FindingSeverities
{
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Info = "info";
}

public static class ReconcileStatuses
{
    public const string Reconciled = "Reconciled";
    public const string WithExceptions = "ReconciledWithExceptions";
    public const string NotReconciled = "NotReconciled";
    public const string Stale = "Stale";

    public static string Label(string status) => status switch
    {
        Reconciled => "Reconciled",
        WithExceptions => "Reconciled with exceptions",
        NotReconciled => "Not reconciled",
        Stale => "Stale",
        _ => status,
    };
}

/// <summary>One finding as shown. A finding about a part the caller may not read keeps severity, code, find numbers and quantities, nothing else (A4).</summary>
public sealed record FindingView(
    int Seq, string Code, string Severity, string Title, bool Redacted, Guid? EbomUsageId, Guid? MbomUsageId, string? ChildNumber, string? ChildName,
    int? EbomFind, int? MbomFind, decimal? Expected, decimal? Actual, string? Unit, string? Detail);

public sealed record ReconciliationRunInfo(
    Guid Id, string MbomNumber, string MbomRevision, int MbomIteration, string Status, string StatusLabel, string Spec, string EbomLabel, string? DerivedLabel,
    int EbomLines, int MbomLines, int CoveredLines, decimal CoveragePct, int Errors, int Warnings, int Infos, int RedactedFindings,
    DateTimeOffset RunAt, string RunBy, int DurationMs, string ContentSha256, bool IntegrityOk);

public sealed record ReconciliationReport(
    ReconciliationRunInfo Run, IReadOnlyList<FindingView> Findings, IReadOnlyList<BomDiffLine> EbomChangesSinceDerivation, bool FindingsTruncated, string Banner);

public sealed record ReconciliationHistory(IReadOnlyList<ReconciliationRunInfo> Runs, string Banner);

// ---------------------------------------------------------------- explosion and queries

/// <summary>One line of an exploded MBOM. Redacted lines keep find number, quantity, unit, level and path (A4).</summary>
public sealed record MbomLine(
    Guid LineId, Guid? ParentLineId, int Level, string Path, int FindNumber, decimal Quantity, string Unit, decimal ExtendedQuantity, decimal GrossQuantity,
    bool Redacted, bool Circular, bool HasChildren, bool Phantom, Guid? MasterId, string? Number, string? Name, string? PartType, string? Source,
    decimal? ScrapFactor, string? SupplyType, string? OperationReference, bool MbomOnly, IReadOnlyList<string> ReferenceDesignators, string? Notes,
    string? ChildMbomRevision, int? ChildMbomIteration, string? ChildMbomState, string? ChildMbomStateLabel, string? Banner, Markings? Markings, EffectivityRange? Effectivity)
{
    public string? EffectivityText => Effectivity?.Describe();

    /// <summary>The line in the shape of the shared BOM tree grid: the revision and state columns show the child's MBOM.</summary>
    public BomLine ToGridLine() => new(
        LineId, ParentLineId, Level, Path, FindNumber, Quantity, Unit, ExtendedQuantity, Redacted, false, Circular, HasChildren, MasterId, Number, Name, PartType,
        ChildMbomRevision, ChildMbomIteration, ChildMbomState, ChildMbomStateLabel, ReferenceDesignators, Notes, Banner, Markings, Effectivity);
}

public sealed record MbomQuery(
    string Number,
    string? Revision = null,
    int? Iteration = null,
    /// <summary>Which MBOM version a Make sub-assembly resolves to: latest released (default) or latest.</summary>
    bool LatestChildren = false,
    int? Depth = null,
    string? ParentLine = null,
    EffectivityRequest? Unit = null);

public sealed record MbomExplosion(
    string Number, string PartNumber, string PartName, string Plant, string Revision, int Iteration, int? Depth, string? ParentLine,
    IReadOnlyList<MbomLine> Lines, string Banner, int RedactedCount, string? Configuration);

public sealed record MbomExport(string FileName, string Csv, string Banner, int Lines, int RedactedCount);

/// <summary>Purchased and consumed quantities per distinct part, unit and supply type. Gross includes planned scrap (compounded through the levels).</summary>
public sealed record MbomSummaryLine(
    bool Redacted, string? Number, string? Name, string? SupplyType, string Unit, decimal NetQuantity, decimal GrossQuantity, int Occurrences, string? Path, string? Banner, Markings? Markings);

public sealed record MbomSummaryResult(string Number, string Plant, string Revision, bool LeavesOnly, IReadOnlyList<MbomSummaryLine> Lines, string Banner);

public sealed record MbomWhereUsedRow(
    bool Redacted, int FindNumber, decimal Quantity, string Unit, string? MbomNumber, string? PartNumber, string? PartName, string? Plant, string? Revision, int? Iteration,
    string? State, string? StateLabel, string? SupplyType, string? Banner, Markings? Markings);

public sealed record MbomWhereUsedResult(string PartNumber, string? Plant, IReadOnlyList<MbomWhereUsedRow> Rows, string Banner);

public sealed record MbomDiffEntry(
    BomChangeKind Kind, bool Redacted, string? Number, string? Name, int? FromFind, int? ToFind, IReadOnlyList<string> Changes, string? Banner, Markings? Markings);

public sealed record MbomComparison(string Number, string From, string To, IReadOnlyList<MbomDiffEntry> Lines, int Unchanged, string Banner);

/// <summary>One side of a side-by-side row. Redacted: the caller may not read the part (find number, quantity and unit only).</summary>
public sealed record SideLine(Guid Id, int FindNumber, bool Redacted, string? Number, string? Name, decimal Quantity, string Unit, string? Detail, EffectivityRange? Effectivity)
{
    public string? EffectivityText => Effectivity?.Describe();
}

public sealed record SideBySideRow(IReadOnlyList<SideLine> Ebom, IReadOnlyList<SideLine> Mbom, string State, string? LinkStatus, string? Reason);

public sealed record SideBySide(string Number, string EbomSource, string MbomLabel, IReadOnlyList<SideBySideRow> Rows, string Banner);

public static class MbomText
{
    public static string Qty(decimal d) => d.ToString("0.######", CultureInfo.InvariantCulture);
}
