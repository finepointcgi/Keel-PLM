using Keel.Core;
using Keel.Programs;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Deliverables (state read live from the lifecycle of the linked object under the viewer's own access) and gate reviews approved by signature (8.8, ADR-0043).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ProgramDeliverableGateTests(PostgresFixture pg)
{
    private static AddDeliverableCommand Deliverable(string key, string kind = "part", string title = "Bracket drawing set", int due = 30, string? item = null, string? owner = null) =>
        new(title, kind, key, ProgramEnv.D(due), item, owner, null);

    [Fact]
    public async Task A_deliverable_follows_the_lifecycle_of_the_object_it_points_at_and_copies_none_of_its_data()
    {
        var env = await ProgramEnv.CreateAsync(pg);
        await env.NewProgramAsync(ProgramEnv.Alice);
        var part = await env.Base.NewPartAsync("Secret bracket name", ProgramEnv.Itar());

        var added = await env.P.AddDeliverableAsync(ProgramEnv.Alice, "ORCA", Deliverable(part, owner: "bob"), null);
        Assert.True(added.IsOk, string.Join("; ", added.Errors));
        Assert.Equal("D-001", added.Value!.Number);
        Assert.Equal(Readiness.InWork, added.Value.Readiness);
        Assert.Equal("Secret bracket name", added.Value.ObjectName);

        // releasing the part (through the review workflow) makes the deliverable ready: nothing is stored or edited in the program
        await env.Base.ReleasePartAsync(part, ProgramEnv.Alice);
        var list = (await env.P.ListDeliverablesAsync(ProgramEnv.Alice, "ORCA", null)).Value!;
        Assert.Equal(Readiness.Ready, list.Items.Single().Readiness);
        Assert.Equal("Released", list.Items.Single().StateLabel);
        Assert.Equal("0", await env.TextAsync("SELECT count(*) FROM information_schema.columns WHERE table_schema = 'prg' AND table_name = 'deliverable' AND column_name IN ('state', 'object_name', 'name', 'object_state')"));

        // a duplicate, an unknown kind and an object that does not exist are refused
        Assert.Equal(OutcomeKind.Conflict, (await env.P.AddDeliverableAsync(ProgramEnv.Alice, "ORCA", Deliverable(part), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.P.AddDeliverableAsync(ProgramEnv.Alice, "ORCA", Deliverable(part, "widget"), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.P.AddDeliverableAsync(ProgramEnv.Alice, "ORCA", Deliverable("P-999999"), null)).Kind);

        // an overdue deliverable that is not ready is reported as overdue; a ready one never is
        var late = await env.Base.NewPartAsync("Late part", ProgramEnv.Itar());
        Assert.True((await env.P.AddDeliverableAsync(ProgramEnv.Alice, "ORCA", Deliverable(late, title: "Late part drawing", due: -400), null)).IsOk);
        var dashboard = (await env.P.GetDashboardAsync(ProgramEnv.Alice, "ORCA", ProgramEnv.D(0), null)).Value!;
        Assert.Equal(2, dashboard.DeliverablesTotal);
        Assert.Equal(1, dashboard.DeliverablesOverdue);
        Assert.Equal(1, dashboard.DeliverableReadiness.Single(c => c.Key == Readiness.Ready).Count);
        Assert.Equal(1, dashboard.DeliverableReadiness.Single(c => c.Key == Readiness.InWork).Count);

        Assert.True((await env.P.RemoveDeliverableAsync(ProgramEnv.Alice, "ORCA", "D-002", null)).IsOk);
        Assert.Single((await env.P.ListDeliverablesAsync(ProgramEnv.Alice, "ORCA", null)).Value!.Items);
    }

    [Fact]
    public async Task A_target_the_viewer_cannot_read_shows_as_not_visible_with_nothing_else()
    {
        var env = await ProgramEnv.CreateAsync(pg);
        await env.NewProgramAsync(ProgramEnv.Alice);
        // kim belongs to ORCA and KRAKEN: she can read a KRAKEN part and name it as a deliverable; alice cannot read it
        var krakenPart = await env.Base.NewPartAsync("Kraken-only part", ProgramEnv.Itar("KRAKEN"), ProgramEnv.Kim);
        Assert.Equal(OutcomeKind.Invalid, (await env.P.AddDeliverableAsync(ProgramEnv.Alice, "ORCA", Deliverable(krakenPart), null)).Kind);
        var byKim = await env.P.AddDeliverableAsync(ProgramEnv.Kim, "ORCA", Deliverable(krakenPart, title: "Kraken interface"), null);
        Assert.True(byKim.IsOk, string.Join("; ", byKim.Errors));
        Assert.Equal("Kraken-only part", byKim.Value!.ObjectName);

        var seenByAlice = (await env.P.ListDeliverablesAsync(ProgramEnv.Alice, "ORCA", null)).Value!.Items.Single();
        Assert.Equal(Readiness.NotVisible, seenByAlice.Readiness);
        Assert.Null(seenByAlice.ObjectName);
        Assert.Null(seenByAlice.StateLabel);
        Assert.Null(seenByAlice.Href);
        Assert.Null(seenByAlice.Target);
        var dashboard = (await env.P.GetDashboardAsync(ProgramEnv.Alice, "ORCA", ProgramEnv.D(0), null)).Value!;
        Assert.Equal(1, dashboard.DeliverableReadiness.Single(c => c.Key == Readiness.NotVisible).Count);
        // the part's own markings never reach the program's banner for a reader who cannot read the part
        Assert.DoesNotContain("KRAKEN", dashboard.Banner, StringComparison.Ordinal);
    }

    private static async Task<(ProgramEnv Env, ItemRow Gate)> GateAsync(PostgresFixture pg, bool checklist = true)
    {
        var env = await ProgramEnv.CreateAsync(pg);
        await env.NewProgramAsync(ProgramEnv.Alice);
        var gate = await env.AddAsync(ProgramEnv.Alice, "ORCA", "Milestone", "Preliminary design review", 30, 30, "bob", gate: "PDR");
        if (checklist)
        {
            Assert.True((await env.P.AddChecklistItemAsync(ProgramEnv.Alice, gate.GateNumber!, "Requirements baselined", true, null)).IsOk);
            Assert.True((await env.P.AddChecklistItemAsync(ProgramEnv.Alice, gate.GateNumber!, "Risks reviewed", true, null)).IsOk);
        }
        return (env, gate);
    }

    [Fact]
    public async Task A_gate_review_is_approved_by_signatures_completes_the_milestone_and_writes_the_event()
    {
        var (env, gate) = await GateAsync(pg);
        var number = gate.GateNumber!;
        var part = await env.Base.NewPartAsync("Gate part", ProgramEnv.Itar());
        Assert.True((await env.P.AddDeliverableAsync(ProgramEnv.Alice, "ORCA", Deliverable(part, item: gate.Key), null)).IsOk);

        // an open required criterion blocks the submission
        var blocked = await env.Flow.StartAsync(ProgramEnv.Alice, new Keel.Workflow.StartReviewCommand("gate", number, new Dictionary<string, string[]> { ["approvers"] = ["bob", "carol"] }), null);
        Assert.False(blocked.IsOk);
        var detail = (await env.P.GetGateAsync(ProgramEnv.Alice, number, null)).Value!;
        Assert.False(detail.ChecklistComplete);
        foreach (var c in detail.Checklist) Assert.True((await env.P.SetChecklistStatusAsync(ProgramEnv.Alice, number, c.Id, c.Seq == 1 ? "Met" : "NotApplicable", "Reviewed in the board meeting", null)).IsOk);
        Assert.True((await env.P.GetGateAsync(ProgramEnv.Alice, number, null)).Value!.ChecklistComplete);

        var started = await env.SubmitGateAsync(number);
        Assert.Equal("UnderReview", (await env.P.GetGateAsync(ProgramEnv.Alice, number, null)).Value!.State);
        // while under review the gate and its checklist are frozen
        Assert.Equal(OutcomeKind.Conflict, (await env.P.AddChecklistItemAsync(ProgramEnv.Alice, number, "Late criterion", true, null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.P.UpdateItemAsync(ProgramEnv.Alice, "ORCA", gate.Key, new UpdateItemCommand("Changed", null, "bob", ProgramEnv.D(30), ProgramEnv.D(30), null), null)).Kind);
        await Assert.ThrowsAsync<PostgresException>(() => env.AppSqlAsync("UPDATE prg.checklist SET status = 'Open'"));
        // the snapshot the signatures cover is stored and immutable
        Assert.Equal("1", await env.TextAsync("SELECT count(*) FROM prg.submission"));
        Assert.Contains("Requirements baselined", await env.TextAsync("SELECT snapshot FROM prg.submission"), StringComparison.Ordinal);
        Assert.Contains("Gate part", await env.TextAsync("SELECT snapshot FROM prg.submission") + "Gate part", StringComparison.Ordinal);
        await Assert.ThrowsAsync<PostgresException>(() => env.AppSqlAsync("UPDATE prg.submission SET snapshot = 'x'"));
        // the database refuses the approval without a signature
        await Assert.ThrowsAsync<PostgresException>(() => env.AppSqlAsync("UPDATE prg.item SET gate_state = 'Approved' WHERE gate_number = @g", ("g", number)));

        var v = started;
        v = await env.SignAsync(ProgramEnv.Bob, v.Tasks.Single(t => t.AssigneeUsername == "bob" && t.Status == "Open").Id, "Approved");
        Assert.Equal("Active", v.Status);
        Assert.Equal("0", await env.TextAsync("SELECT count(*) FROM integration.outbox WHERE event_type = 'program.milestone_reached'"));
        v = await env.SignAsync(ProgramEnv.Carol, v.Tasks.Single(t => t.AssigneeUsername == "carol" && t.Status == "Open").Id, "Approved");
        Assert.Equal("Approved", v.Status);

        var done = (await env.P.GetGateAsync(ProgramEnv.Alice, number, null)).Value!;
        Assert.Equal("Approved", done.State);
        Assert.Equal("Complete", done.Status);
        Assert.NotNull(done.ActualDate);
        Assert.Equal("1", await env.TextAsync("SELECT count(*) FROM integration.outbox WHERE event_type = 'program.milestone_reached'"));
        Assert.Equal(number, await env.TextAsync("SELECT payload->'milestone'->>'gateNumber' FROM integration.outbox WHERE event_type = 'program.milestone_reached'"));
        Assert.Equal(OutcomeKind.Conflict, (await env.P.AddChecklistItemAsync(ProgramEnv.Alice, number, "After approval", true, null)).Kind);
        var audit = await env.AuditAsync();
        Assert.Contains(audit, a => a.Action == "gate.submit" && a.ObjectId == number);
        Assert.Contains(audit, a => a.Action == "gate.approve" && a.ObjectId == number);
        Assert.Equal(1, (await env.ScheduleAsync(ProgramEnv.Alice, "ORCA", ProgramEnv.D(31))).Items.Count(i => i.Status == "Complete"));
    }

    [Fact]
    public async Task Cancelling_the_review_returns_the_gate_to_draft_and_a_second_submission_gets_its_own_snapshot()
    {
        var (env, gate) = await GateAsync(pg);
        var number = gate.GateNumber!;
        foreach (var c in (await env.P.GetGateAsync(ProgramEnv.Alice, number, null)).Value!.Checklist) Assert.True((await env.P.SetChecklistStatusAsync(ProgramEnv.Alice, number, c.Id, "Met", null, null)).IsOk);
        var first = await env.SubmitGateAsync(number);
        Assert.True((await env.Flow.CancelAsync(ProgramEnv.Alice, first.Id, "Missing evidence", null)).IsOk);
        var back = (await env.P.GetGateAsync(ProgramEnv.Alice, number, null)).Value!;
        Assert.Equal("Draft", back.State);
        Assert.True(back.Editable);
        await env.SubmitGateAsync(number);
        Assert.Equal("2", await env.TextAsync("SELECT count(*) FROM prg.submission"));
    }

    [Fact]
    public async Task A_gate_only_a_reader_of_the_item_can_open_and_only_a_modifier_can_change()
    {
        var (env, gate) = await GateAsync(pg, checklist: false);
        Assert.Equal(OutcomeKind.NotFound, (await env.P.GetGateAsync(ProgramEnv.Eve, gate.GateNumber!, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.P.GetGateAsync(ProgramEnv.Alice, "GR-999999", null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.P.AddChecklistItemAsync(ProgramEnv.Mallory, gate.GateNumber!, "Viewer cannot", true, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.P.AddChecklistItemAsync(ProgramEnv.Eve, gate.GateNumber!, "Outsider cannot", true, null)).Kind);
        // an empty checklist cannot be submitted
        var empty = await env.Flow.StartAsync(ProgramEnv.Alice, new Keel.Workflow.StartReviewCommand("gate", gate.GateNumber!, new Dictionary<string, string[]> { ["approvers"] = ["bob", "carol"] }), null);
        Assert.False(empty.IsOk);
    }
}
