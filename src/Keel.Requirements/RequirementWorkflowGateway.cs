using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Npgsql;
using static Keel.Requirements.RequirementStore;

namespace Keel.Requirements;

/// <summary>
/// Lets the workflow engine review and release a requirement specification like any other object (ADR-0018, ADR-0038). Submitting binds the digest of every requirement of the iteration;
/// the signatures cover a digest recomputed from the live rows, so a row edited after signing makes verification fail and the release refuse. The signed text carries counts, never any
/// requirement text. All methods run in the engine's transaction.
/// </summary>
internal sealed class RequirementWorkflowGateway(IAuditLog audit, INotificationSink? notifications = null) : IWorkflowObjectGateway
{
    public const string ContentFormat = "reqspec-content/1";
    public string ObjectType => RequirementStore.ObjectType;

    private static async Task<WorkflowObject> MapAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SpecHead h, CancellationToken ct)
    {
        var lifecycle = await Lifecycle.LoadAsync(conn, tx, h.LifecycleId, ct);
        return new WorkflowObject(RequirementStore.ObjectType, h.Id, h.Number, h.Name, h.Revision, h.RevisionId, h.IterationId, h.Label, h.State, lifecycle?.State(h.State)?.Label ?? h.State,
            h.State == SpecStates.UnderReview, h.IsLatest, null, h.Markings);
    }

    public async Task<WorkflowObject?> FindByKeyAsync(DbConnection conn, DbTransaction tx, string key, bool forUpdate, CancellationToken ct)
    {
        var head = await LoadSpecAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, key, null, forUpdate, ct);
        return head is null ? null : await MapAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, head, ct);
    }

    public async Task<WorkflowObject?> FindByRevisionAsync(DbConnection conn, DbTransaction tx, Guid revisionId, bool forUpdate, CancellationToken ct)
    {
        var head = await LoadSpecByRevisionAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, revisionId, forUpdate, ct);
        return head is null ? null : await MapAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, head, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, WorkflowObject>> FindManyByRevisionAsync(DbConnection conn, DbTransaction tx, IReadOnlyCollection<Guid> revisionIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, WorkflowObject>();
        foreach (var id in revisionIds)
            if (await FindByRevisionAsync(conn, tx, id, false, ct) is { } o) result[id] = o;
        return result;
    }

    private static async Task<GatewayResult> MoveAsync(NpgsqlConnection c, NpgsqlTransaction t, SpecHead head, string from, string to, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var lifecycle = await LifecycleAsync(c, t, head.LifecycleId, ct);
        var transition = lifecycle.Transitions.FirstOrDefault(x => x.From == from && x.To == to);
        if (transition is null || transition.Via != "workflow") return GatewayResult.Fail($"The specification lifecycle has no workflow transition from {from} to {to}.");
        await using var cmd = new NpgsqlCommand("UPDATE req.spec_revision SET lifecycle_state = @to, state_changed_at = @at, state_changed_by = @by WHERE id = @rid AND lifecycle_state = @from", c, t);
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("at", now);
        cmd.Parameters.AddWithValue("by", Who(actor));
        cmd.Parameters.AddWithValue("rid", head.RevisionId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1 ? GatewayResult.Ok : GatewayResult.Fail("The specification changed state concurrently.");
    }

    public async Task<GatewayResult> BeginReviewAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn;
        var t = (NpgsqlTransaction)tx;
        var head = await LoadSpecByRevisionAsync(c, t, obj.RevisionId, true, ct);
        if (head is null) return GatewayResult.Fail("The specification no longer exists.");
        if (!obj.IsLatestRevision) return GatewayResult.Fail($"Revision {head.Revision} is not the latest revision.");
        if (head.State != SpecStates.InWork) return GatewayResult.Fail($"Revision {head.Revision} is {obj.StateLabel}; only a specification In Work can be submitted for review.");
        var work = await LoadWorkAsync(c, t, head.IterationId, ct);
        if (work.Count == 0) return GatewayResult.Fail("A specification with no requirements cannot be submitted for review.");
        if (work.Any(w => w.Status == ReqStatuses.Draft)) return GatewayResult.Fail("One or more requirements are still Draft; approve, defer or withdraw them before the review.");
        var live = HashWork(work);
        if (live != head.IterationSha) return GatewayResult.Fail($"The stored content of {head.Number} {head.Label} no longer matches its recorded digest; it cannot be submitted for review.");
        var review = RequirementRules.ReviewHash(live);
        await using (var ins = new NpgsqlCommand("INSERT INTO req.review_binding (id, revision_id, iteration_id, review_sha256, bound_at, bound_by) VALUES (@id, @r, @i, @sha, @at, @by)", c, t))
        {
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("r", head.RevisionId);
            ins.Parameters.AddWithValue("i", head.IterationId);
            ins.Parameters.AddWithValue("sha", review);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", Who(actor));
            await ins.ExecuteNonQueryAsync(ct);
        }
        var moved = await MoveAsync(c, t, head, SpecStates.InWork, SpecStates.UnderReview, actor, now, ct);
        if (!moved.IsOk) return moved;
        await audit.AppendAsync(t, Entry(actor, "requirement.submit", AuditOutcome.Success, head.Number, null, ("revision", head.Label), ("requirements", Fmt(work.Count)), ("contentSha256", live), ("reviewSha256", review)), ct);
        return GatewayResult.Ok;
    }

    public async Task<GatewayResult> CompleteReleaseAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Guid boundIterationId, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn;
        var t = (NpgsqlTransaction)tx;
        var head = await LoadSpecByRevisionAsync(c, t, obj.RevisionId, true, ct);
        if (head is null) return GatewayResult.Fail("The specification no longer exists.");
        if (head.State != SpecStates.UnderReview) return GatewayResult.Fail($"Revision {head.Revision} is {obj.StateLabel}, not Under Review; it cannot be released by this workflow.");
        if (head.IterationId != boundIterationId) return GatewayResult.Fail("The specification content changed after the review started; the workflow no longer covers the latest iteration.");
        string? bound = null;
        await using (var cmd = new NpgsqlCommand("SELECT review_sha256 FROM req.review_binding WHERE revision_id = @r AND iteration_id = @i ORDER BY bound_at DESC, id DESC LIMIT 1", c, t))
        {
            cmd.Parameters.AddWithValue("r", head.RevisionId);
            cmd.Parameters.AddWithValue("i", head.IterationId);
            bound = await cmd.ExecuteScalarAsync(ct) as string;
        }
        if (bound is null) return GatewayResult.Fail($"{head.Number} was never bound to a review; it cannot be released.");
        var live = HashWork(await LoadWorkAsync(c, t, head.IterationId, ct));
        if (live != head.IterationSha || RequirementRules.ReviewHash(live) != bound)
            return GatewayResult.Fail($"The content of {head.Number} does not match what was reviewed and signed; it cannot be released.");
        var moved = await MoveAsync(c, t, head, SpecStates.UnderReview, SpecStates.Released, actor, now, ct);
        if (!moved.IsOk) return moved;
        await using (var sup = new NpgsqlCommand(
            "UPDATE req.spec_revision SET lifecycle_state = 'Obsolete', state_changed_at = @at, state_changed_by = @by, state_reason = @reason WHERE spec_id = @s AND lifecycle_state = 'Released' AND id <> @rid", c, t))
        {
            sup.Parameters.AddWithValue("at", now);
            sup.Parameters.AddWithValue("by", Who(actor));
            sup.Parameters.AddWithValue("reason", $"Superseded by revision {head.Revision}");
            sup.Parameters.AddWithValue("s", head.Id);
            sup.Parameters.AddWithValue("rid", head.RevisionId);
            await sup.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(t, Entry(actor, "requirement.release", AuditOutcome.Success, head.Number, null, ("revision", head.Label), ("contentSha256", live), ("reviewSha256", bound)), ct);
        if (notifications is not null)
            await notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.WatchedReleased, AwarenessKinds.ReqSpec, head.Number, head.Markings, actor.Id), ct);
        return GatewayResult.Ok;
    }

    public async Task<GatewayResult> ReturnToInWorkAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn;
        var t = (NpgsqlTransaction)tx;
        var head = await LoadSpecByRevisionAsync(c, t, obj.RevisionId, true, ct);
        if (head is null) return GatewayResult.Fail("The specification no longer exists.");
        if (head.State != SpecStates.UnderReview) return GatewayResult.Fail($"Revision {head.Revision} is {obj.StateLabel}, not Under Review.");
        var moved = await MoveAsync(c, t, head, SpecStates.UnderReview, SpecStates.InWork, actor, now, ct);
        if (!moved.IsOk) return moved;
        await audit.AppendAsync(t, Entry(actor, "requirement.review_cancel", AuditOutcome.Success, head.Number, null, ("revision", head.Label)), ct);
        return GatewayResult.Ok;
    }

    public async Task<IterationContent?> LoadIterationContentAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn;
        var t = (NpgsqlTransaction)tx;
        var head = await LoadSpecByIterationAsync(c, t, iterationId, ct);
        if (head is null) return null;
        // The live rows, not the stored digest: editing a row after signing changes what verification computes.
        await using var iter = new NpgsqlCommand("SELECT iteration FROM req.spec_iteration WHERE id = @i", c, t);
        iter.Parameters.AddWithValue("i", iterationId);
        var iteration = Convert.ToInt32(await iter.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        var work = await LoadWorkAsync(c, t, iterationId, ct);
        var live = HashWork(work);
        var label = $"{head.Revision}.{Fmt(iteration)}";
        var description = new StringBuilder()
            .AppendLine($"{head.Number} (requirement specification) version {label}")
            .AppendLine($"Requirements: {Fmt(work.Count)} ({string.Join(", ", ReqTypes.All.Select(ty => $"{Fmt(work.Count(w => w.Type == ty))} {ty}"))})")
            .AppendLine($"Content digest: {live}")
            .Append($"Markings: {head.Markings.BannerText()}")
            .ToString();
        var attributes = new JsonObject { ["format"] = ContentFormat, ["spec"] = head.Number, ["requirements"] = work.Count, ["contentSha256"] = live };
        return new IterationContent(iterationId, ObjectType, head.Number, head.Revision, label, description, attributes.ToJsonString());
    }
}
