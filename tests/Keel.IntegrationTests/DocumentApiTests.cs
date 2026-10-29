using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Keel.Api.Endpoints;
using PdfSharp.Pdf.IO;

namespace Keel.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class DocumentApiTests(PostgresFixture pg)
{
    private static readonly string Alice = ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"]);
    private static readonly string Lena = ApiFactory.Token(sub: "u-lena", usPerson: false, roles: ["plm-viewer", "plm-author"]);

    private const string ItarMarkings = """
        {"exportJurisdiction":"Itar","distributionStatement":"D","owningOrg":"ENG","usmlCategory":"XI(d)","cuiCategories":["EXPT"],
         "controllingOffice":"ACME","distributionReason":"Critical technology","program":"ORCA"}
        """;

    private static string Metadata(string name, string type = "Drawing", string? markings = null) =>
        $$"""{"name":"{{name}}","documentType":"{{type}}","description":"Synthetic","markings":{{markings ?? ItarMarkings}}}""";

    private static MultipartFormDataContent Form(string? metadata, (string Name, byte[] Bytes, string Type)? file, string? role = null)
    {
        var form = new MultipartFormDataContent();
        if (metadata is not null) form.Add(new StringContent(metadata, Encoding.UTF8, "application/json"), "metadata");
        if (role is not null) form.Add(new StringContent(role), "role");
        if (file is { } f)
        {
            var content = new ByteArrayContent(f.Bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(f.Type);
            form.Add(content, "file", f.Name);
        }
        return form;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res) => JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Document_lifecycle_and_marked_downloads_over_http()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);

        var pdf = DocumentServiceTests.SamplePdf(3);
        var created = await alice.PostAsync("/api/v1/documents", Form(Metadata("Housing drawing"), ("housing.pdf", pdf, "application/pdf")));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/v1/documents/D-000001", created.Headers.Location!.ToString());
        var doc = await Json(created);
        var banner = doc.GetProperty("banner").GetString()!;
        Assert.Equal("CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA", banner);
        var primary = doc.GetProperty("iteration").GetProperty("files")[0];
        Assert.Equal(("primary", "housing.pdf", "application/pdf"), (primary.GetProperty("role").GetString(), primary.GetProperty("fileName").GetString(), primary.GetProperty("mediaType").GetString()));

        // Stamped PDF download with the markings header.
        var dl = await alice.GetAsync($"/api/v1/documents/D-000001/files/{primary.GetProperty("id").GetString()}/download");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal("application/pdf", dl.Content.Headers.ContentType!.MediaType);
        Assert.Equal("housing.pdf", dl.Content.Headers.ContentDisposition!.FileNameStar ?? dl.Content.Headers.ContentDisposition.FileName);
        var header = dl.Headers.GetValues(DocumentEndpoints.MarkingsHeader).Single();
        Assert.Equal("CUI//EXPT %C2%B7 ITAR USML XI(d) %C2%B7 DISTRIBUTION D %C2%B7 PROGRAM ORCA", header);
        Assert.Equal(banner, Uri.UnescapeDataString(header));
        using (var stamped = PdfReader.Open(new MemoryStream(await dl.Content.ReadAsByteArrayAsync()), PdfDocumentOpenMode.Import))
            Assert.Equal(3, stamped.PageCount);

