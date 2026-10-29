using Keel.Core;
using Keel.Process;

namespace Keel.IntegrationTests;

[Collection("postgres")]
public sealed class ProcessPlanTests(PostgresFixture pg)
{
    [Fact]
    public async Task A_plan_needs_a_released_mbom_and_is_bound_to_its_revision()
    {
        var env = await ProcEnv.CreateAsync(pg);
        var (p, mbom) = await env.M.ReconciledProductAsync(); // reconciled, not released
        var refused = await env.Plans.CreateAsync(ProcEnv.Alice, new CreatePlanCommand(mbom, null), "c");
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        Assert.Contains("no released revision", string.Join(" ", refused.Errors), StringComparison.Ordinal);

        await env.M.ReleaseAsync(mbom);
        var plan = await env.CreatePlanAsync(mbom);
        Assert.Equal("A", plan.Summary.Revision);
        Assert.Equal("A", plan.Summary.BoundMbomRevision);
        Assert.Equal(Staleness.Current, plan.Summary.Stale);
        Assert.Equal(PlanStates.InWork, plan.Summary.State);
        Assert.True(plan.Summary.IsOpen);
        Assert.Equal(mbom, plan.Summary.MbomNumber);
        Assert.Equal(p.Top, plan.Summary.PartNumber);

        var second = await env.Plans.CreateAsync(ProcEnv.Alice, new CreatePlanCommand(mbom, null), "c");
        Assert.Equal(OutcomeKind.Conflict, second.Kind);
    }

