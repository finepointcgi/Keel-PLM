using Keel.Audit;
using Keel.Core;
using Keel.Workflow;
using Npgsql;
using static Keel.IntegrationTests.WorkflowEnv;

namespace Keel.IntegrationTests;

/// <summary>Review workflow against a real PostgreSQL: progression, release, rejection, cancel, reassign, templates, audit.</summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkflowServiceTests(PostgresFixture pg)
{
    [Fact]
    public async Task Review_to_release_end_to_end()
    {
        var env = await CreateAsync(pg);
        var part = await env.CreatePartAsync();

        var started = await env.StartAsync(part);
        Assert.Equal("Active", started.Status);
        Assert.Equal("UnderReview", await env.PartStateAsync(part));
        Assert.Equal("Under Review", started.Target.StateLabel);
        Assert.Equal("Technical review", Assert.Single(started.Tasks).Name);
        Assert.Equal("rita", started.Tasks[0].AssigneeUsername);
        Assert.Equal(("A.1", "A"), (started.Target.IterationLabel, started.Target.Revision));
        Assert.Equal(env.Clock.GetUtcNow().AddDays(5).Date, started.Tasks[0].DueAt!.Value.Date);

        // The part is locked while the workflow is active: check-out is refused.
        Assert.Contains("locked", (await env.Parts.CheckOutAsync(Alice, part, null)).Errors[0], StringComparison.Ordinal);

        // Step 1: serial technical review; the approvers get nothing yet.
        Assert.Empty((await env.Flow.MyTasksAsync(Bob, null)).Items);
        var afterReview = await env.SignAsync(Rita, started.Tasks[0].Id, "Reviewed");
        Assert.Equal("Active", afterReview.Status);
        Assert.Equal(["bob", "carol"], afterReview.Tasks.Where(t => t.Status == "Open").Select(t => t.AssigneeUsername!).Order());
        Assert.Equal("UnderReview", await env.PartStateAsync(part));

        // Step 2: parallel approval, rule All.
        var afterBob = await env.SignAsync(Bob, TaskOf(afterReview, "bob"), "Approved");
        Assert.Equal("Active", afterBob.Status);
        Assert.Equal("UnderReview", await env.PartStateAsync(part));
        var done = await env.SignAsync(Carol, TaskOf(afterBob, "carol"), "ApprovedWithComments", "Check the finish note on the drawing");
        Assert.Equal("Approved", done.Status);
        Assert.Equal("Released", await env.PartStateAsync(part));
        Assert.Equal("Released", done.Target.StateLabel);
        Assert.DoesNotContain(done.Tasks, t => t.Status == "Open");

        // Three immutable signatures, each with its manifestation.
        Assert.Equal(["Reviewed", "Approved", "Approved with comments"], done.Signatures.Select(s => s.Meaning));
        Assert.All(done.Signatures, s =>
        {
            Assert.Equal(64, s.SignatureHash.Length);
            Assert.StartsWith($"Signed by {s.SignerName} ({s.SignerUsername}) on ", s.Manifestation, StringComparison.Ordinal);
            Assert.EndsWith($" UTC — {s.Meaning}", s.Manifestation, StringComparison.Ordinal);
            Assert.Equal("CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA", s.MarkingBanner);
        });

        // Release locking is enforced by the database too (the app role cannot add content to a released revision).
        await using var insert = env.Ds.CreateCommand("INSERT INTO plm.part_iteration (id, revision_id, iteration, created_at, created_by) SELECT gen_random_uuid(), id, 9, now(), 'x' FROM plm.part_revision");
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync())).SqlState);
        Assert.Contains("locked", (await env.Parts.CheckOutAsync(Alice, part, null)).Errors[0], StringComparison.Ordinal);
        // ... and the manual path cannot release or reopen.
        Assert.Equal(OutcomeKind.Conflict, (await env.Parts.SetStateAsync(Alice, part, "InWork", null, false, null)).Kind);

        // The part page data: release history.
        var status = (await env.Flow.StatusForObjectAsync(Alice, "part", part, null)).Value!;
        Assert.Null(status.Active);
        var release = Assert.Single(status.Releases);
        Assert.Equal(("A", "A.1", 3), (release.Revision, release.IterationLabel, release.Signatures.Count));
    }

    [Fact]
    public async Task Every_step_is_audited_including_the_signature_hash()
    {
        var env = await CreateAsync(pg);
        var part = await env.CreatePartAsync();
        var started = await env.StartAsync(part, "rita", "bob");
        var afterReview = await env.SignAsync(Rita, started.Tasks[0].Id, "Reviewed");
        var done = await env.SignAsync(Bob, TaskOf(afterReview, "bob"), "Approved");

        var events = (await env.AuditAsync()).Where(e => e.Action.StartsWith("workflow.", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            ["workflow.start", "workflow.task_assign", "workflow.sign", "workflow.task_assign", "workflow.sign", "workflow.complete"],
            events.Select(e => e.Action));
        Assert.All(events, e => Assert.Equal(AuditOutcome.Success, e.Outcome));
        Assert.All(events, e => Assert.Equal(("part", part), (e.ObjectType, e.ObjectId)));
        var sign = events.Last(e => e.Action == "workflow.sign");
        Assert.Contains(done.Signatures[1].SignatureHash, sign.Payload, StringComparison.Ordinal);
        Assert.Contains("\"actor\"", sign.Payload, StringComparison.Ordinal);
        Assert.Equal("corr-sign", sign.CorrelationId);
        Assert.Contains("Approved", events[^1].Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejection_returns_the_part_to_work_with_the_reasons_recorded()
    {
        var env = await CreateAsync(pg);
        var part = await env.CreatePartAsync();
        var started = await env.StartAsync(part);
        var afterReview = await env.SignAsync(Rita, started.Tasks[0].Id, "ApprovedWithComments", "Fine from my side");

        // A comment is mandatory for a rejection.
        var noComment = await env.Flow.SignAsync(Fresh(Bob), TaskOf(afterReview, "bob"), "Rejected", "  ", null);
        Assert.Equal(OutcomeKind.Invalid, noComment.Kind);
        Assert.Contains("comment is required", noComment.Errors[0], StringComparison.Ordinal);
        Assert.Equal("UnderReview", await env.PartStateAsync(part));

        var rejected = await env.SignAsync(Bob, TaskOf(afterReview, "bob"), "Rejected", "Wrong material callout");
        Assert.Equal("Rejected", rejected.Status);
        Assert.Contains("Wrong material callout", rejected.CompletionNote, StringComparison.Ordinal);
        var rejection = Assert.Single(rejected.Rejections);
        Assert.Equal(("bob", "Rejected", "Wrong material callout"), (rejection.Username, rejection.Meaning, rejection.Comment));
        // Carol's task is skipped: the All rule is already decided.
        Assert.Equal("Skipped", rejected.Tasks.Single(t => t.AssigneeUsername == "carol").Status);

        // Back in work and unlocked: the author can check it out again; a new review can be started.
        Assert.Equal("InWork", await env.PartStateAsync(part));
        Assert.True((await env.Parts.CheckOutAsync(Alice, part, null)).IsOk);
        Assert.True((await env.Parts.UndoCheckOutAsync(Alice, part, null)).IsOk);
        var status = (await env.Flow.StatusForObjectAsync(Alice, "part", part, null)).Value!;
        Assert.Null(status.Active);
        Assert.Equal("Rejected", status.Last!.Status);
        Assert.Empty(status.Releases);
        Assert.True((await env.Flow.StartAsync(Alice, Start(part, "rita", "bob"), null)).IsOk);
        Assert.Equal("workflow.complete", (await env.AuditAsync()).Last(e => e.Action == "workflow.complete").Action);
    }

    [Fact]
    public async Task The_starter_or_an_admin_can_cancel_with_a_reason()
    {
        var env = await CreateAsync(pg);
        var part = await env.CreatePartAsync();
        var started = await env.StartAsync(part);

        Assert.Equal(OutcomeKind.Forbidden, (await env.Flow.CancelAsync(Mallory, started.Id, "just because", null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Flow.CancelAsync(Bob, started.Id, "not mine", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Flow.CancelAsync(Alice, started.Id, " ", null)).Kind);
        Assert.Equal("UnderReview", await env.PartStateAsync(part));

        var cancelled = (await env.Flow.CancelAsync(Alice, started.Id, "Wrong revision submitted", "corr-cancel")).Value!;
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Contains("Wrong revision submitted", cancelled.CompletionNote, StringComparison.Ordinal);
        Assert.All(cancelled.Tasks, t => Assert.Equal("Cancelled", t.Status));
        Assert.Equal("InWork", await env.PartStateAsync(part));
        Assert.Equal(OutcomeKind.Conflict, (await env.Flow.CancelAsync(Alice, started.Id, "again", null)).Kind);
        // Cancelled tasks cannot be signed.
        var sign = await env.Flow.SignAsync(Fresh(Rita), started.Tasks[0].Id, "Reviewed", null, null);
        Assert.Equal(OutcomeKind.Conflict, sign.Kind);

        // An administrator cancels someone else's review.
        var second = await env.StartAsync(part);
        var byAdmin = (await env.Flow.CancelAsync(Ada, second.Id, "Superseded by ECN", null)).Value!;
        Assert.Equal("Cancelled", byAdmin.Status);
        Assert.Contains("Ada Example", byAdmin.CompletionNote, StringComparison.Ordinal);

        var audit = (await env.AuditAsync()).Where(e => e.Action == "workflow.cancel").ToList();
        Assert.Equal([AuditOutcome.Denied, AuditOutcome.Denied, AuditOutcome.Success, AuditOutcome.Success], audit.Select(a => a.Outcome));
        Assert.Contains("workflow.cancel_not_starter_or_admin", audit[1].Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_clears_a_workflow_whose_part_an_admin_moved_out_of_review()
    {
        var env = await CreateAsync(pg);
        var part = await env.CreatePartAsync();
        var started = await env.StartAsync(part);
        // Break-glass: the audited "set state" override forces the part back.
        Assert.True((await env.Parts.SetStateAsync(Ada, part, "InWork", "Correction", true, null)).IsOk);

        var sign = await env.Flow.SignAsync(Fresh(Rita), started.Tasks[0].Id, "Reviewed", null, null);
        Assert.Equal(OutcomeKind.Conflict, sign.Kind);
        Assert.Contains("no longer under review", sign.Errors[0], StringComparison.Ordinal);
        Assert.Contains("object.state_changed", (await env.AuditAsync()).Last(e => e.Action == "workflow.sign").Payload, StringComparison.Ordinal);

        var cancelled = await env.Flow.CancelAsync(Alice, started.Id, "Part was reset by an administrator", null);
        Assert.Equal("Cancelled", cancelled.Value!.Status);
        Assert.Equal("InWork", await env.PartStateAsync(part));
        Assert.True((await env.Flow.StartAsync(Alice, Start(part, "rita", "bob"), null)).IsOk);
    }

    [Fact]
    public async Task Start_requires_an_unlocked_in_work_part_without_a_check_out()
    {
        var env = await CreateAsync(pg);
        var part = await env.CreatePartAsync();
        Assert.True((await env.Parts.CheckOutAsync(Alice, part, null)).IsOk);
        var checkedOut = await env.Flow.StartAsync(Alice, Start(part, "rita", "bob"), null);
        Assert.Equal(OutcomeKind.Conflict, checkedOut.Kind);
        Assert.Contains("Checked out by alice", checkedOut.Errors[0], StringComparison.Ordinal);
        Assert.Equal("InWork", await env.PartStateAsync(part));
        await env.Parts.UndoCheckOutAsync(Alice, part, null);

        var first = await env.StartAsync(part);
        var again = await env.Flow.StartAsync(Alice, Start(part, "rita", "bob"), null);
        Assert.Equal(OutcomeKind.Conflict, again.Kind);
        Assert.Contains("only parts In Work", again.Errors[0], StringComparison.Ordinal);

        // The database also refuses a second active instance for one object.
        await using var dup = env.Ds.CreateCommand(
            "INSERT INTO wf.instance SELECT (jsonb_populate_record(NULL::wf.instance, to_jsonb(i) || jsonb_build_object('id', gen_random_uuid()))).* FROM wf.instance i WHERE i.id = '" + first.Id + "'");
        var violation = await Assert.ThrowsAsync<PostgresException>(() => dup.ExecuteNonQueryAsync());
        Assert.Equal(("23505", "instance_one_active"), (violation.SqlState, violation.ConstraintName));
    }

    [Fact]
    public async Task Only_authors_who_may_modify_can_submit_and_unreadable_parts_are_not_found()
    {
        var env = await CreateAsync(pg);
        var part = await env.CreatePartAsync();
        var viewer = await env.Flow.StartAsync(Mallory, Start(part, "rita", "bob"), null);
        Assert.Equal(OutcomeKind.Forbidden, viewer.Kind);
        Assert.Contains("role.missing", viewer.Errors);

        var foreign = await env.Flow.StartAsync(Lena, Start(part, "rita", "bob"), null);
        Assert.Equal(OutcomeKind.NotFound, foreign.Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Flow.StartAsync(Alice, Start("P-999999", "rita", "bob"), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Flow.StartAsync(Alice, new StartReviewCommand("drawing", part, new Dictionary<string, string[]>()), null)).Kind);

        var denials = (await env.AuditAsync()).Where(e => e.Action == "workflow.start").ToList();
        Assert.Equal([AuditOutcome.Denied, AuditOutcome.Denied], denials.Select(d => d.Outcome));
        Assert.Equal("InWork", await env.PartStateAsync(part));
    }

    // ---------------------------------------------------------------- participants

    [Fact]
    public async Task Participants_must_be_in_the_directory_hold_the_role_and_be_eligible_for_the_markings()
    {
        var env = await CreateAsync(pg);
        var part = await env.CreatePartAsync();

        async Task<Outcome<InstanceView>> TryStart(string reviewer, params string[] approvers) => await env.Flow.StartAsync(Alice, Start(part, reviewer, approvers), "corr-x");

        var lena = await TryStart("rita", "lena");                     // not a U.S. person, no authorization
        Assert.Equal(OutcomeKind.Invalid, lena.Kind);
        Assert.Contains(lena.Errors, e => e.Contains("lena is not eligible", StringComparison.Ordinal) && e.Contains("export.us_person_or_authorization_required", StringComparison.Ordinal));

        var stranger = await TryStart("rita", "nobody");
        Assert.Contains(stranger.Errors, e => e.Contains("'nobody' is not in the user directory", StringComparison.Ordinal));
        var notApprover = await TryStart("rita", "mallory");
        Assert.Contains(notApprover.Errors, e => e.Contains("mallory does not hold the plm-approver role", StringComparison.Ordinal));
        var auditor = await TryStart("audrey", "bob");                  // auditors never review
        Assert.Contains(auditor.Errors, e => e.Contains("audrey does not hold the plm-viewer role", StringComparison.Ordinal));
        var self = await TryStart("alice", "bob");                      // separation of duties
        Assert.Contains(self.Errors, e => e.Contains("cannot review or approve your own submission", StringComparison.Ordinal));
        var twice = await TryStart("rita", "bob", "bob");
        Assert.Contains(twice.Errors, e => e.Contains("chosen twice", StringComparison.Ordinal));
        var tooFew = await env.Flow.StartAsync(Alice, new StartReviewCommand("part", part, new Dictionary<string, string[]> { ["reviewer"] = ["rita"] }), null);
        Assert.Contains(tooFew.Errors, e => e.Contains("Approvers: choose between 1 and 10", StringComparison.Ordinal));
        var unknownSlot = await env.Flow.StartAsync(Alice, new StartReviewCommand("part", part, new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob"], ["extra"] = ["x"] }), null);
        Assert.Contains(unknownSlot.Errors, e => e.Contains("no participant group 'extra'", StringComparison.Ordinal));

        // Nothing was started and the part was not touched; refusals on eligibility are audited as denials.
        Assert.Equal("InWork", await env.PartStateAsync(part));
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM wf.instance"));
        var denied = (await env.AuditAsync()).Where(e => e.Action == "workflow.start" && e.Outcome == AuditOutcome.Denied).ToList();
        Assert.Contains(denied, e => e.Payload.Contains("lena:export.us_person_or_authorization_required", StringComparison.Ordinal));
        Assert.Contains(denied, e => e.Payload.Contains("alice:sod.submitter", StringComparison.Ordinal));

        // A foreign person holding a listed export authorization IS eligible for a part that lists it.
        var licensed = await env.CreatePartAsync("Licensed bracket", Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)",
            cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "TAA", program: "ORCA", exportAuthorizations: ["TAA-0042"]).Value);
        var ok = await env.Flow.StartAsync(Alice, Start(licensed, "rita", "farid"), null);
        Assert.True(ok.IsOk, string.Join("; ", ok.Errors));
        // ... and a participant outside the program is not.
        await env.Directory.UpsertAsync(User("pat", [Roles.Viewer, Roles.Approver], programs: []));
        var noProgram = await TryStart("rita", "pat");
        Assert.Contains(noProgram.Errors, e => e.Contains("program.not_member", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Directory_search_returns_only_eligible_users_with_limited_fields()
    {
        var env = await CreateAsync(pg);
        var part = await env.CreatePartAsync();

        var all = (await env.Flow.SearchUsersAsync(Alice, "e", null, null, null, 50, null)).Value!;
        Assert.Contains(all, u => u.Username == "lena");
        var users = (await env.Flow.SearchUsersAsync(Alice, "", Roles.Approver, "part", part, 50, "corr-search")).Value!;
        Assert.Equal(["bob", "carol", "dave"], users.Select(u => u.Username).Order());
        Assert.DoesNotContain(users, u => u.Username is "lena" or "farid" or "alice" or "mallory" or "ada");   // ineligible, caller, wrong role
        Assert.All(users, u => Assert.Contains(Roles.Approver, u.Roles));

        var reviewers = (await env.Flow.SearchUsersAsync(Alice, "", Roles.Viewer, "part", part, 50, null)).Value!;
        Assert.DoesNotContain(reviewers, u => u.Username == "audrey");           // auditors never review
        Assert.Contains(reviewers, u => u.Username == "rita");

        // A caller who cannot read the object learns nothing about it.
        Assert.Equal(OutcomeKind.NotFound, (await env.Flow.SearchUsersAsync(Lena, "", null, "part", part, 50, null)).Kind);
        var limited = (await env.Flow.SearchUsersAsync(Alice, "bob", null, null, null, 5, null)).Value!;
        Assert.Equal("Bob Example", Assert.Single(limited).DisplayName);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Flow.SearchUsersAsync(User("nobody", []), "", null, null, null, 5, null)).Kind);
        Assert.Contains((await env.AuditAsync()), e => e.Action == "workflow.users_search" && e.Outcome == AuditOutcome.Denied);
    }

    // ---------------------------------------------------------------- reassign

    [Fact]
    public async Task An_admin_can_reassign_an_open_task_to_another_eligible_user()
    {
        var env = await CreateAsync(pg);
        var part = await env.CreatePartAsync();
        var started = await env.StartAsync(part);
        var afterReview = await env.SignAsync(Rita, started.Tasks[0].Id, "Reviewed");
        var bobTask = TaskOf(afterReview, "bob");

        Assert.Equal(OutcomeKind.Forbidden, (await env.Flow.ReassignTaskAsync(Alice, bobTask, "dave", "x", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Flow.ReassignTaskAsync(Ada, bobTask, "dave", " ", null)).Kind);

        var bad = await env.Flow.ReassignTaskAsync(Ada, bobTask, "lena", "Bob is away", null);
        Assert.Equal(OutcomeKind.Invalid, bad.Kind);
        Assert.Contains("lena is not eligible", bad.Errors[0], StringComparison.Ordinal);
        Assert.Contains("carol already has a task", (await env.Flow.ReassignTaskAsync(Ada, bobTask, "carol", "Bob is away", null)).Errors[0], StringComparison.Ordinal);
        Assert.Contains("alice submitted this review", (await env.Flow.ReassignTaskAsync(Ada, bobTask, "alice", "Bob is away", null)).Errors[0], StringComparison.Ordinal);
        Assert.Contains("mallory does not hold", (await env.Flow.ReassignTaskAsync(Ada, bobTask, "mallory", "Bob is away", null)).Errors[0], StringComparison.Ordinal);
        Assert.Contains("not in the user directory", (await env.Flow.ReassignTaskAsync(Ada, bobTask, "ghost", "Bob is away", null)).Errors[0], StringComparison.Ordinal);

        var moved = (await env.Flow.ReassignTaskAsync(Ada, bobTask, "dave", "Bob is on leave", "corr-re")).Value!;
        Assert.Equal("dave", moved.Tasks.Single(t => t.Id == bobTask).AssigneeUsername);
        Assert.Contains(moved.Events, e => e.Action == "Task reassigned" && e.Detail!.Contains("Bob is on leave", StringComparison.Ordinal));

        // The previous assignee can no longer sign; the new one can.
        Assert.Equal(OutcomeKind.Forbidden, (await env.Flow.SignAsync(Fresh(Bob), bobTask, "Approved", null, null)).Kind);
        Assert.Empty((await env.Flow.MyTasksAsync(Bob, null)).Items);
        Assert.Single((await env.Flow.MyTasksAsync(Dave, null)).Items);
        await env.SignAsync(Dave, bobTask, "Approved");

        var audit = (await env.AuditAsync()).Where(e => e.Action == "workflow.reassign").ToList();
        Assert.Equal(AuditOutcome.Success, audit[^1].Outcome);
        Assert.Contains("Bob is on leave", audit[^1].Payload, StringComparison.Ordinal);
        Assert.Contains(audit, a => a.Outcome == AuditOutcome.Denied);
    }

    // ---------------------------------------------------------------- templates

    private const string PoolTemplate = """
        { "id": "pool-approval", "name": "Any approver releases", "objectType": "part", "start": "flow",
          "nodes": [ { "id": "flow", "type": "Serial", "steps": ["sign", "done"] },
                     { "id": "sign", "type": "Task", "name": "Approval", "kind": "approval", "assignment": { "mode": "Role", "role": "plm-approver" } },
                     { "id": "done", "type": "End", "outcome": "Release" } ] }
        """;

    [Fact]
    public async Task Templates_list_and_admins_upload_validated_new_versions()
    {
        var env = await CreateAsync(pg);
        // 1.6 seeds the ECR CCB and ECN release templates next to the part template.
        var seededTemplates = await env.Flow.ListTemplatesAsync(Alice, true, null);
        Assert.Equal(["baseline", "capa", "deviation", "disposition", "document", "ecn", "ecr", "legal_hold", "mbom", "ncr", "part", "reg_list_version", "reqspec", "retention_schedule", "source_entry", "supplier"], seededTemplates.Select(t => t.ObjectType).Order());
        var seeded = Assert.Single(seededTemplates, t => t.ObjectType == "part");
        Assert.Equal(("part-release-review", 1, true), (seeded.Id, seeded.Version, seeded.IsActive));
        Assert.Equal(["reviewer", "approvers"], seeded.Slots.Select(s => s.Name));

        var denied = await env.Flow.UploadTemplateAsync(Alice, PoolTemplate, null);
        Assert.Equal(OutcomeKind.Forbidden, denied.Kind);

        var invalid = await env.Flow.UploadTemplateAsync(Ada, """{ "id": "x", "name": "X", "objectType": "part", "start": "a", "nodes": [ { "id": "a", "type": "Condition" } ] }""", null);
        Assert.Equal(OutcomeKind.Invalid, invalid.Kind);
        Assert.Contains(invalid.Errors, e => e.Contains("not supported yet", StringComparison.Ordinal));
        var unknownType = await env.Flow.UploadTemplateAsync(Ada, PoolTemplate.Replace("\"part\"", "\"drawing\"", StringComparison.Ordinal), null);
        Assert.Contains("No workflow can run on objects of type 'drawing'", unknownType.Errors[0], StringComparison.Ordinal);

        var v1 = (await env.Flow.UploadTemplateAsync(Ada, PoolTemplate, "corr-up")).Value!;
        Assert.Equal(("pool-approval", 1, true), (v1.Id, v1.Version, v1.IsActive));
        // Replacing the seeded template creates version 2 and retires version 1.
        var replacement = (await env.Flow.UploadTemplateAsync(Ada, SeededWithName("Part release review (v2)"), null)).Value!;
        Assert.Equal(2, replacement.Version);
        var active = (await env.Flow.ListTemplatesAsync(Alice, true, null)).Where(t => t.ObjectType == "part").ToList();
        Assert.Equal(["Any approver releases", "Part release review (v2)"], active.Select(t => t.Name).Order());
        var all = (await env.Flow.ListTemplatesAsync(Alice, false, null)).Where(t => t.ObjectType == "part").ToList();
        Assert.Equal(3, all.Count);
        Assert.False(all.Single(t => t.Id == "part-release-review" && t.Version == 1).IsActive);

        // The database keeps versions immutable.
        await using var edit = env.Ds.CreateCommand("UPDATE wf.template SET definition = '{}'::jsonb");
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => edit.ExecuteNonQueryAsync())).SqlState);
        await using var delete = env.Ds.CreateCommand("DELETE FROM wf.template");
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState);

        var audit = (await env.AuditAsync()).Where(e => e.Action == "workflow.template_upload").Select(a => a.Outcome).ToList();
        Assert.Equal([AuditOutcome.Denied, AuditOutcome.Success, AuditOutcome.Success], audit);
    }

    private static string SeededWithName(string name)
    {
        var json = WorkflowEngineTestsAccess.Seeded();
        return json.Replace("\"name\": \"Part release review\"", $"\"name\": \"{name}\"", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_running_review_keeps_its_template_version_and_pool_tasks_work_with_separation_of_duties()
    {
        var env = await CreateAsync(pg);
        var part = await env.CreatePartAsync();
        var started = await env.StartAsync(part);                         // seeded v1
        Assert.True((await env.Flow.UploadTemplateAsync(Ada, SeededWithName("Replaced"), null)).IsOk);
        Assert.Equal(("part-release-review", 1, "Part release review"), (started.TemplateId, started.TemplateVersion, (await env.ViewAsync(Alice, started.Id)).TemplateName));
        await env.Flow.CancelAsync(Alice, started.Id, "Use the pool template", null);

        // With several templates the submitter must choose one.
        Assert.True((await env.Flow.UploadTemplateAsync(Ada, PoolTemplate, null)).IsOk);
        var ambiguous = await env.Flow.StartAsync(Alice, new StartReviewCommand("part", part, new Dictionary<string, string[]>()), null);
        Assert.Contains("Choose a workflow template", ambiguous.Errors[0], StringComparison.Ordinal);
        var missing = await env.Flow.StartAsync(Alice, new StartReviewCommand("part", part, new Dictionary<string, string[]>(), "nope"), null);
        Assert.Contains("no active workflow template 'nope'", missing.Errors[0], StringComparison.Ordinal);

        var pool = await env.Flow.StartAsync(Alice, new StartReviewCommand("part", part, new Dictionary<string, string[]>(), "pool-approval"), null);
        var task = Assert.Single(pool.Value!.Tasks);
        Assert.Null(task.AssigneeSub);

        // Pool task: shown to approvers who can read the part, hidden from those who cannot or lack the role, never to the submitter.
        Assert.Single((await env.Flow.MyTasksAsync(Bob, null)).Items);
        Assert.Empty((await env.Flow.MyTasksAsync(Lena, null)).Items);         // cannot read an ITAR part: not even listed
        Assert.Empty((await env.Flow.MyTasksAsync(Mallory, null)).Items);      // no approver role
        var aliceApprover = User("alice", [Roles.Viewer, Roles.Author, Roles.Approver]);
        Assert.Empty((await env.Flow.MyTasksAsync(aliceApprover, null)).Items);
        var sod = await env.Flow.SignAsync(Fresh(aliceApprover), task.Id, "Approved", null, "corr-sod");
        Assert.Equal(OutcomeKind.Forbidden, sod.Kind);
        Assert.Equal(["sod.submitter"], sod.Errors);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Flow.SignAsync(Fresh(Mallory), task.Id, "Approved", null, null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Flow.SignAsync(Fresh(Lena), task.Id, "Approved", null, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Flow.SignAsync(Fresh(Bob), task.Id, "Reviewed", null, null)).Kind);   // "Reviewed" is a review meaning, not an approval one

        // Whoever signs a pool task claims it; a single signature releases with this template.
        var done = await env.SignAsync(Bob, task.Id, "Approved");
        Assert.Equal("Approved", done.Status);
        Assert.Equal("bob", done.Tasks[0].AssigneeUsername);
        Assert.Equal("Released", await env.PartStateAsync(part));
        Assert.Contains((await env.AuditAsync()), e => e.Action == "workflow.sign" && e.Outcome == AuditOutcome.Denied && e.Payload.Contains("sod.submitter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Phase_two_features_are_explicitly_not_supported()
    {
        var env = await CreateAsync(pg);
        var delegation = await Assert.ThrowsAsync<NotSupportedException>(() => env.Flow.DelegateTaskAsync(Alice, Guid.NewGuid(), "bob", null));
        Assert.Contains("Phase 2", delegation.Message, StringComparison.Ordinal);
        var escalation = await Assert.ThrowsAsync<NotSupportedException>(() => env.Flow.ScheduleEscalationAsync(Guid.NewGuid(), TimeSpan.FromDays(1)));
        Assert.Contains("Phase 2", escalation.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- lists and visibility

    [Fact]
    public async Task My_tasks_and_task_pages_show_markings_and_redact_for_users_who_lost_eligibility()
    {
        var env = await CreateAsync(pg);
        var part = await env.CreatePartAsync("Secret bracket");
        var started = await env.StartAsync(part);
        var taskId = started.Tasks[0].Id;

        var mine = await env.Flow.MyTasksAsync(Rita, "corr-my");
        var item = Assert.Single(mine.Items);
        Assert.False(item.Redacted);
        Assert.Equal(("Technical review", part, "Secret bracket"), (item.Name, item.Target!.Key, item.Target.Name));
        Assert.Equal("CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA", item.Target.Banner);
        Assert.Equal("CUI//EXPT · ITAR · DISTRIBUTION D", mine.PageBanner);   // page banner is the aggregate of what is shown
        Assert.Equal("Alice Example", item.StartedByName);

        var detail = (await env.Flow.GetTaskAsync(Rita, taskId, null)).Value!;
        Assert.True(detail.CanSign);
        Assert.Equal("Synthetic bracket", detail.Description);
        Assert.Equal(0.42m, detail.Attributes!["mass"]!.GetValue<decimal>());
        Assert.Equal(["Reviewed", "Approved with comments", "Rejected"], detail.ReasonCodes.Select(c => c.Label));   // review task: no plain "Approved"
        Assert.Equal(detail.Summary.Target!.Banner, detail.Banner);

        // Rita is later found to be a foreign person (live token says so): the task stays but is redacted and cannot be signed.
        var ritaNow = Rita with { UsPerson = false };
        var redacted = Assert.Single((await env.Flow.MyTasksAsync(ritaNow, null)).Items);
        Assert.True(redacted.Redacted);
        Assert.Null(redacted.Target);
        Assert.Equal(taskId, redacted.Id);
        Assert.NotEqual("UNCLASSIFIED", mine.PageBanner);
        Assert.Equal("UNCLASSIFIED", (await env.Flow.MyTasksAsync(ritaNow, null)).PageBanner);

        var redactedDetail = (await env.Flow.GetTaskAsync(ritaNow, taskId, null)).Value!;
        Assert.True(redactedDetail.Summary.Redacted);
        Assert.False(redactedDetail.CanSign);
        Assert.Null(redactedDetail.Description);
        Assert.Null(redactedDetail.Instance);
        Assert.Equal("UNCLASSIFIED", redactedDetail.Banner);
        Assert.DoesNotContain(part, System.Text.Json.JsonSerializer.Serialize(redactedDetail), StringComparison.Ordinal);
        Assert.DoesNotContain("Secret bracket", System.Text.Json.JsonSerializer.Serialize(redactedDetail), StringComparison.Ordinal);

        var sign = await env.Flow.SignAsync(Fresh(ritaNow), taskId, "Reviewed", null, "corr-lost");
        Assert.Equal(OutcomeKind.Forbidden, sign.Kind);
        Assert.Equal(["task.not_eligible"], sign.Errors);
        var denial = (await env.AuditAsync()).Last(e => e.Action == "workflow.sign");
        Assert.Equal(AuditOutcome.Denied, denial.Outcome);
        Assert.Contains("export.us_person_or_authorization_required", denial.Payload, StringComparison.Ordinal);   // the audit keeps the real reason
        Assert.Equal("Active", (await env.ViewAsync(Alice, started.Id)).Status);

        // Everyone else gets 404 on the task and instance pages.
        Assert.Equal(OutcomeKind.NotFound, (await env.Flow.GetTaskAsync(Mallory, taskId, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Flow.GetTaskAsync(Bob, taskId, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Flow.GetInstanceAsync(Lena, started.Id, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Flow.GetTaskAsync(Ada, Guid.NewGuid(), null)).Kind);
        // The starter and an administrator who may read the part can open it; a non-US admin cannot.
        Assert.True((await env.Flow.GetTaskAsync(Alice, taskId, null)).IsOk);
        Assert.True((await env.Flow.GetTaskAsync(Ada, taskId, null)).IsOk);
        Assert.Equal(OutcomeKind.NotFound, (await env.Flow.GetTaskAsync(Ada with { UsPerson = false }, taskId, null)).Kind);
        Assert.Contains((await env.AuditAsync()), e => e.Action == "workflow.read" && e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task The_admin_list_shows_only_what_the_admin_may_read_and_is_admin_only()
    {
        var env = await CreateAsync(pg);
        var itar = await env.CreatePartAsync("ITAR bracket");
        var open = await env.CreatePartAsync("Open bracket", Open);
        await env.StartAsync(itar);
        await env.StartAsync(open, "rita", "bob");

        Assert.Equal(OutcomeKind.Forbidden, (await env.Flow.ListInstancesAsync(Alice, null, 50, 0, null)).Kind);
        var page = (await env.Flow.ListInstancesAsync(Ada, null, 50, 0, "corr-list")).Value!;
        Assert.Equal(2, page.Total);
        Assert.Equal([open, itar], page.Items.Select(i => i.Target.Key));
        Assert.Equal("CUI//EXPT · ITAR · DISTRIBUTION D", page.PageBanner);

        // An administrator who is not a U.S. person does not see the ITAR review (admins get no marking bypass).
        var foreignAdmin = Ada with { UsPerson = false, Id = "sub-ada2" };
        var restricted = (await env.Flow.ListInstancesAsync(foreignAdmin, null, 50, 0, null)).Value!;
        Assert.Equal(open, Assert.Single(restricted.Items).Target.Key);
        Assert.Equal("UNCLASSIFIED · DISTRIBUTION A", restricted.PageBanner);
        Assert.Equal(1, restricted.Total);

        Assert.Single((await env.Flow.ListInstancesAsync(Ada, "Active", 1, 0, null)).Value!.Items);
        Assert.Empty((await env.Flow.ListInstancesAsync(Ada, "Cancelled", 50, 0, null)).Value!.Items);
        Assert.Contains((await env.AuditAsync()), e => e.Action == "workflow.list" && e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task Completed_instances_and_tasks_are_immutable_in_the_database()
    {
        var env = await CreateAsync(pg);
        var part = await env.CreatePartAsync();
        var started = await env.StartAsync(part);
        var rejected = await env.Flow.SignAsync(Fresh(Rita), started.Tasks[0].Id, "Rejected", "No", null);
        Assert.Equal("Rejected", rejected.Value!.Status);

        foreach (var sql in new[]
        {
            "UPDATE wf.instance SET comment = 'edited'",
            "UPDATE wf.task SET name = 'edited'",
            "DELETE FROM wf.task",
            "DELETE FROM wf.instance",
            "DELETE FROM wf.token",
        })
        {
            await using var cmd = env.Ds.CreateCommand(sql);
            var e = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.True(e.SqlState is PostgresErrorCodes.CheckViolation or PostgresErrorCodes.InsufficientPrivilege, $"{sql}: {e.SqlState}");
        }
    }
}

internal static class WorkflowEngineTestsAccess
{
    /// <summary>The seeded template JSON, read from the migration so tests cannot drift from it.</summary>
    public static string Seeded()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        var sql = File.ReadAllText(Path.Combine(dir!.FullName, "db", "migrations", "0006_workflow.sql"));
        var start = sql.IndexOf("$tpl$", StringComparison.Ordinal) + "$tpl$".Length;
        return sql[start..sql.IndexOf("$tpl$", start, StringComparison.Ordinal)];
    }
}
