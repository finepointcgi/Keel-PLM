using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Npgsql;

namespace Keel.Programs;

/// <summary>
/// Lets the workflow engine (which must not reference this module) review a gate review (a milestone with a checklist, such as a preliminary or critical design review) like any
/// other object (ADR-0018, ADR-0043). A gate has no revisions, so the engine's "iteration" is the submission: submitting freezes a text snapshot of the gate, its checklist and the
/// readiness of its deliverables, and the engine binds to the id derived from (gate, submission). The state Approved is reachable only through an approved review (a database guard
/// asks for an approving signature); approval completes the milestone and writes the <c>program.milestone_reached</c> outbox event in the same transaction.
/// </summary>
internal sealed class ProgramGateway(IAuditLog audit, ProgramService service) : IWorkflowObjectGateway
{
    public string ObjectType => AwarenessKinds.Gate;

    private static WorkflowObject Map(IHead h)
    {
        var n = h.GateState == GateStates.Draft ? h.Submission + 1 : h.Submission;
        return new WorkflowObject(AwarenessKinds.Gate, h.Id, h.GateNumber!, h.Title, n.ToString(CultureInfo.InvariantCulture), h.Id, ProgramStore.SubmissionId(h.Id, n), $"{h.GateNumber} submission {n}",
            h.GateState!, GateStates.Label(h.GateState!), h.GateState == GateStates.UnderReview, true, null, h.Markings);
    }

    public async Task<WorkflowObject?> FindByKeyAsync(DbConnection conn, DbTransaction tx, string key, bool forUpdate, CancellationToken ct)
    {
        var h = await ProgramStore.LoadItemByGateAsync(conn, tx, key, forUpdate, ct);
        return h is null ? null : Map(h);
    }

