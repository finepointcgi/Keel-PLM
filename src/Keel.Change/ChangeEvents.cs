using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Core;

namespace Keel.Change;

/// <summary>Reports an approved change request and a released change notice to the integration outbox (ADR-0025), in the transaction of the approval or release.</summary>
internal static class ChangeEvents
{
    public static Task EmitAsync(IDomainEventSink? sink, DbConnection conn, DbTransaction tx, string eventType, ChangeRow row, SnapshotData data, IReadOnlyList<string> obsoleted,
        IReadOnlyList<string> retained, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        if (sink is null) return Task.CompletedTask;
        var payload = new JsonObject
        {
            ["changeId"] = row.Id.ToString("D"),
            ["number"] = row.Number,
            ["type"] = row.Type,
            ["title"] = row.Title,
            ["description"] = row.Description,
            ["changeClass"] = row.ChangeClass,
            ["priority"] = row.Priority,
            ["reason"] = row.Reason,
            ["sourceChange"] = data.SourceNumber,
            ["submission"] = data.Submission,
            ["affected"] = new JsonArray([.. data.Affected.Select(a => (JsonNode?)new JsonObject
            {
                ["objectType"] = a.ObjectType, ["number"] = a.Key, ["name"] = a.Name, ["revision"] = a.Revision, ["action"] = a.Action, ["effectivity"] = a.Effectivity,
            })]),
        };
        if (eventType == EventTypes.ChangeReleased)
        {
            payload["resulting"] = new JsonArray([.. data.Resulting.Select(r => (JsonNode?)new JsonObject
            {
                ["objectType"] = r.ObjectType, ["number"] = r.Key, ["revision"] = r.Revision, ["iteration"] = r.IterationLabel,
            })]);
            payload["obsoleted"] = new JsonArray([.. obsoleted.Select(o => (JsonNode?)o)]);
            payload["retainedByEffectivity"] = new JsonArray([.. retained.Select(o => (JsonNode?)o)]);
            payload["releasedAt"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }
        else payload["approvedAt"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return sink.AppendAsync(conn, tx, new DomainEvent(eventType, row.Type, row.Id, row.Number, null, row.Markings, payload, now, actor.Username ?? actor.Id), ct);
    }
}
