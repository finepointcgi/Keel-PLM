using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Npgsql;

namespace Keel.Bom;

/// <summary>
/// Lets the workflow engine (Keel.Workflow, which must not reference this module) review a baseline like any other object (ADR-0018, ADR-0021):
/// a program milestone (PDR, CDR, FCA, PCA) is established by signatures. A baseline is immutable, so the engine's "revision" and "iteration" are
/// the baseline itself; the signed content is a readable summary plus the content hash <em>recomputed from the live rows</em>, so any change to
/// an item after signing makes verification fail. Establishment is atomic with the final signature. All methods run in the engine's transaction.
/// </summary>
internal sealed class BaselineWorkflowGateway(IAuditLog audit) : IWorkflowObjectGateway
{
    public const string ContentFormat = "baseline-content/1";

    public string ObjectType => BaselineService.ObjectType;

    private static WorkflowObject Map(BaselineHead h, string label) =>
        new(BaselineService.ObjectType, h.Id, h.Number, h.Name, "1", h.Id, h.Id, h.Number, h.State, label, h.State == BaselineStates.UnderReview, true, null, h.Markings);

    public async Task<WorkflowObject?> FindByKeyAsync(DbConnection conn, DbTransaction tx, string key, bool forUpdate, CancellationToken ct)
    {
        var head = await BaselineStore.LoadByNumberAsync(conn, tx, key, forUpdate, ct);
        return head is null ? null : Map(head, await BaselineStore.StateLabelAsync(conn, tx, head.State, ct));
    }

    public async Task<WorkflowObject?> FindByRevisionAsync(DbConnection conn, DbTransaction tx, Guid revisionId, bool forUpdate, CancellationToken ct)
    {
        var head = await BaselineStore.LoadByIdAsync(conn, tx, revisionId, forUpdate, ct);
        return head is null ? null : Map(head, await BaselineStore.StateLabelAsync(conn, tx, head.State, ct));
    }

    public async Task<IReadOnlyDictionary<Guid, WorkflowObject>> FindManyByRevisionAsync(DbConnection conn, DbTransaction tx, IReadOnlyCollection<Guid> revisionIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, WorkflowObject>();
        foreach (var id in revisionIds)
            if (await FindByRevisionAsync(conn, tx, id, false, ct) is { } o) result[id] = o;
        return result;
    }

