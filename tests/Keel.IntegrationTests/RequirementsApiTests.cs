using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Documents.Vault;

namespace Keel.IntegrationTests;

/// <summary>The REST surface of requirements through the real host: role gates, the edit and import flow, step-up for formal verification, restricted data and the published OpenAPI document (ADR-0038).</summary>
[Collection(PostgresCollection.Name)]
public sealed class RequirementsApiTests(PostgresFixture pg)
{
    private static string Author(string sub = "u-alice", string[]? programs = null) => ApiFactory.Token(sub, true, roles: ["plm-author", "plm-viewer"], programs: programs ?? ["ORCA"], cui: ["EXPT", "CTI"]);

    private async Task<ApiFactory> HostAsync()
    {
        var database = await pg.CreateDatabaseAsync();
        var dir = Directory.CreateTempSubdirectory("keel-test-");
        var path = Path.Combine(dir.FullName, "checkpoint.pem");
        using var key = CheckpointKeys.Generate();
        await File.WriteAllTextAsync(path, key.ExportPkcs8PrivateKeyPem());
        return new ApiFactory(database, path, new VaultOptions { ServiceUrl = "http://127.0.0.1:9", AccessKey = "unused", SecretKey = "unused" });
    }

    private static object Open => new { exportJurisdiction = "None", distributionStatement = "A", owningOrg = "ENG" };
    private static object Itar => new { exportJurisdiction = "Itar", distributionStatement = "D", owningOrg = "ENG", usmlCategory = "XI(d)", controllingOffice = "ACME", distributionReason = "Critical technology", program = "ORCA", cuiCategories = new[] { "EXPT" } };

    [Fact]
    public async Task Roles_gate_the_routes_and_the_edit_flow_works_end_to_end()
    {
        using var f = await HostAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/v1/requirements")).StatusCode);
        var viewer = f.ClientFor(ApiFactory.Token("u-vera", true, roles: ["plm-viewer"]));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/requirements", new { name = "x", markings = Open })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.ClientFor(ApiFactory.Token("u-sup", null, roles: ["plm-supplier"])).GetAsync("/api/v1/requirements")).StatusCode);

        var alice = f.ClientFor(Author());
        var created = await alice.PostAsJsonAsync("/api/v1/requirements", new { name = "Pod requirements", product = "ORCA pod", markings = Open });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var number = (await created.Content.ReadFromJsonAsync<JsonNode>())!["summary"]!["number"]!.GetValue<string>();

        var edit = await alice.PostAsJsonAsync($"/api/v1/requirements/{number}/edit", new
        {
            baseIteration = 1,
            ops = new[] { new { op = "add", key = (string?)null, input = new { title = "Depth", text = "Report depth.", type = "functional", priority = "high", method = "test", status = "approved" } } },
        });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal("R-0001", (await edit.Content.ReadFromJsonAsync<JsonNode>())!["createdKeys"]![0]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync($"/api/v1/requirements/{number}/edit", new { baseIteration = 1, ops = new[] { new { op = "add", key = (string?)null, input = new { title = "x", text = "y", type = "functional", priority = "low", method = "test", status = "draft" } } } })).StatusCode);

