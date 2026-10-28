using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

public sealed partial class BaselineService
{
    // ---------------------------------------------------------------- search

    private static string SummarySelect(SqlFilter rootFilter) =>
        $"""
        SELECT {BaselineStore.Select}, pm.number, pm.name, pr.revision, pit.iteration, ls.label
        FROM cfg.baseline b
        LEFT JOIN plm.part_master pm ON pm.id = b.root_master_id AND {rootFilter.Sql}
        LEFT JOIN plm.part_revision pr ON pr.id = b.root_revision_id AND pm.id IS NOT NULL
        LEFT JOIN plm.part_iteration pit ON pit.id = b.root_iteration_id AND pm.id IS NOT NULL
        JOIN plm.lifecycle_state ls ON ls.lifecycle_id = b.lifecycle_id AND ls.state = b.lifecycle_state
        """;

    private const int SummaryTail = 28 + MarkingColumns.Count;

    private static (BaselineSummary Summary, BaselineHead Head) ReadSummary(NpgsqlDataReader r)
    {
        var head = BaselineStore.Read(r);
        var t = SummaryTail;
        var readableRoot = !r.IsDBNull(t);
        var markings = head.Markings;
        var summary = new BaselineSummary(head.Id, head.Number, head.Name, head.Type, head.Milestone, head.State, r.GetString(t + 4), head.Signoff,
            readableRoot ? r.GetString(t) : null, readableRoot ? r.GetString(t + 1) : null, readableRoot ? $"{r.GetString(t + 2)}.{r.GetInt32(t + 3)}" : null,
            head.ItemCount, head.SpecText, head.CreatedAt, head.CreatedBy, head.EstablishedAt, markings.BannerText(), markings);
        return (summary, head);
    }

    public async Task<Outcome<BaselinePage>> SearchAsync(Subject subject, BaselineSearch query, string? correlationId, CancellationToken ct = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 200);
        var offset = Math.Max(0, query.Offset);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var filter = AccessPolicy.BuildReadFilter(subject, "b");
        var rootFilter = AccessPolicy.BuildReadFilter(subject, "pm");
        var where = new List<string>();
        var parameters = new List<KeyValuePair<string, object>>(filter.Parameters);
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            where.Add("(b.number ILIKE @q OR b.name ILIKE @q OR b.milestone ILIKE @q OR b.purpose ILIKE @q)");
            parameters.Add(new("q", "%" + query.Text.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%"));
        }
        if (!string.IsNullOrWhiteSpace(query.State)) { where.Add("b.lifecycle_state = @state"); parameters.Add(new("state", query.State.Trim())); }
        if (!string.IsNullOrWhiteSpace(query.Type)) { where.Add("b.baseline_type = @type"); parameters.Add(new("type", query.Type.Trim())); }
        foreach (var (field, column) in new[] { (query.RootNumber, "b.root_master_id"), (query.ContainsPart, "") })
        {
            if (string.IsNullOrWhiteSpace(field)) continue;
            // The part must be one the caller can read: nobody learns which baselines hold a part they cannot see.
            var partFilter = AccessPolicy.BuildReadFilter(subject, "pf");
            Guid? master;
            await using (var find = new NpgsqlCommand($"SELECT pf.id FROM plm.part_master pf WHERE pf.number = @n AND {partFilter.Sql}", conn, tx))
            {
                foreach (var (k, v) in partFilter.Parameters) find.Parameters.AddWithValue(k, v);
                find.Parameters.AddWithValue("n", field.Trim());
                master = (Guid?)await find.ExecuteScalarAsync(ct);
            }
            if (master is null) { where.Add("FALSE"); continue; }
            var name = column.Length > 0 ? "root" : "contains";
            where.Add(column.Length > 0
                ? $"b.root_master_id = @{name}"
                : $"EXISTS (SELECT 1 FROM cfg.baseline_item bi WHERE bi.baseline_id = b.id AND bi.master_id = @{name})");
            parameters.Add(new(name, master.Value));
        }
        var extra = string.Concat(where.Select(w => " AND " + w));

        int total;
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM cfg.baseline b WHERE {filter.Sql}{extra}", conn, tx))
        {
            foreach (var (k, v) in parameters) count.Parameters.AddWithValue(k, v);
            total = Convert.ToInt32(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        var items = new List<BaselineSummary>();
        await using (var cmd = new NpgsqlCommand(
            $"{SummarySelect(rootFilter)} WHERE {filter.Sql}{extra} ORDER BY b.created_at DESC, b.number DESC LIMIT @limit OFFSET @offset", conn, tx))
        {
            foreach (var (k, v) in parameters.Concat(rootFilter.Parameters)) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(ReadSummary(r).Summary);
        }
        var banner = Markings.AggregateBanner(items.Select(i => i.Markings).Distinct());
        await audit.AppendAsync(tx, Entry(subject, "baseline.search", AuditOutcome.Success, null, correlationId,
            ("text", query.Text), ("state", query.State), ("type", query.Type), ("root", query.RootNumber), ("contains", query.ContainsPart), ("results", items.Count.ToString(CultureInfo.InvariantCulture)), ("banner", banner)), ct);
        await tx.CommitAsync(ct);
        return Outcome<BaselinePage>.Ok(new BaselinePage(items, total, banner));
    }

    // ---------------------------------------------------------------- get

    private static async Task<BaselineDetail?> LoadDetailAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, BaselineHead head, IReadOnlyList<string> notices, CancellationToken ct)
    {
        var rootFilter = AccessPolicy.BuildReadFilter(subject, "pm");
        BaselineSummary? summary = null;
        await using (var cmd = new NpgsqlCommand($"{SummarySelect(rootFilter)} WHERE b.id = @id", conn, tx))
        {
            foreach (var (k, v) in rootFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("id", head.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) summary = ReadSummary(r).Summary;
        }
        if (summary is null) return null;

        // Neighbours in the supersession chain are shown only when the reader may read them.
        var filter = AccessPolicy.BuildReadFilter(subject, "b");
        string? supersededBy = null;
        if (head.SupersededBy is { } sid)
        {
            await using var cmd = new NpgsqlCommand($"SELECT b.number FROM cfg.baseline b WHERE b.id = @id AND {filter.Sql}", conn, tx);
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("id", sid);
            supersededBy = (string?)await cmd.ExecuteScalarAsync(ct);
        }
        var supersedes = new List<string>();
        await using (var cmd = new NpgsqlCommand($"SELECT b.number FROM cfg.baseline b WHERE b.superseded_by = @id AND {filter.Sql} ORDER BY b.number", conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("id", head.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) supersedes.Add(r.GetString(0));
        }

        var modify = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Modify);
        var canModify = modify.Permitted;
        var permissions = new BaselinePermissions(canModify,
            canModify && !head.Signoff && head.State == BaselineStates.Draft, head.Signoff,
            canModify && head.State is BaselineStates.Draft or BaselineStates.Established or BaselineStates.Superseded,
            canModify ? null : string.Join(",", modify.Reasons));
        return new BaselineDetail(summary, head.Purpose, head.ReleasedOnly, head.IncludeDocuments, head.DocumentCount, head.Sha256, head.EstablishReason, head.EstablishedBy,
            head.StateReason, supersededBy, supersedes, permissions, notices);
    }

    public async Task<Outcome<BaselineDetail>> GetAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = string.IsNullOrWhiteSpace(number) ? null : await BaselineStore.LoadByNumberAsync(conn, tx, number.Trim(), false, ct);
        if (head is null) return Outcome<BaselineDetail>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "baseline.read", AuditOutcome.Denied, head.Number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<BaselineDetail>.NotFound();
        }
        var detail = await LoadDetailAsync(conn, tx, subject, head, [], ct);
        await audit.AppendAsync(tx, Entry(subject, "baseline.read", AuditOutcome.Success, head.Number, correlationId, ("state", head.State), ("banner", head.Markings.BannerText())), ct);
        await tx.CommitAsync(ct);
        return Outcome<BaselineDetail>.Ok(detail!);
    }

