using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.Documents;
using Keel.Integration;
using Keel.Parts;
using Keel.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Real services of every module with the outbox sink wired in (like the host), plus the dispatcher, administration, pull queries and, on request,
/// the Technical Data Package service. Real Postgres, no mocks.
/// </summary>
internal sealed class IntegrationEnv
{
    public required ConfigEnv Config { get; init; }
    public required IntegrationOptions Options { get; init; }
    public required OutboxEventSink Sink { get; init; }
    public required IntegrationAdminService Admin { get; init; }
    public required DeliveryStore Store { get; init; }
    public required OutboxQueries Queries { get; init; }
    public required SinkRegistry Registry { get; init; }
    public TdpService? Tdp { get; init; }
    public TdpSigner? Signer { get; init; }

    public NpgsqlDataSource Ds => Config.Ds;
    public static Subject Ada => ConfigEnv.Ada;
    public static Subject Alice => ConfigEnv.Alice;

    public static async Task<IntegrationEnv> CreateAsync(PostgresFixture pg, bool documents = false, Action<IntegrationOptions>? configure = null)
    {
        var options = new IntegrationOptions
        {
            FileDropRoot = Directory.CreateTempSubdirectory("keel-drop-").FullName,
            BackoffBase = TimeSpan.FromMilliseconds(20),
            BackoffMax = TimeSpan.FromMilliseconds(60),
            Lease = TimeSpan.FromSeconds(30),
            PollInterval = TimeSpan.FromMilliseconds(50),
        };
        string? keyPath = null;
        if (documents)
        {
            keyPath = Path.Combine(Directory.CreateTempSubdirectory("keel-tdp-key-").FullName, "tdp.pem");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());
            options.TdpSigningKeyPath = keyPath;
        }
        configure?.Invoke(options);
        var config = await ConfigEnv.CreateAsync(pg, documents, (_, audit, clock) => new OutboxEventSink(audit, options, clock, new BomPublicationSource()));
        var timeProvider = config.Clock;
        var secrets = new SecretResolver(options);
        var registry = new SinkRegistry([new WebhookSink(options, secrets, new SsrfGuard(options), timeProvider), new FileDropSink(options, timeProvider), new BrokerSink(options, secrets, new SsrfGuard(options), timeProvider)]);
        var admin = new IntegrationAdminService(config.Ds, config.Audit, timeProvider, registry);
        TdpService? tdp = null;
        TdpSigner? signer = null;
        if (documents)
        {
            signer = new TdpSigner(options);
            var structures = new TdpStructureSource(config.Bom, config.Baselines);
            tdp = new TdpService(config.Ds, config.Audit, timeProvider, options, signer, admin, new PartTdpSource(config.Parts), structures, new TdpDocumentSource(config.Docs!),
                [new LinkedDocumentProvider(config.Docs!)]);
        }
        await new Keel.Workflow.UserDirectory(config.Ds, timeProvider, new Keel.Workflow.WorkflowOptions(), NullLogger<Keel.Workflow.UserDirectory>.Instance).UpsertAsync(WorkflowEnv.Farid);
        return new IntegrationEnv
        {
            Config = config, Options = options, Sink = (OutboxEventSink)config.Sink!, Admin = admin, Store = new DeliveryStore(config.Ds, config.Audit, timeProvider),
            Queries = new OutboxQueries(config.Ds, config.Audit), Registry = registry, Tdp = tdp, Signer = signer,
        };
    }

    public DeliveryDispatcher NewDispatcher() => new(Store, Registry, Options, NullLogger<DeliveryDispatcher>.Instance);

    // ---------------------------------------------------------------- configuration helpers

    public static ProfileInput Profile(string name, string category = "DodContractor", bool usOnly = true, string[]? statements = null, string[]? auths = null, string[]? programs = null,
        string[]? jurisdictions = null, string[]? cui = null) =>
        new(name, name, null, category, usOnly, jurisdictions ?? ["None", "Ear", "Itar"], [], [], cui ?? ["EXPT", "CTI"], programs ?? ["ORCA"], statements ?? ["A", "B", "C", "D", "E"], auths ?? []);

    public async Task<ProfileRecord> SaveProfileAsync(ProfileInput input)
    {
        var r = await Admin.SaveProfileAsync(Ada, input, null);
        Assert.True(r.IsOk, $"profile {input.Name}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public async Task<SinkRecord> SaveSinkAsync(string name, string kind, string profile, JsonObject config, bool enabled = true, string[]? types = null, bool full = false)
    {
        var r = await Admin.SaveSinkAsync(Ada, new SinkInput(name, name, kind, enabled, profile, config, types, full), null);
        Assert.True(r.IsOk, $"sink {name}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    public Task<SinkRecord> SaveFileSinkAsync(string name, string profile, string format = "json", bool enabled = true, string[]? types = null, bool full = false) =>
        SaveSinkAsync(name, "filedrop", profile, new JsonObject { ["directory"] = name, ["format"] = format }, enabled, types, full);

    public string DropDirectory(string sink) => Path.Combine(Options.FileDropRoot, sink);

    // ---------------------------------------------------------------- queries

    public async Task<List<(Guid Id, string Type, string Aggregate, string Payload)>> EventsAsync(string? type = null)
    {
        await using var conn = new NpgsqlConnection(Config.Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT id, event_type, aggregate_number, payload::text FROM integration.outbox WHERE @t::text IS NULL OR event_type = @t ORDER BY seq", conn);
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)type ?? DBNull.Value });
        var list = new List<(Guid, string, string, string)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    public Task<long> ScalarAsync(string sql) => Config.ScalarAsync(sql);
    public Task<string> TextAsync(string sql) => Config.TextAsync(sql);

    public async Task<List<DeliveryView>> DeliveriesAsync(string? status = null, string? sink = null) =>
        [.. (await Admin.ListDeliveriesAsync(Ada, status, sink, 500)).Value!];

    /// <summary>Runs the dispatchers until nothing is due (or the time is up). Deliveries waiting for a backoff are retried when it elapses.</summary>
    public async Task DrainAsync(params DeliveryDispatcher[] dispatchers) => await DrainAsync(TimeSpan.FromSeconds(30), dispatchers);

    public async Task DrainAsync(TimeSpan limit, params DeliveryDispatcher[] dispatchers)
    {
        if (dispatchers.Length == 0) dispatchers = [NewDispatcher()];
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            await Task.WhenAll(dispatchers.Select(d => d.RunOnceAsync(CancellationToken.None)));
            var open = await ScalarAsync("SELECT count(*) FROM integration.delivery WHERE status IN ('pending','failed')");
            if (open == 0) return;
            await Task.Delay(30);
        }
    }

    // ---------------------------------------------------------------- schema validation

    public static string SchemaDirectory => Path.Combine(FindRoot(), "docs", "integration", "schemas");

    public static void AssertPayloadValid(string type, int version, string payloadJson)
    {
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(SchemaDirectory, $"{type}.v{version}.json")))!;
        var errors = JsonSchemaLite.Validate(JsonNode.Parse(payloadJson), schema);
        Assert.True(errors.Count == 0, $"{type} v{version} payload does not match its schema:\n{string.Join("\n", errors)}\n{payloadJson}");
    }

    public static void AssertEnvelopeValid(JsonNode envelope)
    {
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(SchemaDirectory, "envelope.v1.json")))!;
        var errors = JsonSchemaLite.Validate(envelope, schema);
        Assert.True(errors.Count == 0, $"envelope does not match its schema:\n{string.Join("\n", errors)}");
        AssertPayloadValid(envelope["type"]!.GetValue<string>(), envelope["version"]!.GetValue<int>(), envelope["data"]!.ToJsonString());
    }

    public async Task AssertEveryEmittedPayloadMatchesItsSchemaAsync()
    {
        foreach (var e in await EventsAsync()) AssertPayloadValid(e.Type, 1, e.Payload);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
