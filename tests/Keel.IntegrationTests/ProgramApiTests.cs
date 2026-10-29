using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Keel.IntegrationTests;

/// <summary>The programs REST API through the real host with locally signed tokens: role gates, access mapping, error codes, the schedule and the published OpenAPI document (8.8, ADR-0043).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ProgramApiTests(PostgresFixture pg)
{
    private static string Token(string name, string[] roles, bool us = true, string[]? programs = null) =>
        ApiFactory.Token(sub: "u-" + name, roles: roles, usPerson: us, programs: programs, cui: ["EXPT", "CTI"]);

    private static readonly string[] Author = ["plm-viewer", "plm-author"];
    private static readonly string[] Approver = ["plm-viewer", "plm-approver"];

    private static readonly object OrcaMarkings = new { exportJurisdiction = "None", distributionStatement = "A", owningOrg = "ENG", program = "ORCA" };

    private static async Task<JsonElement> Json(HttpResponseMessage res) => JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Programs_are_created_scheduled_and_reviewed_through_the_api_with_access_mapped_to_status_codes()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Token("alice", Author));
        var eve = api.ClientFor(Token("eve", Author, programs: []));
        var viewer = api.ClientFor(Token("dan", ["plm-viewer"]));
        var bob = api.ClientFor(Token("bob", Approver));
        var carol = api.ClientFor(Token("carol", Approver));
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/api/v1/tasks")).StatusCode); // bob and carol sign in once so they appear in the directory
        Assert.Equal(HttpStatusCode.OK, (await carol.GetAsync("/api/v1/tasks")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/programs")).StatusCode);
        var body = new { code = "ORCA", name = "Orca program", description = "Synthetic", customerRef = "Customer 7", contractRef = "C-1", manager = "alice", start = "2027-03-01", end = "2028-03-01", markings = OrcaMarkings };
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/v1/programs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/programs", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/v1/programs", new { body.code, body.name, manager = "alice", markings = new { exportJurisdiction = "None", distributionStatement = "A", owningOrg = "ENG", program = "OTHER" } })).StatusCode);
        var created = await alice.PostAsJsonAsync("/api/v1/programs", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("Planning", (await Json(created)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync("/api/v1/programs", body)).StatusCode);

        // outside the program: 404 on the program, an empty list, nothing in my work
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync("/api/v1/programs/ORCA")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync("/api/v1/programs/NOPE")).StatusCode);
        Assert.Equal(0, (await Json(await eve.GetAsync("/api/v1/programs"))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Json(await alice.GetAsync("/api/v1/programs?q=orca"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/api/v1/programs?sort=password")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/v1/programs?sort=name&dir=desc")).StatusCode);

        var phase = await Json(await alice.PostAsJsonAsync("/api/v1/programs/ORCA/items", new { kind = "Phase", title = "Design", start = "2027-03-01", end = "2027-06-01" }));
        var a = await Json(await alice.PostAsJsonAsync("/api/v1/programs/ORCA/items", new { kind = "Task", parent = phase.GetProperty("key").GetString(), title = "Concept", owner = "bob", start = "2027-03-01", end = "2027-03-31" }));
        var gate = await Json(await alice.PostAsJsonAsync("/api/v1/programs/ORCA/items", new { kind = "Milestone", title = "Preliminary design review", owner = "bob", start = "2027-04-15", end = "2027-04-15", gateType = "PDR" }));
        var gateNumber = gate.GetProperty("gateNumber").GetString()!;
        Assert.StartsWith("GR-", gateNumber, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/programs/ORCA/items", new { kind = "Task", title = "x", start = "2027-03-01", end = "2027-03-02" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/v1/programs/ORCA/dependencies", new { predecessor = a.GetProperty("key").GetString(), successor = gate.GetProperty("key").GetString(), lagDays = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/v1/programs/ORCA/dependencies", new { predecessor = gate.GetProperty("key").GetString(), successor = a.GetProperty("key").GetString() })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/v1/programs/ORCA/baselines", new { name = "Contract" })).StatusCode);

        var schedule = await Json(await alice.GetAsync("/api/v1/programs/ORCA/schedule?asOf=2027-01-01"));
        Assert.Equal(3, schedule.GetProperty("items").GetArrayLength());
        Assert.Equal(1, schedule.GetProperty("criticalPath").GetArrayLength()); // the milestone is fixed by its own date; the task has float
        Assert.Equal(1, schedule.GetProperty("baselines").GetArrayLength());
        var dashboard = await Json(await alice.GetAsync("/api/v1/programs/ORCA/dashboard?asOf=2027-04-01"));
        Assert.Equal(3, dashboard.GetProperty("itemCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync("/api/v1/programs/ORCA/schedule")).StatusCode);

        // gate review: checklist, review through the workflow, signatures
        var detail = await Json(await alice.PostAsJsonAsync($"/api/v1/gates/{gateNumber}/checklist", new { text = "Requirements baselined", required = true }));
        var checklistId = detail.GetProperty("checklist")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await alice.PutAsJsonAsync($"/api/v1/gates/{gateNumber}/checklist/{checklistId}", new { status = "Met", note = "Done" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"/api/v1/gates/{gateNumber}")).StatusCode);
        var review = await alice.PostAsJsonAsync($"/api/v1/gates/{gateNumber}/review", new { participants = new Dictionary<string, string[]> { ["approvers"] = ["bob", "carol"] } });
        Assert.Equal(HttpStatusCode.Created, review.StatusCode);
        Assert.Equal("UnderReview", (await Json(await alice.GetAsync($"/api/v1/gates/{gateNumber}"))).GetProperty("state").GetString());
        foreach (var client in new[] { bob, carol })
        {
            var tasks = await Json(await client.GetAsync("/api/v1/tasks"));
            var taskId = tasks.GetProperty("items")[0].GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/tasks/{taskId}/sign", new { reasonCode = "Approved" })).StatusCode);
        }
        var approved = await Json(await alice.GetAsync($"/api/v1/gates/{gateNumber}"));
        Assert.Equal("Approved", approved.GetProperty("state").GetString());
        Assert.Equal("Complete", approved.GetProperty("status").GetString());
        var mine = await Json(await bob.GetAsync("/api/v1/programs/my-work"));
        Assert.Equal(1, mine.GetProperty("items").GetArrayLength()); // the concept task; the gate is complete
    }

    [Fact]
    public async Task The_published_programs_openapi_document_equals_the_committed_one_and_lists_nothing_else()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var generated = await api.ClientFor(Token("alice", ["plm-viewer"])).GetStringAsync("/openapi/programs.json");
        var json = JsonNode.Parse(generated)!;
        Assert.StartsWith("3.1", json["openapi"]!.GetValue<string>(), StringComparison.Ordinal);
        var paths = json["paths"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("/api/v1/programs", paths);
        Assert.Contains("/api/v1/programs/{code}/schedule", paths);
        Assert.Contains("/api/v1/programs/{code}/deliverables", paths);
        Assert.Contains("/api/v1/gates/{number}/review", paths);
        Assert.All(paths, p => Assert.True(p.StartsWith("/api/v1/programs", StringComparison.Ordinal) || p.StartsWith("/api/v1/gates", StringComparison.Ordinal), p));

        var file = Path.Combine(RepoRoot(), "docs", "programs", "openapi.json");
        var normalized = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("KEEL_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, normalized);
            return;
        }
        Assert.True(File.Exists(file), "docs/programs/openapi.json is missing: run scripts/update-openapi.sh");
        Assert.True(normalized == await File.ReadAllTextAsync(file), "docs/programs/openapi.json differs from the generated document. If the API change is intended, run scripts/update-openapi.sh and commit the result.");

        var integration = JsonNode.Parse(await api.ClientFor(Token("alice", ["plm-viewer"])).GetStringAsync("/openapi/v1.json"))!;
        Assert.DoesNotContain(integration["paths"]!.AsObject().Select(p => p.Key), p => p.Contains("/programs", StringComparison.Ordinal) || p.Contains("/gates", StringComparison.Ordinal));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
