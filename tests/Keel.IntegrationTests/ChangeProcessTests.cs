using Keel.Audit;
using Keel.Change;
using Keel.Core;
using Keel.Workflow;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>The PR → ECR → ECN → CT process rules (A8), reason codes, lifecycle tags and database guards.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ChangeProcessTests(PostgresFixture pg)
{
    private static Subject Alice => ChangeEnv.Alice;

    [Fact]
    public async Task Class_I_needs_an_ecr_approved_by_the_ccb_while_class_II_may_open_an_ecn_directly()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var direct = await env.Change.CreateAsync(Alice, new CreateChangeCommand(ChangeTypes.Ecn, "Direct Class I", null, "High", null, ChangeEnv.Itar(), ChangeClasses.I), null);
        Assert.Equal(OutcomeKind.Invalid, direct.Kind);
        Assert.Contains("approved by the CCB", direct.Errors[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.CreateAsync(Alice, new CreateChangeCommand(ChangeTypes.Ecn, "No class", null, "High", null, ChangeEnv.Itar()), null)).Kind);
        var classII = await env.NewChangeAsync(Alice, ChangeTypes.Ecn, "Direct Class II", cls: ChangeClasses.II);
        Assert.Equal(("ECN-000001", "II", null), (classII.Number, classII.ChangeClass, classII.Source?.Number));

        // A request that is not classified, or Class I and not approved, opens no notice.
        var ecr = await env.NewChangeAsync(Alice, ChangeTypes.Ecr, "Widen the slot");
        var unclassified = await env.Change.CreateFromAsync(Alice, ecr.Number, ChangeTypes.Ecn, new CreateFromCommand(), null);
        Assert.Equal(OutcomeKind.Conflict, unclassified.Kind);
        Assert.Contains("not classified", unclassified.Errors[0], StringComparison.Ordinal);
        Assert.True((await env.Change.ClassifyAsync(Alice, ecr.Number, ChangeClasses.I, null)).IsOk);
        var draftI = await env.Change.CreateFromAsync(Alice, ecr.Number, ChangeTypes.Ecn, new CreateFromCommand(), null);
        Assert.Equal(OutcomeKind.Conflict, draftI.Kind);
        Assert.Contains("after the CCB approved", draftI.Errors[0], StringComparison.Ordinal);
        // Naming a different class on a notice made from a request is refused.
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.CreateFromAsync(Alice, ecr.Number, ChangeTypes.Ecn, new CreateFromCommand(ChangeClass: "II"), null)).Kind);
        // The submit step needs the class too.
        var fresh = await env.NewChangeAsync(Alice, ChangeTypes.Ecr, "Unclassified");
        var submit = await env.Flow.StartAsync(Alice, new StartReviewCommand("ecr", fresh.Number, new Dictionary<string, string[]> { ["ccb"] = ["bob"] }), null);
        Assert.Equal(OutcomeKind.Conflict, submit.Kind);
        Assert.Contains("Classify", submit.Errors[0], StringComparison.Ordinal);

        // The database repeats the rule: a Class I notice whose request is not approved cannot be inserted by any route.
        await using var insert = env.Ds.CreateCommand(
            "INSERT INTO chg.change (id, change_type, number, title, priority, change_class, source_id, requester_sub, requester_username, requester_name, lifecycle_id, lifecycle_state, " +
            "export_jurisdiction, cui_categories, distribution_statement, owning_org, export_authorizations, created_at, created_by, state_changed_at, state_changed_by) " +
            "SELECT gen_random_uuid(), 'ECN', 'ECN-999999', 'Sneaky', 'High', 'I', id, 'x', 'x', 'x', 'ecn-default', 'Draft', 'None', '{}', 'A', 'ENG', '{}', now(), 'x', now(), 'x' FROM chg.change WHERE number = $1");
        insert.Parameters.AddWithValue(ecr.Number);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync())).SqlState);

        // A rejected ECR is not a way to a notice either; it can be reworked or closed with a reason.
        var rejectedEcr = await env.NewChangeAsync(Alice, ChangeTypes.Ecr, "Rejected one", cls: ChangeClasses.I);
        var ccb = await env.SubmitAsync(rejectedEcr.Number, objectType: "ecr");
        await env.SignAsync(ChangeEnv.Bob, WorkflowEnv.TaskOf(ccb, "bob"), "Rejected", "Not justified");
        var afterReject = await env.ChangeAsync(rejectedEcr.Number);
        Assert.Equal("Rejected", afterReject.State);
        Assert.Equal(OutcomeKind.Conflict, (await env.Change.CreateFromAsync(Alice, rejectedEcr.Number, ChangeTypes.Ecn, new CreateFromCommand(), null)).Kind);
        Assert.True((await env.Change.TransitionAsync(Alice, rejectedEcr.Number, "Draft", null, null, null)).IsOk);
        Assert.True((await env.Change.TransitionAsync(Alice, rejectedEcr.Number, "Closed", "Withdrawn", "Dropped", null)).IsOk);

        // A Class II request may open its notice from Draft; the notice takes the request's class.
        var ecrII = await env.NewChangeAsync(Alice, ChangeTypes.Ecr, "Minor tweak", cls: ChangeClasses.II);
        var fromDraft = await env.Change.CreateFromAsync(Alice, ecrII.Number, ChangeTypes.Ecn, new CreateFromCommand(), null);
        Assert.True(fromDraft.IsOk, string.Join("; ", fromDraft.Errors));
        Assert.Equal(("II", ecrII.Number), (fromDraft.Value!.ChangeClass, fromDraft.Value.Source!.Number));
        Assert.Contains((await env.ChangeAsync(ecrII.Number)).Derived, d => d.Number == fromDraft.Value.Number);
    }

    [Fact]
    public async Task A_change_request_created_from_a_problem_report_copies_it_and_the_markings_may_not_loosen()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();
        var pr = await env.NewChangeAsync(Alice, ChangeTypes.Pr, "Bracket cracks", priority: "Critical");
        await env.AddAsync(Alice, pr.Number, part, ChangeActions.None);
        // A problem report only references objects.
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.AddAffectedAsync(Alice, pr.Number, new AddAffectedCommand("part", part, "revise"), null)).Kind);

        // Markings below the problem report's are refused: title and description are copied.
        var lower = await env.Change.CreateFromAsync(Alice, pr.Number, ChangeTypes.Ecr, new CreateFromCommand(Markings: ChangeEnv.Open), null);
        Assert.Equal(OutcomeKind.Invalid, lower.Kind);
        Assert.Contains("must cover", lower.Errors[0], StringComparison.Ordinal);

        var ecr = (await env.Change.CreateFromAsync(Alice, pr.Number, ChangeTypes.Ecr, new CreateFromCommand(), "corr-from")).Value!;
        Assert.Equal(("Bracket cracks", "Critical", "Synthetic description", pr.Number), (ecr.Title, ecr.Priority, ecr.Description, ecr.Source!.Number));
        Assert.Equal(Markings.AggregateBanner([ChangeEnv.Itar()]), ecr.Banner);
        // The referenced part is carried over as an object to revise (it is released).
        var row = Assert.Single(ecr.Affected);
        Assert.Equal((part, "revise", null), (row.Key, row.Action, row.ResultingRevision));
        Assert.Contains((await env.ChangeAsync(pr.Number)).Derived, d => d.Number == ecr.Number);
        Assert.Contains(ecr.History, h => h.Action == "Created" && h.Detail == $"Created from {pr.Number}.");
        // An ECR plans the change; it does not revise anything yet, and holds nothing.
        Assert.Equal("A", (await env.PartAsync(part)).Revision.Revision);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM chg.affected_object WHERE binding"));

        // One PR may spawn several ECRs; a PR cannot be created from anything, and only PR->ECR and ECR->ECN conversions exist.
        Assert.True((await env.Change.CreateFromAsync(Alice, pr.Number, ChangeTypes.Ecr, new CreateFromCommand(Title: "Second request"), null)).IsOk);
        Assert.Equal(2, (await env.ChangeAsync(pr.Number)).Derived.Count);
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.CreateFromAsync(Alice, pr.Number, ChangeTypes.Ecn, new CreateFromCommand(), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.CreateFromAsync(Alice, ecr.Number, ChangeTypes.Ecr, new CreateFromCommand(), null)).Kind);
    }

    [Fact]
    public async Task Leaving_a_lifecycle_needs_a_reason_code_and_process_rules_guard_the_transitions()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var pr = (await env.NewChangeAsync(Alice, ChangeTypes.Pr, "Noise")).Number;

        // Reject and close need a code from their own scope.
        Assert.Contains("Choose a reason", (await env.Change.TransitionAsync(Alice, pr, "Rejected", null, null, null)).Errors[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.TransitionAsync(Alice, pr, "Rejected", "Withdrawn", null, null)).Kind); // 'Withdrawn' is a close code
        Assert.True((await env.Change.TransitionAsync(Alice, pr, "Evaluating", null, null, null)).IsOk);
        // A problem report is resolved only by an implemented request.
        var resolve = await env.Change.TransitionAsync(Alice, pr, "Resolved", null, null, null);
        Assert.Equal(OutcomeKind.Conflict, resolve.Kind);
        Assert.Contains("change request addressed it", resolve.Errors[0], StringComparison.Ordinal);
        var ecr = (await env.Change.CreateFromAsync(Alice, pr, ChangeTypes.Ecr, new CreateFromCommand(), null)).Value!.Number;
        Assert.Contains("still open", (await env.Change.TransitionAsync(Alice, pr, "Closed", "NoActionRequired", null, null)).Errors[0], StringComparison.Ordinal);
        Assert.Contains("still open", (await env.Change.TransitionAsync(Alice, pr, "Resolved", null, null, null)).Errors[0], StringComparison.Ordinal);
        // The request cannot be taken to review states by hand, nor to Implemented.
        var viaWorkflow = await env.Change.TransitionAsync(Alice, ecr, "UnderReview", null, null, null);
        Assert.Equal(OutcomeKind.Conflict, viaWorkflow.Kind);
        Assert.Contains("CCB review", viaWorkflow.Errors[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Conflict, (await env.Change.TransitionAsync(Alice, ecr, "Implemented", null, null, null)).Kind);
        Assert.True((await env.Change.TransitionAsync(Alice, ecr, "Closed", "Withdrawn", "no longer", null)).IsOk);

        // Request closed: the problem report can now be closed, with a reason, and is final.
        var closed = await env.Change.TransitionAsync(Alice, pr, "Closed", "NoActionRequired", "Cosmetic only", "corr-close");
        Assert.True(closed.IsOk, string.Join("; ", closed.Errors));
        Assert.Equal(("Closed", "NoActionRequired", "Cosmetic only", true), (closed.Value!.State, closed.Value.CloseReasonCode, closed.Value.CloseReason, closed.Value.IsFinal));
        Assert.Empty(closed.Value.Transitions);
        Assert.Equal(OutcomeKind.Conflict, (await env.Change.TransitionAsync(Alice, pr, "Open", null, null, null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Change.UpdateAsync(Alice, pr, new UpdateChangeCommand("x", null, "Low", null), null)).Kind);

        // A request with a notice cannot be closed until the notice is cancelled.
        var ecr2 = (await env.NewChangeAsync(Alice, ChangeTypes.Ecr, "Another", cls: ChangeClasses.II)).Number;
        var ecn = (await env.Change.CreateFromAsync(Alice, ecr2, ChangeTypes.Ecn, new CreateFromCommand(), null)).Value!.Number;
        Assert.Contains("Cancel them before closing", (await env.Change.TransitionAsync(Alice, ecr2, "Closed", "Withdrawn", null, null)).Errors[0], StringComparison.Ordinal);
        Assert.True((await env.Change.TransitionAsync(Alice, ecn, "Cancelled", "Superseded", null, null)).IsOk);
        Assert.True((await env.Change.TransitionAsync(Alice, ecr2, "Closed", "Superseded", null, null)).IsOk);

        // Codes are data.
        var codes = await env.Change.ReasonCodesAsync(ChangeTypes.Ecn, "cancel");
        Assert.Contains(codes, c => c.Code == "ScopeChange");
        Assert.DoesNotContain(codes, c => c.Code == "NotAProblem");
        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.Action == "change.transition" && e.ObjectId == pr && e.Payload.Contains("NoActionRequired", StringComparison.Ordinal));
        Assert.Contains(audit, e => e.Action == "change.create" && e.ObjectId == "ECR-000001" && e.Payload.Contains(pr, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validation_and_unsupported_phase_2_operations()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.CreateAsync(Alice, new CreateChangeCommand("XYZ", "t", null, "Low", null, ChangeEnv.Itar()), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.CreateAsync(Alice, new CreateChangeCommand(ChangeTypes.Pr, "", null, "Low", null, ChangeEnv.Itar()), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.CreateAsync(Alice, new CreateChangeCommand(ChangeTypes.Pr, new string('x', 201), null, "Low", null, ChangeEnv.Itar()), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.CreateAsync(Alice, new CreateChangeCommand(ChangeTypes.Pr, "t", null, "Urgent", null, ChangeEnv.Itar()), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.CreateAsync(Alice, new CreateChangeCommand(ChangeTypes.Pr, "t", null, "Low", null, ChangeEnv.Itar(), ChangeClasses.II), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.CreateAsync(Alice, new CreateChangeCommand(ChangeTypes.Ecr, "t", null, "Low", null, ChangeEnv.Itar(), "III"), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.CreateAsync(Alice, new CreateChangeCommand(ChangeTypes.Ecr, "t", null, "Low", null, ChangeEnv.Itar(), null, "PR-999999"), null)).Kind);

        var pr = await env.NewChangeAsync(Alice, ChangeTypes.Pr, "Valid");
        var updated = await env.Change.UpdateAsync(Alice, pr.Number, new UpdateChangeCommand("Renamed", "More text", "High", "Because"), null);
        Assert.Equal(("Renamed", "High", "Because"), (updated.Value!.Title, updated.Value.Priority, updated.Value.Reason));
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.ClassifyAsync(Alice, pr.Number, "I", null)).Kind); // only requests are classified
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.ClassifyAsync(Alice, (await env.NewChangeAsync(Alice, ChangeTypes.Ecr, "R")).Number, "IV", null)).Kind);

        await Assert.ThrowsAsync<NotSupportedException>(() => env.Change.AttachRedlineAsync(Alice, pr.Number, Guid.NewGuid(), null));
        await Assert.ThrowsAsync<NotSupportedException>(() => env.Change.CaptureBaselineAsync(Alice, pr.Number, "BL1", null));
        await Assert.ThrowsAsync<NotSupportedException>(() => env.Change.SetEffectivityAsync(Alice, pr.Number, "SN 100+", null));
        await Assert.ThrowsAsync<NotSupportedException>(() => env.Change.AddMbomChangeAsync(Alice, pr.Number, null));
    }

    [Fact]
    public async Task The_database_keeps_changes_append_only_and_lifecycles_honest()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var (ecn, _) = await env.ReadyEcnAsync();
        var pr = (await env.NewChangeAsync(Alice, ChangeTypes.Pr, "Guarded")).Number;

        // keel_app has no DELETE on any change table, and the triggers refuse deletes and truncation even for a superuser.
        foreach (var table in new[] { "chg.change", "chg.affected_object", "chg.change_task", "chg.history", "chg.change_submission" })
        {
            await using var del = env.Ds.CreateCommand($"DELETE FROM {table}");
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => del.ExecuteNonQueryAsync())).SqlState);
        }
        foreach (var table in new[] { "chg.change", "chg.affected_object", "chg.history" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => env.Db.ExecuteAsSuperuserAsync($"DELETE FROM {table}"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
        var truncate = await Assert.ThrowsAsync<PostgresException>(() => env.Db.ExecuteAsSuperuserAsync("TRUNCATE chg.history"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, truncate.SqlState);

        // History and snapshots are immutable; affected rows only lose their binding or get removed.
        await using var hist = env.Ds.CreateCommand("UPDATE chg.history SET detail = 'rewritten'");
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => hist.ExecuteNonQueryAsync())).SqlState);
        await using var action = env.Ds.CreateCommand("UPDATE chg.affected_object SET change_action = 'obsolete'");
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => action.ExecuteNonQueryAsync())).SqlState);

        // Identity and lifecycle: a notice cannot change number, and cannot jump to a state the lifecycle data does not allow.
        await using var number = env.Ds.CreateCommand("UPDATE chg.change SET number = 'ECN-123456' WHERE number = $1");
        number.Parameters.AddWithValue(ecn);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => number.ExecuteNonQueryAsync())).SqlState);
        await using var jump = env.Ds.CreateCommand("UPDATE chg.change SET lifecycle_state = 'Released' WHERE number = $1");
        jump.Parameters.AddWithValue(ecn);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => jump.ExecuteNonQueryAsync())).SqlState);
        await using var closeEarly = env.Ds.CreateCommand("UPDATE chg.change SET lifecycle_state = 'Resolved' WHERE number = $1");
        closeEarly.Parameters.AddWithValue(pr);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => closeEarly.ExecuteNonQueryAsync())).SqlState); // Open → Resolved is not a transition
        // Numbers have the type's prefix, and each type has its own sequence.
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => env.Db.ExecuteAsSuperuserAsync("UPDATE chg.change SET number = 'ECR-000009' WHERE change_type = 'PR'"))).SqlState);
        Assert.Equal(("PR-000001", "ECN-000001"), (pr, ecn));
        Assert.Equal(1, await env.ScalarAsync("SELECT last_value FROM chg.ecn_seq"));
    }
}
