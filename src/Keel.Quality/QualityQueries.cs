using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Quality;

/// <summary>
/// Lists, the Home dashboard counts and the part view. Every statement over a marked table applies <c>AccessPolicy.BuildReadFilter</c> in its WHERE clause, so a row, a
/// count or a position the caller may not read never appears (ADR-0004). Sort keys are a whitelist looked up, never SQL text (GAP-A-1).
/// </summary>
public sealed class QualityQueries
{
    public const int MaxSearchLimit = 200;
    public const int ExpiringWithinDays = 30;
    private readonly QualityRunner _run;
    private readonly DeviationService _deviations;

    internal QualityQueries(QualityRunner run, DeviationService deviations)
    {
        _run = run;
        _deviations = deviations;
    }

    public static SortMap SortKeysFor(string kind) => kind switch
    {
        QualityKinds.Ncr => new SortMap(("number", ["c.number"]), ("title", ["lower(c.title)"]), ("severity", ["c.severity"]), ("part", ["c.part_number"]), ("state", ["lower(COALESCE(ls.label, c.lifecycle_state))"]),
            ("raisedBy", ["lower(c.reporter_name)"]), ("created", ["c.created_at"])),
        QualityKinds.Capa => new SortMap(("number", ["c.number"]), ("title", ["lower(c.title)"]), ("priority", ["c.priority"]), ("owner", ["lower(c.owner_name)"]), ("state", ["lower(COALESCE(ls.label, c.lifecycle_state))"]),
            ("raisedBy", ["lower(c.requester_name)"]), ("created", ["c.created_at"]), ("due", ["c.target_date"])),
        QualityKinds.Deviation => new SortMap(("number", ["c.number"]), ("title", ["lower(c.title)"]), ("kind", ["c.dev_kind"]), ("state", ["lower(COALESCE(ls.label, c.lifecycle_state))"]),
            ("raisedBy", ["lower(c.requester_name)"]), ("created", ["c.created_at"]), ("due", ["c.valid_to"]), ("window", ["c.valid_from"])),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string SelectFor(string kind) => kind switch
    {
        QualityKinds.Ncr => "c.severity, c.part_number || ' rev ' || c.part_revision, c.reporter_name, c.created_at, NULL::date, COALESCE(ls.is_final, false)",
        QualityKinds.Capa => "c.priority, c.owner_name, c.requester_name, c.created_at, c.target_date, COALESCE(ls.is_final, false)",
        _ => "c.dev_kind, to_char(c.valid_from, 'YYYY-MM-DD') || ' to ' || to_char(c.valid_to, 'YYYY-MM-DD'), c.requester_name, c.created_at, c.valid_to, COALESCE(ls.is_final, false)",
    };

    public async Task<QualitySearchResult> SearchAsync(Subject subject, string kind, QualitySearchQuery query, string? correlationId, CancellationToken ct = default)
    {
        if (!QualityKinds.IsKnown(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var limit = Math.Clamp(query.Limit, 1, MaxSearchLimit);
        var offset = Math.Max(0, query.Offset);
        var q = (query.Text ?? "").Trim();
        var like = "%" + q.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%";
        var filter = AccessPolicy.BuildReadFilter(subject, "c");
        var sorts = SortKeysFor(kind);
        var partClause = kind switch
        {
            QualityKinds.Ncr => " AND (@part::text IS NULL OR c.part_number = @part)",
            QualityKinds.Deviation => " AND (@part::text IS NULL OR EXISTS (SELECT 1 FROM qual.deviation_scope s WHERE s.deviation_id = c.id AND s.part_number = @part))",
            _ => " AND (@part::text IS NULL OR EXISTS (SELECT 1 FROM qual.link l WHERE l.from_kind = 'capa' AND l.from_id = c.id AND l.to_kind = 'part' AND l.to_key = @part AND l.removed_at IS NULL))",
        };
        var today = QualityShared.Today(_run.Clock);
        var items = new List<QualitySummary>();
        long total = 0;
        await using var conn = await _run.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT c.id, c.number, c.title, c.lifecycle_state, COALESCE(ls.label, c.lifecycle_state), {SelectFor(kind)}, {MarkingColumns.Select("c")}, count(*) OVER ()
            FROM {QualityKinds.Table(kind)} c
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = c.lifecycle_id AND ls.state = c.lifecycle_state
            WHERE {filter.Sql}
              AND (@state::text IS NULL OR c.lifecycle_state = @state)
              AND (@q = '' OR c.number ILIKE @like OR c.title ILIKE @like)
              AND (NOT @open OR NOT COALESCE(ls.is_final, false))
              {partClause}
            ORDER BY {sorts.OrderBy(query.Sort, "c.created_at DESC, c.number DESC", "c.number")}
            LIMIT @limit OFFSET @offset
            """, conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("state", (object?)query.State ?? DBNull.Value);
            cmd.Parameters.AddWithValue("q", q);
            cmd.Parameters.AddWithValue("like", like);
            cmd.Parameters.AddWithValue("open", query.OpenOnly);
            cmd.Parameters.AddWithValue("part", (object?)query.PartNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var m = MarkingColumns.Read(r, 11);
                var due = QualityShared.DateAt(r, 9);
                var final = r.GetBoolean(10);
                var state = r.GetString(3);
                var overdue = kind switch
                {
                    QualityKinds.Capa => !final && due is { } d1 && d1 < today,
                    QualityKinds.Deviation => state == "Approved" && due is { } d2 && d2 < today,
                    _ => false,
                };
                items.Add(new QualitySummary(kind, r.GetGuid(0), r.GetString(1), r.GetString(2), state, r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), QualityStore.Utc(r, 8), due, overdue, m.BannerText(), m));
                total = r.GetInt64(11 + MarkingColumns.Count);
            }
        }
        await _run.Audit.AppendAsync(tx, QualityStore.Entry(subject, kind + ".search", AuditOutcome.Success, kind, null, correlationId,
            ("state", query.State), ("query", q), ("sort", query.Sort is null ? null : $"{query.Sort.Key} {query.Sort.DirectionText}"), ("returned", QualityRunner.Int(items.Count)),
            ("numbers", string.Join(",", items.Select(i => i.Number)))), ct);
        await tx.CommitAsync(ct);
        return new QualitySearchResult(items, total, Markings.AggregateBanner(items.Select(i => i.Markings)));
    }

    /// <summary>
    /// The numbers for the Home card. Each count is taken over rows the caller may read; nothing is derived from rows they cannot, so a count can never reveal that a
    /// restricted record exists.
    /// </summary>
    public async Task<QualityDashboardData> DashboardAsync(Subject subject, string? correlationId, CancellationToken ct = default)
    {
        var today = QualityShared.Today(_run.Clock);
        var nFilter = AccessPolicy.BuildReadFilter(subject, "n");
        var pFilter = AccessPolicy.BuildReadFilter(subject, "p");
        var dFilter = AccessPolicy.BuildReadFilter(subject, "d");
        await using var conn = await _run.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT
              (SELECT count(*) FROM qual.ncr n WHERE {nFilter.Sql} AND NOT qual.is_final_state(n.lifecycle_id, n.lifecycle_state)),
              (SELECT count(*) FROM qual.capa p WHERE {pFilter.Sql} AND NOT qual.is_final_state(p.lifecycle_id, p.lifecycle_state)),
              (SELECT count(*) FROM qual.capa p WHERE {pFilter.Sql} AND NOT qual.is_final_state(p.lifecycle_id, p.lifecycle_state) AND p.target_date < @today),
              (SELECT count(*) FROM qual.action a JOIN qual.ncr n ON a.parent_kind = 'ncr' AND n.id = a.parent_id
                 WHERE {nFilter.Sql} AND a.status IN ('Open', 'InProgress') AND a.due_date < @today)
              + (SELECT count(*) FROM qual.action a JOIN qual.capa p ON a.parent_kind = 'capa' AND p.id = a.parent_id
                 WHERE {pFilter.Sql} AND a.status IN ('Open', 'InProgress') AND a.due_date < @today),
              (SELECT count(*) FROM qual.action a JOIN qual.ncr n ON a.parent_kind = 'ncr' AND n.id = a.parent_id
                 WHERE {nFilter.Sql} AND a.status IN ('Open', 'InProgress') AND a.owner_sub = @me)
              + (SELECT count(*) FROM qual.action a JOIN qual.capa p ON a.parent_kind = 'capa' AND p.id = a.parent_id
                 WHERE {pFilter.Sql} AND a.status IN ('Open', 'InProgress') AND a.owner_sub = @me),
              (SELECT count(*) FROM qual.deviation d WHERE {dFilter.Sql} AND d.lifecycle_state = 'Approved' AND d.valid_from <= @today AND d.valid_to >= @today),
              (SELECT count(*) FROM qual.deviation d WHERE {dFilter.Sql} AND d.lifecycle_state = 'Approved' AND d.valid_to >= @today AND d.valid_to <= @soon),
              COALESCE((SELECT max(@today::date - n.created_at::date) FROM qual.ncr n WHERE {nFilter.Sql} AND NOT qual.is_final_state(n.lifecycle_id, n.lifecycle_state)), 0)
            """, conn);
        foreach (var f in new[] { nFilter, pFilter, dFilter })
            foreach (var (k, v) in f.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("today", today.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("soon", today.AddDays(ExpiringWithinDays).ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("me", subject.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var data = new QualityDashboardData((int)r.GetInt64(0), (int)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3), (int)r.GetInt64(4), (int)r.GetInt64(5), (int)r.GetInt64(6), Convert.ToInt32(r.GetValue(7), CultureInfo.InvariantCulture), "");
        await r.CloseAsync();
        await _run.Audit.AppendAsync(QualityStore.Entry(subject, "quality.dashboard", AuditOutcome.Success, "quality", null, correlationId), ct);
        return data;
    }

    /// <summary>What quality knows about a part, for its page: the deviations and waivers that apply, its open reports and the open corrective actions linked to it.</summary>
    public async Task<Outcome<PartQuality>> ForPartAsync(Subject subject, string partNumber, bool includeInactive, string? correlationId, CancellationToken ct = default)
    {
        var dev = await _deviations.ForPartAsync(subject, partNumber, includeInactive, null, null, null, correlationId, ct);
        if (!dev.IsOk) return Outcome<PartQuality>.NotFound();
        var ncrs = await SearchAsync(subject, QualityKinds.Ncr, new QualitySearchQuery(OpenOnly: true, Limit: 50, PartNumber: partNumber), correlationId, ct);
        var capas = await SearchAsync(subject, QualityKinds.Capa, new QualitySearchQuery(OpenOnly: true, Limit: 50, PartNumber: partNumber), correlationId, ct);
        var shown = dev.Value!.Select(x => x.Markings).Concat(ncrs.Items.Select(i => i.Markings)).Concat(capas.Items.Select(i => i.Markings)).ToList();
        return Outcome<PartQuality>.Ok(new PartQuality(dev.Value!, ncrs.Items, capas.Items, Markings.AggregateBanner(shown), shown));
    }
}
