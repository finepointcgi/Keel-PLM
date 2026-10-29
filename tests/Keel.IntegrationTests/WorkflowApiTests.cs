using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Keel.Audit;

namespace Keel.IntegrationTests;

/// <summary>The review workflow over HTTP with locally signed tokens: JWT validation, step-up via auth_time, status mapping.</summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkflowApiTests(PostgresFixture pg)
{
    private static string Token(string name, string[] roles, bool us = true, DateTimeOffset? authTime = null, bool includeAuthTime = true) =>
        ApiFactory.Token(sub: "u-" + name, roles: roles, usPerson: us, authTime: authTime, includeAuthTime: includeAuthTime);

    private static readonly string[] Author = ["plm-viewer", "plm-author"];
    private static readonly string[] Approver = ["plm-viewer", "plm-approver"];

    private static async Task<JsonElement> Json(HttpResponseMessage res) => JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static object NewPart() => new
    {
        name = "Bracket, sensor", partType = "Component", defaultUnit = "EA", source = "Make", description = "Synthetic",
        attributes = new { material = "6061-T6", mass = 0.12 },
        markings = new { exportJurisdiction = "Itar", distributionStatement = "D", owningOrg = "ENG", usmlCategory = "XI(d)", cuiCategories = new[] { "EXPT" },
            controllingOffice = "ACME", distributionReason = "Critical technology", program = "ORCA" },
    };

    /// <summary>Every user must have made one authenticated call, which is how the directory learns them.</summary>
    private static async Task Register(ApiFactory api, params string[] tokens)
    {
        foreach (var t in tokens) Assert.Equal(HttpStatusCode.OK, (await api.ClientFor(t).GetAsync("/api/v1/tasks")).StatusCode);
    }

    private static string Link(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    [Fact]
    public async Task Review_and_release_over_http_with_step_up_enforced_by_auth_time()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Token("alice", Author));
        var rita = Token("rita", Author);
        var bob = Token("bob", Approver);
        var carol = Token("carol", Approver);
        var lena = Token("lena", Approver, us: false);
        await Register(api, rita, bob, carol, lena);

        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/v1/parts", NewPart())).StatusCode);

        // Participant picker: only eligible approvers, only the fields a picker needs.
        var picker = await alice.GetFromJsonAsync<JsonElement>("/api/v1/users?role=plm-approver&objectType=part&objectKey=P-000001");
        Assert.Equal(["bob", "carol"], picker.EnumerateArray().Select(u => u.GetProperty("username").GetString()!).Order());
        Assert.Equal(["displayName", "roles", "sub", "username"], picker[0].EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(HttpStatusCode.NotFound, (await api.ClientFor(lena).GetAsync("/api/v1/users?objectType=part&objectKey=P-000001")).StatusCode);

        // Start: ineligible participants are refused with a clear message, the part is untouched.
        var bad = await alice.PostAsJsonAsync("/api/v1/parts/P-000001/review", new { participants = new { reviewer = new[] { "rita" }, approvers = new[] { "lena" } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("lena is not eligible", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("InWork", (await alice.GetFromJsonAsync<JsonElement>("/api/v1/parts/P-000001")).GetProperty("revision").GetProperty("state").GetString());

        var started = await alice.PostAsJsonAsync("/api/v1/parts/P-000001/review", new { participants = new { reviewer = new[] { "rita" }, approvers = new[] { "bob", "carol" } }, comment = "Please review" });
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var instance = await Json(started);
        var instanceId = instance.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/workflows/{instanceId}", started.Headers.Location!.ToString());
        Assert.Equal("UnderReview", (await alice.GetFromJsonAsync<JsonElement>("/api/v1/parts/P-000001")).GetProperty("revision").GetProperty("state").GetString());
        // Locked while the review runs.
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsync("/api/v1/parts/P-000001/checkout", null)).StatusCode);

        // Rita's inbox.
        var ritaClient = api.ClientFor(rita);
        var inbox = await ritaClient.GetFromJsonAsync<JsonElement>("/api/v1/tasks");
        var item = inbox.GetProperty("items")[0];
        Assert.False(item.GetProperty("redacted").GetBoolean());
        Assert.Equal("P-000001", item.GetProperty("target").GetProperty("key").GetString());
        Assert.StartsWith("CUI//EXPT", inbox.GetProperty("pageBanner").GetString(), StringComparison.Ordinal);
        var taskId = item.GetProperty("id").GetGuid();
        var detail = await ritaClient.GetFromJsonAsync<JsonElement>($"/api/v1/tasks/{taskId}");
        Assert.True(detail.GetProperty("canSign").GetBoolean());
        Assert.Equal("Synthetic", detail.GetProperty("description").GetString());

        // Step-up: a token whose auth_time is older than 300 s, or has none, is refused with an RFC 9470 challenge.
        var stale = api.ClientFor(Token("rita", Author, authTime: DateTimeOffset.UtcNow.AddSeconds(-400)));
        var refused = await stale.PostAsJsonAsync($"/api/v1/tasks/{taskId}/sign", new { reasonCode = "Reviewed" });
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        var challenge = refused.Headers.WwwAuthenticate.ToString();
        Assert.Contains("insufficient_user_authentication", challenge, StringComparison.Ordinal);
        Assert.Contains("max_age=\"300\"", challenge, StringComparison.Ordinal);
        var problem = await Json(refused);
        Assert.Equal(("stepup.required", 300), (problem.GetProperty("reason").GetString(), problem.GetProperty("maxAgeSeconds").GetInt32()));
        var noAuthTime = await api.ClientFor(Token("rita", Author, includeAuthTime: false)).PostAsJsonAsync($"/api/v1/tasks/{taskId}/sign", new { reasonCode = "Reviewed" });
        Assert.Equal(HttpStatusCode.Unauthorized, noAuthTime.StatusCode);

        // Validation and authorization are distinct from step-up.
        Assert.Equal(HttpStatusCode.BadRequest, (await ritaClient.PostAsJsonAsync($"/api/v1/tasks/{taskId}/sign", new { reasonCode = "Rejected" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.ClientFor(bob).PostAsJsonAsync($"/api/v1/tasks/{taskId}/sign", new { reasonCode = "Reviewed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.ClientFor(bob).GetAsync($"/api/v1/tasks/{taskId}")).StatusCode);

        // A fresh token signs; the next step opens for the approvers.
        var reviewed = await ritaClient.PostAsJsonAsync($"/api/v1/tasks/{taskId}/sign", new { reasonCode = "Reviewed" });
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        var again = await ritaClient.PostAsJsonAsync($"/api/v1/tasks/{taskId}/sign", new { reasonCode = "Reviewed" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        foreach (var (name, token) in new[] { ("bob", bob), ("carol", carol) })
        {
            var client = api.ClientFor(token);
            var mine = (await client.GetFromJsonAsync<JsonElement>("/api/v1/tasks")).GetProperty("items");
            Assert.Equal(1, mine.GetArrayLength());
            var sign = await client.PostAsJsonAsync($"/api/v1/tasks/{mine[0].GetProperty("id").GetGuid()}/sign", new { reasonCode = name == "bob" ? "Approved" : "ApprovedWithComments", comment = name == "bob" ? null : "See note" });
            Assert.Equal(HttpStatusCode.OK, sign.StatusCode);
        }

        var final = await alice.GetFromJsonAsync<JsonElement>($"/api/v1/workflows/{instanceId}");
        Assert.Equal("Approved", final.GetProperty("status").GetString());
        var signatures = final.GetProperty("signatures");
        Assert.Equal(3, signatures.GetArrayLength());
        Assert.Matches(@"^Signed by User bob \(bob\) on \d{4}-\d\d-\d\d \d\d:\d\d:\d\d UTC \u2014 Approved$", signatures[1].GetProperty("manifestation").GetString());
        Assert.Equal("Released", (await alice.GetFromJsonAsync<JsonElement>("/api/v1/parts/P-000001")).GetProperty("revision").GetProperty("stateLabel").GetString());

        var verify = await alice.GetFromJsonAsync<JsonElement>($"/api/v1/workflows/{instanceId}/signatures/verify");
        Assert.All(verify.EnumerateArray(), v => Assert.True(v.GetProperty("valid").GetBoolean()));
        var history = await alice.GetFromJsonAsync<JsonElement>("/api/v1/parts/P-000001/workflow");
        Assert.Equal(3, history.GetProperty("releases")[0].GetProperty("signatures").GetArrayLength());

        // Everything above is in the audit chain, including the refusals.
        await using var ds = api.Database.AppDataSource();
        var audit = await new AuditQueries(ds).ListAsync(new AuditQuery(Limit: 1000));
        Assert.Equal(3, audit.Count(e => e.Action == "workflow.sign" && e.Outcome == AuditOutcome.Success));
        Assert.True(audit.Count(e => e.Action == "workflow.sign" && e.Outcome == AuditOutcome.Denied) >= 4);
        Assert.Contains(audit, e => e.Action == "workflow.start" && e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task Cancel_reassign_templates_and_admin_endpoints_enforce_their_roles()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Token("alice", Author));
        var rita = Token("rita", Author);
        var bob = Token("bob", Approver);
        var dave = Token("dave", Approver);
        var admin = api.ClientFor(Token("ada", ["plm-admin", "plm-viewer"]));
        await Register(api, rita, bob, dave);
        await alice.PostAsJsonAsync("/api/v1/parts", NewPart());

        var started = await Json(await alice.PostAsJsonAsync("/api/v1/parts/P-000001/review", new { participants = new { reviewer = new[] { "rita" }, approvers = new[] { "bob" } } }));
        var id = started.GetProperty("id").GetGuid();
        var taskId = started.GetProperty("tasks")[0].GetProperty("id").GetGuid();

        // Admin-only endpoints: 403 for everyone else.
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/v1/workflows")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsJsonAsync($"/api/v1/tasks/{taskId}/reassign", new { assignee = "dave", reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsJsonAsync("/api/v1/workflow/templates", new { id = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/workflows")).StatusCode);
        var listed = await admin.GetFromJsonAsync<JsonElement>("/api/v1/workflows?status=Active");
        Assert.Equal(1, listed.GetProperty("total").GetInt64());

        // Reassign: an ineligible or unknown target is refused; an eligible one takes over and the old assignee loses the task.
        var unknown = await admin.PostAsJsonAsync($"/api/v1/tasks/{taskId}/reassign", new { assignee = "ghost", reason = "Rita is away" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("not in the user directory", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var moved = await admin.PostAsJsonAsync($"/api/v1/tasks/{taskId}/reassign", new { assignee = "dave", reason = "Rita is away" });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal("dave", (await Json(moved)).GetProperty("tasks")[0].GetProperty("assigneeUsername").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.ClientFor(rita).PostAsJsonAsync($"/api/v1/tasks/{taskId}/sign", new { reasonCode = "Reviewed" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.ClientFor(dave).PostAsJsonAsync($"/api/v1/tasks/{taskId}/sign", new { reasonCode = "Reviewed" })).StatusCode);

        // Cancel: not for bystanders, a reason is required, the starter may.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.ClientFor(bob).PostAsJsonAsync($"/api/v1/workflows/{id}/cancel", new { reason = "mine now" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync($"/api/v1/workflows/{id}/cancel", new { reason = "" })).StatusCode);
        var cancelled = await alice.PostAsJsonAsync($"/api/v1/workflows/{id}/cancel", new { reason = "Superseded" });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("Cancelled", (await Json(cancelled)).GetProperty("status").GetString());
        Assert.Equal("InWork", (await alice.GetFromJsonAsync<JsonElement>("/api/v1/parts/P-000001")).GetProperty("revision").GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync($"/api/v1/workflows/{id}/cancel", new { reason = "again" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/v1/workflows/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Templates_can_be_listed_by_anyone_and_uploaded_by_admins_with_validation_errors_explained()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Token("alice", Author));
        var admin = api.ClientFor(Token("ada", ["plm-admin", "plm-viewer"]));

        var listed = await alice.GetFromJsonAsync<JsonElement>("/api/v1/workflow/templates");
        var part = listed.EnumerateArray().Single(t => t.GetProperty("objectType").GetString() == "part");
        Assert.Equal("part-release-review", part.GetProperty("id").GetString());
        Assert.Equal(2, part.GetProperty("slots").GetArrayLength());
        Assert.Equal(["baseline", "capa", "deviation", "disposition", "document", "ecn", "ecr", "gate", "legal_hold", "mbom", "ncr", "part", "process_plan", "reg_list_version", "reqspec", "retention_schedule", "source_entry", "supplier"], listed.EnumerateArray().Select(t => t.GetProperty("objectType").GetString()!).Order()); // 1.6 seeds ECR and ECN templates

        var invalid = await admin.PostAsJsonAsync("/api/v1/workflow/templates", new { id = "orphans", name = "O", objectType = "part", start = "flow",
            nodes = new object[] { new { id = "flow", type = "Serial", steps = new[] { "done" } }, new { id = "done", type = "End", outcome = "Release" }, new { id = "lost", type = "End", outcome = "Release" } } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var text = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("orphan", text, StringComparison.Ordinal);
        Assert.Contains("at least one Task or Parallel", text, StringComparison.Ordinal);

        var created = await admin.PostAsJsonAsync("/api/v1/workflow/templates", JsonDocument.Parse("""
            { "id": "part-release-review", "name": "Part release review, any approver", "objectType": "part", "start": "flow",
              "slots": [ { "name": "reviewer", "label": "Reviewer", "role": "plm-viewer", "min": 1, "max": 1 } ],
              "nodes": [ { "id": "flow", "type": "Serial", "steps": ["review", "done"] },
                         { "id": "review", "type": "Task", "kind": "review", "assignment": { "mode": "Slot", "slot": "reviewer" } },
                         { "id": "done", "type": "End", "outcome": "Release" } ] }
            """).RootElement);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(2, (await Json(created)).GetProperty("version").GetInt32());
        var active = (await alice.GetFromJsonAsync<JsonElement>("/api/v1/workflow/templates")).EnumerateArray().Single(t => t.GetProperty("id").GetString() == "part-release-review");
        Assert.Equal("Part release review, any approver", active.GetProperty("name").GetString());
        Assert.Equal(19, (await alice.GetFromJsonAsync<JsonElement>("/api/v1/workflow/templates?all=true")).GetArrayLength()); // two part versions, ECR, ECN, baseline, MBOM, document, supplier, list entry, the process plan and the three records templates
    }

    [Fact]
    public async Task An_inbox_entry_for_a_user_who_lost_eligibility_is_redacted_over_http()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(Token("alice", Author));
        var rita = Token("rita", Author);
        await Register(api, rita);
        await alice.PostAsJsonAsync("/api/v1/parts", NewPart());
        var started = await alice.PostAsJsonAsync("/api/v1/parts/P-000001/review", new { participants = new { reviewer = new[] { "rita" }, approvers = new[] { "bob" } } });
        Assert.Equal(HttpStatusCode.BadRequest, started.StatusCode);                          // bob has never signed in: not in the directory
        Assert.Contains("not in the user directory", await started.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var bob = Token("bob", Approver);
        await Register(api, bob);
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/v1/parts/P-000001/review", new { participants = new { reviewer = new[] { "rita" }, approvers = new[] { "bob" } } })).StatusCode);

        // Rita's IdP attributes change: the next token says she is not a U.S. person.
        var ritaNow = api.ClientFor(Token("rita", Author, us: false));
        var inbox = await ritaNow.GetFromJsonAsync<JsonElement>("/api/v1/tasks");
        var item = inbox.GetProperty("items")[0];
        Assert.True(item.GetProperty("redacted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("target").ValueKind);
        Assert.Equal("UNCLASSIFIED", inbox.GetProperty("pageBanner").GetString());
        var taskId = item.GetProperty("id").GetGuid();
        var sign = await ritaNow.PostAsJsonAsync($"/api/v1/tasks/{taskId}/sign", new { reasonCode = "Reviewed" });
        Assert.Equal(HttpStatusCode.Forbidden, sign.StatusCode);
        var body = await sign.Content.ReadAsStringAsync();
        Assert.Contains("task.not_eligible", body, StringComparison.Ordinal);
        Assert.DoesNotContain("P-000001", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Bracket", await (await ritaNow.GetAsync($"/api/v1/tasks/{taskId}")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_directory_learns_users_from_api_calls_and_unauthenticated_calls_are_rejected()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().PostAsJsonAsync("/api/v1/tasks/00000000-0000-0000-0000-000000000000/sign", new { reasonCode = "Approved" })).StatusCode);

        var alice = api.ClientFor(Token("alice", Author));
        Assert.Empty((await alice.GetFromJsonAsync<JsonElement>("/api/v1/users?q=zed")).EnumerateArray());
        await Register(api, Token("zed", Approver));
        var found = await alice.GetFromJsonAsync<JsonElement>("/api/v1/users?q=zed");
        Assert.Equal("zed", Assert.Single(found.EnumerateArray()).GetProperty("username").GetString());
        // The pickers expose no security attributes.
        Assert.DoesNotContain("usPerson", found.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.ClientFor(ApiFactory.Token(sub: "u-norole", roles: ["nothing"])).GetAsync("/api/v1/users")).StatusCode);
        Assert.NotEmpty(Link(found[0], "sub"));
    }
}
