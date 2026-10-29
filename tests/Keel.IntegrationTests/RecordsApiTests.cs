using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Vault;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.IntegrationTests;

/// <summary>The REST surface of records management through the real host: role gates, a flow over HTTP and the published OpenAPI document (ADR-0041).</summary>
[Collection(PostgresCollection.Name)]
public sealed class RecordsApiTests(PostgresFixture pg)
{
    private static string Token(string name, params string[] roles) => ApiFactory.Token("u-" + name, true, roles: roles, programs: ["ORCA"], cui: ["EXPT", "CTI"]);

    private async Task<ApiFactory> HostAsync()
    {
        var database = await pg.CreateDatabaseAsync();
        var dir = Directory.CreateTempSubdirectory("keel-test-");
        var path = Path.Combine(dir.FullName, "checkpoint.pem");
        using var key = CheckpointKeys.Generate();
        await File.WriteAllTextAsync(path, key.ExportPkcs8PrivateKeyPem());
        return new ApiFactory(database, path, new VaultOptions { ServiceUrl = "http://127.0.0.1:9", AccessKey = "unused", SecretKey = "unused" });
    }

    [Fact]
    public async Task Roles_gate_every_route_and_records_data_is_not_readable_without_a_records_role()
    {
        using var f = await HostAsync();
        var routes = new[] { "/api/v1/records/schedules", "/api/v1/records/holds", "/api/v1/records/eligible", "/api/v1/records/dispositions", "/api/v1/records/certificates", "/api/v1/records/archives", "/api/v1/records/program-events" };
        foreach (var route in routes) Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync(route)).StatusCode);

        foreach (var token in new[] { Token("viewer", "plm-viewer"), Token("author", "plm-viewer", "plm-author"), Token("approver", "plm-viewer", "plm-approver"), ApiFactory.Token("u-sup", null, roles: ["plm-supplier"]), ApiFactory.Token("u-erp", true, roles: ["plm-integration"]) })
        {
            var c = f.ClientFor(token);
            foreach (var route in routes) Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync(route)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/v1/records/holds", new { })).StatusCode);
        }

        var auditor = f.ClientFor(Token("audrey", "plm-auditor"));
        foreach (var route in routes) Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsJsonAsync("/api/v1/records/schedules", new { title = "x" })).StatusCode); // read only

        var admin = f.ClientFor(Token("ada", "plm-admin", "plm-viewer"));
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/records/schedules", new { title = "By an administrator" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync("/api/v1/records/dispositions/DR-000001/execute", null)).StatusCode); // an administrator alone cannot destroy records
        var audit = await new AuditQueries(f.Services.GetRequiredService<Npgsql.NpgsqlDataSource>()).ListAsync(new AuditQuery(Limit: 500));
        Assert.Contains(audit, e => e.Action == "records.disposition_execute" && e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task A_records_manager_works_through_schedules_holds_and_the_queue_over_HTTP()
    {
        using var f = await HostAsync();
        var docs = f.Services.GetRequiredService<DocumentService>();
        var alice = WorkflowEnv.Alice;
        var created = await docs.CreateAsync(alice, new CreateDocumentCommand("Over HTTP", "Specification", WorkflowEnv.Open, "d"), null, null);
        var number = created.Value!.Number;

        var rex = f.ClientFor(Token("rex", "plm-viewer", "plm-author", "plm-records"));
        var draft = await rex.PostAsJsonAsync("/api/v1/records/schedules", new { title = "HTTP schedule" });
        Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        var version = JsonNode.Parse(await draft.Content.ReadAsStringAsync())!["version"]!.GetValue<int>();
        var cls = new { code = "SPEC", name = "Specifications", priority = 100, kinds = new[] { "document" }, types = Array.Empty<string>(), programs = Array.Empty<string>(), jurisdictions = Array.Empty<string>(), cui = Array.Empty<string>(), statements = Array.Empty<string>(), retentionMonths = 0, triggerEvent = "release", disposition = "destroy", citation = "Policy", notes = (string?)null };
        Assert.Equal(HttpStatusCode.OK, (await rex.PutAsJsonAsync($"/api/v1/records/schedules/{version}/classes", cls)).StatusCode);
        var bad = await rex.PutAsJsonAsync($"/api/v1/records/schedules/{version}/classes", cls with { code = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var got = JsonNode.Parse(await rex.GetStringAsync($"/api/v1/records/schedules/{version}"))!;
        Assert.Equal("SPEC", got["classes"]![0]!["input"]!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await rex.GetAsync("/api/v1/records/schedules/published")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rex.GetAsync("/api/v1/records/schedules/99")).StatusCode);

        var hold = await rex.PostAsJsonAsync("/api/v1/records/holds", new { name = "HTTP matter", reason = "Litigation", scope = new { kind = "objects", objects = new[] { new { kind = "document", number } } } });
        Assert.Equal(HttpStatusCode.Created, hold.StatusCode);
        var holdNumber = JsonNode.Parse(await hold.Content.ReadAsStringAsync())!["number"]!.GetValue<string>();
        var members = JsonNode.Parse(await rex.GetStringAsync($"/api/v1/records/holds/{holdNumber}/members"))!.AsArray();
        Assert.Equal(number, members.Single()!["number"]!.GetValue<string>());
        var status = JsonNode.Parse(await rex.GetStringAsync($"/api/v1/records/objects/document/{number}/status"))!;
        Assert.True(status["held"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NotFound, (await rex.GetAsync("/api/v1/records/objects/document/D-999999/status")).StatusCode);
        var invalidHold = await rex.PostAsJsonAsync("/api/v1/records/holds", new { name = "", reason = "", scope = new { kind = "objects", objects = Array.Empty<object>() } });
        Assert.Equal(HttpStatusCode.BadRequest, invalidHold.StatusCode);

        // The hold is invisible to everyone without a records role, and to a records user outside the data's program the object stays invisible.
        Assert.Equal(HttpStatusCode.Forbidden, (await f.ClientFor(Token("alice", "plm-viewer", "plm-author")).GetAsync($"/api/v1/records/holds/{holdNumber}")).StatusCode);
        var eve = f.ClientFor(ApiFactory.Token("u-eve", true, roles: ["plm-viewer", "plm-records"], programs: [], cui: ["EXPT"]));
        Assert.Equal(HttpStatusCode.OK, (await eve.GetAsync($"/api/v1/records/holds/{holdNumber}")).StatusCode);
        var queue = JsonNode.Parse(await rex.GetStringAsync("/api/v1/records/eligible"))!;
        Assert.Equal(0, queue["total"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest, (await rex.GetAsync("/api/v1/records/eligible?kind=supplier")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rex.PostAsJsonAsync("/api/v1/records/archives", new { title = "t", items = new[] { new { kind = "document", number } } })).StatusCode); // nothing released to archive yet
    }

    [Fact]
    public async Task The_published_records_openapi_document_equals_the_committed_one_and_lists_no_other_surface()
    {
        using var f = await HostAsync();
        var generated = await f.ClientFor(Token("rex", "plm-viewer")).GetStringAsync("/openapi/records.json");
        var json = JsonNode.Parse(generated)!;
        Assert.StartsWith("3.1", json["openapi"]!.GetValue<string>(), StringComparison.Ordinal);
        var paths = json["paths"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("/api/v1/records/schedules", paths);
        Assert.Contains("/api/v1/records/holds/{number}/members", paths);
        Assert.Contains("/api/v1/records/dispositions/{number}/execute", paths);
        Assert.Contains("/api/v1/records/archives/{number}/contents", paths);
        Assert.All(paths, p => Assert.StartsWith("/api/v1/records", p, StringComparison.Ordinal));
        foreach (var other in new[] { "v1", "sourcing", "cad" })
        {
            var doc = JsonNode.Parse(await f.ClientFor(Token("rex", "plm-viewer")).GetStringAsync($"/openapi/{other}.json"))!["paths"]!.AsObject().Select(p => p.Key);
            Assert.DoesNotContain(doc, p => p.Contains("/records", StringComparison.Ordinal)); // the other documents are unchanged
        }

        var file = Path.Combine(RepoRoot(), "docs", "records", "openapi.json");
        var normalized = json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("KEEL_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, normalized);
            return;
        }
        Assert.True(File.Exists(file), "docs/records/openapi.json is missing: run scripts/update-openapi.sh");
        Assert.True(normalized == await File.ReadAllTextAsync(file),
            "docs/records/openapi.json differs from the generated document. If the API change is intended, run scripts/update-openapi.sh and commit the result.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
