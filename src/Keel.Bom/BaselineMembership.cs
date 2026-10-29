using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

/// <summary>
/// The parts a baseline contains, as far as the caller may read them (ADR-0038: requirements coverage by baseline). The baseline must be readable; a part the caller may
/// not read is left out, with no count. Keel.Bom is the only module that reads baseline items.
/// </summary>
public sealed class BaselineMembership(NpgsqlDataSource dataSource) : IBaselineMembership
{
    public async Task<Outcome<IReadOnlyList<BaselinePart>>> ReadablePartsAsync(Subject subject, string baselineNumber, string? correlationId, CancellationToken ct = default)
    {
        var bFilter = AccessPolicy.BuildReadFilter(subject, "b");
        var pFilter = AccessPolicy.BuildReadFilter(subject, "pm");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var items = new List<BaselinePart>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT DISTINCT pm.number, r.revision
            FROM cfg.baseline b
            JOIN cfg.baseline_item bi ON bi.baseline_id = b.id
            JOIN plm.part_master pm ON pm.id = bi.master_id AND {pFilter.Sql}
            JOIN plm.part_revision r ON r.id = bi.revision_id
            WHERE b.number = @n AND {bFilter.Sql}
            ORDER BY pm.number, r.revision
            """, conn))
        {
            foreach (var (k, v) in bFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
            foreach (var (k, v) in pFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("n", baselineNumber);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(new BaselinePart(r.GetString(0), r.GetString(1)));
        }
        if (items.Count == 0)
        {
            await using var exists = new NpgsqlCommand($"SELECT 1 FROM cfg.baseline b WHERE b.number = @n AND {bFilter.Sql}", conn);
            foreach (var (k, v) in bFilter.Parameters) exists.Parameters.AddWithValue(k, v);
            exists.Parameters.AddWithValue("n", baselineNumber);
            if (await exists.ExecuteScalarAsync(ct) is null) return Outcome<IReadOnlyList<BaselinePart>>.NotFound();
        }
        return Outcome<IReadOnlyList<BaselinePart>>.Ok(items);
    }
}
