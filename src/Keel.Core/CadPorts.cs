using System.Data.Common;

namespace Keel.Core;

// CAD integration (ADR-0035). Keel.Cad reaches documents, parts and BOMs only through these ports, implemented by their owners
// (documents: Keel.Documents; parts and BOM structure: the host, which composes both). Every method evaluates the access policy
// itself and audits, exactly as the owning module's own commands do.

/// <summary>A file of a document iteration or working copy. <see cref="Role"/> is the vault role (primary or attachment), not the CAD role.</summary>
public sealed record CadFileInfo(string Role, string FileName, string MediaType, long SizeBytes, string Sha256);

/// <summary>What a CAD set operation needs to know about one document. <see cref="WorkingFiles"/> is filled only for the check-out owner.</summary>
public sealed record CadDocumentInfo(
    Guid MasterId, string Number, string Name, string DocumentType, Markings Markings, string Banner,
    string Revision, int Iteration, string State, bool IsLocked, string? CheckedOutBy, bool CheckedOutByMe,
    IReadOnlyList<CadFileInfo> Files, IReadOnlyList<CadFileInfo>? WorkingFiles);

public sealed record CadCheckInItem(string Number, string? Comment);

/// <summary>One document of an atomic set check-in: the iteration that now exists.</summary>
public sealed record CadCheckedIn(Guid MasterId, string Number, string Revision, int PreviousIteration, int Iteration, Guid IterationId);

/// <summary>A marked delivery (a ZIP holding the unchanged file plus the markings cover sheet). The vault object is verified while it streams.</summary>
public sealed record CadDownload(string FileName, string Banner, Func<Stream, CancellationToken, Task> WriteToAsync);

public interface ICadDocumentPort
{
    /// <summary>Audited read; 404 when the caller may not read the document. The working files are present for the check-out owner.</summary>
    Task<Outcome<CadDocumentInfo>> GetAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default);

    /// <summary>All-or-nothing check-out of every document, in one transaction. One locked, missing or unreadable document fails the whole set.
    /// <paramref name="inTransaction"/> runs in the same transaction, so a record of the set commits or rolls back with the locks.</summary>
    Task<Outcome<IReadOnlyList<CadDocumentInfo>>> CheckOutSetAsync(Subject subject, IReadOnlyList<string> numbers, string? correlationId,
        Func<DbConnection, DbTransaction, CancellationToken, Task>? inTransaction, CancellationToken ct = default);

    Task<Outcome<CadDocumentInfo>> CreateAsync(Subject subject, string name, string documentType, Markings markings, string? description, string? correlationId, CancellationToken ct = default);

    /// <summary>Adds a file to the caller's working copy. The content is streamed through the vault: SHA-256, malware scan and size limit apply as for every upload.</summary>
    Task<Outcome<CadDocumentInfo>> UploadAsync(Subject subject, string number, string role, string fileName, string? mediaType, Stream content, string? correlationId, CancellationToken ct = default);

    /// <summary>
    /// Creates one iteration of every document in one transaction (all or nothing). <paramref name="inTransaction"/> runs in that same transaction
    /// after the iterations exist, so dependent rows commit or roll back with them.
    /// </summary>
    Task<Outcome<IReadOnlyList<CadCheckedIn>>> CheckInSetAsync(Subject subject, IReadOnlyList<CadCheckInItem> items, string? correlationId,
        Func<DbConnection, DbTransaction, IReadOnlyList<CadCheckedIn>, CancellationToken, Task>? inTransaction, CancellationToken ct = default);

    /// <summary>Releases the check-out of every document (owner or administrator). Documents that are not checked out are skipped.</summary>
    Task<Outcome<IReadOnlyList<string>>> UndoCheckOutSetAsync(Subject subject, IReadOnlyList<string> numbers, string? correlationId, CancellationToken ct = default);

    /// <summary>The marked download of a file of the latest iteration, always as the unchanged-file ZIP (a PDF is never stamped, so a connector gets the stored bytes back).</summary>
    Task<Outcome<CadDownload>> DownloadLatestAsync(Subject subject, string number, string sha256, string? correlationId, CancellationToken ct = default);
}

public sealed record CadPartInfo(
    Guid MasterId, string Number, string Name, string PartType, string Unit, string Source, string Revision, int Iteration, string State,
    bool IsLocked, string Banner, Markings Markings);

public sealed record CadPartCreate(string Name, string PartType, string Unit, string Source, Markings Markings, string? Description, IReadOnlyDictionary<string, string>? Attributes);

public sealed record CadBomLine(string ChildNumber, string? ChildName, decimal Quantity, string Unit, int FindNumber, IReadOnlyList<string> ReferenceDesignators, string? Notes);

/// <summary>The direct children of the latest iteration of a part. Children the caller may not read are counted, never named (A4).</summary>
public sealed record CadBomSnapshot(string Number, string Revision, int Iteration, bool IsLocked, string? CheckedOutBy, IReadOnlyList<CadBomLine> Lines, int RedactedCount);

public enum CadBomEditKind { Add, Update, Remove }

/// <summary>One change to the direct children of a part. Remove and Update address the existing line by <see cref="ChildNumber"/> and, when the child is used on several lines, <see cref="CurrentFindNumber"/>.</summary>
public sealed record CadBomEdit(CadBomEditKind Kind, string ChildNumber, decimal Quantity, string Unit, int? FindNumber, IReadOnlyList<string> ReferenceDesignators, string? Notes,
    int? CurrentFindNumber = null);

public interface ICadPartPort
{
    /// <summary>The readable parts among <paramref name="numbers"/> (one query with the read filter; unreadable and missing numbers are simply absent, and no per-part audit event is written).</summary>
    Task<IReadOnlyList<CadPartInfo>> FindManyAsync(Subject subject, IReadOnlyList<string> numbers, CancellationToken ct = default);

    /// <summary>Like <see cref="FindManyAsync"/> by id: the readable parts among <paramref name="ids"/>.</summary>
    Task<IReadOnlyList<CadPartInfo>> FindByIdsAsync(Subject subject, IReadOnlyList<Guid> ids, CancellationToken ct = default);

    /// <summary>Parts whose latest iteration has the attribute value, readable by the caller only (the read filter is part of the query).</summary>
    Task<IReadOnlyList<CadPartInfo>> FindByAttributeAsync(Subject subject, string attribute, string value, int limit, CancellationToken ct = default);

    Task<Outcome<CadPartInfo>> CreateAsync(Subject subject, CadPartCreate part, string? correlationId, CancellationToken ct = default);

    Task<Outcome<CadBomSnapshot>> GetBomAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default);

    /// <summary>
    /// Applies the edits through the part check-out, the BOM working-copy rules and a check-in (a new iteration), so iteration rules, release
    /// locks and audit are the normal ones. A locked or foreign-checked-out part, or any rejected edit, changes nothing. Returns the new iteration label.
    /// </summary>
    Task<Outcome<string>> ApplyBomAsync(Subject subject, string parentNumber, IReadOnlyList<CadBomEdit> edits, string comment, string? correlationId, CancellationToken ct = default);
}
