using System.Text;
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
/// Real services of every module over a fresh Postgres database (no mocks), wired exactly like the host: the part, change and workflow
/// gateways, the BOM and document providers for impact analysis, and the synthetic users of the workflow tests.
/// </summary>
internal sealed class ChangeEnv
{
    public required TestDatabase Db { get; init; }
    public required NpgsqlDataSource Ds { get; init; }
    public required PartService Parts { get; init; }
    public required BomService Bom { get; init; }
    public required DocumentService? Docs { get; init; }
    public required ChangeService Change { get; init; }
    public required ImpactAnalysisService Impact { get; init; }
    public required WorkflowService Flow { get; init; }
    public required UserDirectory Directory { get; init; }
    public required TestClock Clock { get; init; }

    public static Subject Alice => WorkflowEnv.Alice;
    public static Subject Rita => WorkflowEnv.Rita;
    public static Subject Bob => WorkflowEnv.Bob;
    public static Subject Carol => WorkflowEnv.Carol;
    public static Subject Dave => WorkflowEnv.Dave;
    public static Subject Ada => WorkflowEnv.Ada;
    public static Subject Lena => WorkflowEnv.Lena;
    public static Subject Mallory => WorkflowEnv.Mallory;
    public static Subject Audrey => WorkflowEnv.Audrey;

    /// <summary>An author who is not on program ORCA, so ORCA data is invisible to her (the restricted user of the tests).</summary>
    public static readonly Subject Eve = WorkflowEnv.User("eve", [Roles.Viewer, Roles.Author], programs: []);
    /// <summary>An author on a second program, KRAKEN, in addition to ORCA.</summary>
    public static readonly Subject Kim = WorkflowEnv.User("kim", [Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]);

    public static Markings Itar(string program = "ORCA") => WorkflowEnv.Itar(program);
    public static Markings Open => WorkflowEnv.Open;

