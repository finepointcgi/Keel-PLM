using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Compliance;
using Keel.Core;
using Keel.Parts;
using Keel.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>A document lookup for tests: the numbers it knows and their markings.</summary>
internal sealed class FakeDocumentAccess : IObjectAccess
{
    public Dictionary<string, Markings> Documents { get; } = [];
    public string Kind => AwarenessKinds.Document;
    public Task<ObjectInfo?> FindAsync(string number, CancellationToken ct) =>
        Task.FromResult(Documents.TryGetValue(number, out var m) ? new ObjectInfo(AwarenessKinds.Document, number, "Doc " + number, "Released", m) : null);
}

/// <summary>Real services of the compliance module over a fresh Postgres database, with the review engine wired to the part and regulatory list gateways. No mocks.</summary>
internal sealed class ComplianceEnv
{
    public required TestDatabase Db { get; init; }
    public required NpgsqlDataSource Ds { get; init; }
    public required PartService Parts { get; init; }
    public required WorkflowService Flow { get; init; }
    public required UserDirectory Directory { get; init; }
    public required RegulatoryCatalogService Catalog { get; init; }
    public required DeclarationService Declarations { get; init; }
    public required ComplianceReportService Reports { get; init; }
    public required DeclarationExpiryService Expiry { get; init; }
    public required TestClock Clock { get; init; }
    public required RecordingSink Sink { get; init; }
    public required FakeDocumentAccess Docs { get; init; }

    public static Subject Alice => WorkflowEnv.Alice;
    public static Subject Rita => WorkflowEnv.Rita;
    public static Subject Bob => WorkflowEnv.Bob;
    public static Subject Carol => WorkflowEnv.Carol;
    public static Subject Ada => WorkflowEnv.Ada;
    public static Subject Mallory => WorkflowEnv.Mallory;
    public static Subject Audrey => WorkflowEnv.Audrey;
    public static readonly Subject Eve = WorkflowEnv.User("eve", [Roles.Viewer, Roles.Author], programs: []);
    public static readonly Subject Vic = WorkflowEnv.User("vic", [Roles.Viewer], programs: [], cui: []);
    public static Markings Open => WorkflowEnv.Open;
    public static Markings Itar() => WorkflowEnv.Itar();

    public const string Lead = "7439-92-1";
    public const string Mercury = "7439-97-6";
    public const string Cadmium = "7440-43-9";
    public const string Gold = "7440-57-5";
    public const string Tin = "7440-31-5";

