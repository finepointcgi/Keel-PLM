using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Vault;
using Keel.Parts;
using Keel.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Real services of every module that configuration management touches (parts, BOM, baselines, effectivity, change, workflow) over a fresh
/// Postgres database, wired like the host. No mocks.
/// </summary>
internal sealed class ConfigEnv
{
    public required TestDatabase Db { get; init; }
    public required NpgsqlDataSource Ds { get; init; }
    public required PartService Parts { get; init; }
    public required BomService Bom { get; init; }
    public required BaselineService Baselines { get; init; }
    public required EffectivityService Eff { get; init; }
    public required ChangeService Change { get; init; }
    public required WorkflowService Flow { get; init; }
    public required DocumentService? Docs { get; init; }
    public required TestClock Clock { get; init; }

    public static Subject Alice => WorkflowEnv.Alice;
    public static Subject Rita => WorkflowEnv.Rita;
    public static Subject Bob => WorkflowEnv.Bob;
    public static Subject Carol => WorkflowEnv.Carol;
    public static Subject Ada => WorkflowEnv.Ada;
    public static Subject Mallory => WorkflowEnv.Mallory;
    public static Subject Audrey => WorkflowEnv.Audrey;
    /// <summary>An author who is not on program ORCA: ORCA data is invisible to her.</summary>
    public static readonly Subject Eve = WorkflowEnv.User("eve", [Roles.Viewer, Roles.Author], programs: []);
    public static Markings Itar(string program = "ORCA") => WorkflowEnv.Itar(program);
    public static Markings Open => WorkflowEnv.Open;

