using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using Keel.Audit;
using Keel.Core;
using Keel.Workflow;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// E-signature step-up in the Blazor UI (A9, ADR-0007): /account/step-up challenges the IdP with prompt=login and max_age=0,
/// and the returning identity must be the same person. The token-validated handler is exercised directly because it
/// needs an IdP-signed id token; the cookie it compares against is minted with the host's own data protection keys.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UiStepUpTests(PostgresFixture pg)
{
    private const string OidcName = "OpenIdConnect";
    private const string CookieName = "keel.session";

    private static WebApplicationFactory<Program> WithIdp(ApiFactory api) => api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        s.PostConfigure<OpenIdConnectOptions>(OidcName, o =>
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
        })));

    private static HttpClient NoRedirect(WebApplicationFactory<Program> f) => f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static ClaimsPrincipal Cookie(string sub, long sessionExpires, string name = "alice") => new(new ClaimsIdentity(
    [
        new Claim("sub", sub), new Claim("preferred_username", name), new Claim("name", name + " Example"), new Claim("roles", "plm-viewer"),
        new Claim("us_person", "true"), new Claim(Web.Services.KeelUi.SessionExpiresClaim, sessionExpires.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        // Every real session carries an id since the idle lock; a cookie without one is refused as stale.
        new Claim(Web.Services.KeelUi.SessionIdClaim, "test-session-" + sub),
    ], "Cookies"));

    private static string MintCookie(WebApplicationFactory<Program> f, ClaimsPrincipal principal)
    {
        var options = f.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies");
        return options.TicketDataFormat.Protect(new AuthenticationTicket(principal, "Cookies"));
    }

    // ---------------------------------------------------------------- the challenge

    [Fact]
    public async Task Step_up_redirects_to_the_idp_demanding_a_fresh_login()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var factory = WithIdp(api);
        var res = await NoRedirect(factory).GetAsync("/account/step-up?returnUrl=%2Ftasks%2F00000000-0000-0000-0000-000000000001");

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var location = res.Headers.Location!.ToString();
        Assert.StartsWith(ApiFactory.Issuer + "/protocol/openid-connect/auth?", location, StringComparison.Ordinal);
        Assert.Contains("prompt=login", location, StringComparison.Ordinal);
        Assert.Contains("max_age=0", location, StringComparison.Ordinal);
        Assert.Contains("client_id=keel-web", location, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", location, StringComparison.Ordinal);
        Assert.All(res.Headers.GetValues("Set-Cookie"), c => Assert.Contains("httponly", c, StringComparison.OrdinalIgnoreCase));

        // An ordinary (non step-up) sign-in does not force a new login.
        var plain = await NoRedirect(factory).GetAsync("/parts");
        Assert.DoesNotContain("prompt=login", plain.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("max_age", plain.Headers.Location.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Step_up_for_a_signed_in_user_also_challenges_and_refuses_a_non_local_return_target()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var factory = WithIdp(api);
        var client = NoRedirect(factory);
        client.DefaultRequestHeaders.Add("Cookie", $"{CookieName}={MintCookie(factory, Cookie("u-alice", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()))}");
        var res = await client.GetAsync("/account/step-up?returnUrl=https%3A%2F%2Fevil.example%2Fphish");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var location = res.Headers.Location!.ToString();
        Assert.Contains("prompt=login", location, StringComparison.Ordinal);
        Assert.Contains("max_age=0", location, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", location, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- what comes back from the IdP

    private static async Task<(TokenValidatedContext Ctx, IServiceScope Scope)> Validate(
        WebApplicationFactory<Program> f, ClaimsPrincipal? previous, ClaimsPrincipal fresh, bool stepUp, string? jwtAuthTime = null)
    {
        var scope = f.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        if (previous is not null) http.Request.Headers.Cookie = $"{CookieName}={MintCookie(f, previous)}";
        var options = f.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OidcName);
        var properties = new AuthenticationProperties();
        if (stepUp) properties.Items[Web.Services.KeelUi.StepUpItem] = "1";
        var ctx = new TokenValidatedContext(http, new AuthenticationScheme(OidcName, OidcName, typeof(OpenIdConnectHandler)), options, fresh, properties)
        {
            SecurityToken = new JwtSecurityToken(claims: jwtAuthTime is null ? [] : [new Claim("auth_time", jwtAuthTime)]),
        };
        await options.Events.OnTokenValidated(ctx);
        return (ctx, scope);
    }

    private static ClaimsPrincipal Fresh(string sub, string? authTime = "1790000000") => new(new ClaimsIdentity(
        [new Claim("sub", sub), new Claim("preferred_username", "alice"), new Claim("name", "Alice Example"), new Claim("roles", "plm-viewer"), new Claim("roles", "plm-author"),
         new Claim("us_person", "true"), new Claim("org", "ENG"), new Claim("programs", "ORCA"), .. authTime is null ? Array.Empty<Claim>() : [new Claim("auth_time", authTime)]], "oidc"));

    private static async Task<List<AuditRecord>> Events(ApiFactory api)
    {
        await using var ds = api.Database.AppDataSource();
        return [.. await new AuditQueries(ds).ListAsync(new AuditQuery(Limit: 100))];
    }

    [Fact]
    public async Task A_step_up_by_the_same_person_replaces_the_session_without_extending_it_and_is_audited()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var factory = WithIdp(api);
        var originalExpiry = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var (ctx, scope) = await Validate(factory, Cookie("u-alice", originalExpiry), Fresh("u-alice"), stepUp: true);
        using var _ = scope;

        Assert.Null(ctx.Result?.Failure);
        var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
        Assert.Equal("1790000000", identity.FindFirst("auth_time")!.Value);
        // Re-authenticating proves who you are now; it does not buy a longer absolute session.
        Assert.Equal(originalExpiry.ToString(System.Globalization.CultureInfo.InvariantCulture), identity.FindFirst(Web.Services.KeelUi.SessionExpiresClaim)!.Value);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), Security.SubjectClaims.FromPrincipal(ctx.Principal)!.AuthTime);

        var audit = Assert.Single(await Events(api), e => e.Action == "session.step_up");
        Assert.Equal((AuditOutcome.Success, "u-alice"), (audit.Outcome, audit.Actor));
        // The directory learned the user from this sign-in.
        Assert.Equal(1, await Scalar(api, "SELECT count(*) FROM identity.user_directory WHERE sub = 'u-alice' AND username = 'alice'"));
    }

    [Fact]
    public async Task A_step_up_by_a_different_person_is_rejected_and_audited()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var factory = WithIdp(api);
        var (ctx, scope) = await Validate(factory, Cookie("u-alice", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()), Fresh("u-mallory"), stepUp: true);
        using var _ = scope;

        Assert.NotNull(ctx.Result?.Failure);
        Assert.Contains("step_up", ctx.Result!.Failure!.Message, StringComparison.Ordinal);
        var audit = Assert.Single(await Events(api), e => e.Action == "session.step_up");
        Assert.Equal(AuditOutcome.Denied, audit.Outcome);
        Assert.Equal("u-alice", audit.Actor);                                  // the session being attacked
        Assert.Contains("step_up.subject_mismatch", audit.Payload, StringComparison.Ordinal);
        Assert.Equal(0, await Scalar(api, "SELECT count(*) FROM identity.user_directory WHERE sub = 'u-mallory'"));
    }

    [Fact]
    public async Task A_step_up_result_without_auth_time_is_rejected()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var factory = WithIdp(api);
        var (ctx, scope) = await Validate(factory, Cookie("u-alice", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()), Fresh("u-alice", authTime: null), stepUp: true);
        using var _ = scope;
        Assert.NotNull(ctx.Result?.Failure);
        Assert.Contains("step_up.auth_time_missing", (await Events(api)).Single(e => e.Action == "session.step_up").Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_sign_in_captures_auth_time_from_the_id_token_and_sets_a_new_session_expiry()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var factory = WithIdp(api);
        var before = DateTimeOffset.UtcNow;
        // The id token carries auth_time but the principal built from it does not (as with claim filtering).
        var (ctx, scope) = await Validate(factory, previous: null, Fresh("u-alice", authTime: null), stepUp: false, jwtAuthTime: "1790000123");
        using var _ = scope;

        Assert.Null(ctx.Result?.Failure);
        var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
        Assert.Equal("1790000123", identity.FindFirst("auth_time")!.Value);
        var expiry = DateTimeOffset.FromUnixTimeSeconds(long.Parse(identity.FindFirst(Web.Services.KeelUi.SessionExpiresClaim)!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.InRange(expiry, before.AddHours(8).AddSeconds(-5), DateTimeOffset.UtcNow.AddHours(8).AddSeconds(5));
        Assert.DoesNotContain(await Events(api), e => e.Action == "session.step_up");
        Assert.Equal(1, await Scalar(api, "SELECT count(*) FROM identity.user_directory WHERE sub = 'u-alice'"));
    }

    private static async Task<long> Scalar(ApiFactory api, string sql)
    {
        await using var conn = new NpgsqlConnection(api.Database.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
