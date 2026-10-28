using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Keel.IntegrationTests;

/// <summary>The MBOM REST surface through the real host: auth, outcome mapping, reconciliation, structure queries and the cookie-free CSV route.</summary>
[Collection(PostgresCollection.Name)]
public sealed class MbomApiTests(PostgresFixture pg)
{
    private static readonly string Alice = ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"]);
    private static readonly string Bob = ApiFactory.Token(sub: "u-bob", roles: ["plm-viewer", "plm-author"]);
    private static readonly string Eve = ApiFactory.Token(sub: "u-eve", roles: ["plm-viewer", "plm-author"], programs: []);
    private static readonly string Viewer = ApiFactory.Token(sub: "u-vic", roles: ["plm-viewer"]);
    private static readonly string Admin = ApiFactory.Token(sub: "u-ada", roles: ["plm-viewer", "plm-admin"]);

    private static async Task<JsonElement> Json(HttpResponseMessage res) => JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static object NewPart(string name, string type = "Component", bool itar = false) => new
    {
        name, partType = type, defaultUnit = "EA", source = "Buy",
        markings = itar
            ? (object)new { exportJurisdiction = "Itar", distributionStatement = "D", owningOrg = "ENG", usmlCategory = "XI(d)", cuiCategories = new[] { "EXPT" },
                controllingOffice = "ACME", distributionReason = "Critical technology", program = "ORCA" }
            : new { exportJurisdiction = "None", distributionStatement = "A", owningOrg = "ENG" },
    };

    private static async Task<string> Create(HttpClient c, string name, string type = "Component", bool itar = false) =>
        (await Json(await c.PostAsJsonAsync("/api/v1/parts", NewPart(name, type, itar)))).GetProperty("number").GetString()!;

