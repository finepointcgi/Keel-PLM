using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Parts;

/// <summary>
/// Lets change management (Keel.Change, which must not reference this module) revise, review-lock, release, obsolete and discard part
/// revisions inside a change notice's transaction (ADR-0019). Plain SQL, like <see cref="PartWorkflowGateway"/>. Every state change
/// goes through the lifecycle data of 0002/0007: the transition must exist and carry the tag the caller's path requires, so release
/// stays impossible through any other path.
/// </summary>
public sealed class PartChangeGateway(IEnumerable<IPartIterationParticipant>? participants = null) : IChangeObjectGateway
{
    private const string InWork = "InWork";
    private const string UnderReview = "UnderReview";
    private const string Released = "Released";
    private const string Obsolete = "Obsolete";

    private readonly IPartIterationParticipant[] _participants = participants?.ToArray() ?? [];

    public string ObjectType => PartService.ObjectType;

    private static string Select(string tail) =>
        $"""
        SELECT m.id, m.number, m.name, r.id, r.revision, i.id, r.latest_iteration, r.lifecycle_state, COALESCE(s.label, r.lifecycle_state),
               r.is_latest, COALESCE(s.is_locked, true), COALESCE(c.user_name, c.user_id), {MarkingColumns.Select("m")}
        FROM plm.part_revision r
        JOIN plm.part_master m ON m.id = r.master_id
        JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
        LEFT JOIN plm.lifecycle_state s ON s.lifecycle_id = m.lifecycle_id AND s.state = r.lifecycle_state
        LEFT JOIN plm.part_checkout c ON c.revision_id = r.id
        {tail}
        """;

    private static ChangeObject Read(DbDataReader r) => new(
        PartService.ObjectType, r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetString(4), r.GetGuid(5),
        $"{r.GetString(4)}.{r.GetInt32(6)}", r.GetInt32(6), r.GetString(7), r.GetString(8), r.GetBoolean(9), r.GetBoolean(10),
        r.IsDBNull(11) ? null : r.GetString(11), MarkingColumns.Read(r, 12));

    private static NpgsqlCommand Cmd(DbConnection conn, DbTransaction tx, string sql) => new(sql, (NpgsqlConnection)conn, (NpgsqlTransaction)tx);