    public static async Task<ComplianceEnv> CreateAsync(PostgresFixture pg)
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var clock = new TestClock();
        var audit = new PostgresAuditLog(ds, clock);
        var sink = new RecordingSink();
        var docs = new FakeDocumentAccess();
        var options = new WorkflowOptions();
        var env = new ComplianceEnv
        {
            Db = db, Ds = ds, Clock = clock, Sink = sink, Docs = docs,
            Parts = new PartService(ds, audit, clock),
            Flow = new WorkflowService(ds, audit, [new PartWorkflowGateway(), new ComplianceListWorkflowGateway(audit, clock, sink)], options, clock),
            Directory = new UserDirectory(ds, clock, options, NullLogger<UserDirectory>.Instance),
            Catalog = new RegulatoryCatalogService(ds, audit, clock),
            Declarations = new DeclarationService(ds, audit, clock, sink, [docs]),
            Reports = new ComplianceReportService(ds, audit, clock),
            Expiry = new DeclarationExpiryService(ds, audit, clock, sink),
        };
        foreach (var u in new[] { Alice, Rita, Bob, Carol, Ada, Mallory, Audrey, Eve, Vic }) await env.Directory.UpsertAsync(u);
        return env;
    }

    public DateOnly Today => DateOnly.FromDateTime(Clock.GetUtcNow().UtcDateTime);

    public async Task<string> NewPartAsync(string name = "Bracket, sensor", Markings? markings = null, Subject? author = null)
    {
        var part = await Parts.CreateAsync(author ?? Alice, new CreatePartCommand(name, "Component", "EA", "Buy", markings ?? Open, "Synthetic bracket", new JsonObject { ["material"] = "6061-T6" }), null);
        Assert.True(part.IsOk, string.Join("; ", part.Errors));
        return part.Value!.Number;
    }

    public static DeclarationLineInput Sub(string cas, decimal percent, string? exemption = null, bool? conflictFree = null) => new(cas, null, percent, null, exemption, conflictFree);

    public DeclarationInput Input(IReadOnlyList<DeclarationLineInput> lines, string source = DeclarationSources.Internal, string? supplierRef = null, int validDays = 365, string? scope = null, decimal? mass = null) =>
        new(scope, source, supplierRef, null, mass, Today, Today.AddDays(validDays), lines, "note text");

    public async Task<DeclarationDetail> DeclareAsync(string part, IReadOnlyList<DeclarationLineInput> lines, Subject? by = null, string source = DeclarationSources.Internal, string? supplierRef = null, int validDays = 365, string? scope = null)
    {
        var r = await Declarations.CreateAsync(by ?? Alice, new CreateDeclarationCommand(part, Input(lines, source, supplierRef, validDays, scope)), null);
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        return r.Value!;
    }

    /// <summary>Publishes a draft of a list through the review engine: reviewer rita, approvers bob and carol sign.</summary>
    public async Task PublishAsync(string code, int version)
    {
        var key = $"{code}-{version}";
        var started = await Flow.StartAsync(Alice, new StartReviewCommand("reg_list_version", key, new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob", "carol"] }), "corr-start");
        Assert.True(started.IsOk, string.Join("; ", started.Errors));
        var v = started.Value!;
        v = await SignAsync(Rita, v.Tasks.Single(t => t.Status == "Open").Id, "Reviewed");
        v = await SignAsync(Bob, WorkflowEnv.TaskOf(v, "bob"), "Approved");
        v = await SignAsync(Carol, WorkflowEnv.TaskOf(v, "carol"), "Approved");
        Assert.Equal("Approved", v.Status);
    }

    public async Task<InstanceView> SignAsync(Subject who, Guid taskId, string code)
    {
        var r = await Flow.SignAsync(WorkflowEnv.Fresh(who), taskId, code, null, "corr-sign");
        Assert.True(r.IsOk, $"{who.Username} signing as {code}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    /// <summary>Drafts the next version of a list, applies the edit to the copied entries and publishes it.</summary>
    public async Task<int> NewVersionAsync(string code, Func<List<RegEntryInput>, List<RegExemptionInput>, DateOnly> edit)
    {
        var detail = (await Catalog.GetListAsync(Ada, code, null)).Value!;
        var current = detail.Versions.First(v => v.Status == VersionStates.Published);
        var draft = await Catalog.CreateDraftAsync(Ada, code, Today, "test", null);
        Assert.True(draft.IsOk, string.Join("; ", draft.Errors));
        var content = (await Catalog.GetVersionAsync(Ada, code, draft.Value!.Version, null)).Value!;
        var entries = content.Entries.Select(e => new RegEntryInput(e.CasNumber, e.ThresholdPct, e.Note)).ToList();
        var exemptions = content.Exemptions.Select(x => new RegExemptionInput(x.Code, x.Description, x.CasNumber, x.ExpiresOn)).ToList();
        var from = edit(entries, exemptions);
        var saved = await Catalog.SaveDraftAsync(Ada, code, draft.Value.Version, from, "test", entries, exemptions, draft.Value.Revision, null);
        Assert.True(saved.IsOk, string.Join("; ", saved.Errors));
        await PublishAsync(code, draft.Value.Version);
        _ = current;
        return draft.Value.Version;
    }

    public async Task<List<AuditRecord>> AuditAsync() => [.. await new AuditQueries(Ds).ListAsync(new AuditQuery(Limit: 5000))];

    public async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static StructureLine Line(string path, string number, bool hasChildren, string? revision = "A", decimal qty = 1, int level = 1, string name = "") =>
        new(level, 10, path, number, name.Length == 0 ? number : name, revision, qty, "EA", hasChildren);

    public static ReportRequest Request(string root, IReadOnlyList<StructureLine> lines, IReadOnlyDictionary<string, int>? versions = null, DateOnly? asOf = null) =>
        new("ebom", root, "Root", "UNCLASSIFIED · DISTRIBUTION A", lines, versions, asOf);
}