    public static async Task<ChangeEnv> CreateAsync(PostgresFixture pg, bool documents = false)
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var clock = new TestClock();
        var audit = new PostgresAuditLog(ds, clock);
        var options = new WorkflowOptions();
        var participants = new IPartIterationParticipant[] { new BomParticipant() };
        var partChange = new PartChangeGateway(participants);
        var bom = new BomService(ds, audit, clock);
        var docs = documents ? new DocumentService(ds, audit, clock, new ObjectVault(await VaultContainer.OptionsAsync())) : null;
        var structure = new IStructureImpactProvider[] { new BomStructureImpactProvider(bom, ds) };
        ILinkedDocumentProvider[] links = docs is null ? [] : [new LinkedDocumentProvider(docs)];
        var env = new ChangeEnv
        {
            Db = db, Ds = ds, Clock = clock, Bom = bom, Docs = docs,
            Parts = new PartService(ds, audit, clock, participants),
            Change = new ChangeService(ds, audit, [partChange], clock),
            Impact = new ImpactAnalysisService(ds, audit, [partChange], structure, links),
            Flow = new WorkflowService(ds, audit,
                [new PartWorkflowGateway([new ChangeBindingGuard()]), new ChangeWorkflowGateway(ChangeTypes.Ecr, audit, [partChange]), new ChangeWorkflowGateway(ChangeTypes.Ecn, audit, [partChange])],
                options, clock),
            Directory = new UserDirectory(ds, clock, options, NullLogger<UserDirectory>.Instance),
        };
        foreach (var u in new[] { Alice, Rita, Bob, Carol, Dave, Lena, Ada, Mallory, Audrey, Eve, Kim, WorkflowEnv.Farid }) await env.Directory.UpsertAsync(u);
        return env;
    }

    // ---------------------------------------------------------------- parts

    public async Task<string> NewPartAsync(string name = "Bracket, sensor", Markings? markings = null, Subject? author = null, string type = "Component", string description = "Synthetic bracket")
    {
        var part = await Parts.CreateAsync(author ?? Alice, new CreatePartCommand(name, type, "EA", "Make", markings ?? Itar(), description,
            new JsonObject { ["material"] = "6061-T6", ["mass"] = 0.42 }), null);
        Assert.True(part.IsOk, string.Join("; ", part.Errors));
        return part.Value!.Number;
    }

    /// <summary>Creates a part and releases revision A through the part review workflow (reviewer rita, approvers bob and carol).</summary>
    public async Task<string> ReleasedPartAsync(string name = "Bracket, sensor", Markings? markings = null, Subject? author = null, string type = "Component")
    {
        var number = await NewPartAsync(name, markings, author, type);
        await ReleasePartAsync(number, author ?? Alice);
        return number;
    }

    public async Task ReleasePartAsync(string number, Subject? submitter = null)
    {
        var started = await Flow.StartAsync(submitter ?? Alice, WorkflowEnv.Start(number, "rita", "bob", "carol"), "corr-part-start");
        Assert.True(started.IsOk, string.Join("; ", started.Errors));
        var v = started.Value!;
        v = await SignAsync(Rita, v.Tasks.Single(t => t.Status == "Open").Id, "Reviewed");
        v = await SignAsync(Bob, WorkflowEnv.TaskOf(v, "bob"), "Approved");
        v = await SignAsync(Carol, WorkflowEnv.TaskOf(v, "carol"), "Approved");
        Assert.Equal("Approved", v.Status);
    }

    public async Task CheckedInEditAsync(Subject by, string number, string description, string comment = "edit")
    {
        Assert.True((await Parts.CheckOutAsync(by, number, null)).IsOk);
        var detail = (await Parts.GetAsync(by, number, null, null)).Value!;
        Assert.True((await Parts.UpdateWorkingCopyAsync(by, number, description, detail.Checkout!.Attributes, null)).IsOk);
        var r = await Parts.CheckInAsync(by, number, comment, null);
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
    }

    public async Task<PartDetail> PartAsync(string number, string? revision = null, Subject? who = null) =>
        (await Parts.GetAsync(who ?? Alice, number, revision, null)).Value!;

    // ---------------------------------------------------------------- changes

    public async Task<ChangeDetail> NewChangeAsync(Subject by, string type, string title = "Change", Markings? markings = null, string? cls = null, string? source = null, string priority = "Medium")
    {
        var r = await Change.CreateAsync(by, new CreateChangeCommand(type, title, "Synthetic description", priority, "Synthetic reason", markings ?? Itar(), cls, source), "corr-create");
        Assert.True(r.IsOk, $"{type} {title}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public Task<ChangeDetail> NewEcnAsync(Subject? by = null, string title = "Revise bracket", Markings? markings = null) =>
        NewChangeAsync(by ?? Alice, ChangeTypes.Ecn, title, markings, ChangeClasses.II);

    public async Task<ChangeDetail> AddAsync(Subject by, string change, string key, string action = ChangeActions.Revise, bool raise = false, string? revision = null)
    {
        var r = await Change.AddAffectedAsync(by, change, new AddAffectedCommand("part", key, action, revision, raise), "corr-add");
        Assert.True(r.IsOk, $"adding {key} to {change}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public async Task<ChangeDetail> ChangeAsync(string number, Subject? who = null) => (await Change.GetAsync(who ?? Alice, number, null)).Value!;

    public async Task<InstanceView> SubmitAsync(string number, Subject? submitter = null, string objectType = "ecn", string reviewer = "rita", params string[] approvers)
    {
        var participants = objectType == "ecr"
            ? new Dictionary<string, string[]> { ["ccb"] = approvers.Length == 0 ? ["bob", "carol"] : approvers }
            : new Dictionary<string, string[]> { ["reviewer"] = [reviewer], ["approvers"] = approvers.Length == 0 ? ["bob", "carol"] : approvers };
        var started = await Flow.StartAsync(submitter ?? Alice, new StartReviewCommand(objectType, number, participants), "corr-submit");
        Assert.True(started.IsOk, $"submitting {number}: {started.Kind} {string.Join("; ", started.Errors)}");
        return started.Value!;
    }

    public async Task<InstanceView> SignAsync(Subject who, Guid taskId, string code, string? comment = null)
    {
        var r = await Flow.SignAsync(WorkflowEnv.Fresh(who), taskId, code, comment, "corr-sign");
        Assert.True(r.IsOk, $"{who.Username} signing as {code}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    /// <summary>Signs every open task in order (rita reviews, the others approve) until the workflow ends; returns the final view.</summary>
    public async Task<InstanceView> ApproveAllAsync(InstanceView started)
    {
        var v = started;
        foreach (var user in new[] { Rita, Bob, Carol })
            while (v.Status == "Active" && v.Tasks.Any(t => t.Status == "Open" && t.AssigneeUsername == user.Username))
            {
                var task = v.Tasks.First(t => t.Status == "Open" && t.AssigneeUsername == user.Username);
                v = await SignAsync(user, task.Id, task.Kind == "review" ? "Reviewed" : "Approved");
            }
        return v;
    }

    /// <summary>An ECN revising <paramref name="key"/> with the new revision checked in (iteration 2 holds the edit), ready to submit.</summary>
    public async Task<(string Ecn, string Part)> ReadyEcnAsync(string? part = null, string edit = "Revised description")
    {
        part ??= await ReleasedPartAsync();
        var ecn = (await NewEcnAsync()).Number;
        await AddAsync(Alice, ecn, part);
        await CheckedInEditAsync(Alice, part, edit);
        return (ecn, part);
    }

    public async Task<List<AuditRecord>> AuditAsync() => [.. await new AuditQueries(Ds).ListAsync(new AuditQuery(Limit: 5000))];

    /// <summary>Walks the whole audit chain (no checkpoints are written in these tests) and returns the verifier's verdict.</summary>
    public async Task<VerificationResult> VerifyAuditChainAsync()
    {
        await using var conn = new NpgsqlConnection(Db.SuperuserConnectionString);
        await conn.OpenAsync();
        return await AuditVerifier.VerifyAsync(conn, []);
    }

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

    public static FileUpload File(string name) => new(name, "text/plain", new MemoryStream(Encoding.UTF8.GetBytes($"{name} {Guid.NewGuid()}")));
}
