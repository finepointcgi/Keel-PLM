using System.Text;
using Keel.Audit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Documents;
using Keel.Parts;
using Keel.Requirements;
using Keel.Workflow;

namespace Keel.IntegrationTests;

/// <summary>Real services for the requirements tests: everything of <see cref="ConfigEnv"/> plus the requirements service and a review workflow that knows specifications. No mocks.</summary>
internal sealed class ReqEnv
{
    public required ConfigEnv C { get; init; }
    public required RequirementService Req { get; init; }
    public required WorkflowService Flow { get; init; }
    public required ImpactAnalysisService Impact { get; init; }

    public static Subject Alice => ConfigEnv.Alice;
    public static Subject Eve => ConfigEnv.Eve;
    public static Subject Ada => ConfigEnv.Ada;
    public static Subject Bob => ConfigEnv.Bob;
    public static Subject Mallory => ConfigEnv.Mallory;
    public static Markings Open => ConfigEnv.Open;
    public static Markings Itar => ConfigEnv.Itar();

    public static async Task<ReqEnv> CreateAsync(PostgresFixture pg, bool documents = false, INotificationSink? sink = null)
    {
        var c = await ConfigEnv.CreateAsync(pg, documents);
        var options = new WorkflowOptions();
        var req = new RequirementService(c.Ds, c.Audit, c.Clock, sink, new RequirementOptions(), new BaselineMembership(c.Ds));
        return new ReqEnv
        {
            C = c,
            Req = req,
            Impact = new ImpactAnalysisService(c.Ds, c.Audit, [new PartChangeGateway([new BomParticipant()])], [new BomStructureImpactProvider(c.Bom, c.Ds)], [], null, [new RequirementImpactProvider(req)]),
            Flow = new WorkflowService(c.Ds, c.Audit, [new PartWorkflowGateway([new ChangeBindingGuard()], null, c.Sink), new RequirementWorkflowGateway(c.Audit, sink)], options, c.Clock),
        };
    }

    public async Task<string> SpecAsync(string name = "Sensor pod requirements", Markings? markings = null, Subject? by = null)
    {
        var r = await Req.CreateAsync(by ?? Alice, new CreateSpecCommand(name, "ORCA pod", "Synthetic requirement set", markings ?? Open), "corr-spec");
        Assert.True(r.IsOk, $"{r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!.Summary.Number;
    }

    public async Task<int> IterationAsync(string number, Subject? by = null) => (await Req.GetAsync(by ?? Alice, number, null, null)).Value!.Displayed.LatestIteration;

    public static RequirementInput Input(string title, string text = "The sensor shall report depth.", string status = ReqStatuses.Approved, string? parent = null, Markings? markings = null,
        string type = "functional", string priority = "high", string method = "test", bool confirm = false, string? before = null) =>
        new(title, text, type, priority, method, "Because.", status, parent, before, markings, confirm);

    /// <summary>Adds one requirement and returns its key.</summary>
    public async Task<string> AddAsync(string number, RequirementInput input, Subject? by = null)
    {
        var r = await Req.AddAsync(by ?? Alice, number, await IterationAsync(number, by), input, "corr-add");
        Assert.True(r.IsOk, $"add: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!.CreatedKeys.Single();
    }

    public async Task UpdateAsync(string number, string key, RequirementInput input, Subject? by = null)
    {
        var r = await Req.EditAsync(by ?? Alice, number, new EditCommand(await IterationAsync(number, by), null, [new EditOp("update", key, input)]), "corr-upd");
        Assert.True(r.IsOk, $"update: {r.Kind} {string.Join("; ", r.Errors)}");
    }

    public async Task<RequirementSet> SetAsync(string number, Subject? by = null, string? revision = null)
    {
        var r = await Req.GetRequirementsAsync(by ?? Alice, number, revision, "corr-list");
        Assert.True(r.IsOk, $"list: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public async Task LinkAsync(string number, string key, string type, string? target = null, string? targetSpec = null, string? targetKey = null, Subject? by = null)
    {
        var r = await Req.AddLinkAsync(by ?? Alice, number, key, new AddLinkCommand(type, targetSpec, targetKey, target), "corr-link");
        Assert.True(r.IsOk, $"link: {r.Kind} {string.Join("; ", r.Errors)}");
    }

    public static StartReviewCommand Start(string number) => new("reqspec", number, new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob", "carol"] });

    /// <summary>Submits the specification and signs it through (reviewer rita, approvers bob and carol).</summary>
    public async Task ReleaseAsync(string number)
    {
        var started = await Flow.StartAsync(Alice, Start(number), "corr-start");
        Assert.True(started.IsOk, $"submit {number}: {started.Kind} {string.Join("; ", started.Errors)}");
        var v = started.Value!;
        foreach (var user in new[] { ConfigEnv.Rita, ConfigEnv.Bob, ConfigEnv.Carol })
            while (v.Status == "Active" && v.Tasks.Any(t => t.Status == "Open" && t.AssigneeUsername == user.Username))
            {
                var task = v.Tasks.First(t => t.Status == "Open" && t.AssigneeUsername == user.Username);
                var r = await Flow.SignAsync(WorkflowEnv.Fresh(user), task.Id, task.Kind == "review" ? "Reviewed" : "Approved", null, "corr-sign");
                Assert.True(r.IsOk, $"{user.Username}: {r.Kind} {string.Join("; ", r.Errors)}");
                v = r.Value!;
            }
        Assert.Equal("Approved", v.Status);
    }

    public async Task<string> DocumentAsync(string name = "Test report", Markings? markings = null, Subject? by = null)
    {
        var upload = new FileUpload(name + ".txt", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes($"{name} {Guid.NewGuid()}")));
        var r = await C.Docs!.CreateAsync(by ?? Alice, new CreateDocumentCommand(name, "Report", markings ?? Open), upload, null);
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        return r.Value!.Number;
    }

    public async Task<string> AuditJsonAsync() => System.Text.Json.JsonSerializer.Serialize(await C.AuditAsync());
}
