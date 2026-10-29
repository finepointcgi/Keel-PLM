using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.Documents.Vault;
using Keel.Parts;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.IntegrationTests;

/// <summary>The REST surface of options and variants through the real host: auth, the whole round trip, no-leak answers, the pull API and the OpenAPI document (ADR-0039).</summary>
[Collection(PostgresCollection.Name)]
public sealed class VariantsApiTests(PostgresFixture pg)
{
    private static string Token(params string[] roles) => ApiFactory.Token("u-alice", true, roles: roles, programs: ["ORCA"], cui: ["EXPT", "CTI"]);
    private static readonly Subject Alice = BomTestKit.Alice with { Id = "u-alice" };

    private async Task<ApiFactory> HostAsync()
    {
        var database = await pg.CreateDatabaseAsync();
        var dir = Directory.CreateTempSubdirectory("keel-test-");
        var path = Path.Combine(dir.FullName, "checkpoint.pem");
        using var key = CheckpointKeys.Generate();
        await File.WriteAllTextAsync(path, key.ExportPkcs8PrivateKeyPem());
        return new ApiFactory(database, path, new VaultOptions { ServiceUrl = "http://127.0.0.1:9", AccessKey = "unused", SecretKey = "unused" });
    }

    private static async Task<JsonNode> Json(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"{(int)r.StatusCode} {text}");
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!;
    }

    [Fact]
    public async Task Auth_gates_every_route_and_the_round_trip_works_over_HTTP()
    {
        using var f = await HostAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/v1/variants/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.ClientFor(ApiFactory.Token("u-sup", null, roles: ["plm-supplier"], programs: [], cui: [])).GetAsync("/api/v1/variants/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.ClientFor(ApiFactory.Token("u-erp", true, roles: ["plm-integration"])).GetAsync("/api/v1/variants/products")).StatusCode);

        var parts = f.Services.GetRequiredService<PartService>();
        var bom = f.Services.GetRequiredService<BomService>();
        async Task<string> Part(string name, string type = "Component", Markings? m = null) =>
            (await parts.CreateAsync(Alice, new CreatePartCommand(name, type, "EA", "Make", m ?? BomTestKit.Open, "d", new JsonObject()), null)).Value!.Number;
        var engA = await Part("Turbo part");
        var engB = await Part("Electric part");
        var common = await Part("Common");
        var rocket = await Part("Rocket", "Assembly");

        var author = f.ClientFor(Token("plm-viewer", "plm-author"));
        var viewer = f.ClientFor(Token("plm-viewer"));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/v1/variants/products/{rocket}", null)).StatusCode);
        await Json(await author.PostAsync($"/api/v1/variants/products/{rocket}", null), HttpStatusCode.Created);
        await Json(await author.PostAsJsonAsync($"/api/v1/variants/products/{rocket}/families", new { key = "Engine", name = "Engine" }), HttpStatusCode.OK);
        await Json(await author.PostAsJsonAsync($"/api/v1/variants/products/{rocket}/families/Engine/values", new { key = "A", name = "Turbo" }), HttpStatusCode.OK);
        var model = await Json(await author.PostAsJsonAsync($"/api/v1/variants/products/{rocket}/families/Engine/values", new { key = "B", name = "Electric" }), HttpStatusCode.OK);
        Assert.Equal(2, model["validCombinations"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PostAsJsonAsync($"/api/v1/variants/products/{rocket}/families", new { key = "bad key", name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await author.PostAsJsonAsync($"/api/v1/variants/products/{rocket}/families", new { key = "Engine", name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PostAsJsonAsync($"/api/v1/variants/products/{rocket}/rules", new { kind = "sometimes", members = Array.Empty<object>() })).StatusCode);
        await Json(await author.PostAsJsonAsync($"/api/v1/variants/products/{rocket}/variants", new { name = "Turbo", selection = new Dictionary<string, string> { ["Engine"] = "A" } }), HttpStatusCode.OK);
        await Json(await author.PostAsJsonAsync($"/api/v1/variants/products/{rocket}/variants", new { name = "Electric", selection = new Dictionary<string, string> { ["Engine"] = "B" } }), HttpStatusCode.OK);
        await Json(await author.PostAsync($"/api/v1/variants/products/{rocket}/variants/Turbo/release", null), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Conflict, (await author.PostAsync($"/api/v1/variants/products/{rocket}/variants/Turbo/release", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await author.PostAsync($"/api/v1/variants/products/{rocket}/variants/Nope/release", null)).StatusCode);

        // conditions on lines of the working copy, validated against the product
        Assert.True((await parts.CheckOutAsync(Alice, rocket, null)).IsOk);
        foreach (var (child, find, qty) in new[] { (engA, 10, 1m), (engB, 20, 1m), (common, 30, 4m) })
            Assert.True((await bom.AddUsageAsync(Alice, rocket, new AddUsageCommand(child, qty, null, find), null)).IsOk);
        var working = await Json(await author.GetAsync($"/api/v1/parts/{rocket}/working-copy/bom"), HttpStatusCode.OK);
        string UsageId(int find) => working["lines"]!.AsArray().Single(l => l!["findNumber"]!.GetValue<int>() == find)!["id"]!.GetValue<string>();
        var set = await Json(await author.PutAsJsonAsync($"/api/v1/variants/lines/{rocket}/{UsageId(10)}/condition", new { condition = "engine = A", product = rocket }), HttpStatusCode.BadRequest);
        Assert.Contains("does not exist", set.ToJsonString(), StringComparison.Ordinal); // keys are case sensitive and checked against the product
        await Json(await author.PutAsJsonAsync($"/api/v1/variants/lines/{rocket}/{UsageId(10)}/condition", new { condition = "Engine = A", product = rocket }), HttpStatusCode.OK);
        await Json(await author.PutAsJsonAsync($"/api/v1/variants/lines/{rocket}/{UsageId(20)}/condition", new { condition = "Engine IN (B)", product = rocket }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PutAsJsonAsync($"/api/v1/variants/lines/{rocket}/{UsageId(30)}/condition", new { condition = "Engine =" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"/api/v1/variants/lines/{rocket}/{UsageId(30)}/condition", new { condition = "Engine = A" })).StatusCode);
        Assert.True((await parts.CheckInAsync(Alice, rocket, "conditions", null)).IsOk);

        // the preview in plain words
        var preview = await Json(await author.PostAsJsonAsync("/api/v1/variants/conditions/preview", new { product = rocket, condition = "Engine IN (B, A)" }), HttpStatusCode.OK);
        Assert.Equal("Engine IN (A, B)", preview["canonical"]!.GetValue<string>());
        Assert.Equal("Used when Engine is Turbo or Electric.", preview["description"]!.GetValue<string>());
        var broken = await Json(await author.PostAsJsonAsync("/api/v1/variants/conditions/preview", new { product = rocket, condition = "Engine = " }), HttpStatusCode.OK);
        Assert.False(broken["valid"]!.GetValue<bool>());

        // the resolved structure, the CSV and the summary
        var turbo = await Json(await author.GetAsync($"/api/v1/variants/products/{rocket}/variants/Turbo/bom"), HttpStatusCode.OK);
        Assert.Equal(["10", "30"], turbo["lines"]!.AsArray().Select(l => l!["path"]!.GetValue<string>()));
        Assert.Equal("variant Turbo", turbo["configuration"]!.GetValue<string>());
        var csvResponse = await author.GetAsync($"/api/v1/variants/products/{rocket}/variants/Electric/bom/export.csv");
        Assert.Equal(HttpStatusCode.OK, csvResponse.StatusCode);
        var csv = await csvResponse.Content.ReadAsStringAsync();
        Assert.Contains(engB, csv, StringComparison.Ordinal);
        Assert.DoesNotContain(engA, csv, StringComparison.Ordinal);
        var viaCookieRoute = await author.GetAsync($"/api/v1/variants/products/{rocket}/variants/Electric/bom/summary");
        Assert.Equal(HttpStatusCode.OK, viaCookieRoute.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await author.GetAsync($"/api/v1/variants/products/{rocket}/variants/Turbo/bom?spec=Baseline")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync($"/api/v1/variants/products/{rocket}/variants/Nope/bom")).StatusCode);

        // compare two variants
        var diff = await Json(await author.GetAsync($"/api/v1/variants/products/{rocket}/compare?left=Turbo&right=Electric"), HttpStatusCode.OK);
        Assert.Equal(2, diff["lines"]!.AsArray().Count); // the turbo part only in the left, the electric part only in the right
        Assert.Equal(1, diff["unchanged"]!.GetValue<int>());

        // which variants use a part
        var usedBy = await Json(await author.GetAsync($"/api/v1/variants/parts/{engA}/used-by"), HttpStatusCode.OK);
        var product = usedBy["products"]!.AsArray().Single()!;
        Assert.Equal(rocket, product["product"]!.GetValue<string>());
        Assert.Equal(["Turbo"], product["variants"]!.AsArray().Select(v => v!["variant"]!.GetValue<string>()));
        var usedByCommon = await Json(await author.GetAsync($"/api/v1/variants/parts/{common}/used-by"), HttpStatusCode.OK);
        Assert.Equal(["Electric", "Turbo"], usedByCommon["products"]![0]!["variants"]!.AsArray().Select(v => v!["variant"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync("/api/v1/variants/parts/P-999999/used-by")).StatusCode);

        // the pull API resolves for a variant and shows the conditions of a full structure
        var erp = f.ClientFor(ApiFactory.Token("u-erp", true, roles: ["plm-integration"], programs: ["ORCA"], cui: ["EXPT", "CTI"]));
        var pulled = await Json(await erp.GetAsync($"/api/v1/integration/boms/{rocket}?spec=Latest&depth=5&variant=Electric"), HttpStatusCode.OK);
        Assert.Equal([engB, common], pulled["lines"]!.AsArray().Select(l => l!["number"]!.GetValue<string>()));
        var full = await Json(await erp.GetAsync($"/api/v1/integration/boms/{rocket}?spec=Latest&depth=5"), HttpStatusCode.OK);
        Assert.Equal(["Engine = A", "Engine = B", null], full["lines"]!.AsArray().Select(l => l!["condition"]?.GetValue<string>()));
        Assert.Equal(HttpStatusCode.NotFound, (await erp.GetAsync($"/api/v1/integration/boms/{rocket}?spec=Latest&variant=Nope")).StatusCode);
        // a baseline package cannot name a variant
        var tdp = await author.PostAsJsonAsync("/api/v1/tdp/preview", new { objectType = "baseline", number = "BL-000001", recipientProfile = "x", variant = "Turbo" });
        Assert.Equal(HttpStatusCode.BadRequest, tdp.StatusCode);

        // audit trail of the changes
        var audit = await new AuditQueries(f.Services.GetRequiredService<Npgsql.NpgsqlDataSource>()).ListAsync(new AuditQuery(Limit: 1000));
        foreach (var action in new[] { "options.model_create", "options.family_add", "options.value_add", "variant.create", "variant.release", "bom.set_condition", "variant.resolve" })
            Assert.Contains(audit, e => e.Action == action && e.ObjectId == rocket && e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task A_product_the_caller_cannot_read_answers_404_like_a_missing_one_on_every_route()
    {
        using var f = await HostAsync();
        var parts = f.Services.GetRequiredService<PartService>();
        var secret = (await parts.CreateAsync(Alice, new CreatePartCommand("Secret rocket", "Assembly", "EA", "Make", BomTestKit.Itar(), "d", new JsonObject()), null)).Value!.Number;
        var author = f.ClientFor(Token("plm-viewer", "plm-author"));
        await Json(await author.PostAsync($"/api/v1/variants/products/{secret}", null), HttpStatusCode.Created);
        await Json(await author.PostAsJsonAsync($"/api/v1/variants/products/{secret}/families", new { key = "Engine", name = "Engine" }), HttpStatusCode.OK);
        await Json(await author.PostAsJsonAsync($"/api/v1/variants/products/{secret}/families/Engine/values", new { key = "A", name = "Turbo" }), HttpStatusCode.OK);
        await Json(await author.PostAsJsonAsync($"/api/v1/variants/products/{secret}/variants", new { name = "Covert", selection = new Dictionary<string, string> { ["Engine"] = "A" } }), HttpStatusCode.OK);

        var outsider = f.ClientFor(ApiFactory.Token("u-eve", true, roles: ["plm-viewer", "plm-author"], programs: [], cui: ["EXPT", "CTI"]));
        string[] get =
        [
            $"/api/v1/variants/products/{secret}", $"/api/v1/variants/products/{secret}/variants/Covert/bom", $"/api/v1/variants/products/{secret}/variants/Covert/bom/export.csv",
            $"/api/v1/variants/products/{secret}/compare?left=Covert&right=Covert", $"/api/v1/variants/parts/{secret}/used-by",
            "/api/v1/variants/products/P-999999", "/api/v1/variants/products/P-999999/variants/Covert/bom", "/api/v1/variants/products/P-999999/compare?left=a&right=b", "/api/v1/variants/parts/P-999999/used-by",
        ];
        var statuses = new List<(string, HttpStatusCode, string)>();
        foreach (var url in get)
        {
            var r = await outsider.GetAsync(url);
            statuses.Add((url, r.StatusCode, await r.Content.ReadAsStringAsync()));
        }
        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.NotFound, s.Item2));
        Assert.All(statuses, s => Assert.DoesNotContain("Covert", s.Item3, StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"/api/v1/variants/products/{secret}/families", new { key = "Color", name = "Color" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"/api/v1/variants/products/{secret}/check", new { selection = new Dictionary<string, string> { ["Engine"] = "A" } })).StatusCode);
        var list = await Json(await outsider.GetAsync("/api/v1/variants/products"), HttpStatusCode.OK);
        Assert.Empty(list["rows"]!.AsArray());
        Assert.Equal(0, list["total"]!.GetValue<int>());
        var insider = await Json(await author.GetAsync("/api/v1/variants/products"), HttpStatusCode.OK);
        Assert.Single(insider["rows"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await author.GetAsync("/api/v1/variants/products?sort=password")).StatusCode);
    }

    [Fact]
    public async Task The_published_variants_openapi_document_equals_the_committed_one_and_lists_nothing_else()
    {
        using var f = await HostAsync();
        var generated = await f.ClientFor(Token("plm-viewer")).GetStringAsync("/openapi/variants.json");
        var json = JsonNode.Parse(generated)!;
        Assert.StartsWith("3.1", json["openapi"]!.GetValue<string>(), StringComparison.Ordinal);
        var paths = json["paths"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("/api/v1/variants/products/{number}", paths);
        Assert.Contains("/api/v1/variants/products/{number}/variants/{name}/bom", paths);
        Assert.Contains("/api/v1/variants/parts/{number}/used-by", paths);
        Assert.Contains("/api/v1/variants/conditions/preview", paths);
        Assert.All(paths, p => Assert.StartsWith("/api/v1/variants", p, StringComparison.Ordinal));
        var v1 = JsonNode.Parse(await f.ClientFor(Token("plm-viewer")).GetStringAsync("/openapi/v1.json"))!["paths"]!.AsObject().Select(p => p.Key);
        Assert.DoesNotContain(v1, p => p.Contains("/variants/", StringComparison.Ordinal));

        var file = Path.Combine(RepoRoot(), "docs", "variants", "openapi.json");
        var normalized = json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("KEEL_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, normalized);
            return;
        }
        Assert.True(File.Exists(file), "docs/variants/openapi.json is missing: run scripts/update-openapi.sh");
        Assert.True(normalized == await File.ReadAllTextAsync(file),
            "docs/variants/openapi.json differs from the generated document. If the API change is intended, run scripts/update-openapi.sh and commit the result.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
