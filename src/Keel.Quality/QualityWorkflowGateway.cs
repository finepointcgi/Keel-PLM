using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Npgsql;

namespace Keel.Quality;

/// <summary>
/// Lets the workflow engine (which must not reference this module) review quality records like any other object (ADR-0018, ADR-0036): the material review board
/// signs an NCR disposition, verifiers sign the effectiveness of a corrective action, approvers sign a deviation or waiver. One instance per kind; every call runs in the
/// engine's transaction. A record has no revisions, so the engine's "iteration" is the submission: submitting freezes a text snapshot of everything the signatures cover,
/// and the engine binds to the id derived from (record, submission number). The states Dispositioned, Closed and Approved are reachable only through an approved review
/// (lifecycle data plus a database guard that asks for an approving signature).
/// </summary>
internal sealed class QualityWorkflowGateway(string kind, IAuditLog audit) : IWorkflowObjectGateway
{
    public string ObjectType => kind;

    private string StartState => kind switch { QualityKinds.Ncr => "Open", QualityKinds.Capa => "InProgress", _ => "Draft" };
    private string ReviewState => kind == QualityKinds.Capa ? "Verification" : "UnderReview";

    // ---------------------------------------------------------------- lookup

    private WorkflowObject Map(QHead h, Lifecycle lifecycle)
    {
        var n = h.State == StartState ? h.Submission + 1 : h.Submission;
        return new WorkflowObject(kind, h.Id, h.Number, h.Title, n.ToString(CultureInfo.InvariantCulture), h.Id, QualityStore.SubmissionId(kind, h.Id, n), $"{h.Number} submission {n}",
            h.State, lifecycle.State(h.State)?.Label ?? h.State, h.State == ReviewState, true, null, h.Markings);
    }

    private static async Task<Lifecycle> LifecycleOfAsync(DbConnection conn, DbTransaction tx, QHead h, CancellationToken ct) =>
        await Lifecycle.LoadAsync(conn, tx, h.LifecycleId, ct) ?? throw new InvalidOperationException($"Lifecycle {h.LifecycleId} missing.");

    public async Task<WorkflowObject?> FindByKeyAsync(DbConnection conn, DbTransaction tx, string key, bool forUpdate, CancellationToken ct)
    {
        var h = await QualityStore.LoadByNumberAsync(conn, tx, kind, key, forUpdate, ct);
        return h is null ? null : Map(h, await LifecycleOfAsync(conn, tx, h, ct));
    }

    public async Task<WorkflowObject?> FindByRevisionAsync(DbConnection conn, DbTransaction tx, Guid revisionId, bool forUpdate, CancellationToken ct)
    {
        var h = await QualityStore.LoadByIdAsync(conn, tx, kind, revisionId, forUpdate, ct);
        return h is null ? null : Map(h, await LifecycleOfAsync(conn, tx, h, ct));
    }

    public async Task<IReadOnlyDictionary<Guid, WorkflowObject>> FindManyByRevisionAsync(DbConnection conn, DbTransaction tx, IReadOnlyCollection<Guid> revisionIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, WorkflowObject>();
        foreach (var id in revisionIds)
            if (await FindByRevisionAsync(conn, tx, id, false, ct) is { } o) result[id] = o;
        return result;
    }

    // ---------------------------------------------------------------- submit

    public async Task<GatewayResult> BeginReviewAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var head = await QualityStore.LoadByIdAsync(conn, tx, kind, obj.Id, forUpdate: true, ct);
        if (head is null) return GatewayResult.Fail("The record no longer exists.");
        if (head.State != StartState) return GatewayResult.Fail($"{head.Number} is {obj.StateLabel}; it cannot be submitted for review from there.");

