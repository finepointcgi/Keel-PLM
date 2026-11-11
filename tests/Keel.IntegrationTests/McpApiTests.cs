using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;

namespace Keel.IntegrationTests;

/// <summary>The MCP server at /api/v1/mcp (ADR-0064): read-only tools over the module services, markings, ABAC and audit.</summary>
[Collection(PostgresCollection.Name)]
public sealed class McpApiTests(PostgresFixture pg)
{
    private static readonly string Author = ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"], cui: ["EXPT"]);
    private static readonly string UsViewer = ApiFactory.Token(sub: "u-uma", roles: ["plm-viewer"], cui: ["EXPT"]);
    private static readonly string ForeignViewer = ApiFactory.Token(sub: "u-fritz", usPerson: false, roles: ["plm-viewer"], cui: ["EXPT"]);

    private static object Part(string name, object markings) => new { name, partType = "Component", defaultUnit = "EA", source = "Make", markings };

    private static async Task<string> CreateAsync(ApiFactory api, object body)
    {
        var res = await api.ClientFor(Author).PostAsJsonAsync("/api/v1/parts", body);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())!["number"]!.ToString();
    }

    private static Task<HttpResponseMessage> Post(HttpClient http, string json) =>
        http.PostAsync("/api/v1/mcp", new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<JsonObject> Rpc(HttpClient http, string method, JsonObject? parameters = null)
    {
        var req = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = method };
        if (parameters != null) req["params"] = parameters;
        using var res = await Post(http, req.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject();
    }

    private static async Task<(string Text, bool IsError)> Call(HttpClient http, string tool, JsonObject args)
    {
        var reply = await Rpc(http, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args });
        var result = reply["result"]!;
        return (result["content"]![0]!["text"]!.ToString(), result["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Tools_apply_the_read_rule_carry_markings_and_are_audited()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var open = await CreateAsync(api, Part("Open bracket", new { exportJurisdiction = "None", distributionStatement = "A", owningOrg = "ENG" }));
        var itar = await CreateAsync(api, Part("Armor panel", new { exportJurisdiction = "Itar", usmlCategory = "XI(d)", distributionStatement = "D", owningOrg = "ENG", cuiCategories = new[] { "EXPT" }, controllingOffice = "ACME", distributionReason = "Critical technology" }));

        var us = api.ClientFor(UsViewer);
        var tools = (await Rpc(us, "tools/list"))["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.ToString()).ToList();
        Assert.Contains("get_bom", tools);
        var listed = (await Rpc(us, "tools/list"))["result"]!["tools"]!.AsArray();
        Assert.True(listed.Single(t => t!["name"]!.ToString() == "get_part")!["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.False(listed.Single(t => t!["name"]!.ToString() == "create_part")!["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.DoesNotContain(listed, t => t!["name"]!.ToString() is "release_part" or "set_state" or "change_markings" or "sign");

        // A U.S. person reads the ITAR part, and the result leads with its banner.
        var (usText, usError) = await Call(us, "get_part", new JsonObject { ["number"] = itar });
        Assert.False(usError);
        Assert.StartsWith("MARKINGS: ", usText, StringComparison.Ordinal);
        Assert.Contains("ITAR", usText.Split('\n')[0], StringComparison.Ordinal);

        // A foreign person: absent from search, and the same answer as a part that doesn't exist.
        var foreign = api.ClientFor(ForeignViewer);
        var (search, _) = await Call(foreign, "search_parts", new JsonObject { ["limit"] = 100 });
        Assert.Contains(open, search, StringComparison.Ordinal);
        Assert.DoesNotContain(itar, search, StringComparison.Ordinal);
        var (denied, deniedError) = await Call(foreign, "get_part", new JsonObject { ["number"] = itar });
        var (missing, _) = await Call(foreign, "get_part", new JsonObject { ["number"] = "P-NOPE" });
        Assert.True(deniedError);
        Assert.Equal(missing, denied);

        // Bad arguments and unknown tools.
        Assert.True((await Call(us, "get_part", new JsonObject { ["nope"] = 1, ["number"] = open })).IsError);
        Assert.Equal(-32602, (await Rpc(us, "tools/call", new JsonObject { ["name"] = "release_part" }))["error"]!["code"]!.GetValue<int>());

        // Each call is audited, alongside the service's own read events (the denial included).
        await using var ds = api.Database.AppDataSource();
        var events = await new AuditQueries(ds).ListAsync(new AuditQuery(Limit: 1000));
        Assert.Contains(events, e => e.Action == "mcp.tool_call" && e.ObjectId == "get_part" && e.Actor == "u-fritz");
        Assert.Contains(events, e => e.Action == "part.read" && e.ObjectId == itar && e.Outcome == AuditOutcome.Denied && e.Actor == "u-fritz");
    }

    [Fact]
    public async Task Authors_create_and_edit_through_the_same_services_and_viewers_cannot()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var author = api.ClientFor(Author);
        var open = new JsonObject { ["export_jurisdiction"] = "None", ["distribution_statement"] = "A", ["owning_org"] = "ENG" };

        var (created, createdError) = await Call(author, "create_part", new JsonObject
        {
            ["name"] = "Bracket from an assistant", ["part_type"] = "Assembly", ["default_unit"] = "EA", ["source"] = "Make", ["markings"] = open.DeepClone(),
        });
        Assert.False(createdError, created);
        var parent = JsonNode.Parse(created[(created.IndexOf('\n', created.IndexOf('\n') + 1) + 1)..])!["number"]!.ToString();
        var (child, _) = await Call(author, "create_part", new JsonObject { ["name"] = "Pin", ["part_type"] = "Component", ["default_unit"] = "EA", ["source"] = "Buy", ["markings"] = open.DeepClone() });
        var childNumber = JsonNode.Parse(child[(child.IndexOf('\n', child.IndexOf('\n') + 1) + 1)..])!["number"]!.ToString();

        // Markings must be stated; a missing or invalid set is refused before anything is created.
        Assert.True((await Call(author, "create_part", new JsonObject { ["name"] = "x", ["part_type"] = "Component", ["default_unit"] = "EA", ["source"] = "Make" })).IsError);
        Assert.True((await Call(author, "create_part", new JsonObject { ["name"] = "x", ["part_type"] = "Component", ["default_unit"] = "EA", ["source"] = "Make",
            ["markings"] = new JsonObject { ["export_jurisdiction"] = "Itar", ["distribution_statement"] = "D", ["owning_org"] = "ENG" } })).IsError);   // ITAR needs a USML category

        // Check out, add a BOM line, edit, check in.
        Assert.False((await Call(author, "check_out_part", new JsonObject { ["number"] = parent })).IsError);
        var (line, lineError) = await Call(author, "add_bom_line", new JsonObject { ["number"] = parent, ["child_number"] = childNumber, ["quantity"] = 4 });
        Assert.False(lineError, line);
        Assert.False((await Call(author, "update_part_working_copy", new JsonObject { ["number"] = parent, ["description"] = "Holds the panel" })).IsError);
        var (checkedIn, checkInError) = await Call(author, "check_in_part", new JsonObject { ["number"] = parent, ["comment"] = "Structure from the assistant" });
        Assert.False(checkInError, checkedIn);
        var (bom, _) = await Call(author, "get_bom", new JsonObject { ["number"] = parent });
        Assert.Contains(childNumber, bom, StringComparison.Ordinal);

        // A problem report and a comment; an ECN is refused (created in Keel).
        var (pr, prError) = await Call(author, "create_change", new JsonObject { ["type"] = "PR", ["title"] = "Bracket cracks at the bend", ["priority"] = "High", ["markings"] = open.DeepClone() });
        Assert.False(prError, pr);
        Assert.True((await Call(author, "create_change", new JsonObject { ["type"] = "ECN", ["title"] = "x", ["priority"] = "Low", ["markings"] = open.DeepClone() })).IsError);
        Assert.False((await Call(author, "add_comment", new JsonObject { ["kind"] = "part", ["number"] = parent, ["text"] = "Checked the pin count." })).IsError);

        // A viewer reads but can't change anything.
        var viewer = api.ClientFor(UsViewer);
        Assert.False((await Call(viewer, "get_part", new JsonObject { ["number"] = parent })).IsError);
        Assert.True((await Call(viewer, "check_out_part", new JsonObject { ["number"] = parent })).IsError);
        Assert.True((await Call(viewer, "create_part", new JsonObject { ["name"] = "x", ["part_type"] = "Component", ["default_unit"] = "EA", ["source"] = "Make", ["markings"] = open.DeepClone() })).IsError);

        await using var ds = api.Database.AppDataSource();
        var events = await new AuditQueries(ds).ListAsync(new AuditQuery(Limit: 2000));
        Assert.Contains(events, e => e.Action == "mcp.tool_call" && e.ObjectId == "check_in_part" && e.Actor == "u-alice" && e.Outcome == AuditOutcome.Success);
        Assert.Contains(events, e => e.Action == "part.checkin" && e.ObjectId == parent);
    }

    [Fact]
    public async Task Clients_discover_the_idp_and_only_plm_users_without_a_browser_origin_connect()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        const string ping = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";

        using (var anonymous = await Post(api.CreateClient(), ping))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Contains("resource_metadata=", anonymous.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        }

        var metadata = JsonNode.Parse(await api.CreateClient().GetStringAsync("/.well-known/oauth-protected-resource/api/v1/mcp"))!;
        Assert.Equal(ApiFactory.Issuer, metadata["authorization_servers"]![0]!.ToString());
        Assert.EndsWith("/api/v1/mcp", metadata["resource"]!.ToString(), StringComparison.Ordinal);

        using (var norole = await Post(api.ClientFor(ApiFactory.Token(sub: "u-norole", roles: ["something-else"])), ping))
            Assert.Equal(HttpStatusCode.Forbidden, norole.StatusCode);

        var us = api.ClientFor(UsViewer);
        using (var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/mcp") { Content = new StringContent(ping, Encoding.UTF8, "application/json") })
        {
            req.Headers.Add("Origin", "https://evil.example");
            using var page = await us.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        }

        Assert.Equal("keel", (await Rpc(us, "initialize", new JsonObject { ["protocolVersion"] = "2025-06-18" }))["result"]!["serverInfo"]!["name"]!.ToString());
        using (var note = await Post(us, """{"jsonrpc":"2.0","method":"notifications/initialized"}""")) Assert.Equal(HttpStatusCode.Accepted, note.StatusCode);
        using (var get = await us.GetAsync("/api/v1/mcp")) Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        using (var bad = await Post(us, "{nope")) Assert.Contains("-32700", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