    public async Task<ChangeObject?> FindByKeyAsync(DbConnection conn, DbTransaction tx, string key, string? revision, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, Select("WHERE m.number = @n AND ((@rev::text IS NULL AND r.is_latest) OR r.revision = @rev)" + (forUpdate ? " FOR UPDATE OF m" : "")));
        cmd.Parameters.AddWithValue("n", key);
        cmd.Parameters.AddWithValue("rev", (object?)revision ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<ChangeObject?> FindByRevisionAsync(DbConnection conn, DbTransaction tx, Guid revisionId, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, Select("WHERE r.id = @rid" + (forUpdate ? " FOR UPDATE OF m" : "")));
        cmd.Parameters.AddWithValue("rid", revisionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyDictionary<Guid, ChangeObject>> FindByRevisionsAsync(DbConnection conn, DbTransaction tx, IReadOnlyCollection<Guid> revisionIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, ChangeObject>();
        if (revisionIds.Count == 0) return result;
        await using var cmd = Cmd(conn, tx, Select("WHERE r.id = ANY(@ids)"));
        cmd.Parameters.AddWithValue("ids", revisionIds.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var o = Read(reader);
            result[o.RevisionId] = o;
        }
        return result;
    }

    // ---------------------------------------------------------------- revise

    public async Task<ReviseResult> ReviseAsync(DbConnection conn, DbTransaction tx, ChangeObject affected, string changeKey, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        if (!affected.IsLatest) return ReviseResult.Fail($"Revision {affected.Revision} of {affected.Key} is not the latest revision; only the latest revision can be revised.");
        if (affected.State != Released)
            return ReviseResult.Fail($"Revision {affected.Revision} of {affected.Key} is {affected.StateLabel}. Only a Released revision is revised in a change; unreleased revisions are edited directly.");

        var c = (NpgsqlConnection)conn;
        var t = (NpgsqlTransaction)tx;
        var lifecycle = await LifecycleOfAsync(c, t, affected.MasterId, ct);
        // The next label follows the highest revision the master has ever had: an abandoned revision B is never reused.
        var highest = 0;
        await using (var labels = Cmd(conn, tx, "SELECT revision FROM plm.part_revision WHERE master_id = @m"))
        {
            labels.Parameters.AddWithValue("m", affected.MasterId);
            await using var r = await labels.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) highest = Math.Max(highest, RevisionScheme.ToIndex(r.GetString(0)));
        }
        var label = RevisionScheme.FromIndex(highest + 1);
        var revisionId = Guid.CreateVersion7();
        var iterationId = Guid.CreateVersion7();
        var who = actor.Username ?? actor.Id;

        // The old revision stops being the latest first: the unique index allows one latest revision per master.
        await using (var cmd = Cmd(conn, tx,
            """
            UPDATE plm.part_revision SET is_latest = false WHERE id = @old;
            INSERT INTO plm.part_revision (id, master_id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by)
            VALUES (@rid, @m, @label, @state, true, 1, @at, @by, @at, @by);
            INSERT INTO plm.part_iteration (id, revision_id, iteration, description, attributes, comment, created_at, created_by)
            SELECT @iid, @rid, 1, i.description, i.attributes, @comment, @at, @by FROM plm.part_iteration i WHERE i.id = @from;
            INSERT INTO plm.part_document_link (id, part_iteration_id, link_type, document_master_id, document_revision_id, created_at, created_by)
            SELECT gen_random_uuid(), @iid, l.link_type, l.document_master_id, l.document_revision_id, l.created_at, l.created_by
            FROM plm.part_document_link l WHERE l.part_iteration_id = @from;
            """))
        {
            cmd.Parameters.AddWithValue("old", affected.RevisionId);
            cmd.Parameters.AddWithValue("rid", revisionId);
            cmd.Parameters.AddWithValue("m", affected.MasterId);
            cmd.Parameters.AddWithValue("label", label);
            cmd.Parameters.AddWithValue("state", lifecycle.Initial.State);
            cmd.Parameters.AddWithValue("at", now);
            cmd.Parameters.AddWithValue("by", who);
            cmd.Parameters.AddWithValue("iid", iterationId);
            cmd.Parameters.AddWithValue("from", affected.LatestIterationId);
            cmd.Parameters.AddWithValue("comment", $"Revised from {affected.LatestIterationLabel} for {changeKey}");
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var p in _participants) await p.OnReviseAsync(tx, affected.LatestIterationId, iterationId, ct);

        var created = await FindByRevisionAsync(conn, tx, revisionId, forUpdate: false, ct);
        return created is null ? ReviseResult.Fail("The new revision could not be read back.") : ReviseResult.Ok(created);
    }

    // ---------------------------------------------------------------- review, release, obsolete, discard

    public async Task<GatewayResult> BeginReviewResultingAsync(DbConnection conn, DbTransaction tx, ChangeObject resulting, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        if (!resulting.IsLatest) return GatewayResult.Fail($"{resulting.Key} revision {resulting.Revision} is no longer the latest revision.");
        if (resulting.State != InWork) return GatewayResult.Fail($"{resulting.Key} revision {resulting.Revision} is {resulting.StateLabel}; it must be In Work to be reviewed with the change notice.");
        if (resulting.CheckedOutBy is not null) return GatewayResult.Fail($"{resulting.Key} revision {resulting.Revision} is checked out by {resulting.CheckedOutBy}; check it in first.");
        return await TransitionAsync(conn, tx, resulting.MasterId, resulting.RevisionId, InWork, UnderReview, "workflow", actor, now, ct);
    }

    public Task<GatewayResult> ReturnResultingAsync(DbConnection conn, DbTransaction tx, ChangeObject resulting, Subject actor, DateTimeOffset now, CancellationToken ct) =>
        resulting.State != UnderReview
            ? Task.FromResult(GatewayResult.Fail($"{resulting.Key} revision {resulting.Revision} is {resulting.StateLabel}, not Under Review."))
            : TransitionAsync(conn, tx, resulting.MasterId, resulting.RevisionId, UnderReview, InWork, "workflow", actor, now, ct);

    public Task<GatewayResult> ReleaseResultingAsync(DbConnection conn, DbTransaction tx, ChangeObject resulting, Subject actor, DateTimeOffset now, CancellationToken ct) =>
        resulting.State != UnderReview
            ? Task.FromResult(GatewayResult.Fail($"{resulting.Key} revision {resulting.Revision} is {resulting.StateLabel}, not Under Review; it cannot be released with the change notice."))
            : TransitionAsync(conn, tx, resulting.MasterId, resulting.RevisionId, UnderReview, Released, "workflow", actor, now, ct);

    public Task<GatewayResult> ObsoleteAsync(DbConnection conn, DbTransaction tx, ChangeObject affected, Subject actor, DateTimeOffset now, CancellationToken ct) =>
        affected.State != Released
            ? Task.FromResult(GatewayResult.Fail($"{affected.Key} revision {affected.Revision} is {affected.StateLabel}; only a Released revision becomes Obsolete through a change."))
            : TransitionAsync(conn, tx, affected.MasterId, affected.RevisionId, Released, Obsolete, "change", actor, now, ct);

    public async Task<GatewayResult> DiscardResultingAsync(DbConnection conn, DbTransaction tx, ChangeObject resulting, Guid revisedFromRevisionId, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        if (resulting.State != InWork)
            return GatewayResult.Fail($"{resulting.Key} revision {resulting.Revision} is {resulting.StateLabel}; only a revision In Work can be discarded.");
        var c = (NpgsqlConnection)conn;
        var t = (NpgsqlTransaction)tx;
        // An open check-out belongs to the abandoned work: its private working copy (links and structure lines cascade) goes with it.
        if (resulting.CheckedOutBy is not null)
        {
            foreach (var p in _participants) await p.OnUndoCheckOutAsync(tx, resulting.RevisionId, ct);
            await using var del = Cmd(conn, tx, "DELETE FROM plm.part_checkout WHERE revision_id = @rid");
            del.Parameters.AddWithValue("rid", resulting.RevisionId);
            await del.ExecuteNonQueryAsync(ct);
        }
        var lifecycle = await LifecycleOfAsync(c, t, resulting.MasterId, ct);
        var transition = lifecycle.Transitions.FirstOrDefault(x => x.From == InWork && x.To == Obsolete);
        if (transition is null || transition.Via != "change")
            return GatewayResult.Fail($"Lifecycle {lifecycle.Id} has no change transition from {InWork} to {Obsolete}.");
        await using var cmd = Cmd(conn, tx,
            """
            UPDATE plm.part_revision SET lifecycle_state = @to, is_latest = false, state_changed_at = @at, state_changed_by = @by WHERE id = @rid AND lifecycle_state = @from;
            UPDATE plm.part_revision SET is_latest = true WHERE id = @prev;
            """);
        cmd.Parameters.AddWithValue("to", Obsolete);
        cmd.Parameters.AddWithValue("from", InWork);
        cmd.Parameters.AddWithValue("at", now);
        cmd.Parameters.AddWithValue("by", actor.Username ?? actor.Id);
        cmd.Parameters.AddWithValue("rid", resulting.RevisionId);
        cmd.Parameters.AddWithValue("prev", revisedFromRevisionId);
        return await cmd.ExecuteNonQueryAsync(ct) >= 1 ? GatewayResult.Ok : GatewayResult.Fail("The revision changed state concurrently.");
    }

    // ---------------------------------------------------------------- content fingerprint

    public async Task<string?> ContentFingerprintAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        string description, attributes;
        await using (var cmd = Cmd(conn, tx, "SELECT i.description, i.attributes::text FROM plm.part_iteration i WHERE i.id = @id"))
        {
            cmd.Parameters.AddWithValue("id", iterationId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            (description, attributes) = (r.GetString(0), r.GetString(1));
        }
        var links = new List<string>();
        await using (var cmd = Cmd(conn, tx,
            "SELECT l.link_type || '|' || l.document_master_id::text || '|' || COALESCE(l.document_revision_id::text, '') FROM plm.part_document_link l WHERE l.part_iteration_id = @id"))
        {
            cmd.Parameters.AddWithValue("id", iterationId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) links.Add(r.GetString(0));
        }
        links.Sort(StringComparer.Ordinal);
        var owned = new List<string>();
        foreach (var p in _participants)
            if (await p.DescribeContentAsync(tx, iterationId, ct) is { } text) owned.Add(text);
        owned.Sort(StringComparer.Ordinal);

        // Fixed property order and invariant formatting: the same iteration always hashes to the same value.
        var canonical = JsonSerializer.Serialize(new { v = 1, description, attributes, links, owned });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<Lifecycle> LifecycleOfAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid masterId, CancellationToken ct)
    {
        string lifecycleId;
        await using (var lc = new NpgsqlCommand("SELECT lifecycle_id FROM plm.part_master WHERE id = @id", c, t))
        {
            lc.Parameters.AddWithValue("id", masterId);
            lifecycleId = (string)(await lc.ExecuteScalarAsync(ct))!;
        }
        return await Lifecycle.LoadAsync(c, t, lifecycleId, ct) ?? throw new InvalidOperationException($"Lifecycle {lifecycleId} missing.");
    }

    private static async Task<GatewayResult> TransitionAsync(DbConnection conn, DbTransaction tx, Guid masterId, Guid revisionId, string from, string to, string via, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var lifecycle = await LifecycleOfAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, masterId, ct);
        var transition = lifecycle.Transitions.FirstOrDefault(x => x.From == from && x.To == to);
        if (transition is null || transition.Via != via)
            return GatewayResult.Fail($"Lifecycle {lifecycle.Id} has no {via} transition from {from} to {to}.");
        await using var cmd = Cmd(conn, tx,
            "UPDATE plm.part_revision SET lifecycle_state = @to, state_changed_at = @at, state_changed_by = @by WHERE id = @rid AND lifecycle_state = @from");
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("at", now);
        cmd.Parameters.AddWithValue("by", actor.Username ?? actor.Id);
        cmd.Parameters.AddWithValue("rid", revisionId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1 ? GatewayResult.Ok : GatewayResult.Fail("The part changed state concurrently.");
    }
}
