using System.Data.Common;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

/// <summary>
/// Builds the released-EBOM snapshot of an assembly revision for the integration outbox (ADR-0025), inside the transaction that released it.
/// This is a system read without an end user: it returns every line with its markings, and the event's markings (assembly) decide what the
/// readers and sinks of the event may see; lines the assembly's markings do not cover are turned into placeholders by the event builder.
/// </summary>
internal sealed class BomPublicationSource : IBomPublicationSource
{
    private sealed record Row(Guid UsageId, int Find, int Alt, decimal Qty, string Unit, string[] Designators, string? Notes, EffectivityRange? Effectivity,
        Guid ChildId, string Number, string Name, string PartType, Markings Markings, string? Revision, int? Iteration, string? State, Guid? IterationId);

    public async Task<PublishedBom?> BuildAsync(DbConnection conn, DbTransaction tx, Guid partRevisionId, int? maxDepth, int maxLines, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn;
        var t = (NpgsqlTransaction)tx;
        Guid masterId, iterationId;
        string number, name, partType, revision;
        int iteration;
        Markings markings;
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT m.id, m.number, m.name, m.part_type, r.revision, i.id, i.iteration, {MarkingColumns.Select("m")}
            FROM plm.part_revision r
            JOIN plm.part_master m ON m.id = r.master_id
            JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            WHERE r.id = @rid
            """, c, t))
        {
            cmd.Parameters.AddWithValue("rid", partRevisionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            (masterId, number, name, partType, revision, iterationId, iteration) = (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetGuid(5), r.GetInt32(6));
            markings = MarkingColumns.Read(r, 7);
        }
        if (partType != "Assembly") return null;

        var lines = new List<PublishedBomLine>();
        var depthLimit = Math.Min(maxDepth ?? BomLimits.MaxDepth, BomLimits.MaxDepth);
        var tooLarge = false;

        async Task WalkAsync(Guid parentIteration, int level, string prefix, decimal extended, HashSet<Guid> ancestors)
        {
            if (tooLarge) return;
            var rows = await LoadAsync(c, t, parentIteration, ct);
            foreach (var row in rows)
            {
                if (lines.Count >= maxLines) { tooLarge = true; return; }
                var path = prefix + (prefix.Length == 0 ? "" : ".") + row.Find + (row.Alt > 0 ? "~" + row.Alt : "");
                var ext = Math.Round(Math.Min(extended * row.Qty, 1e18m), 6);
                lines.Add(new PublishedBomLine(level, path, row.Find, row.Alt, row.Qty, ext, row.Unit, row.Designators, row.Notes, row.Effectivity?.Describe(),
                    row.ChildId, row.Number, row.Name, row.PartType, row.Revision, row.Iteration, row.State, row.IterationId is null, row.Markings));
                if (level < depthLimit && row.IterationId is { } childIteration && !ancestors.Contains(row.ChildId))
                    await WalkAsync(childIteration, level + 1, path, ext, [.. ancestors, row.ChildId]);
            }
        }
        await WalkAsync(iterationId, 1, "", 1m, [masterId]);
        return new PublishedBom(masterId, partRevisionId, number, name, revision, iteration, markings, tooLarge ? [] : lines, tooLarge);
    }

    private static async Task<List<Row>> LoadAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid parentIteration, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT u.id, u.find_number, u.alt, u.quantity, u.unit, u.reference_designators, u.notes, u.eff_kind, u.eff_context, u.eff_from, u.eff_to,
                   cm.id, cm.number, cm.name, cm.part_type, {MarkingColumns.Select("cm")}, res.revision, res.iteration, res.state, res.iter_id
            FROM plm.bom_usage u
            JOIN plm.part_master cm ON cm.id = u.child_master_id
            LEFT JOIN LATERAL (
                SELECT r.revision, i.iteration, r.lifecycle_state AS state, i.id AS iter_id
                FROM plm.part_revision r JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
                WHERE r.master_id = cm.id AND r.lifecycle_state = 'Released'
                ORDER BY length(r.revision) DESC, r.revision DESC LIMIT 1) res ON true
            WHERE u.parent_iteration_id = @p
            ORDER BY u.find_number, u.alt
            """, c, t);
        cmd.Parameters.AddWithValue("p", parentIteration);
        var rows = new List<Row>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            EffectivityRange? eff = r.IsDBNull(7) ? null : new EffectivityRange(r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : r.GetInt64(10));
            rows.Add(new Row(r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), r.GetDecimal(3), r.GetString(4), (string[])r.GetValue(5), r.IsDBNull(6) ? null : r.GetString(6), eff,
                r.GetGuid(11), r.GetString(12), r.GetString(13), r.GetString(14), MarkingColumns.Read(r, 15),
                r.IsDBNull(25) ? null : r.GetString(25), r.IsDBNull(26) ? null : r.GetInt32(26), r.IsDBNull(27) ? null : r.GetString(27), r.IsDBNull(28) ? null : r.GetGuid(28)));
        }
        return rows;
    }
}
