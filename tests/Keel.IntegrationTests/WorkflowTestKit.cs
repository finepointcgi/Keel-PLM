using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Parts;
using Keel.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>A clock tests can move.</summary>
public sealed class TestClock : TimeProvider
{
    // Runs with real time, so tokens minted "now" stay fresh; Advance shifts it for expiry tests.
    private TimeSpan _shift;
    public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() + _shift;
    public void Advance(TimeSpan by) => _shift += by;
}

/// <summary>Real services over a fresh Postgres database, plus synthetic users, for the workflow tests.</summary>
public sealed class WorkflowEnv
{
    public required TestDatabase Db { get; init; }
    public required NpgsqlDataSource Ds { get; init; }
    public required PartService Parts { get; init; }
    public required WorkflowService Flow { get; init; }
    public required UserDirectory Directory { get; init; }
    public required WorkflowOptions Options { get; init; }
    public required TestClock Clock { get; init; }

    public static async Task<WorkflowEnv> CreateAsync(PostgresFixture pg, bool seedUsers = true)
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var clock = new TestClock();
        var audit = new PostgresAuditLog(ds, clock);
        var options = new WorkflowOptions();
        var env = new WorkflowEnv
        {
            Db = db, Ds = ds, Clock = clock, Options = options,
            Parts = new PartService(ds, audit, clock),
            Flow = new WorkflowService(ds, audit, [new PartWorkflowGateway()], options, clock),
            Directory = new UserDirectory(ds, clock, options, NullLogger<UserDirectory>.Instance),
        };
        if (seedUsers)
            foreach (var u in new[] { Alice, Rita, Bob, Carol, Dave, Lena, Ada, Farid, Mallory, Audrey }) await env.Directory.UpsertAsync(u);
        return env;
    }

    // ---- synthetic users (the same shapes as the dev realm)
    public static Subject User(string name, string[] roles, bool us = true, string org = "ENG", string[]? programs = null, string[]? auths = null, string[]? cui = null) => new()
    {
        Id = "sub-" + name,
        Username = name,
        DisplayName = char.ToUpperInvariant(name[0]) + name[1..] + " Example",
        UsPerson = us,
        Org = org,
        Citizenship = us ? "USA" : "DEU",
        Programs = (programs ?? ["ORCA"]).ToImmutableSortedSet(StringComparer.Ordinal),
        ExportAuthorizations = (auths ?? []).ToImmutableSortedSet(StringComparer.Ordinal),
        CuiCategories = (cui ?? ["EXPT", "CTI"]).ToImmutableSortedSet(StringComparer.Ordinal),
        Roles = roles.ToImmutableSortedSet(StringComparer.Ordinal),
        AuthTime = DateTimeOffset.UtcNow,
    };

    public static readonly Subject Alice = User("alice", [Roles.Viewer, Roles.Author]);
    public static readonly Subject Rita = User("rita", [Roles.Viewer, Roles.Author]);
    public static readonly Subject Bob = User("bob", [Roles.Viewer, Roles.Approver]);
    public static readonly Subject Carol = User("carol", [Roles.Viewer, Roles.Approver]);
    public static readonly Subject Dave = User("dave", [Roles.Viewer, Roles.Approver]);
    public static readonly Subject Lena = User("lena", [Roles.Viewer, Roles.Approver], us: false);
    public static readonly Subject Ada = User("ada", [Roles.Admin, Roles.Viewer]);
    public static readonly Subject Farid = User("farid", [Roles.Viewer, Roles.Approver], us: false, auths: ["TAA-0042"]);
    public static readonly Subject Mallory = User("mallory", [Roles.Viewer]);
    public static readonly Subject Audrey = User("audrey", [Roles.Auditor], programs: []);

    public static Subject Fresh(Subject s, DateTimeOffset? authTime = null) => s with { AuthTime = authTime ?? DateTimeOffset.UtcNow };
    public static Subject WithoutAuthTime(Subject s) => s with { AuthTime = null };

    public static Markings Itar(string program = "ORCA") => Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)",
        cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: program).Value;

    public static readonly Markings Open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;

    public async Task<string> CreatePartAsync(string name = "Bracket, sensor", Markings? markings = null, Subject? author = null)
    {
        var part = await Parts.CreateAsync(author ?? Alice, new CreatePartCommand(name, "Component", "EA", "Make", markings ?? Itar(), "Synthetic bracket",
            new JsonObject { ["material"] = "6061-T6", ["mass"] = 0.42 }), null);
        return part.Value!.Number;
    }

    public static StartReviewCommand Start(string number, string reviewer, params string[] approvers) => new("part", number,
        new Dictionary<string, string[]> { ["reviewer"] = [reviewer], ["approvers"] = approvers });

    /// <summary>Starts the seeded review as alice and returns the instance.</summary>
    public async Task<InstanceView> StartAsync(string number, string reviewer = "rita", params string[] approvers)
    {
        var started = await Flow.StartAsync(Alice, Start(number, reviewer, approvers.Length == 0 ? ["bob", "carol"] : approvers), "corr-start");
        Assert.True(started.IsOk, string.Join("; ", started.Errors));
        return started.Value!;
    }

    public async Task<InstanceView> SignAsync(Subject who, Guid taskId, string code, string? comment = null)
    {
        var r = await Flow.SignAsync(Fresh(who), taskId, code, comment, "corr-sign");
        Assert.True(r.IsOk, $"{who.Username} signing as {code}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public static Guid TaskOf(InstanceView v, string username, string status = "Open") => v.Tasks.Single(t => t.AssigneeUsername == username && t.Status == status).Id;

    public async Task<InstanceView> ViewAsync(Subject who, Guid instanceId) => (await Flow.GetInstanceAsync(who, instanceId, null)).Value!;

    public async Task<List<AuditRecord>> AuditAsync() => [.. await new AuditQueries(Ds).ListAsync(new AuditQuery(Limit: 5000))];

    public async Task<string> PartStateAsync(string number) => (await Parts.GetAsync(Alice, number, null, null)).Value!.Revision.State;

    public async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
