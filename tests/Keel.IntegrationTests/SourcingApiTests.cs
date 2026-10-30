using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Documents.Vault;
using Keel.Parts;
using Keel.Sourcing;
using Keel.Workflow;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.IntegrationTests;

/// <summary>The REST surface of sourcing through the real host: role gates, the supplier portal over HTTP and the published OpenAPI document (ADR-0037).</summary>
[Collection(PostgresCollection.Name)]
public sealed class SourcingApiTests(PostgresFixture pg)
{
    private static string Supplier(string name) => ApiFactory.Token("u-" + name, null, roles: ["plm-supplier"], programs: [], cui: []);
    private static string Internal(params string[] roles) => ApiFactory.Token("u-alice", true, roles: roles, programs: ["ORCA"], cui: ["EXPT", "CTI"]);

    /// <summary>The real host against a fresh database. These tests do not store files, so the vault points at an address nobody listens on (no container needed).</summary>
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
    public async Task Roles_gate_every_route_and_the_supplier_role_opens_the_portal_only()
    {
        using var f = await HostAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/v1/sourcing/suppliers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/v1/portal/requests")).StatusCode);

        var supplier = f.ClientFor(Supplier("sup1"));
        foreach (var route in new[] { "/api/v1/sourcing/suppliers", "/api/v1/sourcing/requests", "/api/v1/sourcing/parts/P-000001", "/api/v1/sourcing/where-sourced?supplier=SUP-000001", "/api/v1/sourcing/coverage?mode=ebom&number=P-000001",
                     "/api/v1/parts", "/api/v1/mboms", "/api/v1/changes", "/api/v1/documents", "/api/v1/me", "/api/v1/audit/events" })
            Assert.Equal(HttpStatusCode.Forbidden, (await supplier.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await supplier.PostAsJsonAsync("/api/v1/sourcing/suppliers", new { })).StatusCode);

        // Internal roles, an administrator and the integration account have no portal.
        foreach (var token in new[] { Internal("plm-viewer"), Internal("plm-admin", "plm-viewer"), Internal("plm-auditor"), ApiFactory.Token("u-erp", true, roles: ["plm-integration"]) })
        {
            var client = f.ClientFor(token);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/portal/requests")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/portal/me")).StatusCode);
        }
        // A supplier user nobody linked reaches the portal route and is refused there, with a reason.
        var unlinked = await supplier.GetAsync("/api/v1/portal/me");
        Assert.Equal(HttpStatusCode.Forbidden, unlinked.StatusCode);
        Assert.Contains("portal.not_linked", await unlinked.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A viewer reads suppliers but cannot create one; the sort whitelist answers 400.
        var viewer = f.ClientFor(Internal("plm-viewer"));
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/sourcing/suppliers")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/v1/sourcing/suppliers?sort=password")).StatusCode);
        var created = await viewer.PostAsJsonAsync("/api/v1/sourcing/suppliers", new { name = "X", country = "USA", roles = new[] { "vendor" }, isForeign = false, markings = new { exportJurisdiction = "None", distributionStatement = "A", owningOrg = "ENG" } });
        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
    }

    [Fact]
    public async Task A_supplier_works_through_the_portal_over_HTTP_and_only_on_its_own_requests()
    {
        using var f = await HostAsync();
        var sp = f.Services;
        var parts = sp.GetRequiredService<PartService>();
        var suppliers = sp.GetRequiredService<SupplierService>();
        var requests = sp.GetRequiredService<RequestService>();
        var directory = sp.GetRequiredService<UserDirectory>();
        var alice = SourcingEnv.Alice;
        var ada = SourcingEnv.Ada;
        var open = SourcingEnv.Open;

        await directory.UpsertAsync(SourcingEnv.Sup1 with { Id = "u-sup1" });
        await directory.UpsertAsync(SourcingEnv.Sup2 with { Id = "u-sup2" });
        var part = (await parts.CreateAsync(alice, new CreatePartCommand("Washer", "Component", "EA", "Buy", open, "d", new JsonObject()), null)).Value!.Number;
        string Make(string name)
        {
            var s = suppliers.CreateAsync(alice, new CreateSupplierCommand(name, "USA", ["vendor"], false, open), null).GetAwaiter().GetResult().Value!;
            var u = suppliers.UpdateAsync(ada, s.Summary.Number, new UpdateSupplierCommand(name, "USA", ["vendor"], false, null, null, "public-release"), s.Revision, null).GetAwaiter().GetResult();
            Assert.True(u.IsOk, string.Join("; ", u.Errors));
            return s.Summary.Number;
        }
        var s1 = Make("Alpha"); var s2 = Make("Bravo");
        Assert.True((await suppliers.LinkPortalUserAsync(ada, s1, "sup1", null)).IsOk);
        Assert.True((await suppliers.LinkPortalUserAsync(ada, s2, "sup2", null)).IsOk);
        async Task<string> Issue(string title, params string[] to)
        {
            var created = await requests.CreateAsync(alice, SourcingEnv.Request(title, open, [part], to), null);
            Assert.True((await requests.IssueAsync(alice, created.Value!.Summary.Number, null)).IsOk);
            return created.Value.Summary.Number;
        }
        var forS1 = await Issue("For S1", s1);
        var forS2 = await Issue("For S2", s2);

        var c1 = f.ClientFor(Supplier("sup1"));
        var me = JsonNode.Parse(await c1.GetStringAsync("/api/v1/portal/me"))!;
        Assert.Equal(s1, me["supplierNumber"]!.GetValue<string>());
        var list = JsonNode.Parse(await c1.GetStringAsync("/api/v1/portal/requests"))!.AsArray();
        Assert.Equal([forS1], list.Select(x => x!["number"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.NotFound, (await c1.GetAsync($"/api/v1/portal/requests/{forS2}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c1.GetAsync("/api/v1/portal/requests/RFQ-999999")).StatusCode);
        var detail = JsonNode.Parse(await c1.GetStringAsync($"/api/v1/portal/requests/{forS1}"))!;
        Assert.Equal(part, detail["parts"]![0]!["partNumber"]!.GetValue<string>());
        Assert.Null(detail["recipients"]); // nothing about other suppliers
        // Responds with a quote (multipart without files).
        using var form = new MultipartFormDataContent { { new StringContent("Pleased to quote"), "message" }, { new StringContent($"[{{\"part\":\"{part}\",\"unitPrice\":1.5,\"currency\":\"USD\",\"leadTimeDays\":10}}]"), "quote" } };
        var response = await c1.PostAsync($"/api/v1/portal/requests/{forS1}/responses", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.ClientFor(Supplier("sup2")).PostAsync($"/api/v1/portal/requests/{forS1}/responses", new MultipartFormDataContent { { new StringContent("hello"), "message" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c1.PostAsync($"/api/v1/portal/requests/{forS1}/responses", new MultipartFormDataContent { { new StringContent(""), "message" } })).StatusCode);

        // The buyer sees it through the internal API.
        var buyer = f.ClientFor(Internal("plm-viewer", "plm-author"));
        var internalDetail = JsonNode.Parse(await buyer.GetStringAsync($"/api/v1/sourcing/requests/{forS1}"))!;
        Assert.Equal("Pleased to quote", internalDetail["responses"]![0]!["message"]!.GetValue<string>());
        // A supplier user cannot read the internal request, and the audit log recorded the supplier's actions.
        Assert.Equal(HttpStatusCode.Forbidden, (await c1.GetAsync($"/api/v1/sourcing/requests/{forS1}")).StatusCode);
        var audit = await new AuditQueries(sp.GetRequiredService<Npgsql.NpgsqlDataSource>()).ListAsync(new AuditQuery(Limit: 500));
        Assert.Contains(audit, e => e.Action == "portal.respond" && e.ObjectId == forS1);
        Assert.Contains(audit, e => e.Action == "portal.request_read" && e.Outcome == AuditOutcome.Denied && e.ObjectId == forS2);
    }

    [Fact]
    public async Task RFQ_comparison_awards_and_the_scorecard_work_over_HTTP_with_the_same_gates_as_the_services()
    {
        using var f = await HostAsync();
        var sp = f.Services;
        var parts = sp.GetRequiredService<PartService>();
        var suppliers = sp.GetRequiredService<SupplierService>();
        var requests = sp.GetRequiredService<RequestService>();
        var directory = sp.GetRequiredService<UserDirectory>();
        var alice = SourcingEnv.Alice;
        var ada = SourcingEnv.Ada;
        var open = SourcingEnv.Open;
        await directory.UpsertAsync(SourcingEnv.Sup1 with { Id = "u-sup1" });
        await directory.UpsertAsync(SourcingEnv.Sup2 with { Id = "u-sup2" });
        var part = (await parts.CreateAsync(alice, new CreatePartCommand("Washer", "Component", "EA", "Buy", open, "d", new JsonObject()), null)).Value!.Number;
        string Make(string name)
        {
            var s = suppliers.CreateAsync(alice, new CreateSupplierCommand(name, "USA", ["vendor"], false, open), null).GetAwaiter().GetResult().Value!;
            Assert.True(suppliers.UpdateAsync(ada, s.Summary.Number, new UpdateSupplierCommand(name, "USA", ["vendor"], false, null, null, "public-release"), s.Revision, null).GetAwaiter().GetResult().IsOk);
            return s.Summary.Number;
        }
        var s1 = Make("Alpha"); var s2 = Make("Bravo");
        Assert.True((await suppliers.LinkPortalUserAsync(ada, s1, "sup1", null)).IsOk);
        Assert.True((await suppliers.LinkPortalUserAsync(ada, s2, "sup2", null)).IsOk);
        var buyer = f.ClientFor(Internal("plm-viewer", "plm-author"));
        var viewer = f.ClientFor(Internal("plm-viewer"));

        // A request with a need-by date through the REST layer; the portal shows it and takes a tiered quote that is valid until a date.
        var created = await buyer.PostAsJsonAsync("/api/v1/sourcing/requests", new
        {
            kind = "rfq", title = "Washers", description = "d", dueOn = (string?)null, markings = new { exportJurisdiction = "None", distributionStatement = "A", owningOrg = "ENG" },
            parts = new[] { new { partNumber = part, quantity = 600, needBy = "2027-03-01" } }, suppliers = new[] { s1, s2 },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var number = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["summary"]!["number"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await buyer.PostAsync($"/api/v1/sourcing/requests/{number}/issue", null)).StatusCode);
        var c1 = f.ClientFor(Supplier("sup1"));
        var c2 = f.ClientFor(Supplier("sup2"));
        Assert.Equal("2027-03-01", JsonNode.Parse(await c1.GetStringAsync($"/api/v1/portal/requests/{number}"))!["parts"]![0]!["needBy"]!.GetValue<string>());
        var valid = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd");
        using (var form = new MultipartFormDataContent { { new StringContent("Tiered"), "message" }, { new StringContent(valid), "validUntil" },
                   { new StringContent($"[{{\"part\":\"{part}\",\"currency\":\"USD\",\"leadTimeDays\":21,\"tiers\":[{{\"quantity\":100,\"unitPrice\":2.5}},{{\"quantity\":500,\"unitPrice\":2.1}}],\"exception\":\"Not before July\"}}]"), "quote" } })
            Assert.Equal(HttpStatusCode.Created, (await c1.PostAsync($"/api/v1/portal/requests/{number}/responses", form)).StatusCode);
        using (var form = new MultipartFormDataContent { { new StringContent("Single"), "message" }, { new StringContent($"[{{\"part\":\"{part}\",\"unitPrice\":2.2,\"currency\":\"USD\",\"leadTimeDays\":10}}]"), "quote" } })
            Assert.Equal(HttpStatusCode.Created, (await c2.PostAsync($"/api/v1/portal/requests/{number}/responses", form)).StatusCode);
        using (var bad = new MultipartFormDataContent { { new StringContent("x"), "message" }, { new StringContent("last week"), "validUntil" } })
            Assert.Equal(HttpStatusCode.BadRequest, (await c1.PostAsync($"/api/v1/portal/requests/{number}/responses", bad)).StatusCode);

        // The comparison: tier applied at 600 pieces, lowest price and shortest lead time marked; only internal roles read it.
        var comparison = JsonNode.Parse(await buyer.GetStringAsync($"/api/v1/sourcing/requests/{number}/comparison"))!;
        var cells = comparison["lines"]![0]!["cells"]!.AsArray();
        Assert.Equal(2.1m, cells[0]!["unitPrice"]!.GetValue<decimal>());
        Assert.True(cells[0]!["lowest"]!.GetValue<bool>());
        Assert.True(cells[1]!["fastest"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Forbidden, (await c1.GetAsync($"/api/v1/sourcing/requests/{number}/comparison")).StatusCode);

        // Awarding needs the right to modify; a supplier cannot; a viewer cannot; the award shows to the winner alone.
        Assert.Equal(HttpStatusCode.Forbidden, (await c1.PostAsJsonAsync($"/api/v1/sourcing/requests/{number}/awards", new { supplier = s1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/v1/sourcing/requests/{number}/awards", new { supplier = s1 })).StatusCode);
        var awarded = await buyer.PostAsJsonAsync($"/api/v1/sourcing/requests/{number}/awards", new { supplier = s1, note = "Best price" });
        Assert.Equal(HttpStatusCode.OK, awarded.StatusCode);
        var award = JsonNode.Parse(await awarded.Content.ReadAsStringAsync())!["awards"]![0]!;
        Assert.Equal(s1, award["supplierNumber"]!.GetValue<string>());
        Assert.StartsWith("SRC-", award["entryNumber"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await buyer.PostAsJsonAsync($"/api/v1/sourcing/requests/{number}/awards", new { supplier = s2, parts = new[] { part } })).StatusCode);
        Assert.Single(JsonNode.Parse(await c1.GetStringAsync($"/api/v1/portal/requests/{number}"))!["awards"]!.AsArray());
        Assert.Empty(JsonNode.Parse(await c2.GetStringAsync($"/api/v1/portal/requests/{number}"))!["awards"]!.AsArray());
        Assert.Equal(HttpStatusCode.OK, (await buyer.PostAsJsonAsync($"/api/v1/sourcing/requests/{number}/awards/{part}/withdraw", new { reason = "Changed our mind" })).StatusCode);

        // The scorecard: readable by internal roles, published by someone who may modify, then visible to that supplier alone.
        Assert.Equal(HttpStatusCode.NotFound, (await c1.GetAsync("/api/v1/portal/scorecard")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/sourcing/suppliers/{s1}/scorecard")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/v1/sourcing/suppliers/{s1}/scorecard/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c1.GetAsync($"/api/v1/sourcing/suppliers/{s1}/scorecard")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await buyer.PostAsync($"/api/v1/sourcing/suppliers/{s1}/scorecard/publish", null)).StatusCode);
        var card = JsonNode.Parse(await c1.GetStringAsync("/api/v1/portal/scorecard"))!["card"]!;
        Assert.Equal(12, card["trend"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.NotFound, (await c2.GetAsync("/api/v1/portal/scorecard")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await buyer.PostAsync($"/api/v1/sourcing/suppliers/{s1}/scorecard/withdraw", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c1.GetAsync("/api/v1/portal/scorecard")).StatusCode);
        // The portal never serves a document it was not asked for by id, and an unknown file is not found.
        Assert.Equal(HttpStatusCode.NotFound, (await c1.GetAsync($"/api/v1/portal/requests/{number}/documents/{Guid.NewGuid():D}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await buyer.GetAsync($"/api/v1/portal/requests/{number}/documents/{Guid.NewGuid():D}")).StatusCode);
        var audit = await new AuditQueries(sp.GetRequiredService<Npgsql.NpgsqlDataSource>()).ListAsync(new AuditQuery(Limit: 1000));
        foreach (var action in new[] { "rfq.compare", "rfq.award", "rfq.award_withdraw", "scorecard.publish", "scorecard.withdraw", "portal.scorecard_read" })
            Assert.Contains(audit, e => e.Action == action);
    }

    [Fact]
    public async Task The_published_sourcing_openapi_document_equals_the_committed_one_and_lists_no_internal_surface()
    {
        using var f = await HostAsync();
        var generated = await f.ClientFor(Internal("plm-viewer")).GetStringAsync("/openapi/sourcing.json");
        var json = JsonNode.Parse(generated)!;
        Assert.StartsWith("3.1", json["openapi"]!.GetValue<string>(), StringComparison.Ordinal);
        var paths = json["paths"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("/api/v1/sourcing/suppliers", paths);
        Assert.Contains("/api/v1/sourcing/entries", paths);
        Assert.Contains("/api/v1/sourcing/coverage", paths);
        Assert.Contains("/api/v1/portal/requests/{number}/responses", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("/api/v1/parts", StringComparison.Ordinal) || p.StartsWith("/api/v1/integration", StringComparison.Ordinal));
        var v1 = JsonNode.Parse(await f.ClientFor(Internal("plm-viewer")).GetStringAsync("/openapi/v1.json"))!["paths"]!.AsObject().Select(p => p.Key);
        Assert.DoesNotContain(v1, p => p.Contains("/sourcing/", StringComparison.Ordinal) || p.Contains("/portal/", StringComparison.Ordinal)); // the integration document is unchanged

        var file = Path.Combine(RepoRoot(), "docs", "sourcing", "openapi.json");
        var normalized = json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("KEEL_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, normalized);
            return;
        }
        Assert.True(File.Exists(file), "docs/sourcing/openapi.json is missing: run scripts/update-openapi.sh");
        Assert.True(normalized == await File.ReadAllTextAsync(file),
            "docs/sourcing/openapi.json differs from the generated document. If the API change is intended, run scripts/update-openapi.sh and commit the result.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
