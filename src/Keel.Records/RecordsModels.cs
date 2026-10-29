using System.Collections.Immutable;
using Keel.Core;

namespace Keel.Records;

/// <summary>The kinds of object records management can retain, hold, dispose of and archive (FEAT-REC-1).</summary>
public static class RecordKinds
{
    public const string Document = "document";
    public const string Part = "part";
    public static readonly ImmutableArray<string> All = [Document, Part];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);
    public static string Label(string kind) => kind switch { Document => "Document", Part => "Part", _ => kind };
    public static string Path(string kind, string number) => (kind == Document ? "documents/" : "parts/") + Uri.EscapeDataString(number);
}

public static class TriggerEvents
{
    public const string Release = "release";
    public const string Obsolescence = "obsolescence";
    public const string ProgramClose = "program_close";
    public const string ContractEnd = "contract_end";
    public static readonly ImmutableArray<string> All = [Release, Obsolescence, ProgramClose, ContractEnd];

    public static string Label(string t) => t switch
    {
        Release => "Release",
        Obsolescence => "Obsolescence",
        ProgramClose => "Program close",
        ContractEnd => "Contract end",
        _ => t,
    };
}

public static class DispositionActions
{
    public const string Destroy = "destroy";
    public const string TransferArchive = "transfer_archive";
    public const string Review = "review";
    public static readonly ImmutableArray<string> All = [Destroy, TransferArchive, Review];

    public static string Label(string a) => a switch
    {
        Destroy => "Destroy",
        TransferArchive => "Transfer to archive",
        Review => "Review",
        _ => a,
    };
}

public static class ScheduleStatuses
{
    public const string Draft = "draft";
    public const string InReview = "in_review";
    public const string Published = "published";
    public const string Superseded = "superseded";
    public const string Discarded = "discarded";

    public static string Label(string s) => s switch
    {
        Draft => "Draft",
        InReview => "In review",
        Published => "Published",
        Superseded => "Superseded",
        Discarded => "Discarded",
        _ => s,
    };
}

public static class DispositionStatuses
{
    public const string Draft = "draft";
    public const string InReview = "in_review";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Executed = "executed";
    public const string Cancelled = "cancelled";

    public static string Label(string s) => s switch
    {
        Draft => "Draft",
        InReview => "In review",
        Approved => "Approved",
        Rejected => "Rejected",
        Executed => "Executed",
        Cancelled => "Cancelled",
        _ => s,
    };
}

public static class HoldScopeKinds
{
    public const string Objects = "objects";
    public const string Program = "program";
    public const string Query = "query";
    public static string Label(string k) => k switch { Objects => "Named objects", Program => "A program", Query => "A saved query", _ => k };
}

/// <summary>Stable reason codes for items a disposition run did not dispose of (the UI maps them to sentences).</summary>
public static class SkipCodes
{
    public const string Held = "held";
    public const string NotEligible = "not_eligible";
    public const string Referenced = "referenced";
    public const string Gone = "gone";
    public const string AlreadyDisposed = "already_disposed";
}

/// <summary>Who may do what in records management (FEAT-REC-2). The role opens the pages; what a holder may see of an object is decided by the markings, as everywhere.</summary>
public static class RecordsAccess
{
    /// <summary>Manage schedules, holds, requests and archives: the records manager and administrators.</summary>
    public static bool CanManage(Subject s) => s.Roles.Contains(Roles.RecordsManager) || s.Roles.Contains(Roles.Admin);

    /// <summary>Read records data (schedules, holds, queue, certificates, archives): managers, and auditors read-only.</summary>
    public static bool CanView(Subject s) => CanManage(s) || s.Roles.Contains(Roles.Auditor);

    /// <summary>Execute an approved disposition: only the records manager (an administrator alone cannot destroy records).</summary>
    public static bool CanExecute(Subject s) => s.Roles.Contains(Roles.RecordsManager);
}

// ---------------------------------------------------------------- schedules

public sealed record ClassInput(
    string Code, string Name, string? Description, int Priority, string[] Kinds, string[] Types, string[] Programs, string[] Jurisdictions, string[] Cui, string[] Statements,
    int RetentionMonths, string TriggerEvent, string Disposition, string? Citation, string? Notes);

public sealed record ClassView(Guid Id, ClassInput Input);

public sealed record ScheduleSummary(Guid Id, int Version, string Status, string Title, int Classes, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset? PublishedAt, string? PublishedBy);

