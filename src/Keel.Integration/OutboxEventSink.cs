using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;
using NpgsqlTypes;

namespace Keel.Integration;

internal static class IntegrationAudit
{
    public const string ObjectType = "integration";

    public static AuditEntry Entry(ActorSnapshot actor, string action, AuditOutcome outcome, string? objectId, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = actor,
        Action = action,
        Outcome = outcome,
        ObjectType = ObjectType,
        ObjectId = objectId,
        CorrelationId = correlationId,
        Details = AuditDetails.Of(details),
    };

    public static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string? objectId, string? correlationId, params (string, string?)[] details) =>
        Entry(ActorSnapshot.From(s), action, outcome, objectId, correlationId, details);
}

/// <summary>
/// Writes domain events to <c>integration.outbox</c> in the caller's transaction and fans each event out to the enabled sinks as pending
/// deliveries, so an event exists exactly when the business change committed (transactional outbox, ADR-0011). Released assemblies also
/// produce their <c>bom.published</c> events here, so the owning modules only report what changed.
/// </summary>
public sealed class OutboxEventSink(IAuditLog audit, IntegrationOptions options, TimeProvider clock, IBomPublicationSource? bomSource = null) : IDomainEventSink
{
    public async Task AppendAsync(DbConnection conn, DbTransaction tx, DomainEvent domainEvent, CancellationToken ct)
    {
        var e = domainEvent;
        await InsertAsync(conn, tx, e, ct);
        if (e.EventType == EventTypes.PartReleased && bomSource is not null && e.Payload["partType"]?.GetValue<string>() == "Assembly" &&
            e.Payload["revisionId"]?.GetValue<string>() is { } revisionId && Guid.TryParse(revisionId, out var revision))
            await PublishBomAsync(conn, tx, e, revision, ct);
    }

