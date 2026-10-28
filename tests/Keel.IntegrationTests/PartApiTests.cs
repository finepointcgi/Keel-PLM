using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Keel.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class PartApiTests(PostgresFixture pg)
{
    private static readonly string Alice = ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"]);
    private static readonly string Lena = ApiFactory.Token(sub: "u-lena", usPerson: false, roles: ["plm-viewer", "plm-author"]);
    private static readonly string Bob = ApiFactory.Token(sub: "u-bob", roles: ["plm-viewer", "plm-author"]);

    private static object CreateBody(string name = "Bracket, sensor", string jurisdiction = "Itar") => new
    {
        name,
        partType = "Component",
        defaultUnit = "EA",
        source = "Make",
        description = "Synthetic",
        attributes = new { material = "6061-T6", mass = 0.12 },
        markings = jurisdiction == "Itar"
            ? (object)new { exportJurisdiction = "Itar", distributionStatement = "D", owningOrg = "ENG", usmlCategory = "XI(d)", cuiCategories = new[] { "EXPT" },
                controllingOffice = "ACME", distributionReason = "Critical technology", program = "ORCA" }
            : new { exportJurisdiction = "None", distributionStatement = "A", owningOrg = "ENG" },
    };

    private static async Task<JsonElement> Json(HttpResponseMessage res) => JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Part_lifecycle_over_http()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);

        var created = await alice.PostAsJsonAsync("/api/v1/parts", CreateBody());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/v1/parts/P-000001", created.Headers.Location!.ToString());
        var part = await Json(created);
        Assert.Equal("CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA", part.GetProperty("banner").GetString());
        Assert.Equal("Itar", part.GetProperty("markings").GetProperty("exportJurisdiction").GetString());

        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync("/api/v1/parts/P-000001/checkout", null)).StatusCode);
        var conflict = await api.ClientFor(Bob).PostAsync("/api/v1/parts/P-000001/checkout", null);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("Already checked out by alice.", (await Json(conflict)).GetProperty("title").GetString());

        var edit = await alice.PutAsJsonAsync("/api/v1/parts/P-000001/working-copy", new { description = "Bracket, sensor, rev A", attributes = new { mass = "0.15" } });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var checkin = await Json(await alice.PostAsJsonAsync("/api/v1/parts/P-000001/checkin", new { comment = "Lighter" }));
        Assert.Equal("A.2", checkin.GetProperty("iteration").GetProperty("label").GetString());
        Assert.Equal(0.15m, checkin.GetProperty("iteration").GetProperty("attributes").GetProperty("mass").GetDecimal());

        var submit = await alice.PostAsJsonAsync("/api/v1/parts/P-000001/state", new { toState = "UnderReview" });
        Assert.Equal("UnderReview", (await Json(submit)).GetProperty("revision").GetProperty("state").GetString());
        var release = await alice.PostAsJsonAsync("/api/v1/parts/P-000001/state", new { toState = "Released" });
        Assert.Equal(HttpStatusCode.Conflict, release.StatusCode);

        var old = await alice.GetAsync("/api/v1/parts/P-000001?revision=B");
        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
    }

    [Fact]
    public async Task Export_controlled_part_is_invisible_to_unauthorized_users()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        await api.ClientFor(Alice).PostAsJsonAsync("/api/v1/parts", CreateBody());
        await api.ClientFor(Alice).PostAsJsonAsync("/api/v1/parts", CreateBody("Bracket, commercial", "None"));
        var lena = api.ClientFor(Lena);

        Assert.Equal(HttpStatusCode.NotFound, (await lena.GetAsync("/api/v1/parts/P-000001")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await lena.GetAsync("/api/v1/parts/P-999999")).StatusCode); // indistinguishable
        var search = await lena.GetFromJsonAsync<JsonElement>("/api/v1/parts?q=bracket");
        Assert.Equal(1, search.GetProperty("total").GetInt64());
        Assert.Equal("P-000002", search.GetProperty("items")[0].GetProperty("number").GetString());

        var create = await lena.PostAsJsonAsync("/api/v1/parts", CreateBody());
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal("export.us_person_or_authorization_required", (await Json(create)).GetProperty("reasons")[0].GetString());
    }

    [Fact]
    public async Task Validation_errors_and_metadata()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Alice);

        var bad = await alice.PostAsJsonAsync("/api/v1/parts", new
        {
            name = "X", partType = "Component", defaultUnit = "EA", source = "Make",
            markings = new { exportJurisdiction = "Itar", distributionStatement = "D", owningOrg = "ENG" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("ITAR data requires a USML category.", await bad.Content.ReadAsStringAsync());

        var meta = await alice.GetFromJsonAsync<JsonElement>("/api/v1/metadata/parts");
        Assert.Contains("RawMaterial", meta.GetProperty("partTypes").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(4, meta.GetProperty("lifecycle").GetProperty("states").GetArrayLength());
        Assert.Equal("mass", meta.GetProperty("attributes")[2].GetProperty("name").GetString());
    }
}
