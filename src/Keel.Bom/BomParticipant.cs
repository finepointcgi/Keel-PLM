using System.Data.Common;
using Keel.Core;
using Npgsql;

namespace Keel.Bom;

/// <summary>
/// Hooks BOM working copies into the part check-out cycle (ADR-0003, ADR-0017). Check-out copies the latest iteration's usage
/// rows into the private working copy; check-in freezes the working rows into the new iteration after re-validating the
/// structure under a global structure lock; undo needs nothing because the working rows cascade with the check-out row.
/// </summary>
internal sealed class BomParticipant : IPartIterationParticipant
{
    public async Task OnCheckOutAsync(DbTransaction tx, Guid revisionId, Guid latestIterationId, CancellationToken ct)
    {
        var (conn, t) = Unwrap(tx);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO plm.bom_working_usage (id, checkout_revision_id, child_master_id, quantity, unit, find_number, reference_designators, notes, structure_type, created_at, created_by,
                                               eff_kind, eff_context, eff_from, eff_to, opt_cond, opt_ast)
            SELECT gen_random_uuid(), @rid, child_master_id, quantity, unit, find_number, reference_designators, notes, structure_type, created_at, created_by,
                   eff_kind, eff_context, eff_from, eff_to, opt_cond, opt_ast
            FROM plm.bom_usage WHERE parent_iteration_id = @iter
            """, conn, t);
        cmd.Parameters.AddWithValue("rid", revisionId);
        cmd.Parameters.AddWithValue("iter", latestIterationId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task OnCheckInAsync(DbTransaction tx, Guid revisionId, Guid newIterationId, CancellationToken ct)
    {
        var (conn, t) = Unwrap(tx);
        var children = new List<(Guid Child, int Find)>();
        await using (var read = new NpgsqlCommand("SELECT child_master_id, find_number FROM plm.bom_working_usage WHERE checkout_revision_id = @rid", conn, t))
        {
            read.Parameters.AddWithValue("rid", revisionId);
            await using var r = await read.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) children.Add((r.GetGuid(0), r.GetInt32(1)));
        }
        if (children.Count == 0) return;
        if (children.Count > BomLimits.MaxChildrenPerParent)
            throw new IterationVetoException($"A parent can have at most {BomLimits.MaxChildrenPerParent} direct children.");

        // Cycle checks at edit time see only committed structure; re-check under a lock so two check-ins cannot each close the loop.
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@k)", conn, t))
        {
            lockCmd.Parameters.AddWithValue("k", BomStructure.StructureLockKey);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }
        Guid master;
        await using (var m = new NpgsqlCommand("SELECT master_id FROM plm.part_revision WHERE id = @rid", conn, t))
        {
            m.Parameters.AddWithValue("rid", revisionId);
            master = (Guid)(await m.ExecuteScalarAsync(ct))!;
        }
        var cycle = await BomStructure.FindCycleAsync(conn, t, master, [.. children.Select(c => c.Child).Distinct()], ct);
        if (cycle is { } hit)
        {
            // Names are omitted on purpose: the message may reach a user who cannot read every part on the loop.
            var find = children.First(c => c.Child == hit.Child).Find;
            throw new IterationVetoException(
                $"Check-in refused: the line with find number {find} would now create a circular structure because another assembly changed since it was added. Remove that line and check in again.");
        }

        await using var ins = new NpgsqlCommand(
            """
            INSERT INTO plm.bom_usage (id, parent_iteration_id, child_master_id, quantity, unit, find_number, reference_designators, notes, structure_type, created_at, created_by,
                                       eff_kind, eff_context, eff_from, eff_to, opt_cond, opt_ast)
            SELECT gen_random_uuid(), @iter, child_master_id, quantity, unit, find_number, reference_designators, notes, structure_type, created_at, created_by,
                   eff_kind, eff_context, eff_from, eff_to, opt_cond, opt_ast
            FROM plm.bom_working_usage WHERE checkout_revision_id = @rid
            ORDER BY find_number, eff_from NULLS FIRST, created_at, id
            """, conn, t);
        ins.Parameters.AddWithValue("iter", newIterationId);
        ins.Parameters.AddWithValue("rid", revisionId);
        await ins.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// A change notice created the next revision: its first iteration starts with the structure of the revision it replaces (ADR-0019).
    /// Same children, so no cycle can appear; the structure guards still apply (the new revision is unlocked).
    /// </summary>
    public async Task OnReviseAsync(DbTransaction tx, Guid fromIterationId, Guid newIterationId, CancellationToken ct)
    {
        var (conn, t) = Unwrap(tx);
        await using var ins = new NpgsqlCommand(
            """
            INSERT INTO plm.bom_usage (id, parent_iteration_id, child_master_id, quantity, unit, find_number, reference_designators, notes, structure_type, created_at, created_by,
                                       eff_kind, eff_context, eff_from, eff_to, opt_cond, opt_ast)
            SELECT gen_random_uuid(), @to, child_master_id, quantity, unit, find_number, reference_designators, notes, structure_type, created_at, created_by,
                   eff_kind, eff_context, eff_from, eff_to, opt_cond, opt_ast
            FROM plm.bom_usage WHERE parent_iteration_id = @from
            ORDER BY find_number, alt
            """, conn, t);
        ins.Parameters.AddWithValue("to", newIterationId);
        ins.Parameters.AddWithValue("from", fromIterationId);
        await ins.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The usage lines of an iteration as stable text, for the content fingerprint a change notice's signatures cover.</summary>
    public async Task<string?> DescribeContentAsync(DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        var (conn, t) = Unwrap(tx);
        var lines = new List<string>();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT concat_ws('|', find_number::text, child_master_id::text, quantity::text, unit, array_to_string(reference_designators, ','), COALESCE(notes, ''))
                   || CASE WHEN eff_kind IS NULL THEN '' ELSE '|eff:' || eff_kind || ':' || COALESCE(eff_context, '') || ':' || COALESCE(eff_from::text, '') || ':' || COALESCE(eff_to::text, '') END
                   || CASE WHEN opt_cond IS NULL THEN '' ELSE '|opt:' || opt_cond END
            FROM plm.bom_usage WHERE parent_iteration_id = @it ORDER BY find_number, alt
            """, conn, t);
        cmd.Parameters.AddWithValue("it", iterationId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) lines.Add(r.GetString(0));
        return lines.Count == 0 ? null : "bom:" + string.Join(';', lines);
    }

    private static (NpgsqlConnection, NpgsqlTransaction) Unwrap(DbTransaction tx) =>
        tx is NpgsqlTransaction t && t.Connection is { } c
            ? (c, t)
            : throw new InvalidOperationException("The BOM module requires an Npgsql transaction.");
}
