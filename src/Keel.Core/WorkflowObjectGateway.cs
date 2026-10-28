using System.Data.Common;

namespace Keel.Core;

/// <summary>
/// What the workflow engine needs to know about a reviewable object, whatever its type. The engine never touches
/// part (or document) tables itself; it talks to the owning module through <see cref="IWorkflowObjectGateway"/>
/// inside its own transaction, so the state change, the workflow advance and the audit event commit atomically.
/// </summary>
public sealed record WorkflowObject(
    string ObjectType,
    Guid Id,
    string Key,
    string Name,
    string Revision,
    Guid RevisionId,
    Guid IterationId,
    string IterationLabel,
    string State,
    string StateLabel,
    /// <summary>True while the object sits in its under-review state (locked for the workflow).</summary>
    bool InReview,
    bool IsLatestRevision,
    string? CheckedOutBy,
    Markings Markings);

/// <summary>The content of one iteration that an e-signature covers (increment 1.5: description and attributes).</summary>
public sealed record IterationContent(
    Guid IterationId,
    string ObjectType,
    string ObjectKey,
    string Revision,
    string IterationLabel,
    string Description,
    string AttributesJson);

/// <summary>Result of a gateway state change. <see cref="Error"/> is user-facing when <see cref="IsOk"/> is false.</summary>
public sealed record GatewayResult(bool IsOk, string? Error)
{
    public static readonly GatewayResult Ok = new(true, null);
    public static GatewayResult Fail(string error) => new(false, error);
}

/// <summary>
/// Implemented once per reviewable object type by the module that owns it (<c>Keel.Parts</c> for parts). Every call runs
/// in the caller's transaction. <c>forUpdate</c> locks the object row so the workflow and concurrent edits serialize.
/// </summary>
public interface IWorkflowObjectGateway
{
    string ObjectType { get; }

    /// <summary>Latest revision of the object with the given business key (e.g. part number), or null.</summary>
    Task<WorkflowObject?> FindByKeyAsync(DbConnection conn, DbTransaction tx, string key, bool forUpdate, CancellationToken ct);

    /// <summary>A specific revision (the one a workflow is bound to), or null.</summary>
    Task<WorkflowObject?> FindByRevisionAsync(DbConnection conn, DbTransaction tx, Guid revisionId, bool forUpdate, CancellationToken ct);

    /// <summary>Batch lookup for list pages; no locking.</summary>
    Task<IReadOnlyDictionary<Guid, WorkflowObject>> FindManyByRevisionAsync(DbConnection conn, DbTransaction tx, IReadOnlyCollection<Guid> revisionIds, CancellationToken ct);

    /// <summary>In Work, not checked out → Under Review (locks the object).</summary>
    Task<GatewayResult> BeginReviewAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct);

    /// <summary>Under Review → Released. Must verify the bound iteration is still the revision's latest.</summary>
    Task<GatewayResult> CompleteReleaseAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Guid boundIterationId, Subject actor, DateTimeOffset now, CancellationToken ct);

    /// <summary>Under Review → In Work (rejection or cancellation).</summary>
    Task<GatewayResult> ReturnToInWorkAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Under Review → returned because a reviewer rejected it (increment 1.6). Defaults to <see cref="ReturnToInWorkAsync"/>; objects with a
    /// distinct rejected state (an ECR) override it.
    /// </summary>
    Task<GatewayResult> CompleteRejectionAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct) =>
        ReturnToInWorkAsync(conn, tx, obj, actor, now, ct);

    /// <summary>The stored content of an iteration, for hashing and tamper verification.</summary>
    Task<IterationContent?> LoadIterationContentAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct);
}