        var problem = kind switch
        {
            QualityKinds.Ncr => await NcrReadyAsync(conn, tx, head, ct),
            QualityKinds.Capa => await CapaService.ReadinessAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, head.Id, requireResult: true, ct),
            _ => await DeviationReadyAsync(conn, tx, head, now, ct),
        };
        if (problem is not null) return GatewayResult.Fail(problem);

        var n = head.Submission + 1;
        var text = await QualitySnapshots.BuildAsync(conn, tx, head, n, ct);
        await QualityStore.InsertSnapshotAsync(conn, tx, head, n, text, QualityStore.Who(actor), now, ct);
        var moved = await QualityStore.TransitionAsync(conn, tx, head, ReviewState, "workflow", QualityStore.Who(actor), now);
        if (!moved.IsOk) return moved;
        await QualityStore.AppendHistoryAsync(conn, tx, kind, head.Id, actor, "Submitted for review", $"Submission {n}.", now, ct);
        await AuditAsync(tx, actor, kind + ".submit", head, ("submission", n.ToString(CultureInfo.InvariantCulture)), ("snapshotSha256", Convert.ToHexStringLower(QualityStore.Sha256(text))));
        return GatewayResult.Ok;
    }

    private static async Task<string?> NcrReadyAsync(DbConnection conn, DbTransaction tx, QHead head, CancellationToken ct)
    {
        await using var q = QualityStore.Cmd(conn, tx, "SELECT proposed_disposition, disposition_rationale FROM qual.ncr WHERE id = @id");
        q.Parameters.AddWithValue("id", head.Id);
        await using var r = await q.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        if (r.IsDBNull(0) || string.IsNullOrWhiteSpace(r.GetString(1))) return "Propose a disposition and give its rationale before submitting the report to the review board.";
        return null;
    }

    private static async Task<string?> DeviationReadyAsync(DbConnection conn, DbTransaction tx, QHead head, DateTimeOffset now, CancellationToken ct)
    {
        await using var q = QualityStore.Cmd(conn, tx, "SELECT (SELECT count(*) FROM qual.deviation_scope WHERE deviation_id = d.id), d.valid_to FROM qual.deviation d WHERE d.id = @id");
        q.Parameters.AddWithValue("id", head.Id);
        await using var r = await q.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        if (r.GetInt64(0) == 0) return "Name at least one part in the scope before submitting.";
        if (DateOnly.FromDateTime(r.GetDateTime(1)) < DateOnly.FromDateTime(now.UtcDateTime)) return "The window ends in the past. Change the dates before submitting.";
        return null;
    }

    // ---------------------------------------------------------------- approve

    public async Task<GatewayResult> CompleteReleaseAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Guid boundIterationId, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var head = await QualityStore.LoadByIdAsync(conn, tx, kind, obj.Id, forUpdate: true, ct);
        if (head is null) return GatewayResult.Fail("The record no longer exists.");
        if (head.State != ReviewState) return GatewayResult.Fail($"{head.Number} is {obj.StateLabel}, not under review.");
        if (QualityStore.SubmissionId(kind, head.Id, head.Submission) != boundIterationId)
            return GatewayResult.Fail($"{head.Number} was submitted again after this review started; the signatures no longer cover it.");

        switch (kind)
        {
            case QualityKinds.Ncr:
            {
                var moved = await QualityStore.TransitionAsync(conn, tx, head, "Dispositioned", "workflow", QualityStore.Who(actor), now, "disposition = proposed_disposition");
                if (!moved.IsOk) return moved;
                await QualityStore.AppendHistoryAsync(conn, tx, kind, head.Id, actor, "Dispositioned", "The review board approved the proposed disposition.", now, ct);
                await AuditAsync(tx, actor, "ncr.disposition_approved", head, ("submission", head.Submission.ToString(CultureInfo.InvariantCulture)));
                return GatewayResult.Ok;
            }
            case QualityKinds.Capa:
            {
                var moved = await QualityStore.TransitionAsync(conn, tx, head, "Closed", "workflow", QualityStore.Who(actor), now);
                if (!moved.IsOk) return moved;
                await QualityStore.AppendHistoryAsync(conn, tx, kind, head.Id, actor, "Closed", "The verification was signed.", now, ct);
                await AuditAsync(tx, actor, "capa.close", head, ("submission", head.Submission.ToString(CultureInfo.InvariantCulture)));
                return GatewayResult.Ok;
            }
            default:
            {
                bool needsCustomer;
                await using (var q = QualityStore.Cmd(conn, tx, "SELECT customer_approval_required AND customer_approval_ref IS NULL FROM qual.deviation WHERE id = @id"))
                {
                    q.Parameters.AddWithValue("id", head.Id);
                    needsCustomer = (bool)(await q.ExecuteScalarAsync(ct))!;
                }
                var to = needsCustomer ? "AwaitingCustomer" : "Approved";
                var moved = await QualityStore.TransitionAsync(conn, tx, head, to, "workflow", QualityStore.Who(actor), now);
                if (!moved.IsOk) return moved;
                await QualityStore.AppendHistoryAsync(conn, tx, kind, head.Id, actor, needsCustomer ? "Approved internally" : "Approved",
                    needsCustomer ? "The customer's approval is still needed." : "Every approver signed.", now, ct);
                await AuditAsync(tx, actor, "deviation.approve", head, ("submission", head.Submission.ToString(CultureInfo.InvariantCulture)), ("to", to));
                return GatewayResult.Ok;
            }
        }
    }

    // ---------------------------------------------------------------- return, reject

    public Task<GatewayResult> ReturnToInWorkAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct) =>
        ReturnAsync(conn, tx, obj, actor, now, rejected: false, ct);

    public Task<GatewayResult> CompleteRejectionAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct) =>
        ReturnAsync(conn, tx, obj, actor, now, rejected: true, ct);

    private async Task<GatewayResult> ReturnAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, bool rejected, CancellationToken ct)
    {
        var head = await QualityStore.LoadByIdAsync(conn, tx, kind, obj.Id, forUpdate: true, ct);
        if (head is null) return GatewayResult.Fail("The record no longer exists.");
        if (head.State != ReviewState) return GatewayResult.Fail($"{head.Number} is {obj.StateLabel}, not under review.");
        var moved = await QualityStore.TransitionAsync(conn, tx, head, StartState, "workflow", QualityStore.Who(actor), now);
        if (!moved.IsOk) return moved;
        await QualityStore.AppendHistoryAsync(conn, tx, kind, head.Id, actor, rejected ? "Rejected in review" : "Review cancelled", "Back to work; editable again.", now, ct);
        await AuditAsync(tx, actor, kind + (rejected ? ".review_reject" : ".review_cancel"), head, ("to", StartState));
        return GatewayResult.Ok;
    }

    // ---------------------------------------------------------------- signed content

    public async Task<IterationContent?> LoadIterationContentAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        await using var q = QualityStore.Cmd(conn, tx, "SELECT s.object_id, s.submission_no, s.snapshot FROM qual.submission s WHERE s.id = @id AND s.object_kind = @k");
        q.Parameters.AddWithValue("id", iterationId);
        q.Parameters.AddWithValue("k", kind);
        Guid objectId;
        int n;
        string text;
        await using (var r = await q.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            (objectId, n, text) = (r.GetGuid(0), r.GetInt32(1), r.GetString(2));
        }
        var head = await QualityStore.LoadByIdAsync(conn, tx, kind, objectId, false, ct);
        if (head is null) return null;
        // The hash of the stored snapshot goes into the signed content: editing the stored text after signing changes the signature hash and verification reports it.
        var attributes = new JsonObject { ["format"] = "quality-content/1", ["submission"] = n, ["snapshotSha256"] = Convert.ToHexStringLower(QualityStore.Sha256(text)) };
        return new IterationContent(iterationId, kind, head.Number, n.ToString(CultureInfo.InvariantCulture), $"{head.Number} submission {n}", text, attributes.ToJsonString());
    }

    private Task<AuditRecord> AuditAsync(DbTransaction tx, Subject actor, string action, QHead head, params (string, string?)[] details) =>
        audit.AppendAsync((NpgsqlTransaction)tx, QualityStore.Entry(actor, action, AuditOutcome.Success, kind, head.Number, null, details));
}