    // ---------------------------------------------------------------- pinned documents

    public async Task<Outcome<BaselineDocuments>> GetDocumentsAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = string.IsNullOrWhiteSpace(number) ? null : await BaselineStore.LoadByNumberAsync(conn, tx, number.Trim(), false, ct);
        if (head is null) return Outcome<BaselineDocuments>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "baseline.documents", AuditOutcome.Denied, head.Number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<BaselineDocuments>.NotFound();
        }
        var rows = new List<BaselineDocumentRow>();
        var markings = new List<Markings> { head.Markings };
        if (head.DocumentCount > 0)
        {
            if (documents is null) throw new NotSupportedException("Showing pinned documents needs the document provider (IDocumentPinProvider).");
            var (items, docs) = await BaselineStore.LoadContentAsync(conn, tx, head.Id, ct);
            var pathOf = items.ToDictionary(i => i.Seq, i => i.Path);
            var described = await documents.DescribeAsync(conn, tx, subject, [.. docs.Select(d => d.RevisionId).Distinct()], ct);
            foreach (var d in docs)
            {
                var info = described.GetValueOrDefault(d.RevisionId);
                if (info is null || !info.Readable) { rows.Add(new BaselineDocumentRow(true, pathOf[d.ItemSeq], d.LinkType, null, null, null, null, null, null, null)); continue; }
                if (info.Markings is not null) markings.Add(info.Markings);
                rows.Add(new BaselineDocumentRow(false, pathOf[d.ItemSeq], d.LinkType, info.Number, info.Name, info.DocumentType, info.Revision, info.State, info.Banner, info.Markings));
            }
        }
        var banner = Markings.AggregateBanner(markings.Distinct());
        await audit.AppendAsync(tx, Entry(subject, "baseline.documents", AuditOutcome.Success, head.Number, correlationId,
            ("documents", rows.Count.ToString(CultureInfo.InvariantCulture)), ("redacted", rows.Count(r => r.Redacted).ToString(CultureInfo.InvariantCulture)), ("banner", banner)), ct);
        await tx.CommitAsync(ct);
        return Outcome<BaselineDocuments>.Ok(new BaselineDocuments(head.Number, rows, banner));
    }

    // ---------------------------------------------------------------- integrity

    /// <summary>Recomputes the content hash from the stored rows and compares it with the one recorded at creation (an audit-time integrity check).</summary>
    public async Task<Outcome<BaselineIntegrity>> VerifyAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = string.IsNullOrWhiteSpace(number) ? null : await BaselineStore.LoadByNumberAsync(conn, tx, number.Trim(), false, ct);
        if (head is null) return Outcome<BaselineIntegrity>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "baseline.verify", AuditOutcome.Denied, head.Number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<BaselineIntegrity>.NotFound();
        }
        var computed = await BaselineStore.RecomputeHashAsync(conn, tx, head, ct);
        var ok = computed == head.Sha256;
        await audit.AppendAsync(tx, Entry(subject, "baseline.verify", ok ? AuditOutcome.Success : AuditOutcome.Failure, head.Number, correlationId, ("stored", head.Sha256), ("computed", computed)), ct);
        await tx.CommitAsync(ct);
        return Outcome<BaselineIntegrity>.Ok(new BaselineIntegrity(head.Number, ok, head.Sha256, computed));
    }
}

public sealed record BaselineIntegrity(string Number, bool Intact, string StoredSha256, string ComputedSha256);
