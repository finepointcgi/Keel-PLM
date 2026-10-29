using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Bom;
using Keel.Compliance;
using Keel.Core;
using Keel.Documents.Vault;
using Keel.Parts;
using Keel.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using static Keel.IntegrationTests.BomTestKit;

namespace Keel.IntegrationTests;

/// <summary>Product compliance through the real host (role gates, the REST surface, exports, the published OpenAPI document) and through the real BOM read path (ADR-0040).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ComplianceApiTests(PostgresFixture pg)
{
    private static string Supplier(string name) => ApiFactory.Token("u-" + name, null, roles: ["plm-supplier"], programs: [], cui: []);
    private static string Internal(params string[] roles) => ApiFactory.Token("u-alice", true, roles: roles, programs: ["ORCA"], cui: ["EXPT", "CTI"]);

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
    public async Task Roles_gate_every_route_and_a_viewer_reads_but_does_not_change()
    {
        using var f = await HostAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/v1/compliance/lists")).StatusCode);
        var supplier = f.ClientFor(Supplier("sup1"));
        foreach (var route in new[] { "/api/v1/compliance/lists", "/api/v1/compliance/substances", "/api/v1/compliance/materials", "/api/v1/compliance/declarations", "/api/v1/compliance/parts/P-000001",
                     "/api/v1/compliance/rollup?mode=ebom&number=P-000001", "/api/v1/compliance/summary", "/api/v1/compliance/regulations" })
            Assert.Equal(HttpStatusCode.Forbidden, (await supplier.GetAsync(route)).StatusCode);

        var viewer = f.ClientFor(Internal("plm-viewer"));
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/compliance/lists")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/compliance/substances?mineral=gold")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/v1/compliance/substances?sort=password")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/v1/compliance/declarations?sort=password")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/v1/compliance/rollup?mode=ebom&number=P-000001&versions=garbage")).StatusCode);
        var denied = await viewer.PostAsJsonAsync("/api/v1/compliance/substances", new { casNumber = "7782-49-2", name = "Selenium" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/compliance/lists/rohs/versions", new { effectiveFrom = "2030-01-01" })).StatusCode);
        var admin = f.ClientFor(Internal("plm-admin", "plm-viewer"));
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/compliance/substances", new { casNumber = "7782-49-2", name = "Selenium" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/v1/compliance/substances", new { casNumber = "7782-49-2", name = "Selenium" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/compliance/substances", new { casNumber = "7782-49-3", name = "Bad" })).StatusCode);
    }

    [Fact]
    public async Task A_part_is_declared_evaluated_exported_and_audited_over_HTTP()
    {
        using var f = await HostAsync();
        var parts = f.Services.GetRequiredService<PartService>();
        var alice = SourcingEnv.Alice;
        await f.Services.GetRequiredService<Keel.Workflow.UserDirectory>().UpsertAsync(alice with { Id = "u-alice" });
        var part = (await parts.CreateAsync(alice, new CreatePartCommand("Terminal block", "Component", "EA", "Buy", SourcingEnv.Open, "d", new JsonObject()), null)).Value!.Number;

        var author = f.ClientFor(Internal("plm-viewer", "plm-author"));
        var body = new
        {
            partNumber = part,
            declaration = new
            {
                declaredOn = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), expiresOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(90).ToString("yyyy-MM-dd"),
                source = "supplier", supplierRef = "SUP-000009",
                lines = new object[] { new { substanceCas = "7439-92-1", percent = 0.5 }, new { substanceCas = "7440-57-5", percent = 0.2, conflictFree = true } },
            },
        };
        var created = await author.PostAsJsonAsync("/api/v1/compliance/declarations", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var detail = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        var number = detail["summary"]!["number"]!.GetValue<string>();
        Assert.Equal("non_compliant", detail["summary"]!["status"]!.GetValue<string>());

        // The part view, the search and the summary agree.
        var view = JsonNode.Parse(await author.GetStringAsync($"/api/v1/compliance/parts/{part}"))!;
        Assert.Equal("non_compliant", view["regulations"]!.AsArray().Single(r => r!["listCode"]!.GetValue<string>() == "rohs")!["standing"]!.GetValue<string>());
        Assert.Equal(1, JsonNode.Parse(await author.GetStringAsync("/api/v1/compliance/declarations?status=non_compliant"))!["total"]!.GetValue<int>());
        Assert.Equal(1, JsonNode.Parse(await author.GetStringAsync("/api/v1/compliance/summary"))!["nonCompliant"]!.GetValue<int>());
        // The author cannot verify; an approver can.
        Assert.Equal(HttpStatusCode.Forbidden, (await author.PostAsync($"/api/v1/compliance/declarations/{number}/verify", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.ClientFor(Internal("plm-viewer", "plm-approver")).PostAsync($"/api/v1/compliance/declarations/{number}/verify", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync("/api/v1/compliance/declarations/CD-999999")).StatusCode);

        // The roll-up of the part itself (a structure with no lines is the part) and its two exports.
        var rollup = JsonNode.Parse(await author.GetStringAsync($"/api/v1/compliance/rollup?mode=ebom&number={part}"))!;
        Assert.Equal("non_compliant", rollup["summaries"]!.AsArray().Single(s => s!["code"]!.GetValue<string>() == "rohs")!["standing"]!.GetValue<string>());
        var csv = await author.GetAsync($"/api/v1/compliance/rollup/export?mode=ebom&number={part}&format=cmrt");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Contains("text/csv", csv.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains(part, text, StringComparison.Ordinal);
        Assert.Contains("SUP-000009", text, StringComparison.Ordinal);
        var xml = await author.GetAsync($"/api/v1/compliance/rollup/export?mode=ebom&number={part}&format=ipc1752");
        Assert.Equal(HttpStatusCode.OK, xml.StatusCode);
        System.Xml.Linq.XDocument.Parse(await xml.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await author.GetAsync($"/api/v1/compliance/rollup/export?mode=ebom&number={part}&format=pdf")).StatusCode);
        // The same file through the cookie-authenticated download route needs a session; a bearer token alone is a plain API call.
        Assert.Equal(HttpStatusCode.OK, (await author.GetAsync($"/downloads/compliance/export?mode=ebom&number={part}&format=cmrt")).StatusCode);

        var audit = await new AuditQueries(f.Services.GetRequiredService<Npgsql.NpgsqlDataSource>()).ListAsync(new AuditQuery(Limit: 500));
        Assert.Contains(audit, e => e.Action == "compliance.declaration_create" && e.ObjectId == part);
        Assert.Contains(audit, e => e.Action == "compliance.export" && e.ObjectId == part);
        Assert.Contains(audit, e => e.Action == "compliance.declaration_verify" && e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task The_rollup_walks_the_real_bill_of_materials_and_a_part_the_reader_cannot_see_changes_nothing()
    {
        var env = await Setup(pg);
        var audit = new PostgresAuditLog(env.Ds, TimeProvider.System);
        var declarations = new DeclarationService(env.Ds, audit, TimeProvider.System);
        var reports = new ComplianceReportService(env.Ds, audit, TimeProvider.System);
        var rollup = new ComplianceRollupService(env.Bom, new MbomService(env.Ds, audit, TimeProvider.System), reports);

        var good = await env.CreatePart(Alice, "Good washer");
        var gap = await env.CreatePart(Alice, "Undeclared nut");
        var secret = await env.CreatePart(Alice, "Secret rotor", markings: Itar());
        var sub = await env.Assembly(Alice, "Sub assembly", null, (good, 2), (gap, 4));
        var top = await env.Assembly(Alice, "Top assembly", null, (sub, 1), (secret, 1));
        DeclarationInput Input(params DeclarationLineInput[] lines) => new(null, "internal", null, null, null, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(60), lines, null);
        Assert.True((await declarations.CreateAsync(Alice, new CreateDeclarationCommand(good, Input(new DeclarationLineInput("7440-31-5", null, 1, null, null, true))), null)).IsOk);
        Assert.True((await declarations.CreateAsync(Alice, new CreateDeclarationCommand(secret, Input(new DeclarationLineInput("7439-97-6", null, 2, null))), null)).IsOk); // mercury: non-compliant

        // Alice reads everything: the secret rotor makes the assembly non-compliant, the undeclared nut is a gap, the sub assembly is not a part to declare.
        var full = (await rollup.ReportAsync(Alice, "ebom", top, null, null, null)).Value!;
        Assert.Equal(new[] { "Top assembly", "Sub assembly", "Good washer", "Undeclared nut", "Secret rotor" }.Order(), full.Lines.Select(l => l.Name).Order());
        var rohs = full.Summaries.Single(s => s.Code == "rohs");
        Assert.Equal(Standings.AssemblyNonCompliant, rohs.Standing);
        Assert.Equal((1, 1, 1), (rohs.Compliant, rohs.NonCompliant, rohs.Gaps));
        Assert.Equal(2, rohs.NotRequired); // the top and the sub assembly
        Assert.Equal([secret, gap], rohs.Causes.Select(c => c.Number));
        Assert.Contains(full.Lines, l => l.Number == sub && !l.Required);

        // Carmen is not on ORCA: the BOM redacts the rotor, so the roll-up has no such line, no count, no cause: the assembly is incomplete (the nut) but not non-compliant.
        var seen = (await rollup.ReportAsync(Carmen, "ebom", top, null, null, null)).Value!;
        Assert.DoesNotContain(seen.Lines, l => l.Number == secret || l.Name == "Secret rotor");
        var carmen = seen.Summaries.Single(s => s.Code == "rohs");
        Assert.Equal(Standings.AssemblyIncomplete, carmen.Standing);
        Assert.Equal((1, 0, 1), (carmen.Compliant, carmen.NonCompliant, carmen.Gaps));
        Assert.Equal([gap], carmen.Causes.Select(c => c.Number));
        Assert.DoesNotContain(secret, string.Join(' ', seen.Summaries.SelectMany(s => s.Causes).SelectMany(c => c.Findings.Select(x => x.DeclarationNumber + x.SubstanceName))), StringComparison.Ordinal);
        // An unknown structure and a part the reader cannot read are not found; a bad mode is invalid.
        Assert.Equal(OutcomeKind.NotFound, (await rollup.ReportAsync(Alice, "ebom", "P-999999", null, null, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await rollup.ReportAsync(Carmen, "ebom", secret, null, null, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await rollup.ReportAsync(Alice, "xbom", top, null, null, null)).Kind);
    }

    [Fact]
    public async Task The_published_compliance_openapi_document_equals_the_committed_one_and_lists_no_other_surface()
    {
        using var f = await HostAsync();
        var generated = await f.ClientFor(Internal("plm-viewer")).GetStringAsync("/openapi/compliance.json");
        var json = JsonNode.Parse(generated)!;
        Assert.StartsWith("3.1", json["openapi"]!.GetValue<string>(), StringComparison.Ordinal);
        var paths = json["paths"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("/api/v1/compliance/declarations", paths);
        Assert.Contains("/api/v1/compliance/lists/{code}/versions/{version}", paths);
        Assert.Contains("/api/v1/compliance/rollup", paths);
        Assert.Contains("/api/v1/compliance/rollup/export", paths);
        Assert.All(paths, p => Assert.StartsWith("/api/v1/compliance", p, StringComparison.Ordinal));
        foreach (var other in new[] { "v1", "sourcing" })
            Assert.DoesNotContain(JsonNode.Parse(await f.ClientFor(Internal("plm-viewer")).GetStringAsync($"/openapi/{other}.json"))!["paths"]!.AsObject().Select(p => p.Key), p => p.Contains("/compliance", StringComparison.Ordinal));

        var file = Path.Combine(RepoRoot(), "docs", "compliance", "openapi.json");
        var normalized = json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("KEEL_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, normalized);
            return;
        }
        Assert.True(File.Exists(file), "docs/compliance/openapi.json is missing: run scripts/update-openapi.sh");
        Assert.True(normalized == await File.ReadAllTextAsync(file),
            "docs/compliance/openapi.json differs from the generated document. If the API change is intended, run scripts/update-openapi.sh and commit the result.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
