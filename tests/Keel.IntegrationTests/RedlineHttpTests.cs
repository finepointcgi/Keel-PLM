using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Keel.Api.Endpoints;
using UglyToad.PdfPig;

namespace Keel.IntegrationTests;

/// <summary>The HTTP surface of cross-document compare and redline review on the real host (increment 9.5, ADR-0053).</summary>
[Collection(PostgresCollection.Name)]
public sealed class RedlineHttpTests(PostgresFixture pg)
{
    private static readonly string Alice = ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"]);
    private static readonly string Bob = ApiFactory.Token(sub: "u-bob", roles: ["plm-viewer", "plm-approver"]);
    private static readonly string Lena = ApiFactory.Token(sub: "u-lena", usPerson: false, roles: ["plm-viewer", "plm-author"]);
    private static readonly string Erp = ApiFactory.Token(sub: "u-erp", roles: ["plm-integration"]);

    private const string ItarMarkings = """
        {"exportJurisdiction":"Itar","distributionStatement":"D","owningOrg":"ENG","usmlCategory":"XI(d)","cuiCategories":["EXPT"],
         "controllingOffice":"ACME","distributionReason":"Critical technology","program":"ORCA"}
        """;

    private static async Task<string> CreateAsync(HttpClient client, string text, string name)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent($$"""{"name":"{{name}}","documentType":"Specification","markings":{{ItarMarkings}}}""", Encoding.UTF8, "application/json"), "metadata" },
        };
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "file", "spec.txt");
        var res = await client.PostAsync("/api/v1/documents", form);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("number").GetString()!;
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync());

    [Fact]
    public async Task Compare_comment_resolve_list_and_export_work_over_rest_and_refuse_what_the_service_refuses()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var bob = api.ClientFor(Bob);
        var one = await CreateAsync(alice, "The wall is 4 mm thick.", "Spec one");
        var two = await CreateAsync(alice, "The wall is 6 mm thick.", "Spec two");

        using var compare = await JsonAsync(await alice.GetAsync($"/api/v1/documents/compare?left={one}&right={two}"));
        var root = compare.RootElement;
        Assert.Equal("text", root.GetProperty("mode").GetString());
        Assert.Equal(1, root.GetProperty("diff").GetProperty("counts").GetProperty("changes").GetInt32());
        Assert.Contains("ITAR", root.GetProperty("banner").GetString());

        var posted = await alice.PostAsJsonAsync("/api/v1/documents/compare/comments", new { left = one, right = two, change = 0, body = "Check the wall" });
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Guid thread, review;
        using (var p = await JsonAsync(posted)) { thread = p.RootElement.GetProperty("id").GetGuid(); review = p.RootElement.GetProperty("reviewId").GetGuid(); }
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/v1/documents/compare/comments", new { left = one, right = two, change = 5, body = "Nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/v1/documents/compare/comments", new { left = one, right = two, change = 0, body = " " })).StatusCode);

        using (var list = await JsonAsync(await bob.GetAsync($"/api/v1/documents/{two}/redlines")))
        {
            var row = Assert.Single(list.RootElement.EnumerateArray());
            Assert.Equal(review, row.GetProperty("reviewId").GetGuid());
            Assert.Equal(1, row.GetProperty("openThreads").GetInt32());
        }
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync($"/api/v1/documents/compare/threads/{thread}/resolve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PostAsync($"/api/v1/documents/compare/threads/{thread}/resolve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync($"/api/v1/documents/compare/threads/{thread}/reopen", null)).StatusCode);

        var pdf = await alice.GetAsync($"/api/v1/documents/compare/report?left={one}&right={two}&format=pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Contains("DISTRIBUTION D", pdf.Headers.GetValues(DocumentEndpoints.MarkingsHeader).Single(), StringComparison.Ordinal);
        Assert.Contains("attachment", pdf.Content.Headers.ContentDisposition!.ToString());
        using (var doc = PdfDocument.Open(await pdf.Content.ReadAsByteArrayAsync())) Assert.Contains("Check the wall", string.Concat(doc.GetPages().Select(pg => pg.Text)));
        var html = await alice.GetAsync($"/api/v1/documents/compare/report?left={one}&right={two}&format=html");
        Assert.StartsWith("text/html", html.Content.Headers.ContentType!.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/v1/documents/compare/report?left={one}&right={two}&format=exe")).StatusCode);

        // The service-level rule for callers who may read but not download (facts only) is covered by RedlineTests; the API policy lets the integration role in no further than the pull API.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.ClientFor(Erp).GetAsync($"/api/v1/documents/compare?left={one}&right={two}")).StatusCode);
    }

    [Fact]
    public async Task A_caller_who_cannot_read_either_document_gets_not_found_on_every_route()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var lena = api.ClientFor(Lena);
        var one = await CreateAsync(alice, "Alpha beta.", "Spec one");
        var two = await CreateAsync(alice, "Alpha gamma.", "Spec two");
        var posted = await alice.PostAsJsonAsync("/api/v1/documents/compare/comments", new { left = one, right = two, change = 0, body = "Remark" });
        Guid thread;
        using (var p = await JsonAsync(posted)) thread = p.RootElement.GetProperty("id").GetGuid();

        foreach (var url in new[]
        {
            $"/api/v1/documents/compare?left={one}&right={two}", $"/api/v1/documents/compare/report?left={one}&right={two}&format=pdf",
        })
        {
            var res = await lena.GetAsync(url);
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            Assert.DoesNotContain("Remark", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await lena.PostAsJsonAsync("/api/v1/documents/compare/comments", new { left = one, right = two, change = 0, body = "Sneaky" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await lena.PostAsync($"/api/v1/documents/compare/threads/{thread}/resolve", null)).StatusCode);
        using var list = await JsonAsync(await lena.GetAsync($"/api/v1/documents/{one}/redlines"));
        Assert.Equal(0, list.RootElement.GetArrayLength());
    }
}