    [Fact]
    public async Task Editing_check_in_and_the_immutability_of_a_checked_in_iteration()
    {
        var env = await ProcEnv.CreateAsync(pg);
        var (p, mbom) = await env.ReleasedMbomAsync();
        await env.WorkCenterAsync("WC-10");
        var plan = await env.CreatePlanAsync(mbom);
        var number = plan.Summary.Number;
        var screw = await env.MasterIdAsync(p.Screw);
        var tool = await env.NewToolAsync();
        var wc = await env.WorkingAsync(number);

        var op10 = ProcEnv.Op(10, "Fasten plate", "WC-10", [new ComponentAssignment(screw, 10, 0, null)], [Guid.Parse(await env.M.TextAsync("SELECT id FROM proc.tool LIMIT 1"))],
            [ProcEnv.Step("Torque the screws", "step"), ProcEnv.Step("Wear gloves", "warning")]);
        var detail = await env.SaveAsync(number, ProcEnv.With(wc, op10));
        Assert.Single(detail.Routings[0].Operations);
        var view = detail.Routings[0].Operations[0];
        Assert.Equal("Center WC-10", view.WorkCenterName);
        Assert.Equal(p.Screw, view.Components[0].PartNumber);
        Assert.Equal(tool.Summary.Number, view.Tools[0].Number);
        Assert.Contains(detail.Findings, f => f.Code == "line.unassigned"); // plate and sub are not consumed anywhere

        // Another reader sees the last checked-in iteration, not the owner's working copy.
        var other = await env.GetAsync(number, ConfigEnv.Bob);
        Assert.Empty(other.Routings[0].Operations);

        var checkedIn = await env.CheckInAsync(number);
        Assert.False(checkedIn.Summary.IsOpen);
        Assert.Single((await env.GetAsync(number, ConfigEnv.Bob)).Routings[0].Operations);

        // The stored iteration cannot be changed, not even through SQL as the application role.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => env.AppSqlAsync("UPDATE proc.plan_iteration SET content = '{}'::jsonb WHERE NOT is_open"));
        Assert.Contains("immutable", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Check-out opens iteration 2 with the same content; undo returns to 1.
        var again = ProcEnv.Ok(await env.Plans.CheckOutAsync(ProcEnv.Alice, number, "c"));
        Assert.Equal("A.2", again.Summary.IterationLabel);
        Assert.Single((await env.WorkingAsync(number)).Routings[0].Operations);
        var undone = ProcEnv.Ok(await env.Plans.UndoCheckOutAsync(ProcEnv.Alice, number, "c"));
        Assert.Equal("A.1", undone.Summary.IterationLabel);
    }

    [Fact]
    public async Task Structure_and_coverage_rules_are_enforced()
    {
        var env = await ProcEnv.CreateAsync(pg);
        var (p, mbom) = await env.ReleasedMbomAsync();
        await env.WorkCenterAsync("WC-10");
        var number = (await env.CreatePlanAsync(mbom)).Summary.Number;
        var screw = await env.MasterIdAsync(p.Screw);
        var wc = await env.WorkingAsync(number);

        // Unknown work center, duplicate operation numbers, an inspection point without text, a missing caption.
        var bad = ProcEnv.With(wc, ProcEnv.Op(10, "A", "NOPE"), ProcEnv.Op(10, "B") with { Inspection = true });
        var refused = await env.Plans.SaveWorkingCopyAsync(ProcEnv.Alice, number, bad, "c");
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        var text = string.Join(" ", refused.Errors);
        Assert.Contains("not an active work center", text, StringComparison.Ordinal);
        Assert.Contains("used twice", text, StringComparison.Ordinal);
        Assert.Contains("needs a text", text, StringComparison.Ordinal);

        // Over-assigning a line is an error finding; an assignment to a line that does not exist is refused at save.
        var over = ProcEnv.With(wc, ProcEnv.Op(10, "Fasten", "WC-10", [new ComponentAssignment(screw, 10, 0, 3m)]), ProcEnv.Op(20, "More", "WC-10", [new ComponentAssignment(screw, 10, 0, 3m)]));
        var saved = await env.SaveAsync(number, over);
        Assert.Contains(saved.Findings, f => f.Code == "line.over" && f.Severity == "error");
        var orphan = ProcEnv.With(wc, ProcEnv.Op(10, "Fasten", "WC-10", [new ComponentAssignment(screw, 99, 0, null)]));
        var no = await env.Plans.SaveWorkingCopyAsync(ProcEnv.Alice, number, orphan, "c");
        Assert.Equal(OutcomeKind.Invalid, no.Kind);
    }

    [Fact]
    public async Task Release_goes_through_the_workflow_locks_the_revision_and_supersedes_on_revise()
    {
        var env = await ProcEnv.CreateAsync(pg);
        var (p, mbom) = await env.ReleasedMbomAsync();
        await env.WorkCenterAsync("WC-10");
        var number = (await env.CreatePlanAsync(mbom)).Summary.Number;
        var wc = await env.WorkingAsync(number);
        var comps = new List<ComponentAssignment>();
        var lines = (await env.M.LinesAsync(mbom, revision: "A")).Lines;
        foreach (var l in lines) comps.Add(new ComponentAssignment(await env.MasterIdAsync(l.ChildNumber!), l.FindNumber, l.Alt, null));
        await env.SaveAsync(number, ProcEnv.With(wc, ProcEnv.Op(10, "Assemble", "WC-10", comps, null, [ProcEnv.Step("Assemble everything")])));

        // Still checked out: cannot be submitted.
        var open = await env.SubmitAsync(number);
        Assert.False(open.IsOk);
        await env.CheckInAsync(number);

        await env.ReleaseAsync(number);
        var released = await env.GetAsync(number);
        Assert.Equal(PlanStates.Released, released.Summary.State);

        // The release lock is in the database: no new iteration, no content edit, no state change except to Obsolete.
        await Assert.ThrowsAnyAsync<Exception>(() => env.AppSqlAsync("UPDATE proc.plan_iteration SET content = content WHERE revision_id IN (SELECT id FROM proc.plan_revision WHERE lifecycle_state = 'Released')"));
        await Assert.ThrowsAnyAsync<Exception>(() => env.AppSqlAsync("UPDATE proc.plan_revision SET lifecycle_state = 'InWork' WHERE lifecycle_state = 'Released'"));
        var checkout = await env.Plans.CheckOutAsync(ProcEnv.Alice, number, "c");
        Assert.Equal(OutcomeKind.Conflict, checkout.Kind);

        // Signatures cover the stored content: altering it invalidates verification.
        var instance = (await env.Flow.StatusForObjectAsync(ProcEnv.Alice, ProcEnv.ProcessStore_ObjectType, number, "c")).Value!.Last!;
        Assert.All((await env.Flow.VerifyInstanceAsync(ProcEnv.Alice, instance.Id, "c")).Value!, v => Assert.True(v.Valid));
        await env.M.Db.ExecuteAsSuperuserAsync("SET session_replication_role = replica; UPDATE proc.plan_iteration SET content = jsonb_set(content, '{routings,0,name}', '\"Tampered\"') WHERE revision_id IN (SELECT id FROM proc.plan_revision WHERE lifecycle_state = 'Released')");
        Assert.All((await env.Flow.VerifyInstanceAsync(ProcEnv.Alice, instance.Id, "c")).Value!, v => Assert.False(v.Valid));
        await env.M.Db.ExecuteAsSuperuserAsync("SET session_replication_role = replica; UPDATE proc.plan_iteration SET content = jsonb_set(content, '{routings,0,name}', '\"Primary routing\"') WHERE revision_id IN (SELECT id FROM proc.plan_revision WHERE lifecycle_state = 'Released')");

        var revised = ProcEnv.Ok(await env.Plans.ReviseAsync(ProcEnv.Alice, number, "c"));
        Assert.Equal("B", revised.Summary.Revision);
        Assert.Equal(PlanStates.InWork, revised.Summary.State);
        Assert.Equal(PlanStates.Released, (await env.GetAsync(number, revision: "A")).Summary.State);
        await env.CheckInAsync(number);
        await env.ReleaseAsync(number);
        Assert.Equal(PlanStates.Obsolete, (await env.GetAsync(number, revision: "A")).Summary.State);
        Assert.Equal(PlanStates.Released, (await env.GetAsync(number, revision: "B")).Summary.State);
        _ = p;
    }

    [Fact]
    public async Task A_new_released_mbom_revision_makes_the_plan_stale_and_blocks_release_until_rebound()
    {
        var env = await ProcEnv.CreateAsync(pg);
        var (p, mbom) = await env.ReleasedMbomAsync();
        await env.WorkCenterAsync("WC-10");
        var number = (await env.CreatePlanAsync(mbom)).Summary.Number;
        await env.CheckInAsync(number);
        await env.ReleaseAsync(number);
        Assert.Equal(Staleness.Current, (await env.GetAsync(number)).Summary.Stale);

        // The MBOM gets a revision B: edit and release it.
        var revise = await env.M.Mbom.ReviseAsync(ProcEnv.Alice, mbom, "new revision", "c");
        Assert.True(revise.IsOk, string.Join("; ", revise.Errors));
        await env.M.CheckOutAsync(mbom);
        await env.M.UpdateLineAsync(mbom, 10, c => c with { Notes = "revision B" });
        await env.M.CheckInAsync(mbom);
        await env.M.ReconcileAsync(mbom);
        await env.M.ReleaseAsync(mbom);

        var stale = await env.GetAsync(number);
        Assert.Equal(Staleness.Stale, stale.Summary.Stale);
        Assert.Equal(PlanStates.Released, stale.Summary.State); // flagged, never changed
        Assert.Contains(stale.Findings, f => f.Code == "stale_mbom");
        Assert.Single((await env.Plans.SearchAsync(ProcEnv.Alice, new PlanSearch(StaleOnly: true), "c")).Value!.Items);

        ProcEnv.Ok(await env.Plans.ReviseAsync(ProcEnv.Alice, number, "c"));
        Assert.Equal("B", (await env.GetAsync(number)).Summary.BoundMbomRevision); // a revision is written against the revision released now
        _ = p;
    }

    [Fact]
    public async Task Rebind_moves_an_in_work_plan_to_the_released_mbom_revision()
    {
        var env = await ProcEnv.CreateAsync(pg);
        var (_, mbom) = await env.ReleasedMbomAsync();
        var number = (await env.CreatePlanAsync(mbom)).Summary.Number;
        await env.CheckInAsync(number);
        ProcEnv.Ok(await env.Plans.CheckOutAsync(ProcEnv.Alice, number, "c"));
        ProcEnv.Ok(await env.Plans.CheckInAsync(ProcEnv.Alice, number, null, "c"));
        Assert.True(await env.Mbom_ReviseAndReleaseAsync(mbom));
        var stale = await env.GetAsync(number);
        Assert.Equal(Staleness.Stale, stale.Summary.Stale);
        var blocked = await env.SubmitAsync(number);
        Assert.False(blocked.IsOk);
        Assert.Contains("rebind", string.Join(" ", blocked.Errors), StringComparison.OrdinalIgnoreCase);
        ProcEnv.Ok(await env.Plans.CheckOutAsync(ProcEnv.Alice, number, "c"));
        var rebound = ProcEnv.Ok(await env.Plans.RebindAsync(ProcEnv.Alice, number, "c"));
        Assert.Equal("B", rebound.Summary.BoundMbomRevision);
        Assert.Equal(Staleness.Current, rebound.Summary.Stale);
    }
}
