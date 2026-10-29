using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Documents;

/// <summary>
/// Lets the workflow engine (Keel.Workflow, which must not reference this module) review and release documents exactly like parts (ADR-0033, increment 7.6).
/// Every method runs in the engine's transaction. State changes honour the lifecycle data (migration 0017 made In Work to Under Review and back
/// <c>workflow</c> transitions, like Under Review to Released): each transition used here must exist and be tagged <c>workflow</c>, so release stays impossible
/// through any other path. The content an e-signature covers is the description, the document type and the file list of the bound iteration with each file's
/// SHA-256, so changing a file after signing makes verification fail (GAP-C-2).
/// </summary>
public sealed class DocumentWorkflowGateway(IDomainEventSink? events = null, INotificationSink? notifications = null) : IWorkflowObjectGateway
{
    private const string InWork = "InWork";
    private const string UnderReview = "UnderReview";
    private const string Released = "Released";

    public string ObjectType => DocumentService.ObjectType;

    private static string Select(string rowLock) =>
        $"""
        SELECT m.id, m.number, m.name, r.id, r.revision, i.id, r.latest_iteration, r.lifecycle_state, COALESCE(s.label, r.lifecycle_state),
               r.is_latest, COALESCE(c.user_name, c.user_id), {MarkingColumns.Select("m")}
        FROM plm.document_revision r
        JOIN plm.document_master m ON m.id = r.master_id
        JOIN plm.document_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
        LEFT JOIN plm.lifecycle_state s ON s.lifecycle_id = m.lifecycle_id AND s.state = r.lifecycle_state
        LEFT JOIN plm.document_checkout c ON c.revision_id = r.id
        {rowLock}
        """;

    private static WorkflowObject Read(DbDataReader r) => new(
        DocumentService.ObjectType, r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(4), r.GetGuid(3), r.GetGuid(5),
        $"{r.GetString(4)}.{r.GetInt32(6).ToString(CultureInfo.InvariantCulture)}", r.GetString(7), r.GetString(8), r.GetString(7) == UnderReview, r.GetBoolean(9),
        r.IsDBNull(10) ? null : r.GetString(10), MarkingColumns.Read(r, 11));

    public async Task<WorkflowObject?> FindByKeyAsync(DbConnection conn, DbTransaction tx, string key, bool forUpdate, CancellationToken ct)
    {
        // Lock the master row: the same row every DocumentService mutation serializes on.
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
        if (obj.State != InWork) return GatewayResult.Fail($"Revision {obj.Revision} is {obj.StateLabel}; only documents In Work can be submitted for review.");
        if (obj.CheckedOutBy is not null) return GatewayResult.Fail($"Checked out by {obj.CheckedOutBy}; check in or undo the check-out first.");
        // ASSUMPTION(A24): a document is reviewed for its content, so submitting needs a primary file.
        if (!await HasPrimaryAsync(conn, tx, obj.IterationId, ct)) return GatewayResult.Fail("Add a primary file before submitting the document for review.");
        if (await QuarantinedFileAsync(conn, tx, obj, ct) is { } quarantined)
            return GatewayResult.Fail($"The document contains a quarantined file ({quarantined}); an administrator must resolve the quarantine before it can be reviewed.");
        var moved = await TransitionAsync(conn, tx, obj, InWork, UnderReview, actor, now, ct);
        if (moved.IsOk) await TellWatchersAsync(conn, tx, obj, actor, ct);
        return moved;
    }

