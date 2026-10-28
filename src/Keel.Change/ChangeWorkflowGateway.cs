using System.Data.Common;
using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Change;

/// <summary>
/// Lets the workflow engine (Keel.Workflow, which must not reference this module) review change requests (CCB) and change notices
/// (release review) like any other object (ADR-0018, ADR-0019). One instance per type. All methods run in the engine's transaction.
/// <para>
/// A change object has no revisions, so the engine's "iteration" is the <em>submission</em>: submitting freezes a snapshot (the change's
/// content, its affected objects and the exact resulting iterations with their content fingerprints) and the engine binds to the id
/// derived from (change, submission number). The signatures therefore cover everything the notice will release.
/// </para>
/// <para>
/// Releasing an ECN is atomic with the final signature: every resulting revision is released, every affected revision becomes Obsolete,
/// the ECN is Released and its ECR (when all its notices are) Implemented, all in the engine's transaction, or none of it.
/// </para>
/// </summary>
internal sealed class ChangeWorkflowGateway(string type, IAuditLog audit, IEnumerable<IChangeObjectGateway> objectGateways) : IWorkflowObjectGateway
{
    private const string Draft = "Draft";
    private const string UnderReview = "UnderReview";

    private readonly IChangeObjectGateway[] _objects = objectGateways.ToArray();

    public string ObjectType => ChangeTypes.ObjectTypeOf(type);

    private bool IsEcn => type == ChangeTypes.Ecn;

    private IChangeObjectGateway ObjectsFor(string objectType) =>
        _objects.FirstOrDefault(g => g.ObjectType == objectType) ?? throw new InvalidOperationException($"No change gateway for objects of type {objectType}.");

    // ---------------------------------------------------------------- lookup

    private static int NextSubmission(ChangeRow r) => r.State == Draft ? r.Submission + 1 : r.Submission;

    private WorkflowObject? Map(ChangeRow? r, Lifecycle lifecycle)
    {
        if (r is null || r.Type != type) return null;
        var n = NextSubmission(r);
        return new WorkflowObject(ObjectType, r.Id, r.Number, r.Title, n.ToString(CultureInfo.InvariantCulture), r.Id, ChangeSnapshot.SubmissionId(r.Id, n),
            $"{r.Number} submission {n}", r.State, lifecycle.State(r.State)?.Label ?? r.State, r.State == UnderReview, true, null, r.Markings);
    }

    private static async Task<Lifecycle> LifecycleAsync(DbConnection conn, DbTransaction tx, string type, CancellationToken ct) =>
        await Lifecycle.LoadAsync(conn, tx, ChangeTypes.LifecycleOf(type), ct) ?? throw new InvalidOperationException($"Lifecycle for {type} missing.");

    public async Task<WorkflowObject?> FindByKeyAsync(DbConnection conn, DbTransaction tx, string key, bool forUpdate, CancellationToken ct) =>
        Map(await ChangeStore.LoadByNumberAsync(conn, tx, key, forUpdate, ct), await LifecycleAsync(conn, tx, type, ct));

    public async Task<WorkflowObject?> FindByRevisionAsync(DbConnection conn, DbTransaction tx, Guid revisionId, bool forUpdate, CancellationToken ct) =>
        Map(await ChangeStore.LoadByIdAsync(conn, tx, revisionId, forUpdate, ct), await LifecycleAsync(conn, tx, type, ct));

    public async Task<IReadOnlyDictionary<Guid, WorkflowObject>> FindManyByRevisionAsync(DbConnection conn, DbTransaction tx, IReadOnlyCollection<Guid> revisionIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, WorkflowObject>();
        var lifecycle = await LifecycleAsync(conn, tx, type, ct);
        foreach (var id in revisionIds)
            if (Map(await ChangeStore.LoadByIdAsync(conn, tx, id, forUpdate: false, ct), lifecycle) is { } o) result[id] = o;
        return result;
    }

    // ---------------------------------------------------------------- submit

    public async Task<GatewayResult> BeginReviewAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var row = await ChangeStore.LoadByIdAsync(conn, tx, obj.Id, forUpdate: true, ct);
        if (row is null || row.Type != type) return GatewayResult.Fail("The change no longer exists.");
        if (row.State != Draft) return GatewayResult.Fail($"{row.Number} is {obj.StateLabel}; only a draft can be submitted for review.");
        if (type == ChangeTypes.Ecr && row.ChangeClass is null)
            return GatewayResult.Fail($"Classify {row.Number} (Class I or II) before submitting it to the CCB.");

