using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

/// <summary>
/// Reports the release of an MBOM revision to the integration outbox, in the transaction of the workflow completion that released it (ADR-0025, FUP-1).
/// The event carries the MBOM's own markings. A line whose child part the MBOM's markings do not cover is written as an explicit restricted placeholder
/// (find number, alternate, quantity and unit alone, A4/INT-7) and counted; nothing about the child, its EBOM links or its child MBOM is disclosed. An MBOM line
/// has no subtree of its own (a Make child points at another MBOM by number), so a placeholder omits that reference too. This is a system read inside the
/// releasing transaction; the event's markings and the outbound release check decide which sinks receive it.
/// </summary>
internal static class MbomEvents
{
    public static async Task EmitReleasedAsync(
        IDomainEventSink? sink, DbConnection conn, DbTransaction tx, MbomHead head, string contentSha256, Guid runId, string runStatus, string runSha256,
        Subject actor, DateTimeOffset now, string? correlationId, CancellationToken ct)
    {
        if (sink is null) return;
        var c = (NpgsqlConnection)conn;
        var t = (NpgsqlTransaction)tx;

        Guid? derived;
        string? derivedSpec;
        await using (var cmd = new NpgsqlCommand("SELECT derived_part_iteration_id, derived_spec FROM mbom.iteration WHERE id = @i", c, t))
        {
            cmd.Parameters.AddWithValue("i", head.IterationId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            (derived, derivedSpec) = (r.IsDBNull(0) ? null : r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1));
        }
        var derivedLabel = derived is { } d ? await MbomStore.IterationLabelAsync(c, t, d, ct) : null;

        var content = await MbomStore.ReadContentAsync(c, t, false, head.IterationId, ct);
        var ordered = MbomStore.WithAlternates(content).ToList();

        // Find numbers of the EBOM lines the links name (they belong to the iteration this MBOM was derived from).
        var ebomIds = content.SelectMany(l => l.Links).Select(k => k.EbomUsageId).Distinct().ToArray();
        var ebomFind = new Dictionary<Guid, int>();
        if (ebomIds.Length > 0)
        {
            await using var cmd = new NpgsqlCommand("SELECT id, find_number FROM plm.bom_usage WHERE id = ANY(@ids)", c, t);
            cmd.Parameters.AddWithValue("ids", ebomIds);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ebomFind[r.GetGuid(0)] = r.GetInt32(1);
        }

        var parts = new Dictionary<Guid, PartRef?>();
        var lines = new JsonArray();
        var restricted = 0;
        foreach (var (l, alt) in ordered)
        {
            if (!parts.TryGetValue(l.Child, out var child)) parts[l.Child] = child = await MbomStore.LoadPartAsync(c, t, l.Child, ct);
            var line = new JsonObject { ["findNumber"] = l.Find, ["alternate"] = alt, ["quantity"] = l.Quantity, ["unit"] = l.Unit };
            if (child is null || !MarkingDominance.Covers(head.Markings, child.Markings))
            {
                line["restricted"] = true;
                restricted++;
                lines.Add(line);
                continue;
            }
            line["restricted"] = false;
            line["child"] = new JsonObject
            {
                ["partId"] = child.Id.ToString("D"), ["number"] = child.Number, ["name"] = child.Name, ["partType"] = child.PartType, ["source"] = child.Source,
            };
            line["scrapFactor"] = l.Scrap;
            line["phantom"] = l.Phantom;
            line["supplyType"] = l.SupplyType;
            line["operationReference"] = l.OperationRef;
            line["mbomOnly"] = l.MbomOnly;
            line["referenceDesignators"] = new JsonArray([.. l.Designators.Select(x => (JsonNode?)x)]);
            line["notes"] = l.Notes;
            line["effectivity"] = l.Eff?.Describe();
            line["ebomLinks"] = new JsonArray([.. l.Links.Where(k => ebomFind.ContainsKey(k.EbomUsageId)).OrderBy(k => ebomFind[k.EbomUsageId]).ThenBy(k => k.Status, StringComparer.Ordinal)
                .Select(k => (JsonNode?)new JsonObject { ["ebomFindNumber"] = ebomFind[k.EbomUsageId], ["status"] = k.Status, ["reason"] = k.Reason })]);
            line["childMbom"] = await ChildMbomAsync(c, t, head, l, child, ct);
            lines.Add(line);
        }

        var payload = new JsonObject
        {
            ["mbom"] = new JsonObject
            {
                ["mbomId"] = head.Id.ToString("D"), ["number"] = head.Number, ["plant"] = head.Plant, ["revision"] = head.Revision,
                ["iteration"] = head.Label, ["state"] = MbomStates.Released,
            },
            ["part"] = new JsonObject { ["partId"] = head.PartMasterId.ToString("D"), ["number"] = head.PartNumber, ["name"] = head.PartName },
            ["derivedFrom"] = new JsonObject { ["ebomIteration"] = derivedLabel, ["spec"] = derivedSpec },
            ["contentSha256"] = contentSha256,
            ["reconciliation"] = new JsonObject { ["runId"] = runId.ToString("D"), ["status"] = runStatus, ["sha256"] = runSha256 },
            ["releasedAt"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["lineCount"] = lines.Count,
            ["restrictedLineCount"] = restricted,
            ["lines"] = lines,
        };
        await sink.AppendAsync(conn, tx, new DomainEvent(EventTypes.MbomReleased, MbomService.ObjectType, head.Id, head.Number, head.Revision, head.Markings, payload, now, actor.Username ?? actor.Id, correlationId), ct);
    }

    /// <summary>
    /// The released MBOM of the same plant a Make sub-assembly line resolves to (what an ERP links the line to), or null. It is named only when this MBOM's markings
    /// cover the child MBOM's markings too.
    /// </summary>
    private static async Task<JsonNode?> ChildMbomAsync(NpgsqlConnection c, NpgsqlTransaction t, MbomHead head, NewMbomLine line, PartRef child, CancellationToken ct)
    {
        if (line.SupplyType != "Make" || child.PartType != "Assembly") return null;
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT mm.number, r.revision, {MarkingColumns.Select("mm")}
            FROM mbom.mbom mm JOIN mbom.revision r ON r.mbom_id = mm.id AND r.lifecycle_state = 'Released'
            WHERE mm.part_master_id = @pm AND mm.plant = @plant
            ORDER BY length(r.revision) DESC, r.revision DESC LIMIT 1
            """, c, t);
        cmd.Parameters.AddWithValue("pm", child.Id);
        cmd.Parameters.AddWithValue("plant", head.Plant);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var markings = MarkingColumns.Read(r, 2);
        return MarkingDominance.Covers(head.Markings, markings) ? new JsonObject { ["number"] = r.GetString(0), ["revision"] = r.GetString(1) } : null;
    }
}