/// <summary>The readable text that a review covers. It is stored with the submission and hashed into every signature.</summary>
internal static class QualitySnapshots
{
    public static async Task<string> BuildAsync(DbConnection conn, DbTransaction tx, QHead head, int submission, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append(head.Number).Append(" submission ").Append(submission).AppendLine();
        sb.Append("Markings: ").AppendLine(head.Markings.BannerText());
        switch (head.Kind)
        {
            case QualityKinds.Ncr: await NcrAsync(conn, tx, head, sb, ct); break;
            case QualityKinds.Capa: await CapaAsync(conn, tx, head, sb, ct); break;
            default: await DeviationAsync(conn, tx, head, sb, ct); break;
        }
        return sb.ToString().TrimEnd();
    }

    private static async Task NcrAsync(DbConnection conn, DbTransaction tx, QHead head, StringBuilder sb, CancellationToken ct)
    {
        await using (var q = QualityStore.Cmd(conn, tx,
            "SELECT title, description, severity, detected_at_stage, part_number, part_revision, lot_number, serial_number, supplier_ref, quantity, unit, proposed_disposition, disposition_rationale FROM qual.ncr WHERE id = @id"))
        {
            q.Parameters.AddWithValue("id", head.Id);
            await using var r = await q.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            sb.Append("Title: ").AppendLine(r.GetString(0));
            sb.Append("Severity: ").Append(r.GetString(2)).Append(", found at stage ").AppendLine(r.GetString(3));
            sb.Append("Part: ").Append(r.GetString(4)).Append(" revision ").AppendLine(r.GetString(5));
            sb.Append("Lot: ").Append(r.IsDBNull(6) ? "none" : r.GetString(6)).Append(", serial: ").AppendLine(r.IsDBNull(7) ? "none" : r.GetString(7));
            sb.Append("Supplier reference: ").AppendLine(r.IsDBNull(8) ? "none" : r.GetString(8));
            sb.Append("Quantity: ").Append(QualityStore.Num(r.GetDecimal(9))).Append(' ').AppendLine(r.GetString(10));
            sb.Append("Defect: ").AppendLine(r.GetString(1));
            sb.Append("Proposed disposition: ").AppendLine(NcrDispositions.Label(r.IsDBNull(11) ? null : r.GetString(11)));
            sb.Append("Rationale: ").AppendLine(r.GetString(12));
        }
        sb.AppendLine("Containment and correction:");
        foreach (var line in await QualityStore.ActionLinesAsync(conn, tx, "ncr", head.Id, ct)) sb.AppendLine(line);
    }

