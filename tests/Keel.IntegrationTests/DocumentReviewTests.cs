using System.Data.Common;
using System.Text;
using Keel.Audit;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Vault;
using Keel.Security;
using Keel.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using static Keel.IntegrationTests.WorkflowEnv;

namespace Keel.IntegrationTests;

/// <summary>Records what the document gateway and the workflow engine hand to the integration outbox and to the notification sink.</summary>
internal sealed class RecordingSinks : IDomainEventSink, INotificationSink
{
    public List<DomainEvent> Events { get; } = [];
    public List<NotificationRequest> Notifications { get; } = [];
    public List<(string User, string Type, string Number)> Watches { get; } = [];

    public Task AppendAsync(DbConnection conn, DbTransaction tx, DomainEvent domainEvent, CancellationToken ct)
    {
        Events.Add(domainEvent);
        return Task.CompletedTask;
    }

    public Task AppendAsync(DbConnection conn, DbTransaction tx, NotificationRequest request, CancellationToken ct)
    {
        Notifications.Add(request);
        return Task.CompletedTask;
    }

    public Task AutoWatchAsync(DbConnection conn, DbTransaction tx, string userSub, string objectType, string objectNumber, CancellationToken ct)
    {
        Watches.Add((userSub, objectType, objectNumber));
        return Task.CompletedTask;
    }
}

/// <summary>Real document and workflow services (gateway included) over a fresh Postgres database and a real vault, for the document review tests (7.6, ADR-0033).</summary>
internal sealed class DocReviewEnv
{
    public required TestDatabase Db { get; init; }
    public required NpgsqlDataSource Ds { get; init; }
    public required DocumentService Docs { get; init; }
    public required WorkflowService Flow { get; init; }
    public required RecordingSinks Sinks { get; init; }
    public required TestClock Clock { get; init; }

