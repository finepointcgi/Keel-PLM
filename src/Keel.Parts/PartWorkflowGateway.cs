using System.Data.Common;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Parts;

/// <summary>
/// Lets the workflow engine (Keel.Workflow, which must not reference this module) review and release parts. All methods
/// run in the engine's transaction. State changes honour the lifecycle data of 0002: each transition used here must exist
/// and be tagged <c>workflow</c>, so release stays impossible through any other path.
/// </summary>
public sealed class PartWorkflowGateway(IEnumerable<IChangeBindingGuard>? bindingGuards = null) : IWorkflowObjectGateway
{
    // 1.6: a revision created by an open change notice is released only through that notice (ADR-0019).
    private readonly IChangeBindingGuard[] _bindingGuards = bindingGuards?.ToArray() ?? [];

    private const string InWork = "InWork";
    private const string UnderReview = "UnderReview";
    private const string Released = "Released";

    public string ObjectType => PartService.ObjectType;

    private static string Select(string rowLock) =>
        $"""
        SELECT m.id, m.number, m.name, r.id, r.revision, i.id, r.latest_iteration, r.lifecycle_state, COALESCE(s.label, r.lifecycle_state),
               r.is_latest, COALESCE(c.user_name, c.user_id), {MarkingColumns.Select("m")}
        FROM plm.part_revision r
        JOIN plm.part_master m ON m.id = r.master_id
        JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
        LEFT JOIN plm.lifecycle_state s ON s.lifecycle_id = m.lifecycle_id AND s.state = r.lifecycle_state
        LEFT JOIN plm.part_checkout c ON c.revision_id = r.id
        {rowLock}
        """;

    private static WorkflowObject Read(DbDataReader r) => new(
        PartService.ObjectType, r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(4), r.GetGuid(3), r.GetGuid(5),
        $"{r.GetString(4)}.{r.GetInt32(6)}", r.GetString(7), r.GetString(8), r.GetString(7) == UnderReview, r.GetBoolean(9), r.IsDBNull(10) ? null : r.GetString(10),
        MarkingColumns.Read(r, 11));

    public async Task<WorkflowObject?> FindByKeyAsync(DbConnection conn, DbTransaction tx, string key, bool forUpdate, CancellationToken ct)
    {
        // Lock the master row: the same row every PartService mutation serializes on.
        await using var cmd = new NpgsqlCommand(
            Select("WHERE r.is_latest AND m.number = @n" + (forUpdate ? " FOR UPDATE OF m" : "")), (NpgsqlConnection)conn, (NpgsqlTransaction)tx);
        cmd.Parameters.AddWithValue("n", key);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<WorkflowObject?> FindByRevisionAsync(DbConnection conn, DbTransaction tx, Guid revisionId, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            Select("WHERE r.id = @rid" + (forUpdate ? " FOR UPDATE OF m" : "")), (NpgsqlConnection)conn, (NpgsqlTransaction)tx);
        cmd.Parameters.AddWithValue("rid", revisionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyDictionary<Guid, WorkflowObject>> FindManyByRevisionAsync(DbConnection conn, DbTransaction tx, IReadOnlyCollection<Guid> revisionIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, WorkflowObject>();
        if (revisionIds.Count == 0) return result;
        await using var cmd = new NpgsqlCommand(Select("WHERE r.id = ANY(@ids)"), (NpgsqlConnection)conn, (NpgsqlTransaction)tx);
        cmd.Parameters.AddWithValue("ids", revisionIds.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var o = Read(reader);
            result[o.RevisionId] = o;
        }
        return result;
    }

    public async Task<GatewayResult> BeginReviewAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        if (!obj.IsLatestRevision) return GatewayResult.Fail($"Revision {obj.Revision} is not the latest revision.");
        if (obj.State != InWork) return GatewayResult.Fail($"Revision {obj.Revision} is {obj.StateLabel}; only parts In Work can be submitted for review.");
        if (obj.CheckedOutBy is not null) return GatewayResult.Fail($"Checked out by {obj.CheckedOutBy}; check in or undo the check-out first.");
        foreach (var guard in _bindingGuards)
            if (await guard.BoundChangeAsync(conn, tx, ObjectType, obj.RevisionId, ct) is { } change)
                return GatewayResult.Fail($"Revision {obj.Revision} belongs to change notice {change} and is released only with it. Submit {change} for review instead.");
        return await TransitionAsync(conn, tx, obj, InWork, UnderReview, actor, now, ct);
    }

    public async Task<GatewayResult> CompleteReleaseAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Guid boundIterationId, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        if (obj.State != UnderReview) return GatewayResult.Fail($"Revision {obj.Revision} is {obj.StateLabel}, not Under Review; it cannot be released by this workflow.");
        if (obj.IterationId != boundIterationId) return GatewayResult.Fail("The part content changed after the review started; the workflow no longer covers the latest iteration.");
        return await TransitionAsync(conn, tx, obj, UnderReview, Released, actor, now, ct);
    }

    public async Task<GatewayResult> ReturnToInWorkAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        if (obj.State != UnderReview) return GatewayResult.Fail($"Revision {obj.Revision} is {obj.StateLabel}, not Under Review.");
        return await TransitionAsync(conn, tx, obj, UnderReview, InWork, actor, now, ct);
    }

    public async Task<IterationContent?> LoadIterationContentAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        // Callers establish read access to the owning object first (the workflow service evaluates the live markings).
        await using var cmd = new NpgsqlCommand(
            """
            SELECT i.id, m.number, r.revision, i.iteration, i.description, i.attributes::text
            FROM plm.part_iteration i
            JOIN plm.part_revision r ON r.id = i.revision_id
            JOIN plm.part_master m ON m.id = r.master_id
            WHERE i.id = @id
            """, (NpgsqlConnection)conn, (NpgsqlTransaction)tx);
        cmd.Parameters.AddWithValue("id", iterationId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new IterationContent(reader.GetGuid(0), PartService.ObjectType, reader.GetString(1), reader.GetString(2),
            $"{reader.GetString(2)}.{reader.GetInt32(3)}", reader.GetString(4), reader.GetString(5));
    }

    private static async Task<GatewayResult> TransitionAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, string from, string to, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn;
        var t = (NpgsqlTransaction)tx;
        string lifecycleId;
        await using (var lc = new NpgsqlCommand("SELECT lifecycle_id FROM plm.part_master WHERE id = @id", c, t))
        {
            lc.Parameters.AddWithValue("id", obj.Id);
            lifecycleId = (string)(await lc.ExecuteScalarAsync(ct))!;
        }
        var lifecycle = await Lifecycle.LoadAsync(c, t, lifecycleId, ct) ?? throw new InvalidOperationException($"Lifecycle {lifecycleId} missing.");
        var transition = lifecycle.Transitions.FirstOrDefault(x => x.From == from && x.To == to);
        if (transition is null || transition.Via != "workflow")
            return GatewayResult.Fail($"Lifecycle {lifecycleId} has no workflow transition from {from} to {to}.");

        await using var cmd = new NpgsqlCommand(
            "UPDATE plm.part_revision SET lifecycle_state = @to, state_changed_at = @at, state_changed_by = @by WHERE id = @rid AND lifecycle_state = @from", c, t);
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("at", now);
        cmd.Parameters.AddWithValue("by", actor.Username ?? actor.Id);
        cmd.Parameters.AddWithValue("rid", obj.RevisionId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1 ? GatewayResult.Ok : GatewayResult.Fail("The part changed state concurrently.");
    }
}
