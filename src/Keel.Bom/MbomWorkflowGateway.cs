using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Npgsql;

namespace Keel.Bom;

/// <summary>
/// Lets the workflow engine review and release an MBOM like any other object (ADR-0018, ADR-0024). Submitting needs a fresh reconciliation without errors
/// (and without warnings where the plant says so); the submission binds the iteration content digest and the reconciliation run, and the signatures cover a digest
/// recomputed from the live rows, so editing a line, a link or the run after signing makes verification fail and the release refuse. All methods run in the engine's transaction.
/// </summary>
internal sealed class MbomWorkflowGateway(IAuditLog audit, IDomainEventSink? events = null) : IWorkflowObjectGateway
{
    public const string ContentFormat = "mbom-content/1";

    public string ObjectType => MbomService.ObjectType;

    private static async Task<WorkflowObject> MapAsync(NpgsqlConnection conn, NpgsqlTransaction tx, MbomHead h, CancellationToken ct)
    {
        var lifecycle = await Lifecycle.LoadAsync(conn, tx, h.LifecycleId, ct);
        bool latest;
        await using (var cmd = new NpgsqlCommand("SELECT is_latest FROM mbom.revision WHERE id = @r", conn, tx))
        {
            cmd.Parameters.AddWithValue("r", h.RevisionId);
            latest = (bool)(await cmd.ExecuteScalarAsync(ct))!;
        }
        return new WorkflowObject(MbomService.ObjectType, h.Id, h.Number, $"{h.PartName} ({h.Plant})", h.Revision, h.RevisionId, h.IterationId, h.Label, h.State,
            lifecycle?.State(h.State)?.Label ?? h.State, h.State == MbomStates.UnderReview, latest, h.CheckoutUserName ?? h.CheckoutUser, h.Markings);
    }

    public async Task<WorkflowObject?> FindByKeyAsync(DbConnection conn, DbTransaction tx, string key, bool forUpdate, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn;
        var t = (NpgsqlTransaction)tx;
        var head = await MbomStore.LoadByNumberAsync(c, t, key, null, forUpdate, ct);
        return head is null ? null : await MapAsync(c, t, head, ct);
    }

    public async Task<WorkflowObject?> FindByRevisionAsync(DbConnection conn, DbTransaction tx, Guid revisionId, bool forUpdate, CancellationToken ct)
    {
        var head = await MbomStore.LoadByRevisionAsync(conn, tx, revisionId, forUpdate, ct);
        return head is null ? null : await MapAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, head, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, WorkflowObject>> FindManyByRevisionAsync(DbConnection conn, DbTransaction tx, IReadOnlyCollection<Guid> revisionIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, WorkflowObject>();
        foreach (var id in revisionIds)
            if (await FindByRevisionAsync(conn, tx, id, false, ct) is { } o) result[id] = o;
        return result;
    }

    private static async Task<GatewayResult> MoveAsync(NpgsqlConnection c, NpgsqlTransaction t, MbomHead head, string from, string to, Subject actor, DateTimeOffset now, string? reason, CancellationToken ct)
    {
        var lifecycle = await Lifecycle.LoadAsync(c, t, head.LifecycleId, ct) ?? throw new InvalidOperationException("Lifecycle missing.");
        var transition = lifecycle.Transitions.FirstOrDefault(x => x.From == from && x.To == to);
        if (transition is null || transition.Via != "workflow") return GatewayResult.Fail($"The MBOM lifecycle has no workflow transition from {from} to {to}.");
        await using var cmd = new NpgsqlCommand(
            "UPDATE mbom.revision SET lifecycle_state = @to, state_changed_at = @at, state_changed_by = @by, state_reason = @reason WHERE id = @rid AND lifecycle_state = @from", c, t);
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("at", now);
        cmd.Parameters.AddWithValue("by", BomService.DisplayId(actor));
        cmd.Parameters.Add(new NpgsqlParameter("reason", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)reason ?? DBNull.Value });
        cmd.Parameters.AddWithValue("rid", head.RevisionId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1 ? GatewayResult.Ok : GatewayResult.Fail("The MBOM changed state concurrently.");
    }

    private static async Task<(string Stored, Guid? Derived)> IterationFactsAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid iterationId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT content_sha256, derived_part_iteration_id FROM mbom.iteration WHERE id = @i", c, t);
        cmd.Parameters.AddWithValue("i", iterationId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetGuid(1));
    }

