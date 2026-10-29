using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Vault;
using Keel.Parts;
using Keel.Records;
using Keel.TestSupport;
using Keel.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>A store that says whether it supports S3 Object Lock and records what it was asked to lock.</summary>
internal sealed class FakeWorm(bool supported) : IWormStorage
{
    public List<(string Sha, string Mode, DateTimeOffset Until)> Applied { get; } = [];
    public bool FailApply { get; set; }
    public Task<WormCapability> DetectAsync(CancellationToken ct = default) => Task.FromResult(new WormCapability(supported, supported ? "enabled" : "The store does not provide S3 Object Lock."));

    public Task<WormApplied> ApplyRetentionAsync(string sha256, string mode, DateTimeOffset retainUntil, CancellationToken ct = default)
    {
        if (FailApply) throw new InvalidOperationException("refused");
        Applied.Add((sha256, mode, retainUntil));
        return Task.FromResult(new WormApplied(mode, retainUntil));
    }
}

/// <summary>Real services of records management over a fresh database and the real vault; the review engine knows the part, document and records gateways. No mocks except S3 Object Lock.</summary>
internal sealed class RecordsEnv
{
    public required TestDatabase Db { get; init; }
    public required NpgsqlDataSource Ds { get; init; }
    public required PostgresAuditLog Audit { get; init; }
    public required TestClock Clock { get; init; }
    public required ObjectVault Vault { get; init; }
    public required VaultOptions VaultOptions { get; init; }
    public required VaultContentStore Store { get; init; }
    public required DocumentService Docs { get; init; }
    public required PartService Parts { get; init; }
    public required WorkflowService Flow { get; init; }
    public required UserDirectory Directory { get; init; }
    public required ScheduleService Schedules { get; init; }
    public required RetentionClassificationService Classifier { get; init; }
    public required HoldService Holds { get; init; }
    public required ArchiveService Archives { get; init; }
    public required DispositionService Disposition { get; init; }
    public required RecordsSigner Signer { get; init; }
    public required TrustedCheckpointKeys Trusted { get; init; }
    public required CheckpointSigner CheckpointSigner { get; init; }
    public required FakeWorm Worm { get; init; }

    // ---- synthetic users: Rex is the records manager (viewer, author and records roles), Ada an administrator, Eve a user outside program ORCA
    public static Subject Rex => WorkflowEnv.User("rex", [Roles.Viewer, Roles.Author, Roles.RecordsManager]);
    public static Subject Rhea => WorkflowEnv.User("rhea", [Roles.Viewer, Roles.Author, Roles.RecordsManager]);
    public static Subject Alice => WorkflowEnv.Alice;
    public static Subject Rita => WorkflowEnv.Rita;
    public static Subject Bob => WorkflowEnv.Bob;
    public static Subject Carol => WorkflowEnv.Carol;
    public static Subject Ada => WorkflowEnv.Ada;
    public static Subject Audrey => WorkflowEnv.Audrey;
    /// <summary>A records manager outside program ORCA: she cannot read ORCA data, whatever her role.</summary>
    /// <summary>An author of program KRAKEN only.</summary>
    public static Subject Kay => WorkflowEnv.User("kay", [Roles.Viewer, Roles.Author], programs: ["KRAKEN"]);
    public static Subject Eve => WorkflowEnv.User("eve", [Roles.Viewer, Roles.Author, Roles.RecordsManager], programs: []);