    private static async Task CapaAsync(DbConnection conn, DbTransaction tx, QHead head, StringBuilder sb, CancellationToken ct)
    {
        await using (var q = QualityStore.Cmd(conn, tx,
            "SELECT title, description, capa_type, source, priority, owner_username, root_cause, verification_method, verification_criteria, verification_result, verification_effective FROM qual.capa WHERE id = @id"))
        {
            q.Parameters.AddWithValue("id", head.Id);
            await using var r = await q.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            sb.Append("Title: ").AppendLine(r.GetString(0));
            sb.Append("Type: ").Append(r.GetString(2)).Append(", source ").Append(r.GetString(3)).Append(", priority ").Append(r.GetString(4)).Append(", owner ").AppendLine(r.GetString(5));
            sb.Append("Problem: ").AppendLine(r.GetString(1));
            sb.Append("Root cause: ").AppendLine(r.GetString(6));
            sb.Append("Verification method: ").AppendLine(r.GetString(7));
            sb.Append("Success criteria: ").AppendLine(r.GetString(8));
            sb.Append("Verification result: ").Append(r.GetString(9)).Append(" (").Append(!r.IsDBNull(10) && r.GetBoolean(10) ? "effective" : "not effective").AppendLine(")");
        }
        await using (var q = QualityStore.Cmd(conn, tx, "SELECT seq, why_text, evidence FROM qual.rca_why WHERE capa_id = @id ORDER BY seq"))
        {
            q.Parameters.AddWithValue("id", head.Id);
            await using var r = await q.ExecuteReaderAsync(ct);
            sb.AppendLine("Whys:");
            while (await r.ReadAsync(ct)) sb.Append("  ").Append(r.GetInt32(0)).Append(". ").Append(r.GetString(1)).AppendLine(r.GetString(2).Length == 0 ? "" : " Evidence: " + r.GetString(2));
        }
        await using (var q = QualityStore.Cmd(conn, tx, "SELECT category, description, is_root FROM qual.rca_cause WHERE capa_id = @id ORDER BY category, id"))
        {
            q.Parameters.AddWithValue("id", head.Id);
            await using var r = await q.ExecuteReaderAsync(ct);
            sb.AppendLine("Causes:");
            while (await r.ReadAsync(ct)) sb.Append("  ").Append(r.GetString(0)).Append(r.GetBoolean(2) ? " (root)" : "").Append(": ").AppendLine(r.GetString(1));
        }
        sb.AppendLine("Actions:");
        foreach (var line in await QualityStore.ActionLinesAsync(conn, tx, "capa", head.Id, ct)) sb.AppendLine(line);
    }

    private static async Task DeviationAsync(DbConnection conn, DbTransaction tx, QHead head, StringBuilder sb, CancellationToken ct)
    {
        await using (var q = QualityStore.Cmd(conn, tx, "SELECT dev_kind, title, description, requirement_ref, customer_approval_required, valid_from, valid_to FROM qual.deviation WHERE id = @id"))
        {
            q.Parameters.AddWithValue("id", head.Id);
            await using var r = await q.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            sb.Append(r.GetString(0)).Append(": ").AppendLine(r.GetString(1));
            sb.Append("Requirement: ").AppendLine(r.GetString(3));
            sb.Append("Window: ").Append(QualityStore.DateText(DateOnly.FromDateTime(r.GetDateTime(5)))).Append(" to ").AppendLine(QualityStore.DateText(DateOnly.FromDateTime(r.GetDateTime(6))));
            sb.Append("Customer approval: ").AppendLine(r.GetBoolean(4) ? "required" : "not required");
            sb.Append("Justification: ").AppendLine(r.GetString(2));
        }
        await using (var q = QualityStore.Cmd(conn, tx, "SELECT part_number, part_revision, serial_from, serial_to, serials, lots, max_quantity FROM qual.deviation_scope WHERE deviation_id = @id ORDER BY part_number, part_revision NULLS FIRST"))
        {
            q.Parameters.AddWithValue("id", head.Id);
            await using var r = await q.ExecuteReaderAsync(ct);
            sb.AppendLine("Scope:");
            while (await r.ReadAsync(ct))
                sb.Append("  ").Append(r.GetString(0)).Append(": ").AppendLine(DeviationService.DescribeScope(r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3),
                    r.GetFieldValue<string[]>(4), r.GetFieldValue<string[]>(5), r.IsDBNull(6) ? null : r.GetDecimal(6)));
        }
    }
}
