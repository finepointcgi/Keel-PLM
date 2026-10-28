using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Parts;

/// <summary>
/// Reports part revision releases and obsolescence to the integration outbox (ADR-0025), in the transaction of the state change that caused
/// them. The payload is the released content of the revision (what an ERP material master needs); the event carries the part's markings.
/// </summary>
internal static class PartEvents
{
    public const string ViaWorkflow = "workflow";
    public const string ViaChange = "change";
    public const string ViaAdmin = "admin";

    public static async Task EmitAsync(IDomainEventSink? sink, DbConnection conn, DbTransaction tx, Guid revisionId, string eventType, string via, Subject actor, DateTimeOffset now, string? correlationId, CancellationToken ct)
    {
        if (sink is null) return;
        Guid masterId;
        string number, revision, state;
        JsonObject payload;
        Markings markings;
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT m.id, m.number, m.name, m.part_type, m.source, m.default_unit, r.revision, r.latest_iteration, r.lifecycle_state, i.description, i.attributes::text,
                   r.state_changed_by, {MarkingColumns.Select("m")}
            FROM plm.part_revision r
            JOIN plm.part_master m ON m.id = r.master_id
            JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            WHERE r.id = @rid
            """, (NpgsqlConnection)conn, (NpgsqlTransaction)tx))
        {
            cmd.Parameters.AddWithValue("rid", revisionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new InvalidOperationException("The part revision disappeared before its event was written.");
            (masterId, number, revision, state) = (r.GetGuid(0), r.GetString(1), r.GetString(6), r.GetString(8));
            markings = MarkingColumns.Read(r, 12);
            payload = new JsonObject
            {
                ["partId"] = masterId.ToString("D"),
                ["revisionId"] = revisionId.ToString("D"),
                ["number"] = number,
                ["name"] = r.GetString(2),
                ["partType"] = r.GetString(3),
                ["source"] = r.GetString(4),
                ["defaultUnit"] = r.GetString(5),
                ["revision"] = revision,
                ["iteration"] = $"{revision}.{r.GetInt32(7).ToString(CultureInfo.InvariantCulture)}",
                ["state"] = state,
                ["description"] = r.GetString(9),
                ["attributes"] = JsonNode.Parse(r.GetString(10)),
                ["via"] = via,
                ["stateChangedBy"] = r.GetString(11),
            };
        }
        await sink.AppendAsync(conn, tx, new DomainEvent(eventType, PartService.ObjectType, masterId, number, revision, markings, payload, now, actor.Username ?? actor.Id, correlationId), ct);
    }
}

/// <summary>Part data for Technical Data Packages, read through <see cref="PartService"/> so access is evaluated and audited as for any user.</summary>
public sealed class PartTdpSource(PartService parts) : ITdpPartSource
{
    public async Task<Outcome<TdpPart>> GetAsync(Subject subject, string number, string? revision, string? correlationId, CancellationToken ct)
    {
        var o = await parts.GetAsync(subject, number, revision, correlationId, ct);
        if (!o.IsOk) return new Outcome<TdpPart>(o.Kind, default, o.Errors);
        var p = o.Value!;
        return Outcome<TdpPart>.Ok(new TdpPart(p.Id, p.Revision.Id, p.Number, p.Name, p.PartType, p.Source, p.DefaultUnit, p.Revision.Revision, p.Iteration.Label, p.Revision.StateLabel,
            p.Iteration.Description, p.Iteration.Attributes.ToJsonString(), p.Markings));
    }
}
