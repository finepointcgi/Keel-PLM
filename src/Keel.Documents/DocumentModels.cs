using Keel.Core;

namespace Keel.Documents;

public static class DocumentTypes
{
    public static readonly IReadOnlyList<string> All = ["Specification", "Drawing", "CAD", "Report", "Procedure", "Other"];
}

public static class FileRoles
{
    public const string Primary = "primary";
    public const string Attachment = "attachment";
    public static readonly IReadOnlyList<string> All = [Primary, Attachment];
}

public static class LinkTypes
{
    /// <summary>The document describes the part (e.g. its drawing). Pins a document revision.</summary>
    public const string Describes = "describes";
    /// <summary>The part conforms to the document (e.g. a specification). Resolves at read time (A23).</summary>
    public const string References = "references";
    public static readonly IReadOnlyList<string> All = [Describes, References];
}

/// <summary>A file to store: its bytes are streamed from <see cref="Content"/> into the vault.</summary>
public sealed record FileUpload(string FileName, string? MediaType, Stream Content);

public sealed record CreateDocumentCommand(
    string Name,
    string DocumentType,
    Markings Markings,
    string? Description = null,
    // ASSUMPTION(A6): explicit numbers are for migration and restricted to administrators.
    string? Number = null);

public sealed record DocumentSummary(
    Guid Id, string Number, string Name, string DocumentType, string Revision, int Iteration, string State, string Banner, Markings Markings);

public sealed record DocumentSearchResult(IReadOnlyList<DocumentSummary> Items, long Total, string PageBanner);

public sealed record DocumentFileView(Guid Id, string Role, string FileName, string MediaType, long SizeBytes, string Sha256, DateTimeOffset AddedAt, string AddedBy);

public sealed record DocumentRevisionView(Guid Id, string Revision, string State, string StateLabel, bool IsLatest, bool IsLocked,
    int LatestIteration, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset StateChangedAt, string StateChangedBy);

public sealed record DocumentIterationView(Guid Id, string Revision, int Iteration, string Label, string Description, string? Comment,
    DateTimeOffset CreatedAt, string CreatedBy, IReadOnlyList<DocumentFileView> Files);

/// <summary>The working copy is private: description and files are present only for the owner.</summary>
public sealed record DocumentCheckoutView(string UserId, string? UserName, DateTimeOffset CheckedOutAt, bool IsMine,
    string? Description, IReadOnlyList<DocumentFileView>? Files);

public sealed record DocumentTransitionView(string To, string Label, string Via, bool Allowed, string? Reason);

public sealed record DocumentPermissions(bool CanModify, bool CanAdminister, bool CanDownload, IReadOnlyList<string> ModifyDeniedReasons);

public sealed record DocumentDetail(
    Guid Id,
    string Number,
    string Name,
    string DocumentType,
    string LifecycleId,
    Markings Markings,
    string Banner,
    DocumentRevisionView Revision,
    DocumentIterationView Iteration,
    IReadOnlyList<DocumentRevisionView> Revisions,
    IReadOnlyList<DocumentIterationView> History,
    DocumentCheckoutView? Checkout,
    IReadOnlyList<DocumentTransitionView> Transitions,
    DocumentPermissions Permissions,
    DateTimeOffset CreatedAt,
    string CreatedBy);

/// <summary>
/// A marked file ready to send (A10). <see cref="WriteToAsync"/> streams the stamped PDF or marked ZIP; the vault
/// object is verified again while it streams, and a mismatch aborts the transfer.
/// </summary>
public sealed record MarkedDownload(
    string FileName,
    string ContentType,
    string Banner,
    string Format,
    Func<Stream, CancellationToken, Task> WriteToAsync);

// ---------------------------------------------------------------- part <-> document links (ADR-0016)

/// <summary>The document side of a link the caller may read.</summary>
public sealed record LinkedDocumentView(
    string Number, string Name, string DocumentType, string Revision, int Iteration, string State, string Banner, Markings Markings);

/// <summary>
/// One link of a part iteration (or of the caller's working copy). When <see cref="Redacted"/> is true the caller may not
/// read the document: only the link id and type are disclosed (A4).
/// </summary>
public sealed record PartDocumentLinkView(
    Guid LinkId,
    string LinkType,
    bool IsWorking,
    bool Redacted,
    LinkedDocumentView? Document,
    // describes: the pinned revision. references: null (resolved at read time).
    string? PinnedRevision,
    bool NewerRevisionAvailable,
    DateTimeOffset? CreatedAt,
    string? CreatedBy);

public sealed record PartDocumentLinks(
    string PartNumber,
    string IterationLabel,
    IReadOnlyList<PartDocumentLinkView> Links,
    // Present when the caller owns the part's check-out: the links that check-in will write.
    IReadOnlyList<PartDocumentLinkView>? WorkingLinks,
    string PageBanner);

/// <summary>A part whose latest iteration links to a document. Redacted when the caller may not read the part.</summary>
public sealed record DocumentPartLinkView(
    Guid LinkId, string LinkType, bool Redacted, string? PartNumber, string? PartName, string? IterationLabel, string? Banner, Markings? Markings);

public sealed record DocumentPartLinks(string DocumentNumber, IReadOnlyList<DocumentPartLinkView> Parts, string PageBanner);
