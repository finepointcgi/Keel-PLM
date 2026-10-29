using Keel.Audit;
using Keel.Core;
using Keel.Records;

namespace Keel.IntegrationTests;

/// <summary>Retention schedules as signed, versioned data, program events, classification and the disposition queue (ADR-0041).</summary>
[Collection(PostgresCollection.Name)]
public sealed class RecordsScheduleTests(PostgresFixture pg)
{
    [Fact]
    public async Task A_schedule_is_drafted_signed_and_published_and_a_published_version_never_changes()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        var v1 = await env.PublishScheduleAsync(RecordsEnv.DocClass("SPEC"));

        // Three signatures (the reviewer and both approvers) cover the schedule.
        Assert.Equal(3, await env.ScalarAsync("SELECT count(*) FROM wf.signature WHERE object_type = 'retention_schedule' AND object_key = @k", ("k", $"v{v1}")));
        var published = (await env.Schedules.GetPublishedAsync(RecordsEnv.Rex, null)).Value!;
        Assert.Equal(ScheduleStatuses.Published, published.Status);
        Assert.Equal("SPEC", Assert.Single(published.Classes).Input.Code);
        Assert.Equal("Policy 7.1", published.Classes[0].Input.Citation);

        // The database refuses to change or delete anything of a published version, for the application role too.
        Assert.Contains("draft", await env.AppSqlAsync("UPDATE rec.record_class SET name = 'changed'") ?? "ran", StringComparison.Ordinal);
        Assert.Contains("draft", await env.AppSqlAsync("DELETE FROM rec.record_class") ?? "ran", StringComparison.Ordinal);
        Assert.Contains("only a draft", await env.AppSqlAsync("UPDATE rec.schedule SET title = 'changed' WHERE status = 'published'") ?? "ran", StringComparison.Ordinal);
        Assert.NotNull(await env.AppSqlAsync("DELETE FROM rec.schedule")); // no privilege at all

