using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Integration;
using Keel.Security;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>The pull API for service accounts: cursor paging that cannot skip late commits, and ABAC over the outbox against an in-memory oracle.</summary>
[Collection(PostgresCollection.Name)]
public sealed class IntegrationPullTests(PostgresFixture pg)
{
    private static Subject Service(string name, bool us = true, string[]? programs = null, string[]? cui = null, string[]? auths = null, string[]? roles = null) =>
        WorkflowEnv.User(name, roles ?? [Roles.Integration], us, programs: programs ?? ["ORCA"], cui: cui ?? ["EXPT", "CTI"], auths: auths);

    private static async Task AppendAsync(IntegrationEnv env, DomainEvent e)
    {
        await using var conn = await env.Ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await env.Sink.AppendAsync(conn, tx, e, CancellationToken.None);
        await tx.CommitAsync();
    }

    private static DomainEvent Event(string aggregate, Markings markings) => new(EventTypes.PartReleased, "part", Guid.NewGuid(), aggregate, "A", markings,
        new JsonObject { ["partType"] = "Component", ["n"] = aggregate }, DateTimeOffset.UtcNow, "alice");

    private static async Task<List<JsonObject>> PullAllAsync(IntegrationEnv env, Subject who, int limit = 7, string? type = null)
    {
        var all = new List<JsonObject>();
        string? cursor = null;
        for (var page = 0; page < 100; page++)
        {
            var r = await env.Queries.PullAsync(who, cursor, limit, type, null, "corr");
            Assert.True(r.IsOk, string.Join(";", r.Errors));
            if (r.Value!.Events.Count == 0) return all;
            all.AddRange(r.Value.Events.Select(e => e.Envelope));
            Assert.NotEqual(cursor, r.Value.NextCursor);
            cursor = r.Value.NextCursor;
        }
        throw new InvalidOperationException("The pull never ended.");
    }