        ChangeRow? source = row.SourceId is { } sid ? await ChangeStore.LoadByIdAsync(conn, tx, sid, forUpdate: false, ct) : null;
        if (IsEcn && row.ChangeClass == ChangeClasses.I && (source is null || source.State != "Approved"))
            return GatewayResult.Fail($"{row.Number} is Class I: its change request must be approved by the CCB first.");

        var affected = await ChangeStore.LoadAffectedAsync(conn, tx, row.Id, ct);
        if (IsEcn && !affected.Any(a => a.Action != ChangeActions.None))
            return GatewayResult.Fail($"{row.Number} changes nothing yet. Add at least one object to revise or obsolete before submitting it.");

        // Lock the objects in master-id order (the order every writer uses), re-check them and, for a notice, lock the resulting revisions.
        var snapAffected = new List<SnapshotAffected>();
        var snapResulting = new List<SnapshotResulting>();
        var toReview = new List<(IChangeObjectGateway Gateway, ChangeObject Resulting)>();
        foreach (var a in affected)
        {
            var gateway = ObjectsFor(a.ObjectType);
            var live = await gateway.FindByRevisionAsync(conn, tx, a.RevisionId, forUpdate: IsEcn, ct);
            if (live is null) return GatewayResult.Fail($"{a.Key} no longer exists.");
            var name = AccessPolicy.Evaluate(actor, live.Markings, PlmAction.Read).Permitted ? live.Key : "A listed object";
            var gaps = MarkingDominance.Gaps(row.Markings, live.Markings);
            if (gaps.Count > 0)
                return GatewayResult.Fail($"{name}'s markings are no longer covered by {row.Number}. {MarkingDominance.Describe(gaps)} Raise the markings of {row.Number} and submit again.");
            if (IsEcn && a.Action is ChangeActions.Revise or ChangeActions.Obsolete && live.State != "Released")
                return GatewayResult.Fail($"{name} revision {live.Revision} is {live.StateLabel}, not Released; take it off {row.Number} or restore it.");
            snapAffected.Add(new SnapshotAffected(a.ObjectType, live.Key, live.Name, live.Revision, live.RevisionId, a.Action));

            if (IsEcn && a.Action == ChangeActions.Revise && a.ResultingRevisionId is { } resultingId)
            {
                var resulting = await gateway.FindByRevisionAsync(conn, tx, resultingId, forUpdate: true, ct);
                if (resulting is null) return GatewayResult.Fail($"The resulting revision of {name} no longer exists.");
                var rname = AccessPolicy.Evaluate(actor, resulting.Markings, PlmAction.Read).Permitted ? resulting.Key : "A listed object";
                var rgaps = MarkingDominance.Gaps(row.Markings, resulting.Markings);
                if (rgaps.Count > 0)
                    return GatewayResult.Fail($"{rname} revision {resulting.Revision} has markings that {row.Number} does not cover. {MarkingDominance.Describe(rgaps)} Raise the markings of {row.Number} and submit again.");
                if (resulting.State != "InWork")
                    return GatewayResult.Fail($"{rname} revision {resulting.Revision} is {resulting.StateLabel}; it must be In Work to be reviewed with {row.Number}.");
                if (resulting.CheckedOutBy is not null)
                    return GatewayResult.Fail($"{rname} revision {resulting.Revision} is checked out by {resulting.CheckedOutBy}. Check it in or undo the check-out before submitting {row.Number}.");
                var fingerprint = await gateway.ContentFingerprintAsync(conn, tx, resulting.LatestIterationId, ct)
                    ?? throw new InvalidOperationException("The resulting iteration disappeared.");
                snapResulting.Add(new SnapshotResulting(a.ObjectType, resulting.Key, resulting.Revision, resulting.RevisionId, resulting.LatestIterationLabel, resulting.LatestIterationId, fingerprint));
                toReview.Add((gateway, resulting));
            }
        }
        foreach (var (gateway, resulting) in toReview)
        {
            var begun = await gateway.BeginReviewResultingAsync(conn, tx, resulting, actor, now, ct);
            if (!begun.IsOk) return begun;
        }

