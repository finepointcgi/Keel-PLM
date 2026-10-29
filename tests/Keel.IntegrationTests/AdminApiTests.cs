using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>The administration REST API through the real host: role gating with audited denials, validation, and the data behind the metadata endpoints.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AdminApiTests(PostgresFixture pg)
{
    private static readonly string Admin = ApiFactory.Token(sub: "u-ada", roles: ["plm-admin", "plm-viewer"]);
    private static readonly string Author = ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"]);
    private static readonly string Viewer = ApiFactory.Token(sub: "u-mallory", roles: ["plm-viewer"]);

    private static async Task<JsonElement> Json(HttpResponseMessage res) => JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static object PartBody(object attributes) => new
    {
        name = "Pin", partType = "Component", defaultUnit = "EA", source = "Make", attributes,
        markings = new { exportJurisdiction = "None", distributionStatement = "A", owningOrg = "ENG" },
    };

    [Fact]
    public async Task Non_administrators_get_403_and_the_denial_is_audited_while_anonymous_callers_get_401()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        foreach (var path in new[] { "/api/v1/admin/attributes/part", "/api/v1/admin/lifecycles", "/api/v1/admin/numbering", "/api/v1/admin/config/export", "/api/v1/admin/config/history", "/api/v1/admin/workflow/templates" })
            Assert.Equal(HttpStatusCode.Forbidden, (await api.ClientFor(Author).GetAsync(path)).StatusCode);
        var post = await api.ClientFor(Author).PostAsJsonAsync("/api/v1/admin/classification/classes", new { code = "X", name = "X" });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/admin/attributes/part")).StatusCode);

        await using var conn = new NpgsqlConnection(api.Database.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit.event WHERE action = 'access.denied' AND object_id LIKE '% /api/v1/admin/%'", conn);
        Assert.Equal(7L, (long)(await cmd.ExecuteScalarAsync())!);
        await using var none = new NpgsqlCommand("SELECT count(*) FROM cfg.class_node", conn);
        Assert.Equal(0L, (long)(await none.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Attribute_definitions_drive_the_metadata_endpoint_and_part_validation_over_http()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var admin = api.ClientFor(Admin);
        var created = await admin.PostAsJsonAsync("/api/v1/admin/attributes/part", new { name = "diameter", label = "Diameter", dataType = "number", unit = "mm", min = 1, max = 500 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/v1/admin/attributes/part", new { name = "diameter", label = "Diameter", dataType = "number" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/admin/attributes/part", new { name = "Bad", label = "", dataType = "string" })).StatusCode);

        var meta = await Json(await api.ClientFor(Viewer).GetAsync("/api/v1/metadata/parts"));
        Assert.Contains(meta.GetProperty("attributes").EnumerateArray(), a => a.GetProperty("name").GetString() == "diameter" && a.GetProperty("max").GetDecimal() == 500);

        var author = api.ClientFor(Author);
        var low = await author.PostAsJsonAsync("/api/v1/parts", PartBody(new { diameter = 0 }));
        Assert.Equal(HttpStatusCode.BadRequest, low.StatusCode);
        Assert.Contains("Diameter must be at least 1", await low.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Created, (await author.PostAsJsonAsync("/api/v1/parts", PartBody(new { diameter = 12 }))).StatusCode);

        var retire = await admin.PostAsJsonAsync("/api/v1/admin/attributes/part/diameter/retire", new { retired = true, reason = "Replaced" });
        Assert.Equal(HttpStatusCode.OK, retire.StatusCode);
        meta = await Json(await api.ClientFor(Viewer).GetAsync("/api/v1/metadata/parts"));
        Assert.DoesNotContain(meta.GetProperty("attributes").EnumerateArray(), a => a.GetProperty("name").GetString() == "diameter");

        // Attributes of other object types are not stored yet: 501 with the phase that delivers them.
        var doc = await admin.GetAsync("/api/v1/admin/attributes/document");
        Assert.Equal(HttpStatusCode.NotImplemented, doc.StatusCode);
        Assert.Contains("Phase 2c", await doc.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lists_numbering_and_lifecycles_are_managed_over_http_and_the_effects_show_in_the_metadata()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var admin = api.ClientFor(Admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/admin/lists/unit/yd", new { label = "Yard", sortOrder = 200 })).StatusCode);
        var units = await Json(await api.ClientFor(Viewer).GetAsync("/api/v1/metadata/lists/unit"));
        Assert.Contains(units.EnumerateArray(), u => u.GetProperty("code").GetString() == "yd");
        Assert.Equal(HttpStatusCode.NotFound, (await api.ClientFor(Viewer).GetAsync("/api/v1/metadata/lists/nonsense")).StatusCode);
        var retire = await admin.PostAsJsonAsync("/api/v1/admin/lists/unit/EA/active", new { active = false, reason = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, retire.StatusCode); // EA is referred to by name in code

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/admin/numbering/part", new { prefix = "PRT-", width = 7, reason = "Scheme" })).StatusCode);
        var created = await Json(await api.ClientFor(Author).PostAsJsonAsync("/api/v1/parts", PartBody(new { })));
        Assert.Equal("PRT-0000001", created.GetProperty("number").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/admin/numbering/ecn", new { prefix = "X-", width = 6, reason = "x" })).StatusCode);

        var families = await Json(await admin.GetAsync("/api/v1/admin/lifecycles"));
        Assert.Equal(9, families.GetArrayLength()); // part, document, pr, ecr, ecn, ct + baseline + mbom + reqspec
        var current = await Json(await admin.GetAsync("/api/v1/admin/lifecycles/part-default"));
        var states = current.GetProperty("definition").GetProperty("states").EnumerateArray().Select(s => (object)new
        {
            state = s.GetProperty("state").GetString(), label = s.GetProperty("label").GetString() == "Obsolete" ? "Retired" : s.GetProperty("label").GetString(),
            sortOrder = s.GetProperty("sortOrder").GetInt32(), isInitial = s.GetProperty("isInitial").GetBoolean(), isLocked = s.GetProperty("isLocked").GetBoolean(), isFinal = s.GetProperty("isFinal").GetBoolean(),
        }).ToList();
        var transitions = current.GetProperty("definition").GetProperty("transitions").EnumerateArray().Select(t => (object)new
        {
            from = t.GetProperty("from").GetString(), to = t.GetProperty("to").GetString(), via = t.GetProperty("via").GetString(),
        }).ToList();
        var body = new { states, transitions, changeNote = "Relabel Obsolete" };
        var validate = await Json(await admin.PostAsJsonAsync("/api/v1/admin/lifecycles/part-default/validate", body));
        Assert.Equal(0, validate.GetProperty("errors").GetArrayLength());
        Assert.Contains(validate.GetProperty("effects").EnumerateArray(), e => e.GetString()!.Contains("Renamed Obsolete to Retired", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/admin/lifecycles/part-default/versions", body)).StatusCode);
        var meta = await Json(await api.ClientFor(Viewer).GetAsync("/api/v1/metadata/parts"));
        Assert.Equal(2, meta.GetProperty("lifecycle").GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/admin/lifecycles/part-default/versions", new { states, transitions, changeNote = "Same again" })).StatusCode); // nothing changed
        Assert.Equal(HttpStatusCode.NotImplemented, (await admin.PostAsJsonAsync("/api/v1/admin/lifecycles/ecn-default/versions", body)).StatusCode);
    }

    [Fact]
    public async Task Classification_over_http_tree_assignment_search_import_export_and_config_promotion()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var admin = api.ClientFor(Admin);
        var author = api.ClientFor(Author);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/admin/classification/classes", new { code = "MECH", name = "Mechanical", sortOrder = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/admin/classification/classes", new { code = "FAST", name = "Fasteners", parentCode = "MECH" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/admin/classification/classes/FAST/attributes", new { name = "diameter", label = "Diameter", dataType = "number", unit = "mm", min = 1, max = 100, required = true })).StatusCode);

        // Anyone with a PLM role reads the tree; only administrators change it.
        var tree = await Json(await api.ClientFor(Viewer).GetAsync("/api/v1/classification/tree"));
        Assert.Equal(["MECH", "FAST"], tree.EnumerateArray().Select(n => n.GetProperty("code").GetString()!));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.ClientFor(Viewer).PostAsJsonAsync("/api/v1/admin/classification/classes", new { code = "Z", name = "Z" })).StatusCode);
        var detail = await Json(await api.ClientFor(Viewer).GetAsync("/api/v1/classification/classes/FAST"));
        Assert.Equal("diameter", detail.GetProperty("effective")[0].GetProperty("definition").GetProperty("name").GetString());

        var part = await Json(await author.PostAsJsonAsync("/api/v1/parts", PartBody(new { })));
        var number = part.GetProperty("number").GetString();
        Assert.Equal(HttpStatusCode.Conflict, (await author.PutAsJsonAsync($"/api/v1/parts/{number}/classification", new { classCode = "FAST", attributes = new { diameter = 8 } })).StatusCode); // not checked out
        await author.PostAsync($"/api/v1/parts/{number}/checkout", null);
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PutAsJsonAsync($"/api/v1/parts/{number}/classification", new { classCode = "FAST", attributes = new { diameter = 800 } })).StatusCode);
        var set = await author.PutAsJsonAsync($"/api/v1/parts/{number}/classification", new { classCode = "FAST", attributes = new { diameter = 8 } });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await author.PostAsJsonAsync($"/api/v1/parts/{number}/checkin", new { comment = "classified" })).StatusCode);

        var found = await Json(await api.ClientFor(Viewer).PostAsJsonAsync("/api/v1/classification/search", new { classCode = "MECH", filters = new[] { new { attribute = "diameter", min = 5, max = 10 } } }));
        Assert.Equal(1, found.GetProperty("total").GetInt64());
        Assert.Equal(number, found.GetProperty("items")[0].GetProperty("number").GetString());
        Assert.Equal(8, found.GetProperty("facets")[0].GetProperty("min").GetDecimal());

        // Class hierarchy export (JSON and CSV), a dry-run import, and the whole-configuration export with a dry-run import of it.
        var json = await admin.GetStringAsync("/api/v1/admin/classification/export");
        Assert.Contains("keel-classification/1", json, StringComparison.Ordinal);
        var csv = await admin.GetStringAsync("/api/v1/admin/classification/export?format=csv");
        Assert.StartsWith("record,code,parent", csv, StringComparison.Ordinal);
        var dry = await admin.PostAsync("/api/v1/admin/classification/import?format=csv&dryRun=true", new StringContent(csv, Encoding.UTF8, "text/csv"));
        Assert.Equal(HttpStatusCode.OK, dry.StatusCode);
        Assert.Equal(2, (await Json(dry)).GetProperty("unchanged").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/v1/admin/classification/import", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);

        var config = await admin.GetStringAsync("/api/v1/admin/config/export");
        var plan = await Json(await admin.PostAsync("/api/v1/admin/config/import?dryRun=true", new StringContent(config, Encoding.UTF8, "application/json")));
        Assert.True(plan.GetProperty("dryRun").GetBoolean());
        Assert.Equal(0, plan.GetProperty("changes").GetArrayLength());
        var history = await Json(await admin.GetAsync("/api/v1/admin/config/history?limit=20"));
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("action").GetString() == "admin.class_create");
    }
}
