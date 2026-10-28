using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Core;
using Npgsql;

namespace Keel.Bom;

/// <summary>Reports the establishment of a baseline to the integration outbox, in the transaction that established it (ADR-0025).</summary>
internal static class BaselineEvents
{
    public static async Task EmitEstablishedAsync(IDomainEventSink? sink, DbConnection conn, DbTransaction tx, Guid baselineId, Subject actor, string? correlationId, CancellationToken ct)
    {
        if (sink is null) return;
        var head = await BaselineStore.LoadByIdAsync(conn, tx, baselineId, forUpdate: false, ct) ?? throw new InvalidOperationException("The baseline disappeared.");
        string rootNumber, rootRevision;
        int rootIteration;
        await using (var cmd = new NpgsqlCommand(
            "SELECT m.number, r.revision, i.iteration FROM plm.part_master m JOIN plm.part_revision r ON r.master_id = m.id JOIN plm.part_iteration i ON i.revision_id = r.id WHERE m.id = @m AND r.id = @r AND i.id = @i",
            (NpgsqlConnection)conn, (NpgsqlTransaction)tx))
        {
            cmd.Parameters.AddWithValue("m", head.RootMasterId);
            cmd.Parameters.AddWithValue("r", head.RootRevisionId);
            cmd.Parameters.AddWithValue("i", head.RootIterationId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            (rootNumber, rootRevision, rootIteration) = (r.GetString(0), r.GetString(1), r.GetInt32(2));
        }
        var at = head.EstablishedAt ?? DateTimeOffset.UtcNow;
        var payload = new JsonObject
        {
            ["baselineId"] = head.Id.ToString("D"),
            ["number"] = head.Number,
            ["name"] = head.Name,
            ["type"] = head.Type,
            ["state"] = head.State,
            ["milestone"] = head.Milestone,
            ["root"] = new JsonObject { ["number"] = rootNumber, ["revision"] = rootRevision, ["iteration"] = $"{rootRevision}.{rootIteration.ToString(CultureInfo.InvariantCulture)}" },
            ["configuration"] = head.SpecText,
            ["itemCount"] = head.ItemCount,
            ["documentCount"] = head.DocumentCount,
            ["contentSha256"] = head.Sha256,
            ["establishedAt"] = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["establishReason"] = head.EstablishReason,
        };
        await sink.AppendAsync(conn, tx, new DomainEvent(EventTypes.BaselineEstablished, "baseline", head.Id, head.Number, null, head.Markings, payload, at, actor.Username ?? actor.Id, correlationId), ct);
    }
}
