using System.Globalization;
using System.Security.Claims;
using Keel.Audit;
using Keel.Core;
using Keel.Parts;
using Keel.Security;
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
    /// <summary>Authentication property marking an OIDC challenge as an e-signature step-up (prompt=login, max_age=0).</summary>
    public const string StepUpItem = "keel.step_up";
    public const string StepUpPath = "/account/step-up";
    private const string StepUpFailure = "keel.step_up_refused";

    /// <summary>Registers the Blazor UI and its server-side OIDC sign-in (ADR-0014). Call after AddAuthentication().</summary>
    public static AuthenticationBuilder AddKeelUi(this AuthenticationBuilder auth, KeelUiOptions options, bool secureCookies)
    {
        var services = auth.Services;
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, SessionRevalidatingStateProvider>();
        services.AddScoped<CurrentUser>();
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
        });

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
            if (fresh is not null && services.GetService<IAuditLog>() is { } log)
                await log.AppendAsync(new AuditEntry
                {
                    Actor = ActorSnapshot.From(fresh), Action = "session.step_up", Outcome = AuditOutcome.Success, ObjectType = "session",
                    Details = AuditDetails.Of(("authTime", fresh.AuthTime?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))),
                });
        }
        identity.AddClaim(new Claim(SessionExpiresClaim, expires));
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
public sealed class CurrentUser(AuthenticationStateProvider auth)
{
    public async Task<Subject> GetAsync()
    {
        var state = await auth.GetAuthenticationStateAsync();
        return SubjectClaims.FromPrincipal(state.User) ?? throw new InvalidOperationException("No signed-in user in this circuit.");
    }

    /// <summary>A fresh correlation id per user action, written to the audit record of that action.</summary>
    public static string NewCorrelationId() => Guid.CreateVersion7().ToString("D");
}

/// <summary>
/// Ends a Blazor circuit's authentication when the session lifetime recorded at sign-in has passed; the cookie
/// alone isn't re-checked by a long-lived circuit.
/// </summary>
public sealed class SessionRevalidatingStateProvider(ILoggerFactory loggerFactory, TimeProvider clock)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken) =>
        Task.FromResult(IsValid(authenticationState.User, clock.GetUtcNow()));

    internal static bool IsValid(ClaimsPrincipal user, DateTimeOffset now) =>
        long.TryParse(user.FindFirst(KeelUi.SessionExpiresClaim)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exp)
        && now.ToUnixTimeSeconds() < exp;
}