public sealed record ScheduleView(
    Guid Id, int Version, string Status, string Title, string? Notes, int Revision, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset? PublishedAt, string? PublishedBy,
    IReadOnlyList<ClassView> Classes);

public sealed record ProgramEventView(string Program, string Event, DateOnly OccurredOn, DateTimeOffset RecordedAt, string RecordedBy);

public sealed record ClassifyReport(int ScheduleVersion, int Examined, int Classified, int Pending, int Removed);

// ---------------------------------------------------------------- holds

public sealed record HoldObjectRef(string Kind, string Number);

/// <summary>The criteria of a query hold. Every criterion that is set must match; the query is evaluated over ALL objects at placement and again at re-evaluation.</summary>
public sealed record HoldQuery(string? Kind = null, string? Program = null, string? ObjectType = null, string? NumberPrefix = null, string? Jurisdiction = null, string? CuiCategory = null, DateOnly? CreatedBefore = null, DateOnly? CreatedAfter = null);

public sealed record HoldScope(string Kind, IReadOnlyList<HoldObjectRef>? Objects = null, string? Program = null, HoldQuery? Query = null);

public sealed record PlaceHoldCommand(string Name, string Reason, HoldScope Scope);

public sealed record HoldSummary(string Number, string Name, string Status, string ScopeKind, DateTimeOffset PlacedAt, string PlacedBy, DateTimeOffset? ReleasedAt, bool InRelease);

/// <summary><see cref="ReadableMembers"/> counts only the members the caller may read: a hold never says how many hidden objects it covers.</summary>
public sealed record HoldView(
    string Number, string Name, string Reason, string Status, string ScopeKind, string ScopeText, DateTimeOffset PlacedAt, string PlacedBy, DateTimeOffset? ReleasedAt, string? ReleasedBy,
    bool InRelease, int Revision, long ReadableMembers);

public sealed record HoldMemberView(string Kind, string Number, string Name, string Banner, Markings Markings);

/// <summary>What a reader of an object's page may learn about records management: shown only to records roles who can read the object.</summary>
public sealed record ObjectRecordsStatus(bool Held, IReadOnlyList<string> HoldNumbers, bool Tombstoned, string? CertificateNumber, string? ClassCode, string? ClassName, DateOnly? EligibleOn, string? Disposition);

// ---------------------------------------------------------------- disposition

public sealed record ObjectRef(string Kind, string Number);

public sealed record EligibleQuery(string? Kind = null, string? ClassCode = null, string? Search = null, int Limit = 50, int Offset = 0);

public sealed record EligibleRow(string Kind, string Number, string Name, string ClassCode, string TriggerEvent, DateOnly TriggerDate, DateOnly EligibleOn, string Disposition, string Banner, Markings Markings);

/// <summary><see cref="HeldReadable"/> counts eligible objects the caller can read that a hold keeps out of the list; hidden objects are never counted.</summary>
public sealed record EligiblePage(IReadOnlyList<EligibleRow> Items, long Total, long HeldReadable, string PageBanner);

public sealed record CreateDispositionCommand(IReadOnlyList<ObjectRef> Items, string? Note);

public sealed record DispositionItemView(string Kind, string Number, string ClassCode, string Action, int ScheduleVersion, DateOnly EligibleOn, string? Result, string? ResultCode);

public sealed record DispositionSummary(string Number, string Status, int Items, DateTimeOffset CreatedAt, string CreatedBy, string Banner);

public sealed record DispositionView(
    string Number, string Status, string? Note, int Revision, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset? ExecutedAt, string? ExecutedBy, string Banner, Markings Markings,
    IReadOnlyList<DispositionItemView> Items, string? CertificateNumber);

public sealed record ExecutionResult(string DispositionNumber, string? CertificateNumber, int Destroyed, int Transferred, int Retained, int Skipped, string? ArchiveNumber);

public sealed record CertificateSummary(string Number, string DispositionNumber, DateTimeOffset IssuedAt, string IssuedBy, int Objects, string Banner);

public sealed record CertificateVerification(bool HashMatches, bool KeyKnown, bool SignatureValid, string KeyId)
{
    public bool Valid => HashMatches && KeyKnown && SignatureValid;
}

public sealed record ContentConfirmation(string Sha256, long SizeBytes, string State, DateTimeOffset? ConfirmedAt);

