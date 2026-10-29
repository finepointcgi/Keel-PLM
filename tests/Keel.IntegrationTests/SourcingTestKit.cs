using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Parts;
using Keel.Sourcing;
using Keel.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>An in-memory vault: content addressed, and a payload containing EICAR is rejected the way the scanning vault rejects it.</summary>
internal sealed class FakeContentStore : IContentStore, IContentReader
{
    public Dictionary<string, byte[]> Objects { get; } = [];

    public async Task<ContentInfo> PutAsync(Stream content, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        if (Encoding.ASCII.GetString(bytes).Contains("EICAR", StringComparison.Ordinal)) throw new ContentRejectedException("the malware scanner detected Eicar-Test-Signature");
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Objects[sha] = bytes;
        return new ContentInfo(sha, bytes.Length);
    }

    public Task<bool> ExistsAsync(string sha256, long sizeBytes, CancellationToken ct = default) => Task.FromResult(Objects.ContainsKey(sha256));
    public Task VerifyAsync(string sha256, long sizeBytes, CancellationToken ct = default) => Task.CompletedTask;
    public Task<Stream> OpenReadAsync(string sha256, long sizeBytes, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream(Objects[sha256]));
}

/// <summary>Records the notification requests the services make (pointers only by construction).</summary>
internal sealed class RecordingSink : INotificationSink
{
    public List<NotificationRequest> Requests { get; } = [];
    public List<(string User, string Type, string Number)> Watches { get; } = [];

    public Task AppendAsync(DbConnection conn, DbTransaction tx, NotificationRequest request, CancellationToken ct) { lock (Requests) Requests.Add(request); return Task.CompletedTask; }
    public Task AutoWatchAsync(DbConnection conn, DbTransaction tx, string userSub, string objectType, string objectNumber, CancellationToken ct) { Watches.Add((userSub, objectType, objectNumber)); return Task.CompletedTask; }
}

/// <summary>Real services of the sourcing module over a fresh Postgres database, with the review engine wired to the part, supplier and list entry gateways. No mocks except the vault.</summary>
internal sealed class SourcingEnv
{
    public required TestDatabase Db { get; init; }
    public required NpgsqlDataSource Ds { get; init; }
    public required PartService Parts { get; init; }
    public required WorkflowService Flow { get; init; }
    public required UserDirectory Directory { get; init; }
    public required SupplierService Suppliers { get; init; }
    public required SourcingService Sourcing { get; init; }
    public required RequestService Requests { get; init; }
    public required PortalService Portal { get; init; }
    public required CertificationExpiryService Expiry { get; init; }
    public required TestClock Clock { get; init; }
    public required FakeContentStore Store { get; init; }
    public required RecordingSink Sink { get; init; }

    public static Subject Alice => WorkflowEnv.Alice;
    public static Subject Rita => WorkflowEnv.Rita;
    public static Subject Bob => WorkflowEnv.Bob;
    public static Subject Carol => WorkflowEnv.Carol;
    public static Subject Ada => WorkflowEnv.Ada;
    public static Subject Mallory => WorkflowEnv.Mallory;
    public static Subject Audrey => WorkflowEnv.Audrey;
    /// <summary>An author who is not on program ORCA: ORCA data is invisible to her.</summary>
    public static readonly Subject Eve = WorkflowEnv.User("eve", [Roles.Viewer, Roles.Author], programs: []);
    /// <summary>A viewer who holds no CUI category: restricted suppliers and parts are invisible to him.</summary>
    public static readonly Subject Vic = WorkflowEnv.User("vic", [Roles.Viewer], programs: [], cui: []);
    public static readonly Subject Sup1 = WorkflowEnv.User("sup1", [Roles.Supplier], programs: [], cui: [], org: "SUP1");
    public static readonly Subject Sup2 = WorkflowEnv.User("sup2", [Roles.Supplier], programs: [], cui: [], org: "SUP2");
    public static readonly Subject Sup3 = WorkflowEnv.User("sup3", [Roles.Supplier], programs: [], cui: [], org: "SUP3");

    public static Markings Itar() => WorkflowEnv.Itar();
    public static Markings Open => WorkflowEnv.Open;
    public static Markings Cui() => Markings.Create(ExportJurisdiction.None, DistributionStatement.C, "ENG", cuiCategories: ["CTI"], controllingOffice: "ACME", distributionReason: "Proprietary").Value;

