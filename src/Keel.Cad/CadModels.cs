using Keel.Cad.Structure;
using Keel.Core;

namespace Keel.Cad;

/// <summary>The CAD role of a document in a package (docs/integration/cad-connector.md).</summary>
public static class CadRoles
{
    public const string Model = "model";
    public const string Drawing = "drawing";
    public const string DerivedPdf = "derived-pdf";
    public const string Step = "step";
    public static readonly IReadOnlyList<string> All = [Model, Drawing, DerivedPdf, Step];
    /// <summary>Roles that are made from a model: they go stale when the model changes.</summary>
    public static bool IsDerived(string role) => role is Drawing or DerivedPdf or Step;
}

public static class AssociationKinds
{
    public const string ModelOf = "model-of";
    public const string DrawingOf = "drawing-of";
    public static readonly IReadOnlyList<string> All = [ModelOf, DrawingOf];
}

/// <summary>Stable error codes of the connector protocol (docs/integration/cad-connector.md).</summary>
public static class CadErrors
{
    public const string Locked = "cad.locked";
    public const string PackageIncomplete = "cad.package_incomplete";
    public const string IntegrityMismatch = "cad.integrity_mismatch";
    public const string SetClosed = "cad.set_closed";
    public const string MemberUnknown = "cad.member_unknown";
    public const string ReferenceUnknown = "cad.reference_unknown";
    public const string Proposal = "cad.proposal";
}

// ---------------------------------------------------------------- packages

public sealed record PackageOptions(IReadOnlyList<string>? Roles = null, bool IncludeDependents = true, bool AllowPartial = false);

public sealed record PackageMember(
    string Number, string Name, string DocumentType, string CadRole, string Revision, int Iteration, string State, string Banner,
    IReadOnlyList<CadFileInfo> Files, IReadOnlyList<string> References, bool IsTop, bool IsNew);

/// <summary>The manifest a connector works from: what is checked out, with the stored hash of every file.</summary>
public sealed record PackageCheckout(Guid Id, string Top, string Banner, DateTimeOffset CreatedAt, bool Open, IReadOnlyList<PackageMember> Members, int RedactedCount);

public sealed record AddMemberRequest(string Name, string DocumentType, string CadRole, string? Description = null, IReadOnlyList<string>? References = null);

public sealed record CheckInMemberRequest(string Number, string? Comment = null, IReadOnlyList<string>? References = null, IReadOnlyList<ExpectedFile>? Files = null);

public sealed record ExpectedFile(string FileName, string Sha256);

public sealed record PackageCheckInRequest(string? Comment = null, IReadOnlyList<CheckInMemberRequest>? Members = null, bool SkipUnchanged = false);

public sealed record CheckInResult(Guid Id, IReadOnlyList<CadCheckedIn> Created, IReadOnlyList<string> Released, bool SetClosed);

// ---------------------------------------------------------------- dependency graph

/// <summary>
/// One node of the dependency view. <see cref="Redacted"/> nodes carry only the edge facts (A4); everything identifying is null.
/// <see cref="Stale"/>: the referenced document has a later iteration than the one the referencing iteration was built against.
/// </summary>
public sealed record DependencyNode(
    string? Number, string? Name, string? CadRole, string? Revision, int? Iteration, string? State, string? Banner, Markings? Markings,
    bool Redacted, string? SeenRevision, int? SeenIteration, bool Stale, bool Cycle, IReadOnlyList<DependencyNode> Children);

public sealed record AssociationView(Guid Id, string Kind, string? PartNumber, string? PartName, string? DocumentNumber, string? DocumentName, string? Banner, bool Redacted, bool Stale);

public sealed record CadPackageView(
    string Number, string Name, string? CadRole, bool Registered, string Revision, int Iteration,
    IReadOnlyList<DependencyNode> Uses, IReadOnlyList<DependencyNode> UsedBy, IReadOnlyList<AssociationView> Parts,
    int RedactedCount, int StaleCount, string Banner);

public sealed record StaleItem(string Number, string Name, string CadRole, string Banner, string? ModelNumber, string? ModelName, bool ModelRedacted,
    string SeenLabel, string CurrentLabel);

public sealed record StaleList(IReadOnlyList<StaleItem> Items, string Banner);

public sealed record RegisterRoleRequest(string Role, string? CadSystem = null);

// ---------------------------------------------------------------- BOM proposals

public sealed record ProposalPartDecision(string Ref, string Decision, string? MatchNumber = null);

public sealed record CreateProposalRequest(string Title, string Format, Markings Markings, string? RootNumber = null, bool ApplyRemovals = false);

public sealed record ProposalSummary(Guid Id, string Number, string Title, string Status, string SourceFormat, string? SourceSystem, int Parts, int Lines,
    DateTimeOffset CreatedAt, string CreatedBy, string Banner, Markings Markings);

public sealed record ProposalList(IReadOnlyList<ProposalSummary> Items, long Total, string PageBanner);