    [Fact]
    public async Task Pull_results_equal_the_policy_oracle_for_every_subject_and_paging_never_skips_or_repeats()
    {
        var env = await IntegrationEnv.CreateAsync(pg);
        var rnd = new Random(4242);
        var statements = Enum.GetValues<DistributionStatement>();
        var markingsById = new Dictionary<string, Markings>();
        for (var i = 0; i < 60; i++)
        {
            var jur = (ExportJurisdiction)rnd.Next(3);
            var stmt = statements[rnd.Next(statements.Length)];
            var eccn = jur == ExportJurisdiction.Ear ? (rnd.Next(2) == 0 ? "EAR99" : "9A610") : null;
            var controlled = jur == ExportJurisdiction.Itar || (jur == ExportJurisdiction.Ear && eccn != "EAR99");
            var cui = controlled ? new List<string> { "EXPT" } : [];
            if (rnd.Next(3) == 0) cui.Add("PROPIN");
            var program = stmt == DistributionStatement.F ? "ORCA" : new[] { null, "ORCA", "KRAKEN" }[rnd.Next(3)];
            var m = Markings.Create(jur, stmt, "ENG", eccn, jur == ExportJurisdiction.Itar ? "XI(d)" : null, cui, stmt == DistributionStatement.A ? null : "ACME", stmt == DistributionStatement.A ? null : "r",
                program, jur != ExportJurisdiction.None && rnd.Next(3) == 0 ? ["TAA-0042"] : null);
            if (!m.IsSuccess) { i--; continue; }
            var number = $"P-{i:D6}";
            markingsById[number] = m.Value;
            await AppendAsync(env, Event(number, m.Value));
        }

        var subjects = new[]
        {
            Service("erp-us", us: true), Service("erp-nonus", us: false), Service("erp-nonus-taa", us: false, auths: ["TAA-0042"]), Service("erp-noprogram", programs: []),
            Service("erp-kraken", programs: ["KRAKEN"]), Service("erp-all", programs: ["ORCA", "KRAKEN"], cui: ["EXPT", "CTI", "PROPIN"]), Service("erp-nocui", cui: []),
        };
        foreach (var s in subjects)
        {
            var expected = markingsById.Where(kv => AccessPolicy.Evaluate(s, kv.Value, PlmAction.Read).Permitted).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
            var pulled = (await PullAllAsync(env, s)).Select(e => e["aggregate"]!["number"]!.GetValue<string>()).ToList();
            Assert.Equal(pulled.Count, pulled.Distinct().Count()); // no repeats
            Assert.Equal(expected, pulled.Order(StringComparer.Ordinal).ToList()); // exactly what the policy permits
            Assert.Equal(pulled.OrderBy(p => p, StringComparer.Ordinal).Select((_, _) => 0).Count(), expected.Count);
        }
        Assert.True((await PullAllAsync(env, subjects[5])).Count > (await PullAllAsync(env, subjects[1])).Count); // the subjects differ in what they see
        // Without the integration role (or any read role) nothing is returned and the denial is audited.
        var denied = await env.Queries.PullAsync(WorkflowEnv.User("nobody", []), null, 10, null, null, "corr");
        Assert.Equal(OutcomeKind.Forbidden, denied.Kind);
        Assert.Contains(await env.Config.AuditAsync(), a => a.Action == "integration.pull" && a.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task Events_in_pages_come_in_commit_order_and_an_open_transaction_holds_back_what_follows_it()
    {
        var env = await IntegrationEnv.CreateAsync(pg);
        var reader = Service("erp-us");
        await AppendAsync(env, Event("P-000001", ConfigEnv.Open));

        // A transaction that inserted an event but has not committed yet, and a later one that committed.
        await using var slowConn = await env.Ds.OpenConnectionAsync();
        await using var slow = await slowConn.BeginTransactionAsync();
        await env.Sink.AppendAsync(slowConn, slow, Event("P-000002", ConfigEnv.Open), CancellationToken.None);
        await AppendAsync(env, Event("P-000003", ConfigEnv.Open));

        // Neither the open transaction's event nor anything after it is visible: a cursor can never move past an event that will commit late.
        var visible = (await PullAllAsync(env, reader, 10)).Select(e => e["aggregate"]!["number"]!.GetValue<string>()).ToList();
        Assert.Equal(["P-000001"], visible);

        await slow.CommitAsync();
        var after = (await PullAllAsync(env, reader, 10)).Select(e => e["aggregate"]!["number"]!.GetValue<string>()).ToList();
        Assert.Equal(["P-000001", "P-000002", "P-000003"], after);

        // Resuming from a cursor returns only newer events.
        var first = await env.Queries.PullAsync(reader, null, 2, null, null, null);
        var rest = await env.Queries.PullAsync(reader, first.Value!.NextCursor, 10, null, null, null);
        Assert.Equal(["P-000003"], rest.Value!.Events.Select(e => e.Envelope["aggregate"]!["number"]!.GetValue<string>()));
        Assert.Equal(OutcomeKind.Invalid, (await env.Queries.PullAsync(reader, "garbage", 10, null, null, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Queries.PullAsync(reader, null, 10, "no.such.type", null, null)).Kind);
    }

    [Fact]
    public async Task The_administrator_outbox_view_shows_payloads_only_for_events_the_administrator_may_read()
    {
        var env = await IntegrationEnv.CreateAsync(pg);
        await AppendAsync(env, Event("P-000001", ConfigEnv.Open));
        await AppendAsync(env, Event("P-000002", ConfigEnv.Itar("KRAKEN"))); // Ada is on ORCA only
        var listed = await env.Queries.ListForAdminAsync(IntegrationEnv.Ada, null, null, null, 50, "corr");
        Assert.True(listed.IsOk);
        var open = listed.Value!.Single(i => i.Readable);
        Assert.Equal("P-000001", open.AggregateNumber);
        Assert.NotNull(open.Envelope);
        var restricted = listed.Value!.Single(i => !i.Readable);
        Assert.Null(restricted.AggregateNumber);
        Assert.Null(restricted.Envelope);
        Assert.StartsWith("CUI//EXPT", restricted.Banner, StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Queries.ListForAdminAsync(ConfigEnv.Alice, null, null, null, 50, null)).Kind);
        // A filter by aggregate number must not reveal that a restricted event exists.
        Assert.Empty((await env.Queries.ListForAdminAsync(IntegrationEnv.Ada, null, "P-000002", null, 50, null)).Value!);
    }
}

/// <summary>The REST surface through the real host: roles, ABAC, the BOM and change views, the OpenAPI document.</summary>
[Collection(PostgresCollection.Name)]
public sealed class IntegrationApiTests(PostgresFixture pg)
{
    private static string Integration(bool us = true, string[]? programs = null) => ApiFactory.Token("u-erp", us, roles: ["plm-integration"], programs: programs ?? ["ORCA"], cui: ["EXPT", "CTI"]);

    private static async Task SeedEventAsync(ApiFactory f, string number, Markings markings)
    {
        var sink = new OutboxEventSink(f.Services.GetRequiredService<IAuditLog>(), new IntegrationOptions(), TimeProvider.System);
        await using var conn = await f.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await sink.AppendAsync(conn, tx, new DomainEvent(EventTypes.PartReleased, "part", Guid.NewGuid(), number, "A", markings,
            new JsonObject { ["partType"] = "Component" }, DateTimeOffset.UtcNow, "alice"), CancellationToken.None);
        await tx.CommitAsync();
    }

    [Fact]
    public async Task Roles_gate_the_integration_endpoints_and_the_integration_role_opens_nothing_else()
    {
        using var f = await ApiFactory.CreateAsync(pg);
        await SeedEventAsync(f, "P-000001", ConfigEnv.Itar());

        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/v1/integration/events")).StatusCode);
        var viewer = f.ClientFor(ApiFactory.Token(roles: ["plm-viewer"]));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/integration/events")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.ClientFor(ApiFactory.Token(roles: ["plm-admin"])).GetAsync("/api/v1/integration/events")).StatusCode);

        var erp = f.ClientFor(Integration());
        var ok = await erp.GetAsync("/api/v1/integration/events?limit=10");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = JsonNode.Parse(await ok.Content.ReadAsStringAsync())!;
        Assert.Equal("P-000001", body["events"]![0]!["envelope"]!["aggregate"]!["number"]!.GetValue<string>());
        Assert.Equal("part.released", body["events"]![0]!["envelope"]!["type"]!.GetValue<string>());
        Assert.NotNull(body["nextCursor"]);

        // The service role grants no interactive access: parts, documents, admin and the UI-facing endpoints refuse it.
        foreach (var path in new[] { "/api/v1/parts", "/api/v1/documents", "/api/v1/me", "/api/v1/admin/integration/sinks", "/api/v1/tdp" })
            Assert.Equal(HttpStatusCode.Forbidden, (await erp.GetAsync(path)).StatusCode);
        // plm-integration cannot administer, modify or sign, and a human role is not admitted to the pull API.
        Assert.False(AccessPolicy.HasRoleFor(Service(), PlmAction.Administer));
        Assert.False(AccessPolicy.HasRoleFor(Service(), PlmAction.Modify));
        Assert.False(AccessPolicy.HasRoleFor(Service(), PlmAction.Download));
        Assert.DoesNotContain(Roles.Integration, Roles.All);
    }

    private static Subject Service() => WorkflowEnv.User("svc", [Roles.Integration]);

    [Fact]
    public async Task The_pull_applies_the_callers_markings_a_non_us_service_account_sees_no_itar_events()
    {
        using var f = await ApiFactory.CreateAsync(pg);
        await SeedEventAsync(f, "P-000001", ConfigEnv.Itar());
        await SeedEventAsync(f, "P-000002", ConfigEnv.Open);
        async Task<List<string>> Numbers(string token)
        {
            var body = JsonNode.Parse(await f.ClientFor(token).GetStringAsync("/api/v1/integration/events"))!;
            return [.. body["events"]!.AsArray().Select(e => e!["envelope"]!["aggregate"]!["number"]!.GetValue<string>())];
        }
        Assert.Equal(["P-000001", "P-000002"], await Numbers(Integration()));
        Assert.Equal(["P-000002"], await Numbers(Integration(us: false)));
        Assert.Equal(["P-000002"], await Numbers(Integration(programs: [])));
        var filtered = await f.ClientFor(Integration()).GetStringAsync("/api/v1/integration/events?type=part.obsoleted");
        Assert.Empty(JsonNode.Parse(filtered)!["events"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await f.ClientFor(Integration()).GetAsync("/api/v1/integration/events?cursor=zzz")).StatusCode);
    }

    [Fact]
    public async Task The_bom_snapshot_and_change_views_resolve_children_at_released_revisions_under_the_callers_markings()
    {
        using var f = await ApiFactory.CreateAsync(pg);
        var parts = f.Services.GetRequiredService<Keel.Parts.PartService>();
        var bom = f.Services.GetRequiredService<Keel.Bom.BomService>();
        var alice = WorkflowEnv.Alice;
        var ada = WorkflowEnv.Ada;
        async Task<string> Part(string name, string type, Markings m) => (await parts.CreateAsync(alice, new Keel.Parts.CreatePartCommand(name, type, "EA", "Make", m), null)).Value!.Number;
        var bolt = await Part("Bolt", "Component", ConfigEnv.Open);
        var asm = await Part("Pod", "Assembly", ConfigEnv.Open);
        Assert.True((await parts.CheckOutAsync(alice, asm, null)).IsOk);
        Assert.True((await bom.AddUsageAsync(alice, asm, new Keel.Bom.AddUsageCommand(bolt, 4m, null, 10, ["R1", "R2", "R3", "R4"]), null)).IsOk);
        Assert.True((await bom.AddUsageAsync(alice, asm, new Keel.Bom.AddUsageCommand(bolt, 1m, null, 20), null)).IsOk);
        Assert.True((await parts.CheckInAsync(alice, asm, "structure", null)).IsOk);
        foreach (var p in new[] { bolt, asm }) Assert.True((await parts.SetStateAsync(ada, p, "Released", "load", true, null)).IsOk);

        var erp = f.ClientFor(Integration());
        var snapshot = JsonNode.Parse(await erp.GetStringAsync($"/api/v1/integration/boms/{asm}?spec=LatestReleased"))!;
        Assert.Equal(asm, snapshot["number"]!.GetValue<string>());
        var lines = snapshot["lines"]!.AsArray();
        Assert.Equal(2, lines.Count);
        Assert.Equal(bolt, lines[0]!["number"]!.GetValue<string>());
        Assert.Equal("A.1", lines[0]!["iteration"]!.GetValue<string>());
        Assert.Equal(4m, lines[0]!["quantity"]!.GetValue<decimal>());
        Assert.Equal("EA", lines[0]!["unit"]!.GetValue<string>());
        Assert.Equal(10, lines[0]!["findNumber"]!.GetValue<int>());
        Assert.Equal(["R1", "R2", "R3", "R4"], lines[0]!["referenceDesignators"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.BadRequest, (await erp.GetAsync($"/api/v1/integration/boms/{asm}?spec=Effectivity")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await erp.GetAsync("/api/v1/integration/boms/P-999999")).StatusCode);
        // The view is computed under the caller's own markings; open data is visible to a non-US account without programs too.
        Assert.Equal(HttpStatusCode.OK, (await f.ClientFor(Integration(us: false, programs: [])).GetAsync($"/api/v1/integration/boms/{asm}")).StatusCode);

        var changes = f.Services.GetRequiredService<Keel.Change.ChangeService>();
        var ecn = await changes.CreateAsync(alice, new Keel.Change.CreateChangeCommand(Keel.Change.ChangeTypes.Ecn, "Revise pod", "Synthetic", "Medium", "Synthetic", ConfigEnv.Open, Keel.Change.ChangeClasses.II), null);
        Assert.True(ecn.IsOk, string.Join(";", ecn.Errors));
        Assert.True((await changes.AddAffectedAsync(alice, ecn.Value!.Number, new Keel.Change.AddAffectedCommand("part", asm, Keel.Change.ChangeActions.Revise, null, false), null)).IsOk);
        var view = JsonNode.Parse(await erp.GetStringAsync($"/api/v1/integration/changes/{ecn.Value.Number}"))!;
        Assert.Equal(asm, view["affected"]![0]!["number"]!.GetValue<string>());
        Assert.Equal(Keel.Change.ChangeActions.Revise, view["affected"]![0]!["action"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await erp.GetAsync("/api/v1/integration/changes/ECN-999999")).StatusCode);
    }

    [Fact]
    public async Task Administration_endpoints_need_the_administrator_role_and_tdp_export_without_a_signing_key_answers_503()
    {
        using var f = await ApiFactory.CreateAsync(pg);
        var admin = f.ClientFor(ApiFactory.Token(roles: ["plm-admin", "plm-viewer"]));
        var profiles = JsonNode.Parse(await admin.GetStringAsync("/api/v1/admin/integration/profiles"))!.AsArray();
        Assert.Contains(profiles, p => p!["name"]!.GetValue<string>() == "dod-contractor");
        Assert.Equal(HttpStatusCode.Forbidden, (await f.ClientFor(ApiFactory.Token(roles: ["plm-author"])).GetAsync("/api/v1/admin/integration/profiles")).StatusCode);
        var saved = await admin.PutAsJsonAsync("/api/v1/admin/integration/sinks", new
        {
            name = "bus", label = "Bus", kind = "broker", enabled = true, profileName = "dod-contractor", config = new { },
        });
        Assert.Equal(HttpStatusCode.NotImplemented, saved.StatusCode);
        Assert.Contains("Phase 4b", await saved.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var viewer = f.ClientFor(ApiFactory.Token(roles: ["plm-viewer", "plm-author"]));
        var export = await viewer.PostAsJsonAsync("/api/v1/tdp", new { objectType = "part", number = "P-000001", revision = (string?)null, recipientProfile = "dod-contractor" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, export.StatusCode);
    }

    [Fact]
    public async Task The_published_openapi_document_equals_the_committed_one_and_is_3_1()
    {
        using var f = await ApiFactory.CreateAsync(pg);
        var generated = await f.ClientFor(ApiFactory.Token(roles: ["plm-viewer"])).GetStringAsync("/openapi/v1.json");
        var json = JsonNode.Parse(generated)!;
        Assert.StartsWith("3.1", json["openapi"]!.GetValue<string>(), StringComparison.Ordinal);
        var paths = json["paths"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("/api/v1/integration/events", paths);
        Assert.Contains("/api/v1/integration/boms/{number}", paths);
        Assert.Contains("/api/v1/integration/mboms/{number}", paths);
        Assert.Contains("/api/v1/integration/changes/{number}", paths);
        Assert.Contains("/api/v1/tdp", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("/api/v1/parts", StringComparison.Ordinal)); // only the integration surface is published

        var file = Path.Combine(RepoRoot(), "docs", "integration", "openapi.json");
        var normalized = json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("KEEL_UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(file, normalized);
            return;
        }
        Assert.True(File.Exists(file), "docs/integration/openapi.json is missing: run scripts/update-openapi.sh");
        Assert.True(normalized == await File.ReadAllTextAsync(file),
            "docs/integration/openapi.json differs from the generated document. If the API change is intended, run scripts/update-openapi.sh and commit the result.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
