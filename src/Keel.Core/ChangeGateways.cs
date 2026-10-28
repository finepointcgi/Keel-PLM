using System.Data.Common;

namespace Keel.Core;

/// <summary>
/// One revision of a changeable object (a part in Phase 1) as change management needs to see it: identity, state and the
/// markings the access policy is evaluated against. Document revisions join in Phase 2.
/// </summary>
public sealed record ChangeObject(
    string ObjectType,
    Guid MasterId,
    string Key,
    string Name,
    Guid RevisionId,
    string Revision,
    Guid LatestIterationId,
    string LatestIterationLabel,
    int LatestIteration,
    string State,
    string StateLabel,
    bool IsLatest,
    bool IsLocked,
    string? CheckedOutBy,
    Markings Markings);

/// <summary>Result of creating the next revision of an object for a change: the new revision, or a user-facing error.</summary>
public sealed record ReviseResult(bool IsOk, string? Error, ChangeObject? Resulting)
{
    public static ReviseResult Ok(ChangeObject resulting) => new(true, null, resulting);
    public static ReviseResult Fail(string error) => new(false, error, null);
}

/// <summary>
/// What <c>Keel.Change</c> needs from the module that owns a changeable object type (<c>Keel.Parts</c> for parts), so Change never
/// touches part tables. Every call runs in the caller's transaction. <c>forUpdate</c> locks the object's master row, the same row
/// that every object mutation and the review workflow serialize on.
/// </summary>
public interface IChangeObjectGateway
{
    string ObjectType { get; }

    /// <summary>Latest revision of the object with this business key, or the named revision. Null when absent.</summary>
    Task<ChangeObject?> FindByKeyAsync(DbConnection conn, DbTransaction tx, string key, string? revision, bool forUpdate, CancellationToken ct);

    Task<ChangeObject?> FindByRevisionAsync(DbConnection conn, DbTransaction tx, Guid revisionId, bool forUpdate, CancellationToken ct);

    /// <summary>Batch lookup for display; no locking. Callers evaluate the access policy on the returned markings.</summary>
    Task<IReadOnlyDictionary<Guid, ChangeObject>> FindByRevisionsAsync(DbConnection conn, DbTransaction tx, IReadOnlyCollection<Guid> revisionIds, CancellationToken ct);

    /// <summary>
    /// Creates the next revision (iteration 1) from the affected revision's latest iteration, in the initial state, as the new latest
    /// revision, carrying forward the iteration-owned data (document links, BOM lines). The affected revision must be the latest and
    /// Released; it stays unchanged.
    /// </summary>
    Task<ReviseResult> ReviseAsync(DbConnection conn, DbTransaction tx, ChangeObject affected, string changeKey, Subject actor, DateTimeOffset now, CancellationToken ct);

    /// <summary>In Work, checked in → Under Review (locks the resulting revision while the change notice is reviewed).</summary>
    Task<GatewayResult> BeginReviewResultingAsync(DbConnection conn, DbTransaction tx, ChangeObject resulting, Subject actor, DateTimeOffset now, CancellationToken ct);

    /// <summary>Under Review → In Work (the change notice was rejected or its review cancelled).</summary>
    Task<GatewayResult> ReturnResultingAsync(DbConnection conn, DbTransaction tx, ChangeObject resulting, Subject actor, DateTimeOffset now, CancellationToken ct);

    /// <summary>Under Review → Released (the change notice was approved).</summary>
    Task<GatewayResult> ReleaseResultingAsync(DbConnection conn, DbTransaction tx, ChangeObject resulting, Subject actor, DateTimeOffset now, CancellationToken ct);

    /// <summary>Released → Obsolete through the lifecycle's <c>change</c> transition.</summary>
    Task<GatewayResult> ObsoleteAsync(DbConnection conn, DbTransaction tx, ChangeObject affected, Subject actor, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Abandons a resulting revision that never released: discards an open check-out, marks it Obsolete and makes the revision it was
    /// created from the latest again. Nothing is deleted.
    /// </summary>
    Task<GatewayResult> DiscardResultingAsync(DbConnection conn, DbTransaction tx, ChangeObject resulting, Guid revisedFromRevisionId, Subject actor, DateTimeOffset now, CancellationToken ct);

    /// <summary>SHA-256 (hex) over the canonical content of one iteration: description, attributes, document links and structure lines.</summary>
    Task<string?> ContentFingerprintAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct);
}

/// <summary>
/// Lets the owning module refuse actions on a revision that an open change notice is working on, without referencing
/// <c>Keel.Change</c>. Implemented by Keel.Change; consumed by the part review workflow gateway.
/// </summary>
public interface IChangeBindingGuard
{
    /// <summary>The number of the open change notice that created this revision, or null when it is not bound to one.</summary>
    Task<string?> BoundChangeAsync(DbConnection conn, DbTransaction tx, string objectType, Guid revisionId, CancellationToken ct);
}

/// <summary>One parent in a where-used result. Redacted rows carry only level, find number, quantity and unit (A4).</summary>
public sealed record WhereUsedEntry(
    int Level, bool Redacted, int FindNumber, decimal Quantity, string Unit,
    Guid? ParentMasterId, string? ParentKey, string? ParentName, string? ParentRevision, string? ParentState, string? Banner, Markings? Markings);

public sealed record StructureDelta(string Kind, bool Redacted, string? Key, string? Name, string Summary);

public sealed record StructureComparison(string From, string To, IReadOnlyList<StructureDelta> Changes, int Unchanged, string Banner);

/// <summary>Structure queries the impact analysis needs, implemented by <c>Keel.Bom</c> over its where-used and compare queries.</summary>
public interface IStructureImpactProvider
{
    string ObjectType { get; }

    Task<Outcome<IReadOnlyList<WhereUsedEntry>>> WhereUsedAsync(Subject subject, string key, int levels, string? correlationId, CancellationToken ct);

    /// <summary>Compares the direct structure of two iterations given as labels such as "A.3" and "B.1".</summary>
    Task<Outcome<StructureComparison>> CompareAsync(Subject subject, string key, string fromLabel, string toLabel, string? correlationId, CancellationToken ct);
}

/// <summary>A document linked to a part iteration. Redacted when the caller may not read the document (A4).</summary>
public sealed record LinkedDocument(
    bool Redacted, string LinkType, string? Number, string? Name, string? DocumentType, string? Revision, string? State, string? Banner, Markings? Markings);

/// <summary>Documents linked to an object, implemented by <c>Keel.Documents</c> over its part-document link queries.</summary>
public interface ILinkedDocumentProvider
{
    string ObjectType { get; }

    Task<Outcome<IReadOnlyList<LinkedDocument>>> GetLinkedAsync(Subject subject, string key, string? revision, int? iteration, string? correlationId, CancellationToken ct);
}
