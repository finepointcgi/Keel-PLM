using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Keel.IntegrationTests;

/// <summary>Change management over HTTP with locally signed tokens: the whole PR → ECR → ECN → task process, access mapping and error status codes.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ChangeApiTests(PostgresFixture pg)
{
    private static string Token(string name, string[] roles, bool us = true, string[]? programs = null) =>
        ApiFactory.Token(sub: "u-" + name, roles: roles, usPerson: us, programs: programs, cui: ["EXPT", "CTI"]);

    private static readonly string[] Author = ["plm-viewer", "plm-author"];
    private static readonly string[] Approver = ["plm-viewer", "plm-approver"];

    private static readonly object Itar = new
    {
        exportJurisdiction = "Itar", distributionStatement = "D", owningOrg = "ENG", usmlCategory = "XI(d)", cuiCategories = new[] { "EXPT" },
        controllingOffice = "ACME", distributionReason = "Critical technology", program = "ORCA",
    };

    private static async Task<JsonElement> Json(HttpResponseMessage res) => JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static async Task Register(ApiFactory api, params string[] tokens)
    {
        foreach (var t in tokens) Assert.Equal(HttpStatusCode.OK, (await api.ClientFor(t).GetAsync("/api/v1/tasks")).StatusCode);
    }

    /// <summary>Each user in turn signs every open task of theirs (review tasks as Reviewed, approvals as Approved).</summary>
    private static async Task SignOpenTasksAsync(ApiFactory api, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            var client = api.ClientFor(token);
            foreach (var item in (await client.GetFromJsonAsync<JsonElement>("/api/v1/tasks")).GetProperty("items").EnumerateArray().ToList())
            {
                var code = item.GetProperty("kind").GetString() == "review" ? "Reviewed" : "Approved";
                var signed = await client.PostAsJsonAsync($"/api/v1/tasks/{item.GetProperty("id").GetGuid()}/sign", new { reasonCode = code });
                Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
            }
        }
    }

    [Fact]
    public async Task The_whole_change_process_over_http()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var aliceToken = Token("alice", Author);
        var (rita, bob, carol) = (Token("rita", Author), Token("bob", Approver), Token("carol", Approver));
        await Register(api, rita, bob, carol);
        var alice = api.ClientFor(aliceToken);

        // A released part to change.
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/v1/parts", new
        {
            name = "Bracket, sensor", partType = "Component", defaultUnit = "EA", source = "Make", description = "Synthetic", attributes = new { material = "6061-T6" }, markings = Itar,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/v1/parts/P-000001/review", new { participants = new { reviewer = new[] { "rita" }, approvers = new[] { "bob", "carol" } } })).StatusCode);
        await SignOpenTasksAsync(api, rita, bob, carol);
        Assert.Equal("Released", (await alice.GetFromJsonAsync<JsonElement>("/api/v1/parts/P-000001")).GetProperty("revision").GetProperty("state").GetString());

        // ---- PR
        var created = await alice.PostAsJsonAsync("/api/v1/changes", new { type = "pr", title = "Bracket cracks", description = "Found in test", priority = "High", reason = "Field report", markings = Itar });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/v1/changes/PR-000001", created.Headers.Location!.ToString());
        var pr = await Json(created);
        Assert.Equal(("PR", "Open", "High"), (pr.GetProperty("type").GetString(), pr.GetProperty("state").GetString(), pr.GetProperty("priority").GetString()));
        Assert.Equal("Bracket cracks", (await alice.GetFromJsonAsync<JsonElement>("/api/v1/changes?type=PR&q=crack")).GetProperty("items")[0].GetProperty("title").GetString());
        Assert.Equal(1, (await alice.GetFromJsonAsync<JsonElement>("/api/v1/changes?open=true")).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await alice.PutAsJsonAsync("/api/v1/changes/PR-000001", new { title = "Bracket cracks under load", description = "Found in test", priority = "Critical", reason = "Field report" })).StatusCode);

        // ---- PR -> ECR, classification, affected objects, impact analysis
        var ecrResponse = await alice.PostAsJsonAsync("/api/v1/changes/PR-000001/ecr", new { title = "Thicken the bracket" });
        Assert.Equal(HttpStatusCode.Created, ecrResponse.StatusCode);
        Assert.Equal("PR-000001", (await Json(ecrResponse)).GetProperty("source").GetProperty("number").GetString());
        Assert.Equal(HttpStatusCode.OK, (await alice.PutAsJsonAsync("/api/v1/changes/ECR-000001/class", new { changeClass = "I" })).StatusCode);
        var added = await alice.PostAsJsonAsync("/api/v1/changes/ECR-000001/affected", new { objectType = "part", key = "P-000001", action = "revise" });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var impact = await alice.GetFromJsonAsync<JsonElement>("/api/v1/changes/ECR-000001/impact?levels=3");
        Assert.Equal(1, impact.GetProperty("summary").GetProperty("affectedObjects").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/api/v1/changes/ECR-000001/impact?levels=99")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync("/api/v1/changes/ECR-000001/ecn", new { })).StatusCode); // Class I, not yet approved by the CCB

        // ---- the CCB review of the ECR through the workflow engine
        var review = await alice.PostAsJsonAsync("/api/v1/changes/ECR-000001/review", new { participants = new { ccb = new[] { "bob", "carol" } }, comment = "CCB please" });
        Assert.Equal(HttpStatusCode.Created, review.StatusCode);
        Assert.Equal("UnderReview", (await alice.GetFromJsonAsync<JsonElement>("/api/v1/changes/ECR-000001")).GetProperty("state").GetString());
        Assert.Equal("ecr", (await alice.GetFromJsonAsync<JsonElement>("/api/v1/changes/ECR-000001/workflow")).GetProperty("active").GetProperty("target").GetProperty("objectType").GetString());
        await SignOpenTasksAsync(api, bob, carol);
        Assert.Equal("Approved", (await alice.GetFromJsonAsync<JsonElement>("/api/v1/changes/ECR-000001")).GetProperty("state").GetString());

        // ---- ECR -> ECN: the object is carried over and revised; changes affecting the part are listed
        var ecnResponse = await alice.PostAsJsonAsync("/api/v1/changes/ECR-000001/ecn", new { });
        Assert.Equal(HttpStatusCode.Created, ecnResponse.StatusCode);
        var ecn = await Json(ecnResponse);
        Assert.Equal(("ECN-000001", "I", "B"), (ecn.GetProperty("number").GetString(), ecn.GetProperty("changeClass").GetString(), ecn.GetProperty("affected")[0].GetProperty("resultingRevision").GetString()));
        var affecting = await alice.GetFromJsonAsync<JsonElement>("/api/v1/changes/affecting/part/P-000001");
        Assert.Equal(["ECN-000001", "ECR-000001"], affecting.GetProperty("open").EnumerateArray().Select(c => c.GetProperty("number").GetString()!).Order());
        Assert.Equal("ECN-000001", affecting.GetProperty("boundTo").GetProperty("number").GetString());

        // The part's own review is refused while the notice holds the new revision.
        var partReview = await alice.PostAsJsonAsync("/api/v1/parts/P-000001/review", new { participants = new { reviewer = new[] { "rita" }, approvers = new[] { "bob" } } });
        Assert.Equal(HttpStatusCode.Conflict, partReview.StatusCode);
        Assert.Contains("ECN-000001", await partReview.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Work on revision B, then tasks and the release review.
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync("/api/v1/parts/P-000001/checkout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PutAsJsonAsync("/api/v1/parts/P-000001/working-copy", new { description = "Thicker wall", attributes = new { material = "6061-T6" } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/v1/parts/P-000001/checkin", new { comment = "thicker" })).StatusCode);
        var task = await alice.PostAsJsonAsync("/api/v1/changes/ECN-000001/tasks", new { title = "Update the work instruction", assignee = "bob", dueDate = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd") });
        Assert.Equal(HttpStatusCode.OK, task.StatusCode);
        Assert.Equal("CT-000001", (await Json(task)).GetProperty("tasks")[0].GetProperty("number").GetString());
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/v1/changes/ECN-000001/review", new { participants = new { reviewer = new[] { "rita" }, approvers = new[] { "bob", "carol" } } })).StatusCode);
        await SignOpenTasksAsync(api, rita, bob, carol);
        var released = await alice.GetFromJsonAsync<JsonElement>("/api/v1/changes/ECN-000001");
        Assert.Equal("Released", released.GetProperty("state").GetString());
        var part = await alice.GetFromJsonAsync<JsonElement>("/api/v1/parts/P-000001");
        Assert.Equal(("B", "Released"), (part.GetProperty("revision").GetProperty("revision").GetString(), part.GetProperty("revision").GetProperty("state").GetString()));
        Assert.Equal("Implemented", (await alice.GetFromJsonAsync<JsonElement>("/api/v1/changes/ECR-000001")).GetProperty("state").GetString());
        Assert.True((await alice.GetFromJsonAsync<JsonElement>("/api/v1/changes/ECN-000001/history")).GetArrayLength() >= 5);

        // ---- tasks: the assignee's inbox, work, and the Implemented rule
        var bobClient = api.ClientFor(bob);
        var mine = await bobClient.GetFromJsonAsync<JsonElement>("/api/v1/changes/tasks/mine");
        Assert.Equal("CT-000001", mine.GetProperty("items")[0].GetProperty("number").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync("/api/v1/changes/ECN-000001/state", new { toState = "Implemented" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsync("/api/v1/changes/tasks/CT-000001/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bobClient.PostAsync("/api/v1/changes/tasks/CT-000001/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bobClient.PostAsJsonAsync("/api/v1/changes/tasks/CT-000001/complete", new { text = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bobClient.PostAsJsonAsync("/api/v1/changes/tasks/CT-000001/complete", new { text = "WI-12 rev C issued" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/v1/changes/ECN-000001/state", new { toState = "Implemented" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsync("/api/v1/changes/tasks/CT-999999/start", null)).StatusCode);
        Assert.Contains("ScopeChange", await alice.GetStringAsync("/api/v1/changes/reason-codes?type=ECN&verb=cancel"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Access_validation_and_status_codes()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Token("alice", Author));
        var eve = api.ClientFor(Token("eve", Author, programs: []));
        var viewer = api.ClientFor(Token("vic", ["plm-viewer"]));
        var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/changes")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/v1/changes", new { type = "PR", title = "Secret", priority = "Low", markings = Itar })).StatusCode);

        // Hidden objects are 404 for everything; the list is filtered.
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync("/api/v1/changes/PR-000001")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync("/api/v1/changes/PR-000001/impact")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.PostAsJsonAsync("/api/v1/changes/PR-000001/ecr", new { })).StatusCode);
        Assert.Equal(0, (await eve.GetFromJsonAsync<JsonElement>("/api/v1/changes")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await alice.GetFromJsonAsync<JsonElement>("/api/v1/changes")).GetProperty("total").GetInt32());

        // Authorization: a viewer reads open data but cannot create (403 with reason codes); invalid input is 400; clashes are 409.
        var forbidden = await viewer.PostAsJsonAsync("/api/v1/changes", new { type = "PR", title = "x", priority = "Low", markings = new { exportJurisdiction = "None", distributionStatement = "A", owningOrg = "ENG" } });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Contains("role.missing", await forbidden.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/v1/changes", new { type = "PR", title = "", priority = "Low", markings = Itar })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/v1/changes", new { type = "PR", title = "x", priority = "Low", markings = new { exportJurisdiction = "Itar", distributionStatement = "D", owningOrg = "ENG" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/v1/changes", new { type = "ECN", title = "Direct Class I", priority = "Low", changeClass = "I", markings = Itar })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync("/api/v1/changes/PR-000001/state", new { toState = "Resolved" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/v1/changes/PR-000001/state", new { toState = "Closed", reasonCode = "NoActionRequired", reason = "Cosmetic" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/v1/changes/PR-000001/review", new { participants = new { ccb = new[] { "bob" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/v1/changes/PR-999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/v1/changes/affecting/part/P-999999")).StatusCode);
    }
}
