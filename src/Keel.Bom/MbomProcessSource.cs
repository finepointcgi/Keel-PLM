using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Npgsql;

namespace Keel.Bom;

/// <summary>The MBOM module's side of process planning (ADR-0042): headers, latest Released revision and lines of a frozen revision, all under the caller's access.</summary>
internal sealed class MbomProcessSource(NpgsqlDataSource dataSource, IAuditLog audit) : IMbomProcessSource
{
    private static async Task<MbomForProcess> ToRefAsync(NpgsqlConnection conn, NpgsqlTransaction tx, MbomHead head, CancellationToken ct)
    {
        MbomRevisionRef? released = null;
        await using var cmd = new NpgsqlCommand(
            """
            SELECT r.revision, r.latest_iteration, i.content_sha256 FROM mbom.revision r JOIN mbom.iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            WHERE r.mbom_id = @m AND r.lifecycle_state = 'Released' ORDER BY length(r.revision) DESC, r.revision DESC LIMIT 1
            """, conn, tx);
        cmd.Parameters.AddWithValue("m", head.Id);
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct)) released = new MbomRevisionRef(r.GetString(0), "Released", r.GetInt32(1), r.GetString(2));
        return new MbomForProcess(head.Id, head.Number, head.PartNumber, head.PartName, head.Plant, head.Markings, head.PartMarkings, head.Markings.BannerText(), released);
    }

    public async Task<MbomForProcess?> FindAsync(Subject subject, string mbomNumber, string? correlationId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await MbomStore.LoadByNumberAsync(conn, tx, mbomNumber, null, false, ct);
        if (head is null) return null;
        var read = MbomService.CanRead(subject, head);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, MbomService.Entry(subject, "mbom.process_read", AuditOutcome.Denied, mbomNumber, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return null;
        }
        var result = await ToRefAsync(conn, tx, head, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<MbomRevisionRef?> LatestReleasedAsync(string mbomNumber, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await MbomStore.LoadByNumberAsync(conn, tx, mbomNumber, null, false, ct);
        var result = head is null ? null : (await ToRefAsync(conn, tx, head, ct)).LatestReleased;
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<string>> ActivePlantsAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT code FROM mbom.plant WHERE is_active ORDER BY sort_order, code", conn);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    public async Task<IReadOnlyDictionary<string, MbomForProcess>> ReadableAsync(Subject subject, IReadOnlyCollection<string> mbomNumbers, CancellationToken ct)
    {
        var result = new Dictionary<string, MbomForProcess>(StringComparer.Ordinal);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var number in mbomNumbers.Distinct(StringComparer.Ordinal))
        {
            var head = await MbomStore.LoadByNumberAsync(conn, tx, number, null, false, ct);
            if (head is not null && MbomService.CanRead(subject, head).Permitted) result[number] = await ToRefAsync(conn, tx, head, ct);
        }
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<Outcome<IReadOnlyList<MbomLineRef>>> LinesAsync(Subject subject, string mbomNumber, string revision, string? correlationId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await MbomStore.LoadByNumberAsync(conn, tx, mbomNumber, revision, false, ct);
        if (head is null || head.State is not ("Released" or "Obsolete")) return Outcome<IReadOnlyList<MbomLineRef>>.NotFound();
        var read = MbomService.CanRead(subject, head);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, MbomService.Entry(subject, "mbom.process_read", AuditOutcome.Denied, mbomNumber, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<IReadOnlyList<MbomLineRef>>.NotFound();
        }
        var views = await MbomService.LoadLineViewsAsync(conn, tx, subject, false, head.IterationId, null, new MarkingCache(), ct);
        await audit.AppendAsync(tx, MbomService.Entry(subject, "mbom.process_read", AuditOutcome.Success, mbomNumber, correlationId,
            ("revision", head.Label), ("lines", views.Count.ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        IReadOnlyList<MbomLineRef> lines = [.. views.Select(v => new MbomLineRef(v.FindNumber, v.Alt, v.Redacted, v.Redacted ? null : v.ChildMasterId, v.ChildNumber, v.ChildName, v.Quantity, v.Unit, v.Phantom, v.SupplyType, v.Redacted ? null : v.Markings))];
        return Outcome<IReadOnlyList<MbomLineRef>>.Ok(lines);
    }
}
