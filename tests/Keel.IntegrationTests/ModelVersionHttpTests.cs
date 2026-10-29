using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Keel.Api.Endpoints;
using Keel.Core.Geometry;
using Keel.TestSupport;
using Keel.Viewing;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>The HTTP surface of model versions on the real host (increment 9.1, ADR-0046): REST, the compare page, the triangle stream and the versions ZIP.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ModelVersionHttpTests(PostgresFixture pg)
{
    private static readonly string Alice = ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"]);
    private static readonly string Lena = ApiFactory.Token(sub: "u-lena", usPerson: false, roles: ["plm-viewer", "plm-author"]);

    private const string ItarMarkings = """
        {"exportJurisdiction":"Itar","distributionStatement":"D","owningOrg":"ENG","usmlCategory":"XI(d)","cuiCategories":["EXPT"],
         "controllingOffice":"ACME","distributionReason":"Critical technology","program":"ORCA"}
        """;

    private static async Task<string> CreateAsync(HttpClient client, string fileName, byte[] bytes)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent($$"""{"name":"Model {{fileName}}","documentType":"CAD","markings":{{ItarMarkings}}}""", Encoding.UTF8, "application/json"), "metadata" },
        };
        form.Add(new ByteArrayContent(bytes), "file", fileName);
        var res = await client.PostAsync("/api/v1/documents", form);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("number").GetString()!;
    }

    private static MultipartFormDataContent VersionForm(byte[] bytes, string? note, string name = "model.stl", int? expected = null)
    {
        var form = new MultipartFormDataContent();
        if (note is not null) form.Add(new StringContent(note), "note");
        if (expected is not null) form.Add(new StringContent(expected.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "expectedIteration");
        form.Add(new ByteArrayContent(bytes), "file", name);
        return form;
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync());

    private static byte[] Shape(double height) => SyntheticMesh.ToBinaryStl(SyntheticMesh.Bracket(40, height, 4, 20), "http " + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task Rest_upload_history_compare_and_restore_work_and_refuse_what_the_service_refuses()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var number = await CreateAsync(alice, "model.stl", Shape(30));

        var missingNote = await alice.PostAsync($"/api/v1/documents/{number}/versions", VersionForm(Shape(32), null));
        Assert.Equal(HttpStatusCode.BadRequest, missingNote.StatusCode);
        var v2 = Shape(32);
        var created = await alice.PostAsync($"/api/v1/documents/{number}/versions", VersionForm(v2, "Taller upright", expected: 1));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using (var detail = await JsonAsync(created)) Assert.Equal("A.2", detail.RootElement.GetProperty("iteration").GetProperty("label").GetString());
        var same = await alice.PostAsync($"/api/v1/documents/{number}/versions", VersionForm(v2, "Same again"));
        Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);
        Assert.Contains("identical", await same.Content.ReadAsStringAsync());
        var stale = await alice.PostAsync($"/api/v1/documents/{number}/versions", VersionForm(Shape(34), "stale", expected: 1));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var history = await JsonAsync(await alice.GetAsync($"/api/v1/documents/{number}/versions"));
        var versions = history.RootElement.GetProperty("versions");
        Assert.Equal(2, versions.GetArrayLength());
        Assert.Equal("analysed", versions[1].GetProperty("metrics").GetProperty("status").GetString());
        var a = versions[0].GetProperty("iterationId").GetString();
        var b = versions[1].GetProperty("iterationId").GetString();

        using var compare = await JsonAsync(await alice.GetAsync($"/api/v1/documents/{number}/versions/compare?a={a}&b={b}"));
        Assert.StartsWith("Taller by 2.0 units", compare.RootElement.GetProperty("delta").GetProperty("summary").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/v1/documents/{number}/versions/compare?a={a}&b={a}")).StatusCode);

        var restore = await alice.PostAsync($"/api/v1/documents/{number}/versions/{a}/restore?expectedIteration=2", null);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        using (var detail = await JsonAsync(restore)) Assert.Equal("A.3", detail.RootElement.GetProperty("iteration").GetProperty("label").GetString());
    }

    [Fact]
    public async Task A_caller_who_cannot_read_the_document_gets_not_found_everywhere()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var lena = api.ClientFor(Lena);
        var number = await CreateAsync(alice, "model.stl", Shape(30));
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync($"/api/v1/documents/{number}/versions", VersionForm(Shape(32), "v2"))).StatusCode);
        using var history = await JsonAsync(await alice.GetAsync($"/api/v1/documents/{number}/versions"));
        var a = history.RootElement.GetProperty("versions")[0].GetProperty("iterationId").GetString();
        var b = history.RootElement.GetProperty("versions")[1].GetProperty("iterationId").GetString();
        var fileId = history.RootElement.GetProperty("versions")[0].GetProperty("fileId").GetString();

        foreach (var url in new[]
        {
            $"/api/v1/documents/{number}/versions", $"/api/v1/documents/{number}/versions/compare?a={a}&b={b}",
            $"/viewer/mesh/documents/{number}/files/{fileId}", $"/downloads/documents/{number}/versions.zip?ids={a},{b}",
        })
            Assert.Equal(HttpStatusCode.NotFound, (await lena.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await lena.PostAsync($"/api/v1/documents/{number}/versions/{a}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await lena.PostAsync($"/api/v1/documents/{number}/versions", VersionForm(Shape(40), "x"))).StatusCode);

        var page = await lena.GetAsync($"/viewer/compare/documents/{number}?a={a}&b={b}");
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("UNCLASSIFIED", html, StringComparison.Ordinal); // neutral banner: there are no markings to show
        Assert.DoesNotContain("ITAR", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Model model.stl", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.Equal(ViewerCsp.Page, page.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task The_mesh_stream_is_access_checked_verified_uncached_sandboxed_and_audited_as_a_view()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var number = await CreateAsync(alice, "cube.stl", SyntheticMesh.ToBinaryStl(SyntheticMesh.Box(10, 10, 10)));
        using var history = await JsonAsync(await alice.GetAsync($"/api/v1/documents/{number}/versions"));
        var fileId = history.RootElement.GetProperty("versions")[0].GetProperty("fileId").GetString();

        var res = await alice.GetAsync($"/viewer/mesh/documents/{number}/files/{fileId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(12 * 36, bytes.Length);
        Assert.Equal("12", res.Headers.GetValues("X-Keel-Triangles").Single());
        Assert.Equal("z", res.Headers.GetValues("X-Keel-Up").Single());
        Assert.Equal("application/octet-stream", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store, max-age=0", res.Headers.CacheControl!.ToString());
        Assert.Equal(ViewerCsp.Content, res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("CUI//EXPT %C2%B7 ITAR USML XI(d) %C2%B7 DISTRIBUTION D %C2%B7 PROGRAM ORCA", res.Headers.GetValues(DocumentEndpoints.MarkingsHeader).Single());
        // 12 triangles of a 10 mm cube: every coordinate is 0 or 10
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        Assert.All(floats, f => Assert.True(f == 0f || f == 10f));

        await using var conn = await api.Database.AppDataSource().OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit.event WHERE action = 'document.view' AND outcome = 'Success' AND payload LIKE '%compare%'", conn);
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task The_mesh_stream_refuses_formats_it_does_not_read_and_hostile_files_with_a_plain_message()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var step = await CreateAsync(alice, "box.step", ViewerSamples.StepBox());
        var broken = await CreateAsync(alice, "broken.stl", Encoding.ASCII.GetBytes("solid x\nvertex 1 2\nendsolid x\n"));
        foreach (var (number, expected) in new[] { (step, HttpStatusCode.UnsupportedMediaType), (broken, HttpStatusCode.UnprocessableEntity) })
        {
            using var history = await JsonAsync(await alice.GetAsync($"/api/v1/documents/{number}/versions"));
            var fileId = history.RootElement.GetProperty("versions")[0].GetProperty("fileId").GetString();
            var res = await alice.GetAsync($"/viewer/mesh/documents/{number}/files/{fileId}");
            Assert.Equal(expected, res.StatusCode);
            Assert.Contains("detail", await res.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task The_compare_page_has_both_banners_the_distribution_statement_the_viewer_csp_and_loads_both_versions_through_the_mesh_route()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var number = await CreateAsync(alice, "bracket.stl", Shape(30));
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync($"/api/v1/documents/{number}/versions", VersionForm(Shape(32), "v2", "bracket.stl"))).StatusCode);
        using var history = await JsonAsync(await alice.GetAsync($"/api/v1/documents/{number}/versions"));
        var versions = history.RootElement.GetProperty("versions");
        var (a, b) = (versions[0].GetProperty("iterationId").GetString(), versions[1].GetProperty("iterationId").GetString());
        var (fa, fb) = (versions[0].GetProperty("fileId").GetString(), versions[1].GetProperty("fileId").GetString());

        var res = await alice.GetAsync($"/viewer/compare/documents/{number}?a={a}&b={b}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(ViewerCsp.Page, res.Headers.GetValues("Content-Security-Policy").Single());
        var html = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(html, @"data-viewer-banner"));
        Assert.Contains("CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA", html, StringComparison.Ordinal);
        Assert.Contains("DISTRIBUTION STATEMENT D", html, StringComparison.Ordinal);
        Assert.Contains("data-viewer-distribution", html, StringComparison.Ordinal);
        Assert.Contains($"data-mesh-a=\"/viewer/mesh/documents/{number}/files/{fa}\"", html, StringComparison.Ordinal);
        Assert.Contains($"data-mesh-b=\"/viewer/mesh/documents/{number}/files/{fb}\"", html, StringComparison.Ordinal);
        Assert.Contains("compare.js", html, StringComparison.Ordinal);
        Assert.Contains("Taller by 2.0 units", html, StringComparison.Ordinal);
        Assert.Contains("no automatic registration", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"<script(?![^>]*\bsrc=)", html);
        Assert.DoesNotMatch(@"\sstyle=""", html);

        // the compare itself is audited
        await using var conn = await api.Database.AppDataSource().OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit.event WHERE action = 'document.model_compare' AND outcome = 'Success'", conn);
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());

        // same version twice, or a step file: a neutral message, never a viewer that cannot work
        var same = await alice.GetAsync($"/viewer/compare/documents/{number}?a={a}&b={a}");
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
    }

    [Fact]
    public async Task The_versions_ZIP_streams_the_chosen_versions_and_the_cookie_route_refuses_a_bad_list()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var v1 = Shape(30);
        var number = await CreateAsync(alice, "bracket.stl", v1);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync($"/api/v1/documents/{number}/versions", VersionForm(Shape(32), "v2", "bracket.stl"))).StatusCode);
        using var history = await JsonAsync(await alice.GetAsync($"/api/v1/documents/{number}/versions"));
        var ids = string.Join(',', history.RootElement.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("iterationId").GetString()));

        var res = await alice.GetAsync($"/downloads/documents/{number}/versions.zip?ids={ids}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/zip", res.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("CUI//EXPT", Uri.UnescapeDataString(res.Headers.GetValues(DocumentEndpoints.MarkingsHeader).Single()), StringComparison.Ordinal);
        using var zip = new ZipArchive(new MemoryStream(await res.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        Assert.Contains(zip.Entries, e => e.FullName == "A.1/bracket.stl" && e.Length == v1.Length);
        Assert.Contains(zip.Entries, e => e.FullName == "A.2/bracket.stl");
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/downloads/documents/{number}/versions.zip?ids=not-a-guid")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/downloads/documents/{number}/versions.zip")).StatusCode);
    }
}
