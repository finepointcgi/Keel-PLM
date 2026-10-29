using Keel.Core;
using Npgsql;

namespace Keel.Process;

/// <summary>The tool-use index of a plan iteration (where-used of tools, calibration warnings): pointer rows only.</summary>
internal static class ProcessToolUse
{
    public static async Task RebuildAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid revisionId, int iteration, PlanContent content, CancellationToken ct)
    {
        Guid iterationId;
        await using (var q = new NpgsqlCommand("SELECT id FROM proc.plan_iteration WHERE revision_id = @r AND iteration = @n", conn, tx))
        {
            q.Parameters.AddWithValue("r", revisionId);
            q.Parameters.AddWithValue("n", iteration);
            iterationId = (Guid)(await q.ExecuteScalarAsync(ct))!;
        }
        await using (var del = new NpgsqlCommand("DELETE FROM proc.tool_use WHERE iteration_id = @i", conn, tx))
        {
            del.Parameters.AddWithValue("i", iterationId);
            await del.ExecuteNonQueryAsync(ct);
        }
        var ids = content.Routings.SelectMany(r => r.Operations).SelectMany(o => o.Tools.Concat(o.Steps.SelectMany(s => s.Tools))).Distinct().ToArray();
        if (ids.Length == 0) return;
        await using var ins = new NpgsqlCommand("INSERT INTO proc.tool_use (iteration_id, tool_id) SELECT @i, t.id FROM proc.tool t WHERE t.id = ANY(@ids) ON CONFLICT DO NOTHING", conn, tx);
        ins.Parameters.AddWithValue("i", iterationId);
        ins.Parameters.AddWithValue("ids", ids);
        await ins.ExecuteNonQueryAsync(ct);
    }
}