    public async Task<WorkflowObject?> FindByRevisionAsync(DbConnection conn, DbTransaction tx, Guid revisionId, bool forUpdate, CancellationToken ct)
    {
        var h = await ProgramStore.LoadItemByIdAsync(conn, tx, revisionId, forUpdate, ct);
        return h?.GateNumber is null ? null : Map(h);
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
        var item = await ProgramStore.LoadItemByIdAsync(conn, tx, obj.Id, true, ct);
        if (item?.GateNumber is null) return GatewayResult.Fail("The gate review no longer exists.");
        if (item.GateState != GateStates.Draft) return GatewayResult.Fail($"{item.GateNumber} is {obj.StateLabel}; it cannot be submitted from there.");
        var program = await ProgramStore.LoadProgramByIdAsync(conn, tx, item.ProgramId, ct);
        if (program is null || program.Closed) return GatewayResult.Fail("The program is closed.");
        await using (var q = ProgramStore.Cmd(conn, tx, "SELECT count(*), count(*) FILTER (WHERE required AND status = 'Open') FROM prg.checklist WHERE item_id = @id"))
        {
            q.Parameters.AddWithValue("id", item.Id);
            await using var r = await q.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            if (r.GetInt64(0) == 0) return GatewayResult.Fail("Add the review criteria to the checklist before submitting the gate review.");
            if (r.GetInt64(1) > 0) return GatewayResult.Fail("Every required criterion must be marked met or not applicable before the gate review is submitted.");
        }
        var n = item.Submission + 1;
        var text = await service.BuildGateSnapshotAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, actor, program, item, n, ct);
        await using (var ins = ProgramStore.Cmd(conn, tx, "INSERT INTO prg.submission (id, item_id, submission_no, snapshot, snapshot_sha256, created_at, created_by) VALUES (@id, @i, @n, @s, @h, @at, @by)"))
        {
            ins.Parameters.AddWithValue("id", ProgramStore.SubmissionId(item.Id, n));
            ins.Parameters.AddWithValue("i", item.Id);
            ins.Parameters.AddWithValue("n", n);
            ins.Parameters.AddWithValue("s", text);
            ins.Parameters.AddWithValue("h", ProgramStore.Sha256(text));
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", ProgramStore.Who(actor));
            await ins.ExecuteNonQueryAsync(ct);
        }
        await using (var up = ProgramStore.Cmd(conn, tx, "UPDATE prg.item SET gate_state = 'UnderReview', submission_no = @n, updated_at = @at, updated_by = @by WHERE id = @id AND gate_state = 'Draft'"))
        {
            up.Parameters.AddWithValue("n", n);
            up.Parameters.AddWithValue("at", now);
            up.Parameters.AddWithValue("by", ProgramStore.Who(actor));
            up.Parameters.AddWithValue("id", item.Id);
            if (await up.ExecuteNonQueryAsync(ct) != 1) return GatewayResult.Fail($"{item.GateNumber} changed state concurrently.");
        }
        await AuditAsync(tx, actor, "gate.submit", item, ("submission", ProgramStore.Int(n)), ("snapshotSha256", ProgramStore.Hex(text)));
        return GatewayResult.Ok;
    }

    public async Task<GatewayResult> CompleteReleaseAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Guid boundIterationId, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var item = await ProgramStore.LoadItemByIdAsync(conn, tx, obj.Id, true, ct);
        if (item?.GateNumber is null) return GatewayResult.Fail("The gate review no longer exists.");
        if (item.GateState != GateStates.UnderReview) return GatewayResult.Fail($"{item.GateNumber} is {obj.StateLabel}, not under review.");
        if (ProgramStore.SubmissionId(item.Id, item.Submission) != boundIterationId)
            return GatewayResult.Fail($"{item.GateNumber} was submitted again after this review started; the signatures no longer cover it.");
        var program = await ProgramStore.LoadProgramByIdAsync(conn, tx, item.ProgramId, ct);
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        await using (var up = ProgramStore.Cmd(conn, tx,
            "UPDATE prg.item SET gate_state = 'Approved', status = 'Complete', percent_complete = 100, actual_start = COALESCE(actual_start, @d), actual_end = @d, updated_at = @at, updated_by = @by WHERE id = @id AND gate_state = 'UnderReview'"))
        {
            up.Parameters.AddWithValue("d", day.ToDateTime(TimeOnly.MinValue));
            up.Parameters.AddWithValue("at", now);
            up.Parameters.AddWithValue("by", ProgramStore.Who(actor));
            up.Parameters.AddWithValue("id", item.Id);
            if (await up.ExecuteNonQueryAsync(ct) != 1) return GatewayResult.Fail($"{item.GateNumber} changed state concurrently.");
        }
        await service.EmitMilestoneReachedAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, program!, item, day, ProgramStore.Who(actor), null, now, ct);
        await AuditAsync(tx, actor, "gate.approve", item, ("submission", ProgramStore.Int(item.Submission)), ("program", program!.Code));
        return GatewayResult.Ok;
    }

    public Task<GatewayResult> ReturnToInWorkAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct) => ReturnAsync(conn, tx, obj, actor, now, false, ct);

    public Task<GatewayResult> CompleteRejectionAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct) => ReturnAsync(conn, tx, obj, actor, now, true, ct);

    private async Task<GatewayResult> ReturnAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, bool rejected, CancellationToken ct)
    {
        var item = await ProgramStore.LoadItemByIdAsync(conn, tx, obj.Id, true, ct);
        if (item?.GateNumber is null) return GatewayResult.Fail("The gate review no longer exists.");
        if (item.GateState != GateStates.UnderReview) return GatewayResult.Fail($"{item.GateNumber} is {obj.StateLabel}, not under review.");
        await using (var up = ProgramStore.Cmd(conn, tx, "UPDATE prg.item SET gate_state = 'Draft', updated_at = @at, updated_by = @by WHERE id = @id AND gate_state = 'UnderReview'"))
        {
            up.Parameters.AddWithValue("at", now);
            up.Parameters.AddWithValue("by", ProgramStore.Who(actor));
            up.Parameters.AddWithValue("id", item.Id);
            if (await up.ExecuteNonQueryAsync(ct) != 1) return GatewayResult.Fail($"{item.GateNumber} changed state concurrently.");
        }
        await AuditAsync(tx, actor, rejected ? "gate.review_reject" : "gate.review_cancel", item, ("to", GateStates.Draft));
        return GatewayResult.Ok;
    }

    public async Task<IterationContent?> LoadIterationContentAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        await using var q = ProgramStore.Cmd(conn, tx, "SELECT s.item_id, s.submission_no, s.snapshot FROM prg.submission s WHERE s.id = @id");
        q.Parameters.AddWithValue("id", iterationId);
        Guid itemId;
        int n;
        string text;
        await using (var r = await q.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            (itemId, n, text) = (r.GetGuid(0), r.GetInt32(1), r.GetString(2));
        }
        var item = await ProgramStore.LoadItemByIdAsync(conn, tx, itemId, false, ct);
        if (item?.GateNumber is null) return null;
        // The hash of the stored snapshot goes into the signed content: editing the stored text after signing changes the signature hash and verification reports it.
        var attributes = new JsonObject { ["format"] = "gate-content/1", ["submission"] = n, ["snapshotSha256"] = ProgramStore.Hex(text) };
        return new IterationContent(iterationId, AwarenessKinds.Gate, item.GateNumber, n.ToString(CultureInfo.InvariantCulture), $"{item.GateNumber} submission {n}", text, attributes.ToJsonString());
    }

    private Task<AuditRecord> AuditAsync(DbTransaction tx, Subject actor, string action, IHead item, params (string, string?)[] details) =>
        audit.AppendAsync((NpgsqlTransaction)tx, ProgramStore.Entry(actor, action, AuditOutcome.Success, AwarenessKinds.Gate, item.GateNumber, null, details));
}