    public static async Task<DocReviewEnv> CreateAsync(PostgresFixture pg, params Subject[] extraUsers)
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var clock = new TestClock();
        var audit = new PostgresAuditLog(ds, clock);
        var sinks = new RecordingSinks();
        var options = new WorkflowOptions();
        var env = new DocReviewEnv
        {
            Db = db, Ds = ds, Clock = clock, Sinks = sinks,
            Docs = new DocumentService(ds, audit, clock, new ObjectVault(await VaultContainer.OptionsAsync()), sinks, sinks),
            Flow = new WorkflowService(ds, audit, [new DocumentWorkflowGateway(sinks, sinks)], options, clock, sinks),
        };
        var directory = new UserDirectory(ds, clock, options, NullLogger<UserDirectory>.Instance);
        foreach (var u in new[] { Alice, Rita, Bob, Carol, Dave, Lena, Ada, Farid, Mallory, Audrey }.Concat(extraUsers)) await directory.UpsertAsync(u);
        return env;
    }

    private static byte[] Unique(string text) => Encoding.UTF8.GetBytes($"{text}\n{Guid.NewGuid()}\n");

    /// <summary>A new document with a primary file (and an attachment when asked), created by alice unless told otherwise.</summary>
    public async Task<string> NewDocumentAsync(string name = "Bracket specification", Markings? markings = null, Subject? by = null, bool primary = true, bool attachment = false)
    {
        var created = await Docs.CreateAsync(by ?? Alice, new CreateDocumentCommand(name, "Specification", markings ?? Itar(), "Synthetic specification"),
            primary ? new FileUpload("spec.txt", "text/plain", new MemoryStream(Unique("specification body"))) : null, null);
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        var number = created.Value!.Number;
        if (attachment)
        {
            var who = by ?? Alice;
            Assert.True((await Docs.CheckOutAsync(who, number, null)).IsOk);
            var up = await Docs.UploadFileAsync(who, number, FileRoles.Attachment, new FileUpload("loads.csv", "text/csv", new MemoryStream(Unique("loads"))), null);
            Assert.True(up.IsOk, string.Join("; ", up.Errors));
            Assert.True((await Docs.CheckInAsync(who, number, "add attachment", null)).IsOk);
        }
        return number;
    }

    public static StartReviewCommand Start(string number, string reviewer = "rita", params string[] approvers) => new("document", number,
        new Dictionary<string, string[]> { ["reviewer"] = [reviewer], ["approvers"] = approvers.Length == 0 ? ["bob", "carol"] : approvers });

    public async Task<InstanceView> StartAsync(string number, Subject? by = null, string reviewer = "rita", params string[] approvers)
    {
        var started = await Flow.StartAsync(by ?? Alice, Start(number, reviewer, approvers), "corr-start");
        Assert.True(started.IsOk, $"{started.Kind}: {string.Join("; ", started.Errors)}");
        return started.Value!;
    }

    public async Task<InstanceView> SignAsync(Subject who, Guid taskId, string code, string? comment = null)
    {
        var r = await Flow.SignAsync(Fresh(who), taskId, code, comment, "corr-sign");
        Assert.True(r.IsOk, $"{who.Username} signing as {code}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    /// <summary>Signs the review through: rita reviews, bob and carol approve.</summary>
    public async Task<InstanceView> ReleaseAsync(string number, Subject? by = null)
    {
        var started = await StartAsync(number, by);
        var afterReview = await SignAsync(Rita, started.Tasks[0].Id, "Reviewed");
        var afterBob = await SignAsync(Bob, TaskOf(afterReview, "bob"), "Approved");
        return await SignAsync(Carol, TaskOf(afterBob, "carol"), "Approved");
    }

    public async Task<DocumentDetail> GetAsync(string number, Subject? who = null) => (await Docs.GetAsync(who ?? Alice, number, null, null)).Value!;

    public async Task<string> StateAsync(string number) => (await GetAsync(number)).Revision.State;

    public async Task<List<AuditRecord>> AuditAsync() => [.. await new AuditQueries(Ds).ListAsync(new AuditQuery(Limit: 5000))];
}

/// <summary>
/// Documents go through the same review, e-signature and release engine as parts (ADR-0033): submit, tasks, signatures, release locks the revision, cancel, reassign,
/// markings enforcement and the integration points. Real PostgreSQL and vault, no mocks.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DocumentReviewTests(PostgresFixture pg)
{
    [Fact]
    public async Task Review_to_release_end_to_end_locks_the_revision_and_reports_the_release()
    {
        var env = await DocReviewEnv.CreateAsync(pg);
        var doc = await env.NewDocumentAsync();

        var started = await env.StartAsync(doc);
        Assert.Equal("Active", started.Status);
        Assert.Equal(("document", doc, "Under Review", "A.1"), (started.Target.ObjectType, started.Target.Key, started.Target.StateLabel, started.Target.IterationLabel));
        Assert.Equal("UnderReview", await env.StateAsync(doc));
        var detail = await env.GetAsync(doc);
        Assert.True(detail.Revision.IsLocked);

        // Locked while the review runs: no check-out, no manual way out, no markings change.
        Assert.Contains("locked", (await env.Docs.CheckOutAsync(Alice, doc, null)).Errors[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Conflict, (await env.Docs.SetStateAsync(Alice, doc, "InWork", null, false, null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Docs.ChangeMarkingsAsync(Alice, doc, Open, "x", null)).Kind);

        // Serial technical review first; the approvers get nothing yet.
        Assert.Empty((await env.Flow.MyTasksAsync(Bob, null)).Items);
        var afterReview = await env.SignAsync(Rita, started.Tasks[0].Id, "Reviewed");
        Assert.Equal(["bob", "carol"], afterReview.Tasks.Where(t => t.Status == "Open").Select(t => t.AssigneeUsername!).Order());
        var afterBob = await env.SignAsync(Bob, TaskOf(afterReview, "bob"), "Approved");
        Assert.Equal("UnderReview", await env.StateAsync(doc));
        var done = await env.SignAsync(Carol, TaskOf(afterBob, "carol"), "ApprovedWithComments", "Check the load table");

        Assert.Equal("Approved", done.Status);
        Assert.Equal("Released", await env.StateAsync(doc));
        Assert.Equal(["Reviewed", "Approved", "Approved with comments"], done.Signatures.Select(s => s.Meaning));
        Assert.All(done.Signatures, s =>
        {
            Assert.Equal(64, s.SignatureHash.Length);
            Assert.Equal("CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA", s.MarkingBanner);
            Assert.EndsWith($" UTC — {s.Meaning}", s.Manifestation, StringComparison.Ordinal);
        });

        // The released revision stays locked: the database refuses new content, and the application refuses check-out.
        await using var insert = env.Ds.CreateCommand("INSERT INTO plm.document_iteration (id, revision_id, iteration, created_at, created_by) SELECT gen_random_uuid(), id, 9, now(), 'x' FROM plm.document_revision");
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync())).SqlState);
        Assert.Contains("locked", (await env.Docs.CheckOutAsync(Alice, doc, null)).Errors[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Conflict, (await env.Docs.SetStateAsync(Alice, doc, "InWork", null, false, null)).Kind);

        // The release history for the document page.
        var status = (await env.Flow.StatusForObjectAsync(Alice, "document", doc, null)).Value!;
        Assert.Null(status.Active);
        var release = Assert.Single(status.Releases);
        Assert.Equal(("A", "A.1", 3), (release.Revision, release.IterationLabel, release.Signatures.Count));

        // The ERP event commits with the release and says it came through the workflow.
        var evt = Assert.Single(env.Sinks.Events);
        Assert.Equal((EventTypes.DocumentReleased, "document", doc), (evt.EventType, evt.AggregateType, evt.AggregateNumber));
        Assert.Equal("workflow", evt.Payload["via"]!.GetValue<string>());
        Assert.Equal("Released", evt.Payload["state"]!.GetValue<string>());
        Assert.Single(evt.Payload["files"]!.AsArray());
    }

    [Fact]
    public async Task Every_step_is_audited_and_the_notification_hooks_fire_inside_the_workflow()
    {
        var env = await DocReviewEnv.CreateAsync(pg);
        var doc = await env.NewDocumentAsync();
        var started = await env.StartAsync(doc, null, "rita", "bob");
        var afterReview = await env.SignAsync(Rita, started.Tasks[0].Id, "Reviewed");
        var done = await env.SignAsync(Bob, TaskOf(afterReview, "bob"), "Approved");
        Assert.Equal("Approved", done.Status);

        var workflow = (await env.AuditAsync()).Where(e => e.Action.StartsWith("workflow.", StringComparison.Ordinal)).ToList();
        Assert.Equal(["workflow.start", "workflow.task_assign", "workflow.sign", "workflow.task_assign", "workflow.sign", "workflow.complete"], workflow.Select(e => e.Action));
        Assert.All(workflow, e => Assert.Equal(AuditOutcome.Success, e.Outcome));
        Assert.All(workflow, e => Assert.Equal(("document", doc), (e.ObjectType, e.ObjectId)));
        Assert.Contains(done.Signatures[1].SignatureHash, workflow.Last(e => e.Action == "workflow.sign").Payload, StringComparison.Ordinal);

        // Notifications: watchers hear that the document entered review; the reviewer is asked; the approver is assigned; the submitter hears the decision; signers watch.
        var kinds = env.Sinks.Notifications.Select(n => (n.Kind, n.ObjectType, n.ObjectNumber)).ToList();
        Assert.Contains((NotificationKindCodes.WatchedState, AwarenessKinds.Document, doc), kinds);
        Assert.Contains(env.Sinks.Notifications, n => n.Kind == NotificationKindCodes.ReviewRequested && n.RecipientSub == Rita.Id && n.ObjectType == AwarenessKinds.Document);
        Assert.Contains(env.Sinks.Notifications, n => n.Kind == NotificationKindCodes.TaskAssigned && n.RecipientSub == Bob.Id);
        Assert.Contains(env.Sinks.Notifications, n => n.Kind == NotificationKindCodes.ReviewApproved && n.RecipientSub == Alice.Id);
        Assert.Contains((Rita.Id, AwarenessKinds.Document, doc), env.Sinks.Watches);
        Assert.All(env.Sinks.Notifications, n => Assert.Equal(env.Sinks.Notifications[0].Markings, n.Markings));
    }

    [Fact]
    public async Task Submitting_needs_a_primary_file_a_check_in_and_the_right_to_modify()
    {
        var env = await DocReviewEnv.CreateAsync(pg);
        var bare = await env.NewDocumentAsync("No file yet", primary: false);
        var refused = await env.Flow.StartAsync(Alice, DocReviewEnv.Start(bare), null);
        Assert.Equal(OutcomeKind.Conflict, refused.Kind);
        Assert.Contains("primary file", refused.Errors[0], StringComparison.Ordinal);
        Assert.Equal("InWork", await env.StateAsync(bare));

        var doc = await env.NewDocumentAsync("Checked out");
        Assert.True((await env.Docs.CheckOutAsync(Alice, doc, null)).IsOk);
        var checkedOut = await env.Flow.StartAsync(Alice, DocReviewEnv.Start(doc), null);
        Assert.Equal(OutcomeKind.Conflict, checkedOut.Kind);
        Assert.Contains("Checked out", checkedOut.Errors[0], StringComparison.Ordinal);
        Assert.True((await env.Docs.UndoCheckOutAsync(Alice, doc, null)).IsOk);

        // Viewers who may read but not modify cannot submit; people who may not read it get nothing to learn from the answer.
        Assert.Equal(OutcomeKind.Forbidden, (await env.Flow.StartAsync(Mallory, DocReviewEnv.Start(doc), null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Flow.StartAsync(Lena, DocReviewEnv.Start(doc), null)).Kind);
        Assert.Equal("InWork", await env.StateAsync(doc));
        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.Action == "workflow.start" && e.Outcome == AuditOutcome.Denied && e.ObjectId == doc);

        // A second review cannot start while one runs, and a template for another object type is not offered.
        var started = await env.StartAsync(doc);
        Assert.Equal(OutcomeKind.Conflict, (await env.Flow.StartAsync(Alice, DocReviewEnv.Start(doc), null)).Kind);
        Assert.Equal("document", started.Target.ObjectType);
        var templates = await env.Flow.ListTemplatesAsync(Alice, activeOnly: true, null);
        Assert.Contains(templates, t => t.Id == "document-release-review" && t.ObjectType == "document");
    }

    [Fact]
    public async Task The_manual_path_can_neither_submit_nor_release_a_document_but_the_administrator_override_still_works()
    {
        var env = await DocReviewEnv.CreateAsync(pg);
        var doc = await env.NewDocumentAsync();
        var detail = await env.GetAsync(doc);
        var toReview = Assert.Single(detail.Transitions);
        Assert.Equal(("UnderReview", "workflow", false), (toReview.To, toReview.Via, toReview.Allowed));
        Assert.Contains("Review & release", toReview.Reason, StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Conflict, (await env.Docs.SetStateAsync(Alice, doc, "UnderReview", null, false, null)).Kind);

        // Administrators correct a state with a reason; it is audited as an override, and a workflow orphaned by it cannot be signed.
        var started = await env.StartAsync(doc);
        Assert.Equal("InWork", (await env.Docs.SetStateAsync(Ada, doc, "InWork", "Test: orphan the review", true, null)).Value!.Revision.State);
        var orphan = await env.Flow.SignAsync(Fresh(Rita), started.Tasks[0].Id, "Reviewed", null, null);
        Assert.Equal(OutcomeKind.Conflict, orphan.Kind);
        Assert.True((await env.Flow.CancelAsync(Alice, started.Id, "Closed after the override", null)).IsOk);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "document.set_state" && e.ObjectId == doc);
    }

    [Fact]
    public async Task A_rejection_returns_the_document_to_work_and_a_new_review_can_start()
    {
        var env = await DocReviewEnv.CreateAsync(pg);
        var doc = await env.NewDocumentAsync();
        var started = await env.StartAsync(doc);
        var afterReview = await env.SignAsync(Rita, started.Tasks[0].Id, "ApprovedWithComments", "Fine from my side");
        var noComment = await env.Flow.SignAsync(Fresh(Bob), TaskOf(afterReview, "bob"), "Rejected", " ", null);
        Assert.Equal(OutcomeKind.Invalid, noComment.Kind);

        var rejected = await env.SignAsync(Bob, TaskOf(afterReview, "bob"), "Rejected", "Wrong revision of the load table");
        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal("Skipped", rejected.Tasks.Single(t => t.AssigneeUsername == "carol").Status);
        Assert.Equal("InWork", await env.StateAsync(doc));
        Assert.Empty(env.Sinks.Events);

        // Unlocked again: check out, change, check in, review the new iteration.
        Assert.True((await env.Docs.CheckOutAsync(Alice, doc, null)).IsOk);
        Assert.True((await env.Docs.UpdateWorkingCopyAsync(Alice, doc, "Corrected load table", null)).IsOk);
        Assert.True((await env.Docs.CheckInAsync(Alice, doc, "fix", null)).IsOk);
        var again = await env.StartAsync(doc);
        Assert.Equal("A.2", again.Target.IterationLabel);
        var status = (await env.Flow.StatusForObjectAsync(Alice, "document", doc, null)).Value!;
        Assert.Equal(("Active", "A.2"), (status.Active!.Status, status.Active.Target.IterationLabel));
        Assert.Empty(status.Releases);
    }

    [Fact]
    public async Task The_starter_or_an_admin_can_cancel_and_an_admin_can_reassign_but_never_to_the_submitter()
    {
        var env = await DocReviewEnv.CreateAsync(pg);
        var doc = await env.NewDocumentAsync();
        var started = await env.StartAsync(doc);

        Assert.Equal(OutcomeKind.Forbidden, (await env.Flow.CancelAsync(Mallory, started.Id, "just because", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Flow.CancelAsync(Alice, started.Id, " ", null)).Kind);
        Assert.Equal("UnderReview", await env.StateAsync(doc));

        // Reassign the open review task: administrators only, to an eligible person, never to the submitter.
        var task = started.Tasks[0];
        Assert.Equal(OutcomeKind.Forbidden, (await env.Flow.ReassignTaskAsync(Alice, task.Id, "dave", "x", null)).Kind);
        var toSubmitter = await env.Flow.ReassignTaskAsync(Ada, task.Id, "alice", "Bob is on leave", null);
        Assert.False(toSubmitter.IsOk);
        var moved = (await env.Flow.ReassignTaskAsync(Ada, task.Id, "dave", "Rita is on leave", "corr-reassign")).Value!;
        Assert.Equal("dave", moved.Tasks.Single(t => t.Id == task.Id).AssigneeUsername);
        Assert.Contains(env.Sinks.Notifications, n => n.RecipientSub == Dave.Id && n.Kind == NotificationKindCodes.ReviewRequested);
        var afterDave = await env.SignAsync(Dave, task.Id, "Reviewed");
        Assert.Equal("Active", afterDave.Status);

        var cancelled = (await env.Flow.CancelAsync(Alice, started.Id, "Wrong version submitted", "corr-cancel")).Value!;
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal("InWork", await env.StateAsync(doc));
        Assert.Contains("Wrong version submitted", cancelled.CompletionNote, StringComparison.Ordinal);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "workflow.cancel" && e.ObjectType == "document" && e.ObjectId == doc);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "workflow.reassign" && e.ObjectType == "document" && e.ObjectId == doc);
    }

    [Fact]
    public async Task The_submitter_cannot_review_or_sign_their_own_document()
    {
        var env = await DocReviewEnv.CreateAsync(pg);
        var doc = await env.NewDocumentAsync();
        var self = await env.Flow.StartAsync(Alice, DocReviewEnv.Start(doc, "alice"), null);
        Assert.False(self.IsOk);
        Assert.Equal("InWork", await env.StateAsync(doc));
    }

    [Fact]
    public async Task Signatures_cover_the_description_and_every_file_so_tampering_is_detected()
    {
        var env = await DocReviewEnv.CreateAsync(pg);
        var doc = await env.NewDocumentAsync(attachment: true);
        var started = await env.StartAsync(doc);
        var afterReview = await env.SignAsync(Rita, started.Tasks[0].Id, "Reviewed");
        var done = await env.SignAsync(Bob, TaskOf(afterReview, "bob"), "Approved");

        var clean = (await env.Flow.VerifyInstanceAsync(Alice, done.Id, "corr-verify")).Value!;
        Assert.Equal(2, clean.Count);
        Assert.All(clean, v => Assert.True(v.Valid, string.Join("; ", v.Problems)));

        // The iteration's description is covered ...
        await env.Db.ExecuteAsSuperuserAsync("SET session_replication_role = replica; UPDATE plm.document_iteration SET description = 'Tampered description'");
        Assert.All((await env.Flow.VerifyInstanceAsync(Alice, done.Id, null)).Value!, v => Assert.False(v.Valid));
        await env.Db.ExecuteAsSuperuserAsync("SET session_replication_role = replica; UPDATE plm.document_iteration SET description = 'Synthetic specification'");
        Assert.All((await env.Flow.VerifyInstanceAsync(Alice, done.Id, null)).Value!, v => Assert.True(v.Valid));

        // ... and so is every file: swapping an attachment's content hash, or its name, breaks verification.
        await env.Db.ExecuteAsSuperuserAsync($"SET session_replication_role = replica; UPDATE plm.document_file SET sha256 = '{new string('a', 64)}' WHERE role = 'attachment'");
        var swapped = (await env.Flow.VerifyInstanceAsync(Alice, done.Id, null)).Value!;
        Assert.All(swapped, v =>
        {
            Assert.False(v.Valid);
            Assert.Contains(v.Problems, p => p.Contains("changed after signing", StringComparison.Ordinal));
        });
        Assert.Contains((await env.AuditAsync()), e => e.Action == "workflow.verify" && e.Outcome == AuditOutcome.Failure);
    }

    [Fact]
    public async Task A_file_quarantined_before_or_during_the_review_blocks_submission_and_release()
    {
        var env = await DocReviewEnv.CreateAsync(pg);
        var held = await env.NewDocumentAsync("Held back");
        await env.Db.ExecuteAsSuperuserAsync(
            "INSERT INTO plm.file_quarantine (master_id, sha256, signature, quarantined_at, quarantined_by) " +
            "SELECT m.id, f.sha256, 'Test.Signature', now(), 'scanner' FROM plm.document_master m JOIN plm.document_revision r ON r.master_id = m.id JOIN plm.document_iteration i ON i.revision_id = r.id JOIN plm.document_file f ON f.iteration_id = i.id WHERE m.number = '" + held + "'");
        var refused = await env.Flow.StartAsync(Alice, DocReviewEnv.Start(held), null);
        Assert.Equal(OutcomeKind.Conflict, refused.Kind);
        Assert.Contains("quarantined", refused.Errors[0], StringComparison.Ordinal);

        var doc = await env.NewDocumentAsync("Flagged during review");
        var started = await env.StartAsync(doc);
        var afterReview = await env.SignAsync(Rita, started.Tasks[0].Id, "Reviewed");
        await env.Db.ExecuteAsSuperuserAsync(
            "INSERT INTO plm.file_quarantine (master_id, sha256, signature, quarantined_at, quarantined_by) " +
            "SELECT m.id, f.sha256, 'Test.Signature', now(), 'scanner' FROM plm.document_master m JOIN plm.document_revision r ON r.master_id = m.id JOIN plm.document_iteration i ON i.revision_id = r.id JOIN plm.document_file f ON f.iteration_id = i.id WHERE m.number = '" + doc + "'");
        var afterBob = await env.SignAsync(Bob, TaskOf(afterReview, "bob"), "Approved");
        Assert.Equal("Active", afterBob.Status);
        var last = await env.Flow.SignAsync(Fresh(Carol), TaskOf(afterBob, "carol"), "Approved", null, null);
        Assert.False(last.IsOk);
        Assert.Contains("quarantined", string.Join(" ", last.Errors), StringComparison.Ordinal);
        Assert.Equal("UnderReview", await env.StateAsync(doc));
        Assert.Empty(env.Sinks.Events);
    }

    // ---------------------------------------------------------------- markings: the SQL read filter against the in-memory policy

    private static Markings Itar(string program, string org = "ENG") => Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, org, usmlCategory: "XI(d)",
        cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: program).Value;

    private static readonly Subject Sam = User("sam", [Roles.Viewer, Roles.Author], programs: ["ORCA", "SABLE"]);
    private static readonly Subject Reviewer = User("rev", [Roles.Viewer], programs: ["ORCA", "SABLE"]);
    private static readonly Subject ApproverOne = User("aone", [Roles.Viewer, Roles.Approver], programs: ["ORCA", "SABLE"]);

    [Fact]
    public async Task Review_lists_and_pages_never_show_a_document_the_caller_cannot_read_the_sql_filter_agrees_with_the_policy()
    {
        var probes = new[]
        {
            User("p-admin-us", [Roles.Admin, Roles.Viewer], programs: ["ORCA", "SABLE"]),
            User("p-admin-orca", [Roles.Admin, Roles.Viewer], programs: ["ORCA"]),
            User("p-admin-none", [Roles.Admin, Roles.Viewer], programs: []),
            User("p-admin-foreign", [Roles.Admin, Roles.Viewer], us: false, programs: ["ORCA", "SABLE"]),
            User("p-admin-foreign-auth", [Roles.Admin, Roles.Viewer], us: false, programs: ["ORCA"], auths: ["TAA-0042"]),
            User("p-admin-nocui", [Roles.Admin, Roles.Viewer], programs: ["ORCA", "SABLE"], cui: []),
            User("p-admin-otherorg", [Roles.Admin, Roles.Viewer], org: "OTH", programs: ["ORCA", "SABLE"]),
        };
        var env = await DocReviewEnv.CreateAsync(pg, [Sam, Reviewer, ApproverOne, .. probes]);

        var marked = new List<(string Number, Markings Markings)>();
        foreach (var (name, markings) in new[]
                 {
                     ("Open spec", Open), ("Orca spec", Itar("ORCA")), ("Sable spec", Itar("SABLE")), ("Other org spec", Itar("ORCA", "OTH")),
                     ("Orca spec with authorization", Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"],
                         controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA", exportAuthorizations: ["TAA-0042"]).Value),
                 })
        {
            var number = await env.NewDocumentAsync(name, markings, Sam);
            await env.StartAsync(number, Sam, "rev", "aone");
            marked.Add((number, markings));
        }

        foreach (var probe in probes)
        {
            var expected = marked.Where(d => AccessPolicy.Evaluate(probe, d.Markings, PlmAction.Read).Permitted).Select(d => d.Number).Order().ToList();

            // The administrator's list of workflows (SQL filter over the instance's marking snapshot, then the live markings).
            var page = (await env.Flow.ListInstancesAsync(probe, null, 100, 0, "corr-list")).Value!;
            var listed = page.Items.Where(i => i.Target.ObjectType == "document").Select(i => i.Target.Key).Order().ToList();
            Assert.Equal(expected, listed);
            Assert.All(page.Items, i => Assert.DoesNotContain(marked.Where(d => !expected.Contains(d.Number)).Select(d => d.Number), n => n == i.Target.Key));

            foreach (var (number, markings) in marked)
            {
                var mayRead = expected.Contains(number);
                var status = await env.Flow.StatusForObjectAsync(probe, "document", number, null);
                Assert.Equal(mayRead ? OutcomeKind.Ok : OutcomeKind.NotFound, status.Kind);
                if (mayRead) Assert.Equal(markings.BannerText(), status.Value!.Active!.Target.Banner);
                else Assert.All(status.Errors, e => { Assert.DoesNotContain(number, e, StringComparison.Ordinal); Assert.DoesNotContain(markings.BannerText(), e, StringComparison.Ordinal); });

                var slice = await new WorkflowTimelineSource(env.Flow).GetAsync(probe, "document", number, null, CancellationToken.None);
                Assert.Equal(mayRead, slice.Accessible);
                Assert.Equal(mayRead, slice.Events.Count > 0);

                var search = await env.Flow.SearchUsersAsync(probe, "", Roles.Approver, "document", number, 50, null);
                Assert.Equal(mayRead ? OutcomeKind.Ok : OutcomeKind.NotFound, search.Kind);
            }
        }

        // Instance pages and the progress map follow the same rule for people who hold no task.
        var instances = (await env.Flow.ListInstancesAsync(probes[0], null, 100, 0, null)).Value!.Items;
        foreach (var probe in probes)
            foreach (var inst in instances)
            {
                var mayRead = marked.Single(d => d.Number == inst.Target.Key).Let(d => AccessPolicy.Evaluate(probe, d.Markings, PlmAction.Read).Permitted);
                Assert.Equal(mayRead ? OutcomeKind.Ok : OutcomeKind.NotFound, (await env.Flow.GetInstanceAsync(probe, inst.Id, null)).Kind);
                Assert.Equal(mayRead ? OutcomeKind.Ok : OutcomeKind.NotFound, (await env.Flow.GetMapAsync(probe, inst.Id, null)).Kind);
                Assert.Equal(mayRead ? OutcomeKind.Ok : OutcomeKind.NotFound, (await env.Flow.VerifyInstanceAsync(probe, inst.Id, null)).Kind);
            }

        // Restricted tasks do not leak either: the reviewer's own inbox shows what the reviewer may read, and nothing of the rest.
        var limited = User("rev", [Roles.Viewer], programs: ["ORCA"]);
        var tasks = (await env.Flow.MyTasksAsync(limited, null)).Items;
        Assert.All(tasks.Where(t => !t.Redacted), t => Assert.True(marked.Single(d => d.Number == t.Target!.Key).Let(d => AccessPolicy.Evaluate(limited, d.Markings, PlmAction.Read).Permitted)));
        Assert.All(tasks.Where(t => t.Redacted), t => Assert.Null(t.Target));
    }

    [Fact]
    public async Task People_who_may_not_see_the_document_cannot_be_chosen_as_reviewers_or_sign()
    {
        var env = await DocReviewEnv.CreateAsync(pg);
        var doc = await env.NewDocumentAsync();
        // Lena is not a U.S. person and holds no authorization: she is not eligible, whatever her roles say.
        var foreign = await env.Flow.StartAsync(Alice, DocReviewEnv.Start(doc, "rita", "lena"), null);
        Assert.False(foreign.IsOk);
        Assert.Equal("InWork", await env.StateAsync(doc));

        var started = await env.StartAsync(doc);
        var afterReview = await env.SignAsync(Rita, started.Tasks[0].Id, "Reviewed");
        Assert.Equal(OutcomeKind.Forbidden, (await env.Flow.SignAsync(Fresh(Mallory), TaskOf(afterReview, "bob"), "Approved", null, null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Flow.SignAsync(Fresh(Lena), TaskOf(afterReview, "bob"), "Approved", null, null)).Kind);
        // A signature needs a fresh authentication (step-up).
        var stale = await env.Flow.SignAsync(Alice with { AuthTime = null }, TaskOf(afterReview, "bob"), "Approved", null, null);
        Assert.False(stale.IsOk);
        Assert.Equal("UnderReview", await env.StateAsync(doc));
    }

    [Fact]
    public async Task The_review_appears_on_the_document_timeline_and_the_progress_map_for_readers_only()
    {
        var env = await DocReviewEnv.CreateAsync(pg);
        var doc = await env.NewDocumentAsync();
        var started = await env.StartAsync(doc);
        var afterReview = await env.SignAsync(Rita, started.Tasks[0].Id, "Reviewed");

        var slice = await new WorkflowTimelineSource(env.Flow).GetAsync(Alice, "document", doc, null, CancellationToken.None);
        Assert.True(slice.Accessible);
        Assert.Contains(slice.Events, e => e.Title.StartsWith("Submitted for review", StringComparison.Ordinal));
        Assert.Contains(slice.Events, e => e.Title.Contains("Rita Example signed", StringComparison.Ordinal));
        Assert.True(new WorkflowTimelineSource(env.Flow).Handles("document"));

        var map = (await env.Flow.GetMapAsync(Alice, started.Id, null)).Value!;
        Assert.False(map.Redacted);
        Assert.Equal("Document release review", map.TemplateName);
        Assert.Equal(map.Banner, (await env.GetAsync(doc)).Banner);
        Assert.Contains(map.Stages, s => s.Name == "Technical review" && s.Tasks.Any(t => t.Status == "Signed"));
        Assert.Equal(OutcomeKind.NotFound, (await env.Flow.GetMapAsync(Lena, started.Id, null)).Kind);
        Assert.NotEmpty(afterReview.Tasks);
    }

    [Fact]
    public async Task Migration_0017_makes_review_transitions_workflow_only_in_every_document_lifecycle_version()
    {
        var env = await DocReviewEnv.CreateAsync(pg);
        await using var cmd = env.Ds.CreateCommand(
            "SELECT from_state, to_state, via FROM plm.lifecycle_transition WHERE lifecycle_id IN (SELECT id FROM plm.lifecycle WHERE family = 'document-default') ORDER BY from_state, to_state");
        var rows = new List<(string, string, string)>();
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        Assert.Equal([("InWork", "UnderReview", "workflow"), ("Released", "Obsolete", "change"), ("UnderReview", "InWork", "workflow"), ("UnderReview", "Released", "workflow")], rows);

        await using var template = env.Ds.CreateCommand("SELECT object_type, is_active FROM wf.template WHERE id = 'document-release-review' AND version = 1");
        await using var t = await template.ExecuteReaderAsync();
        Assert.True(await t.ReadAsync());
        Assert.Equal(("document", true), (t.GetString(0), t.GetBoolean(1)));
    }
}

internal static class TestExtensions
{
    public static TResult Let<T, TResult>(this T value, Func<T, TResult> f) => f(value);
}
