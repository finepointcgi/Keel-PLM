using System.Globalization;
using System.Security.Claims;
using Keel.Audit;
using Keel.Core;
using Keel.Parts;
using Keel.Security;
using Keel.Web.Services.Demo;
using Keel.Web.Services.Visuals;
using Keel.Workflow;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keel.Web.Services;

public sealed class KeelUiOptions
{
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "keel-web";
    public string ClientSecret { get; set; } = "";
    public bool RequireHttpsMetadata { get; set; } = true;
    /// <summary>Absolute UI session lifetime; the circuit is ended when it passes (NIST SP 800-171 3.1.11).</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);
}

public static class KeelUi
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string OidcScheme = OpenIdConnectDefaults.AuthenticationScheme;
    public const string SessionExpiresClaim = "keel_session_expires";
    /// <summary>Identifies one sign-in (kept across a step-up): the key of the idle lock, shared by every tab of the session (ADR-0026).</summary>
    public const string SessionIdClaim = "keel_session_id";

    /// <summary>
    /// A cookie that authenticates but carries no session id was issued before the idle lock existed (or by another build). The lock
    /// registry cannot track it, so it is refused and the user signs in again, instead of every circuit failing on it.
    /// </summary>
    internal static bool IsStaleSession(ClaimsPrincipal? principal) =>
        principal?.Identity?.IsAuthenticated == true && !principal.HasClaim(c => c.Type == SessionIdClaim && c.Value.Length > 0);
    /// <summary>Added to the circuit's principal while its session is locked.</summary>
    public const string LockedClaim = "keel_session_locked";
    public const string SessionEndedPath = "/account/session-ended";
    /// <summary>Authentication property marking an OIDC challenge as an e-signature step-up (prompt=login, max_age=0).</summary>
    public const string StepUpItem = "keel.step_up";
    public const string StepUpPath = "/account/step-up";
    private const string StepUpFailure = "keel.step_up_refused";

    /// <summary>Registers the Blazor UI and its server-side OIDC sign-in (ADR-0014). Call after AddAuthentication().</summary>
    public static AuthenticationBuilder AddKeelUi(this AuthenticationBuilder auth, KeelUiOptions options, bool secureCookies, bool demo = false)
    {
        var services = auth.Services;
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, SessionRevalidatingStateProvider>();
        services.AddScoped<CurrentUser>();
        // UX-1 (ADR-0028): shell state, feedback, search and the Home dashboard reads.
        services.AddScoped<ToastService>();
        services.AddScoped<ShellJs>();
        services.AddScoped<LifecycleSteps>();
        services.AddScoped<ShellState>();
        services.AddScoped<PaletteState>();
        services.AddScoped<WorkspaceReads>();
        services.AddScoped<CommentDrafts>(); // UX-4: comment drafts live in the circuit only
        services.AddKeelVisuals(); // UX-3 relationship pictures (ADR-0029)
        services.AddScoped<IGlobalSearch, GlobalSearchService>();
        services.AddSingleton(options);

        auth.AddCookie(CookieScheme, o =>
        {
            o.Cookie.Name = "keel.session";
            o.Cookie.HttpOnly = true;
            // Lax, not Strict: the browser must send the cookie on the top-level redirect back from the IdP.
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = options.SessionLifetime;
            o.SlidingExpiration = false;
            o.ForwardChallenge = OidcScheme;
            o.AccessDeniedPath = "/access-denied";
            // A session that was locked for too long is over (3.1.11): its cookie is refused and removed on the next request, whichever tab or client sends it.
            o.Events.OnValidatePrincipal = async ctx =>
            {
                var registry = ctx.HttpContext.RequestServices.GetService<SessionLockRegistry>();
                if (registry is { Enabled: true } && (IsStaleSession(ctx.Principal) || (ctx.Principal?.FindFirst(SessionIdClaim)?.Value is { } id && registry.IsTerminated(id))))
                {
                    ctx.RejectPrincipal();
                    await ctx.HttpContext.SignOutAsync(CookieScheme);
                }
            };
        });

        // Demo mode (ADR-0045) swaps the remote identity provider for a local sign-in page under the same scheme name; it is off unless the host proved both
        // ASPNETCORE_ENVIRONMENT=Demo and Keel:Demo:Enabled=true (DemoMode.IsEnabled). Production never reaches this branch.
        if (demo) return auth.AddKeelDemo();

        auth.AddOpenIdConnect(OidcScheme, o =>
        {
            o.Authority = options.Authority;
            o.ClientId = options.ClientId;
            o.ClientSecret = options.ClientSecret;
            o.RequireHttpsMetadata = options.RequireHttpsMetadata;
            o.ResponseType = "code";
            o.UsePkce = true;
            o.SaveTokens = false; // tokens are not needed after sign-in; nothing token-shaped is kept in the cookie
            o.MapInboundClaims = false;
            o.GetClaimsFromUserInfoEndpoint = false;
            o.Scope.Clear();
            o.Scope.Add("openid");
            o.Scope.Add("profile");
            o.SignInScheme = CookieScheme;
            o.TokenValidationParameters.NameClaimType = "preferred_username";
            o.TokenValidationParameters.RoleClaimType = "roles";
            // E-signature step-up (A9, ADR-0007): force a fresh login and require auth_time to be returned.
            o.Events.OnRedirectToIdentityProvider = ctx =>
            {
                if (ctx.Properties.Items.ContainsKey(StepUpItem))
                {
                    ctx.ProtocolMessage.Prompt = "login";
                    ctx.ProtocolMessage.MaxAge = "0";
                }
                return Task.CompletedTask;
            };
            o.Events.OnTokenValidated = OnTokenValidatedAsync(options);
            o.Events.OnRemoteFailure = ctx =>
            {
                if (ctx.Failure?.Message == StepUpFailure)
                {
                    ctx.Response.Redirect("/access-denied");
                    ctx.HandleResponse();
                }
                return Task.CompletedTask;
            };
            // Keycloak (and most IdPs) require client_id when no id_token_hint is sent with the logout request.
            o.Events.OnRedirectToIdentityProviderForSignOut = ctx =>
            {
                ctx.ProtocolMessage.ClientId = options.ClientId;
                return Task.CompletedTask;
            };
        });
        return auth;
    }

    private static Func<TokenValidatedContext, Task> OnTokenValidatedAsync(KeelUiOptions options) => async ctx =>
    {
        var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
        // Capture auth_time from the id token on every sign-in so signatures can require recent authentication.
        if (identity.FindFirst("auth_time") is null
            && ctx.SecurityToken is System.IdentityModel.Tokens.Jwt.JwtSecurityToken jwt && jwt.Claims.FirstOrDefault(c => c.Type == "auth_time") is { } authTime)
            identity.AddClaim(new Claim("auth_time", authTime.Value));

        var expires = DateTimeOffset.UtcNow.Add(options.SessionLifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var services = ctx.HttpContext.RequestServices;
        var sessionId = Guid.NewGuid().ToString("N");
        string? unlockSessionId = null;
        var fresh = SubjectClaims.FromPrincipal(ctx.Principal);
        if (ctx.Properties?.Items.ContainsKey(StepUpItem) == true)
        {
            // The step-up replaces the cookie principal, so it must be the same person re-authenticating.
            var previous = (await ctx.HttpContext.AuthenticateAsync(CookieScheme)).Principal;
            var verdict = StepUp.Check(previous, ctx.Principal);
            if (verdict is StepUp.Verdict.SubjectMismatch or StepUp.Verdict.MissingAuthTime)
            {
                if (services.GetService<IAuditLog>() is { } audit)
                    await audit.AppendAsync(new AuditEntry
                    {
                        Actor = SubjectClaims.FromPrincipal(previous ?? ctx.Principal) is { } who ? ActorSnapshot.From(who) : ActorSnapshot.System,
                        Action = "session.step_up", Outcome = AuditOutcome.Denied, ObjectType = "session",
                        Details = AuditDetails.Of(("reason", verdict == StepUp.Verdict.SubjectMismatch ? "step_up.subject_mismatch" : "step_up.auth_time_missing")),
                    });
                ctx.Fail(StepUpFailure);
                return;
            }
            // A step-up proves recent authentication; it does not extend the absolute session lifetime.
            expires = previous?.FindFirst(SessionExpiresClaim)?.Value ?? expires;
            // The session keeps its identity across the step-up, so the lock of this session (and of every tab of it) can be released by it.
            if (previous?.FindFirst(SessionIdClaim)?.Value is { Length: > 0 } carried) { sessionId = carried; unlockSessionId = carried; }
            if (fresh is not null && services.GetService<IAuditLog>() is { } log)
                await log.AppendAsync(new AuditEntry
                {
                    Actor = ActorSnapshot.From(fresh), Action = "session.step_up", Outcome = AuditOutcome.Success, ObjectType = "session",
                    Details = AuditDetails.Of(("authTime", fresh.AuthTime?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))),
                });
        }
        identity.AddClaim(new Claim(SessionExpiresClaim, expires));
        identity.AddClaim(new Claim(SessionIdClaim, sessionId));
        // Re-authentication at the IdP (prompt=login, max_age=0, same subject: verified above) is what clears an idle lock (3.1.10, ADR-0026).
        if (unlockSessionId is not null && services.GetService<SessionLockRegistry>() is { } locks)
            await locks.UnlockAsync(unlockSessionId, "step-up");
        // The user directory learns who signed in (and their current attributes) for participant pickers (A50).
        if (fresh is not null && services.GetService<UserDirectory>() is { } directory) await directory.UpsertAsync(fresh);
    };

    /// <summary>Only same-site relative paths are accepted as return targets; anything else goes home.</summary>
    internal static string LocalReturnUrl(string? returnUrl) =>
        returnUrl is { Length: > 0 } && returnUrl[0] == '/' && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\')) ? returnUrl : "/";

    public static void MapKeelUi<TApp>(this WebApplication app)
    {
        app.MapRazorComponents<TApp>().AddInteractiveServerRenderMode();

        // E-signature step-up: a challenge of the OIDC scheme with prompt=login and max_age=0. The result replaces the
        // session cookie (same subject required, see OnTokenValidated) and records the new auth_time.
        app.MapGet(StepUpPath, (string? returnUrl) => Results.Challenge(
            new AuthenticationProperties(new Dictionary<string, string?> { [StepUpItem] = "1" }) { RedirectUri = LocalReturnUrl(returnUrl) }, [OidcScheme])).AllowAnonymous();

        // The end of a session that stayed locked too long: sign out here (cookie and IdP session) so the next visit needs a full login. Only a session the
        // registry has terminated is signed out; anything else is sent home, so this cannot be used to log somebody out.
        app.MapGet(SessionEndedPath, async (HttpContext ctx) =>
        {
            var registry = ctx.RequestServices.GetService<SessionLockRegistry>();
            // The cookie of a terminated session no longer authenticates (OnValidatePrincipal), so read its ticket directly: it is ours (encrypted and signed
            // with the host's data protection keys), and what matters is only whether its session is the one the registry ended.
            var cookieOptions = ctx.RequestServices.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieScheme);
            var protectedTicket = ctx.Request.Cookies[cookieOptions.Cookie.Name ?? "keel.session"];
            var principal = protectedTicket is null ? null : cookieOptions.TicketDataFormat.Unprotect(protectedTicket)?.Principal;
            var id = principal?.FindFirst(SessionIdClaim)?.Value;
            if (registry is null || id is null || !registry.IsTerminated(id)) return Results.Redirect("/");
            await ctx.SignOutAsync(CookieScheme);
            await ctx.SignOutAsync(OidcScheme, new AuthenticationProperties { RedirectUri = "/" });
            return Results.Empty;
        }).AllowAnonymous();

        app.MapPost("/account/logout", async (HttpContext ctx, IAntiforgery antiforgery) =>
        {
            // The antiforgery middleware only records its result for minimal APIs; reject cross-site logout requests here.
            if (!await antiforgery.IsRequestValidAsync(ctx)) return Results.BadRequest();
            if (SubjectClaims.FromPrincipal(ctx.User) is { } s)
                await ctx.RequestServices.GetRequiredService<IAuditLog>().AppendAsync(new AuditEntry
                {
                    Actor = ActorSnapshot.From(s), Action = "session.logout", Outcome = AuditOutcome.Success, ObjectType = "session",
                });
            await ctx.SignOutAsync(CookieScheme);
            await ctx.SignOutAsync(OidcScheme, new AuthenticationProperties { RedirectUri = "/" });
            return Results.Empty;
        }).DisableAntiforgery(); // validated explicitly above
    }
}

