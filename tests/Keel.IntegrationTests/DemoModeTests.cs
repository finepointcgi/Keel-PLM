using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Keel.Security;
using Keel.Web.Services;
using Keel.Web.Services.Demo;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Demo mode (ADR-0045) through the real host: the guards, the sign-in flow, the step-up and the API cookie path.</summary>
[Collection(PostgresCollection.Name)]
public sealed partial class DemoModeTests(PostgresFixture pg)
{
    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    private static HttpClient DemoClient(ApiFactory api) => api.WithWebHostBuilder(b => b.UseEnvironment(DemoMode.EnvironmentName))
        .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static ApiFactory Demo(ApiFactory api)
    {
        api.ExtraSettings["Keel:Demo:Enabled"] = "true";
        return api;
    }

    private static async Task<string> TokenOfAsync(HttpResponseMessage page) => TokenPattern().Match(await page.Content.ReadAsStringAsync()).Groups[1].Value;

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string token, params (string, string)[] fields)
    {
        var body = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        body.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
        return await client.PostAsync(path, new FormUrlEncodedContent(body));
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Demo_enabled_outside_the_Demo_environment_stops_the_host(string environment)
    {
        await using var api = Demo(await ApiFactory.CreateAsync(pg));
        var e = Assert.ThrowsAny<Exception>(() => api.WithWebHostBuilder(b => b.UseEnvironment(environment)).CreateClient());
        Assert.Contains("Keel:Demo:Enabled", e.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Demo_sign_in_does_not_exist_when_demo_is_not_enabled_even_in_the_Demo_environment()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var client = api.WithWebHostBuilder(b => b.UseEnvironment(DemoMode.EnvironmentName).ConfigureTestServices(s =>
            s.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
            {
                var config = new OpenIdConnectConfiguration { Issuer = ApiFactory.Issuer, AuthorizationEndpoint = ApiFactory.Issuer + "/protocol/openid-connect/auth" };
                o.Configuration = config;
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                o.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
            }))).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // The route is not mapped: an anonymous browser is sent to the identity provider like for any page, and the POST finds nothing to sign in.
        var page = await client.GetAsync("/account/demo-signin");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.StartsWith(ApiFactory.Issuer, page.Headers.Location!.ToString(), StringComparison.Ordinal);
        var post = await client.PostAsync("/account/demo-signin", new FormUrlEncodedContent([new("username", "alice")]));
        Assert.True(post.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.BadRequest);
        Assert.False(post.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("keel.session", StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Demo_mode_refuses_a_database_that_already_has_non_demo_data()
    {
        await using var api = Demo(await ApiFactory.CreateAsync(pg));
        await using (var data = NpgsqlDataSource.Create(api.Database.AppConnectionString))
        await using (var cmd = data.CreateCommand(
            """
            INSERT INTO identity.user_directory (sub, username, display_name, org, roles, us_person, citizenship, programs, export_authorizations, cui_categories, first_seen, last_seen)
            VALUES ('real-person-1', 'jdoe', 'J Doe', 'ENG', ARRAY['plm-viewer'], true, 'USA', ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], now(), now())
            """))
            await cmd.ExecuteNonQueryAsync();

        var e = Assert.ThrowsAny<Exception>(() => DemoClient(api));
        Assert.Contains("non-demo data", e.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Demo_sign_in_produces_the_session_the_rest_of_the_application_relies_on()
    {
        await using var api = Demo(await ApiFactory.CreateAsync(pg));
        var client = DemoClient(api);

        // Anonymous API requests are still 401 and a forged bearer token is still refused: production JWT validation is untouched.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
        using (var forged = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me"))
        {
            forged.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(forged)).StatusCode);
        }

        // An unauthenticated page request lands on the demo page.
        var challenge = await client.GetAsync("/parts");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.StartsWith("/account/demo-signin?returnUrl=%2Fparts", challenge.Headers.Location!.OriginalString, StringComparison.Ordinal);

        var page = await client.GetAsync(challenge.Headers.Location.OriginalString);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains(DemoMode.BannerText, html, StringComparison.Ordinal);
        Assert.Contains("value=\"alice\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("erp-service", html, StringComparison.Ordinal);

        var token = TokenPattern().Match(html).Groups[1].Value;
        Assert.NotEmpty(token);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/account/demo-signin", new FormUrlEncodedContent([new("username", "alice")]))).StatusCode); // no antiforgery token
        var signedIn = await PostAsync(client, "/account/demo-signin", token, ("username", "alice"), ("returnUrl", "/parts"));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal("/parts", signedIn.Headers.Location!.OriginalString);
        Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), c => c.StartsWith("keel.session=", StringComparison.Ordinal) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));

        // The cookie is accepted by the REST API (demo only) and maps to the same subject as the dev realm's alice.
        var me = JsonDocument.Parse(await (await client.GetAsync("/api/v1/me")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("d0f8df8c-51d0-5241-8c4f-f39864b95d21", me.GetProperty("id").GetString());
        Assert.Contains("plm-author", me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        // The user directory learned about the sign-in, as with a real one.
        await using var data = NpgsqlDataSource.Create(api.Database.AppConnectionString);
        await using var count = data.CreateCommand("SELECT count(*) FROM identity.user_directory WHERE username = 'alice'");
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task The_e_signature_step_up_confirms_the_same_person_and_renews_auth_time()
    {
        await using var api = Demo(await ApiFactory.CreateAsync(pg));
        var client = DemoClient(api);
        var token = await TokenOfAsync(await client.GetAsync("/account/demo-signin"));
        await PostAsync(client, "/account/demo-signin", token, ("username", "bob"));

        var step = await client.GetAsync("/account/step-up?returnUrl=/tasks");
        Assert.Equal(HttpStatusCode.Redirect, step.StatusCode);
        Assert.Equal("/account/demo-signin?returnUrl=%2Ftasks&stepUp=1", step.Headers.Location!.OriginalString);
        var confirm = await client.GetAsync(step.Headers.Location.OriginalString);
        var html = await confirm.Content.ReadAsStringAsync();
        Assert.Contains("data-demo-confirm", html, StringComparison.Ordinal);
        Assert.Contains("Bob Approver", html, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"alice\"", html, StringComparison.Ordinal); // a step-up never offers another person

        var done = await PostAsync(client, "/account/demo-signin/confirm", TokenPattern().Match(html).Groups[1].Value, ("returnUrl", "/tasks"));
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        Assert.Equal("/tasks", done.Headers.Location!.OriginalString);
        var me = JsonDocument.Parse(await (await client.GetAsync("/api/v1/me")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("8c9e66a4-8f68-53a8-a31c-b63659bd1606", me.GetProperty("id").GetString());

        // The step-up is in the audit log.
        await using var data = NpgsqlDataSource.Create(api.Database.AppConnectionString);
        await using var audit = data.CreateCommand("SELECT count(*) FROM audit.event WHERE action = 'session.step_up' AND outcome = 'Success' AND actor = '8c9e66a4-8f68-53a8-a31c-b63659bd1606'");
        Assert.Equal(1L, await audit.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_confirm_without_a_session_goes_back_to_the_sign_in_page()
    {
        await using var api = Demo(await ApiFactory.CreateAsync(pg));
        var client = DemoClient(api);
        var token = await TokenOfAsync(await client.GetAsync("/account/demo-signin"));
        var res = await PostAsync(client, "/account/demo-signin/confirm", token, ("returnUrl", "/"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/account/demo-signin", res.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Every_demo_principal_maps_to_the_subject_the_seed_gives_the_realm_user()
    {
        var realm = Keel.Seed.RealmUsers.Load();
        foreach (var user in DemoUsers.List)
        {
            var expected = realm[user.Username];
            var subject = SubjectClaims.FromPrincipal(DemoUsers.CreatePrincipal(user, DateTimeOffset.UtcNow, "s", DateTimeOffset.UtcNow.AddHours(1)))!;
            Assert.Equal(expected.Id, subject.Id);
            Assert.Equal(expected.Username, subject.Username);
            Assert.Equal(expected.DisplayName, subject.DisplayName);
            Assert.Equal(expected.UsPerson, subject.UsPerson);
            Assert.Equal(expected.Citizenship, subject.Citizenship);
            Assert.Equal(expected.Org, subject.Org);
            Assert.Equal(expected.Programs.Order(StringComparer.Ordinal), subject.Programs.Order(StringComparer.Ordinal));
            Assert.Equal(expected.ExportAuthorizations.Order(StringComparer.Ordinal), subject.ExportAuthorizations.Order(StringComparer.Ordinal));
            Assert.Equal(expected.CuiCategories.Order(StringComparer.Ordinal), subject.CuiCategories.Order(StringComparer.Ordinal));
            Assert.Equal(expected.Roles.Order(StringComparer.Ordinal), subject.Roles.Order(StringComparer.Ordinal));
            Assert.NotNull(subject.AuthTime);
        }
        await Task.CompletedTask;
    }
}
