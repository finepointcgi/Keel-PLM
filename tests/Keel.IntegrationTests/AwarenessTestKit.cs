using System.Text.Json;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Documents;
using Keel.Integration;
using Keel.Notifications;
using Keel.Parts;
using Keel.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Real services over a fresh Postgres database for the awareness tests (UX-4, ADR-0030): parts, review workflow and change management with the notification sink and the
/// outbox sink wired exactly like the host, plus the notification, watch, comment and activity services and the fan-out. Synthetic users, no mocks.
/// </summary>
internal sealed class AwarenessEnv
{
    public required TestDatabase Db { get; init; }
    public required NpgsqlDataSource Ds { get; init; }
    public required TestClock Clock { get; init; }
    public required PostgresAuditLog Audit { get; init; }
    public required PartService Parts { get; init; }
    public required WorkflowService Flow { get; init; }
    public required ChangeService Change { get; init; }
    public required UserDirectory Directory { get; init; }
    public required NotificationOptions Options { get; init; }
    public required NotificationHub Hub { get; init; }
    public required NotificationWriter Writer { get; init; }
    public required NotificationSink Sink { get; init; }
    public required ObjectAccessResolver Objects { get; init; }
    public required NotificationCenter Center { get; init; }
    public required WatchService Watches { get; init; }
    public required CommentService Comments { get; init; }
    public required ActivityService Feed { get; init; }
    public required FanOutService Fan { get; init; }
    public required RetentionService Retention { get; init; }
    public required OutboxEventSink Outbox { get; init; }

    public static Subject Alice => WorkflowEnv.Alice;
    public static Subject Rita => WorkflowEnv.Rita;
    public static Subject Bob => WorkflowEnv.Bob;
    public static Subject Carol => WorkflowEnv.Carol;
    public static Subject Lena => WorkflowEnv.Lena;
    public static Subject Ada => WorkflowEnv.Ada;
    public static Subject Mallory => WorkflowEnv.Mallory;
    public static Subject Eve => ChangeEnv.Eve;
    public static Subject Kim => ChangeEnv.Kim;
    /// <summary>A viewer on program ORCA (can read the synthetic ITAR parts, can write comments, cannot edit).</summary>
    public static readonly Subject Dan = WorkflowEnv.User("dan", [Roles.Viewer]);
    /// <summary>An auditor who belongs to ORCA, so the object checks pass and the auditor-only views can be tested.</summary>
    public static readonly Subject Auditor = WorkflowEnv.User("auditor", [Roles.Auditor]);
    public static Markings Itar(string program = "ORCA") => WorkflowEnv.Itar(program);

