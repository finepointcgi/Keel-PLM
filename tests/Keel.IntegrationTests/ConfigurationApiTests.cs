using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Keel.IntegrationTests;

/// <summary>The configuration management REST surface through the real host: baselines, effectivity contexts, unit-specific structures.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ConfigurationApiTests(PostgresFixture pg)
{
    private static readonly string Alice = ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"]);
    private static readonly string Lena = ApiFactory.Token(sub: "u-lena", usPerson: false, roles: ["plm-viewer", "plm-author"]);
    private static readonly string Viewer = ApiFactory.Token(sub: "u-vic", roles: ["plm-viewer"]);
    private static readonly string NoProgram = ApiFactory.Token(sub: "u-eve", programs: [], roles: ["plm-viewer", "plm-author"]);

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

    /// <summary>Pod with find 10 = plug X for serials 1-100 and plug Y from 101, find 20 = bracket for every unit. Not released: the lines are what is tested.</summary>
    private static async Task<(string Pod, string X, string Y, string Bracket)> Pod(HttpClient alice)
    {
        var x = await Create(alice, "Plug X");
        var y = await Create(alice, "Plug Y");
        var bracket = await Create(alice, "Bracket");
        var pod = await Create(alice, "Pod", "Assembly");
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/v1/effectivity/contexts", new { id = "ORCA-POD", name = "Orca pod", blocks = new[] { new { name = "Block 1", number = 1 } } })).StatusCode);
        await alice.PostAsync($"/api/v1/parts/{pod}/checkout", null);
        async Task Add(string child, int find, object? eff) =>
            Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync($"/api/v1/parts/{pod}/working-copy/bom", new { childNumber = child, quantity = 1, findNumber = find, effectivity = eff })).StatusCode);
        await Add(x, 10, new { kind = "serial", context = "ORCA-POD", from = "1", to = "100" });
        await Add(y, 10, new { kind = "serial", context = "ORCA-POD", from = "101" });
        await Add(bracket, 20, null);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync($"/api/v1/parts/{pod}/checkin", new { comment = "structure" })).StatusCode);
        return (pod, x, y, bracket);
    }

    [Fact]
    public async Task Effectivity_lines_and_units_work_over_http()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var (pod, x, y, bracket) = await Pod(alice);

        // Overlap is a 409 with the reason in words; a malformed range is a 400.
        await alice.PostAsync($"/api/v1/parts/{pod}/checkout", null);
        var overlap = await alice.PostAsJsonAsync($"/api/v1/parts/{pod}/working-copy/bom", new { childNumber = x, quantity = 1, findNumber = 10, effectivity = new { kind = "serial", context = "ORCA-POD", from = "50", to = "60" } });
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        Assert.Contains("overlaps", (await Json(overlap)).GetProperty("title").GetString(), StringComparison.Ordinal);
        var malformed = await alice.PostAsJsonAsync($"/api/v1/parts/{pod}/working-copy/bom", new { childNumber = x, quantity = 1, findNumber = 30, effectivity = new { kind = "serial", context = "ORCA-POD", from = "ten" } });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        await alice.PostAsync($"/api/v1/parts/{pod}/undo-checkout", null);

        // The unit selects the lines.
        string[] Numbers(JsonElement bom) => [.. bom.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("number").GetString()!)];
        var s50 = await Json(await alice.GetAsync($"/api/v1/parts/{pod}/bom?spec=Effectivity&context=ORCA-POD&serial=50"));
        Assert.Equal([x, bracket], Numbers(s50));
        Assert.Equal("serial 50 (ORCA-POD)", s50.GetProperty("configuration").GetString());
        Assert.Equal("serial 1-100 (ORCA-POD)", s50.GetProperty("lines")[0].GetProperty("effectivityText").GetString());
        var s150 = await Json(await alice.GetAsync($"/api/v1/parts/{pod}/bom?spec=Effectivity&context=ORCA-POD&serial=150"));
        Assert.Equal([y, bracket], Numbers(s150));
        Assert.Equal("10~1", s150.GetProperty("lines")[0].GetProperty("path").GetString());
        var byDate = await Json(await alice.GetAsync($"/api/v1/parts/{pod}/bom?spec=Effectivity&date=2027-05-01"));
        Assert.Equal([bracket], Numbers(byDate));   // only the line without effectivity applies to a date

        // Missing or malformed units, unknown specs and a missing baseline are 400s.
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/v1/parts/{pod}/bom?spec=Effectivity")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/v1/parts/{pod}/bom?spec=Effectivity&serial=5")).StatusCode);   // no context
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/v1/parts/{pod}/bom?spec=Effectivity&date=nonsense")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/v1/parts/{pod}/bom?spec=Baseline")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/v1/parts/{pod}/bom?spec=Nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/v1/parts/{pod}/bom?spec=Baseline&baseline=BL-999999")).StatusCode);

        // CSV, where-used and compare-units follow the same resolution.
        var csv = await alice.GetAsync($"/api/v1/parts/{pod}/bom/export.csv?spec=Effectivity&context=ORCA-POD&serial=150");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Contains("serial 101 and up (ORCA-POD)", await csv.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(1, (await Json(await alice.GetAsync($"/api/v1/parts/{x}/where-used"))).GetProperty("rows").GetArrayLength());
        // For a unit the parent must be a released revision that the unit resolves to; this pod was never released, so nothing qualifies.
        Assert.Equal(0, (await Json(await alice.GetAsync($"/api/v1/parts/{x}/where-used?context=ORCA-POD&serial=50"))).GetProperty("rows").GetArrayLength());
        var diff = await Json(await alice.GetAsync($"/api/v1/parts/{pod}/bom/compare-units?context=ORCA-POD&serial=100&serialTo=101"));
        Assert.Equal((1, 1, 0), (diff.GetProperty("removed").GetInt32(), diff.GetProperty("added").GetInt32(), diff.GetProperty("changed").GetInt32()));
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/v1/parts/{pod}/bom/compare-units?serial=1&serialTo=2")).StatusCode);
        var revisions = await Json(await alice.GetAsync($"/api/v1/parts/{x}/effectivity"));
        Assert.Equal(1, revisions.GetProperty("revisions").GetArrayLength());

        // Contexts: duplicates conflict, viewers are forbidden, program-less users see nothing of a program context.
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync("/api/v1/effectivity/contexts", new { id = "ORCA-POD", name = "Again" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.ClientFor(Viewer).PostAsJsonAsync("/api/v1/effectivity/contexts", new { id = "VIC-POD", name = "No" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/v1/effectivity/contexts/ORCA-POD")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/v1/effectivity/contexts/NO-SUCH")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/effectivity/contexts")).StatusCode);
    }

    [Fact]
    public async Task Baselines_are_created_read_compared_exported_and_established_over_http()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var bolt = await Create(alice, "Bolt");
        var secret = await Create(alice, "Stealth rotor", itar: true);
        var asm = await Create(alice, "Assembly", "Assembly");
        await alice.PostAsync($"/api/v1/parts/{asm}/checkout", null);
        await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/working-copy/bom", new { childNumber = bolt, quantity = 4 });
        await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/working-copy/bom", new { childNumber = secret, quantity = 1 });
        await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/checkin", new { comment = "structure" });

        // The markings of an open assembly with an ITAR item must be raised, and the creator confirms.
        var refused = await alice.PostAsJsonAsync("/api/v1/baselines", new { rootNumber = asm, name = "CDR", type = "Design", spec = "Latest", milestone = "CDR" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("do not cover", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var created = await alice.PostAsJsonAsync("/api/v1/baselines", new { rootNumber = asm, name = "CDR", type = "Design", spec = "Latest", milestone = "CDR", confirmRaiseMarkings = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var detail = await Json(created);
        var number = detail.GetProperty("summary").GetProperty("number").GetString()!;
        Assert.Equal("BL-000001", number);
        Assert.Equal($"/api/v1/baselines/{number}", created.Headers.Location!.ToString());
        Assert.Contains("ITAR", detail.GetProperty("summary").GetProperty("banner").GetString(), StringComparison.Ordinal);

        var items = await Json(await alice.GetAsync($"/api/v1/baselines/{number}/items"));
        Assert.Equal(2, items.GetProperty("lines").GetArrayLength());
        Assert.Equal("Baseline", items.GetProperty("spec").GetString());
        var csv = await alice.GetAsync($"/api/v1/baselines/{number}/items/export.csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Contains(secret, await csv.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(2, (await Json(await alice.GetAsync($"/api/v1/baselines/{number}/summary"))).GetProperty("lines").GetArrayLength());
        Assert.True((await Json(await alice.GetAsync($"/api/v1/baselines/{number}/verify"))).GetProperty("intact").GetBoolean());
        Assert.Equal(1, (await Json(await alice.GetAsync($"/api/v1/parts/{asm}/baselines"))).GetProperty("items").GetArrayLength());
        // The BOM endpoint takes the baseline as a config spec too.
        Assert.Equal(2, (await Json(await alice.GetAsync($"/api/v1/parts/{asm}/bom?spec=Baseline&baseline={number}"))).GetProperty("lines").GetArrayLength());

        // Search honours the access filter: a user without program ORCA sees nothing and gets 404 for the baseline.
        var eve = api.ClientFor(NoProgram);
        Assert.Equal(0, (await Json(await eve.GetAsync("/api/v1/baselines"))).GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"/api/v1/baselines/{number}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"/api/v1/baselines/{number}/items")).StatusCode);
        Assert.Equal(1, (await Json(await alice.GetAsync("/api/v1/baselines?q=cdr&state=Draft&type=Design"))).GetProperty("total").GetInt32());

        // A non-U.S. person cannot read the ITAR item, so cannot create a baseline of it (the count only, no names).
        var lena = await api.ClientFor(Lena).PostAsJsonAsync("/api/v1/baselines", new { rootNumber = asm, name = "Lena's", type = "Design", spec = "Latest" });
        Assert.Equal(HttpStatusCode.BadRequest, lena.StatusCode);
        var lenaBody = await lena.Content.ReadAsStringAsync();
        Assert.Contains("not accessible to you", lenaBody, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, lenaBody, StringComparison.Ordinal);
        // Viewers cannot create or establish.
        var viewer = api.ClientFor(Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/baselines", new { rootNumber = asm, name = "x", type = "Design", spec = "Latest" })).StatusCode);

        // Compare: a second baseline after a structure change.
        await alice.PostAsync($"/api/v1/parts/{asm}/checkout", null);
        var nut = await Create(alice, "Nut");
        await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/working-copy/bom", new { childNumber = nut, quantity = 2 });
        await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/checkin", new { comment = "added a nut" });
        var second = await Json(await alice.PostAsJsonAsync("/api/v1/baselines", new { rootNumber = asm, name = "After", type = "Design", spec = "Latest", confirmRaiseMarkings = true }));
        var secondNumber = second.GetProperty("summary").GetProperty("number").GetString()!;
        var cmp = await Json(await alice.GetAsync($"/api/v1/baselines/compare?from={number}&to={secondNumber}"));
        Assert.Equal(1, cmp.GetProperty("added").GetInt32());
        Assert.Equal(nut, cmp.GetProperty("lines")[0].GetProperty("number").GetString());
        var cur = await Json(await alice.GetAsync($"/api/v1/baselines/{number}/compare-current?spec=Latest"));
        Assert.Equal(1, cur.GetProperty("added").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/v1/baselines/{number}/compare-current?spec=Effectivity")).StatusCode);
        var diffCsv = await alice.GetAsync($"/api/v1/baselines/compare.csv?from={number}&to={secondNumber}");
        Assert.Contains("Added", await diffCsv.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/v1/baselines/compare?from={number}&to=BL-999999")).StatusCode);

        // Lifecycle: establish needs a reason; the second is superseded by the first... then archive.
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync($"/api/v1/baselines/{number}/establish", new { reason = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/v1/baselines/{number}/establish", new { reason = "x" })).StatusCode);
        var established = await Json(await alice.PostAsJsonAsync($"/api/v1/baselines/{number}/establish", new { reason = "CDR agreed" }));
        Assert.Equal("Established", established.GetProperty("summary").GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync($"/api/v1/baselines/{number}/establish", new { reason = "again" })).StatusCode);
        await alice.PostAsJsonAsync($"/api/v1/baselines/{secondNumber}/establish", new { reason = "After CDR" });
        var superseded = await Json(await alice.PostAsJsonAsync($"/api/v1/baselines/{number}/supersede", new { supersededBy = secondNumber, reason = "Nut added" }));
        Assert.Equal("Superseded", superseded.GetProperty("summary").GetProperty("state").GetString());
        var archived = await Json(await alice.PostAsJsonAsync($"/api/v1/baselines/{number}/archive", new { reason = "Phase over" }));
        Assert.Equal("Archived", archived.GetProperty("summary").GetProperty("state").GetString());
        var renamed = await alice.PutAsJsonAsync($"/api/v1/baselines/{secondNumber}", new { name = "After CDR" });
        Assert.Equal(HttpStatusCode.Conflict, renamed.StatusCode);   // established baselines are frozen

        // Types, unauthenticated access and the review start (no template participants given) are mapped properly.
        Assert.Equal(3, (await Json(await alice.GetAsync("/api/v1/baselines/types"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/baselines")).StatusCode);
        var review = await alice.PostAsJsonAsync($"/api/v1/baselines/{secondNumber}/review", new { participants = new Dictionary<string, string[]>() });
        Assert.NotEqual(HttpStatusCode.Created, review.StatusCode);   // established baselines cannot be reviewed again
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/v1/baselines/{secondNumber}/workflow")).StatusCode);
    }
}