        // Check out, add an attachment through multipart, check in.
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync("/api/v1/documents/D-000001/checkout", null)).StatusCode);
        var csv = Encoding.UTF8.GetBytes("load,case\n1,2\n" + Guid.NewGuid());
        var upload = await alice.PostAsync("/api/v1/documents/D-000001/working-copy/files", Form(null, ("loads.csv", csv, "text/csv"), "attachment"));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var working = (await Json(upload)).GetProperty("checkout").GetProperty("files");
        Assert.Equal(2, working.GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await alice.PutAsJsonAsync("/api/v1/documents/D-000001/working-copy", new { description = "With load table" })).StatusCode);
        var checkin = await Json(await alice.PostAsJsonAsync("/api/v1/documents/D-000001/checkin", new { comment = "Loads" }));
        Assert.Equal("A.2", checkin.GetProperty("iteration").GetProperty("label").GetString());
        var attachment = checkin.GetProperty("iteration").GetProperty("files").EnumerateArray().Single(f => f.GetProperty("role").GetString() == "attachment");

        // Non-PDF: marked ZIP.
        var zipRes = await alice.GetAsync($"/api/v1/documents/D-000001/files/{attachment.GetProperty("id").GetString()}/download");
        Assert.Equal("application/zip", zipRes.Content.Headers.ContentType!.MediaType);
        using (var zip = new ZipArchive(new MemoryStream(await zipRes.Content.ReadAsByteArrayAsync())))
            Assert.Equal(["MARKINGS.pdf", "MARKINGS.txt", "loads.csv"], zip.Entries.Select(e => e.FullName));

        // Remove a working-copy file and undo.
        await alice.PostAsync("/api/v1/documents/D-000001/checkout", null);
        var co = await Json(await alice.GetAsync("/api/v1/documents/D-000001"));
        var workingAttachment = co.GetProperty("checkout").GetProperty("files").EnumerateArray().Single(f => f.GetProperty("role").GetString() == "attachment");
        var del = await alice.DeleteAsync($"/api/v1/documents/D-000001/working-copy/files/{workingAttachment.GetProperty("id").GetString()}");
        Assert.Equal(1, (await Json(del)).GetProperty("checkout").GetProperty("files").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync("/api/v1/documents/D-000001/undo-checkout", null)).StatusCode);

        // 7.6: review is entered through the workflow engine; the manual state endpoint refuses.
        var state = await alice.PostAsJsonAsync("/api/v1/documents/D-000001/state", new { toState = "UnderReview" });
        Assert.Equal(HttpStatusCode.Conflict, state.StatusCode);

        var search = await alice.GetFromJsonAsync<JsonElement>("/api/v1/documents?q=housing&type=Drawing");
        Assert.Equal(1, search.GetProperty("total").GetInt64());

        var meta = await alice.GetFromJsonAsync<JsonElement>("/api/v1/metadata/documents");
        Assert.Equal(6, meta.GetProperty("documentTypes").GetArrayLength());
        Assert.Equal(2L * 1024 * 1024 * 1024, meta.GetProperty("maxUploadBytes").GetInt64());
        Assert.Equal("document-default", meta.GetProperty("lifecycle").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Unauthorized_users_cannot_see_or_download_and_uploads_are_validated()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var created = await Json(await alice.PostAsync("/api/v1/documents", Form(Metadata("Seeker specification", "Specification"), ("seeker.txt", Encoding.UTF8.GetBytes("seeker " + Guid.NewGuid()), "text/plain"))));
        var fileId = created.GetProperty("iteration").GetProperty("files")[0].GetProperty("id").GetString();

        var lena = api.ClientFor(Lena);
        Assert.Equal(HttpStatusCode.NotFound, (await lena.GetAsync("/api/v1/documents/D-000001")).StatusCode);
        var denied = await lena.GetAsync($"/api/v1/documents/D-000001/files/{fileId}/download");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.False(denied.Headers.Contains(DocumentEndpoints.MarkingsHeader));
        Assert.Equal(0, (await lena.GetFromJsonAsync<JsonElement>("/api/v1/documents?q=seeker")).GetProperty("total").GetInt64());

        // Malformed or misordered multipart requests are rejected cleanly.
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/v1/documents", new { name = "x" })).StatusCode);
        var fileFirst = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "a.bin" }, { new StringContent(Metadata("Late metadata")), "metadata" } };
        var misordered = await alice.PostAsync("/api/v1/documents", fileFirst);
        Assert.Equal(HttpStatusCode.BadRequest, misordered.StatusCode);
        Assert.Contains("metadata", await misordered.Content.ReadAsStringAsync());
        var badName = await alice.PostAsync("/api/v1/documents", Form(Metadata("Bad name"), ("evil‮txt.exe", [1], "application/octet-stream")));
        Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);
        Assert.Contains("control or formatting", await badName.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsync("/api/v1/documents", Form("{not json", null))).StatusCode);

        // Without a file the document is created with no primary yet.
        var bare = await alice.PostAsync("/api/v1/documents", Form(Metadata("Placeholder", "Other", """{"exportJurisdiction":"None","distributionStatement":"A","owningOrg":"ENG"}"""), null));
        Assert.Equal(HttpStatusCode.Created, bare.StatusCode);
        Assert.Equal(0, (await Json(bare)).GetProperty("iteration").GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task Part_document_links_over_http()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var part = new
        {
            name = "Bracket", partType = "Component", defaultUnit = "EA", source = "Make", attributes = new { },
            markings = new { exportJurisdiction = "None", distributionStatement = "A", owningOrg = "ENG" },
        };
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/v1/parts", part)).StatusCode);
        await alice.PostAsync("/api/v1/documents", Form(Metadata("Bracket drawing"), ("bracket.txt", Encoding.UTF8.GetBytes("dwg " + Guid.NewGuid()), "text/plain")));

        var notCheckedOut = await alice.PostAsJsonAsync("/api/v1/parts/P-000001/working-copy/documents", new { documentNumber = "D-000001", linkType = "describes" });
        Assert.Equal(HttpStatusCode.Conflict, notCheckedOut.StatusCode);
        await alice.PostAsync("/api/v1/parts/P-000001/checkout", null);
        var added = await Json(await alice.PostAsJsonAsync("/api/v1/parts/P-000001/working-copy/documents", new { documentNumber = "D-000001", linkType = "describes" }));
        var link = added.GetProperty("workingLinks")[0];
        Assert.Equal(("describes", "A", "D-000001"), (link.GetProperty("linkType").GetString(), link.GetProperty("pinnedRevision").GetString(),
            link.GetProperty("document").GetProperty("number").GetString()));
        await alice.PostAsJsonAsync("/api/v1/parts/P-000001/checkin", new { comment = "Linked drawing" });

        var links = await alice.GetFromJsonAsync<JsonElement>("/api/v1/parts/P-000001/documents");
        Assert.Equal("A.2", links.GetProperty("iterationLabel").GetString());
        Assert.Equal(1, links.GetProperty("links").GetArrayLength());
        Assert.Equal(0, (await alice.GetFromJsonAsync<JsonElement>("/api/v1/parts/P-000001/documents?revision=A&iteration=1")).GetProperty("links").GetArrayLength());

        // Lena may read the open part but not the ITAR drawing: a redacted placeholder.
        var lena = await api.ClientFor(Lena).GetFromJsonAsync<JsonElement>("/api/v1/parts/P-000001/documents");
        var placeholder = lena.GetProperty("links")[0];
        Assert.True(placeholder.GetProperty("redacted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, placeholder.GetProperty("document").ValueKind);
        Assert.DoesNotContain("D-000001", lena.GetRawText());
        Assert.DoesNotContain("ITAR", lena.GetRawText());

        var reverse = await alice.GetFromJsonAsync<JsonElement>("/api/v1/documents/D-000001/parts");
        Assert.Equal("P-000001", reverse.GetProperty("parts")[0].GetProperty("partNumber").GetString());

        await alice.PostAsync("/api/v1/parts/P-000001/checkout", null);
        var working = await alice.GetFromJsonAsync<JsonElement>("/api/v1/parts/P-000001/documents");
        var workingId = working.GetProperty("workingLinks")[0].GetProperty("linkId").GetString();
        var removed = await alice.DeleteAsync($"/api/v1/parts/P-000001/working-copy/documents/{workingId}");
        Assert.Equal(0, (await Json(removed)).GetProperty("workingLinks").GetArrayLength());
    }

    [Fact]
    public async Task Documents_are_reviewed_signed_and_released_over_http_with_the_part_workflow_contract()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var rita = ApiFactory.Token(sub: "u-rita", roles: ["plm-viewer", "plm-author"]);
        var bob = ApiFactory.Token(sub: "u-bob", roles: ["plm-viewer", "plm-approver"]);
        foreach (var t in new[] { rita, bob }) Assert.Equal(HttpStatusCode.OK, (await api.ClientFor(t).GetAsync("/api/v1/tasks")).StatusCode);

        var bare = await alice.PostAsync("/api/v1/documents", Form(Metadata("No file yet"), null));
        Assert.Equal(HttpStatusCode.Created, bare.StatusCode);
        var noFile = await alice.PostAsJsonAsync("/api/v1/documents/D-000001/review", new { participants = new { reviewer = new[] { "rita" }, approvers = new[] { "bob" } } });
        Assert.Equal(HttpStatusCode.Conflict, noFile.StatusCode);
        Assert.Contains("primary file", await noFile.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsync("/api/v1/documents", Form(Metadata("Housing specification", "Specification"), ("housing.txt", Encoding.UTF8.GetBytes("housing " + Guid.NewGuid()), "text/plain")))).StatusCode);
        var lena = api.ClientFor(Lena);
        Assert.Equal(HttpStatusCode.NotFound, (await lena.GetAsync("/api/v1/documents/D-000002/workflow")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await lena.PostAsJsonAsync("/api/v1/documents/D-000002/review", new { participants = new { reviewer = new[] { "rita" }, approvers = new[] { "bob" } } })).StatusCode);

        var started = await alice.PostAsJsonAsync("/api/v1/documents/D-000002/review", new { participants = new { reviewer = new[] { "rita" }, approvers = new[] { "bob" } }, comment = "Please review" });
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var instance = await Json(started);
        Assert.Equal("document", instance.GetProperty("target").GetProperty("objectType").GetString());
        Assert.Equal("UnderReview", (await alice.GetFromJsonAsync<JsonElement>("/api/v1/documents/D-000002")).GetProperty("revision").GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsync("/api/v1/documents/D-000002/checkout", null)).StatusCode);

        var ritaClient = api.ClientFor(rita);
        var task = (await ritaClient.GetFromJsonAsync<JsonElement>("/api/v1/tasks")).GetProperty("items")[0];
        Assert.Equal("D-000002", task.GetProperty("target").GetProperty("key").GetString());
        Assert.Equal(HttpStatusCode.OK, (await ritaClient.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("id").GetGuid()}/sign", new { reasonCode = "Reviewed" })).StatusCode);
        var bobClient = api.ClientFor(bob);
        var approval = (await bobClient.GetFromJsonAsync<JsonElement>("/api/v1/tasks")).GetProperty("items")[0];
        Assert.Equal(HttpStatusCode.OK, (await bobClient.PostAsJsonAsync($"/api/v1/tasks/{approval.GetProperty("id").GetGuid()}/sign", new { reasonCode = "Approved" })).StatusCode);

        var released = await alice.GetFromJsonAsync<JsonElement>("/api/v1/documents/D-000002");
        Assert.Equal("Released", released.GetProperty("revision").GetProperty("stateLabel").GetString());
        var history = await alice.GetFromJsonAsync<JsonElement>("/api/v1/documents/D-000002/workflow");
        Assert.Equal(2, history.GetProperty("releases")[0].GetProperty("signatures").GetArrayLength());
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync("/api/v1/documents/D-000002/state", new { toState = "InWork" })).StatusCode);
    }

    [Fact]
    public async Task Readiness_includes_the_vault()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var ready = await api.CreateClient().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", body.RootElement.GetProperty("checks").GetProperty("vault").GetProperty("status").GetString());
    }
}