    /// <summary>Inserts the outbox row and one pending delivery per enabled, subscribed sink; returns the event id.</summary>
    internal async Task<Guid> InsertAsync(DbConnection conn, DbTransaction tx, DomainEvent e, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn;
        var t = (NpgsqlTransaction)tx;
        var id = Guid.CreateVersion7();
        await using (var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO integration.outbox (id, event_type, event_version, aggregate_type, aggregate_id, aggregate_number, aggregate_revision, occurred_at, payload,
                {MarkingColumns.InsertColumns}, correlation_id, actor)
            VALUES (@id, @type, @version, @atype, @aid, @anum, @arev, @at, @payload, {MarkingColumns.InsertValues}, @corr, @actor)
            """, c, t))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("type", e.EventType);
            cmd.Parameters.AddWithValue("version", e.EventVersion);
            cmd.Parameters.AddWithValue("atype", e.AggregateType);
            cmd.Parameters.Add(new NpgsqlParameter("aid", NpgsqlDbType.Uuid) { Value = (object?)e.AggregateId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("anum", e.AggregateNumber);
            cmd.Parameters.Add(new NpgsqlParameter("arev", NpgsqlDbType.Text) { Value = (object?)e.AggregateRevision ?? DBNull.Value });
            cmd.Parameters.AddWithValue("at", e.OccurredAt);
            cmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = e.Payload.ToJsonString() });
            foreach (var p in MarkingColumns.Parameters(e.Markings)) cmd.Parameters.Add(new NpgsqlParameter(p.Key, p.Value));
            cmd.Parameters.Add(new NpgsqlParameter("corr", NpgsqlDbType.Text) { Value = (object?)e.CorrelationId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("actor", e.Actor);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var now = clock.GetUtcNow();
        await using var fan = new NpgsqlCommand(
            """
            INSERT INTO integration.delivery (id, event_id, sink_id, status, next_attempt_at, created_at, updated_at)
            SELECT gen_random_uuid(), @id, s.id, 'pending', @now, @now, @now
            FROM integration.sink s
            WHERE s.enabled
              AND CASE WHEN @type = 'bom.structure.published' THEN s.full_structure
                       ELSE cardinality(s.event_types) = 0 OR @type = ANY(s.event_types) END
            """, c, t);
        fan.Parameters.AddWithValue("id", id);
        fan.Parameters.AddWithValue("now", now);
        fan.Parameters.AddWithValue("type", e.EventType);
        await fan.ExecuteNonQueryAsync(ct);
        return id;
    }

    // ---------------------------------------------------------------- bom.published (derived from part.released of an assembly)

    private async Task PublishBomAsync(DbConnection conn, DbTransaction tx, DomainEvent released, Guid revisionId, CancellationToken ct)
    {
        var one = await bomSource!.BuildAsync(conn, tx, revisionId, 1, options.BomPublishedMaxLines, ct);
        if (one is null) return;
        if (one.TooLarge)
        {
            await SkippedAsync(tx, released, "bom.published", options.BomPublishedMaxLines, ct);
            return;
        }
        await InsertAsync(conn, tx, BuildBomEvent(EventTypes.BomPublished, one, released), ct);

        // The multi-level snapshot is built only when a sink asked for it, and never truncated: a partial BOM must not reach an ERP.
        await using var any = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM integration.sink WHERE enabled AND full_structure)", (NpgsqlConnection)conn, (NpgsqlTransaction)tx);
        if (!(bool)(await any.ExecuteScalarAsync(ct))!) return;
        var full = await bomSource.BuildAsync(conn, tx, revisionId, null, options.BomFullStructureMaxLines, ct);
        if (full is null) return;
        if (full.TooLarge)
        {
            await SkippedAsync(tx, released, EventTypes.BomStructurePublished, options.BomFullStructureMaxLines, ct);
            return;
        }
        await InsertAsync(conn, tx, BuildBomEvent(EventTypes.BomStructurePublished, full, released), ct);
    }

    private Task<AuditRecord> SkippedAsync(DbTransaction tx, DomainEvent released, string type, int limit, CancellationToken ct) =>
        audit.AppendAsync((NpgsqlTransaction)tx, IntegrationAudit.Entry(ActorSnapshot.System, "integration.event_skipped", AuditOutcome.Failure, released.AggregateNumber, released.CorrelationId,
            ("eventType", type), ("limit", limit.ToString(CultureInfo.InvariantCulture)),
            ("reason", "The structure is larger than the configured limit; a partial structure is never published. Use the pull API for this assembly.")), ct);

    /// <summary>
    /// The event carries the assembly's markings. A line whose child markings the assembly's markings do not cover would show it to readers
    /// (and sinks) cleared for the assembly only, so it is written as a placeholder with find number, quantity and unit alone (A4), and
    /// the subtree under it is left out. The placeholder is explicit (<c>restricted</c>), counted, and never silently dropped.
    /// </summary>
    internal static DomainEvent BuildBomEvent(string type, PublishedBom bom, DomainEvent released)
    {
        var lines = new JsonArray();
        var restrictedPaths = new List<string>();
        var restricted = 0;
        foreach (var l in bom.Lines)
        {
            if (restrictedPaths.Any(p => l.Path.StartsWith(p + ".", StringComparison.Ordinal))) continue;
            var covered = MarkingDominance.Covers(bom.AssemblyMarkings, l.ChildMarkings);
            var line = new JsonObject();
            if (type == EventTypes.BomStructurePublished) { line["level"] = l.Level; line["path"] = l.Path; }
            line["findNumber"] = l.FindNumber;
            line["alternate"] = l.Alternate;
            line["quantity"] = l.Quantity;
            line["unit"] = l.Unit;
            if (!covered)
            {
                line["restricted"] = true;
                restricted++;
                restrictedPaths.Add(l.Path);
                lines.Add(line);
                continue;
            }
            line["restricted"] = false;
            if (type == EventTypes.BomStructurePublished) line["extendedQuantity"] = l.ExtendedQuantity;
            line["referenceDesignators"] = new JsonArray([.. l.ReferenceDesignators.Select(d => (JsonNode?)d)]);
            line["notes"] = l.Notes;
            line["effectivity"] = l.Effectivity;
            line["child"] = new JsonObject
            {
                ["partId"] = l.ChildMasterId.ToString("D"),
                ["number"] = l.ChildNumber,
                ["name"] = l.ChildName,
                ["partType"] = l.ChildPartType,
                ["revision"] = l.ChildRevision,
                ["iteration"] = l.ChildIteration,
                ["state"] = l.ChildState,
                ["unresolved"] = l.Unresolved,
            };
            lines.Add(line);
        }
        var payload = new JsonObject
        {
            ["assembly"] = new JsonObject
            {
                ["partId"] = bom.AssemblyMasterId.ToString("D"),
                ["revisionId"] = bom.AssemblyRevisionId.ToString("D"),
                ["number"] = bom.Number,
                ["name"] = bom.Name,
                ["revision"] = bom.Revision,
                ["iteration"] = $"{bom.Revision}.{bom.Iteration.ToString(CultureInfo.InvariantCulture)}",
            },
            ["spec"] = "LatestReleased",
            ["levels"] = type == EventTypes.BomStructurePublished ? "all" : "one",
            ["lineCount"] = lines.Count,
            ["restrictedLineCount"] = restricted,
            ["lines"] = lines,
        };
        return new DomainEvent(type, "part", bom.AssemblyMasterId, bom.Number, bom.Revision, bom.AssemblyMarkings, payload, released.OccurredAt, released.Actor, released.CorrelationId);
    }
}
