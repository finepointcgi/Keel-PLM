using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Npgsql;

namespace Keel.Process;

/// <summary>
/// Lets the workflow engine review and release a process plan like any other object (ADR-0018, ADR-0042). Submitting needs a checked-in iteration, a plan that is still current with the
/// released MBOM, and no errors in the findings; the submission binds the content digest and the MBOM revision. The signatures cover a digest recomputed from the stored content, so a
/// changed row after signing makes verification fail and the release refuse. All methods run in the engine's transaction.
/// </summary>
internal sealed class ProcessWorkflowGateway(IAuditLog audit, ProcessPlanService plans, IMbomProcessSource mboms, INotificationSink? notifications = null) : IWorkflowObjectGateway
{
    public const string ContentFormat = "process-plan-content/1";

    public string ObjectType => ProcessStore.PlanObjectType;

    private static WorkflowObject Map(PlanHead h) =>
        new(ProcessStore.PlanObjectType, h.Id, h.Number, $"{h.PartName} ({h.Plant})", h.Revision, h.RevisionId, h.IterationId, h.Label, h.State, PlanStates.Label(h.State),
            h.State == PlanStates.UnderReview, h.IsLatest, h.OpenByName ?? h.OpenBy, h.Markings);

    public async Task<WorkflowObject?> FindByKeyAsync(DbConnection conn, DbTransaction tx, string key, bool forUpdate, CancellationToken ct)
    {
        var head = await ProcessStore.LoadByNumberAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, key, null, forUpdate, ct);
        return head is null ? null : Map(head);
    }

    public async Task<WorkflowObject?> FindByRevisionAsync(DbConnection conn, DbTransaction tx, Guid revisionId, bool forUpdate, CancellationToken ct)
    {
        var head = await ProcessStore.LoadByRevisionAsync(conn, tx, revisionId, forUpdate, ct);
        return head is null ? null : Map(head);
    }

    public async Task<IReadOnlyDictionary<Guid, WorkflowObject>> FindManyByRevisionAsync(DbConnection conn, DbTransaction tx, IReadOnlyCollection<Guid> revisionIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, WorkflowObject>();
        foreach (var id in revisionIds)
            if (await FindByRevisionAsync(conn, tx, id, false, ct) is { } o) result[id] = o;
        return result;
    }

    private static async Task<GatewayResult> MoveAsync(NpgsqlConnection c, NpgsqlTransaction t, PlanHead head, string from, string to, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE proc.plan_revision SET lifecycle_state = @to, state_changed_at = @at, state_changed_by = @by WHERE id = @rid AND lifecycle_state = @from", c, t);
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("at", now);
        cmd.Parameters.AddWithValue("by", ProcessStore.Who(actor));
        cmd.Parameters.AddWithValue("rid", head.RevisionId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1 ? GatewayResult.Ok : GatewayResult.Fail("The plan changed state concurrently.");
    }

    private async Task<string?> BlockerAsync(NpgsqlConnection c, NpgsqlTransaction t, PlanHead head, Subject actor, CancellationToken ct)
    {
        var current = await mboms.LatestReleasedAsync(head.MbomNumber, ct);
        var stale = ProcessRules.StalenessOf(head.BoundRevision, head.BoundSha, current);
        if (stale != Staleness.Current) return stale == Staleness.Stale
            ? $"The plan is written against MBOM revision {head.BoundRevision}, but revision {current!.Revision} is released. Check the plan out and rebind it first."
            : "The MBOM has no released revision, so the plan cannot be released.";
        var content = ProcessRules.Normalize(ProcessRules.Deserialize(head.ContentJson));
        var lines = await mboms.LinesAsync(actor, head.MbomNumber, head.BoundRevision, null, ct);
        var findings = await plans.FindingsAsync(c, t, actor, head, null, content, lines.IsOk ? lines.Value! : [], lines.IsOk, ct);
        var errors = findings.Where(f => f.Severity == Severities.Error).Select(f => f.Message).ToList();
        return errors.Count == 0 ? null : "The plan has errors: " + string.Join(" ", errors.Take(3)) + (errors.Count > 3 ? $" (and {errors.Count - 3} more)" : "");
    }

    public async Task<GatewayResult> BeginReviewAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var head = await ProcessStore.LoadByRevisionAsync(c, t, obj.RevisionId, true, ct);
        if (head is null) return GatewayResult.Fail("The plan no longer exists.");
        if (!head.IsLatest) return GatewayResult.Fail($"Revision {head.Revision} is not the latest revision.");
        if (head.State != PlanStates.InWork) return GatewayResult.Fail($"Revision {head.Revision} is {PlanStates.Label(head.State).ToLowerInvariant()}; only a plan In work can be submitted for review.");
        if (head.IsOpen) return GatewayResult.Fail($"Checked out by {head.OpenByName ?? head.OpenBy}; check in or undo the check-out first.");
        var stored = head.ContentSha ?? "";
        if (ProcessRules.Hash(ProcessRules.Normalize(ProcessRules.Deserialize(head.ContentJson))) != stored)
            return GatewayResult.Fail($"The stored content of {head.Number} {head.Label} no longer matches its recorded digest; it cannot be submitted for review.");
        if (await BlockerAsync(c, t, head, actor, ct) is { } blocker) return GatewayResult.Fail(blocker);
        var review = ProcessRules.ReviewHash(stored, head.BoundRevision, head.BoundSha);
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO proc.review_binding (id, revision_id, iteration_id, review_sha256, mbom_revision, bound_at, bound_by) VALUES (@id, @r, @i, @sha, @m, @at, @by)", c, t))
        {
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("r", head.RevisionId);
            ins.Parameters.AddWithValue("i", head.IterationId);
            ins.Parameters.AddWithValue("sha", review);
            ins.Parameters.AddWithValue("m", head.BoundRevision);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", ProcessStore.Who(actor));
            await ins.ExecuteNonQueryAsync(ct);
        }
        var moved = await MoveAsync(c, t, head, PlanStates.InWork, PlanStates.UnderReview, actor, now, ct);
        if (!moved.IsOk) return moved;
        await audit.AppendAsync(t, ProcessPlanService.Entry(actor, "process.plan_submit", AuditOutcome.Success, head.Number, null, ("revision", head.Label), ("mbomRevision", head.BoundRevision), ("contentSha256", stored), ("reviewSha256", review)), ct);
        return GatewayResult.Ok;
    }

    public async Task<GatewayResult> CompleteReleaseAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Guid boundIterationId, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var head = await ProcessStore.LoadByRevisionAsync(c, t, obj.RevisionId, true, ct);
        if (head is null) return GatewayResult.Fail("The plan no longer exists.");
        if (head.State != PlanStates.UnderReview) return GatewayResult.Fail($"Revision {head.Revision} is {PlanStates.Label(head.State).ToLowerInvariant()}, not under review; it cannot be released by this workflow.");
        if (head.IterationId != boundIterationId) return GatewayResult.Fail("The plan content changed after the review started; the workflow no longer covers the latest iteration.");
        string? bound = null;
        await using (var q = new NpgsqlCommand("SELECT review_sha256 FROM proc.review_binding WHERE revision_id = @r AND iteration_id = @i ORDER BY bound_at DESC, id DESC LIMIT 1", c, t))
        {
            q.Parameters.AddWithValue("r", head.RevisionId);
            q.Parameters.AddWithValue("i", head.IterationId);
            bound = (string?)await q.ExecuteScalarAsync(ct);
        }
        if (bound is null) return GatewayResult.Fail($"{head.Number} was never bound to a review; it cannot be released.");
        var live = ProcessRules.Hash(ProcessRules.Normalize(ProcessRules.Deserialize(head.ContentJson)));
        if (live != head.ContentSha || ProcessRules.ReviewHash(live, head.BoundRevision, head.BoundSha) != bound)
            return GatewayResult.Fail($"The content of {head.Number} does not match what was reviewed and signed; it cannot be released.");
        if (await BlockerAsync(c, t, head, actor, ct) is { } blocker) return GatewayResult.Fail(blocker);
        var moved = await MoveAsync(c, t, head, PlanStates.UnderReview, PlanStates.Released, actor, now, ct);
        if (!moved.IsOk) return moved;
        await using (var sup = new NpgsqlCommand(
            "UPDATE proc.plan_revision SET lifecycle_state = 'Obsolete', state_changed_at = @at, state_changed_by = @by WHERE plan_id = @p AND lifecycle_state = 'Released' AND id <> @rid", c, t))
        {
            sup.Parameters.AddWithValue("at", now);
            sup.Parameters.AddWithValue("by", ProcessStore.Who(actor));
            sup.Parameters.AddWithValue("p", head.Id);
            sup.Parameters.AddWithValue("rid", head.RevisionId);
            await sup.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(t, ProcessPlanService.Entry(actor, "process.plan_release", AuditOutcome.Success, head.Number, null, ("revision", head.Label), ("mbomRevision", head.BoundRevision), ("contentSha256", live), ("reviewSha256", bound)), ct);
        if (notifications is not null)
            await notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.WatchedReleased, AwarenessKinds.ProcessPlan, head.Number, head.Markings, actor.Id), ct);
        return GatewayResult.Ok;
    }

    public async Task<GatewayResult> ReturnToInWorkAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var head = await ProcessStore.LoadByRevisionAsync(c, t, obj.RevisionId, true, ct);
        if (head is null) return GatewayResult.Fail("The plan no longer exists.");
        if (head.State != PlanStates.UnderReview) return GatewayResult.Fail($"Revision {head.Revision} is not under review.");
        var moved = await MoveAsync(c, t, head, PlanStates.UnderReview, PlanStates.InWork, actor, now, ct);
        if (!moved.IsOk) return moved;
        await audit.AppendAsync(t, ProcessPlanService.Entry(actor, "process.plan_review_cancel", AuditOutcome.Success, head.Number, null, ("revision", head.Label)), ct);
        return GatewayResult.Ok;
    }

    public async Task<IterationContent?> LoadIterationContentAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        Guid revisionId; int iteration; string json;
        await using (var q = new NpgsqlCommand("SELECT revision_id, iteration, content::text FROM proc.plan_iteration WHERE id = @i", c, t))
        {
            q.Parameters.AddWithValue("i", iterationId);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            (revisionId, iteration, json) = (r.GetGuid(0), r.GetInt32(1), r.GetString(2));
        }
        var head = await ProcessStore.LoadByRevisionAsync(c, t, revisionId, false, ct);
        if (head is null) return null;
        var content = ProcessRules.Normalize(ProcessRules.Deserialize(json));
        var live = ProcessRules.Hash(content); // the live digest, not the stored one: editing a row after signing changes what verification computes
        var label = $"{head.Revision}.{iteration.ToString(CultureInfo.InvariantCulture)}";
        var sb = new StringBuilder()
            .AppendLine($"{head.Number} (process plan, plant {head.Plant}): {head.PartName} ({head.PartNumber})")
            .AppendLine($"Version {label}; written against MBOM {head.MbomNumber} revision {head.BoundRevision}")
            .AppendLine($"Markings: {head.Markings.BannerText()}");
        foreach (var r in content.Routings)
        {
            sb.AppendLine($"Routing {r.Code}{(r.IsPrimary ? " (primary)" : "")}: {r.Name}{(r.Condition is null ? "" : $"; applies when {r.Condition}")}");
            foreach (var o in r.Operations)
                sb.AppendLine($"  {o.Number}: {o.Name}; work center {o.WorkCenter ?? "none"}; setup {o.SetupMinutes?.ToString("0.##", CultureInfo.InvariantCulture) ?? "-"} min, run {o.RunMinutes?.ToString("0.####", CultureInfo.InvariantCulture) ?? "-"} min; " +
                    $"{o.Components.Count} component line(s), {o.Tools.Count} tool(s), {o.Steps.Count} step(s){(o.Inspection ? "; inspection point" : "")}");
        }
        var attributes = new JsonObject
        {
            ["format"] = ContentFormat, ["plan"] = head.Number, ["plant"] = head.Plant, ["mbom"] = head.MbomNumber, ["mbomRevision"] = head.BoundRevision, ["mbomSha256"] = head.BoundSha,
            ["routings"] = content.Routings.Count, ["operations"] = content.Routings.Sum(r => r.Operations.Count), ["contentSha256"] = live,
        };
        return new IterationContent(iterationId, ObjectType, head.Number, head.Revision, label, sb.ToString().TrimEnd(), attributes.ToJsonString());
    }
}
