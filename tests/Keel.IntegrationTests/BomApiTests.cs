using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Keel.IntegrationTests;

/// <summary>The BOM REST surface through the real host: auth, outcome mapping, redaction and CSV.</summary>
[Collection(PostgresCollection.Name)]
public sealed class BomApiTests(PostgresFixture pg)
{
    private static readonly string Alice = ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"]);
    private static readonly string Bob = ApiFactory.Token(sub: "u-bob", roles: ["plm-viewer", "plm-author"]);
    private static readonly string Lena = ApiFactory.Token(sub: "u-lena", usPerson: false, roles: ["plm-viewer", "plm-author"]);
    private static readonly string Viewer = ApiFactory.Token(sub: "u-vic", roles: ["plm-viewer"]);

    private static object NewPart(string name, string type = "Component", bool itar = false) => new
    {
        name, partType = type, defaultUnit = "EA", source = "Make",
        markings = itar
            ? (object)new { exportJurisdiction = "Itar", distributionStatement = "D", owningOrg = "ENG", usmlCategory = "XI(d)", cuiCategories = new[] { "EXPT" },
                controllingOffice = "ACME", distributionReason = "Critical technology", program = "ORCA" }
            : new { exportJurisdiction = "None", distributionStatement = "A", owningOrg = "ENG" },
    };

    private static async Task<JsonElement> Json(HttpResponseMessage res) => JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> Create(HttpClient c, string name, string type = "Component", bool itar = false) =>
        (await Json(await c.PostAsJsonAsync("/api/v1/parts", NewPart(name, type, itar)))).GetProperty("number").GetString()!;

    [Fact]
    public async Task Edit_check_in_and_read_the_structure_over_http()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var screw = await Create(alice, "Screw");
        var plate = await Create(alice, "Plate");
        var secret = await Create(alice, "Stealth rotor", itar: true);
        var asm = await Create(alice, "Assembly", "Assembly");

        // Editing needs the check-out.
        var early = await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/working-copy/bom", new { childNumber = screw, quantity = 4 });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("Check the part out before editing its BOM.", (await Json(early)).GetProperty("title").GetString());

        await alice.PostAsync($"/api/v1/parts/{asm}/checkout", null);
        var created = await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/working-copy/bom", new { childNumber = screw, quantity = 4, referenceDesignators = new[] { "S1", "S2", "S3", "S4" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var line = await Json(created);
        Assert.Equal(10, line.GetProperty("findNumber").GetInt32());
        Assert.StartsWith($"/api/v1/parts/{asm}/working-copy/bom/", created.Headers.Location!.ToString(), StringComparison.Ordinal);
        var lineId = line.GetProperty("id").GetGuid();
        await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/working-copy/bom", new { childNumber = plate, quantity = 1 });
        await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/working-copy/bom", new { childNumber = secret, quantity = 2 });

