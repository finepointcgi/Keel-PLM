using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Keel.IntegrationTests;

/// <summary>The quality REST API through the real host with locally signed tokens: role gates, access mapping, error codes, lists and the published OpenAPI document (8.1, ADR-0036).</summary>
[Collection(PostgresCollection.Name)]
public sealed class QualityApiTests(PostgresFixture pg)
{
    private static string Token(string name, string[] roles, bool us = true, string[]? programs = null) =>
        ApiFactory.Token(sub: "u-" + name, roles: roles, usPerson: us, programs: programs, cui: ["EXPT", "CTI"]);

    private static readonly string[] Author = ["plm-viewer", "plm-author"];
    private static readonly string[] Approver = ["plm-viewer", "plm-approver"];

    private static readonly object Itar = new
    {
        exportJurisdiction = "Itar", distributionStatement = "D", owningOrg = "ENG", usmlCategory = "XI(d)", cuiCategories = new[] { "EXPT" },
        controllingOffice = "ACME", distributionReason = "Critical technology", program = "ORCA",
    };

    private static async Task<JsonElement> Json(HttpResponseMessage res) => JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> CreatePartAsync(HttpClient alice)
    {
        var created = await alice.PostAsJsonAsync("/api/v1/parts", new { name = "Bracket, sensor", partType = "Component", defaultUnit = "EA", source = "Make", description = "Synthetic", attributes = new { material = "6061-T6" }, markings = Itar });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await Json(created)).GetProperty("number").GetString()!;
    }

    [Fact]
    public async Task Roles_and_markings_gate_the_quality_endpoints_and_a_refusal_never_names_the_record()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Token("alice", Author));
        var eve = api.ClientFor(Token("eve", Author, programs: []));
        var viewer = api.ClientFor(Token("dan", ["plm-viewer"]));
        var bobToken = Token("bob", Approver);
        Assert.Equal(HttpStatusCode.OK, (await api.ClientFor(bobToken).GetAsync("/api/v1/tasks")).StatusCode); // bob signs in once so he can own an action

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/quality/ncrs")).StatusCode);
        var part = await CreatePartAsync(alice);

        var body = new { title = "Burr on mounting face", description = "Synthetic defect", severity = "Major", stage = "Receiving", partNumber = part, lotNumber = "L-7", quantity = 5, unit = "EA", supplierRef = "SUP-1", markings = Itar };
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/quality/ncrs", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/v1/quality/ncrs", new { body.title, body.severity, body.stage, partNumber = part, quantity = 0, markings = Itar })).StatusCode);
        var created = await alice.PostAsJsonAsync("/api/v1/quality/ncrs", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ncr = await Json(created);
        var number = ncr.GetProperty("number").GetString()!;
        Assert.Equal("NCR-000001", number);
        Assert.Equal("Open", ncr.GetProperty("state").GetString());

        // a user outside the program: 404 on the record, nothing in the list, no counts
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"/api/v1/quality/ncrs/{number}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync("/api/v1/quality/ncrs/NCR-999999")).StatusCode);
        var eveList = await Json(await eve.GetAsync("/api/v1/quality/ncrs"));
        Assert.Equal(0, eveList.GetProperty("total").GetInt32());
        Assert.Equal(0, (await Json(await eve.GetAsync("/api/v1/quality/dashboard"))).GetProperty("openNcrs").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"/api/v1/quality/parts/{part}")).StatusCode);

        // the owner sees it everywhere
        var list = await Json(await alice.GetAsync("/api/v1/quality/ncrs?open=true&q=burr"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(1, (await Json(await alice.GetAsync("/api/v1/quality/dashboard"))).GetProperty("openNcrs").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/api/v1/quality/ncrs?sort=part_number%3B--")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/v1/quality/ncrs?sort=severity&dir=desc")).StatusCode);

        // disposition, containment action with a notification-eligible owner, links
        Assert.Equal(HttpStatusCode.OK, (await alice.PutAsJsonAsync($"/api/v1/quality/ncrs/{number}/disposition", new { disposition = "Scrap", rationale = "Beyond repair" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PutAsJsonAsync($"/api/v1/quality/ncrs/{number}/disposition", new { disposition = "Burn", rationale = "x" })).StatusCode);
        var action = await alice.PostAsJsonAsync($"/api/v1/quality/ncrs/{number}/actions", new { actionType = "Containment", description = "Quarantine the lot", owner = "bob" });
        Assert.Equal(HttpStatusCode.Created, action.StatusCode);
        var mine = await Json(await api.ClientFor(bobToken).GetAsync("/api/v1/quality/my-actions"));
        Assert.Equal(1, mine.GetArrayLength());
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync($"/api/v1/quality/ncrs/{number}/links", new { toKind = "part", toKey = part })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync($"/api/v1/quality/ncrs/{number}/links", new { toKind = "part", toKey = "P-999999" })).StatusCode);

        // a CAPA and a deviation with scope over the same part
        var capa = await alice.PostAsJsonAsync("/api/v1/quality/capas", new { title = "Recurring burr", capaType = "Corrective", source = "Ncr", priority = "High", markings = Itar });
        Assert.Equal(HttpStatusCode.Created, capa.StatusCode);
        var today = DateTime.UtcNow.Date;
        var dev = await alice.PostAsJsonAsync("/api/v1/quality/deviations", new { kind = "Waiver", title = "Alternate finish", description = "Why", validFrom = DateOnly.FromDateTime(today), validTo = DateOnly.FromDateTime(today.AddDays(30)), customerApprovalRequired = false, markings = Itar });
        Assert.Equal(HttpStatusCode.Created, dev.StatusCode);
        var devNumber = (await Json(dev)).GetProperty("number").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync($"/api/v1/quality/deviations/{devNumber}/scope", new { partNumber = part, serialFrom = 1, serialTo = 50 })).StatusCode);
        var summary = await Json(await alice.GetAsync($"/api/v1/quality/parts/{part}?all=true"));
        Assert.Equal(1, summary.GetProperty("deviations").GetArrayLength());
        Assert.Equal(1, summary.GetProperty("openNcrs").GetArrayLength());
        var applicable = await Json(await alice.GetAsync($"/api/v1/quality/parts/{part}/applicable?serial=10"));
        Assert.Equal(0, applicable.GetArrayLength()); // not approved yet
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"/api/v1/quality/deviations/{devNumber}")).StatusCode);

        // the review is the workflow engine's
        var review = await alice.PostAsJsonAsync($"/api/v1/quality/deviations/{devNumber}/review", new { participants = new { approvers = new[] { "bob" } } });
        Assert.Equal(HttpStatusCode.Created, review.StatusCode);
        var status = await Json(await alice.GetAsync($"/api/v1/quality/deviations/{devNumber}/workflow"));
        Assert.Equal("Active", status.GetProperty("active").GetProperty("status").GetString());
        var tasks = await Json(await api.ClientFor(bobToken).GetAsync("/api/v1/tasks"));
        var taskId = tasks.GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await api.ClientFor(bobToken).PostAsJsonAsync($"/api/v1/tasks/{taskId}/sign", new { reasonCode = "Approved" })).StatusCode);
        var approved = await Json(await alice.GetAsync($"/api/v1/quality/deviations/{devNumber}"));
        Assert.Equal("Approved", approved.GetProperty("state").GetString());
        Assert.Equal(1, (await Json(await alice.GetAsync($"/api/v1/quality/parts/{part}/applicable?serial=10"))).GetArrayLength());
        Assert.Equal(0, (await Json(await alice.GetAsync($"/api/v1/quality/parts/{part}/applicable?serial=99"))).GetArrayLength());
    }

    [Fact]
    public async Task The_published_quality_openapi_document_equals_the_committed_one_and_lists_nothing_else()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var generated = await api.ClientFor(Token("alice", ["plm-viewer"])).GetStringAsync("/openapi/quality.json");
        var json = JsonNode.Parse(generated)!;
        Assert.StartsWith("3.1", json["openapi"]!.GetValue<string>(), StringComparison.Ordinal);
        var paths = json["paths"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("/api/v1/quality/ncrs", paths);
        Assert.Contains("/api/v1/quality/ncrs/{number}", paths);
        Assert.Contains("/api/v1/quality/capas/{number}/analysis", paths);
        Assert.Contains("/api/v1/quality/deviations/{number}/scope", paths);
        Assert.Contains("/api/v1/quality/dashboard", paths);
        Assert.Contains("/api/v1/quality/parts/{number}/applicable", paths);
        Assert.All(paths, p => Assert.StartsWith("/api/v1/quality", p, StringComparison.Ordinal)); // only the quality surface is published
        Assert.DoesNotContain(paths, p => p.StartsWith("/api/v1/parts", StringComparison.Ordinal));

        var file = Path.Combine(RepoRoot(), "docs", "quality", "openapi.json");
        var normalized = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("KEEL_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, normalized);
            return;
        }
        Assert.True(File.Exists(file), "docs/quality/openapi.json is missing: run scripts/update-openapi.sh");
        Assert.True(normalized == await File.ReadAllTextAsync(file), "docs/quality/openapi.json differs from the generated document. If the API change is intended, run scripts/update-openapi.sh and commit the result.");

        // the integration document does not leak the quality routes into the ERP contract
        var integration = JsonNode.Parse(await api.ClientFor(Token("alice", ["plm-viewer"])).GetStringAsync("/openapi/v1.json"))!;
        Assert.DoesNotContain(integration["paths"]!.AsObject().Select(p => p.Key), p => p.Contains("/quality", StringComparison.Ordinal));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
