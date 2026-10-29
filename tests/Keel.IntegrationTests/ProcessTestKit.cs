using Keel.Audit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Core.Viewing;
using Keel.Parts;
using Keel.Process;
using Keel.Workflow;

namespace Keel.IntegrationTests;

/// <summary>Real services for the process tests: the MBOM environment plus the plan service, tooling and a review workflow that knows plans. No mocks.</summary>
internal sealed class ProcEnv
{
    public const string Plant = MbomEnv.Plant;
    public required MbomEnv M { get; init; }
    public required ProcessPlanService Plans { get; init; }
    public required ToolingService Tools { get; init; }
    public required WorkflowService Flow { get; init; }
    public required IMbomProcessSource Source { get; init; }
    public required CalibrationExpiryService Expiry { get; init; }

    public static Subject Alice => ConfigEnv.Alice;
    public static Subject Eve => ConfigEnv.Eve;
    public static Subject Ada => ConfigEnv.Ada;
    public static Subject Bob => ConfigEnv.Bob;
    public TestClock Clock => M.C.Clock;

    public static async Task<ProcEnv> CreateAsync(PostgresFixture pg, IViewableFileSource? files = null)
    {
        var m = await MbomEnv.CreateAsync(pg);
        var c = m.C;
        var audit = new PostgresAuditLog(c.Ds, c.Clock);
        var source = new MbomProcessSource(c.Ds);
        var plans = new ProcessPlanService(c.Ds, audit, c.Clock, source, files);
        var gateway = new ProcessWorkflowGateway(audit, plans, source);
        var flow = new WorkflowService(c.Ds, audit, [new PartWorkflowGateway([new ChangeBindingGuard()], null, c.Sink), new MbomWorkflowGateway(audit, c.Sink), gateway], new WorkflowOptions(), c.Clock);
        return new ProcEnv
        {
            M = m, Plans = plans, Tools = new ToolingService(c.Ds, audit, c.Clock, source), Flow = flow, Source = source, Expiry = new CalibrationExpiryService(c.Ds, audit, c.Clock),
        };
    }

    public static T Ok<T>(Outcome<T> o, string what = "")
    {
        Assert.True(o.IsOk, $"{what}: {o.Kind} {string.Join("; ", o.Errors)}");
        return o.Value!;
    }

    /// <summary>A product with a Released MBOM revision A in the plant (lines: 10 screw x4, 20 plate x1, 30 sub x2).</summary>
    public async Task<(MbomEnv.Product Product, string Mbom)> ReleasedMbomAsync()
    {
        var (p, mbom) = await M.ReconciledProductAsync();
        await M.ReleaseAsync(mbom);
        return (p, mbom);
    }

    /// <summary>Revises the MBOM, changes a quantity and releases revision B.</summary>
    public async Task<bool> Mbom_ReviseAndReleaseAsync(string mbom)
    {
        Ok(await M.Mbom.ReviseAsync(Alice, mbom, "new revision", "c"));
        await M.CheckOutAsync(mbom);
        await M.UpdateLineAsync(mbom, 10, c => c with { Notes = "revision B" });
        await M.CheckInAsync(mbom);
        await M.ReconcileAsync(mbom);
        await M.ReleaseAsync(mbom);
        return true;
    }

    /// <summary>Runs SQL as the application role (the least-privilege role the services use).</summary>
    public async Task AppSqlAsync(string sql) { await using var cmd = M.C.Ds.CreateCommand(sql); await cmd.ExecuteNonQueryAsync(); }

    public async Task WorkCenterAsync(string code, string plant = Plant, string kind = "work_center") =>
        Ok(await Tools.SaveWorkCenterAsync(Ada, new SaveWorkCenterCommand(plant, code, "Center " + code, kind, true), null), "work center " + code);

    public async Task<PlanDetail> CreatePlanAsync(string mbom, Subject? by = null, Markings? markings = null) =>
        Ok(await Plans.CreateAsync(by ?? Alice, new CreatePlanCommand(mbom, "Synthetic plan", markings), "corr-plan"), "create plan");