public sealed record CertificateView(
    string Number, string DispositionNumber, DateTimeOffset IssuedAt, string IssuedBy, string Body, string BodySha256, string KeyId, int Objects, string Banner, Markings Markings,
    CertificateVerification Verification, IReadOnlyList<ContentConfirmation> Content);

public sealed record ConfirmReport(string CertificateNumber, int Confirmed, int RetainedShared, int Pending);

// ---------------------------------------------------------------- archive

public sealed record CreateArchiveCommand(string Title, IReadOnlyList<ObjectRef> Items);

public sealed record ArchiveSummary(string Number, string Title, int Objects, int Files, long Size, string WormMode, DateTimeOffset CreatedAt, string CreatedBy, string Banner);

public sealed record ArchiveVerification(bool Valid, IReadOnlyList<string> Problems, int Entries, int Events, bool ManifestSigned, bool CheckpointVerified, string? Note);

public sealed record ArchivedFile(string Name, string MediaType, long Size, string Sha256);
public sealed record ArchivedIteration(int Iteration, string Description, DateTimeOffset CreatedAt, string CreatedBy, IReadOnlyList<ArchivedFile> Files);
public sealed record ArchivedRevision(string Revision, string State, DateTimeOffset StateChangedAt, IReadOnlyList<ArchivedIteration> Iterations);
public sealed record ArchivedObject(string Kind, string Number, string Name, string? Type, string Banner, IReadOnlyList<ArchivedRevision> Revisions);
public sealed record ArchivedSignature(string ObjectKind, string ObjectKey, string Revision, string Signer, string Meaning, string Decision, DateTimeOffset SignedAt, string ContentHash, string SignatureHash);
public sealed record ArchivedEvent(long Seq, DateTimeOffset OccurredAt, string Action, string Outcome);

public sealed record ArchiveView(
    string Number, string Title, DateTimeOffset CreatedAt, string CreatedBy, string PackageSha256, long PackageSize, string ManifestSha256, string KeyId, string WormMode, DateTimeOffset? RetainUntil,
    string Banner, Markings Markings, IReadOnlyList<(string Kind, string Number, int Revisions)> Objects);

/// <summary>The read-only view of an archive package: what it contains, with its verification. No operation here can change the package.</summary>
public sealed record ArchiveContents(
    ArchiveView Archive, ArchiveVerification Verification, IReadOnlyList<ArchivedObject> Objects, IReadOnlyList<ArchivedSignature> Signatures, IReadOnlyList<ArchivedEvent> Events);

public sealed class RecordsOptions
{
    public const string Section = "Keel:Records";

    /// <summary>S3 Object Lock for archive packages: Off (application-level immutability only), IfAvailable (apply when the vault bucket supports it) or Required (refuse to archive without it).</summary>
    public string ObjectLock { get; set; } = "IfAvailable";

    /// <summary><c>governance</c> (privileged users can shorten it) or <c>compliance</c> (nobody can, until it expires).</summary>
    public string ObjectLockMode { get; set; } = "governance";

    public int ObjectLockDays { get; set; } = 3650;

    /// <summary>Largest package the application builds (it is staged on disk and uploaded in one object).</summary>
    public long MaxArchiveBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    public int MaxItemsPerRequest { get; set; } = 500;

    /// <summary>Interval of the optional background job that re-evaluates holds and refreshes the classification cache. 0 (the default) turns it off.</summary>
    public int BackgroundMinutes { get; set; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (ObjectLock is not ("Off" or "IfAvailable" or "Required")) errors.Add("Keel:Records:ObjectLock must be Off, IfAvailable or Required.");
        if (ObjectLockMode is not ("governance" or "compliance")) errors.Add("Keel:Records:ObjectLockMode must be governance or compliance.");
        if (ObjectLockDays is < 1 or > 36500) errors.Add("Keel:Records:ObjectLockDays must be between 1 and 36500.");
        if (MaxArchiveBytes < 1024) errors.Add("Keel:Records:MaxArchiveBytes must be at least 1024.");
        if (BackgroundMinutes is < 0 or > 1440) errors.Add("Keel:Records:BackgroundMinutes must be between 0 and 1440.");
        if (MaxItemsPerRequest is < 1 or > 5000) errors.Add("Keel:Records:MaxItemsPerRequest must be between 1 and 5000.");
        return errors;
    }
}