        var bad = await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/working-copy/bom", new { childNumber = screw, quantity = 2.5, unit = "EA" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("whole number", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var put = await alice.PutAsJsonAsync($"/api/v1/parts/{asm}/working-copy/bom/{lineId}", new { quantity = 6, unit = "EA", findNumber = 10 });
        Assert.Equal(6m, (await Json(put)).GetProperty("quantity").GetDecimal());
        var working = await Json(await alice.GetAsync($"/api/v1/parts/{asm}/working-copy/bom"));
        Assert.Equal(3, working.GetProperty("lines").GetArrayLength());
        var renumber = await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/working-copy/bom/renumber", new { start = 100, step = 10 });
        Assert.Equal(100, (await Json(renumber)).GetProperty("lines")[0].GetProperty("findNumber").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync($"/api/v1/parts/{asm}/working-copy/bom/{Guid.NewGuid()}")).StatusCode);

        // Others cannot touch it; viewers cannot edit at all.
        Assert.Equal(HttpStatusCode.Conflict, (await api.ClientFor(Bob).PostAsJsonAsync($"/api/v1/parts/{asm}/working-copy/bom", new { childNumber = plate, quantity = 1, findNumber = 7 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.ClientFor(Bob).GetAsync($"/api/v1/parts/{asm}/working-copy/bom")).StatusCode);
        var viewerEdit = await api.ClientFor(Viewer).PostAsJsonAsync($"/api/v1/parts/{asm}/working-copy/bom", new { childNumber = plate, quantity = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, viewerEdit.StatusCode);
        Assert.Equal("role.missing", (await Json(viewerEdit)).GetProperty("reasons")[0].GetString());

        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/checkin", new { comment = "structure" })).StatusCode);

        var bom = await Json(await alice.GetAsync($"/api/v1/parts/{asm}/bom"));
        Assert.Equal(3, bom.GetProperty("lines").GetArrayLength());
        Assert.Equal(new[] { "100", "110", "120" }, bom.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("path").GetString()));
        Assert.Equal(6m, bom.GetProperty("lines")[0].GetProperty("extendedQuantity").GetDecimal());
        Assert.StartsWith("CUI//EXPT", bom.GetProperty("banner").GetString(), StringComparison.Ordinal);

        var lazy = await Json(await alice.GetAsync($"/api/v1/parts/{asm}/bom?depth=1&parentLine=100"));
        Assert.Equal(0, lazy.GetProperty("lines").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/v1/parts/{asm}/bom?parentLine=999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/v1/parts/{asm}/bom?depth=99")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/v1/parts/{asm}/bom?spec=sideways")).StatusCode);
        var phase2 = await alice.GetAsync($"/api/v1/parts/{asm}/bom?spec=effectivity");
        Assert.Equal(HttpStatusCode.NotImplemented, phase2.StatusCode);
        Assert.Contains("Phase 2", await phase2.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var released = await Json(await alice.GetAsync($"/api/v1/parts/{asm}/bom?spec=LatestReleased"));
        Assert.Equal(3, released.GetProperty("unresolvedCount").GetInt32());

        var summary = await Json(await alice.GetAsync($"/api/v1/parts/{asm}/bom/summary"));
        Assert.Equal(3, summary.GetProperty("lines").GetArrayLength());
        var used = await Json(await alice.GetAsync($"/api/v1/parts/{screw}/where-used?levels=2"));
        Assert.Equal(asm, used.GetProperty("rows")[0].GetProperty("parentNumber").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/v1/parts/{screw}/where-used?levels=11")).StatusCode);
        var compare = await Json(await alice.GetAsync($"/api/v1/parts/{asm}/bom/compare?from=A.1&to=A.2"));
        Assert.Equal(3, compare.GetProperty("lines").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/v1/parts/{asm}/bom/compare?from=A&to=A.2")).StatusCode);

        // CSV: banner first and last line, download headers.
        var csv = await alice.GetAsync($"/api/v1/parts/{asm}/bom/export.csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Contains("_bom.csv", csv.Content.Headers.ContentDisposition!.FileName, StringComparison.Ordinal);
        var lines = (await csv.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(bom.GetProperty("banner").GetString(), lines[0]);
        Assert.Equal(lines[0], lines[^1]);
        Assert.Equal(6, lines.Length);

        // Deleting a line works on the next check-out.
        await alice.PostAsync($"/api/v1/parts/{asm}/checkout", null);
        var again = await Json(await alice.GetAsync($"/api/v1/parts/{asm}/working-copy/bom"));
        var first = again.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var del = await alice.DeleteAsync($"/api/v1/parts/{asm}/working-copy/bom/{first}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(2, (await Json(del)).GetProperty("remaining").GetInt32());
        var copy = await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/working-copy/bom/copy-from", new { sourceNumber = asm, replace = true });
        Assert.Equal(HttpStatusCode.BadRequest, copy.StatusCode);
    }

    [Fact]
    public async Task Restricted_items_are_placeholders_over_http_and_unauthenticated_calls_are_refused()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var open = await Create(alice, "Open bracket");
        var secret = await Create(alice, "Stealth rotor", itar: true);
        var asm = await Create(alice, "Assembly", "Assembly");
        await alice.PostAsync($"/api/v1/parts/{asm}/checkout", null);
        await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/working-copy/bom", new { childNumber = open, quantity = 2 });
        await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/working-copy/bom", new { childNumber = secret, quantity = 7 });
        await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/checkin", new { comment = "x" });

        var lena = api.ClientFor(Lena);
        foreach (var url in new[] { $"/api/v1/parts/{asm}/bom", $"/api/v1/parts/{asm}/bom/summary?leavesOnly=false", $"/api/v1/parts/{asm}/bom/export.csv",
                     $"/api/v1/parts/{open}/where-used", $"/api/v1/parts/{asm}/bom/compare?from=A.1&to=A.2" })
        {
            var res = await lena.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await res.Content.ReadAsStringAsync();
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
            Assert.DoesNotContain("Stealth", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ITAR", body, StringComparison.Ordinal);
        }
        var lines = (await Json(await lena.GetAsync($"/api/v1/parts/{asm}/bom"))).GetProperty("lines");
        var hidden = lines.EnumerateArray().Single(l => l.GetProperty("redacted").GetBoolean());
        Assert.Equal(7m, hidden.GetProperty("quantity").GetDecimal());
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("number").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await lena.GetAsync($"/api/v1/parts/{secret}/where-used")).StatusCode);

        var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/parts/{asm}/bom")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/v1/parts/{asm}/working-copy/bom", new { childNumber = open, quantity = 1 })).StatusCode);
    }

    [Fact]
    public async Task The_ui_csv_route_needs_a_browser_session_and_redirects_to_the_idp_otherwise()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var client = api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
            {
                var config = new OpenIdConnectConfiguration { Issuer = ApiFactory.Issuer, AuthorizationEndpoint = ApiFactory.Issuer + "/protocol/openid-connect/auth" };
                o.Configuration = config;
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                o.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
            }))).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var res = await client.GetAsync("/ui/parts/P-000001/bom.csv");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.StartsWith(ApiFactory.Issuer, res.Headers.Location!.ToString(), StringComparison.Ordinal);

        // With a bearer token (machine client) the same route serves the export through the same service.
        var alice = api.ClientFor(Alice);
        var asm = await Create(alice, "Assembly", "Assembly");
        var csv = await alice.GetAsync($"/ui/parts/{asm}/bom.csv");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
    }
}
