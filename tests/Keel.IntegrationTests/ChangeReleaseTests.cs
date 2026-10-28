using Keel.Audit;
using Keel.Change;
using Keel.Core;
using Keel.Workflow;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>ECN release through the workflow engine: submit, sign with step-up, atomic release, rejection, cancellation, races, tamper detection.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ChangeReleaseTests(PostgresFixture pg)
{
    private static Subject Alice => ChangeEnv.Alice;

    [Fact]
    public async Task Submitting_needs_the_resulting_revisions_checked_in_and_locks_them_for_review()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();
        var ecn = (await env.NewEcnAsync()).Number;

        // Nothing to review yet.
        var empty = await env.Flow.StartAsync(Alice, new StartReviewCommand("ecn", ecn, new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob"] }), null);
        Assert.Equal(OutcomeKind.Conflict, empty.Kind);
        Assert.Contains("changes nothing yet", empty.Errors[0], StringComparison.Ordinal);

        await env.AddAsync(Alice, ecn, part);
        Assert.True((await env.Parts.CheckOutAsync(Alice, part, null)).IsOk);
        var checkedOut = await env.Flow.StartAsync(Alice, new StartReviewCommand("ecn", ecn, new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob"] }), null);
        Assert.Equal(OutcomeKind.Conflict, checkedOut.Kind);
        Assert.Contains("checked out by alice", checkedOut.Errors[0], StringComparison.Ordinal);
        Assert.Equal("Draft", (await env.ChangeAsync(ecn)).State);
        Assert.Equal("InWork", (await env.PartAsync(part)).Revision.State);

        Assert.True((await env.Parts.UndoCheckOutAsync(Alice, part, null)).IsOk);
        var started = await env.SubmitAsync(ecn);
        Assert.Equal("Active", started.Status);
        Assert.Equal("ecn", started.Target.ObjectType);
        Assert.Equal(ecn, started.Target.Key);

        // The ECN and its resulting revision are locked; revision A is untouched.
        var detail = await env.ChangeAsync(ecn);
        Assert.Equal(("UnderReview", true, 1), (detail.State, detail.IsLocked, detail.Submission));
        Assert.Equal("UnderReview", (await env.PartAsync(part)).Revision.State);
        Assert.Equal("Released", (await env.PartAsync(part, "A")).Revision.State);
        Assert.Contains("locked", (await env.Parts.CheckOutAsync(Alice, part, null)).Errors[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Conflict, (await env.Change.UpdateAsync(Alice, ecn, new UpdateChangeCommand("New title", null, "High", null), null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Change.AddAffectedAsync(Alice, ecn, new AddAffectedCommand("part", part, "none"), null)).Kind);
        // The part's own review cannot be started, and an administrator override cannot bypass the DB lock on the ECN row.
        await using var cmd = env.Ds.CreateCommand("UPDATE chg.change SET title = 'x' WHERE number = $1");
        cmd.Parameters.AddWithValue(ecn);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task Final_approval_releases_the_resulting_revisions_obsoletes_the_old_ones_and_implements_the_ecr_atomically()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();
        var other = await env.ReleasedPartAsync("Cover plate");

        // PR -> ECR -> CCB approval -> ECN (Class I), the chain of A8.
        var pr = (await env.NewChangeAsync(Alice, ChangeTypes.Pr, "Bracket cracks under load")).Number;
        var ecr = (await env.Change.CreateFromAsync(Alice, pr, ChangeTypes.Ecr, new CreateFromCommand(Title: "Thicken the bracket"), null)).Value!;
        Assert.Equal(pr, ecr.Source!.Number);
        Assert.True((await env.Change.ClassifyAsync(Alice, ecr.Number, ChangeClasses.I, null)).IsOk);
        await env.AddAsync(Alice, ecr.Number, part, ChangeActions.Revise);
        var ccb = await env.SubmitAsync(ecr.Number, objectType: "ecr");
        Assert.Equal("UnderReview", (await env.ChangeAsync(ecr.Number)).State);
        await env.ApproveAllAsync(ccb);
        Assert.Equal("Approved", (await env.ChangeAsync(ecr.Number)).State);

        var ecnDetail = (await env.Change.CreateFromAsync(Alice, ecr.Number, ChangeTypes.Ecn, new CreateFromCommand(), null)).Value!;
        var ecn = ecnDetail.Number;
        Assert.Equal(("I", ecr.Number), (ecnDetail.ChangeClass, ecnDetail.Source!.Number));
        // The ECR's object was carried over and revised.
        Assert.Equal("B", Assert.Single(ecnDetail.Affected).ResultingRevision);
        await env.AddAsync(Alice, ecn, other, ChangeActions.Obsolete);
        await env.CheckedInEditAsync(Alice, part, "Thicker wall");

        var started = await env.SubmitAsync(ecn);
        var afterReview = await env.SignAsync(ChangeEnv.Rita, started.Tasks.Single(t => t.Status == "Open").Id, "Reviewed");
        var afterBob = await env.SignAsync(ChangeEnv.Bob, WorkflowEnv.TaskOf(afterReview, "bob"), "Approved");
        // One approval still missing: nothing is released yet.
        Assert.Equal("UnderReview", (await env.ChangeAsync(ecn)).State);
        Assert.Equal("Released", (await env.PartAsync(other)).Revision.State);
        Assert.Equal("UnderReview", (await env.PartAsync(part)).Revision.State);

        var done = await env.SignAsync(ChangeEnv.Carol, WorkflowEnv.TaskOf(afterBob, "carol"), "Approved");
        Assert.Equal("Approved", done.Status);

        // Everything happened together.
        var released = await env.ChangeAsync(ecn);
        Assert.Equal(("Released", true), (released.State, released.IsLocked));
        var b = await env.PartAsync(part);
        Assert.Equal(("B", "Released", true, "B.2"), (b.Revision.Revision, b.Revision.State, b.Revision.IsLatest, b.Iteration.Label));
        Assert.Equal("Obsolete", (await env.PartAsync(part, "A")).Revision.State);
        Assert.Equal("Obsolete", (await env.PartAsync(other)).Revision.State); // the 'obsolete' action
        Assert.Equal("Implemented", (await env.ChangeAsync(ecr.Number)).State);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM chg.affected_object WHERE binding"));
        Assert.Contains(released.History, h => h.Action == "Released" && h.Detail!.Contains($"{part} B", StringComparison.Ordinal));
        Assert.Contains((await env.ChangeAsync(ecr.Number)).History, h => h.Action == "Implemented" && h.Detail!.Contains(ecn, StringComparison.Ordinal));

        // The lifecycle moved through the 'change' tag: obsoleting is not available to the manual path.
        Assert.False((await env.Parts.SetStateAsync(Alice, part, "Obsolete", null, false, null)).IsOk);

        // Signatures cover the notice and verify; the audit chain is intact and records each step.
        var verified = (await env.Flow.VerifyInstanceAsync(Alice, done.Id, null)).Value!;
        Assert.Equal(3, verified.Count);
        Assert.All(verified, v => Assert.True(v.Valid, string.Join("; ", v.Problems)));
        var audit = await env.AuditAsync();
        foreach (var action in new[] { "change.submit", "change.release", "change.approve", "workflow.sign", "workflow.complete" })
            Assert.Contains(audit, e => e.Action == action && e.Outcome == AuditOutcome.Success);
        var release = Assert.Single(audit, e => e.Action == "change.release" && e.ObjectId == ecn);
        Assert.Contains($"{part}:B".Replace(":", " ", StringComparison.Ordinal), release.Payload, StringComparison.Ordinal);
        Assert.True((await env.VerifyAuditChainAsync()).Valid);

        // A released change notice is immutable at the database too, and the tasks it left can still be worked.
        await using var cmd = env.Ds.CreateCommand("UPDATE chg.change SET description = 'rewritten' WHERE number = $1");
        cmd.Parameters.AddWithValue(ecn);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync())).SqlState);
        Assert.Equal(OutcomeKind.Conflict, (await env.Change.UpdateAsync(Alice, ecn, new UpdateChangeCommand("x", null, "Low", null), null)).Kind);
        Assert.Contains("locked", (await env.Parts.CheckOutAsync(Alice, part, null)).Errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejection_returns_the_notice_and_its_revisions_to_work_and_the_notice_can_be_submitted_again()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var (ecn, part) = await env.ReadyEcnAsync();
        var started = await env.SubmitAsync(ecn);
        var afterReview = await env.SignAsync(ChangeEnv.Rita, started.Tasks.Single(t => t.Status == "Open").Id, "Reviewed");
        var rejected = await env.SignAsync(ChangeEnv.Bob, WorkflowEnv.TaskOf(afterReview, "bob"), "Rejected", "Wall thickness not justified");
        Assert.Equal("Rejected", rejected.Status);

        var detail = await env.ChangeAsync(ecn);
        Assert.Equal("Draft", detail.State);
        Assert.Contains(detail.History, h => h.Action == "Rejected in review" && h.Detail!.Contains("returned to In Work", StringComparison.Ordinal));
        var b = await env.PartAsync(part);
        Assert.Equal(("InWork", "B.2"), (b.Revision.State, b.Iteration.Label));
        Assert.Equal("Released", (await env.PartAsync(part, "A")).Revision.State);
        // Still held by this notice: nobody else can revise the part meanwhile.
        var other = (await env.NewEcnAsync(title: "Competing")).Number;
        Assert.Equal(OutcomeKind.Conflict, (await env.Change.AddAffectedAsync(Alice, other, new AddAffectedCommand("part", part, "revise"), null)).Kind);

        // Rework, submit again (submission 2) and release: the first submission's signatures still verify against their own snapshot.
        await env.CheckedInEditAsync(Alice, part, "Wall thickness justified by analysis");
        var second = await env.SubmitAsync(ecn);
        Assert.NotEqual(started.Tasks[0].Id, second.Tasks[0].Id);
        var done = await env.ApproveAllAsync(second);
        Assert.Equal("Approved", done.Status);
        Assert.Equal(("Released", 2), ((await env.ChangeAsync(ecn)).State, (await env.ChangeAsync(ecn)).Submission));
        Assert.Equal("B.3", (await env.PartAsync(part)).Iteration.Label);
        Assert.All((await env.Flow.VerifyInstanceAsync(Alice, rejected.Id, null)).Value!, v => Assert.True(v.Valid, string.Join("; ", v.Problems)));
        Assert.All((await env.Flow.VerifyInstanceAsync(Alice, done.Id, null)).Value!, v => Assert.True(v.Valid, string.Join("; ", v.Problems)));
        Assert.True((await env.VerifyAuditChainAsync()).Valid);
    }

    [Fact]
    public async Task Cancelling_a_draft_abandons_the_resulting_revisions_and_frees_the_originals()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var (ecn, part) = await env.ReadyEcnAsync();
        Assert.True((await env.Parts.CheckOutAsync(Alice, part, null)).IsOk); // open check-out, discarded with the revision

        // A reason code is required, and it must be one of the notice's.
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.TransitionAsync(Alice, ecn, "Cancelled", null, null, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.TransitionAsync(Alice, ecn, "Cancelled", "NotAProblem", null, null)).Kind);
        var cancelled = await env.Change.TransitionAsync(Alice, ecn, "Cancelled", "ScopeChange", "Replaced by a bigger change", "corr-cancel");
        Assert.True(cancelled.IsOk, string.Join("; ", cancelled.Errors));
        Assert.Equal(("Cancelled", "ScopeChange"), (cancelled.Value!.State, cancelled.Value.CloseReasonCode));
        Assert.Contains(cancelled.Value.History, h => h.Action == "Resulting revisions abandoned");

        // No orphan: B exists and is plainly Obsolete, A is the latest and Released again, nothing is held, no check-out remains.
        var a = await env.PartAsync(part);
        Assert.Equal(("A", "Released", true, null), (a.Revision.Revision, a.Revision.State, a.Revision.IsLatest, a.Checkout?.UserId));
        var b = await env.PartAsync(part, "B");
        Assert.Equal(("Obsolete", false, 2), (b.Revision.State, b.Revision.IsLatest, b.Revision.LatestIteration));
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM plm.part_checkout"));
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM chg.affected_object WHERE binding"));

        // The part can be revised again by a new notice (revision C), and a cancelled notice is final.
        var next = (await env.NewEcnAsync(title: "Second attempt")).Number;
        Assert.Equal("C", (await env.AddAsync(Alice, next, part)).Affected.Single().ResultingRevision);
        Assert.Equal(OutcomeKind.Conflict, (await env.Change.TransitionAsync(Alice, ecn, "Draft", null, null, null)).Kind);

        // Under review a notice cannot simply be cancelled: its review is cancelled first (workflow), which returns it to Draft.
        var (ecn2, part2) = await env.ReadyEcnAsync();
        var started = await env.SubmitAsync(ecn2);
        var refused = await env.Change.TransitionAsync(Alice, ecn2, "Cancelled", "Withdrawn", null, null);
        Assert.Equal(OutcomeKind.Conflict, refused.Kind);
        var review = await env.Flow.CancelAsync(Alice, started.Id, "Wrong approvers", null);
        Assert.True(review.IsOk, string.Join("; ", review.Errors));
        Assert.Equal("Draft", (await env.ChangeAsync(ecn2)).State);
        Assert.Equal("InWork", (await env.PartAsync(part2)).Revision.State);
        Assert.True((await env.Change.TransitionAsync(Alice, ecn2, "Cancelled", "Withdrawn", null, null)).IsOk);
    }

    [Fact]
    public async Task Two_approvers_signing_at_once_release_exactly_once()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var (ecn, part) = await env.ReadyEcnAsync();
        var started = await env.SubmitAsync(ecn);
        var afterReview = await env.SignAsync(ChangeEnv.Rita, started.Tasks.Single(t => t.Status == "Open").Id, "Reviewed");

        var bob = env.Flow.SignAsync(WorkflowEnv.Fresh(ChangeEnv.Bob), WorkflowEnv.TaskOf(afterReview, "bob"), "Approved", null, "race-bob");
        var carol = env.Flow.SignAsync(WorkflowEnv.Fresh(ChangeEnv.Carol), WorkflowEnv.TaskOf(afterReview, "carol"), "Approved", null, "race-carol");
        var results = await Task.WhenAll(bob, carol);
        Assert.All(results, r => Assert.True(r.IsOk, string.Join("; ", r.Errors)));

        Assert.Equal("Released", (await env.ChangeAsync(ecn)).State);
        Assert.Equal("Released", (await env.PartAsync(part)).Revision.State);
        Assert.Equal("Obsolete", (await env.PartAsync(part, "A")).Revision.State);
        Assert.Single(await env.AuditAsync(), e => e.Action == "change.release");
        Assert.Equal(3, await env.ScalarAsync("SELECT count(*) FROM wf.signature WHERE object_type = 'ecn'"));
        Assert.True((await env.VerifyAuditChainAsync()).Valid);
    }

    [Fact]
    public async Task The_final_approval_and_a_review_cancellation_never_both_win()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        for (var round = 0; round < 6; round++)
        {
            var (ecn, part) = await env.ReadyEcnAsync(edit: $"Round {round}");
            var started = await env.SubmitAsync(ecn);
            var afterBob = await env.SignAsync(ChangeEnv.Rita, started.Tasks.Single(t => t.Status == "Open").Id, "Reviewed");
            afterBob = await env.SignAsync(ChangeEnv.Bob, WorkflowEnv.TaskOf(afterBob, "bob"), "Approved");

            var sign = env.Flow.SignAsync(WorkflowEnv.Fresh(ChangeEnv.Carol), WorkflowEnv.TaskOf(afterBob, "carol"), "Approved", null, "race-sign");
            var cancel = env.Flow.CancelAsync(Alice, started.Id, "Changed my mind", "race-cancel");
            var (signed, cancelled) = (await sign, await cancel);

            var state = (await env.ChangeAsync(ecn)).State;
            var revision = await env.PartAsync(part);
            if (signed.IsOk && cancelled.IsOk)
                Assert.Fail("Both the final approval and the cancellation succeeded.");
            if (state == "Released")
            {
                Assert.True(signed.IsOk);
                Assert.Equal(("Released", "Obsolete"), (revision.Revision.State, (await env.PartAsync(part, "A")).Revision.State));
            }
            else
            {
                Assert.True(cancelled.IsOk, $"{signed.Kind}: {string.Join("; ", signed.Errors)}");
                Assert.Equal(("Draft", "InWork", "Released"), (state, revision.Revision.State, (await env.PartAsync(part, "A")).Revision.State));
            }
        }
        Assert.True((await env.VerifyAuditChainAsync()).Valid);
    }

    [Fact]
    public async Task The_signature_covers_the_notice_and_the_resulting_content_so_tampering_is_detected()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var (ecn, part) = await env.ReadyEcnAsync();
        var started = await env.SubmitAsync(ecn);
        var afterReview = await env.SignAsync(ChangeEnv.Rita, started.Tasks.Single(t => t.Status == "Open").Id, "Reviewed");
        var afterBob = await env.SignAsync(ChangeEnv.Bob, WorkflowEnv.TaskOf(afterReview, "bob"), "Approved");

        // The reviewers see what they sign: the notice text and the exact resulting iteration with its content fingerprint.
        var task = (await env.Flow.GetTaskAsync(ChangeEnv.Carol, WorkflowEnv.TaskOf(afterBob, "carol"), null)).Value!;
        Assert.Contains("Revise bracket", task.Description, StringComparison.Ordinal);
        Assert.Contains($"{part} revision A: revise", task.Description, StringComparison.Ordinal);
        Assert.Contains($"{part} revision B (B.2)", task.Description, StringComparison.Ordinal);
        Assert.Contains(task.Attributes!, kv => kv.Key == "resulting 1" && kv.Value!.ToString().Contains("content ", StringComparison.Ordinal));

        // Someone with database access edits the resulting iteration after two signatures were given (bypassing the immutability trigger).
        await env.Db.ExecuteAsSuperuserAsync("SET session_replication_role = replica; UPDATE plm.part_iteration SET description = 'Tampered' WHERE revision_id = (SELECT id FROM plm.part_revision WHERE revision = 'B')");
        var verified = (await env.Flow.VerifyInstanceAsync(Alice, started.Id, null)).Value!;
        Assert.Equal(2, verified.Count);
        Assert.All(verified, v => Assert.False(v.Valid));

        // The release itself refuses to proceed on content that is not what was reviewed: the final signature is refused, nothing is released.
        var last = await env.Flow.SignAsync(WorkflowEnv.Fresh(ChangeEnv.Carol), WorkflowEnv.TaskOf(afterBob, "carol"), "Approved", null, null);
        Assert.Equal(OutcomeKind.Conflict, last.Kind);
        Assert.Contains("does not match what was reviewed", last.Errors[0], StringComparison.Ordinal);
        Assert.Equal("UnderReview", (await env.ChangeAsync(ecn)).State);
        Assert.Equal("UnderReview", (await env.PartAsync(part)).Revision.State);
        Assert.Equal("Released", (await env.PartAsync(part, "A")).Revision.State);

        // Restoring the content makes everything verify and release again; editing the stored notice snapshot is caught too.
        await env.Db.ExecuteAsSuperuserAsync("SET session_replication_role = replica; UPDATE plm.part_iteration SET description = 'Revised description' WHERE revision_id = (SELECT id FROM plm.part_revision WHERE revision = 'B')");
        Assert.All((await env.Flow.VerifyInstanceAsync(Alice, started.Id, null)).Value!, v => Assert.True(v.Valid, string.Join("; ", v.Problems)));
        await env.Db.ExecuteAsSuperuserAsync("SET session_replication_role = replica; UPDATE chg.change_submission SET snapshot = replace(snapshot, 'Revise bracket', 'Something else')");
        Assert.All((await env.Flow.VerifyInstanceAsync(Alice, started.Id, null)).Value!, v => Assert.False(v.Valid));
        await env.Db.ExecuteAsSuperuserAsync("SET session_replication_role = replica; UPDATE chg.change_submission SET snapshot = replace(snapshot, 'Something else', 'Revise bracket')");
        var done = await env.SignAsync(ChangeEnv.Carol, WorkflowEnv.TaskOf(afterBob, "carol"), "Approved");
        Assert.Equal("Approved", done.Status);
        Assert.Equal("Released", (await env.ChangeAsync(ecn)).State);
        Assert.All((await env.Flow.VerifyInstanceAsync(Alice, done.Id, null)).Value!, v => Assert.True(v.Valid));
    }
}