public sealed record ProposalPartView(
    string Ref, string? CadNumber, string Name, string PartType, string Unit, string Source, string MatchKind, string Decision,
    string? MatchedNumber, string? MatchedName, IReadOnlyList<string> CandidateNumbers, string? CreatedNumber, bool IsParent)
{
    /// <summary>A Keel part was matched or chosen but the caller may not read it: nothing about it is shown.</summary>
    public bool MatchHidden { get; init; }
    /// <summary>The part's own box in the file's length unit, when the structure file allowed one to be derived (ADR-0052).</summary>
    public BoundingBox? Box { get; init; }
}

public sealed record ProposalLineView(int Seq, string ParentRef, string ChildRef, int? FindNumber, decimal Quantity, string? Unit, IReadOnlyList<string> ReferenceDesignators, string? Notes);

public enum DiffKind { Added, Removed, Changed, Unchanged }

public sealed record DiffSide(decimal Quantity, string Unit, int? FindNumber, IReadOnlyList<string> ReferenceDesignators);

/// <summary>One row of the reviewable difference between the proposal and the current EBOM of a parent.</summary>
public sealed record DiffRow(
    string ParentRef, string ParentLabel, string ChildLabel, string? ChildNumber, DiffKind Kind, DiffSide? Before, DiffSide? After, bool Applies, string? Note);

public sealed record DiffParent(string ParentRef, string Label, string State, string? Blocker, int Added, int Removed, int Changed, int Unchanged, int RedactedCurrentLines);

public sealed record ProposalDetail(
    Guid Id, string Number, string Title, string Status, string SourceFormat, string? SourceSystem, string RootRef, bool ApplyRemovals,
    string Banner, Markings Markings, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset? DecidedAt, string? DecidedBy,
    IReadOnlyList<ProposalPartView> Parts, IReadOnlyList<ProposalLineView> Lines, IReadOnlyList<DiffParent> Parents, IReadOnlyList<DiffRow> Diff,
    bool DiffTruncated, int Undecided, bool CanApply, bool CanDecide, IReadOnlyList<string> ApplyBlockers, ApplyReport? LastResult)
{
    /// <summary>Usage occurrences in the structure file and how many of them have a placement (ADR-0052); both 0 for formats that carry none.</summary>
    public int Occurrences { get; init; }
    public int PlacedOccurrences { get; init; }
    public string? LengthUnit { get; init; }
}

public sealed record ApplyNodeResult(string Parent, bool Ok, string? Iteration, string? Error, int Edits);

public sealed record ApplyReport(IReadOnlyList<string> CreatedParts, IReadOnlyList<ApplyNodeResult> Parents, bool Complete);

// ---------------------------------------------------------------- placements (ADR-0052)

/// <summary>The placements stored with a proposal's structure: parts with their own boxes, every occurrence with its child-to-parent matrix, and the expanded instances.</summary>
public sealed record ProposalPlacements(
    string Number, string RootRef, string? LengthUnit, double? LengthUnitMm, string Banner, Markings Markings,
    IReadOnlyList<PlacementPart> Parts, IReadOnlyList<PlacementOccurrence> Occurrences, IReadOnlyList<PlacedInstance> Instances,
    int Unplaced, bool Truncated, BoundingBox? WorldBox);

public sealed record PlacementPart(string Ref, string? CadNumber, string Name, bool IsAssembly, BoundingBox? Box);

/// <summary><see cref="Transform"/>: 16 numbers, row-major, child to parent, column vectors, in the file's length unit; null when the file has no placement for the occurrence.</summary>
public sealed record PlacementOccurrence(string ParentRef, string ChildRef, string OccurrenceId, string? Name, IReadOnlyList<double>? Transform);

/// <summary>The placed structure of the STEP file of a document, derived by the server from the verified stored bytes on request (ADR-0052). Banner and markings are the document's.</summary>
public sealed record DocumentPlacements(
    string DocumentNumber, string FileName, string IterationLabel, string Format, string? SourceSystem, string Banner, Markings Markings, string RootRef, string? LengthUnit, double? LengthUnitMm,
    IReadOnlyList<PlacementPart> Parts, IReadOnlyList<PlacementOccurrence> Occurrences, IReadOnlyList<PlacedInstance> Instances, int Unplaced, bool Truncated, BoundingBox? WorldBox)
{
    public static DocumentPlacements From(AssemblyViewResult r)
    {
        var file = r.File!;
        var model = r.Model!;
        var layout = r.Layout!;
        var parents = model.Lines.Select(l => l.ParentRef).ToHashSet(StringComparer.Ordinal);
        return new DocumentPlacements(file.DocumentNumber, file.FileName, file.IterationLabel, r.Format!, model.SourceSystem, file.Banner, file.Markings, model.RootRef, model.LengthUnit, model.LengthUnitMm,
            [.. model.Parts.Select(p => new PlacementPart(p.Ref, p.Number, p.Name, parents.Contains(p.Ref), p.Box))],
            [.. model.Occurrences.Select(o => new PlacementOccurrence(o.ParentRef, o.ChildRef, o.OccurrenceId, o.Name, o.Transform))],
            layout.Instances, layout.Unplaced, layout.Truncated, layout.WorldBox);
    }
}
