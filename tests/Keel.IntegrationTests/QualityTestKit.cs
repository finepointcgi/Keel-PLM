using Keel.Audit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Notifications;
using Keel.Parts;
using Keel.Quality;
using Keel.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Real services over a fresh Postgres database for the quality tests (8.1, ADR-0036): parts and change management from <see cref="ChangeEnv"/>, the quality services and the
/// three review gateways wired like the host, the notification sink and center, synthetic users. No mocks.
/// </summary>
internal sealed class QualityEnv
{
    public required ChangeEnv Base { get; init; }
    public required NpgsqlDataSource Ds { get; init; }
    public required TestClock Clock { get; init; }
    public required PostgresAuditLog Audit { get; init; }
    public required QualityServices Q { get; init; }
    public required WorkflowService Flow { get; init; }
    public required NotificationCenter Center { get; init; }
    public required TestDatabase Db { get; init; }

    public NcrService Ncrs => Q.Ncrs;
    public CapaService Capas => Q.Capas;
    public DeviationService Deviations => Q.Deviations;
    public QualityActions Actions => Q.Actions;
    public QualityQueries Queries => Q.Queries;

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
    public static Markings Open => ChangeEnv.Open;

    public static async Task<QualityEnv> CreateAsync(PostgresFixture pg)
    {
        var b = await ChangeEnv.CreateAsync(pg);
        var audit = new PostgresAuditLog(b.Ds, b.Clock);
        var options = new NotificationOptions { PublishDelayMilliseconds = 0 };
        var hub = new NotificationHub();
        var writer = new NotificationWriter(options, b.Clock, hub);
        var sink = new NotificationSink(writer, NullLogger<NotificationSink>.Instance);
        var partChange = new PartChangeGateway([new BomParticipant()]);
        var objects = new ObjectAccessResolver([new PartObjectAccess(b.Ds), new ChangeObjectAccess(b.Ds), new QualityObjectAccess(b.Ds, QualityKinds.Ncr), new QualityObjectAccess(b.Ds, QualityKinds.Capa), new QualityObjectAccess(b.Ds, QualityKinds.Deviation)]);
        var q = QualityModule.Create(b.Ds, audit, b.Clock, [partChange], [new ChangeObjectAccess(b.Ds)], sink, new QualityOptions { ExpiryWarningDays = 14 });
        var flow = new WorkflowService(b.Ds, audit,
            [new PartWorkflowGateway([new ChangeBindingGuard()]), new QualityWorkflowGateway(QualityKinds.Ncr, audit), new QualityWorkflowGateway(QualityKinds.Capa, audit), new QualityWorkflowGateway(QualityKinds.Deviation, audit)],
            new WorkflowOptions(), b.Clock, sink);
        return new QualityEnv { Base = b, Ds = b.Ds, Clock = b.Clock, Audit = audit, Q = q, Flow = flow, Db = b.Db, Center = new NotificationCenter(b.Ds, objects, audit, b.Clock, hub) };
    }

    // ---------------------------------------------------------------- parts

    public Task<string> ReleasedPartAsync(Markings? markings = null, string name = "Bracket, sensor") => Base.ReleasedPartAsync(name, markings ?? Itar());

    // ---------------------------------------------------------------- records