        var list = (await alice.GetFromJsonAsync<JsonNode>($"/api/v1/requirements/{number}/requirements"))!;
        Assert.Equal("Depth", list["rows"]![0]!["title"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/v1/requirements/RS-999999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/api/v1/requirements?sort=nope")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/v1/requirements?sort=name&dir=desc")).StatusCode);

        var link = await alice.PostAsJsonAsync($"/api/v1/requirements/{number}/requirements/R-0001/links", new { linkType = "changed-by", targetNumber = "ECN-000001" });
        Assert.Equal(HttpStatusCode.BadRequest, link.StatusCode); // no such change
        var coverage = (await alice.GetFromJsonAsync<JsonNode>($"/api/v1/requirements/{number}/coverage"))!;
        Assert.Equal(1, coverage["summary"]!["total"]!.GetValue<int>());
        var links = await alice.GetAsync("/api/v1/requirements/link-types");
        Assert.Equal(5, (await links.Content.ReadFromJsonAsync<JsonNode>())!.AsArray().Count);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync("/api/v1/requirements/link-types/tested-on", new { label = "a", reverseLabel = "b", targetKind = "part" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsync("/api/v1/requirements/suspect-links/sweep", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.ClientFor(ApiFactory.Token("u-ada", true, roles: ["plm-admin", "plm-viewer"], programs: ["ORCA"])).PostAsync("/api/v1/requirements/suspect-links/sweep", null)).StatusCode);
    }

    [Fact]
    public async Task A_restricted_requirement_is_a_placeholder_and_a_restricted_specification_is_not_found()
    {
        using var f = await HostAsync();
        var alice = f.ClientFor(Author());
        var eve = f.ClientFor(Author("u-eve", programs: []));
        var open = (await (await alice.PostAsJsonAsync("/api/v1/requirements", new { name = "Open set", markings = Open })).Content.ReadFromJsonAsync<JsonNode>())!["summary"]!["number"]!.GetValue<string>();
        var restricted = (await (await alice.PostAsJsonAsync("/api/v1/requirements", new { name = "Restricted set", markings = Itar })).Content.ReadFromJsonAsync<JsonNode>())!["summary"]!["number"]!.GetValue<string>();
        var add = await alice.PostAsJsonAsync($"/api/v1/requirements/{open}/edit", new
        {
            baseIteration = 1,
            ops = new[] { new { op = "add", key = (string?)null, input = new { title = "Secret detail", text = "Marker-secret-text.", type = "safety", priority = "high", method = "test", status = "approved", markings = Itar } } },
        });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);

        var rows = (await eve.GetFromJsonAsync<JsonNode>($"/api/v1/requirements/{open}/requirements"))!["rows"]!.AsArray();
        Assert.Single(rows);
        Assert.True(rows[0]!["redacted"]!.GetValue<bool>());
        Assert.DoesNotContain("Secret", rows.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"/api/v1/requirements/{restricted}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"/api/v1/requirements/{open}/requirements/R-0001")).StatusCode);
        var page = (await eve.GetFromJsonAsync<JsonNode>("/api/v1/requirements"))!;
        Assert.DoesNotContain(restricted, page.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"/api/v1/requirements/{open}/requirements/R-0001/links")).StatusCode);
    }

    [Fact]
    public async Task Import_previews_then_applies_and_export_carries_the_banner_and_formal_verification_needs_step_up()
    {
        using var f = await HostAsync();
        var alice = f.ClientFor(Author());
        var number = (await (await alice.PostAsJsonAsync("/api/v1/requirements", new { name = "Import set", markings = Open })).Content.ReadFromJsonAsync<JsonNode>())!["summary"]!["number"]!.GetValue<string>();
        var csv = "title,text,status\nOne,The first.,approved\nTwo,The second.,approved\n";
        HttpContent Body() => new StringContent(csv, Encoding.UTF8, "text/csv");
        var preview = (await (await alice.PostAsync($"/api/v1/requirements/{number}/import?format=csv", Body())).Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.Equal(2, preview["created"]!.GetValue<int>());
        Assert.False(preview["applied"]!.GetValue<bool>());
        var digest = preview["digest"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsync($"/api/v1/requirements/{number}/import?format=csv&dryRun=false&digest=bad", Body())).StatusCode);
        var applied = await alice.PostAsync($"/api/v1/requirements/{number}/import?format=csv&dryRun=false&digest={digest}", Body());
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.True((await applied.Content.ReadFromJsonAsync<JsonNode>())!["applied"]!.GetValue<bool>());

        var export = await alice.GetAsync($"/api/v1/requirements/{number}/export?format=csv");
        Assert.Equal("text/csv", export.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("# ", await export.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/v1/requirements/{number}/export?format=reqif")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/v1/requirements/{number}/export?format=docx")).StatusCode);

        // Formal verification: a token whose authentication is old gets the RFC 9470 challenge.
        var stale = f.ClientFor(ApiFactory.Token("u-bob", true, roles: ["plm-approver", "plm-viewer"], programs: ["ORCA"], cui: ["EXPT"], authTime: DateTimeOffset.UtcNow - TimeSpan.FromHours(2)));
        var formal = await stale.PostAsJsonAsync($"/api/v1/requirements/{number}/requirements/R-0001/verifications", new { result = "pass", formal = true, signatureMeaning = "Verified", evidenceDocuments = new[] { "DOC-000001" } });
        Assert.Equal(HttpStatusCode.Unauthorized, formal.StatusCode);
        Assert.Contains("insufficient_user_authentication", string.Join(' ', formal.Headers.WwwAuthenticate.Select(h => h.ToString())), StringComparison.Ordinal);
        var informal = await alice.PostAsJsonAsync($"/api/v1/requirements/{number}/requirements/R-0001/verifications", new { result = "pass" });
        Assert.Equal(HttpStatusCode.Created, informal.StatusCode);
        Assert.Equal("verified", (await alice.GetFromJsonAsync<JsonNode>($"/api/v1/requirements/{number}/requirements/R-0001/verifications"))!["state"]!.GetValue<string>());
        _ = new AuthenticationHeaderValue("Bearer");
    }

    [Fact]
    public async Task The_published_requirements_openapi_document_equals_the_committed_one()
    {
        using var f = await HostAsync();
        var generated = await f.ClientFor(Author()).GetStringAsync("/openapi/requirements.json");
        var json = JsonNode.Parse(generated)!;
        Assert.StartsWith("3.1", json["openapi"]!.GetValue<string>(), StringComparison.Ordinal);
        var paths = json["paths"]!.AsObject().Select(p => p.Key).ToList();
        foreach (var p in new[] { "/api/v1/requirements", "/api/v1/requirements/{number}/edit", "/api/v1/requirements/{number}/coverage", "/api/v1/requirements/{number}/import", "/api/v1/requirements/trace/{kind}/{key}" })
            Assert.Contains(p, paths);
        Assert.DoesNotContain(paths, p => !p.StartsWith("/api/v1/requirements", StringComparison.Ordinal));
        var v1 = JsonNode.Parse(await f.ClientFor(Author()).GetStringAsync("/openapi/v1.json"))!["paths"]!.AsObject().Select(p => p.Key);
        Assert.DoesNotContain(v1, p => p.Contains("/requirements", StringComparison.Ordinal));

        var file = Path.Combine(RepoRoot(), "docs", "requirements", "openapi.json");
        var normalized = json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("KEEL_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, normalized);
            return;
        }
        Assert.True(File.Exists(file), "docs/requirements/openapi.json is missing: run scripts/update-openapi.sh");
        Assert.True(normalized == await File.ReadAllTextAsync(file), "docs/requirements/openapi.json differs from the generated document. If the API change is intended, run scripts/update-openapi.sh and commit the result.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