    public async Task<GatewayResult> BeginReviewAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var head = await BaselineStore.LoadByIdAsync(conn, tx, obj.Id, forUpdate: true, ct);
        if (head is null) return GatewayResult.Fail("The baseline no longer exists.");
        if (head.State != BaselineStates.Draft) return GatewayResult.Fail($"{head.Number} is {obj.StateLabel}; only a draft baseline can be submitted for review.");
        // The snapshot being signed must be the one that was created.
        if (await BaselineStore.RecomputeHashAsync(conn, tx, head, ct) != head.Sha256)
            return GatewayResult.Fail($"The stored content of {head.Number} no longer matches its recorded hash; it cannot be submitted for review.");
        var moved = await BaselineStore.TransitionAsync(conn, tx, head, BaselineStates.UnderReview, "workflow", BomService.DisplayId(actor), now, ct: ct);
        if (!moved.IsOk) return moved;
        await Audit(tx, actor, "baseline.submit", head, ("items", head.ItemCount.ToString(CultureInfo.InvariantCulture)), ("contentSha256", head.Sha256));
        return GatewayResult.Ok;
    }

    public async Task<GatewayResult> CompleteReleaseAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Guid boundIterationId, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var head = await BaselineStore.LoadByIdAsync(conn, tx, obj.Id, forUpdate: true, ct);
        if (head is null) return GatewayResult.Fail("The baseline no longer exists.");
        if (head.State != BaselineStates.UnderReview) return GatewayResult.Fail($"{head.Number} is {obj.StateLabel}, not under review; it cannot be established by this workflow.");
        if (boundIterationId != head.Id) return GatewayResult.Fail($"The review is not bound to {head.Number}.");
        if (await BaselineStore.RecomputeHashAsync(conn, tx, head, ct) != head.Sha256)
            return GatewayResult.Fail($"The content of {head.Number} does not match what was reviewed and signed; it cannot be established.");
        var moved = await BaselineStore.TransitionAsync(conn, tx, head, BaselineStates.Established, "workflow", BomService.DisplayId(actor), now,
            establishReason: "Established by review: every approver signed the baseline.", ct: ct);
        if (!moved.IsOk) return moved;
        await Audit(tx, actor, "baseline.establish", head, ("path", "workflow"), ("contentSha256", head.Sha256));
        return GatewayResult.Ok;
    }

    public async Task<GatewayResult> ReturnToInWorkAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var head = await BaselineStore.LoadByIdAsync(conn, tx, obj.Id, forUpdate: true, ct);
        if (head is null) return GatewayResult.Fail("The baseline no longer exists.");
        if (head.State != BaselineStates.UnderReview) return GatewayResult.Fail($"{head.Number} is {obj.StateLabel}, not under review.");
        var moved = await BaselineStore.TransitionAsync(conn, tx, head, BaselineStates.Draft, "workflow", BomService.DisplayId(actor), now, ct: ct);
        if (!moved.IsOk) return moved;
        await Audit(tx, actor, "baseline.review_cancel", head);
        return GatewayResult.Ok;
    }

    public async Task<IterationContent?> LoadIterationContentAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        var head = await BaselineStore.LoadByIdAsync(conn, tx, iterationId, forUpdate: false, ct);
        if (head is null) return null;
        string rootText;
        await using (var cmd = new NpgsqlCommand(
            "SELECT m.number || ' ' || r.revision || '.' || i.iteration FROM plm.part_master m JOIN plm.part_revision r ON r.master_id = m.id JOIN plm.part_iteration i ON i.revision_id = r.id WHERE m.id = @m AND r.id = @r AND i.id = @i",
            (NpgsqlConnection)conn, (NpgsqlTransaction)tx))
        {
            cmd.Parameters.AddWithValue("m", head.RootMasterId);
            cmd.Parameters.AddWithValue("r", head.RootRevisionId);
            cmd.Parameters.AddWithValue("i", head.RootIterationId);
            rootText = (string?)await cmd.ExecuteScalarAsync(ct) ?? "unknown";
        }
        // The live hash, not the stored one: editing a row after signing changes what verification computes.
        var live = await BaselineStore.RecomputeHashAsync(conn, tx, head, ct);
        var description = new StringBuilder()
            .AppendLine($"{head.Number} ({head.Type} baseline): {head.Name}")
            .AppendLine($"Root: {rootText}")
            .AppendLine($"Configuration: {head.SpecText}")
            .AppendLine($"Items: {head.ItemCount.ToString(CultureInfo.InvariantCulture)}; pinned documents: {head.DocumentCount.ToString(CultureInfo.InvariantCulture)}")
            .AppendLine($"Markings: {head.Markings.BannerText()}")
            .Append(head.Milestone is null ? "" : $"Milestone: {head.Milestone}\n")
            .Append(head.Purpose.Length == 0 ? "" : $"Purpose: {head.Purpose}\n")
            .ToString().TrimEnd();
        var attributes = new JsonObject
        {
            ["format"] = ContentFormat,
            ["root"] = rootText,
            ["spec"] = head.Spec,
            ["releasedOnly"] = head.ReleasedOnly,
            ["items"] = head.ItemCount,
            ["documents"] = head.DocumentCount,
            ["contentSha256"] = live,
        };
        return new IterationContent(iterationId, ObjectType, head.Number, "1", head.Number, description, attributes.ToJsonString());
    }

    private Task<AuditRecord> Audit(DbTransaction tx, Subject actor, string action, BaselineHead head, params (string, string?)[] details) =>
        audit.AppendAsync((NpgsqlTransaction)tx, BaselineService.Entry(actor, action, AuditOutcome.Success, head.Number, null, details));
}
