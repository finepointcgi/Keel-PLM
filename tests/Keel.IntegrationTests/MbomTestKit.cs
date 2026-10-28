using Keel.Audit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Parts;
using Keel.Workflow;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Real services for the MBOM tests: everything of <see cref="ConfigEnv"/> plus the MBOM service and a review workflow that knows MBOMs. No mocks.</summary>
internal sealed class MbomEnv
{
    public const string Plant = "PLANT-A";
    public required ConfigEnv C { get; init; }
    public required MbomService Mbom { get; init; }
    public required WorkflowService Flow { get; init; }
    public required MbomBomSourceHandle Source { get; init; }
    public required ImpactAnalysisService Impact { get; init; }

    public static Subject Alice => ConfigEnv.Alice;
    public static Subject Eve => ConfigEnv.Eve;
    public static Subject Ada => ConfigEnv.Ada;
    public NpgsqlDataSource Ds => C.Ds;
    public TestDatabase Db => C.Db;

    /// <summary>With an existing <paramref name="config"/> (for example the one of an <see cref="IntegrationEnv"/>) the MBOM gateway and the part gateway report to its outbox sink.</summary>
    public static async Task<MbomEnv> CreateAsync(PostgresFixture pg, ConfigEnv? config = null)
    {
        var c = config ?? await ConfigEnv.CreateAsync(pg);
        var audit = new PostgresAuditLog(c.Ds, c.Clock);
        return new MbomEnv
        {
            C = c,
            Mbom = new MbomService(c.Ds, audit, c.Clock),
            Flow = new WorkflowService(c.Ds, audit, [new PartWorkflowGateway([new ChangeBindingGuard()], null, c.Sink), new MbomWorkflowGateway(audit, c.Sink)], new WorkflowOptions(), c.Clock),
            Source = new MbomBomSourceHandle(new MbomBomSource(c.Ds, audit), new MbomImpactProvider(c.Ds, audit)),
            Impact = new ImpactAnalysisService(c.Ds, audit, [new PartChangeGateway([new BomParticipant()])], [new BomStructureImpactProvider(c.Bom, c.Ds)], [], [new MbomImpactProvider(c.Ds, audit)]),
        };
    }

    public sealed record MbomBomSourceHandle(IManufacturingBomSource Source, IManufacturingImpactProvider Impact);

    // ---------------------------------------------------------------- parts

    public async Task<string> BuyPartAsync(string name, string type = "Component", Markings? markings = null, Subject? by = null, string unit = "EA")
    {
        var r = await C.Parts.CreateAsync(by ?? Alice, new CreatePartCommand(name, type, unit, "Buy", markings ?? ConfigEnv.Open), null);
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        return r.Value!.Number;
    }

    /// <summary>The product of the tests: TOP (Assembly) = 4 SCREW (find 10), 1 PLATE (20), 2 SUB (30); SUB (Assembly) = 1 BRACKET. TOP and SUB are Released.</summary>
    public sealed record Product(string Top, string Sub, string Screw, string Plate, string Bracket);

    public async Task<Product> ProductAsync(Markings? markings = null)
    {
        var screw = await BuyPartAsync("Screw");
        var plate = await C.NewPartAsync("Plate", "Component", markings);
        var bracket = await C.NewPartAsync("Bracket", "Component", markings);
        var sub = await C.NewPartAsync("Subassembly", "Assembly", markings);
        await C.CheckOutAsync(sub);
        await C.AddLineAsync(sub, bracket, 1);
        await C.CheckInAsync(sub);
        await C.ReleaseAsync(sub);
        var top = await C.NewPartAsync("Top assembly", "Assembly", markings);
        await C.CheckOutAsync(top);
        await C.AddLineAsync(top, screw, 4, 10);
        await C.AddLineAsync(top, plate, 1, 20);
        await C.AddLineAsync(top, sub, 2, 30);
        await C.CheckInAsync(top);
        await C.ReleaseAsync(top);
        return new Product(top, sub, screw, plate, bracket);
    }

    /// <summary>
    /// Revises a released assembly through an ECN (revision B, edited by <paramref name="edit"/> inside the check-out) and, when asked, releases the notice so B is the
    /// latest released revision.
    /// </summary>
    public async Task<ConfigEnv.ECN> ReviseAsync(string part, Func<Task> edit, bool release = true)
    {
        var ecn = await C.ReviseWithEffectivityAsync(part, null);
        await C.CheckOutAsync(part);
        await edit();
        await C.CheckInAsync(part, comment: "structure change");
        if (release)
        {
            var done = await C.ReleaseEcnAsync(ecn.Number);
            Assert.Equal("Approved", done.Status);
        }
        return ecn;
    }

    /// <summary>The working-copy EBOM line of a part at a find number.</summary>
    public async Task<WorkingBomLine> EbomLineAsync(string part, int find) => (await C.Bom.GetWorkingAsync(Alice, part, null)).Value!.Lines.Single(l => l.FindNumber == find);