    public async Task<ToolDetail> NewToolAsync(string name = "Torque wrench", string kind = "gauge", bool calibrated = true, int interval = 90, DateOnly? last = null, Markings? markings = null, Subject? by = null) =>
        Ok(await Tools.CreateAsync(by ?? Alice, new CreateToolCommand(name, kind, Plant, null, calibrated, calibrated ? interval : null, last, markings ?? ConfigEnv.Open), "corr-tool"), "create tool");

    public async Task<PlanContent> WorkingAsync(string plan, Subject? by = null) => Ok(await Plans.GetWorkingCopyAsync(by ?? Alice, plan, "corr-wc"), "working copy");

    public async Task<PlanDetail> SaveAsync(string plan, PlanContent content, Subject? by = null) => Ok(await Plans.SaveWorkingCopyAsync(by ?? Alice, plan, content, "corr-save"), "save");

    public async Task<PlanDetail> CheckInAsync(string plan, Subject? by = null) => Ok(await Plans.CheckInAsync(by ?? Alice, plan, "edit", "corr-ci"), "check-in");

    public async Task<PlanDetail> GetAsync(string plan, Subject? by = null, string? revision = null) => Ok(await Plans.GetAsync(by ?? Alice, plan, revision, "corr-get"), "get");

    public static OperationDef Op(int number, string name, string? workCenter = null, IReadOnlyList<ComponentAssignment>? components = null, IReadOnlyList<Guid>? tools = null, IReadOnlyList<StepDef>? steps = null) =>
        new(Guid.NewGuid(), number, name, workCenter, 5m, 1.5m, null, false, null, components ?? [], tools ?? [], steps ?? []);

    public static StepDef Step(string text, string kind = "step", IReadOnlyList<Guid>? tools = null) => new(Guid.NewGuid(), kind, text, tools ?? [], []);

    public static PlanContent With(PlanContent c, params OperationDef[] ops) =>
        new([c.Routings[0] with { Operations = ops }, .. c.Routings.Skip(1)]);

    /// <summary>The part master id of a part number (assignments name the component by master id).</summary>
    public Task<Guid> MasterIdAsync(string part) => M.C.Ds.CreateCommand("SELECT id FROM plm.part_master WHERE number = '" + part + "'").ExecuteScalarAsync().ContinueWith(t => (Guid)t.Result!);

    /// <summary>Submits for review (reviewer rita, approvers bob and carol).</summary>
    public Task<Outcome<InstanceView>> SubmitAsync(string plan, Subject? by = null) =>
        Flow.StartAsync(by ?? Alice, new StartReviewCommand(ProcessStore_ObjectType, plan, new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob", "carol"] }), "corr-submit");

    public const string ProcessStore_ObjectType = ProcessPlanService.ObjectType;

    public async Task<InstanceView> SignAllAsync(InstanceView started)
    {
        var v = started;
        foreach (var user in new[] { ConfigEnv.Rita, ConfigEnv.Bob, ConfigEnv.Carol })
            while (v.Status == "Active" && v.Tasks.Any(t => t.Status == "Open" && t.AssigneeUsername == user.Username))
            {
                var task = v.Tasks.First(t => t.Status == "Open" && t.AssigneeUsername == user.Username);
                var r = await Flow.SignAsync(WorkflowEnv.Fresh(user), task.Id, task.Kind == "review" ? "Reviewed" : "Approved", null, "corr-sign");
                Assert.True(r.IsOk, $"{user.Username}: {r.Kind} {string.Join("; ", r.Errors)}");
                v = r.Value!;
            }
        return v;
    }

    public async Task ReleaseAsync(string plan)
    {
        var started = await SubmitAsync(plan);
        Assert.True(started.IsOk, $"submit {plan}: {started.Kind} {string.Join("; ", started.Errors)}");
        var done = await SignAllAsync(started.Value!);
        Assert.Equal("Approved", done.Status);
    }
}
