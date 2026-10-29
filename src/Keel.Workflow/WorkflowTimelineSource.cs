using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Workflow;

public sealed partial class WorkflowService
{
    /// <summary>
    /// Review entries of one object for its timeline: when a review was submitted, every signature (who signed, with which meaning and decision) and how the
    /// review ended. Not accessible when the caller may not read the object. Read-only; the timeline page records its own audit event.
    /// </summary>
    internal async Task<TimelineSlice> TimelineAsync(Subject subject, string objectType, string key, int rank, CancellationToken ct)
    {
        var gateway = GatewayFor(objectType);
        if (gateway is null) return TimelineSlice.NotAccessible;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var obj = await gateway.FindByKeyAsync(conn, tx, key, forUpdate: false, ct);
        if (obj is null || !AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Read).Permitted) return TimelineSlice.NotAccessible;

        var ids = new List<Guid>();
        await using (var cmd = new NpgsqlCommand("SELECT i.id FROM wf.instance i WHERE i.object_id = @o AND i.object_type = @t ORDER BY i.started_at, i.id", conn, tx))
        {
            cmd.Parameters.AddWithValue("t", objectType);
            cmd.Parameters.AddWithValue("o", obj.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add(r.GetGuid(0));
        }

        var events = new List<TimelineEvent>();
        long order = 0;
        foreach (var id in ids)
        {
            var inst = (await WorkflowStore.LoadInstanceAsync(conn, tx, id, forUpdate: false, ct))!;
            var href = $"workflows/{id:D}";
            events.Add(new TimelineEvent(inst.StartedAt, TimelineKinds.Lifecycle, $"Submitted for review ({inst.IterationLabel})", inst.Comment, inst.StartedByName,
                inst.ObjectRevision, href, false, rank, order++));
            foreach (var s in await WorkflowStore.LoadSignaturesAsync(conn, tx, id, ct))
                events.Add(new TimelineEvent(s.SignedAt, TimelineKinds.Signature, $"{s.SignerName} signed: {s.MeaningLabel}",
                    $"{(s.Decision == "approve" ? "Approved" : "Rejected")} version {s.IterationLabel}{(string.IsNullOrEmpty(s.Comment) ? "" : $" · {s.Comment}")}",
                    s.SignerName, s.Revision, href, false, rank, order++));
            if (inst.CompletedAt is { } done)
                events.Add(new TimelineEvent(done, TimelineKinds.Lifecycle,
                    inst.Status switch { InstanceStatus.Approved => "Review approved", InstanceStatus.Rejected => "Review rejected", _ => "Review cancelled" },
                    inst.CompletionNote, null, inst.ObjectRevision, href, false, rank, order++));
        }
        return TimelineSlice.Of(events);
    }
}

/// <summary>Timeline source of the workflow module: reviews and signatures of parts, documents, change notices, baselines and manufacturing BOMs.</summary>
public sealed class WorkflowTimelineSource(WorkflowService workflow) : ITimelineSource
{
    public const int RankValue = 30;
    public int Rank => RankValue;
    public bool Handles(string objectType) => objectType is "part" or "document" or "mbom";

    public Task<TimelineSlice> GetAsync(Subject subject, string objectType, string key, string? correlationId, CancellationToken ct) =>
        workflow.TimelineAsync(subject, objectType, key, Rank, ct);
}
