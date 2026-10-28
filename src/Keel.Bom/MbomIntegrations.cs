using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

/// <summary>
/// Read API for integrations (ADR-0024): the released MBOM of a part in a plant with children resolved to the released MBOMs of the same plant. Applies the access
/// policy and the read filter like every read, redacts lines of unreadable parts (A4) and audits the read. ERP publishing itself belongs to the integration module.
/// </summary>
internal sealed class MbomBomSource(NpgsqlDataSource dataSource, IAuditLog audit) : IManufacturingBomSource
{
    public async Task<Outcome<ManufacturingBomSnapshot>> GetReleasedAsync(Subject subject, string partNumber, string plant, string? revision, string? correlationId, CancellationToken ct)
    {
        var plantCode = plant?.Trim().ToUpperInvariant() ?? "";
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var part = await MbomStore.LoadPartByNumberAsync(conn, tx, partNumber, ct);
        MbomHead? head = null;
        if (part is not null)
        {
            string? number, rev;
            await using (var cmd = new NpgsqlCommand(
                """
                SELECT mm.number, r.revision FROM mbom.mbom mm JOIN mbom.revision r ON r.mbom_id = mm.id
                WHERE mm.part_master_id = @pm AND mm.plant = @plant AND r.lifecycle_state = 'Released' AND (@rev::text IS NULL OR r.revision = @rev)
                ORDER BY length(r.revision) DESC, r.revision DESC LIMIT 1
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("pm", part.Id);
                cmd.Parameters.AddWithValue("plant", plantCode);
                cmd.Parameters.Add(new NpgsqlParameter("rev", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)revision ?? DBNull.Value });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                (number, rev) = await r.ReadAsync(ct) ? (r.GetString(0), r.GetString(1)) : (null, null);
            }
            if (number is not null) head = await MbomStore.LoadByNumberAsync(conn, tx, number, rev, false, ct);
        }
        if (head is null) return Outcome<ManufacturingBomSnapshot>.NotFound();
        var read = MbomService.CanRead(subject, head);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, MbomService.Entry(subject, "mbom.read_released", AuditOutcome.Denied, head.Number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<ManufacturingBomSnapshot>.NotFound();
        }

        var cache = new MarkingCache();
        var views = await MbomService.LoadLineViewsAsync(conn, tx, subject, false, head.IterationId, null, cache, ct);
        var children = views.Where(v => !v.Redacted && v.SupplyType == "Make" && v.ChildPartType == "Assembly" && v.ChildMasterId is not null).Select(v => v.ChildMasterId!.Value).Distinct().ToArray();
        var childMboms = new Dictionary<Guid, (string Number, string Revision)>();
        if (children.Length > 0)
        {
            var filter = AccessPolicy.BuildReadFilter(subject, "mm");
            await using var cmd = new NpgsqlCommand(
                $"""
                SELECT DISTINCT ON (mm.part_master_id) mm.part_master_id, mm.number, r.revision
                FROM mbom.mbom mm JOIN mbom.revision r ON r.mbom_id = mm.id AND r.lifecycle_state = 'Released'
                WHERE mm.plant = @plant AND mm.part_master_id = ANY(@ids) AND {filter.Sql}
                ORDER BY mm.part_master_id, length(r.revision) DESC, r.revision DESC
                """, conn, tx);
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("plant", head.Plant);
            cmd.Parameters.AddWithValue("ids", children);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) childMboms[r.GetGuid(0)] = (r.GetString(1), r.GetString(2));
        }
        var lines = views.Select(v =>
        {
            childMboms.TryGetValue(v.ChildMasterId ?? Guid.Empty, out var child);
            return new ManufacturingBomLine(v.FindNumber, v.Alt, v.Redacted, v.ChildNumber, v.ChildName, v.ChildSource, v.Quantity, v.Unit, v.ScrapFactor, v.Phantom, v.SupplyType, v.OperationReference, v.MbomOnly,
                v.ReferenceDesignators, v.Notes, v.Effectivity, [.. v.Links.Select(l => new ManufacturingBomLink(l.EbomFindNumber, l.Status, l.Reason))], child.Number, child.Revision);
        }).ToList();

        string sha;
        Guid? derived;
        await using (var cmd = new NpgsqlCommand("SELECT content_sha256, derived_part_iteration_id FROM mbom.iteration WHERE id = @i", conn, tx))
        {
            cmd.Parameters.AddWithValue("i", head.IterationId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            sha = r.GetString(0);
            derived = r.IsDBNull(1) ? null : r.GetGuid(1);
        }
        var derivedLabel = derived is { } d ? await MbomStore.IterationLabelAsync(conn, tx, d, ct) : null;
        Guid? runId = null;
        string? status = null;
        await using (var cmd = new NpgsqlCommand(
            "SELECT b.run_id, r.status FROM mbom.review_binding b JOIN mbom.reconciliation_run r ON r.id = b.run_id WHERE b.iteration_id = @i ORDER BY b.bound_at DESC, b.id DESC LIMIT 1", conn, tx))
        {
            cmd.Parameters.AddWithValue("i", head.IterationId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) (runId, status) = (r.GetGuid(0), r.GetString(1));
        }
        var banner = Markings.AggregateBanner(cache.Distinct.Append(head.Markings));
        await audit.AppendAsync(tx, MbomService.Entry(subject, "mbom.read_released", AuditOutcome.Success, head.Number, correlationId,
            ("plant", head.Plant), ("revision", head.Label), ("lines", lines.Count.ToString(CultureInfo.InvariantCulture)), ("redacted", lines.Count(l => l.Redacted).ToString(CultureInfo.InvariantCulture)), ("banner", banner)), ct);
        await tx.CommitAsync(ct);
        return Outcome<ManufacturingBomSnapshot>.Ok(new ManufacturingBomSnapshot(head.Number, head.PartNumber, head.PartName, head.Plant, head.Revision, head.LatestIteration, head.State,
            derivedLabel, sha, runId, status, head.Markings, banner, lines));
    }
}

/// <summary>
/// Warns change management which MBOMs were derived from the EBOM of a part a change revises (they become stale when the new revision exists). An MBOM the caller
/// may not read is counted, never named.
/// </summary>
internal sealed class MbomImpactProvider(NpgsqlDataSource dataSource, IAuditLog audit) : IManufacturingImpactProvider
{
    public string ObjectType => BomService.ObjectType;

    public async Task<Outcome<ManufacturingImpact>> StaleMbomsAsync(Subject subject, string partKey, string? correlationId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var part = await MbomStore.LoadPartByNumberAsync(conn, tx, partKey, ct);
        if (part is null || !AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Read).Permitted) return Outcome<ManufacturingImpact>.NotFound();
        var current = await CurrentLabelAsync(conn, tx, part.Id, ct);
        var filter = AccessPolicy.BuildReadFilter(subject, "mm");
        var items = new List<StaleMbom>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT mm.number, mm.plant, r.revision, r.latest_iteration, r.lifecycle_state, COALESCE(ls.label, r.lifecycle_state), dr.revision || '.' || di.iteration, i.derived_part_iteration_id,
                   {MarkingColumns.Select("mm")}
            FROM mbom.mbom mm
            JOIN mbom.revision r ON r.mbom_id = mm.id AND r.is_latest AND r.lifecycle_state <> 'Obsolete'
            JOIN mbom.iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            JOIN plm.part_iteration di ON di.id = i.derived_part_iteration_id
            JOIN plm.part_revision dr ON dr.id = di.revision_id
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = mm.lifecycle_id AND ls.state = r.lifecycle_state
            WHERE {filter.Sql} AND mm.part_master_id = @pm
            ORDER BY mm.plant, mm.number
            """, conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("pm", part.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var m = MarkingColumns.Read(r, 8);
                var derivedLabel = r.GetString(6);
                items.Add(new StaleMbom(r.GetString(0), part.Number, r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetString(5), derivedLabel, current.Label, derivedLabel != current.Label, m.BannerText(), m));
            }
        }
        long total;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT count(*) FROM mbom.mbom mm JOIN mbom.revision r ON r.mbom_id = mm.id AND r.is_latest AND r.lifecycle_state <> 'Obsolete'
            JOIN mbom.iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            WHERE mm.part_master_id = @pm AND i.derived_part_iteration_id IS NOT NULL
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("pm", part.Id);
            total = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        var hidden = (int)(total - items.Count);
        await audit.AppendAsync(tx, MbomService.Entry(subject, "mbom.impact", AuditOutcome.Success, part.Number, correlationId,
            ("mboms", items.Count.ToString(CultureInfo.InvariantCulture)), ("hidden", hidden.ToString(CultureInfo.InvariantCulture)), ("stale", items.Count(i => i.StaleNow).ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return Outcome<ManufacturingImpact>.Ok(new ManufacturingImpact(items, hidden));
    }

    private static async Task<(Guid Iteration, string Label)> CurrentLabelAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid master, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT i.id, r.revision || '.' || i.iteration FROM plm.part_revision r JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration WHERE r.master_id = @m AND r.is_latest", conn, tx);
        cmd.Parameters.AddWithValue("m", master);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetGuid(0), r.GetString(1));
    }
}
