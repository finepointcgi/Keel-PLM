using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.S3.Model;
using Keel.Api.Endpoints;
using Keel.Audit;
using Keel.Documents.Vault;
using Keel.TestSupport;
using Keel.Viewing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>The viewer's HTTP surface on the real host (increment 6.0, ADR-0027): headers, status codes, the viewer-only CSP and the unchanged strict policy everywhere else.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ViewerHttpTests(PostgresFixture pg)
{
    private static readonly string Alice = ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"]);
    private static readonly string Lena = ApiFactory.Token(sub: "u-lena", usPerson: false, roles: ["plm-viewer", "plm-author"]);

    /// <summary>What <c>UseSecurityHeaders</c> writes for the test host (the IdP origin comes from <see cref="ApiFactory.Issuer"/>).</summary>
    private const string GlobalCsp = "default-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self' https://idp.test";

    private const string ItarMarkings = """
        {"exportJurisdiction":"Itar","distributionStatement":"D","owningOrg":"ENG","usmlCategory":"XI(d)","cuiCategories":["EXPT"],
         "controllingOffice":"ACME","distributionReason":"Critical technology","program":"ORCA"}
        """;

    private static async Task<(string Number, string FileId, string Sha)> Upload(HttpClient client, string fileName, byte[] bytes, string mediaType = "application/octet-stream")
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent($$"""{"name":"Viewer {{fileName}}","documentType":"Drawing","markings":{{ItarMarkings}}}""", Encoding.UTF8, "application/json"), "metadata" },
        };
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        form.Add(content, "file", fileName);
        var res = await client.PostAsync("/api/v1/documents", form);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var file = doc.RootElement.GetProperty("iteration").GetProperty("files")[0];
        return (doc.RootElement.GetProperty("number").GetString()!, file.GetProperty("id").GetString()!, file.GetProperty("sha256").GetString()!);
    }

    private static string Csp(HttpResponseMessage r) => r.Headers.GetValues("Content-Security-Policy").Single();

    [Fact]
    public async Task Content_is_streamed_marked_inline_uncached_sandboxed_and_audited()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var stl = ViewerSamples.AsciiStlCube();
        var (number, fileId, sha) = await Upload(alice, "bracket.stl", stl, "text/plain");

        var res = await alice.GetAsync($"/viewer/content/documents/{number}/files/{fileId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(stl, await res.Content.ReadAsByteArrayAsync());
        Assert.Equal("model/stl", res.Content.Headers.ContentType!.MediaType); // detected, not the stored text/plain
        Assert.Equal("inline", res.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("bracket.stl", res.Content.Headers.ContentDisposition.FileNameStar ?? res.Content.Headers.ContentDisposition.FileName);
        Assert.Equal("no-store, max-age=0", res.Headers.CacheControl!.ToString());
        Assert.Equal("no-cache", res.Headers.Pragma.ToString());
        Assert.Equal("CUI//EXPT %C2%B7 ITAR USML XI(d) %C2%B7 DISTRIBUTION D %C2%B7 PROGRAM ORCA", res.Headers.GetValues(DocumentEndpoints.MarkingsHeader).Single());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("none", res.Headers.GetValues("Accept-Ranges").Single());
        Assert.Equal(ViewerCsp.Content, Csp(res));
        Assert.Equal("overlay", res.Headers.GetValues("X-Keel-Handling").Single());
        Assert.False(res.Headers.Contains("Set-Cookie") && res.Headers.GetValues("Set-Cookie").Any(c => c.Contains("token", StringComparison.OrdinalIgnoreCase)));

        // Ranges are not honoured: the whole file is verified (and PDFs stamped) before any byte is sent.
        using var ranged = new HttpRequestMessage(HttpMethod.Get, $"/viewer/content/documents/{number}/files/{fileId}");
        ranged.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 9);
        var partial = await alice.SendAsync(ranged);
        Assert.Equal(HttpStatusCode.OK, partial.StatusCode);
        Assert.Equal(stl.Length, (await partial.Content.ReadAsByteArrayAsync()).Length);

        await using var conn = await api.Database.AppDataSource().OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit.event WHERE action = 'document.view' AND outcome = 'Success' AND payload LIKE @s", conn);
        cmd.Parameters.AddWithValue("s", $"%\"sha256\":\"{sha}\"%");
        Assert.Equal(2L, await cmd.ExecuteScalarAsync()); // every delivery is its own audited view
    }

    [Fact]
    public async Task Pdf_content_is_the_stamped_pdf_and_svg_is_served_as_a_sandboxed_image()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var pdf = ViewerSamples.Pdf(2, Guid.NewGuid().ToString("N"));
        var (n1, f1, _) = await Upload(alice, "sheet.pdf", pdf, "application/pdf");
        var res = await alice.GetAsync($"/viewer/content/documents/{n1}/files/{f1}");
        Assert.Equal("application/pdf", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("pdf-stamped", res.Headers.GetValues("X-Keel-Handling").Single());
        using (var stamped = UglyToad.PdfPig.PdfDocument.Open(await res.Content.ReadAsByteArrayAsync()))
        {
            Assert.Equal(2, stamped.NumberOfPages);
            Assert.All(stamped.GetPages(), p => Assert.Contains("DISTRIBUTION STATEMENT D", p.Text, StringComparison.Ordinal));
        }

        var (n2, f2, _) = await Upload(alice, "shape.svg", ViewerSamples.SvgWithScript(), "image/svg+xml");
        var svg = await alice.GetAsync($"/viewer/content/documents/{n2}/files/{f2}");
        Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType!.MediaType);
        Assert.Contains("sandbox", Csp(svg), StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", Csp(svg), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Denials_are_the_same_not_found_for_page_meta_and_content_and_unknown_documents_look_alike()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var lena = api.ClientFor(Lena);
        var (number, fileId, _) = await Upload(alice, "bracket.stl", ViewerSamples.AsciiStlCube());

        foreach (var url in new[] { $"/viewer/meta/documents/{number}/files/{fileId}", $"/viewer/content/documents/{number}/files/{fileId}" })
        {
            var hidden = await lena.GetAsync(url);
            var missing = await lena.GetAsync(url.Replace(number, "D-999999", StringComparison.Ordinal));
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            Assert.Equal(missing.StatusCode, hidden.StatusCode);
            Assert.DoesNotContain("bracket", await hidden.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        var page = await lena.GetAsync($"/viewer/documents/{number}/files/{fileId}");
        var missingPage = await lena.GetAsync($"/viewer/documents/D-999999/files/{fileId}");
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        var pageHtml = await page.Content.ReadAsStringAsync();
        Assert.Contains("was not found, or you are not authorized to see it", pageHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("ITAR", pageHtml, StringComparison.Ordinal); // no markings of the hidden object leak into the banner
        Assert.Contains("UNCLASSIFIED", pageHtml, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, missingPage.StatusCode);
        Assert.Equal(ViewerCsp.Page, Csp(page));

        await using var conn = await api.Database.AppDataSource().OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit.event WHERE action = 'document.view' AND outcome = 'Denied' AND object_id = @n", conn);
        cmd.Parameters.AddWithValue("n", number);
        Assert.Equal(3L, await cmd.ExecuteScalarAsync()); // meta, content and page of the hidden document; the unknown number has nothing to audit against
    }

    [Fact]
    public async Task The_viewer_page_and_metadata_carry_the_objects_markings_and_the_viewer_policy()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var (number, fileId, sha) = await Upload(alice, "bracket.step", ViewerSamples.StepBox());

        var page = await alice.GetAsync($"/viewer/documents/{number}/files/{fileId}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html; charset=utf-8", page.Content.Headers.ContentType!.ToString());
        Assert.Equal(ViewerCsp.Page, Csp(page));
        Assert.Equal("no-store", page.Headers.CacheControl!.ToString());
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(html, "data-viewer-banner"));
        Assert.Contains("CUI//EXPT &#xB7; ITAR USML XI(d) &#xB7; DISTRIBUTION D &#xB7; PROGRAM ORCA", html, StringComparison.Ordinal);
        Assert.Contains("DISTRIBUTION STATEMENT D", html, StringComparison.Ordinal);
        Assert.Contains($"/viewer/content/documents/{number}/files/{fileId}", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"/documents/{number}\"", html, StringComparison.Ordinal);

        var meta = JsonDocument.Parse(await (await alice.GetAsync($"/viewer/meta/documents/{number}/files/{fileId}")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(("ready", "model3d", "step"), (meta.GetProperty("status").GetString(), meta.GetProperty("viewer").GetString(), meta.GetProperty("format").GetString()));
        Assert.Equal(sha, meta.GetProperty("sha256").GetString());
        Assert.Equal("CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA", meta.GetProperty("banner").GetString());
        Assert.Equal("A.1", meta.GetProperty("iteration").GetString());
        Assert.Equal(5_000_000, meta.GetProperty("maxTriangles").GetInt32());

        // The primary-file shortcut used from part pages and document links.
        var redirect = await NoRedirect(api, Alice).GetAsync($"/viewer/documents/{number}/primary");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal($"/viewer/documents/{number}/files/{fileId}", redirect.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await api.ClientFor(Lena).GetAsync($"/viewer/documents/{number}/primary")).StatusCode);
    }

    [Fact]
    public async Task Only_viewer_routes_and_assets_get_the_viewer_policy_every_other_route_keeps_the_strict_global_one()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var (number, fileId, _) = await Upload(alice, "bracket.stl", ViewerSamples.AsciiStlCube());

        // Every route outside the viewer: exactly the global policy.
        foreach (var path in new[] { "/health/live", "/health/ready", "/api/v1/me", "/api/v1/documents", $"/api/v1/documents/{number}", $"/downloads/documents/{number}/files/{fileId}",
                     "/_content/Keel.Web/app.css", "/_content/Keel.Web/bom.css", "/does-not-exist", "/viewerx/anything" })
        {
            var res = await alice.GetAsync(path);
            Assert.True(res.Headers.TryGetValues("Content-Security-Policy", out var values), path);
            Assert.Equal(GlobalCsp, values.Single());
        }
        // The Blazor shell page of the app (a document page) as well.
        // (The Blazor endpoint adds a second header of its own, frame-ancestors only; the global one is intact and no viewer policy leaks onto it.)
        var shell = await NoRedirect(api, Alice).GetAsync($"/documents/{number}");
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        var shellPolicies = shell.Headers.GetValues("Content-Security-Policy").ToList();
        Assert.Contains(GlobalCsp, shellPolicies);
        Assert.DoesNotContain(shellPolicies, p => p.Contains("blob:", StringComparison.Ordinal) || p.Contains("unsafe-eval", StringComparison.Ordinal));

        // The viewer: its page, data, assets and the two workers each get their own.
        Assert.Equal(ViewerCsp.Page, Csp(await alice.GetAsync($"/viewer/documents/{number}/files/{fileId}")));
        Assert.Equal(ViewerCsp.Page, Csp(await alice.GetAsync($"/viewer/meta/documents/{number}/files/{fileId}")));
        Assert.Equal(ViewerCsp.Content, Csp(await alice.GetAsync($"/viewer/content/documents/{number}/files/{fileId}")));
        var script = await alice.GetAsync("/_content/Keel.Web/viewer/viewer.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType!.MediaType);
        Assert.Equal(ViewerCsp.Page, Csp(script));
        Assert.Equal(ViewerCsp.Page, Csp(await alice.GetAsync("/_content/Keel.Web/viewer/viewer.css")));
        Assert.Equal(ViewerCsp.StepWorker, Csp(await alice.GetAsync("/_content/Keel.Web/viewer/step-worker.js")));
        Assert.Equal(ViewerCsp.PdfWorker, Csp(await alice.GetAsync("/_content/Keel.Web/viewer/vendor/pdf.worker.min.mjs")));
        var wasm = await alice.GetAsync("/_content/Keel.Web/viewer/vendor/occt-import-js.wasm");
        Assert.Equal("application/wasm", wasm.Content.Headers.ContentType!.MediaType);
        Assert.Equal(ViewerCsp.Page, Csp(wasm));
    }

    [Fact]
    public async Task Viewer_routes_need_a_session_and_a_tampered_vault_object_is_a_500_that_is_audited()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var (number, fileId, sha) = await Upload(alice, "tamper.stl", ViewerSamples.AsciiStlCube(21));

        // No session: the browser is sent to the IdP like for any UI page; nothing about the file is disclosed.
        foreach (var path in new[] { $"/viewer/documents/{number}/files/{fileId}", $"/viewer/meta/documents/{number}/files/{fileId}", $"/viewer/content/documents/{number}/files/{fileId}" })
        {
            var anonymous = await NoRedirect(api).GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.StartsWith(ApiFactory.Issuer, anonymous.Headers.Location!.ToString(), StringComparison.Ordinal);
        }

        using var s3 = ObjectVault.CreateClient(api.Vault);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = api.Vault.Bucket, Key = sha, InputStream = new MemoryStream(ViewerSamples.AsciiStlCube(22)) });
        var res = await alice.GetAsync($"/viewer/content/documents/{number}/files/{fileId}");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Contains("failed integrity verification", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await using var conn = await api.Database.AppDataSource().OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit.event WHERE action = 'document.view' AND outcome = 'Failure' AND object_id = @n", conn);
        cmd.Parameters.AddWithValue("n", number);
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Refusals_map_to_clear_status_codes_with_a_message()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var (n1, f1, _) = await Upload(alice, "notes.stl", Encoding.UTF8.GetBytes("plain text pretending to be a model " + Guid.NewGuid()));
        var noPreview = await alice.GetAsync($"/viewer/content/documents/{n1}/files/{f1}");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, noPreview.StatusCode);
        Assert.Contains("not a valid STL model", await noPreview.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var (n2, f2, _) = await Upload(alice, "broken.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\nbroken " + Guid.NewGuid()), "application/pdf");
        var unprocessable = await alice.GetAsync($"/viewer/content/documents/{n2}/files/{f2}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unprocessable.StatusCode);
        Assert.Contains("cannot be marked", await unprocessable.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The page for a type with no preview shows facts and the download action instead of failing.
        var page = await (await alice.GetAsync($"/viewer/documents/{n1}/files/{f1}")).Content.ReadAsStringAsync();
        Assert.Contains("No preview for this type", page, StringComparison.Ordinal);
        Assert.Contains($"/downloads/documents/{n1}/files/{f1}", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Too_large_files_show_the_download_alternative_with_the_configured_limit()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        await using var small = api.WithWebHostBuilder(b => b.UseSetting("Keel:Viewer:MaxModelBytes", "1000"));
        var alice = small.CreateClient();
        alice.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Alice);
        var (number, fileId, _) = await Upload(alice, "big.stl", ViewerSamples.AsciiStlCube());
        var res = await alice.GetAsync($"/viewer/content/documents/{number}/files/{fileId}");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
        var page = await (await alice.GetAsync($"/viewer/documents/{number}/files/{fileId}")).Content.ReadAsStringAsync();
        Assert.Contains("Too large to preview", page, StringComparison.Ordinal);
        Assert.Contains("the limit for STL model files is 1000 B", page, StringComparison.Ordinal);
        Assert.Contains("Download (marked)", page, StringComparison.Ordinal);
    }

    private static HttpClient NoRedirect(ApiFactory api, string? token = null)
    {
        var client = api.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
            {
                var config = new OpenIdConnectConfiguration
                {
                    Issuer = ApiFactory.Issuer,
                    AuthorizationEndpoint = ApiFactory.Issuer + "/protocol/openid-connect/auth",
                    EndSessionEndpoint = ApiFactory.Issuer + "/protocol/openid-connect/logout",
                };
                o.Configuration = config;
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                o.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
            }))).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (token is not null) client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