    public async Task SetEbomQuantityAsync(string part, int find, decimal qty)
    {
        var l = await EbomLineAsync(part, find);
        var r = await C.Bom.UpdateUsageAsync(Alice, part, l.Id, new UpdateUsageCommand(qty, l.Unit, l.FindNumber, l.ReferenceDesignators, l.Notes), null);
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
    }

    // ---------------------------------------------------------------- MBOM helpers

    public async Task<MbomDetail> GenerateAsync(string part, string plant = Plant, EbomSpec? spec = null, bool multi = false, Subject? by = null)
    {
        var r = await Mbom.GenerateAsync(by ?? Alice, new GenerateMbomCommand(part, plant, spec, multi), "corr-gen");
        Assert.True(r.IsOk, $"generate {part}: {r.Kind} {string.Join("; ", r.Errors)}");
        return await GetAsync(r.Value!.Items[0].MbomNumber, by);
    }

    public async Task<MbomDetail> GetAsync(string number, Subject? by = null, string? revision = null)
    {
        var r = await Mbom.GetAsync(by ?? Alice, number, revision, "corr-get");
        Assert.True(r.IsOk, $"get {number}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public async Task<MbomLines> LinesAsync(string number, Subject? by = null, string? revision = null, int? iteration = null)
    {
        var r = await Mbom.GetLinesAsync(by ?? Alice, number, revision, iteration, "corr-lines");
        Assert.True(r.IsOk, $"lines {number}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public async Task CheckOutAsync(string number, Subject? by = null)
    {
        var r = await Mbom.CheckOutAsync(by ?? Alice, number, "corr-co");
        Assert.True(r.IsOk, $"check-out {number}: {r.Kind} {string.Join("; ", r.Errors)}");
    }

    public async Task CheckInAsync(string number, Subject? by = null, string? comment = "edit")
    {
        var r = await Mbom.CheckInAsync(by ?? Alice, number, comment, "corr-ci");
        Assert.True(r.IsOk, $"check-in {number}: {r.Kind} {string.Join("; ", r.Errors)}");
    }

    public async Task<MbomLineView> WorkingLineAsync(string number, int find, Subject? by = null, int alt = 0) =>
        (await Mbom.GetWorkingAsync(by ?? Alice, number, null)).Value!.Lines.Single(l => l.FindNumber == find && l.Alt == alt);

    /// <summary>Edits one line of the working copy through the service.</summary>
    public async Task<MbomLineView> UpdateLineAsync(string number, int find, Func<UpdateMbomLineCommand, UpdateMbomLineCommand> change, Subject? by = null)
    {
        var line = await WorkingLineAsync(number, find, by);
        var cmd = new UpdateMbomLineCommand(line.Quantity, line.Unit, line.FindNumber, line.ReferenceDesignators, line.ScrapFactor, line.Phantom, line.SupplyType, line.OperationReference, line.MbomOnly, line.Notes);
        var r = await Mbom.UpdateLineAsync(by ?? Alice, number, line.Id, change(cmd), "corr-upd");
        Assert.True(r.IsOk, $"update line {find}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public async Task<ReconciliationReport> ReconcileAsync(string number, EbomSpec? spec = null, Subject? by = null)
    {
        var r = await Mbom.ReconcileAsync(by ?? Alice, number, spec, "corr-rec");
        Assert.True(r.IsOk, $"reconcile {number}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public static string[] Codes(ReconciliationReport r) => [.. r.Findings.Select(f => f.Code)];

    /// <summary>Submits the MBOM for review (reviewer rita, approvers bob and carol) and returns the view.</summary>
    public Task<Outcome<InstanceView>> SubmitAsync(string number, Subject? by = null) =>
        Flow.StartAsync(by ?? Alice, new StartReviewCommand(MbomService.ObjectType, number,
            new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob", "carol"] }), "corr-submit");

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

    /// <summary>Reconciles (when no run exists), submits and signs through: the MBOM ends Released.</summary>
    public async Task<InstanceView> ReleaseAsync(string number)
    {
        var started = await SubmitAsync(number);
        Assert.True(started.IsOk, $"submit {number}: {started.Kind} {string.Join("; ", started.Errors)}");
        var done = await SignAllAsync(started.Value!);
        Assert.Equal("Approved", done.Status);
        return done;
    }

    public Task<long> ScalarAsync(string sql) => C.ScalarAsync(sql);
    public Task<string> TextAsync(string sql) => C.TextAsync(sql);
    public Task<List<AuditRecord>> AuditAsync() => C.AuditAsync();

    /// <summary>A product whose MBOM was generated and reconciled clean (Reconciled), for the release and query tests.</summary>
    public async Task<(Product Product, string Mbom)> ReconciledProductAsync()
    {
        var p = await ProductAsync();
        var d = await GenerateAsync(p.Top);
        // The Make sub-assembly needs its own released MBOM for the plant.
        var sub = await GenerateAsync(p.Sub);
        await ReconcileAsync(sub.Summary.Number);
        await ReleaseAsync(sub.Summary.Number);
        var r = await ReconcileAsync(d.Summary.Number);
        Assert.Equal(ReconcileStatuses.Reconciled, r.Run.Status);
        return (p, d.Summary.Number);
    }
}
