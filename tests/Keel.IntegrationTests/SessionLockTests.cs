using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using Keel.Audit;
using Keel.Core;
using Keel.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Keel.IntegrationTests;

/// <summary>
/// The idle session lock on the real host (ADR-0026): the server's own timer locks a session nobody is connected to, a locked session's cookie is refused
/// everywhere but the shell and the sign-in endpoints, the lock is audited, only a re-authentication at the IdP releases it, and a session that stays locked
/// too long is terminated (cookie rejected, signed out).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SessionLockTests(PostgresFixture pg)
{
    private const string OidcName = "OpenIdConnect";
    private const string CookieName = "keel.session";
    private const string DownloadRoute = "/downloads/documents/D-000001/files/00000000-0000-0000-0000-000000000001";

    private static ApiFactory ShortTimers(ApiFactory api, double idleMinutes = 0.04, double terminateMinutes = 0.06)
    {
        api.ExtraSettings["Keel:Session:IdleLockMinutes"] = idleMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        api.ExtraSettings["Keel:Session:WarningSeconds"] = "1";
        api.ExtraSettings["Keel:Session:LockedTerminateMinutes"] = terminateMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return api;
    }

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

    private static HttpClient Client(WebApplicationFactory<Program> f, string? cookie = null)
    {
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", $"{CookieName}={cookie}");
        return client;
    }

    private static ClaimsPrincipal CookiePrincipal(string sessionId, string sub = "u-alice", long? expires = null) => new(new ClaimsIdentity(
    [
        new Claim("sub", sub), new Claim("preferred_username", "alice"), new Claim("name", "Alice Example"), new Claim("roles", "plm-viewer"),
        new Claim("us_person", "true"), new Claim(KeelUi.SessionIdClaim, sessionId),
        new Claim(KeelUi.SessionExpiresClaim, (expires ?? DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()).ToString(System.Globalization.CultureInfo.InvariantCulture)),
    ], "Cookies"));

    private static string Mint(WebApplicationFactory<Program> f, ClaimsPrincipal principal) =>
        f.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies").TicketDataFormat.Protect(new AuthenticationTicket(principal, "Cookies"));

    private static async Task<List<AuditRecord>> Events(ApiFactory api)
    {
        await using var ds = api.Database.AppDataSource();
        return [.. await new AuditQueries(ds).ListAsync(new AuditQuery(Limit: 200))];
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline) { if (condition()) return true; await Task.Delay(100); }
        return condition();
    }

    private static ActorSnapshot Alice => ActorSnapshot.System with { Id = "u-alice", Name = "alice" };

    // ---------------------------------------------------------------- the server's own timer and the guard

    [Fact]
    public async Task The_server_locks_an_idle_session_by_itself_and_refuses_its_cookie_everywhere_but_the_shell()
    {
        await using var api = ShortTimers(await ApiFactory.CreateAsync(pg));
        var factory = WithIdp(api);
        var registry = factory.Services.GetRequiredService<SessionLockRegistry>();
        registry.Register("session-a", Alice, DateTimeOffset.UtcNow.AddHours(1));
        var cookie = Mint(factory, CookiePrincipal("session-a"));
        var client = Client(factory, cookie);

        // Active: the data endpoint answers (404: there is no such document; that it is reached at all is the point).
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(DownloadRoute)).StatusCode);

        // Nobody touches the session; no circuit exists. The server's timer locks it (2.4 s).
        Assert.True(await WaitAsync(() => registry.IsLocked("session-a")), "the idle timer locked the session");

        var refused = await client.GetAsync(DownloadRoute);
        Assert.Equal(HttpStatusCode.Locked, refused.StatusCode);
        Assert.Contains("Session locked", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Locked, (await client.GetAsync("/anything/else")).StatusCode);
        Assert.Equal(HttpStatusCode.Locked, (await client.PostAsync("/documents/export", null)).StatusCode);

        // What the lock screen needs still works: the application shell, the framework file, the sign-in endpoint, the health probes.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/parts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/_framework/blazor.web.js")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/account/step-up?returnUrl=%2Fparts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);

        // Another session of the same user is untouched.
        registry.Register("session-b", Alice, DateTimeOffset.UtcNow.AddHours(1));
        Assert.False(registry.IsLocked("session-b"));

        var lockEvent = Assert.Single(await Events(api), e => e.Action == "session.lock");
        Assert.Equal(("u-alice", "session", AuditOutcome.Success), (lockEvent.Actor, lockEvent.ObjectType, lockEvent.Outcome));
        Assert.Contains("idle", lockEvent.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("session-a", lockEvent.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_locked_session_that_is_not_released_is_terminated_its_cookie_rejected_and_the_user_signed_out()
    {
        await using var api = ShortTimers(await ApiFactory.CreateAsync(pg), idleMinutes: 0.03, terminateMinutes: 0.04);
        var factory = WithIdp(api);
        var registry = factory.Services.GetRequiredService<SessionLockRegistry>();
        registry.Register("session-t", Alice, DateTimeOffset.UtcNow.AddHours(1));
        var client = Client(factory, Mint(factory, CookiePrincipal("session-t")));

        Assert.True(await WaitAsync(() => registry.IsTerminated("session-t"), 30));

        var actions = (await Events(api)).Select(e => e.Action).Where(a => a.StartsWith("session.", StringComparison.Ordinal)).ToList();
        Assert.Equal(["session.lock", "session.lock_expired", "session.terminated_idle"], actions);

        // The cookie is no longer accepted: the next page request is treated as signed out and sent to the IdP.
        var page = await client.GetAsync("/parts");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.StartsWith(ApiFactory.Issuer, page.Headers.Location!.ToString(), StringComparison.Ordinal);

        // The sign-out endpoint the circuit navigates to clears the cookie and ends the IdP session.
        var ended = await client.GetAsync(KeelUi.SessionEndedPath);
        Assert.Equal(HttpStatusCode.Redirect, ended.StatusCode);
        Assert.StartsWith(ApiFactory.Issuer + "/protocol/openid-connect/logout", ended.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Contains(ended.Headers.GetValues("Set-Cookie"), c => c.StartsWith(CookieName + "=;", StringComparison.Ordinal) || c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_session_ended_endpoint_cannot_be_used_to_sign_somebody_out_of_a_live_session()
    {
        await using var api = ShortTimers(await ApiFactory.CreateAsync(pg), idleMinutes: 30, terminateMinutes: 30);
        var factory = WithIdp(api);
        var registry = factory.Services.GetRequiredService<SessionLockRegistry>();
        registry.Register("session-live", Alice, DateTimeOffset.UtcNow.AddHours(1));
        var client = Client(factory, Mint(factory, CookiePrincipal("session-live")));

        var res = await client.GetAsync(KeelUi.SessionEndedPath);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/", res.Headers.Location!.OriginalString);
        Assert.False(res.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Redirect, (await Client(factory).GetAsync(KeelUi.SessionEndedPath)).StatusCode); // no cookie at all: also just home
    }

    // ---------------------------------------------------------------- unlocking: only the re-authentication

    private static async Task<(TokenValidatedContext Ctx, IServiceScope Scope)> Validate(WebApplicationFactory<Program> f, ClaimsPrincipal? previous, ClaimsPrincipal fresh, bool stepUp)
    {
        var scope = f.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        if (previous is not null) http.Request.Headers.Cookie = $"{CookieName}={Mint(f, previous)}";
        var options = f.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OidcName);
        var properties = new AuthenticationProperties();
        if (stepUp) properties.Items[KeelUi.StepUpItem] = "1";
        var ctx = new TokenValidatedContext(http, new AuthenticationScheme(OidcName, OidcName, typeof(OpenIdConnectHandler)), options, fresh, properties)
        {
            SecurityToken = new JwtSecurityToken(claims: [new Claim("auth_time", "1790000000")]),
        };
        await options.Events.OnTokenValidated(ctx);
        return (ctx, scope);
    }

    private static ClaimsPrincipal Fresh(string sub) => new(new ClaimsIdentity(
        [new Claim("sub", sub), new Claim("preferred_username", "alice"), new Claim("name", "Alice Example"), new Claim("roles", "plm-viewer"),
         new Claim("us_person", "true"), new Claim("org", "ENG"), new Claim("auth_time", "1790000000")], "oidc"));

    [Fact]
    public async Task Only_a_step_up_by_the_same_person_unlocks_and_the_session_keeps_its_identity()
    {
        await using var api = ShortTimers(await ApiFactory.CreateAsync(pg), idleMinutes: 30, terminateMinutes: 30);
        var factory = WithIdp(api);
        var registry = factory.Services.GetRequiredService<SessionLockRegistry>();
        registry.Register("session-u", Alice, DateTimeOffset.UtcNow.AddHours(1));
        await registry.LockNowAsync("session-u");

        // Somebody else answers the login prompt: refused, still locked.
        var (wrong, wrongScope) = await Validate(factory, CookiePrincipal("session-u"), Fresh("u-mallory"), stepUp: true);
        using (wrongScope) Assert.NotNull(wrong.Result?.Failure);
        Assert.True(registry.IsLocked("session-u"));

        // A plain sign-in (no prompt=login) is a different session and releases nothing.
        var (plain, plainScope) = await Validate(factory, CookiePrincipal("session-u"), Fresh("u-alice"), stepUp: false);
        using (plainScope) Assert.NotEqual("session-u", ((ClaimsIdentity)plain.Principal!.Identity!).FindFirst(KeelUi.SessionIdClaim)!.Value);
        Assert.True(registry.IsLocked("session-u"));

        // The person re-authenticates (prompt=login, max_age=0): the lock is released and the session id is carried over.
        var (ok, okScope) = await Validate(factory, CookiePrincipal("session-u"), Fresh("u-alice"), stepUp: true);
        using (okScope)
        {
            Assert.Null(ok.Result?.Failure);
            Assert.Equal("session-u", ((ClaimsIdentity)ok.Principal!.Identity!).FindFirst(KeelUi.SessionIdClaim)!.Value);
        }
        Assert.False(registry.IsLocked("session-u"));
        var unlock = Assert.Single(await Events(api), e => e.Action == "session.unlock");
        Assert.Contains("step-up", unlock.Payload, StringComparison.Ordinal);
        Assert.Equal(["session.lock", "session.unlock"], (await Events(api)).Select(e => e.Action).Where(a => a is "session.lock" or "session.unlock"));
    }

    [Fact]
    public async Task A_step_up_after_termination_cannot_resurrect_the_session()
    {
        await using var api = ShortTimers(await ApiFactory.CreateAsync(pg), idleMinutes: 0.03, terminateMinutes: 0.03);
        var factory = WithIdp(api);
        var registry = factory.Services.GetRequiredService<SessionLockRegistry>();
        registry.Register("session-x", Alice, DateTimeOffset.UtcNow.AddHours(1));
        Assert.True(await WaitAsync(() => registry.IsTerminated("session-x"), 30));

        var (ctx, scope) = await Validate(factory, CookiePrincipal("session-x"), Fresh("u-alice"), stepUp: true);
        using (scope) Assert.Null(ctx.Result?.Failure); // the login itself is fine ...
        Assert.True(registry.IsTerminated("session-x")); // ... but the old session stays over
    }

    // ---------------------------------------------------------------- configuration at the host

    [Fact]
    public async Task The_host_validates_the_session_settings_and_production_refuses_to_run_without_the_lock()
    {
        await using var invalid = await ApiFactory.CreateAsync(pg);
        invalid.ExtraSettings["Keel:Session:IdleLockMinutes"] = "500";
        Assert.Contains("IdleLockMinutes", Assert.ThrowsAny<Exception>(() => invalid.CreateClient()).ToString(), StringComparison.Ordinal);

        await using var production = await ApiFactory.CreateAsync(pg);
        production.ExtraSettings["Keel:Session:IdleLockMinutes"] = "0";
        production.ExtraSettings["Keel:DataProtection:KeyDirectory"] = Directory.CreateTempSubdirectory("keel-dp-").FullName;
        var asProduction = production.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        Assert.Contains("refused in production", Assert.ThrowsAny<Exception>(() => asProduction.CreateClient()).ToString(), StringComparison.Ordinal);

        // Outside production 0 switches the lock off (development convenience): nothing locks.
        await using var off = await ApiFactory.CreateAsync(pg);
        off.ExtraSettings["Keel:Session:IdleLockMinutes"] = "0";
        var registry = off.Services.GetRequiredService<SessionLockRegistry>();
        Assert.False(registry.Enabled);
    }

    [Fact]
    public async Task Bearer_token_api_requests_are_not_affected_by_the_session_lock()
    {
        await using var api = ShortTimers(await ApiFactory.CreateAsync(pg), idleMinutes: 0.03, terminateMinutes: 0.03);
        var registry = api.Services.GetRequiredService<SessionLockRegistry>();
        registry.Register("session-api", Alice, DateTimeOffset.UtcNow.AddHours(1));
        Assert.True(await WaitAsync(() => registry.IsTerminated("session-api"), 30));

        var res = await api.ClientFor(ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer"])).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode); // the token's own lifetime governs the API
    }
}
