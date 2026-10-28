using System.Text.Json.Nodes;
using Keel.Admin;
using Keel.Audit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Parts;
using Keel.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Real administration, part, change and workflow services over a fresh database (no mocks), for the 2.2 tests.</summary>
public sealed class AdminEnv
{
    public required TestDatabase Db { get; init; }
    public required NpgsqlDataSource Ds { get; init; }
    public required TestClock Clock { get; init; }
    public required AdminRunner Run { get; init; }
    public required ReferenceDataService Refs { get; init; }
    public required ReferenceAdminService Lists { get; init; }
    public required AttributeAdminService Attributes { get; init; }
    public required LifecycleAdminService Lifecycles { get; init; }
    public required NumberingAdminService Numbering { get; init; }
    public required ReasonCodeAdminService Reasons { get; init; }
    public required WorkflowAdminService Workflows { get; init; }
    public required WorkflowTemplateAdmin TemplateAdmin { get; init; }
    public required ClassificationService Classes { get; init; }
    public required ConfigTransferService Config { get; init; }
    public required ConfigHistoryService History { get; init; }
    public required PartService Parts { get; init; }
    public required ChangeService Change { get; init; }
    public required WorkflowService Flow { get; init; }
    public required UserDirectory Directory { get; init; }

    public IReferenceData RefData => Refs;
    public static Subject Ada => WorkflowEnv.Ada;
    public static Subject Alice => WorkflowEnv.Alice;
    public static Subject Rita => WorkflowEnv.Rita;
    public static Subject Bob => WorkflowEnv.Bob;
    public static Subject Carol => WorkflowEnv.Carol;
    public static Subject Mallory => WorkflowEnv.Mallory;
    /// <summary>An author who is not on program ORCA, so ORCA data is invisible to her.</summary>
    public static readonly Subject Eve = WorkflowEnv.User("eve", [Roles.Viewer, Roles.Author], programs: []);
    public static Markings Itar(string program = "ORCA") => WorkflowEnv.Itar(program);
    public static Markings Open => WorkflowEnv.Open;

    public static async Task<AdminEnv> CreateAsync(PostgresFixture pg)
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var clock = new TestClock();
        var audit = new PostgresAuditLog(ds, clock);
        var options = new WorkflowOptions();
        var participants = new IPartIterationParticipant[] { new BomParticipant(), new ClassificationParticipant() };
        var partChange = new PartChangeGateway(participants);
        IWorkflowObjectGateway[] gateways =
        [
            new PartWorkflowGateway([new ChangeBindingGuard()], participants),
            new ChangeWorkflowGateway(ChangeTypes.Ecr, audit, [partChange]),
            new ChangeWorkflowGateway(ChangeTypes.Ecn, audit, [partChange]),
        ];
        var flow = new WorkflowService(ds, audit, gateways, options, clock);
        var templateAdmin = new WorkflowTemplateAdmin(ds, audit, flow, gateways, clock);
        var run = new AdminRunner(ds, audit, clock);
        var refs = new ReferenceDataService(ds, clock);
        var lists = new ReferenceAdminService(run, refs);
        var attributes = new AttributeAdminService(run);
        var lifecycles = new LifecycleAdminService(run, templateAdmin);
        var numbering = new NumberingAdminService(run);
        var reasons = new ReasonCodeAdminService(run);
        var workflows = new WorkflowAdminService(templateAdmin);
        var classes = new ClassificationService(run, audit);
        var env = new AdminEnv
        {
            Db = db, Ds = ds, Clock = clock, Run = run, Refs = refs, Lists = lists, Attributes = attributes, Lifecycles = lifecycles, Numbering = numbering, Reasons = reasons,
            Workflows = workflows, TemplateAdmin = templateAdmin, Classes = classes, History = new ConfigHistoryService(run, new AuditQueries(ds)),
            Config = new ConfigTransferService(run, audit, attributes, lifecycles, lists, numbering, reasons, workflows, classes),
            Parts = new PartService(ds, audit, clock, participants),
            Change = new ChangeService(ds, audit, [partChange], clock),
            Flow = flow,
            Directory = new UserDirectory(ds, clock, options, NullLogger<UserDirectory>.Instance),
        };
        foreach (var u in new[] { Alice, Rita, Bob, Carol, Ada, Mallory, Eve }) await env.Directory.UpsertAsync(u);
        return env;
    }

    public async Task<string> NewPartAsync(string name = "Bracket, sensor", Markings? markings = null, Subject? author = null, string type = "Component", JsonObject? attributes = null)
    {
        var part = await Parts.CreateAsync(author ?? Alice, new CreatePartCommand(name, type, "EA", "Make", markings ?? Itar(), "Synthetic bracket",
            attributes ?? new JsonObject { ["material"] = "6061-T6", ["mass"] = 0.42 }), null);
        Assert.True(part.IsOk, string.Join("; ", part.Errors));
        return part.Value!.Number;
    }

    /// <summary>Releases revision A through the part review (reviewer rita, approvers bob and carol).</summary>
    public async Task ReleaseAsync(string number)
    {
        var started = await Flow.StartAsync(Alice, WorkflowEnv.Start(number, "rita", "bob", "carol"), "corr-start");
        Assert.True(started.IsOk, $"{started.Kind} {string.Join("; ", started.Errors)}");
        var v = started.Value!;
        foreach (var (who, code) in new[] { (Rita, "Reviewed"), (Bob, "Approved"), (Carol, "Approved") })
        {
            var task = v.Tasks.First(t => t.Status == "Open" && t.AssigneeUsername == who.Username);
            var r = await Flow.SignAsync(WorkflowEnv.Fresh(who), task.Id, code, null, "corr-sign");
            Assert.True(r.IsOk, $"{who.Username}: {r.Kind} {string.Join("; ", r.Errors)}");
            v = r.Value!;
        }
        Assert.Equal("Approved", v.Status);
    }

    public async Task<PartDetail> PartAsync(string number, Subject? who = null) => (await Parts.GetAsync(who ?? Alice, number, null, null)).Value!;

    /// <summary>Classifies a part in its working copy and checks it in.</summary>
    public async Task ClassifyAsync(string number, string? classCode, JsonObject? attributes, Subject? by = null)
    {
        by ??= Alice;
        Assert.True((await Parts.CheckOutAsync(by, number, null)).IsOk);
        var set = await Classes.SetForPartAsync(by, number, classCode, attributes, null);
        Assert.True(set.IsOk, $"{set.Kind} {string.Join("; ", set.Errors)}");
        var r = await Parts.CheckInAsync(by, number, "classified", null);
        Assert.True(r.IsOk, $"{r.Kind} {string.Join("; ", r.Errors)}");
    }

    public static T Ok<T>(Outcome<T> o)
    {
        Assert.True(o.IsOk, $"{o.Kind}: {string.Join("; ", o.Errors)}");
        return o.Value!;
    }

    public static string Errors<T>(Outcome<T> o) => string.Join(" | ", o.Errors);

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

    // ---- small builders
    public static AttributeDefinitionInput Number(string name, string label, string? unit = null, decimal? min = null, decimal? max = null, bool required = false) =>
        new(name, label, "number", unit, Min: min, Max: max, Required: required);

    public static AttributeDefinitionInput Text(string name, string label, string? pattern = null, int? maxLength = null, string? help = null) =>
        new(name, label, "string", MaxLength: maxLength, Pattern: pattern, HelpText: help);

    public static AttributeDefinitionInput Enum(string name, string label, params string[] values) => new(name, label, "enum", EnumValues: values);
}
