using Npgsql;

namespace Keel.Migration;

public sealed record BatchSummary(Guid Id, string Name, string SourceSystem, string PackageSha256, string MappingSha256, string AuditGranularity, bool Quarantine, string Status,
    DateTimeOffset StartedAt, string StartedBy, DateTimeOffset? CompletedAt, string SummaryJson);

public sealed record QuarantineRow(string EntityType, string SourceId, string Code, string Message);
public sealed record StoredReport(string Kind, DateTimeOffset CreatedAt, string ContentJson);
public sealed record BatchDetail(BatchSummary Batch, IReadOnlyList<QuarantineRow> Quarantine, IReadOnlyList<StoredReport> Reports);

/// <summary>
/// Read access to the migration bookkeeping tables (<c>mig.*</c>) for the admin page and the CLI. These tables hold counts, source ids, codes and
/// hashes only: no object names or descriptions, so they are not subject to the markings read filter (ADR-0023).
/// </summary>
public sealed class BatchQueries(NpgsqlDataSource ds)
{
    public async Task<IReadOnlyList<BatchSummary>> ListAsync(int limit, CancellationToken ct)
    {
        var list = new List<BatchSummary>();
        await using var c = ds.CreateCommand("SELECT id, name, source_system, package_sha256, mapping_sha256, audit_granularity, quarantine_mode, status, started_at, started_by, completed_at, summary::text FROM mig.batch ORDER BY started_at DESC LIMIT @n");
        c.Parameters.AddWithValue("n", limit);
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<BatchDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        BatchSummary? batch = null;
        await using (var c = ds.CreateCommand("SELECT id, name, source_system, package_sha256, mapping_sha256, audit_granularity, quarantine_mode, status, started_at, started_by, completed_at, summary::text FROM mig.batch WHERE id = @id"))
        {
            c.Parameters.AddWithValue("id", id);
            await using var r = await c.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) batch = Read(r);
        }
        if (batch is null) return null;
        var quarantine = new List<QuarantineRow>();
        await using (var c = ds.CreateCommand("SELECT entity_type, source_id, code, message FROM mig.quarantine WHERE batch_id = @id ORDER BY id"))
        {
            c.Parameters.AddWithValue("id", id);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) quarantine.Add(new QuarantineRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }
        var reports = new List<StoredReport>();
        await using (var c = ds.CreateCommand("SELECT kind, created_at, content::text FROM mig.report WHERE batch_id = @id ORDER BY id"))
        {
            c.Parameters.AddWithValue("id", id);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) reports.Add(new StoredReport(r.GetString(0), new DateTimeOffset(r.GetDateTime(1), TimeSpan.Zero), r.GetString(2)));
        }
        return new BatchDetail(batch, quarantine, reports);
    }

    private static BatchSummary Read(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetBoolean(6), r.GetString(7),
            new DateTimeOffset(r.GetDateTime(8), TimeSpan.Zero), r.GetString(9), r.IsDBNull(10) ? null : new DateTimeOffset(r.GetDateTime(10), TimeSpan.Zero), r.GetString(11));
}