    public static async Task<SourcingEnv> CreateAsync(PostgresFixture pg, Action<SupplierService>? _ = null)
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var clock = new TestClock();
        var audit = new PostgresAuditLog(ds, clock);
        var sink = new RecordingSink();
        var store = new FakeContentStore();
        var options = new WorkflowOptions();
        var env = new SourcingEnv
        {
            Db = db, Ds = ds, Clock = clock, Store = store, Sink = sink,
            Parts = new PartService(ds, audit, clock),
            Flow = new WorkflowService(ds, audit, [new PartWorkflowGateway(), new SupplierWorkflowGateway(audit, clock, sink), new SourceEntryWorkflowGateway(audit, clock)], options, clock),
            Directory = new UserDirectory(ds, clock, options, NullLogger<UserDirectory>.Instance),
            Suppliers = new SupplierService(ds, audit, clock, sink),
            Sourcing = new SourcingService(ds, audit, clock),
            Requests = new RequestService(ds, audit, clock, store),
            Portal = new PortalService(ds, audit, clock, store, store),
            Expiry = new CertificationExpiryService(ds, audit, clock, sink),
        };
        foreach (var u in new[] { Alice, Rita, Bob, Carol, Ada, Mallory, Audrey, Eve, Vic, Sup1, Sup2, Sup3 }) await env.Directory.UpsertAsync(u);
        return env;
    }

    // ---------------------------------------------------------------- data builders

    public async Task<string> NewPartAsync(string name = "Bracket, sensor", Markings? markings = null, Subject? author = null, string source = "Buy")
    {
        var part = await Parts.CreateAsync(author ?? Alice, new CreatePartCommand(name, "Component", "EA", source, markings ?? Open, "Synthetic bracket", new JsonObject { ["material"] = "6061-T6" }), null);
        Assert.True(part.IsOk, string.Join("; ", part.Errors));
        return part.Value!.Number;
    }

    public async Task<string> NewSupplierAsync(string name = "Alpine Machining", string country = "USA", bool foreign = false, string[]? roles = null, Markings? markings = null, Subject? by = null, string? profile = null, string? cage = null)
    {
        var s = await Suppliers.CreateAsync(by ?? Alice, new CreateSupplierCommand(name, country, roles ?? ["vendor", "manufacturer"], foreign, markings ?? Open, cage), null);
        Assert.True(s.IsOk, string.Join("; ", s.Errors));
        if (profile is not null)
        {
            var u = await Suppliers.UpdateAsync(Ada, s.Value!.Summary.Number, new UpdateSupplierCommand(name, country, roles ?? ["vendor", "manufacturer"], foreign, cage, null, profile), s.Value.Revision, null);
            Assert.True(u.IsOk, string.Join("; ", u.Errors));
        }
        return s.Value!.Summary.Number;
    }

    public async Task AddCertificationAsync(string supplier, int validForDays = 365, string type = "AS9100")
    {
        var today = DateOnly.FromDateTime(Clock.GetUtcNow().UtcDateTime);
        var r = await Suppliers.AddCertificationAsync(Alice, supplier, new AddCertificationCommand(type, "C-" + Guid.NewGuid().ToString("N")[..6], "Registrar", null, today.AddDays(-30), today.AddDays(validForDays)), null);
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
    }

    /// <summary>Gets the supplier approved (or conditional) through the supplier review: reviewer rita, approvers bob and carol sign.</summary>
    public async Task ApproveSupplierAsync(string number, string status = SupplierStatuses.Approved)
    {
        if (status == SupplierStatuses.Approved) await AddCertificationAsync(number);
        var proposed = await Suppliers.ProposeStatusAsync(Alice, number, status, null);
        Assert.True(proposed.IsOk, string.Join("; ", proposed.Errors));
        await ReviewAsync("supplier", number);
        var after = await Suppliers.GetAsync(Alice, number, null);
        Assert.Equal(status, after.Value!.Summary.Status);
    }

    public async Task ReviewAsync(string objectType, string key)
    {
        var started = await Flow.StartAsync(Alice, new StartReviewCommand(objectType, key, new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob", "carol"] }), "corr-start");
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

    public async Task<EntryView> DraftEntryAsync(string kind, string part, string supplier, string status = EntryStatuses.Approved, string? mpn = "MPN-100", string? manufacturer = null, DateOnly? from = null, DateOnly? to = null, string? revision = null, Subject? by = null)
    {
        var r = await Sourcing.CreateEntryAsync(by ?? Alice, new CreateEntryCommand(kind, part, revision, supplier, kind == SourceKinds.Aml || manufacturer is not null ? mpn : null, manufacturer, status, from, to), null);
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        return r.Value!;
    }

    /// <summary>A list entry that went through its review and is Active.</summary>
    public async Task<EntryView> ActiveEntryAsync(string kind, string part, string supplier, string status = EntryStatuses.Approved, string? mpn = "MPN-100", DateOnly? from = null, DateOnly? to = null, string? revision = null)
    {
        var e = await DraftEntryAsync(kind, part, supplier, status, mpn, null, from, to, revision);
        await ReviewAsync("source_entry", e.Number);
        var again = await Sourcing.GetEntryAsync(Alice, e.Number, null);
        Assert.Equal(EntryApprovalStates.Active, again.Value!.ApprovalState);
        return again.Value;
    }

    public async Task LinkPortalUserAsync(string supplier, Subject user)
    {
        var r = await Suppliers.LinkPortalUserAsync(Ada, supplier, user.Username!, null);
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
    }

    public Task SetProfileProgramsAsync(string profile, params string[] programs) =>
        Db.ExecuteAsSuperuserAsync($"UPDATE integration.clearance_profile SET allowed_programs = ARRAY[{string.Join(",", programs.Select(p => $"'{p}'"))}]::text[] WHERE name = '{profile}'");

    public async Task<List<AuditRecord>> AuditAsync() => [.. await new AuditQueries(Ds).ListAsync(new AuditQuery(Limit: 5000))];

    public async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static CreateRequestCommand Request(string title, Markings markings, IReadOnlyList<string> parts, IReadOnlyList<string> suppliers, string kind = RequestKinds.Rfq) =>
        new(kind, title, "Please quote.", null, markings, [.. parts.Select(p => new RequestPartInput(p, 10))], suppliers);

    public async Task<string> IssuedRequestAsync(string title, Markings markings, IReadOnlyList<string> parts, IReadOnlyList<string> suppliers)
    {
        var created = await Requests.CreateAsync(Alice, Request(title, markings, parts, suppliers), null);
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        var issued = await Requests.IssueAsync(Alice, created.Value!.Summary.Number, null);
        Assert.True(issued.IsOk, string.Join("; ", issued.Errors));
        return created.Value.Summary.Number;
    }
}