/// <summary>Per-circuit access to the signed-in user.</summary>
public sealed class CurrentUser(AuthenticationStateProvider auth, SessionLockCoordinator? sessionLock = null)
{
    /// <summary>
    /// The signed-in user of this circuit. Every UI service call starts here, so a locked session fails closed: nothing is read or changed
    /// until the user has signed in again (ADR-0026), whatever the page in the browser still shows.
    /// </summary>
    public async Task<Subject> GetAsync()
    {
        var state = await auth.GetAuthenticationStateAsync();
        sessionLock?.ThrowIfLocked(state.User);
        return SubjectClaims.FromPrincipal(state.User) ?? throw new InvalidOperationException("No signed-in user in this circuit.");
    }

    /// <summary>A fresh correlation id per user action, written to the audit record of that action.</summary>
    public static string NewCorrelationId() => Guid.CreateVersion7().ToString("D");
}

/// <summary>
/// Ends a Blazor circuit's authentication when the session lifetime recorded at sign-in has passed; the cookie
/// alone isn't re-checked by a long-lived circuit.
/// </summary>
public sealed class SessionRevalidatingStateProvider(ILoggerFactory loggerFactory, TimeProvider clock, SessionLockRegistry? locks = null)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        var user = authenticationState.User;
        if (!IsValid(user, clock.GetUtcNow())) return Task.FromResult(false);
        if (locks is not { Enabled: true }) return Task.FromResult(true);
        // With the idle lock on, a session that cannot be identified (a cookie from before the lock existed) or that was terminated is not valid.
        var id = user.FindFirst(KeelUi.SessionIdClaim)?.Value;
        return Task.FromResult(id is not null && !locks.IsTerminated(id));
    }

    /// <summary>The circuit's principal, marked <see cref="KeelUi.LockedClaim"/> while its session is locked, so components and guards can see it.</summary>
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var state = await base.GetAuthenticationStateAsync();
        if (locks is { Enabled: true } && state.User.FindFirst(KeelUi.SessionIdClaim)?.Value is { } id && locks.IsLocked(id) && !state.User.HasClaim(c => c.Type == KeelUi.LockedClaim))
        {
            var marked = new ClaimsPrincipal(state.User.Identities.Select(i => i.Clone()));
            marked.AddIdentity(new ClaimsIdentity([new Claim(KeelUi.LockedClaim, "true")]));
            return new AuthenticationState(marked);
        }
        return state;
    }

    internal static bool IsValid(ClaimsPrincipal user, DateTimeOffset now) =>
        long.TryParse(user.FindFirst(KeelUi.SessionExpiresClaim)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exp)
        && now.ToUnixTimeSeconds() < exp;
}