        var n = row.Submission + 1;
        var data = new SnapshotData(ChangeSnapshot.CurrentFormat, type, row.Number, n, row.Title, row.Description, row.Priority, row.ChangeClass, row.Reason, source?.Number,
            row.Markings.BannerText(), [.. snapAffected.OrderBy(a => a.Key, StringComparer.Ordinal)], [.. snapResulting.OrderBy(r => r.Key, StringComparer.Ordinal)]);
        var text = ChangeSnapshot.Serialize(data);
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO chg.change_submission (id, change_id, submission_no, snapshot, snapshot_sha256, created_at, created_by) VALUES (@id, @c, @n, @s, @h, @at, @by)",
            (NpgsqlConnection)conn, (NpgsqlTransaction)tx))
        {
            ins.Parameters.AddWithValue("id", ChangeSnapshot.SubmissionId(row.Id, n));
            ins.Parameters.AddWithValue("c", row.Id);
            ins.Parameters.AddWithValue("n", n);
            ins.Parameters.AddWithValue("s", text);
            ins.Parameters.AddWithValue("h", ChangeSnapshot.Sha256(text));
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", ChangeStore.Who(actor));
            await ins.ExecuteNonQueryAsync(ct);
        }
        var moved = await ChangeStore.TransitionAsync(conn, tx, row, UnderReview, "workflow", actor, now, null, null, ct);
        if (!moved.IsOk) return moved;
        await using (var up = new NpgsqlCommand("UPDATE chg.change SET submission_no = @n WHERE id = @id", (NpgsqlConnection)conn, (NpgsqlTransaction)tx))
        {
            up.Parameters.AddWithValue("n", n);
            up.Parameters.AddWithValue("id", row.Id);
            await up.ExecuteNonQueryAsync(ct);
        }
        await ChangeStore.AppendHistoryAsync(conn, tx, row.Id, null, actor, "Submitted for review",
            $"Submission {n}: {affected.Count} object(s){(snapResulting.Count > 0 ? $", {snapResulting.Count} resulting revision(s) locked for review" : "")}.", now, ct);
        await Audit(tx, actor, "change.submit", row, ("submission", n.ToString(CultureInfo.InvariantCulture)), ("objects", affected.Count.ToString(CultureInfo.InvariantCulture)),
            ("resulting", string.Join(",", snapResulting.Select(r => $"{r.Key}:{r.Revision}"))), ("snapshotSha256", Convert.ToHexStringLower(ChangeSnapshot.Sha256(text))));
        return GatewayResult.Ok;
    }

    // ---------------------------------------------------------------- approve / release

    public async Task<GatewayResult> CompleteReleaseAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Guid boundIterationId, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var row = await ChangeStore.LoadByIdAsync(conn, tx, obj.Id, forUpdate: true, ct);
        if (row is null || row.Type != type) return GatewayResult.Fail("The change no longer exists.");
        if (row.State != UnderReview) return GatewayResult.Fail($"{row.Number} is {obj.StateLabel}, not under review; it cannot be {(IsEcn ? "released" : "approved")} by this workflow.");
        if (ChangeSnapshot.SubmissionId(row.Id, row.Submission) != boundIterationId)
            return GatewayResult.Fail($"{row.Number} was submitted again after this review started; the workflow no longer covers it.");

        if (!IsEcn)
        {
            var approved = await ChangeStore.TransitionAsync(conn, tx, row, "Approved", "workflow", actor, now, null, null, ct);
            if (!approved.IsOk) return approved;
            await ChangeStore.AppendHistoryAsync(conn, tx, row.Id, null, actor, "Approved by the CCB", $"Submission {row.Submission}: every CCB member approved.", now, ct);
            await Audit(tx, actor, "change.approve", row, ("submission", row.Submission.ToString(CultureInfo.InvariantCulture)));
            return GatewayResult.Ok;
        }

        var snapshotText = await LoadSnapshotAsync(conn, tx, boundIterationId, ct);
        if (snapshotText is null) return GatewayResult.Fail("The review snapshot of the change notice is missing.");
        var data = ChangeSnapshot.Parse(snapshotText);

        var released = new List<string>();
        var obsoleted = new List<string>();
        foreach (var a in await ChangeStore.LoadAffectedAsync(conn, tx, row.Id, ct))
        {
            var gateway = ObjectsFor(a.ObjectType);
            var affected = await gateway.FindByRevisionAsync(conn, tx, a.RevisionId, forUpdate: true, ct);
            if (affected is null) return GatewayResult.Fail($"{a.Key} no longer exists.");
            if (a.Action is ChangeActions.Revise or ChangeActions.Obsolete && affected.State != "Released")
                return GatewayResult.Fail($"{a.Key} revision {affected.Revision} is {affected.StateLabel}; the change notice can no longer retire it.");

            if (a.Action == ChangeActions.Revise)
            {
                var frozen = data.Resulting.FirstOrDefault(r => r.RevisionId == a.ResultingRevisionId);
                if (frozen is null || a.ResultingRevisionId is not { } resultingId) return GatewayResult.Fail($"The resulting revision of {a.Key} is not part of the reviewed snapshot.");
                var resulting = await gateway.FindByRevisionAsync(conn, tx, resultingId, forUpdate: false, ct);
                if (resulting is null) return GatewayResult.Fail($"The resulting revision of {a.Key} no longer exists.");
                if (resulting.LatestIterationId != frozen.IterationId)
                    return GatewayResult.Fail($"{resulting.Key} revision {resulting.Revision} changed after it was reviewed; the signatures no longer cover it.");
                var live = await gateway.ContentFingerprintAsync(conn, tx, frozen.IterationId, ct);
                if (live != frozen.Fingerprint)
                    return GatewayResult.Fail($"The content of {resulting.Key} revision {resulting.Revision} does not match what was reviewed and signed.");
                var rel = await gateway.ReleaseResultingAsync(conn, tx, resulting, actor, now, ct);
                if (!rel.IsOk) return rel;
                released.Add($"{resulting.Key} {resulting.Revision}");
            }
            var obs = await gateway.ObsoleteAsync(conn, tx, affected, actor, now, ct);
            if (!obs.IsOk) return obs;
            obsoleted.Add($"{affected.Key} {affected.Revision}");
        }
        await ChangeStore.ReleaseBindingsAsync(conn, tx, row.Id, ct);
        var done = await ChangeStore.TransitionAsync(conn, tx, row, "Released", "workflow", actor, now, null, null, ct);
        if (!done.IsOk) return done;

        var implemented = await ImplementEcrAsync(conn, tx, row, actor, now, ct);
        if (implemented is { IsOk: false } failed) return failed;
        await ChangeStore.AppendHistoryAsync(conn, tx, row.Id, null, actor, "Released",
            $"Released {(released.Count == 0 ? "no revisions" : string.Join(", ", released))}; obsoleted {(obsoleted.Count == 0 ? "none" : string.Join(", ", obsoleted))}.", now, ct);
        await Audit(tx, actor, "change.release", row, ("submission", row.Submission.ToString(CultureInfo.InvariantCulture)), ("released", string.Join(",", released)),
            ("obsoleted", string.Join(",", obsoleted)), ("ecr", implemented is null ? null : "implemented"));
        return GatewayResult.Ok;
    }

    /// <summary>
    /// When the released notice was the last open one of its change request, the request becomes Implemented (lifecycle <c>change</c> transition).
    /// Null when there is nothing to do.
    /// </summary>
    private static async Task<GatewayResult?> ImplementEcrAsync(DbConnection conn, DbTransaction tx, ChangeRow ecn, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        if (ecn.SourceId is not { } ecrId) return null;
        var ecr = await ChangeStore.LoadByIdAsync(conn, tx, ecrId, forUpdate: true, ct);
        if (ecr is null || ecr.State != "Approved") return null;
        await using (var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM chg.change c WHERE c.source_id = @id AND c.id <> @self AND c.lifecycle_state IN ('Draft', 'UnderReview')", (NpgsqlConnection)conn, (NpgsqlTransaction)tx))
        {
            cmd.Parameters.AddWithValue("id", ecrId);
            cmd.Parameters.AddWithValue("self", ecn.Id);
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0) return null;
        }
        var moved = await ChangeStore.TransitionAsync(conn, tx, ecr, "Implemented", "change", actor, now, null, null, ct);
        if (!moved.IsOk) return moved;
        await ChangeStore.AppendHistoryAsync(conn, tx, ecr.Id, null, actor, "Implemented", $"Implemented by {ecn.Number}.", now, ct);
        return GatewayResult.Ok;
    }

    // ---------------------------------------------------------------- return, reject

    public Task<GatewayResult> ReturnToInWorkAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct) =>
        ReturnAsync(conn, tx, obj, actor, now, rejected: false, ct);

    public Task<GatewayResult> CompleteRejectionAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct) =>
        ReturnAsync(conn, tx, obj, actor, now, rejected: true, ct);

    /// <summary>
    /// Review cancelled or rejected. An ECN goes back to Draft and its resulting revisions back to In Work (editable). A cancelled ECR goes back
    /// to Draft; a rejected ECR becomes Rejected (it can be reworked into a draft or closed).
    /// </summary>
    private async Task<GatewayResult> ReturnAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, bool rejected, CancellationToken ct)
    {
        var row = await ChangeStore.LoadByIdAsync(conn, tx, obj.Id, forUpdate: true, ct);
        if (row is null || row.Type != type) return GatewayResult.Fail("The change no longer exists.");
        if (row.State != UnderReview) return GatewayResult.Fail($"{row.Number} is {obj.StateLabel}, not under review.");
        var returned = 0;
        if (IsEcn)
            foreach (var a in await ChangeStore.LoadAffectedAsync(conn, tx, row.Id, ct))
            {
                if (a.ResultingRevisionId is not { } resultingId) continue;
                var gateway = ObjectsFor(a.ObjectType);
                var resulting = await gateway.FindByRevisionAsync(conn, tx, resultingId, forUpdate: true, ct);
                if (resulting is null || resulting.State != "UnderReview") continue;
                var back = await gateway.ReturnResultingAsync(conn, tx, resulting, actor, now, ct);
                if (!back.IsOk) return back;
                returned++;
            }
        var to = !IsEcn && rejected ? "Rejected" : Draft;
        var moved = await ChangeStore.TransitionAsync(conn, tx, row, to, "workflow", actor, now, null, null, ct);
        if (!moved.IsOk) return moved;
        var what = rejected ? "Rejected in review" : "Review cancelled";
        await ChangeStore.AppendHistoryAsync(conn, tx, row.Id, null, actor, what,
            $"{(to == "Rejected" ? "The change request was rejected." : "Back to Draft; editable again.")}{(returned > 0 ? $" {returned} resulting revision(s) returned to In Work." : "")}", now, ct);
        await Audit(tx, actor, rejected ? "change.reject" : "change.review_cancel", row, ("to", to), ("returnedRevisions", returned.ToString(CultureInfo.InvariantCulture)));
        return GatewayResult.Ok;
    }

    // ---------------------------------------------------------------- signed content

    public async Task<IterationContent?> LoadIterationContentAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        var text = await LoadSnapshotAsync(conn, tx, iterationId, ct);
        if (text is null) return null;
        var data = ChangeSnapshot.Parse(text);
        // The live fingerprint of every resulting iteration goes into the signed content (not the stored one), so editing a resulting
        // part's description, attributes, links or structure after signing changes the hash and verification reports it.
        var live = new Dictionary<Guid, string?>();
        foreach (var r in data.Resulting) live[r.IterationId] = await ObjectsFor(r.ObjectType).ContentFingerprintAsync(conn, tx, r.IterationId, ct);
        var attributes = ChangeSnapshot.Attributes(data, Convert.ToHexStringLower(ChangeSnapshot.Sha256(text)), live);
        return new IterationContent(iterationId, ObjectType, data.Number, data.Submission.ToString(CultureInfo.InvariantCulture),
            $"{data.Number} submission {data.Submission}", ChangeSnapshot.Describe(data), attributes.ToJsonString());
    }

    private static async Task<string?> LoadSnapshotAsync(DbConnection conn, DbTransaction tx, Guid submissionId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT s.snapshot FROM chg.change_submission s WHERE s.id = @id", (NpgsqlConnection)conn, (NpgsqlTransaction)tx);
        cmd.Parameters.AddWithValue("id", submissionId);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    private Task<AuditRecord> Audit(DbTransaction tx, Subject actor, string action, ChangeRow row, params (string, string?)[] details) =>
        audit.AppendAsync((NpgsqlTransaction)tx, ChangeStore.Entry(actor, action, AuditOutcome.Success, ObjectType, row.Number, null, details));
}