    public static Markings Open => WorkflowEnv.Open;
    public static Markings Itar(string program = "ORCA") => Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: program).Value;
    public static Markings Cui() => Markings.Create(ExportJurisdiction.None, DistributionStatement.C, "ENG", cuiCategories: ["CTI"], controllingOffice: "ACME", distributionReason: "Proprietary").Value;

    public static async Task<RecordsEnv> CreateAsync(PostgresFixture pg, bool wormSupported = false, RecordsOptions? options = null)
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var clock = new TestClock();
        var audit = new PostgresAuditLog(ds, clock);
        var vaultOptions = await VaultContainer.IsolatedOptionsAsync();
        var vault = new ObjectVault(vaultOptions, null, new PostgresVaultPendingGuard(ds, clock));
        var store = new VaultContentStore(vault);
        var key = CheckpointKeys.Generate();
        var checkpointSigner = new CheckpointSigner(key, ds, clock);
        var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo(), out _);
        var trusted = new TrustedCheckpointKeys([pub]);
        var signer = new RecordsSigner(checkpointSigner, trusted);
        var worm = new FakeWorm(wormSupported);
        options ??= new RecordsOptions();
        var archives = new ArchiveService(ds, audit, clock, signer, trusted, options, store, store, worm);
        var wfOptions = new WorkflowOptions();
        var env = new RecordsEnv
        {
            Db = db, Ds = ds, Audit = audit, Clock = clock, Vault = vault, VaultOptions = vaultOptions, Store = store, Signer = signer, Trusted = trusted, CheckpointSigner = checkpointSigner, Worm = worm,
            Docs = new DocumentService(ds, audit, clock, vault),
            Parts = new PartService(ds, audit, clock),
            Flow = new WorkflowService(ds, audit, [new PartWorkflowGateway(), new DocumentWorkflowGateway(), new ScheduleWorkflowGateway(audit), new HoldWorkflowGateway(audit), new DispositionWorkflowGateway(audit, clock)], wfOptions, clock),
            Directory = new UserDirectory(ds, clock, wfOptions, NullLogger<UserDirectory>.Instance),
            Schedules = new ScheduleService(ds, audit, clock),
            Classifier = new RetentionClassificationService(ds, audit, clock),
            Holds = new HoldService(ds, audit, clock),
            Archives = archives,
            Disposition = new DispositionService(ds, audit, clock, signer, archives, options, store),
        };
        foreach (var u in new[] { Rex, Rhea, Alice, Rita, Bob, Carol, Ada, Audrey, Eve }) await env.Directory.UpsertAsync(u);
        return env;
    }

    // ---------------------------------------------------------------- data builders

    public static byte[] Unique(string text) => Encoding.UTF8.GetBytes($"{text}\n{Guid.NewGuid()}\n");

    public async Task<string> NewDocAsync(string name = "Interface spec", Markings? markings = null, string type = "Specification", byte[]? content = null, string fileName = "spec.txt", Subject? by = null)
    {
        var d = await Docs.CreateAsync(by ?? Alice, new CreateDocumentCommand(name, type, markings ?? Open, "Synthetic"), new FileUpload(fileName, "text/plain", new MemoryStream(content ?? Unique(name))), null);
        Assert.True(d.IsOk, string.Join("; ", d.Errors));
        return d.Value!.Number;
    }

    public async Task<string> NewPartAsync(string name = "Bracket", Markings? markings = null)
    {
        var p = await Parts.CreateAsync(Alice, new CreatePartCommand(name, "Component", "EA", "Buy", markings ?? Open, "Synthetic", new JsonObject()), null);
        Assert.True(p.IsOk, string.Join("; ", p.Errors));
        return p.Value!.Number;
    }

    public async Task ReleaseDocAsync(string number)
    {
        var r = await Docs.SetStateAsync(Ada, number, "Released", "test release", true, null);
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
    }

    public async Task ObsoleteDocAsync(string number)
    {
        var r = await Docs.SetStateAsync(Ada, number, "Obsolete", "test obsolescence", true, null);
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
    }

    public async Task ReleasePartAsync(string number)
    {
        var r = await Parts.SetStateAsync(Ada, number, "Released", "test release", true, null);
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
    }

    public async Task<string> ReleasedDocAsync(string name = "Interface spec", Markings? markings = null, string type = "Specification", byte[]? content = null)
    {
        var n = await NewDocAsync(name, markings, type, content);
        await ReleaseDocAsync(n);
        return n;
    }

    public static ClassInput DocClass(string code = "SPEC", int months = 0, string action = DispositionActions.Destroy, string[]? types = null, string[]? programs = null, int priority = 100, string trigger = TriggerEvents.Release, string[]? kinds = null) =>
        new(code, "Class " + code, null, priority, kinds ?? ["document"], types ?? [], programs ?? [], [], [], [], months, trigger, action, "Policy 7.1", null);

    /// <summary>Runs a review of a records object with reviewer rita and approvers bob and carol; the submitter is Rex.</summary>
    public async Task ReviewAsync(string objectType, string key, Subject? submitter = null, string[]? approvers = null)
    {
        var participants = new Dictionary<string, string[]> { ["approvers"] = approvers ?? ["bob", "carol"] };
        if (objectType != "legal_hold") participants["reviewer"] = ["rita"];
        var started = await Flow.StartAsync(submitter ?? Rex, new StartReviewCommand(objectType, key, participants), "corr-start");
        Assert.True(started.IsOk, string.Join("; ", started.Errors));
        var v = started.Value!;
        if (objectType != "legal_hold") v = await SignAsync(Rita, v.Tasks.Single(t => t.Status == "Open").Id, "Reviewed");
        foreach (var a in approvers ?? ["bob", "carol"])
        {
            var who = a == "bob" ? Bob : Carol;
            v = await SignAsync(who, WorkflowEnv.TaskOf(v, a), "Approved");
        }
        Assert.Equal("Approved", v.Status);
    }

    public async Task<InstanceView> SignAsync(Subject who, Guid taskId, string code)
    {
        var r = await Flow.SignAsync(WorkflowEnv.Fresh(who), taskId, code, null, "corr-sign");
        Assert.True(r.IsOk, $"{who.Username} signing as {code}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    /// <summary>Drafts, fills, signs and publishes a schedule version; returns its number.</summary>
    public async Task<int> PublishScheduleAsync(params ClassInput[] classes)
    {
        var draft = await Schedules.CreateDraftAsync(Rex, "Test schedule", null, null);
        Assert.True(draft.IsOk, string.Join("; ", draft.Errors));
        foreach (var c in classes)
        {
            var up = await Schedules.UpsertClassAsync(Rex, draft.Value!.Version, c, null);
            Assert.True(up.IsOk, string.Join("; ", up.Errors));
        }
        await ReviewAsync("retention_schedule", $"v{draft.Value!.Version}");
        var published = await Schedules.GetPublishedAsync(Rex, null);
        Assert.Equal(draft.Value.Version, published.Value!.Version);
        return draft.Value.Version;
    }

    public async Task ClassifyAsync()
    {
        var c = await Classifier.ClassifyAsync(Rex, null);
        Assert.True(c.IsOk, string.Join("; ", c.Errors));
    }

    /// <summary>Creates a disposition request for the objects, runs its review, and returns the number of the approved request.</summary>
    public async Task<string> ApprovedRequestAsync(params (string Kind, string Number)[] items)
    {
        var created = await Disposition.CreateAsync(Rex, new CreateDispositionCommand([.. items.Select(i => new ObjectRef(i.Kind, i.Number))], null), null);
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        await ReviewAsync("disposition", created.Value!.Number);
        var again = await Disposition.GetAsync(Rex, created.Value.Number, null);
        Assert.Equal(DispositionStatuses.Approved, again.Value!.Status);
        return created.Value.Number;
    }

    public async Task<HoldView> PlaceObjectHoldAsync(string kind, string number, Subject? by = null)
    {
        var h = await Holds.PlaceAsync(by ?? Rex, new PlaceHoldCommand("Matter " + Guid.NewGuid().ToString("N")[..6], "Litigation", new HoldScope(HoldScopeKinds.Objects, [new HoldObjectRef(kind, number)])), null);
        Assert.True(h.IsOk, string.Join("; ", h.Errors));
        return h.Value!;
    }

    /// <summary>A vault garbage collector whose clock runs days ahead of the real one (objects keep their real last-modified time).</summary>
    public VaultGarbageCollector Collector(int daysAhead = 3) => new(Vault, Ds, Audit, new ManualTimeProvider(DateTimeOffset.UtcNow.AddDays(daysAhead)));

    public Task<GcReport> SweepAsync(string? prefix = null, GcMode mode = GcMode.Delete, int daysAhead = 3) =>
        Collector(daysAhead).RunAsync(new GcRequest(mode, TimeSpan.FromDays(1), 1000, 100, null, ActorSnapshot.System, "records-test", prefix));

    public static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public async Task<string> FileHashAsync(string docNumber)
    {
        var d = await Docs.GetAsync(Ada, docNumber, null, null);
        return d.Value!.Iteration.Files[0].Sha256;
    }

    public async Task<long> ScalarAsync(string sql, params (string, object)[] parameters)
    {
        await using var conn = new NpgsqlConnection(Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<List<AuditRecord>> AuditAsync() => [.. await new AuditQueries(Ds).ListAsync(new AuditQuery(Limit: 10000))];

    public async Task<VerificationResult> VerifyAuditChainAsync()
    {
        await CheckpointSigner.CreateAsync();
        await using var conn = new NpgsqlConnection(Db.SuperuserConnectionString);
        await conn.OpenAsync();
        return await AuditVerifier.VerifyAsync(conn, Trusted.Keys);
    }

    /// <summary>The app role's attempt to run a statement; returns the database's refusal message, or null when it ran.</summary>
    public async Task<string?> AppSqlAsync(string sql, params (string, object)[] parameters)
    {
        await using var conn = await Ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
        try { await cmd.ExecuteNonQueryAsync(); return null; }
        catch (PostgresException e) { return e.MessageText; }
    }

    public static ImmutableSortedSet<string> Set(params string[] v) => v.ToImmutableSortedSet(StringComparer.Ordinal);
}