        // The next version is a copy, edited; publishing it supersedes the first, which stays on record.
        var v2 = await env.PublishScheduleAsync(RecordsEnv.DocClass("OTHER", months: 12));
        Assert.Equal(v1 + 1, v2);
        var list = (await env.Schedules.ListAsync(RecordsEnv.Rex, null)).Value!;
        Assert.Equal([(v2, ScheduleStatuses.Published), (v1, ScheduleStatuses.Superseded)], list.Select(s => (s.Version, s.Status)));
        Assert.Equal(["OTHER", "SPEC"], (await env.Schedules.GetAsync(RecordsEnv.Rex, v2, null)).Value!.Classes.Select(c => c.Input.Code).Order(StringComparer.Ordinal));
        Assert.Equal(["SPEC"], (await env.Schedules.GetAsync(RecordsEnv.Rex, v1, null)).Value!.Classes.Select(c => c.Input.Code));

        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.Action == "records.schedule_publish" && e.ObjectId == $"v{v2}");
        Assert.Contains(audit, e => e.Action == "records.schedule_submit" && e.ObjectId == $"v{v2}");
    }

    [Fact]
    public async Task Only_one_draft_is_open_and_a_draft_can_be_validated_edited_and_discarded()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        var draft = (await env.Schedules.CreateDraftAsync(RecordsEnv.Rex, "First", null, null)).Value!;
        Assert.Equal(OutcomeKind.Conflict, (await env.Schedules.CreateDraftAsync(RecordsEnv.Rex, "Second", null, null)).Kind);

        var bad = await env.Schedules.UpsertClassAsync(RecordsEnv.Rex, draft.Version, RecordsEnv.DocClass("x"), null);
        Assert.Equal(OutcomeKind.Invalid, bad.Kind);
        var ok = await env.Schedules.UpsertClassAsync(RecordsEnv.Rex, draft.Version, RecordsEnv.DocClass("SPEC", months: 36), null);
        Assert.True(ok.IsOk);
        // Same code replaces; the list does not grow.
        var replaced = await env.Schedules.UpsertClassAsync(RecordsEnv.Rex, draft.Version, RecordsEnv.DocClass("SPEC", months: 60), null);
        Assert.Equal(60, Assert.Single(replaced.Value!.Classes).Input.RetentionMonths);
        Assert.True((await env.Schedules.RemoveClassAsync(RecordsEnv.Rex, draft.Version, "SPEC", null)).IsOk);
        Assert.Equal(OutcomeKind.Invalid, (await env.Schedules.RemoveClassAsync(RecordsEnv.Rex, draft.Version, "SPEC", null)).Kind);

        // An empty schedule cannot be submitted.
        var started = await env.Flow.StartAsync(RecordsEnv.Rex, new Keel.Workflow.StartReviewCommand("retention_schedule", $"v{draft.Version}", new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob"] }), null);
        Assert.False(started.IsOk);
        Assert.Contains("at least one record class", string.Join(" ", started.Errors), StringComparison.Ordinal);

        Assert.True((await env.Schedules.DiscardDraftAsync(RecordsEnv.Rex, draft.Version, null)).IsOk);
        Assert.Equal(OutcomeKind.Conflict, (await env.Schedules.UpsertClassAsync(RecordsEnv.Rex, draft.Version, RecordsEnv.DocClass("SPEC"), null)).Kind);
        Assert.True((await env.Schedules.CreateDraftAsync(RecordsEnv.Rex, "Third", null, null)).IsOk); // the discarded draft no longer blocks
    }

    [Fact]
    public async Task A_rejected_schedule_returns_to_draft_and_publishes_nothing()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        var draft = (await env.Schedules.CreateDraftAsync(RecordsEnv.Rex, "Rejected", null, null)).Value!;
        await env.Schedules.UpsertClassAsync(RecordsEnv.Rex, draft.Version, RecordsEnv.DocClass("SPEC"), null);
        var started = (await env.Flow.StartAsync(RecordsEnv.Rex, new Keel.Workflow.StartReviewCommand("retention_schedule", $"v{draft.Version}", new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob"] }), null)).Value!;
        Assert.Equal(ScheduleStatuses.InReview, (await env.Schedules.GetAsync(RecordsEnv.Rex, draft.Version, null)).Value!.Status);
        // While in review the schedule cannot be edited.
        Assert.Equal(OutcomeKind.Conflict, (await env.Schedules.UpsertClassAsync(RecordsEnv.Rex, draft.Version, RecordsEnv.DocClass("MORE"), null)).Kind);
        var reject = await env.Flow.SignAsync(WorkflowEnv.Fresh(RecordsEnv.Rita), started.Tasks.Single(t => t.Status == "Open").Id, "Rejected", "Needs a citation", "corr");
        Assert.True(reject.IsOk, string.Join("; ", reject.Errors));
        Assert.Equal(ScheduleStatuses.Draft, (await env.Schedules.GetAsync(RecordsEnv.Rex, draft.Version, null)).Value!.Status);
        Assert.Equal(OutcomeKind.NotFound, (await env.Schedules.GetPublishedAsync(RecordsEnv.Rex, null)).Kind);
    }

    [Fact]
    public async Task Records_data_is_visible_only_to_records_roles_and_denials_are_audited()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        await env.PublishScheduleAsync(RecordsEnv.DocClass("SPEC"));
        // An author, an approver and a plain viewer have no records role.
        foreach (var who in new[] { RecordsEnv.Alice, RecordsEnv.Bob, WorkflowEnv.Mallory })
        {
            Assert.Equal(OutcomeKind.Forbidden, (await env.Schedules.ListAsync(who, null)).Kind);
            Assert.Equal(OutcomeKind.Forbidden, (await env.Schedules.CreateDraftAsync(who, "x", null, null)).Kind);
            Assert.Equal(OutcomeKind.Forbidden, (await env.Holds.ListAsync(who, true, null)).Kind);
            Assert.Equal(OutcomeKind.Forbidden, (await env.Disposition.EligibleAsync(who, new EligibleQuery(), null)).Kind);
            Assert.Equal(OutcomeKind.Forbidden, (await env.Archives.ListAsync(who, null)).Kind);
            Assert.Equal(OutcomeKind.Forbidden, (await env.Classifier.ClassifyAsync(who, null)).Kind);
            Assert.Null(await env.Holds.StatusOfAsync(who, "document", "D-000001"));
        }
        // An auditor reads, but cannot change anything; an administrator manages but cannot execute.
        Assert.True((await env.Schedules.ListAsync(RecordsEnv.Audrey, null)).IsOk);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Schedules.CreateDraftAsync(RecordsEnv.Audrey, "x", null, null)).Kind);
        Assert.True((await env.Schedules.CreateDraftAsync(RecordsEnv.Ada, "By admin", null, null)).IsOk);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Disposition.ExecuteAsync(RecordsEnv.Ada, "DR-000001", null)).Kind);

        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.Action == "records.schedule_create" && e.Outcome == AuditOutcome.Denied && e.Actor.Contains("alice", StringComparison.Ordinal));
        Assert.Contains(audit, e => e.Action == "records.disposition_execute" && e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task Program_events_are_appended_never_changed_and_the_latest_one_counts()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        Assert.True((await env.Schedules.RecordProgramEventAsync(RecordsEnv.Rex, "ORCA", TriggerEvents.ProgramClose, new DateOnly(2024, 1, 1), null)).IsOk);
        Assert.True((await env.Schedules.RecordProgramEventAsync(RecordsEnv.Rex, "ORCA", TriggerEvents.ProgramClose, new DateOnly(2025, 6, 30), null)).IsOk); // a correction
        Assert.Equal(OutcomeKind.Invalid, (await env.Schedules.RecordProgramEventAsync(RecordsEnv.Rex, "ORCA", "release", new DateOnly(2025, 6, 30), null)).Kind);
        var events = (await env.Schedules.ListProgramEventsAsync(RecordsEnv.Rex, null)).Value!;
        Assert.Equal(new DateOnly(2025, 6, 30), Assert.Single(events).OccurredOn);
        Assert.Equal(2, await env.ScalarAsync("SELECT count(*) FROM rec.program_event"));
        Assert.NotNull(await env.AppSqlAsync("UPDATE rec.program_event SET occurred_on = '2000-01-01'")); // the application role has no UPDATE or DELETE privilege on the table
        Assert.NotNull(await env.AppSqlAsync("DELETE FROM rec.program_event"));
    }

    [Fact]
    public async Task Classification_puts_each_object_in_its_class_and_the_queue_lists_only_what_is_due()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        await env.PublishScheduleAsync(RecordsEnv.DocClass("DRAW", priority: 10, months: 60, types: ["Drawing"]), RecordsEnv.DocClass("SPEC", priority: 20, months: 0, types: ["Specification"]),
            RecordsEnv.DocClass("OBS", priority: 30, months: 0, types: ["Report"], trigger: TriggerEvents.Obsolescence));
        var dueSpec = await env.ReleasedDocAsync("Due spec");
        var youngDrawing = await env.ReleasedDocAsync("Young drawing", type: "Drawing");
        var inWork = await env.NewDocAsync("Still in work");
        var report = await env.NewDocAsync("Old report", type: "Report");
        await env.ReleaseDocAsync(report);
        await env.ObsoleteDocAsync(report);
        var memo = await env.ReleasedDocAsync("No class", type: "Procedure");
        await env.ClassifyAsync();

        var queue = (await env.Disposition.EligibleAsync(RecordsEnv.Rex, new EligibleQuery(), null)).Value!;
        Assert.Equal(new[] { dueSpec, report }.Order(StringComparer.Ordinal), queue.Items.Select(i => i.Number).Order(StringComparer.Ordinal));
        Assert.Equal(2, queue.Total);
        Assert.DoesNotContain(queue.Items, i => i.Number == youngDrawing || i.Number == inWork || i.Number == memo);
        Assert.Equal("OBS", queue.Items.Single(i => i.Number == report).ClassCode);
        Assert.Equal(TriggerEvents.Obsolescence, queue.Items.Single(i => i.Number == report).TriggerEvent);

        var status = await env.Holds.StatusOfAsync(RecordsEnv.Rex, "document", youngDrawing);
        Assert.Equal("DRAW", status!.ClassCode);
        Assert.True(status.EligibleOn > DateOnly.FromDateTime(DateTime.UtcNow.AddYears(4)));
        Assert.Null((await env.Holds.StatusOfAsync(RecordsEnv.Rex, "document", memo))!.ClassCode);

        // The class of an object follows the schedule: no cached answer outlives a reclassification.
        Assert.Equal(1, (await env.Disposition.EligibleAsync(RecordsEnv.Rex, new EligibleQuery(Kind: "document", ClassCode: "SPEC"), null)).Value!.Total);
        Assert.Equal(1, (await env.Disposition.EligibleAsync(RecordsEnv.Rex, new EligibleQuery(Search: "old"), null)).Value!.Total);
    }

    [Fact]
    public async Task A_request_can_only_list_objects_that_are_due_unheld_and_not_in_another_request()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        await env.PublishScheduleAsync(RecordsEnv.DocClass("SPEC", months: 0));
        var due = await env.ReleasedDocAsync("Due");
        var young = await env.NewDocAsync("Never released");
        var held = await env.ReleasedDocAsync("Held");
        await env.PlaceObjectHoldAsync("document", held);

        var notDue = await env.Disposition.CreateAsync(RecordsEnv.Rex, new CreateDispositionCommand([new ObjectRef("document", young)], null), null);
        Assert.Equal(OutcomeKind.Invalid, notDue.Kind);
        Assert.Contains("not due", string.Join(" ", notDue.Errors), StringComparison.Ordinal);
        var heldTry = await env.Disposition.CreateAsync(RecordsEnv.Rex, new CreateDispositionCommand([new ObjectRef("document", held)], null), null);
        Assert.Equal(OutcomeKind.Invalid, heldTry.Kind);
        Assert.Contains("legal hold", string.Join(" ", heldTry.Errors), StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await env.Disposition.CreateAsync(RecordsEnv.Rex, new CreateDispositionCommand(Array.Empty<ObjectRef>(), null), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Disposition.CreateAsync(RecordsEnv.Rex, new CreateDispositionCommand([new ObjectRef("document", "D-999999")], null), null)).Kind);

        var first = await env.Disposition.CreateAsync(RecordsEnv.Rex, new CreateDispositionCommand([new ObjectRef("document", due)], "first"), null);
        Assert.True(first.IsOk, string.Join("; ", first.Errors));
        var second = await env.Disposition.CreateAsync(RecordsEnv.Rex, new CreateDispositionCommand([new ObjectRef("document", due)], null), null);
        Assert.Contains("already in request", string.Join(" ", second.Errors), StringComparison.Ordinal);
        // An object in an open request leaves the queue; cancelling puts it back.
        await env.ClassifyAsync();
        Assert.Empty((await env.Disposition.EligibleAsync(RecordsEnv.Rex, new EligibleQuery(), null)).Value!.Items);
        Assert.True((await env.Disposition.CancelAsync(RecordsEnv.Rex, first.Value!.Number, null)).IsOk);
        Assert.Equal(due, Assert.Single((await env.Disposition.EligibleAsync(RecordsEnv.Rex, new EligibleQuery(), null)).Value!.Items).Number);
    }
}