    public static async Task<ConfigEnv> CreateAsync(PostgresFixture pg, bool documents = false)
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var clock = new TestClock();
        var audit = new PostgresAuditLog(ds, clock);
        var options = new WorkflowOptions();
        var participants = new IPartIterationParticipant[] { new BomParticipant() };
        var partChange = new PartChangeGateway(participants);
        var effectivity = new RevisionEffectivityService();
        var docs = documents ? new DocumentService(ds, audit, clock, new ObjectVault(await VaultContainer.OptionsAsync())) : null;
        var env = new ConfigEnv
        {
            Db = db, Ds = ds, Clock = clock, Docs = docs,
            Parts = new PartService(ds, audit, clock, participants),
            Bom = new BomService(ds, audit, clock),
            Baselines = new BaselineService(ds, audit, clock, documents ? new DocumentPinProvider() : null),
            Eff = new EffectivityService(ds, audit, clock),
            Change = new ChangeService(ds, audit, [partChange], clock, effectivity),
            Flow = new WorkflowService(ds, audit,
                [new PartWorkflowGateway([new ChangeBindingGuard()]), new ChangeWorkflowGateway(ChangeTypes.Ecn, audit, [partChange], effectivity),
                 new ChangeWorkflowGateway(ChangeTypes.Ecr, audit, [partChange], effectivity), new BaselineWorkflowGateway(audit)],
                options, clock),
        };
        var directory = new UserDirectory(ds, clock, options, NullLogger<UserDirectory>.Instance);
        foreach (var u in new[] { Alice, Rita, Bob, Carol, WorkflowEnv.Dave, WorkflowEnv.Lena, Ada, Mallory, Audrey, Eve }) await directory.UpsertAsync(u);
        return env;
    }

    // ---------------------------------------------------------------- parts and structures

    public async Task<string> NewPartAsync(string name = "Bracket", string type = "Component", Markings? markings = null, Subject? by = null, string unit = "EA")
    {
        var part = await Parts.CreateAsync(by ?? Alice, new CreatePartCommand(name, type, unit, "Make", markings ?? Open, "Synthetic " + name,
            new JsonObject { ["material"] = "6061-T6", ["mass"] = 0.42 }), null);
        Assert.True(part.IsOk, string.Join("; ", part.Errors));
        return part.Value!.Number;
    }

    /// <summary>Submits the part and signs it through: reviewer rita, approvers bob and carol. Revision A (or the open revision) becomes Released.</summary>
    public async Task ReleaseAsync(string number, Subject? submitter = null)
    {
        var started = await Flow.StartAsync(submitter ?? Alice, WorkflowEnv.Start(number, "rita", "bob", "carol"), "corr-release");
        Assert.True(started.IsOk, $"releasing {number}: {started.Kind} {string.Join("; ", started.Errors)}");
        var v = await SignAllAsync(started.Value!);
        Assert.Equal("Approved", v.Status);
    }

    public async Task<InstanceView> SignAllAsync(InstanceView started)
    {
        var v = started;
        foreach (var user in new[] { Rita, Bob, Carol })
            while (v.Status == "Active" && v.Tasks.Any(t => t.Status == "Open" && t.AssigneeUsername == user.Username))
            {
                var task = v.Tasks.First(t => t.Status == "Open" && t.AssigneeUsername == user.Username);
                var r = await Flow.SignAsync(WorkflowEnv.Fresh(user), task.Id, task.Kind == "review" ? "Reviewed" : "Approved", null, "corr-sign");
                Assert.True(r.IsOk, $"{user.Username}: {r.Kind} {string.Join("; ", r.Errors)}");
                v = r.Value!;
            }
        return v;
    }

    public async Task<string> ReleasedComponentAsync(string name = "Bracket", Markings? markings = null)
    {
        var n = await NewPartAsync(name, "Component", markings);
        await ReleaseAsync(n);
        return n;
    }

    public async Task CheckOutAsync(string number, Subject? by = null) => Assert.True((await Parts.CheckOutAsync(by ?? Alice, number, null)).IsOk);

    public async Task CheckInAsync(string number, Subject? by = null, string comment = "edit")
    {
        var r = await Parts.CheckInAsync(by ?? Alice, number, comment, null);
        Assert.True(r.IsOk, $"check-in {number}: {r.Kind} {string.Join("; ", r.Errors)}");
    }

    public async Task<WorkingBomLine> AddLineAsync(string parent, string child, decimal qty = 1, int? find = null, EffectivityInput? eff = null, Subject? by = null)
    {
        var r = await Bom.AddUsageAsync(by ?? Alice, parent, new AddUsageCommand(child, qty, null, find, null, null, eff), null);
        Assert.True(r.IsOk, $"adding {child} to {parent}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    /// <summary>A new assembly with the given children, checked in; not released.</summary>
    public async Task<string> AssemblyAsync(string name, params (string Child, decimal Qty)[] children)
    {
        var n = await NewPartAsync(name, "Assembly");
        if (children.Length == 0) return n;
        await CheckOutAsync(n);
        foreach (var (child, qty) in children) await AddLineAsync(n, child, qty);
        await CheckInAsync(n, comment: "structure");
        return n;
    }

    public async Task<string> ReleasedAssemblyAsync(string name, params (string Child, decimal Qty)[] children)
    {
        var n = await AssemblyAsync(name, children);
        await ReleaseAsync(n);
        return n;
    }

    public async Task<BomExplosion> ExplodeAsync(string number, BomConfigSpec spec = BomConfigSpec.LatestReleased, Subject? by = null, EffectivityRequest? unit = null, string? baseline = null, int? depth = null)
    {
        var r = await Bom.ExplodeAsync(by ?? Alice, new BomQuery(number, null, null, spec, depth, null, baseline, unit), "corr-explode");
        Assert.True(r.IsOk, $"explode {number} {spec}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public async Task<ECN> ReviseWithEffectivityAsync(string part, EffectivityInput? effectivity, string edit = "Revised")
    {
        var ecn = await Change.CreateAsync(Alice, new CreateChangeCommand(ChangeTypes.Ecn, "Revise " + part, "Synthetic", "Medium", "Synthetic reason", Open, ChangeClasses.II), "corr-create");
        Assert.True(ecn.IsOk, string.Join("; ", ecn.Errors));
        var added = await Change.AddAffectedAsync(Alice, ecn.Value!.Number, new AddAffectedCommand("part", part, ChangeActions.Revise, null, false, effectivity), "corr-add");
        Assert.True(added.IsOk, $"{added.Kind} {string.Join("; ", added.Errors)}");
        await CheckOutAsync(part);
        var detail = (await Parts.GetAsync(Alice, part, null, null, default)).Value!;
        Assert.True((await Parts.UpdateWorkingCopyAsync(Alice, part, edit, detail.Checkout!.Attributes, null)).IsOk);
        await CheckInAsync(part, comment: edit);
        return new ECN(ecn.Value!.Number, added.Value!.Affected.Single().Id);
    }

    public sealed record ECN(string Number, Guid AffectedId);

    /// <summary>Submits an ECN and signs it through; returns the final workflow view.</summary>
    public async Task<InstanceView> ReleaseEcnAsync(string number)
    {
        var started = await Flow.StartAsync(Alice, new StartReviewCommand("ecn", number,
            new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob", "carol"] }), "corr-submit");
        Assert.True(started.IsOk, $"submitting {number}: {started.Kind} {string.Join("; ", started.Errors)}");
        return await SignAllAsync(started.Value!);
    }

    public async Task<PartDetail> PartAsync(string number, string? revision = null, Subject? who = null) =>
        (await Parts.GetAsync(who ?? Alice, number, revision, null, default)).Value!;

    public async Task<List<AuditRecord>> AuditAsync() => [.. await new AuditQueries(Ds).ListAsync(new AuditQuery(Limit: 5000))];

    public async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<string> TextAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToString(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    public static EffectivityInput Serial(string context, long? from, long? to) => new("serial", context, from?.ToString(System.Globalization.CultureInfo.InvariantCulture), to?.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public async Task NewContextAsync(string id = "ORCA-POD", params (string Name, int Number)[] blocks)
    {
        var r = await Eff.CreateContextAsync(Alice, new CreateContextCommand(id, "Synthetic end item " + id, "", null, [.. blocks.Select(b => new EffectivityBlock(b.Name, b.Number))]), null);
        Assert.True(r.IsOk, $"{r.Kind} {string.Join("; ", r.Errors)}");
    }

    public async Task<BaselineDetail> BaselineAsync(string root, string type = "Design", BomConfigSpec spec = BomConfigSpec.LatestReleased, bool releasedOnly = false,
        Subject? by = null, Markings? markings = null, bool confirm = false, string name = "Synthetic baseline", EffectivityRequest? unit = null, bool docs = false)
    {
        var r = await Baselines.CreateAsync(by ?? Alice, new CreateBaselineCommand(root, name, type, spec, releasedOnly, docs, "Synthetic purpose", "PDR", null, unit, markings, confirm), "corr-bl");
        Assert.True(r.IsOk, $"baseline {root}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }
}
