using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Integration;

public sealed record PulledEvent(string Cursor, JsonObject Envelope);

public sealed record EventPage(IReadOnlyList<PulledEvent> Events, string? NextCursor);

/// <summary>One outbox row as an administrator sees it: metadata always, identity and payload only when the administrator may read the event's markings.</summary>
public sealed record OutboxListItem(
    Guid Id, long Seq, string EventType, int Version, DateTimeOffset OccurredAt, string Banner, bool Readable,
    string? AggregateType, string? AggregateNumber, string? AggregateRevision, JsonObject? Envelope, IReadOnlyDictionary<string, int> Deliveries);

/// <summary>
/// Reads of the outbox on behalf of people and service accounts. Every statement carries the ABAC read filter of the caller over the
/// outbox's marking columns (ADR-0004), so an event is visible exactly to those who could read the object it reports. Rows are only shown
/// once their transaction is older than every in-flight transaction, so a cursor never skips an event that commits late.
/// </summary>
public sealed class OutboxQueries(NpgsqlDataSource dataSource, IAuditLog audit)
{
    public const int MaxLimit = 500;
    private const string Visible = "o.created_in_tx < pg_snapshot_xmin(pg_current_snapshot())";

    /// <summary>
    /// Reconciliation pull for service accounts: events in commit order after <paramref name="cursor"/>, only those the caller may read.
    /// The caller's own attributes decide (no sink clearance profile applies to a pull).
    /// </summary>
    public async Task<Outcome<EventPage>> PullAsync(Subject subject, string? cursor, int limit, string? eventType, string? aggregateNumber, string? correlationId, CancellationToken ct = default)
    {
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Read))
        {
            await audit.AppendAsync(IntegrationAudit.Entry(subject, "integration.pull", AuditOutcome.Denied, null, correlationId, ("reasons", AccessPolicy.RoleMissing)), ct);
            return Outcome<EventPage>.Forbidden([AccessPolicy.RoleMissing]);
        }
        if (!TryParseCursor(cursor, out var xid, out var seq)) return Outcome<EventPage>.Invalid(["The cursor is not valid."]);
        if (eventType is not null && !EventTypes.All.Contains(eventType)) return Outcome<EventPage>.Invalid([$"Unknown event type '{eventType}'."]);
        limit = Math.Clamp(limit, 1, MaxLimit);

        var filter = AccessPolicy.BuildReadFilter(subject, "o");
        await using var cmd = dataSource.CreateCommand(
            $"""
            SELECT {OutboxRow.Columns}, {MarkingColumns.Select("o")}, o.created_in_tx::text
            FROM integration.outbox o
            WHERE {Visible} AND {filter.Sql}
              AND (o.created_in_tx, o.seq) > (@cx::xid8, @cs)
              AND (@type::text IS NULL OR o.event_type = @type)
              AND (@agg::text IS NULL OR o.aggregate_number = @agg)
            ORDER BY o.created_in_tx, o.seq
            LIMIT @limit
            """);
        foreach (var p in filter.Parameters) cmd.Parameters.AddWithValue(p.Key, p.Value);
        cmd.Parameters.AddWithValue("cx", xid.ToString(CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("cs", seq);
        cmd.Parameters.Add(new NpgsqlParameter("type", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)eventType ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("agg", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)aggregateNumber ?? DBNull.Value });
        cmd.Parameters.AddWithValue("limit", limit + 1);

        var events = new List<PulledEvent>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var row = OutboxRow.Read(r);
                events.Add(new PulledEvent($"{r.GetString(22)}:{row.Seq.ToString(CultureInfo.InvariantCulture)}", Envelope.ToJson(row)));
            }
        var more = events.Count > limit;
        if (more) events.RemoveAt(events.Count - 1);
        await audit.AppendAsync(IntegrationAudit.Entry(subject, "integration.pull", AuditOutcome.Success, null, correlationId,
            ("count", events.Count.ToString(CultureInfo.InvariantCulture)), ("type", eventType), ("aggregate", aggregateNumber)), ct);
        return Outcome<EventPage>.Ok(new EventPage(events, events.Count == 0 ? cursor : events[^1].Cursor));
    }

    private static bool TryParseCursor(string? cursor, out long xid, out long seq)
    {
        xid = 0;
        seq = 0;
        if (string.IsNullOrEmpty(cursor)) return true;
        var parts = cursor.Split(':');
        return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out xid) && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out seq);
    }

    /// <summary>Administrator's outbox viewer, newest first. Unreadable events show only type, time, banner and delivery counts ("restricted event").</summary>
    public async Task<Outcome<IReadOnlyList<OutboxListItem>>> ListForAdminAsync(Subject subject, string? eventType, string? aggregateNumber, long? beforeSeq, int limit, string? correlationId, CancellationToken ct = default)
    {
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Administer))
        {
            await audit.AppendAsync(IntegrationAudit.Entry(subject, "integration.outbox.list", AuditOutcome.Denied, null, correlationId, ("reasons", AccessPolicy.RoleMissing)), ct);
            return Outcome<IReadOnlyList<OutboxListItem>>.Forbidden([AccessPolicy.RoleMissing]);
        }
        var filter = AccessPolicy.BuildReadFilter(subject, "o");
        await using var cmd = dataSource.CreateCommand(
            $"""
            SELECT o.id, o.seq, o.event_type, o.event_version, o.occurred_at, ({filter.Sql}) AS readable,
                   CASE WHEN {filter.Sql} THEN o.aggregate_type END, CASE WHEN {filter.Sql} THEN o.aggregate_number END, CASE WHEN {filter.Sql} THEN o.aggregate_revision END,
                   CASE WHEN {filter.Sql} THEN o.payload::text END,
                   o.correlation_id, o.actor, {MarkingColumns.Select("o")},
                   (SELECT jsonb_object_agg(x.status, x.n) FROM (SELECT d.status, count(*) AS n FROM integration.delivery d WHERE d.event_id = o.id GROUP BY d.status) x)::text
            FROM integration.outbox o
            WHERE (@type::text IS NULL OR o.event_type = @type) AND (@before::bigint IS NULL OR o.seq < @before)
              AND (@agg::text IS NULL OR ({filter.Sql} AND o.aggregate_number = @agg))
            ORDER BY o.seq DESC LIMIT @limit
            """);
        foreach (var p in filter.Parameters) cmd.Parameters.AddWithValue(p.Key, p.Value);
        cmd.Parameters.Add(new NpgsqlParameter("type", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)eventType ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("agg", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)aggregateNumber ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("before", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)beforeSeq ?? DBNull.Value });
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));

        var items = new List<OutboxListItem>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var markings = MarkingColumns.Read(r, 12);
                var readable = r.GetBoolean(5);
                var counts = r.IsDBNull(22) ? new Dictionary<string, int>() : JsonNode.Parse(r.GetString(22))!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<int>());
                JsonObject? envelope = null;
                if (readable)
                {
                    var row = new OutboxRow(r.GetGuid(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetString(6), null, r.GetString(7),
                        r.IsDBNull(8) ? null : r.GetString(8), r.GetFieldValue<DateTimeOffset>(4), r.GetString(9), markings, r.IsDBNull(10) ? null : r.GetString(10), r.GetString(11));
                    envelope = Envelope.ToJson(row);
                }
                items.Add(new OutboxListItem(r.GetGuid(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetFieldValue<DateTimeOffset>(4), markings.BannerText(), readable,
                    readable ? r.GetString(6) : null, readable ? r.GetString(7) : null, readable && !r.IsDBNull(8) ? r.GetString(8) : null, envelope, counts));
            }
        await audit.AppendAsync(IntegrationAudit.Entry(subject, "integration.outbox.list", AuditOutcome.Success, null, correlationId,
            ("count", items.Count.ToString(CultureInfo.InvariantCulture)), ("unreadable", items.Count(i => !i.Readable).ToString(CultureInfo.InvariantCulture))), ct);
        return Outcome<IReadOnlyList<OutboxListItem>>.Ok(items);
    }
}
