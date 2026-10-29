using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Keel.Core;
using Keel.Audit;
using Keel.Documents.Vault;

namespace Keel.IntegrationTests;

/// <summary>The REST surface of process planning through the real host: auth, the working copy round trip, no-leak answers and the OpenAPI document (ADR-0042).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ProcessApiTests(PostgresFixture pg)
{
    private static string Token(params string[] roles) => ApiFactory.Token("u-alice", true, roles: roles, programs: ["ORCA"], cui: ["EXPT", "CTI"]);
    private static string OutsiderToken(params string[] roles) => ApiFactory.Token("u-eve", true, roles: roles, programs: [], cui: []);

    private static ApiFactory Host(TestDatabase database)
    {
        var dir = Directory.CreateTempSubdirectory("keel-test-");
        var path = Path.Combine(dir.FullName, "checkpoint.pem");
        using var key = CheckpointKeys.Generate();
        File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
        return new ApiFactory(database, path, new VaultOptions { ServiceUrl = "http://127.0.0.1:9", AccessKey = "unused", SecretKey = "unused" });
    }

    private static async Task<JsonNode> Json(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"{(int)r.StatusCode} {text}");
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!;
    }

    [Fact]
    public async Task Auth_gates_every_route_and_the_plan_round_trip_works_over_HTTP()
    {
        var env = await ProcEnv.CreateAsync(pg);
        var (p, mbom) = await env.ReleasedMbomAsync();
        await env.WorkCenterAsync("WC-1");
        using var f = Host(env.M.C.Db);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/v1/process/plans")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.ClientFor(ApiFactory.Token("u-sup", null, roles: ["plm-supplier"], programs: [], cui: [])).GetAsync("/api/v1/process/plans")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.ClientFor(ApiFactory.Token("u-erp", true, roles: ["plm-integration"])).GetAsync("/api/v1/process/tools")).StatusCode);

        var author = f.ClientFor(Token("plm-viewer", "plm-author"));
        var viewer = f.ClientFor(Token("plm-viewer"));
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.PostAsJsonAsync("/api/v1/process/plans", new { mbom })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PostAsJsonAsync("/api/v1/process/plans", new { mbom = "MB-999999" })).StatusCode);
        var created = await Json(await author.PostAsJsonAsync("/api/v1/process/plans", new { mbom, description = "Over HTTP" }), HttpStatusCode.Created);
        var number = created["summary"]!["number"]!.GetValue<string>();
        Assert.Equal("InWork", created["summary"]!["state"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await author.PostAsJsonAsync("/api/v1/process/plans", new { mbom })).StatusCode);

        // Edit the working copy as JSON, check it in, read the matrix. The shop floor view needs a released revision.
        var wc = await Json(await author.GetAsync($"/api/v1/process/plans/{number}/working-copy"), HttpStatusCode.OK);
        var screw = await env.MasterIdAsync(p.Screw);
        wc["routings"]![0]!["operations"] = new JsonArray(JsonNode.Parse($$"""
            {"id":"{{Guid.NewGuid()}}","number":10,"name":"Fasten","workCenter":"WC-1","setupMinutes":5,"runMinutes":1.5,"condition":null,"inspection":false,"inspectionText":null,
             "components":[{"partMasterId":"{{screw}}","find":10,"alt":0,"quantity":null}],"tools":[],"steps":[{"id":"{{Guid.NewGuid()}}","kind":"warning","text":"Wear gloves","tools":[],"attachments":[]}]}
            """));
        var saved = await Json(await author.PutAsJsonAsync($"/api/v1/process/plans/{number}/working-copy", wc), HttpStatusCode.OK);
        Assert.Equal("Fasten", saved["routings"]![0]!["operations"]![0]!["name"]!.GetValue<string>());
        wc["routings"]![0]!["operations"]![0]!["workCenter"] = "NOPE";
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PutAsJsonAsync($"/api/v1/process/plans/{number}/working-copy", wc)).StatusCode);
        await Json(await author.PostAsJsonAsync($"/api/v1/process/plans/{number}/checkin", new { comment = "first" }), HttpStatusCode.OK);
        var matrix = await Json(await viewer.GetAsync($"/api/v1/process/plans/{number}/matrix"), HttpStatusCode.OK);
        Assert.Equal("PRIMARY", matrix["routingCode"]!.GetValue<string>());
        Assert.Equal(3, matrix["rows"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.Conflict, (await viewer.GetAsync($"/api/v1/process/plans/{number}/shop-floor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/v1/process/plans/{number}/checkout", null)).StatusCode);
        var list = await Json(await viewer.GetAsync("/api/v1/process/plans?q=" + number), HttpStatusCode.OK);
        Assert.Single(list["items"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/v1/process/plans?sort=password")).StatusCode);
        var forMbom = await Json(await viewer.GetAsync($"/api/v1/process/mboms/{mbom}/plan"), HttpStatusCode.OK);
        Assert.Single(forMbom.AsArray());

        // The review starts through the same workflow API as other objects.
        var start = await author.PostAsJsonAsync($"/api/v1/process/plans/{number}/review", new { participants = new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob"] } });
        Assert.True(start.StatusCode is HttpStatusCode.Created or HttpStatusCode.BadRequest or HttpStatusCode.Conflict, await start.Content.ReadAsStringAsync()); // the directory of this host knows no reviewers
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/process/plans/{number}/workflow")).StatusCode);
    }

    [Fact]
    public async Task Tools_and_work_centers_over_HTTP_and_a_plan_you_may_not_read_is_a_404()
    {
        var env = await ProcEnv.CreateAsync(pg);
        var (_, mbom) = await env.ReleasedMbomAsync();
        var secret = (await env.CreatePlanAsync(mbom, markings: ConfigEnv.Itar())).Summary.Number;
        using var f = Host(env.M.C.Db);
        var author = f.ClientFor(Token("plm-viewer", "plm-author"));
        var admin = f.ClientFor(Token("plm-viewer", "plm-admin"));
        var outsider = f.ClientFor(OutsiderToken("plm-viewer", "plm-author"));

        Assert.Equal(HttpStatusCode.Forbidden, (await author.PutAsJsonAsync("/api/v1/process/work-centers", new { plant = "PLANT-A", code = "WC-9", name = "Mill" })).StatusCode);
        await Json(await admin.PutAsJsonAsync("/api/v1/process/work-centers", new { plant = "PLANT-A", code = "WC-9", name = "Mill", resourceKind = "machine" }), HttpStatusCode.OK);
        var centers = await Json(await author.GetAsync("/api/v1/process/work-centers?plant=PLANT-A"), HttpStatusCode.OK);
        Assert.Contains(centers.AsArray(), c => c!["code"]!.GetValue<string>() == "WC-9");

        var markings = new { exportJurisdiction = "None", distributionStatement = "A", owningOrg = "ENG" };
        var tool = await Json(await author.PostAsJsonAsync("/api/v1/process/tools", new { name = "Torque wrench", kind = "gauge", plant = "PLANT-A", markings, requiresCalibration = true, intervalDays = 90, lastCalibratedOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-100) }), HttpStatusCode.Created);
        var number = tool["summary"]!["number"]!.GetValue<string>();
        Assert.Equal("overdue", tool["summary"]!["calibrationState"]!.GetValue<string>());
        var calibrated = await Json(await author.PostAsJsonAsync($"/api/v1/process/tools/{number}/calibrations", new { calibratedOn = DateOnly.FromDateTime(DateTime.UtcNow), reference = "CERT-9" }), HttpStatusCode.OK);
        Assert.Equal("valid", calibrated["summary"]!["calibrationState"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PostAsJsonAsync($"/api/v1/process/tools/{number}/calibrations", new { calibratedOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PostAsJsonAsync($"/api/v1/process/tools/{number}/retire", new { reason = "" })).StatusCode);
        var tools = await Json(await author.GetAsync("/api/v1/process/tools?due=false&kind=gauge"), HttpStatusCode.OK);
        Assert.Contains(tools["items"]!.AsArray(), t => t!["number"]!.GetValue<string>() == number);
        Assert.Equal(HttpStatusCode.BadRequest, (await author.GetAsync("/api/v1/process/tools?sort=password")).StatusCode);

        // No-leak: a plan the caller may not read answers exactly like a missing one, on every route.
        foreach (var url in new[] { $"/api/v1/process/plans/{secret}", $"/api/v1/process/plans/{secret}/matrix", $"/api/v1/process/plans/{secret}/shop-floor", $"/api/v1/process/plans/{secret}/working-copy" })
        {
            var hidden = await outsider.GetAsync(url);
            var missing = await outsider.GetAsync(url.Replace(secret, "PP-999999", StringComparison.Ordinal).Replace(mbom, "MB-999999", StringComparison.Ordinal));
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            Assert.Equal(missing.StatusCode, hidden.StatusCode);
        }
        Assert.Empty((await Json(await outsider.GetAsync($"/api/v1/process/mboms/{mbom}/plan"), HttpStatusCode.OK)).AsArray()); // the MBOM is readable; its plan is not listed
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsync($"/api/v1/process/plans/{secret}/checkout", null)).StatusCode);
        var found = await Json(await outsider.GetAsync("/api/v1/process/plans?q=" + secret), HttpStatusCode.OK);
        Assert.Empty(found["items"]!.AsArray());
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public async Task The_published_process_openapi_document_equals_the_committed_one_and_lists_nothing_else()
    {
        var database = await pg.CreateDatabaseAsync();
        using var f = Host(database);
        var generated = await f.ClientFor(Token("plm-viewer")).GetStringAsync("/openapi/process.json");
        var json = JsonNode.Parse(generated)!;
        Assert.StartsWith("3.1", json["openapi"]!.GetValue<string>(), StringComparison.Ordinal);
        var paths = json["paths"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("/api/v1/process/plans/{number}", paths);
        Assert.Contains("/api/v1/process/plans/{number}/shop-floor", paths);
        Assert.Contains("/api/v1/process/plans/{number}/matrix", paths);
        Assert.Contains("/api/v1/process/tools/{number}/calibrations", paths);
        Assert.Contains("/api/v1/process/work-centers", paths);
        Assert.All(paths, p => Assert.StartsWith("/api/v1/process", p, StringComparison.Ordinal));
        var v1 = JsonNode.Parse(await f.ClientFor(Token("plm-viewer")).GetStringAsync("/openapi/v1.json"))!["paths"]!.AsObject().Select(p => p.Key);
        Assert.DoesNotContain(v1, p => p.Contains("/process/", StringComparison.Ordinal));

        var file = Path.Combine(RepoRoot(), "docs", "process", "openapi.json");
        var normalized = json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("KEEL_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, normalized);
            return;
        }
        Assert.True(File.Exists(file), "docs/process/openapi.json is missing: run scripts/update-openapi.sh");
        Assert.True(normalized == await File.ReadAllTextAsync(file),
            "docs/process/openapi.json differs from the generated document. If the API change is intended, run scripts/update-openapi.sh and commit the result.");
    }
}