    public async Task<GatewayResult> BeginReviewAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn;
        var t = (NpgsqlTransaction)tx;
        var head = await MbomStore.LoadByRevisionAsync(c, t, obj.RevisionId, true, ct);
        if (head is null) return GatewayResult.Fail("The MBOM no longer exists.");
        if (!obj.IsLatestRevision) return GatewayResult.Fail($"Revision {head.Revision} is not the latest revision.");
        if (head.State != MbomStates.InWork) return GatewayResult.Fail($"Revision {head.Revision} is {obj.StateLabel}; only an MBOM In Work can be submitted for review.");
        if (head.CheckoutUser is not null) return GatewayResult.Fail($"Checked out by {head.CheckoutUserName ?? head.CheckoutUser}; check in or undo the check-out first.");
        var plant = await MbomStore.LoadPlantAsync(c, t, head.Plant, ct) ?? throw new InvalidOperationException("The plant of an MBOM is missing.");
        var (run, blocker) = await MbomService.ReleaseCheckAsync(c, t, head, plant, ct);
        if (blocker is not null || run is null) return GatewayResult.Fail(blocker ?? "No reconciliation exists.");
        var (stored, derived) = await IterationFactsAsync(c, t, head.IterationId, ct);
        if (await MbomStore.HashAsync(c, t, false, head.IterationId, derived, ct) != stored)
            return GatewayResult.Fail($"The stored content of {head.Number} {head.Label} no longer matches its recorded digest; it cannot be submitted for review.");
        var review = MbomRules.ReviewHash(stored, run.Id, run.Sha256);
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO mbom.review_binding (id, revision_id, iteration_id, run_id, review_sha256, bound_at, bound_by) VALUES (@id, @r, @i, @run, @sha, @at, @by)", c, t))
        {
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("r", head.RevisionId);
            ins.Parameters.AddWithValue("i", head.IterationId);
            ins.Parameters.AddWithValue("run", run.Id);
            ins.Parameters.AddWithValue("sha", review);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", BomService.DisplayId(actor));
            await ins.ExecuteNonQueryAsync(ct);
        }
        var moved = await MoveAsync(c, t, head, MbomStates.InWork, MbomStates.UnderReview, actor, now, null, ct);
        if (!moved.IsOk) return moved;
        await Audit(t, actor, "mbom.submit", head, ("revision", head.Label), ("reconciliation", run.Id.ToString("D")), ("status", run.Status), ("contentSha256", stored), ("reviewSha256", review));
        return GatewayResult.Ok;
    }

    public async Task<GatewayResult> CompleteReleaseAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Guid boundIterationId, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn;
        var t = (NpgsqlTransaction)tx;
        var head = await MbomStore.LoadByRevisionAsync(c, t, obj.RevisionId, true, ct);
        if (head is null) return GatewayResult.Fail("The MBOM no longer exists.");
        if (head.State != MbomStates.UnderReview) return GatewayResult.Fail($"Revision {head.Revision} is {obj.StateLabel}, not Under Review; it cannot be released by this workflow.");
        if (head.IterationId != boundIterationId) return GatewayResult.Fail("The MBOM content changed after the review started; the workflow no longer covers the latest iteration.");

        // What was signed must still be what is stored: lines and links, the reconciliation run and the binding made when the review began.
        var (stored, derived) = await IterationFactsAsync(c, t, head.IterationId, ct);
        string? bound = null;
        Guid runId = default;
        await using (var cmd = new NpgsqlCommand("SELECT review_sha256, run_id FROM mbom.review_binding WHERE revision_id = @r AND iteration_id = @i ORDER BY bound_at DESC, id DESC LIMIT 1", c, t))
        {
            cmd.Parameters.AddWithValue("r", head.RevisionId);
            cmd.Parameters.AddWithValue("i", head.IterationId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) (bound, runId) = (r.GetString(0), r.GetGuid(1));
        }
        if (bound is null) return GatewayResult.Fail($"{head.Number} was never bound to a reconciliation; it cannot be released.");
        var run = await MbomService.LoadRunAsync(c, t, runId, ct);
        var live = await MbomStore.HashAsync(c, t, false, head.IterationId, derived, ct);
        var liveRun = await MbomService.RunHashAsync(c, t, runId, ct);
        if (live != stored || run is null || liveRun != run.Sha256 || MbomRules.ReviewHash(live, runId, liveRun) != bound)
            return GatewayResult.Fail($"The content of {head.Number} does not match what was reviewed and signed (lines, EBOM links or the reconciliation changed); it cannot be released.");
        if (run.Status is not (ReconcileStatuses.Reconciled or ReconcileStatuses.WithExceptions))
            return GatewayResult.Fail("The reconciliation bound to this review has errors; the MBOM cannot be released.");

        var moved = await MoveAsync(c, t, head, MbomStates.UnderReview, MbomStates.Released, actor, now, null, ct);
        if (!moved.IsOk) return moved;
        // Manufacturing revises its own structure: the revision this one replaces is superseded.
        await using (var sup = new NpgsqlCommand(
            "UPDATE mbom.revision SET lifecycle_state = 'Obsolete', state_changed_at = @at, state_changed_by = @by, state_reason = @reason WHERE mbom_id = @m AND lifecycle_state = 'Released' AND id <> @rid", c, t))
        {
            sup.Parameters.AddWithValue("at", now);
            sup.Parameters.AddWithValue("by", BomService.DisplayId(actor));
            sup.Parameters.AddWithValue("reason", $"Superseded by revision {head.Revision}");
            sup.Parameters.AddWithValue("m", head.Id);
            sup.Parameters.AddWithValue("rid", head.RevisionId);
            await sup.ExecuteNonQueryAsync(ct);
        }
        await Audit(t, actor, "mbom.release", head, ("revision", head.Label), ("reconciliation", runId.ToString("D")), ("contentSha256", live), ("reviewSha256", bound));
        // 4.0 follow-up: the ERP event, in this transaction (it exists exactly when the release committed; not emitted on check-in or submission).
        await MbomEvents.EmitReleasedAsync(events, c, t, head with { State = MbomStates.Released }, live, runId, run.Status, run.Sha256, actor, now, null, ct);
        return GatewayResult.Ok;
    }

    public async Task<GatewayResult> ReturnToInWorkAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn;
        var t = (NpgsqlTransaction)tx;
        var head = await MbomStore.LoadByRevisionAsync(c, t, obj.RevisionId, true, ct);
        if (head is null) return GatewayResult.Fail("The MBOM no longer exists.");
        if (head.State != MbomStates.UnderReview) return GatewayResult.Fail($"Revision {head.Revision} is {obj.StateLabel}, not Under Review.");
        var moved = await MoveAsync(c, t, head, MbomStates.UnderReview, MbomStates.InWork, actor, now, null, ct);
        if (!moved.IsOk) return moved;
        await Audit(t, actor, "mbom.review_cancel", head, ("revision", head.Label));
        return GatewayResult.Ok;
    }

    public async Task<IterationContent?> LoadIterationContentAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn;
        var t = (NpgsqlTransaction)tx;
        Guid revisionId;
        int iteration;
        string? derivedSpec;
        Guid? derived;
        int lines, links;
        await using (var cmd = new NpgsqlCommand("SELECT revision_id, iteration, derived_spec, derived_part_iteration_id, line_count, link_count FROM mbom.iteration WHERE id = @i", c, t))
        {
            cmd.Parameters.AddWithValue("i", iterationId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            (revisionId, iteration, derivedSpec, derived, lines, links) = (r.GetGuid(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetInt32(4), r.GetInt32(5));
        }
        var head = await MbomStore.LoadByRevisionAsync(c, t, revisionId, false, ct);
        if (head is null) return null;
        // The live digests, not the stored ones: editing a row after signing changes what verification computes.
        var liveContent = await MbomStore.HashAsync(c, t, false, iterationId, derived, ct);
        Guid? runId = null;
        await using (var cmd = new NpgsqlCommand("SELECT run_id FROM mbom.review_binding WHERE iteration_id = @i ORDER BY bound_at DESC, id DESC LIMIT 1", c, t))
        {
            cmd.Parameters.AddWithValue("i", iterationId);
            runId = await cmd.ExecuteScalarAsync(ct) as Guid?;
        }
        var run = runId is { } id ? await MbomService.LoadRunAsync(c, t, id, ct) : null;
        var liveRun = run is null ? null : await MbomService.RunHashAsync(c, t, run.Id, ct);
        var derivedLabel = derived is { } d ? await MbomStore.IterationLabelAsync(c, t, d, ct) : null;
        var label = $"{head.Revision}.{iteration.ToString(CultureInfo.InvariantCulture)}";
        var description = new StringBuilder()
            .AppendLine($"{head.Number} (MBOM, plant {head.Plant}): {head.PartName} ({head.PartNumber})")
            .AppendLine($"Version: {label}; derived from EBOM {derivedLabel ?? "none"}{(derivedSpec is null ? "" : $" ({derivedSpec})")}")
            .AppendLine($"Lines: {lines.ToString(CultureInfo.InvariantCulture)}; EBOM links: {links.ToString(CultureInfo.InvariantCulture)}")
            .Append(run is null ? "Reconciliation: none\n" : $"Reconciliation {run.Id:D}: {ReconcileStatuses.Label(run.Status)} against {run.Spec}; {run.Errors} errors, {run.Warnings} warnings, {run.Infos} notes\n")
            .Append($"Markings: {head.Markings.BannerText()}")
            .ToString();
        var attributes = new JsonObject
        {
            ["format"] = ContentFormat,
            ["mbom"] = head.Number,
            ["plant"] = head.Plant,
            ["part"] = head.PartNumber,
            ["derivedFrom"] = derivedLabel,
            ["lines"] = lines,
            ["links"] = links,
            ["contentSha256"] = liveContent,
            ["reconciliationRun"] = run?.Id.ToString("D"),
            ["reconciliationStatus"] = run?.Status,
            ["reconciliationSha256"] = liveRun,
        };
        return new IterationContent(iterationId, ObjectType, head.Number, head.Revision, label, description, attributes.ToJsonString());
    }

    private Task<AuditRecord> Audit(NpgsqlTransaction tx, Subject actor, string action, MbomHead head, params (string, string?)[] details) =>
        audit.AppendAsync(tx, MbomService.Entry(actor, action, AuditOutcome.Success, head.Number, null, details));
}
