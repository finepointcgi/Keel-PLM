using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Keel.Audit;

namespace Keel.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ApiTests(PostgresFixture pg)
{
    private static readonly string Auditor = ApiFactory.Token(sub: "u-audrey", roles: ["plm-auditor"]);

    private static async Task<IReadOnlyList<AuditRecord>> Events(ApiFactory api)
    {
        await using var ds = api.Database.AppDataSource();
        return await new AuditQueries(ds).ListAsync(new AuditQuery(Limit: 1000));
    }

    [Fact]
    public async Task Health_endpoints_report_database_and_fips_posture()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", body.RootElement.GetProperty("checks").GetProperty("database").GetProperty("status").GetString());
        // The test host is macOS/Linux without FIPS mode: reported as degraded, not hidden.
        Assert.NotEqual("Unhealthy", body.RootElement.GetProperty("checks").GetProperty("fips").GetProperty("status").GetString());

        var metrics = await client.GetStringAsync("/metrics");
        Assert.Contains("http_requests_received_total", metrics);
    }

    [Fact]
    public async Task Requests_without_valid_token_are_rejected()
    {
        await using var api = await ApiFactory.CreateAsync(pg);

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.ClientFor(ApiFactory.Token(audience: "other-api")).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.ClientFor(ApiFactory.Token(expires: DateTime.UtcNow.AddMinutes(-10))).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.ClientFor(ApiFactory.Token() + "x").GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Token_without_plm_role_is_forbidden_and_audited()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var res = await api.ClientFor(ApiFactory.Token(sub: "u-norole", roles: ["something-else"])).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var denied = Assert.Single(await Events(api));
        Assert.Equal(("access.denied", AuditOutcome.Denied, "u-norole"), (denied.Action, denied.Outcome, denied.Actor));
    }

    [Fact]
    public async Task Me_returns_subject_with_fail_closed_defaults_and_security_headers()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var correlation = Guid.NewGuid().ToString("D");
        var client = api.ClientFor(ApiFactory.Token(usPerson: null, cui: ["cti", "expt"]));
        client.DefaultRequestHeaders.Add("X-Correlation-Id", correlation);

        var res = await client.GetAsync("/api/v1/me");
        res.EnsureSuccessStatusCode();
        using var me = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.False(me.RootElement.GetProperty("usPerson").GetBoolean());
        Assert.Equal(["CTI", "EXPT"], me.RootElement.GetProperty("cuiCategories").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(correlation, res.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Contains("default-src 'self'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Access_evaluate_explains_denial_and_validates_markings()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var itar = new
        {
            markings = new
            {
                exportJurisdiction = "Itar", distributionStatement = "D", owningOrg = "ENG", usmlCategory = "XI(d)",
                cuiCategories = new[] { "EXPT" }, controllingOffice = "ACME", distributionReason = "Critical technology", program = "ORCA",
            },
            action = "Read",
        };

        var foreign = await api.ClientFor(ApiFactory.Token(usPerson: false)).PostAsJsonAsync("/api/v1/access/evaluate", itar);
        using var d = JsonDocument.Parse(await foreign.Content.ReadAsStringAsync());
        Assert.False(d.RootElement.GetProperty("permitted").GetBoolean());
        Assert.Equal("export.us_person_or_authorization_required", d.RootElement.GetProperty("reasons")[0].GetString());
        Assert.Equal("CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA", d.RootElement.GetProperty("banner").GetString());

        var us = await api.ClientFor(ApiFactory.Token()).PostAsJsonAsync("/api/v1/access/evaluate", itar);
        using var ok = JsonDocument.Parse(await us.Content.ReadAsStringAsync());
        Assert.True(ok.RootElement.GetProperty("permitted").GetBoolean());

        var invalid = await api.ClientFor(ApiFactory.Token()).PostAsJsonAsync("/api/v1/access/evaluate",
            new { markings = new { exportJurisdiction = "Itar", distributionStatement = "A", owningOrg = "ENG" }, action = "Read" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("ITAR data requires a USML category.", await invalid.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Audit_endpoints_require_auditor_and_audit_themselves()
    {
        await using var api = await ApiFactory.CreateAsync(pg);

        var viewer = await api.ClientFor(ApiFactory.Token(sub: "u-vic")).GetAsync("/api/v1/audit/events");
        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);

        var list = await api.ClientFor(Auditor).GetFromJsonAsync<JsonElement>("/api/v1/audit/events");
        var first = list.EnumerateArray().Single();
        Assert.Equal("access.denied", first.GetProperty("action").GetString());
        Assert.Equal("GET /api/v1/audit/events", first.GetProperty("objectId").GetString());

        var verify = await api.ClientFor(Auditor).PostAsync("/api/v1/audit/verify", null);
        using var v = JsonDocument.Parse(await verify.Content.ReadAsStringAsync());
        Assert.True(v.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(2, v.RootElement.GetProperty("eventsChecked").GetInt64());

        var actions = (await Events(api)).Select(e => e.Action).ToList();
        Assert.Equal(["access.denied", "audit.read", "audit.verify"], actions);
    }

    [Fact]
    public async Task Admin_can_sign_checkpoint_and_verify_uses_it()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.ClientFor(Auditor).PostAsync("/api/v1/audit/checkpoints", null)).StatusCode);

        var admin = api.ClientFor(ApiFactory.Token(sub: "u-ada", roles: ["plm-admin"]));
        using var cp = JsonDocument.Parse(await (await admin.PostAsync("/api/v1/audit/checkpoints", null)).Content.ReadAsStringAsync());
        Assert.True(cp.RootElement.GetProperty("created").GetBoolean());
        Assert.Equal(1, cp.RootElement.GetProperty("seq").GetInt64());

        using var v = JsonDocument.Parse(await (await api.ClientFor(Auditor).PostAsync("/api/v1/audit/verify", null)).Content.ReadAsStringAsync());
        Assert.True(v.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(1, v.RootElement.GetProperty("checkpointsChecked").GetInt32());
    }

    [Fact]
    public async Task Client_config_is_public_and_contains_no_secrets()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var config = await api.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/config");
        Assert.Equal(ApiFactory.Issuer, config.GetProperty("authority").GetString());
        Assert.Equal(3, config.EnumerateObject().Count());
    }
}
