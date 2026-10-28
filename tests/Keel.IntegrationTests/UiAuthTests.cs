using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Keel.IntegrationTests;

/// <summary>ADR-0014: browsers are sent to the IdP; API clients get 401 and never a redirect.</summary>
[Collection(PostgresCollection.Name)]
public sealed class UiAuthTests(PostgresFixture pg)
{
    private static HttpClient NoRedirectClient(ApiFactory api)
    {
        var client = api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
            {
                var config = new OpenIdConnectConfiguration
                {
                    Issuer = ApiFactory.Issuer,
                    AuthorizationEndpoint = ApiFactory.Issuer + "/protocol/openid-connect/auth",
                    EndSessionEndpoint = ApiFactory.Issuer + "/protocol/openid-connect/logout",
                };
                o.Configuration = config;
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                o.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
            }))).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return client;
    }

    [Fact]
    public async Task Ui_document_download_needs_a_session_and_streams_marked_output()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var path = $"/downloads/documents/D-000001/files/{Guid.CreateVersion7():D}";
        // No session: the browser is sent to the IdP, like any UI page.
        var anonymous = await NoRedirectClient(api).GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.StartsWith(ApiFactory.Issuer, anonymous.Headers.Location!.ToString(), StringComparison.Ordinal);

        // Authenticated (a bearer token stands in for the session cookie here): same marked output as the REST route.
        var alice = api.ClientFor(ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"]));
        var form = new MultipartFormDataContent
        {
            { new StringContent("""{"name":"Ui drawing","documentType":"Drawing","markings":{"exportJurisdiction":"None","distributionStatement":"A","owningOrg":"ENG"}}"""), "metadata" },
            { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("ui " + Guid.NewGuid())), "file", "ui.txt" },
        };
        using var created = System.Text.Json.JsonDocument.Parse(await (await alice.PostAsync("/api/v1/documents", form)).Content.ReadAsStringAsync());
        var fileId = created.RootElement.GetProperty("iteration").GetProperty("files")[0].GetProperty("id").GetString();
        var res = await alice.GetAsync($"/downloads/documents/D-000001/files/{fileId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("UNCLASSIFIED %C2%B7 DISTRIBUTION A", res.Headers.GetValues("X-Keel-Markings").Single());
        Assert.Equal("ui.txt.zip", res.Content.Headers.ContentDisposition!.FileNameStar ?? res.Content.Headers.ContentDisposition.FileName);
    }

    [Fact]
    public async Task Ui_request_without_session_redirects_to_idp_with_pkce()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var res = await NoRedirectClient(api).GetAsync("/parts");

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var location = res.Headers.Location!.ToString();
        Assert.StartsWith(ApiFactory.Issuer + "/protocol/openid-connect/auth?", location, StringComparison.Ordinal);
        Assert.Contains("client_id=keel-web", location, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", location, StringComparison.Ordinal);
        Assert.Contains("response_type=code", location, StringComparison.Ordinal);
        // Correlation and nonce cookies are HTTP-only.
        Assert.All(res.Headers.GetValues("Set-Cookie"), c => Assert.Contains("httponly", c, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Api_requests_get_401_not_a_redirect_and_ui_assets_are_served()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var client = NoRedirectClient(api);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/parts")).StatusCode);
        var withCookieLikeBrowser = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        withCookieLikeBrowser.Headers.Add("Accept", "text/html");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(withCookieLikeBrowser)).StatusCode);

        var script = await client.GetAsync("/_framework/blazor.web.js");
        Assert.True(script.StatusCode == HttpStatusCode.OK, $"blazor.web.js: {(int)script.StatusCode} {await script.Content.ReadAsStringAsync()}");
        Assert.Equal("text/javascript", script.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Logout_requires_antiforgery_token()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        // Authenticate with a bearer token so the request reaches the endpoint; the missing antiforgery token must reject it.
        var client = NoRedirectClient(api);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiFactory.Token());
        var res = await client.PostAsync("/account/logout", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
