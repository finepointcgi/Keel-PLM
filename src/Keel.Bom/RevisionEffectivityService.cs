using System.Data.Common;
using Keel.Core;
using Npgsql;

namespace Keel.Bom;

/// <summary>
/// Effectivity of part revisions for change management (<see cref="IRevisionEffectivity"/>, ADR-0021). The rule when a notice releases revision B
/// effective from unit N onward: B is recorded with that range; every other Released revision of the master is cut back to the units below N and
/// stays Released with a closed range, unless nothing is left below N, in which case it becomes Obsolete. Without an effectivity every other
/// Released revision is superseded completely, as before. Plain SQL over the module's own tables, in the caller's transaction.
/// </summary>
internal sealed class RevisionEffectivityService : IRevisionEffectivity
{
    private static NpgsqlCommand Cmd(DbConnection conn, DbTransaction tx, string sql) => new(sql, (NpgsqlConnection)conn, (NpgsqlTransaction)tx);

    public Task<(EffectivityRange? Range, string? Error)> ParseAsync(DbConnection conn, DbTransaction tx, Subject actor, EffectivityInput input, CancellationToken ct) =>
        EffectivityStore.ParseAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, actor, input, ct);

    private static async Task<List<EffectivityRules.ReleasedRevision>> ReleasedAsync(DbConnection conn, DbTransaction tx, Guid master, Guid? except, CancellationToken ct)
    {
        var list = new List<EffectivityRules.ReleasedRevision>();
        await using var cmd = Cmd(conn, tx,
            """
            SELECT r.id, r.revision, e.eff_kind, e.eff_context, e.eff_from, e.eff_to
            FROM plm.part_revision r LEFT JOIN cfg.revision_effectivity e ON e.revision_id = r.id
            WHERE r.master_id = @m AND r.lifecycle_state = 'Released' AND (@except::uuid IS NULL OR r.id <> @except)
            ORDER BY length(r.revision), r.revision
            """);
        cmd.Parameters.AddWithValue("m", master);
        cmd.Parameters.Add(new NpgsqlParameter("except", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)except ?? DBNull.Value });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new EffectivityRules.ReleasedRevision(r.GetGuid(0), r.GetString(1), EffectivityStore.ReadRange(r, 2)));
        return list;
    }

    public async Task<string?> ValidateForReviseAsync(DbConnection conn, DbTransaction tx, Guid masterId, EffectivityRange range, CancellationToken ct)
    {
        if (range.From is null || range.To is not null) return "A revision's effectivity must start at a unit and stay open at the top (from N onward).";
        var plan = EffectivityRules.Supersede(await ReleasedAsync(conn, tx, masterId, null, ct), range);
        return plan.Error;
    }

    public async Task SetForAffectedAsync(DbConnection conn, DbTransaction tx, Guid affectedObjectId, EffectivityRange? range, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        if (range is null)
        {
            await using var del = Cmd(conn, tx, "DELETE FROM cfg.change_effectivity WHERE affected_object_id = @id");
            del.Parameters.AddWithValue("id", affectedObjectId);
            await del.ExecuteNonQueryAsync(ct);
            return;
        }
        await using var cmd = Cmd(conn, tx,
            """
            INSERT INTO cfg.change_effectivity (affected_object_id, eff_kind, eff_context, eff_from, eff_to, created_at, created_by)
            VALUES (@id, @ek, @ec, @ef, @et, @at, @by)
            ON CONFLICT (affected_object_id) DO UPDATE SET eff_kind = EXCLUDED.eff_kind, eff_context = EXCLUDED.eff_context, eff_from = EXCLUDED.eff_from, eff_to = EXCLUDED.eff_to,
                created_at = EXCLUDED.created_at, created_by = EXCLUDED.created_by
            """);
        cmd.Parameters.AddWithValue("id", affectedObjectId);
        EffectivityStore.Bind(cmd, range);
        cmd.Parameters.AddWithValue("at", now);
        cmd.Parameters.AddWithValue("by", actor.Username ?? actor.Id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, EffectivityRange>> GetForAffectedAsync(DbConnection conn, DbTransaction tx, IReadOnlyCollection<Guid> affectedObjectIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, EffectivityRange>();
        if (affectedObjectIds.Count == 0) return result;
        await using var cmd = Cmd(conn, tx, "SELECT affected_object_id, eff_kind, eff_context, eff_from, eff_to FROM cfg.change_effectivity WHERE affected_object_id = ANY(@ids)");
        cmd.Parameters.AddWithValue("ids", affectedObjectIds.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result[r.GetGuid(0)] = EffectivityStore.ReadRange(r, 1)!;
        return result;
    }

    public async Task<SupersessionPlan> ApplyReleaseAsync(
        DbConnection conn, DbTransaction tx, Guid masterId, Guid resultingRevisionId, EffectivityRange? range, string changeNumber, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var others = await ReleasedAsync(conn, tx, masterId, resultingRevisionId, ct);
        var plan = EffectivityRules.Supersede(others, range);
        if (plan.Error is not null) return plan;
        var who = actor.Username ?? actor.Id;
        if (range is not null) await UpsertAsync(conn, tx, masterId, resultingRevisionId, range, changeNumber, who, now, ct);
        foreach (var n in plan.Narrowed) await UpsertAsync(conn, tx, masterId, n.RevisionId, n.After, changeNumber, who, now, ct);
        return plan;
    }

    private static async Task UpsertAsync(DbConnection conn, DbTransaction tx, Guid master, Guid revision, EffectivityRange range, string change, string who, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            """
            INSERT INTO cfg.revision_effectivity (revision_id, master_id, eff_kind, eff_context, eff_from, eff_to, source_change, set_at, set_by)
            VALUES (@rev, @m, @ek, @ec, @ef, @et, @change, @at, @by)
            ON CONFLICT (revision_id) DO UPDATE SET eff_kind = EXCLUDED.eff_kind, eff_context = EXCLUDED.eff_context, eff_from = EXCLUDED.eff_from, eff_to = EXCLUDED.eff_to,
                source_change = EXCLUDED.source_change, set_at = EXCLUDED.set_at, set_by = EXCLUDED.set_by
            """);
        cmd.Parameters.AddWithValue("rev", revision);
        cmd.Parameters.AddWithValue("m", master);
        EffectivityStore.Bind(cmd, range);
        cmd.Parameters.AddWithValue("change", change);
        cmd.Parameters.AddWithValue("at", now);
        cmd.Parameters.AddWithValue("by", who);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