    public async Task<NcrDetail> NewNcrAsync(Subject by, string part, Markings? markings = null, string title = "Burr on mounting face", string severity = "Major", decimal quantity = 5, string? lot = "LOT-7", string? serial = null, string? supplier = null)
    {
        var r = await Ncrs.CreateAsync(by, new CreateNcrCommand(title, "Synthetic defect description", severity, "Receiving", part, null, lot, serial, supplier, quantity, "EA", markings ?? Itar()), "corr-ncr");
        Assert.True(r.IsOk, $"NCR: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public async Task<CapaDetail> NewCapaAsync(Subject by, Markings? markings = null, string title = "Recurring burr", string? owner = null)
    {
        var r = await Capas.CreateAsync(by, new CreateCapaCommand(title, "Synthetic problem statement", "Corrective", "Ncr", "High", owner, null, markings ?? Itar()), "corr-capa");
        Assert.True(r.IsOk, $"CAPA: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public async Task<DeviationDetail> NewDeviationAsync(Subject by, Markings? markings = null, string title = "Alternate finish", bool customer = false, int fromDays = 0, int toDays = 60, string kind = "Deviation")
    {
        var today = DateOnly.FromDateTime(Clock.GetUtcNow().UtcDateTime);
        var r = await Deviations.CreateAsync(by, new CreateDeviationCommand(kind, title, "Synthetic justification", "Spec 4.2", customer, today.AddDays(fromDays), today.AddDays(toDays), markings ?? Itar()), "corr-dev");
        Assert.True(r.IsOk, $"Deviation: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public async Task<DeviationDetail> AddScopeAsync(Subject by, string deviation, string part, string? revision = null, long? from = null, long? to = null, string[]? lots = null, decimal? qty = null)
    {
        var r = await Deviations.AddScopeAsync(by, deviation, new ScopeInput(part, revision, from, to, null, lots, qty), "corr-scope");
        Assert.True(r.IsOk, $"Scope: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public async Task<ActionView> AddActionAsync(Subject by, string kind, string number, string type, string owner, string what = "Sort the lot and quarantine suspect parts", DateOnly? due = null)
    {
        var r = await Actions.AddAsync(by, kind, number, new CreateActionCommand(type, what, owner, due), "corr-action");
        Assert.True(r.IsOk, $"Action: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public async Task FinishActionAsync(Subject owner, string kind, string number, string action)
    {
        Assert.True((await Actions.StartAsync(owner, kind, number, action, null)).IsOk);
        var done = await Actions.CompleteAsync(owner, kind, number, action, "Done as described", null);
        Assert.True(done.IsOk, string.Join("; ", done.Errors));
    }

    // ---------------------------------------------------------------- reviews

    public async Task<InstanceView> SubmitAsync(string kind, string number, Subject? submitter = null, string reviewer = "rita", params string[] approvers)
    {
        var participants = kind switch
        {
            QualityKinds.Ncr => new Dictionary<string, string[]> { ["reviewer"] = [reviewer], ["mrb"] = approvers.Length == 0 ? ["bob", "carol"] : approvers },
            QualityKinds.Capa => new Dictionary<string, string[]> { ["verifiers"] = approvers.Length == 0 ? ["bob", "carol"] : approvers },
            _ => new Dictionary<string, string[]> { ["approvers"] = approvers.Length == 0 ? ["bob", "carol"] : approvers },
        };
        var started = await Flow.StartAsync(submitter ?? Alice, new StartReviewCommand(kind, number, participants), "corr-submit");
        Assert.True(started.IsOk, $"submitting {number}: {started.Kind} {string.Join("; ", started.Errors)}");
        return started.Value!;
    }

    public Task<Outcome<InstanceView>> TrySubmitAsync(string kind, string number, Subject? submitter = null)
    {
        var participants = kind switch
        {
            QualityKinds.Ncr => new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["mrb"] = ["bob", "carol"] },
            QualityKinds.Capa => new Dictionary<string, string[]> { ["verifiers"] = ["bob", "carol"] },
            _ => new Dictionary<string, string[]> { ["approvers"] = ["bob", "carol"] },
        };
        return Flow.StartAsync(submitter ?? Alice, new StartReviewCommand(kind, number, participants), "corr-submit");
    }

    public async Task<InstanceView> SignAsync(Subject who, Guid taskId, string code, string? comment = null)
    {
        var r = await Flow.SignAsync(WorkflowEnv.Fresh(who), taskId, code, comment, "corr-sign");
        Assert.True(r.IsOk, $"{who.Username} signing as {code}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

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

    // ---------------------------------------------------------------- raw reads for assertions

    public async Task<List<AuditRecord>> AuditAsync() => [.. await new AuditQueries(Ds).ListAsync(new AuditQuery(Limit: 5000))];

    public async Task<VerificationResult> VerifyAuditChainAsync()
    {
        await using var conn = new NpgsqlConnection(Db.SuperuserConnectionString);
        await conn.OpenAsync();
        return await AuditVerifier.VerifyAsync(conn, []);
    }

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