    public static async Task<AwarenessEnv> CreateAsync(PostgresFixture pg, Action<NotificationOptions>? configure = null)
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var clock = new TestClock();
        var audit = new PostgresAuditLog(ds, clock);
        var outbox = new OutboxEventSink(audit, new IntegrationOptions(), clock);
        var options = new NotificationOptions { PublishDelayMilliseconds = 0 };
        configure?.Invoke(options);
        var hub = new NotificationHub();
        var writer = new NotificationWriter(options, clock, hub);
        var sink = new NotificationSink(writer, NullLogger<NotificationSink>.Instance);
        var objects = new ObjectAccessResolver([new PartObjectAccess(ds), new DocumentObjectAccess(ds), new ChangeObjectAccess(ds), new BaselineObjectAccess(ds), new MbomObjectAccess(ds)]);
        var partChange = new PartChangeGateway(null, outbox);
        var wfOptions = new WorkflowOptions();
        var env = new AwarenessEnv
        {
            Db = db, Ds = ds, Clock = clock, Audit = audit, Options = options, Hub = hub, Writer = writer, Sink = sink, Objects = objects, Outbox = outbox,
            Parts = new PartService(ds, audit, clock, null, outbox, sink),
            Change = new ChangeService(ds, audit, [partChange], clock, null, sink),
            Flow = new WorkflowService(ds, audit,
                [new PartWorkflowGateway([new ChangeBindingGuard()], null, outbox), new ChangeWorkflowGateway(ChangeTypes.Ecr, audit, [partChange], null, outbox, sink), new ChangeWorkflowGateway(ChangeTypes.Ecn, audit, [partChange], null, outbox, sink)],
                wfOptions, clock, sink),
            Directory = new UserDirectory(ds, clock, wfOptions, NullLogger<UserDirectory>.Instance),
            Center = new NotificationCenter(ds, objects, audit, clock, hub),
            Watches = new WatchService(ds, objects, audit, options, clock),
            Comments = new CommentService(ds, objects, audit, options, writer, clock),
            Feed = new ActivityService(ds, objects, audit),
            Fan = new FanOutService(ds, writer, options, NullLogger<FanOutService>.Instance),
            Retention = new RetentionService(ds, options, clock, NullLogger<RetentionService>.Instance),
        };
        foreach (var u in new[] { Alice, Rita, Bob, Carol, Lena, Ada, Mallory, Eve, Kim, Dan, Auditor, WorkflowEnv.Dave, WorkflowEnv.Farid }) await env.Directory.UpsertAsync(u);
        return env;
    }

    // ---------------------------------------------------------------- parts and reviews

    public async Task<string> NewPartAsync(string name = "Bracket, sensor", Markings? markings = null, Subject? author = null, string description = "Synthetic bracket")
    {
        var part = await Parts.CreateAsync(author ?? Alice, new CreatePartCommand(name, "Component", "EA", "Make", markings ?? Itar(), description,
            new JsonObject { ["material"] = "6061-T6", ["mass"] = 0.42 }), null);
        Assert.True(part.IsOk, string.Join("; ", part.Errors));
        return part.Value!.Number;
    }

    public async Task CheckedInEditAsync(Subject by, string number, string description, string comment = "edit")
    {
        Assert.True((await Parts.CheckOutAsync(by, number, null)).IsOk);
        var detail = (await Parts.GetAsync(by, number, null, null)).Value!;
        Assert.True((await Parts.UpdateWorkingCopyAsync(by, number, description, detail.Checkout!.Attributes, null)).IsOk);
        var r = await Parts.CheckInAsync(by, number, comment, null);
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
    }

    public async Task<InstanceView> StartReviewAsync(string number, Subject? submitter = null, string reviewer = "rita", params string[] approvers)
    {
        var started = await Flow.StartAsync(submitter ?? Alice, WorkflowEnv.Start(number, reviewer, approvers.Length == 0 ? ["bob", "carol"] : approvers), "corr-start");
        Assert.True(started.IsOk, string.Join("; ", started.Errors));
        return started.Value!;
    }

    public async Task<InstanceView> SignAsync(Subject who, Guid taskId, string code, string? comment = null)
    {
        var r = await Flow.SignAsync(WorkflowEnv.Fresh(who), taskId, code, comment, "corr-sign");
        Assert.True(r.IsOk, $"{who.Username} signing as {code}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    /// <summary>Submits the part (reviewer rita, approvers bob and carol) and carries it through review to Released.</summary>
    public async Task ReleasePartAsync(string number, Subject? submitter = null)
    {
        var v = await StartReviewAsync(number, submitter);
        v = await SignAsync(Rita, v.Tasks.Single(t => t.Status == "Open").Id, "Reviewed");
        v = await SignAsync(Bob, WorkflowEnv.TaskOf(v, "bob"), "Approved");
        v = await SignAsync(Carol, WorkflowEnv.TaskOf(v, "carol"), "Approved");
        Assert.Equal("Approved", v.Status);
    }

    // ---------------------------------------------------------------- reading what the services stored

    public sealed record Row(Guid Id, string Recipient, string Kind, string ObjectType, string ObjectNumber, Guid? RefId, string? Actor, int Occurrences, bool Read, DateTimeOffset At);

    public async Task<List<Row>> RowsAsync(string? recipient = null)
    {
        await using var conn = new NpgsqlConnection(Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, recipient_sub, kind, object_type, object_number, ref_id, actor_sub, occurrences, read_at IS NOT NULL, created_at FROM ntf.notification WHERE @r::text IS NULL OR recipient_sub = @r ORDER BY created_at, id", conn);
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)recipient ?? DBNull.Value });
        var rows = new List<Row>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            rows.Add(new Row(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetGuid(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt32(7), r.GetBoolean(8), r.GetFieldValue<DateTimeOffset>(9)));
        return rows;
    }

    public static string Sub(Subject s) => s.Id;

    public async Task<List<Row>> RowsOfAsync(Subject who, string? kind = null) => [.. (await RowsAsync(who.Id)).Where(r => kind is null || r.Kind == kind)];

    public async Task<long> ScalarAsync(string sql, params (string, object)[] parameters)
    {
        await using var conn = new NpgsqlConnection(Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<string> TextAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task ExecuteAsync(string sql, params (string, object)[] parameters)
    {
        await using var conn = new NpgsqlConnection(Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<AuditRecord>> AuditAsync(string? action = null) =>
        [.. (await new AuditQueries(Ds).ListAsync(new AuditQuery(Limit: 5000))).Where(a => action is null || a.Action == action)];

    /// <summary>Runs the fan-out until the committed part of the outbox is processed; returns how many events were handled.</summary>
    public async Task<int> FanOutAsync()
    {
        var total = 0;
        for (var n = await Fan.ProcessBatchAsync(CancellationToken.None); n > 0; n = await Fan.ProcessBatchAsync(CancellationToken.None)) total += n;
        return total;
    }

    /// <summary>Adds a watch row directly (as if the user had been allowed to watch earlier, before their access changed).</summary>
    public Task InsertWatchAsync(Subject who, string kind, string number, string level = "all") => ExecuteAsync(
        "INSERT INTO ntf.watch (id, user_sub, object_type, object_number, level, created_at) VALUES (@id, @u, @t, @n, @l, now()) ON CONFLICT DO NOTHING",
        ("id", Guid.CreateVersion7()), ("u", who.Id), ("t", kind), ("n", number), ("l", level));

    /// <summary>The text of every column of every notification-schema row, for "nothing marked is stored here" checks.</summary>
    public async Task<string> DumpNtfTextAsync()
    {
        await using var conn = new NpgsqlConnection(Db.SuperuserConnectionString);
        await conn.OpenAsync();
        var sb = new System.Text.StringBuilder();
        foreach (var table in new[] { "notification", "watch", "preference", "kind_preference", "fanout_cursor" })
        {
            await using var cmd = new NpgsqlCommand($"SELECT t::text FROM ntf.{table} t", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) sb.AppendLine(r.GetString(0));
        }
        return sb.ToString();
    }

    public static string Json(object value) => JsonSerializer.Serialize(value);
}