    public async Task<GatewayResult> CompleteReleaseAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Guid boundIterationId, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        if (obj.State != UnderReview) return GatewayResult.Fail($"Revision {obj.Revision} is {obj.StateLabel}, not Under Review; it cannot be released by this workflow.");
        if (obj.IterationId != boundIterationId) return GatewayResult.Fail("The document content changed after the review started; the workflow no longer covers the latest iteration.");
        // A file found infected while the review ran must not be released (the scanner can quarantine content at any time).
        if (await QuarantinedFileAsync(conn, tx, obj, ct) is { } quarantined)
            return GatewayResult.Fail($"The document contains a quarantined file ({quarantined}); it cannot be released until an administrator resolves the quarantine.");
        var released = await TransitionAsync(conn, tx, obj, UnderReview, Released, actor, now, ct);
        // 4.0 (ADR-0025): the ERP event commits with the release; releases reach watchers through the outbox, as for administrator releases.
        if (released.IsOk) await DocumentEvents.EmitAsync(events, conn, tx, obj.RevisionId, EventTypes.DocumentReleased, actor, now, null, ct, DocumentEvents.ViaWorkflow);
        return released;
    }

    public async Task<GatewayResult> ReturnToInWorkAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        if (obj.State != UnderReview) return GatewayResult.Fail($"Revision {obj.Revision} is {obj.StateLabel}, not Under Review.");
        var moved = await TransitionAsync(conn, tx, obj, UnderReview, InWork, actor, now, ct);
        if (moved.IsOk) await TellWatchersAsync(conn, tx, obj, actor, ct);
        return moved;
    }

    public async Task<IterationContent?> LoadIterationContentAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        // Callers establish read access to the owning object first (the workflow service evaluates the live markings).
        string number, revision, description, documentType;
        int iteration;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT m.number, r.revision, i.iteration, i.description, m.document_type
            FROM plm.document_iteration i
            JOIN plm.document_revision r ON r.id = i.revision_id
            JOIN plm.document_master m ON m.id = r.master_id
            WHERE i.id = @id
            """, (NpgsqlConnection)conn, (NpgsqlTransaction)tx))
        {
            cmd.Parameters.AddWithValue("id", iterationId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            (number, revision, iteration, description, documentType) = (reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4));
        }
        // The file list is part of what is signed: role, name, media type, size and SHA-256 of every file, in a fixed order.
        var files = new JsonArray();
        await using (var cmd = new NpgsqlCommand(
            "SELECT role, file_name, media_type, size_bytes, sha256 FROM plm.document_file WHERE iteration_id = @i ORDER BY role DESC, lower(file_name), file_name", (NpgsqlConnection)conn, (NpgsqlTransaction)tx))
        {
            cmd.Parameters.AddWithValue("i", iterationId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                files.Add(new JsonObject
                {
                    ["role"] = reader.GetString(0), ["fileName"] = reader.GetString(1), ["mediaType"] = reader.GetString(2), ["sizeBytes"] = reader.GetInt64(3), ["sha256"] = reader.GetString(4),
                });
        }
        var attributes = new JsonObject { ["documentType"] = documentType, ["files"] = files };
        return new IterationContent(iterationId, DocumentService.ObjectType, number, revision, $"{revision}.{iteration.ToString(CultureInfo.InvariantCulture)}", description, attributes.ToJsonString());
    }

    private static async Task<bool> HasPrimaryAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM plm.document_file WHERE iteration_id = @i AND role = 'primary')", (NpgsqlConnection)conn, (NpgsqlTransaction)tx);
        cmd.Parameters.AddWithValue("i", iterationId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>The file name of a file of the bound iteration that is quarantined for this document (compliance wave, ADR-0026), or null.</summary>
    private static async Task<string?> QuarantinedFileAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT f.file_name FROM plm.document_file f
            JOIN plm.file_quarantine q ON q.sha256 = f.sha256 AND q.master_id = @m AND q.released_at IS NULL
            WHERE f.iteration_id = @i ORDER BY f.file_name LIMIT 1
            """, (NpgsqlConnection)conn, (NpgsqlTransaction)tx);
        cmd.Parameters.AddWithValue("m", obj.Id);
        cmd.Parameters.AddWithValue("i", obj.IterationId);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    /// <summary>UX-4 (ADR-0030): watchers hear that the document entered or left review, inside the engine's transaction (a rolled-back step leaves nothing).</summary>
    private async Task TellWatchersAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, CancellationToken ct)
    {
        if (notifications is null) return;
        await notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.WatchedState, AwarenessKinds.Document, obj.Key, obj.Markings, actor.Id), ct);
    }

    private static async Task<GatewayResult> TransitionAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, string from, string to, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn;
        var t = (NpgsqlTransaction)tx;
        string lifecycleId;
        await using (var lc = new NpgsqlCommand("SELECT lifecycle_id FROM plm.document_master WHERE id = @id", c, t))
        {
            lc.Parameters.AddWithValue("id", obj.Id);
            lifecycleId = (string)(await lc.ExecuteScalarAsync(ct))!;
        }
        var lifecycle = await Lifecycle.LoadAsync(c, t, lifecycleId, ct) ?? throw new InvalidOperationException($"Lifecycle {lifecycleId} missing.");
        var transition = lifecycle.Transitions.FirstOrDefault(x => x.From == from && x.To == to);
        if (transition is null || transition.Via != "workflow")
            return GatewayResult.Fail($"Lifecycle {lifecycleId} has no workflow transition from {from} to {to}.");

        await using var cmd = new NpgsqlCommand(
            "UPDATE plm.document_revision SET lifecycle_state = @to, state_changed_at = @at, state_changed_by = @by WHERE id = @rid AND lifecycle_state = @from", c, t);
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("at", now);
        cmd.Parameters.AddWithValue("by", actor.Username ?? actor.Id);
        cmd.Parameters.AddWithValue("rid", obj.RevisionId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1 ? GatewayResult.Ok : GatewayResult.Fail("The document changed state concurrently.");
    }
}
