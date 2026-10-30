using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Keel.IntegrationTests;

/// <summary>The resources and cost REST API through the real host (9.5, ADR-0049): the program cost permission as 403 with a reason code, access as 404, the export with its banner and without money for a reader who may not see it.</summary>
[Collection(PostgresCollection.Name)]
public sealed partial class ProgramResourceApiTests(PostgresFixture pg)
{
    private static string Token(string name, string[] roles, string[]? programs = null) => ApiFactory.Token(sub: "u-" + name, roles: roles, usPerson: true, programs: programs, cui: ["EXPT", "CTI"]);

    private static readonly string[] Author = ["plm-viewer", "plm-author"];
    private static readonly string[] CostAuthor = ["plm-viewer", "plm-author", "plm-program-cost"];
    private static readonly object OrcaMarkings = new { exportJurisdiction = "None", distributionStatement = "A", owningOrg = "ENG", program = "ORCA" };

    private static async Task<JsonElement> Json(HttpResponseMessage res) => JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    [GeneratedRegex("<sheet [^>]*name=\"([^\"]+)\"")]
    private static partial Regex SheetName();

    private static List<string> Sheets(byte[] xlsx)
    {
        using var zip = new ZipArchive(new MemoryStream(xlsx));
        using var reader = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open());
        return [.. SheetName().Matches(reader.ReadToEnd()).Select(m => m.Groups[1].Value)];
    }

    [Fact]
    public async Task Resources_load_and_cost_work_through_the_api_with_the_cost_permission_mapped_to_403_and_access_to_404()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Token("alice", Author));
        var carla = api.ClientFor(Token("carla", CostAuthor));
        var eve = api.ClientFor(Token("eve", CostAuthor, programs: []));
        var viewer = api.ClientFor(Token("dan", ["plm-viewer"]));

        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/v1/programs", new { code = "ORCA", name = "Orca", manager = "alice", start = "2027-03-01", end = "2028-03-01", markings = OrcaMarkings })).StatusCode);
        var task = await Json(await alice.PostAsJsonAsync("/api/v1/programs/ORCA/items", new { kind = "Task", title = "Concept", start = "2027-03-01", end = "2027-03-12" }));
        var taskKey = task.GetProperty("key").GetString()!;

        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/v1/programs/ORCA/resource-pools", new { name = "Mechanical" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/programs/ORCA/resource-pools", new { name = "Nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.PostAsJsonAsync("/api/v1/programs/ORCA/resource-pools", new { name = "Nope" })).StatusCode);
        // a rate needs the cost permission: refused for an author without it, with the reason code
        var refused = await alice.PostAsJsonAsync("/api/v1/programs/ORCA/resource-roles", new { pool = "RP-001", name = "Designer", hourlyRate = 100 });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("cost.role_required", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/v1/programs/ORCA/resource-roles", new { pool = "RP-001", name = "Designer" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync("/api/v1/programs/ORCA/resource-roles/RR-001/rate", new { hourlyRate = 100, effectiveFrom = "2027-01-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await carla.PutAsJsonAsync("/api/v1/programs/ORCA/resource-roles/RR-001/rate", new { hourlyRate = 100, effectiveFrom = "2027-01-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/v1/programs/ORCA/resources", new { role = "RR-001", name = "Crew A", capacityHoursPerDay = 8 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/v1/programs/ORCA/assignments", new { item = taskKey, resource = "RS-001", start = "2027-03-05", end = "2027-03-01", hoursPerDay = 8 })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/v1/programs/ORCA/assignments", new { item = taskKey, resource = "RS-001", start = "2027-03-01", end = "2027-03-12", hoursPerDay = 10 })).StatusCode);

        // the catalog: hours for everyone, the rate only for the holder of the permission
        var plain = await Json(await viewer.GetAsync("/api/v1/programs/ORCA/resources"));
        Assert.True(plain.GetProperty("roles")[0].GetProperty("rateHidden").GetBoolean());
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("roles")[0].GetProperty("currentRate").ValueKind);
        var rated = await Json(await carla.GetAsync("/api/v1/programs/ORCA/resources"));
        Assert.Equal(100m, rated.GetProperty("roles")[0].GetProperty("currentRate").GetDecimal());
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync("/api/v1/programs/ORCA/resources")).StatusCode);

        // load and over-allocation (10 hours a day on a resource that works 8)
        var load = await Json(await viewer.GetAsync("/api/v1/programs/ORCA/load"));
        Assert.Equal(1, load.GetProperty("overAllocations").GetArrayLength());
        Assert.Equal(100m, load.GetProperty("rows")[0].GetProperty("allocated").GetDecimal());

        // cost: 403 without the permission, the numbers with it
        var denied = await alice.GetAsync("/api/v1/programs/ORCA/cost");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("cost.role_required", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync("/api/v1/programs/ORCA/cost")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await carla.PutAsJsonAsync("/api/v1/programs/ORCA/budget", new { costType = "Labor", period = "2027-03-15", amount = 20000 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync("/api/v1/programs/ORCA/budget", new { costType = "Labor", period = "2027-03-15", amount = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await carla.PutAsJsonAsync("/api/v1/programs/ORCA/budget", new { costType = "Travel", period = "2027-03-15", amount = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await carla.PostAsJsonAsync("/api/v1/programs/ORCA/actuals", new { costType = "Labor", incurredOn = "2027-03-02", hours = 8, resource = "RS-001" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await carla.PostAsJsonAsync("/api/v1/programs/ORCA/actuals", new { costType = "Other", incurredOn = "2027-03-03", amount = 250.5 })).StatusCode);
        var cost = await Json(await carla.GetAsync("/api/v1/programs/ORCA/cost?asOf=2027-03-05"));
        Assert.Equal("USD", cost.GetProperty("currency").GetString());
        Assert.Equal(20000m, cost.GetProperty("total").GetProperty("budget").GetDecimal());
        Assert.Equal(800m + 250.5m, cost.GetProperty("total").GetProperty("actual").GetDecimal());
        Assert.Equal(10 * 10 * 100m, cost.GetProperty("byType").GetProperty("Labor").GetProperty("planned").GetDecimal());

        // export: Excel with every table for the holder, hours only for the reader without the permission
        var xlsxRes = await carla.GetAsync("/api/v1/programs/ORCA/resources-export?format=xlsx&asOf=2027-03-05");
        Assert.Equal(HttpStatusCode.OK, xlsxRes.StatusCode);
        Assert.False(string.IsNullOrEmpty(xlsxRes.Headers.GetValues("X-Keel-Markings").Single()));
        Assert.Equal(["Summary", "By month", "Roles", "Budget", "Actuals", "Material", "Resources", "Assignments", "Load by week", "Over-allocation"], Sheets(await xlsxRes.Content.ReadAsByteArrayAsync()));
        Assert.Contains("program-ORCA-resources-cost-", xlsxRes.Content.Headers.ContentDisposition!.FileName, StringComparison.Ordinal);
        var plainX = await viewer.GetAsync("/api/v1/programs/ORCA/resources-export?format=xlsx");
        Assert.Equal(["Resources", "Assignments", "Load by week", "Over-allocation"], Sheets(await plainX.Content.ReadAsByteArrayAsync()));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/programs/ORCA/resources-export?format=csv&table=months")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync("/api/v1/programs/ORCA/resources-export?format=xlsx")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await carla.GetAsync("/api/v1/programs/ORCA/resources-export?format=pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await carla.GetAsync("/api/v1/programs/ORCA/resources-export?format=csv&table=secrets")).StatusCode);

        // CSV: one table, UTF-8 with BOM, the banner and the currency before the header, numbers as numbers
        var csv = await carla.GetAsync("/api/v1/programs/ORCA/resources-export?format=csv&table=months&asOf=2027-03-05");
        var bytes = await csv.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("UNCLASSIFIED", text.Split("\r\n")[0] + text.Split("\r\n")[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Amounts in USD", text, StringComparison.Ordinal);
        Assert.Contains("Month,Cost type,Budget,Planned,Actual,Estimate to complete,Estimate at completion", text, StringComparison.Ordinal);
        Assert.Contains("2027-03-01,Labor,20000", text, StringComparison.Ordinal);
        var load2 = await viewer.GetAsync("/api/v1/programs/ORCA/resources-export?format=csv&table=load");
        Assert.Equal(HttpStatusCode.OK, load2.StatusCode);
        Assert.DoesNotContain("100", (await load2.Content.ReadAsStringAsync()).Split("Resource,Name")[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_published_programs_openapi_document_describes_the_resource_and_cost_endpoints()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var json = JsonDocument.Parse(await api.ClientFor(Token("alice", ["plm-viewer"])).GetStringAsync("/openapi/programs.json")).RootElement;
        var paths = json.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        foreach (var p in new[] { "/api/v1/programs/{code}/resources", "/api/v1/programs/{code}/load", "/api/v1/programs/{code}/cost", "/api/v1/programs/{code}/budget", "/api/v1/programs/{code}/actuals",
                     "/api/v1/programs/{code}/material", "/api/v1/programs/{code}/assignments", "/api/v1/programs/{code}/resources-export" })
            Assert.Contains(p, paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("/ui/", StringComparison.Ordinal));
    }
}
