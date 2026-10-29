using Keel.Audit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Integration;
using Keel.Notifications;
using Keel.Parts;
using Keel.Programs;
using Keel.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Real services over a fresh Postgres database for the programs tests (8.8, ADR-0043): parts and change management from <see cref="ChangeEnv"/>, the program service, the gate review
/// gateway wired into the workflow engine like the host, the notification sink and the outbox. No mocks.
/// </summary>
internal sealed class ProgramEnv
{
    public required ChangeEnv Base { get; init; }
    public required NpgsqlDataSource Ds { get; init; }
    public required TestClock Clock { get; init; }
    public required PostgresAuditLog Audit { get; init; }
    public required ProgramService P { get; init; }
    public required ProgramMaintenance Maintenance { get; init; }
    public required WorkflowService Flow { get; init; }
    public required TestDatabase Db { get; init; }

    public static Subject Alice => ChangeEnv.Alice;
    public static Subject Rita => ChangeEnv.Rita;
    public static Subject Bob => ChangeEnv.Bob;
    public static Subject Carol => ChangeEnv.Carol;
    public static Subject Ada => ChangeEnv.Ada;
    public static Subject Eve => ChangeEnv.Eve;
    public static Subject Kim => ChangeEnv.Kim;
    public static Subject Mallory => ChangeEnv.Mallory;
    public static Subject Audrey => ChangeEnv.Audrey;
    public static Markings Itar(string program = "ORCA") => ChangeEnv.Itar(program);

    /// <summary>The markings of a program page: no export restriction, but the program's own code.</summary>
    public static Markings Prg(string code = "ORCA") => Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG", program: code).Value;

    public static DateOnly D(int days) => new DateOnly(2027, 3, 1).AddDays(days);

    public static async Task<ProgramEnv> CreateAsync(PostgresFixture pg)
    {
        var b = await ChangeEnv.CreateAsync(pg);
        var audit = new PostgresAuditLog(b.Ds, b.Clock);
        var options = new NotificationOptions { PublishDelayMilliseconds = 0 };
        var writer = new NotificationWriter(options, b.Clock, new NotificationHub());
        var sink = new NotificationSink(writer, NullLogger<NotificationSink>.Instance);
        var outbox = new OutboxEventSink(audit, new IntegrationOptions(), b.Clock);
        var p = ProgramModule.Create(b.Ds, audit, b.Clock, [new PartObjectAccess(b.Ds), new ChangeObjectAccess(b.Ds)], sink, outbox);
        var flow = new WorkflowService(b.Ds, audit, [new PartWorkflowGateway([new ChangeBindingGuard()]), ProgramModule.CreateGateway(audit, p)], new WorkflowOptions(), b.Clock, sink);
        return new ProgramEnv { Base = b, Ds = b.Ds, Clock = b.Clock, Audit = audit, P = p, Maintenance = ProgramModule.CreateMaintenance(b.Ds, audit, b.Clock, sink), Flow = flow, Db = b.Db };
    }

    public async Task<ProgramDetail> NewProgramAsync(Subject by, string code = "ORCA", string manager = "alice", Markings? markings = null)
    {
        var r = await P.CreateAsync(by, new CreateProgramCommand(code, $"Program {code}", "Synthetic program", "Customer 7", "CONTRACT-001", manager, D(0), D(365), markings ?? Prg(code)), "corr-prg");
        Assert.True(r.IsOk, $"Program: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public async Task<ItemRow> AddAsync(Subject by, string code, string kind, string title, int start, int end, string? owner = null, string? parent = null, string? gate = null, Markings? markings = null)
    {
        var r = await P.AddItemAsync(by, code, new AddItemCommand(kind, parent, title, "Synthetic item", owner, D(start), D(end), gate, markings), "corr-item");
        Assert.True(r.IsOk, $"Item {title}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public async Task DependAsync(Subject by, string code, string pred, string succ, int lag = 0)
    {
        var r = await P.AddDependencyAsync(by, code, pred, succ, lag, "corr-dep");
        Assert.True(r.IsOk, $"Dependency {pred}->{succ}: {r.Kind} {string.Join("; ", r.Errors)}");
    }

    public async Task<ScheduleView> ScheduleAsync(Subject who, string code, DateOnly? asOf = null, int? baseline = null)
    {
        var r = await P.GetScheduleAsync(who, code, baseline, asOf ?? D(0), "corr-sched");
        Assert.True(r.IsOk, $"Schedule: {r.Kind}");
        return r.Value!;
    }

    public async Task<InstanceView> SubmitGateAsync(string gate, Subject? submitter = null, params string[] approvers)
    {
        var started = await Flow.StartAsync(submitter ?? Alice, new StartReviewCommand("gate", gate, new Dictionary<string, string[]> { ["approvers"] = approvers.Length == 0 ? ["bob", "carol"] : approvers }), "corr-gate");
        Assert.True(started.IsOk, $"submitting {gate}: {started.Kind} {string.Join("; ", started.Errors)}");
        return started.Value!;
    }

    public async Task<InstanceView> SignAsync(Subject who, Guid taskId, string code)
    {
        var r = await Flow.SignAsync(WorkflowEnv.Fresh(who), taskId, code, null, "corr-sign");
        Assert.True(r.IsOk, $"{who.Username} signing as {code}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public async Task<List<AuditRecord>> AuditAsync() => [.. await new AuditQueries(Ds).ListAsync(new AuditQuery(Limit: 5000))];

    /// <summary>Runs SQL as the least-privilege application role (the database guards must hold for it).</summary>
    public async Task<int> AppSqlAsync(string sql, params (string, object)[] parameters)
    {
        await using var cmd = Ds.CreateCommand(sql);
        foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<string> TextAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToString(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }
}