    [Fact]
    public async Task Generate_edit_reconcile_and_query_an_mbom_over_http()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var screw = await Create(alice, "Screw");
        var plate = await Create(alice, "Plate");
        var secret = await Create(alice, "Stealth rotor", itar: true);
        var asm = await Create(alice, "Assembly", "Assembly");
        await alice.PostAsync($"/api/v1/parts/{asm}/checkout", null);
        foreach (var (child, qty) in new[] { (screw, 4), (plate, 1), (secret, 2) })
            Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/working-copy/bom", new { childNumber = child, quantity = qty })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/checkin", new { comment = "structure" })).StatusCode);

        // Plants and supply types are data.
        var plants = await Json(await alice.GetAsync("/api/v1/mboms/plants"));
        Assert.Equal(["PLANT-A", "PLANT-B"], plants.EnumerateArray().Select(p => p.GetProperty("code").GetString()));
        Assert.Equal(3, (await Json(await alice.GetAsync("/api/v1/mboms/supply-types"))).GetArrayLength());

        // Generate: validation, access, and the created response.
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/v1/mboms/generate", new { partNumber = asm, plant = "PLANT-A", ebom = new { spec = "sideways" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/v1/mboms/generate", new { partNumber = asm, plant = "NOWHERE", ebom = new { spec = "Latest" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.ClientFor(Eve).PostAsJsonAsync("/api/v1/mboms/generate", new { partNumber = asm, plant = "PLANT-A", ebom = new { spec = "Latest" } })).StatusCode); // holds a restricted part
        var gen = await alice.PostAsJsonAsync("/api/v1/mboms/generate", new { partNumber = asm, plant = "PLANT-A", ebom = new { spec = "Latest" } });
        Assert.Equal(HttpStatusCode.Created, gen.StatusCode);
        var number = (await Json(gen)).GetProperty("items")[0].GetProperty("mbomNumber").GetString()!;
        Assert.Equal($"/api/v1/mboms/{number}", gen.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync("/api/v1/mboms/generate", new { partNumber = asm, plant = "PLANT-A", ebom = new { spec = "Latest" } })).StatusCode);

        var detail = await Json(await alice.GetAsync($"/api/v1/mboms/{number}"));
        Assert.Equal(("InWork", "PLANT-A", asm), (detail.GetProperty("summary").GetProperty("state").GetString(), detail.GetProperty("summary").GetProperty("plant").GetString(), detail.GetProperty("summary").GetProperty("partNumber").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await api.ClientFor(Bob).GetAsync("/api/v1/mboms/MB-999999")).StatusCode);
        var search = await Json(await alice.GetAsync($"/api/v1/mboms?q={asm}&plant=PLANT-A"));
        Assert.Equal(1, search.GetProperty("items").GetArrayLength());

        // The restricted line is a placeholder for Eve, who can read the (open) MBOM.
        var eveLines = await Json(await api.ClientFor(Eve).GetAsync($"/api/v1/mboms/{number}/lines"));
        var restricted = eveLines.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("redacted").GetBoolean());
        Assert.Equal(2m, restricted.GetProperty("quantity").GetDecimal());
        Assert.Equal(JsonValueKind.Null, restricted.GetProperty("childNumber").ValueKind);

        // Working copy: edits need the check-out; viewers cannot edit.
        var early = await alice.PostAsJsonAsync($"/api/v1/mboms/{number}/working-copy/lines", new { childNumber = screw, quantity = 1, findNumber = 900, mbomOnly = true });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync($"/api/v1/mboms/{number}/checkout", null)).StatusCode);
        var added = await alice.PostAsJsonAsync($"/api/v1/mboms/{number}/working-copy/lines", new { childNumber = plate, quantity = 3, findNumber = 900, mbomOnly = true, supplyType = "Consume", operationReference = "OP-90", scrapFactor = 0.1 });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var lineId = (await Json(added)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync($"/api/v1/mboms/{number}/working-copy/lines", new { childNumber = plate, quantity = 1, scrapFactor = -2 })).StatusCode);
        var updated = await alice.PutAsJsonAsync($"/api/v1/mboms/{number}/working-copy/lines/{lineId}", new { quantity = 4, unit = "EA", findNumber = 900, mbomOnly = true, supplyType = "Consume", operationReference = "OP-90", scrapFactor = 0.1 });
        Assert.Equal(4m, (await Json(updated)).GetProperty("quantity").GetDecimal());
        Assert.Equal(HttpStatusCode.Conflict, (await api.ClientFor(Bob).PostAsync($"/api/v1/mboms/{number}/checkout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.ClientFor(Viewer).PostAsJsonAsync($"/api/v1/mboms/{number}/working-copy/lines", new { childNumber = plate, quantity = 1 })).StatusCode);
        var ebomLines = await Json(await alice.GetAsync($"/api/v1/mboms/{number}/ebom-lines"));
        Assert.Equal(3, ebomLines.GetProperty("lines").GetArrayLength());
        var links = await alice.PutAsJsonAsync($"/api/v1/mboms/{number}/working-copy/lines/{lineId}/links", new { links = new[] { new { ebomUsageId = ebomLines.GetProperty("lines")[0].GetProperty("usageId").GetGuid(), status = "matched" } } });
        Assert.Equal(HttpStatusCode.BadRequest, links.StatusCode); // an MBOM-only line takes no links
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync($"/api/v1/mboms/{number}/checkin", new { comment = "adhesive" })).StatusCode);

        // Reconciliation: create, read, history.
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync($"/api/v1/mboms/{number}/reconcile", new { spec = "nonsense" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.ClientFor(Viewer).PostAsJsonAsync($"/api/v1/mboms/{number}/reconcile", new { spec = "Latest" })).StatusCode);
        var rec = await alice.PostAsJsonAsync($"/api/v1/mboms/{number}/reconcile", new { spec = "Latest" });
        Assert.Equal(HttpStatusCode.Created, rec.StatusCode);
        var report = await Json(rec);
        Assert.Equal("ReconciledWithExceptions", report.GetProperty("run").GetProperty("status").GetString());
        Assert.Equal(100m, report.GetProperty("run").GetProperty("coveragePct").GetDecimal());
        var runId = report.GetProperty("run").GetProperty("id").GetGuid();
        Assert.Equal(["mbom_only_line"], report.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("code").GetString()));
        Assert.Equal(runId, (await Json(await alice.GetAsync($"/api/v1/mboms/{number}/reconciliations/latest"))).GetProperty("run").GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/v1/mboms/{number}/reconciliations/{runId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/v1/mboms/{number}/reconciliations/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(1, (await Json(await alice.GetAsync($"/api/v1/mboms/{number}/reconciliations"))).GetProperty("runs").GetArrayLength());

        // Structure queries.
        var explosion = await Json(await alice.GetAsync($"/api/v1/mboms/{number}/explosion"));
        Assert.Equal(4, explosion.GetProperty("lines").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/v1/mboms/{number}/explosion?parentLine=999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/v1/mboms/{number}/explosion?depth=99")).StatusCode);
        var csv = await alice.GetAsync($"/api/v1/mboms/{number}/explosion.csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        var rows = (await csv.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(rows[0], rows[^1]);
        Assert.Contains("Extended gross quantity", rows[1], StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await api.ClientFor(Eve).GetAsync($"/api/v1/mboms/{number}/explosion.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/v1/mboms/{number}/explosion.csv?depth=0")).StatusCode);
        var summary = await Json(await alice.GetAsync($"/api/v1/mboms/{number}/summary"));
        Assert.Equal(4, summary.GetProperty("lines").GetArrayLength());
        var compare = await Json(await alice.GetAsync($"/api/v1/mboms/{number}/compare?from=A.1&to=A.2"));
        Assert.Equal(1, compare.GetProperty("lines").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/v1/mboms/{number}/compare?from=A&to=A.2")).StatusCode);
        var side = await Json(await alice.GetAsync($"/api/v1/mboms/{number}/side-by-side?spec=Latest"));
        Assert.Equal(4, side.GetProperty("rows").GetArrayLength());
        var used = await Json(await alice.GetAsync($"/api/v1/mboms/where-used/{plate}?plant=PLANT-A"));
        Assert.Equal(2, used.GetProperty("rows").GetArrayLength());
        var proposal = await Json(await alice.GetAsync($"/api/v1/mboms/{number}/regenerate?spec=Latest"));
        Assert.False(proposal.GetProperty("ebomChanged").GetBoolean());
        var applied = await Json(await alice.PostAsJsonAsync($"/api/v1/mboms/{number}/regenerate", new { ebom = new { spec = "Latest" } }));
        Assert.False(applied.GetProperty("createdIteration").GetBoolean());

        // Lifecycle: manual transitions with a reason, markings, revise.
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync($"/api/v1/mboms/{number}/transition", new { to = "Obsolete" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync($"/api/v1/mboms/{number}/transition", new { to = "Released" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync($"/api/v1/mboms/{number}/revise", new { reason = "early" })).StatusCode);
        var obsolete = await alice.PostAsJsonAsync($"/api/v1/mboms/{number}/transition", new { to = "Obsolete", reason = "Superseded layout" });
        Assert.Equal("Obsolete", (await Json(obsolete)).GetProperty("summary").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Review_of_an_mbom_starts_through_the_workflow_endpoint_and_plants_are_administered_by_administrators()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);
        var plate = await Create(alice, "Plate");
        var asm = await Create(alice, "Assembly", "Assembly");
        await alice.PostAsync($"/api/v1/parts/{asm}/checkout", null);
        await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/working-copy/bom", new { childNumber = plate, quantity = 1 });
        await alice.PostAsJsonAsync($"/api/v1/parts/{asm}/checkin", new { comment = "structure" });
        var number = (await Json(await alice.PostAsJsonAsync("/api/v1/mboms/generate", new { partNumber = asm, plant = "PLANT-B", ebom = new { spec = "Latest" } }))).GetProperty("items")[0].GetProperty("mbomNumber").GetString()!;

        // Submitting without a reconciliation is refused with the reason, before any participant is looked at.
        var early = await alice.PostAsJsonAsync($"/api/v1/mboms/{number}/review", new { participants = new { reviewer = new[] { "rita" }, approvers = new[] { "bob" } } });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("Run a reconciliation", (await Json(early)).GetProperty("title").GetString(), StringComparison.Ordinal);
        var workflow = await alice.GetAsync($"/api/v1/mboms/{number}/workflow");
        Assert.Equal(HttpStatusCode.OK, workflow.StatusCode);

        // Plants: only administrators write them.
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync("/api/v1/mboms/plants/PLANT-Z", new { name = "Plant Z" })).StatusCode);
        var saved = await api.ClientFor(Admin).PutAsJsonAsync("/api/v1/mboms/plants/PLANT-Z", new { name = "Plant Z (synthetic)", releaseAllowsWarnings = false });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.False((await Json(saved)).GetProperty("releaseAllowsWarnings").GetBoolean());
        Assert.Equal(3, (await Json(await alice.GetAsync("/api/v1/mboms/plants"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/mboms")).StatusCode);
    }
}
