using System.Globalization;
using System.Security.Claims;
using Keel.Audit;
using Keel.Core;
using Keel.Parts;
using Keel.Security;
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
            o.Events.OnTokenValidated = ctx =>
            {
                var expires = DateTimeOffset.UtcNow.Add(options.SessionLifetime).ToUnixTimeSeconds();
                ((ClaimsIdentity)ctx.Principal!.Identity!).AddClaim(new Claim(SessionExpiresClaim, expires.ToString(CultureInfo.InvariantCulture)));
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

    public static void MapKeelUi<TApp>(this WebApplication app)
    {
        app.MapRazorComponents<TApp>().AddInteractiveServerRenderMode();

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
