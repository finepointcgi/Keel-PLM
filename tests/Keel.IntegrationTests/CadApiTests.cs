using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Keel.IntegrationTests;

/// <summary>CAD connector protocol over HTTP against the real host, PostgreSQL and vault (ADR-0035).</summary>
[Collection(PostgresCollection.Name)]
public sealed class CadApiTests(PostgresFixture pg)
{
    internal static readonly string Alice = ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"]);
    internal static readonly string Bob = ApiFactory.Token(sub: "u-bob", roles: ["plm-viewer", "plm-author"]);
    internal static readonly string Lena = ApiFactory.Token(sub: "u-lena", usPerson: false, roles: ["plm-viewer", "plm-author"]);
    internal static readonly string Viewer = ApiFactory.Token(sub: "u-vic", roles: ["plm-viewer"]);

    internal const string Itar = """{"exportJurisdiction":"Itar","distributionStatement":"D","owningOrg":"ENG","usmlCategory":"XI(d)","cuiCategories":["EXPT"],"controllingOffice":"ACME","distributionReason":"Critical technology","program":"ORCA"}""";
    internal const string Open = """{"exportJurisdiction":"None","distributionStatement":"A","owningOrg":"ENG"}""";

    internal static async Task<JsonElement> Json(HttpResponseMessage res) => JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    internal static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    internal static async Task<string> CreateDocument(HttpClient c, string name, string type, byte[] bytes, string fileName, string markings = Open)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent($$"""{"name":"{{name}}","documentType":"{{type}}","markings":{{markings}}}""", Encoding.UTF8, "application/json"), "metadata" },
        };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        var res = await c.PostAsync("/api/v1/documents", form);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await Json(res)).GetProperty("number").GetString()!;
    }

    internal static async Task<HttpResponseMessage> Upload(HttpClient c, string setId, string number, string fileName, byte[] bytes, string? sha = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/cad/checkouts/{setId}/members/{number}/files?fileName={fileName}") { Content = new ByteArrayContent(bytes) };
        req.Headers.Add("X-Content-SHA256", sha ?? Sha(bytes));
        return await c.SendAsync(req);
    }

    internal static async Task<JsonElement> CheckOut(HttpClient c, string top, object? extra = null)
    {
        var res = await c.PostAsJsonAsync("/api/v1/cad/packages/checkout", new { top });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return await Json(res);
    }

    [Fact]
    public async Task A_package_is_checked_out_changed_and_checked_in_as_a_set_and_staleness_follows_the_model()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var model = await CreateDocument(alice, "Housing model", "CAD", Encoding.UTF8.GetBytes("model v1"), "housing.prt", Itar);
        var drawing = await CreateDocument(alice, "Housing drawing", "Drawing", Encoding.UTF8.GetBytes("drawing v1"), "housing.drw", Itar);

        // The drawing is checked in once, saying it was made from the model: that records the dependency.
        var first = await CheckOut(alice, drawing);
        var firstId = first.GetProperty("id").GetString()!;
        Assert.Single(first.GetProperty("members").EnumerateArray());
        Assert.Equal((HttpStatusCode.OK), (await Upload(alice, firstId, drawing, "housing.drw", Encoding.UTF8.GetBytes("drawing v2"))).StatusCode);
        var checkin1 = await alice.PostAsJsonAsync($"/api/v1/cad/checkouts/{firstId}/checkin", new { comment = "Linked", members = new[] { new { number = drawing, references = new[] { model } } } });
        Assert.Equal(HttpStatusCode.OK, checkin1.StatusCode);
        Assert.True((await Json(checkin1)).GetProperty("setClosed").GetBoolean());

        // Now the package of the model brings its drawing along, with roles.
        var package = await CheckOut(alice, model);
        var setId = package.GetProperty("id").GetString()!;
        var members = package.GetProperty("members").EnumerateArray().ToDictionary(m => m.GetProperty("number").GetString()!, m => m);
        Assert.Equal(2, members.Count);
        Assert.Equal("drawing", members[drawing].GetProperty("cadRole").GetString());
        Assert.Equal("model", members[model].GetProperty("cadRole").GetString());
        Assert.Contains("ITAR", package.GetProperty("banner").GetString());

        // A second user cannot take any of them, and the failure took nothing.
        var bob = api.ClientFor(Bob);
        var denied = await bob.PostAsJsonAsync("/api/v1/cad/packages/checkout", new { top = drawing });
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal("cad.locked", (await Json(denied)).GetProperty("code").GetString());

        // Download: the unchanged bytes inside the marked ZIP.
        var modelFile = members[model].GetProperty("files")[0];
        var dl = await alice.GetAsync($"/api/v1/cad/checkouts/{setId}/members/{model}/files/{modelFile.GetProperty("sha256").GetString()}");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Contains("ITAR", Uri.UnescapeDataString(dl.Headers.GetValues("X-Keel-Markings").Single()));
        using (var zip = new ZipArchive(new MemoryStream(await dl.Content.ReadAsByteArrayAsync())))
        {
            using var s = zip.GetEntry("housing.prt")!.Open();
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            Assert.Equal("model v1", Encoding.UTF8.GetString(ms.ToArray()));
        }

        // Integrity: wrong declared hash is refused and nothing is recorded.
        var bad = await Upload(alice, setId, model, "housing.prt", Encoding.UTF8.GetBytes("model v2"), new string('a', 64));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal("cad.integrity_mismatch", (await Json(bad)).GetProperty("code").GetString());
        var unchanged = await Json(await alice.GetAsync($"/api/v1/cad/checkouts/{setId}"));
        Assert.Equal(modelFile.GetProperty("sha256").GetString(), unchanged.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("number").GetString() == model).GetProperty("files")[0].GetProperty("sha256").GetString());

        // Change the model; check the whole set in (the drawing is iterated too, without declaring references).
        Assert.Equal(HttpStatusCode.OK, (await Upload(alice, setId, model, "housing.prt", Encoding.UTF8.GetBytes("model v2"))).StatusCode);
        var checkin = await alice.PostAsJsonAsync($"/api/v1/cad/checkouts/{setId}/checkin", new { comment = "Wall thickness" });
        Assert.Equal(HttpStatusCode.OK, checkin.StatusCode);
        var created = (await Json(checkin)).GetProperty("created").EnumerateArray().ToDictionary(c => c.GetProperty("number").GetString()!, c => c.GetProperty("iteration").GetInt32());
        Assert.Equal(2, created.Count);
        Assert.Equal(2, created[model]); // the model was created as iteration 1 and has not been checked in before: this check-in makes iteration 2

        // The drawing now says it was built against model iteration 2, which is no longer the latest.
        var view = await Json(await alice.GetAsync($"/api/v1/cad/documents/{drawing}/package"));
        Assert.Equal(1, view.GetProperty("staleCount").GetInt32());
        Assert.True(view.GetProperty("uses")[0].GetProperty("stale").GetBoolean());
        var stale = await Json(await alice.GetAsync("/api/v1/cad/stale"));
        Assert.Equal(drawing, stale.GetProperty("items")[0].GetProperty("number").GetString());
        var whereUsed = await Json(await alice.GetAsync($"/api/v1/cad/documents/{model}/where-used"));
        Assert.Equal(drawing, whereUsed[0].GetProperty("number").GetString());

        // Regenerating the drawing against the current model clears it.
        var again = await CheckOut(alice, drawing);
        var againId = again.GetProperty("id").GetString()!;
        // (checking out the drawing alone: its package also contains the model it references)
        Assert.Equal(2, again.GetProperty("members").GetArrayLength());
        var clear = await alice.PostAsJsonAsync($"/api/v1/cad/checkouts/{againId}/checkin", new { skipUnchanged = true, members = new object[] { new { number = drawing, references = new[] { model } }, new { number = model } } });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        var cleared = await Json(clear);
        Assert.Equal(1, cleared.GetProperty("released").GetArrayLength());
        var after = await Json(await alice.GetAsync($"/api/v1/cad/documents/{drawing}/package"));
        Assert.Equal(0, after.GetProperty("staleCount").GetInt32());
    }

    [Fact]
    public async Task A_set_checkin_is_atomic_when_one_member_is_refused_and_unreadable_documents_do_not_exist()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var secret = await CreateDocument(alice, "Secret model", "CAD", Encoding.UTF8.GetBytes("s"), "s.prt", Itar);
        var open = await CreateDocument(alice, "Open model", "CAD", Encoding.UTF8.GetBytes("o"), "o.prt");
        // A non-US person: the ITAR document does not exist for her, in the same words as a missing one.
        var lena = api.ClientFor(Lena);
        var hidden = await lena.PostAsJsonAsync("/api/v1/cad/packages/checkout", new { top = secret });
        var missing = await lena.PostAsJsonAsync("/api/v1/cad/packages/checkout", new { top = "D-999999" });
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        // Everything but the per-request trace id is identical (status, type, title, detail, instance and any other member).
        static string WithoutTrace(string body)
        {
            var o = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
            Assert.True(o.Remove("traceId"));
            return o.ToJsonString();
        }
        Assert.Equal(WithoutTrace(await missing.Content.ReadAsStringAsync()), WithoutTrace(await hidden.Content.ReadAsStringAsync()));
        Assert.Equal(HttpStatusCode.NotFound, (await lena.GetAsync($"/api/v1/cad/documents/{secret}/package")).StatusCode);

        // Atomic check-in: a member without its required primary file keeps the whole set open and creates no iteration.
        var set = await CheckOut(alice, open);
        var setId = set.GetProperty("id").GetString()!;
        var added = await alice.PostAsJsonAsync($"/api/v1/cad/checkouts/{setId}/members", new { name = "New drawing", documentType = "Drawing", cadRole = "drawing", references = new[] { open } });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var newNumber = (await Json(added)).GetProperty("number").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Upload(alice, setId, open, "o.prt", Encoding.UTF8.GetBytes("o2"))).StatusCode);
        // Unknown member and unknown reference are refused with protocol codes.
        var unknown = await alice.PostAsJsonAsync($"/api/v1/cad/checkouts/{setId}/checkin", new { members = new[] { new { number = "D-424242" } } });
        Assert.Equal("cad.member_unknown", (await Json(unknown)).GetProperty("code").GetString());
        var badRef = await alice.PostAsJsonAsync($"/api/v1/cad/checkouts/{setId}/checkin", new { members = new[] { new { number = open, references = new[] { "D-999999" } } } }); // alice may see the ITAR document, so a reference to a missing one is the refusal case
        Assert.Equal("cad.reference_unknown", (await Json(badRef)).GetProperty("code").GetString());
        var lenaSet = await lena.GetAsync($"/api/v1/cad/checkouts/{setId}");
        Assert.Equal(HttpStatusCode.NotFound, lenaSet.StatusCode);

        // Now give the new drawing its file and check in: both iterate together.
        Assert.Equal(HttpStatusCode.OK, (await Upload(alice, setId, newNumber, "n.drw", Encoding.UTF8.GetBytes("n"))).StatusCode);
        var ok = await alice.PostAsJsonAsync($"/api/v1/cad/checkouts/{setId}/checkin", new { comment = "Both", members = new object[] { new { number = open }, new { number = newNumber, references = new[] { open } } } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var docs = await Json(await alice.GetAsync($"/api/v1/documents/{open}"));
        Assert.Equal("A.2", docs.GetProperty("iteration").GetProperty("label").GetString());
        // Undo of a closed set is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsync($"/api/v1/cad/checkouts/{setId}/undo", null)).StatusCode);
    }

    [Fact]
    public async Task Undo_releases_every_lock_of_the_set()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var a = await CreateDocument(alice, "A", "CAD", Encoding.UTF8.GetBytes("a"), "a.prt");
        var set = await CheckOut(alice, a);
        var id = set.GetProperty("id").GetString()!;
        Assert.True((await Json(await alice.GetAsync($"/api/v1/documents/{a}"))).GetProperty("checkout").ValueKind == JsonValueKind.Object);
        var viewer = api.ClientFor(Viewer);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/v1/cad/checkouts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync($"/api/v1/cad/checkouts/{id}/undo", null)).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(await alice.GetAsync($"/api/v1/documents/{a}"))).GetProperty("checkout").ValueKind);
        Assert.Empty((await Json(await alice.GetAsync("/api/v1/cad/checkouts"))).EnumerateArray());
    }

    [Fact]
    public async Task The_cad_openapi_document_equals_the_committed_one_and_is_3_1()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var generated = await api.ClientFor(Viewer).GetStringAsync("/openapi/cad.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(generated)!;
        Assert.StartsWith("3.1", json["openapi"]!.GetValue<string>(), StringComparison.Ordinal);
        var paths = json["paths"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("/api/v1/cad/packages/checkout", paths);
        Assert.Contains("/api/v1/cad/checkouts/{id}/checkin", paths);
        Assert.Contains("/api/v1/cad/bom-proposals/{number}/apply", paths);
        Assert.DoesNotContain(paths, p => !p.StartsWith("/api/v1/cad", StringComparison.Ordinal));
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Keel.slnx"))) root = root.Parent!;
        var file = Path.Combine(root.FullName, "docs", "integration", "cad-openapi.json");
        var normalized = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("KEEL_UPDATE_OPENAPI") == "1") { await File.WriteAllTextAsync(file, normalized); return; }
        Assert.True(normalized == await File.ReadAllTextAsync(file), "docs/integration/cad-openapi.json differs from the generated document. Run scripts/update-openapi.sh and commit the result.");
    }
}
