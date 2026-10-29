using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Keel.Workflow;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keel.Web.Services.Demo;

/// <summary>
/// Stands in for the OpenID Connect scheme in demo mode (registered under the same scheme name, so the cookie scheme's forwarded challenge, the
/// e-signature step-up and sign-out need no change): a challenge goes to the local demo sign-in page, a sign-out returns to the page asked for.
/// There is no remote identity provider and nothing to authenticate here.
/// </summary>
internal sealed class DemoAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder), IAuthenticationSignOutHandler
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var query = new Dictionary<string, string?> { ["returnUrl"] = KeelUi.LocalReturnUrl(properties.RedirectUri) };
        if (properties.Items.ContainsKey(KeelUi.StepUpItem)) query["stepUp"] = "1";
        Response.Redirect(QueryHelpers.AddQueryString(DemoMode.SignInPath, query));
        return Task.CompletedTask;
    }

    public Task SignOutAsync(AuthenticationProperties? properties)
    {
        Response.Redirect(KeelUi.LocalReturnUrl(properties?.RedirectUri));
        return Task.CompletedTask;
    }
}

/// <summary>The demo sign-in page and its two actions: sign in as a demo user, and confirm identity for an e-signature or to unlock an idle session.</summary>
public static class DemoSignIn
{
    /// <summary>Registers the demo services and the OIDC-named demo scheme. Called only when <see cref="DemoMode.IsEnabled"/> is true.</summary>
    public static AuthenticationBuilder AddKeelDemo(this AuthenticationBuilder auth)
    {
        auth.Services.AddSingleton(new DemoModeInfo(DemoMode.BannerText));
        auth.Services.AddHostedService<DemoDatabaseGuard>();
        return auth.AddScheme<AuthenticationSchemeOptions, DemoAuthenticationHandler>(KeelUi.OidcScheme, displayName: "Demo sign-in", configureOptions: null);
    }

    public static void MapKeelDemo(this WebApplication app)
    {
        app.MapGet(DemoMode.SignInPath, (HttpContext ctx, IAntiforgery antiforgery, string? returnUrl, string? stepUp) =>
        {
            var target = KeelUi.LocalReturnUrl(returnUrl);
            var current = ctx.User.Identity?.IsAuthenticated == true ? DemoUsers.Find(ctx.User.FindFirst("preferred_username")?.Value) : null;
            var token = antiforgery.GetAndStoreTokens(ctx);
            var html = stepUp == "1" && current is not null ? ConfirmPage(current, target, token) : ChoosePage(target, token);
            return Results.Content(html, "text/html; charset=utf-8");
        }).AllowAnonymous();

        app.MapPost(DemoMode.SignInPath, async (HttpContext ctx, IAntiforgery antiforgery, TimeProvider clock) =>
        {
            if (!await antiforgery.IsRequestValidAsync(ctx)) return Results.BadRequest();
            var form = await ctx.Request.ReadFormAsync();
            var user = DemoUsers.Find(form["username"].ToString());
            if (user is null) return Results.BadRequest("Unknown demo user.");
            var now = clock.GetUtcNow();
            var options = ctx.RequestServices.GetRequiredService<KeelUiOptions>();
            var principal = DemoUsers.CreatePrincipal(user, now, Guid.NewGuid().ToString("N"), now.Add(options.SessionLifetime));
            await ctx.SignInAsync(KeelUi.CookieScheme, principal);
            if (SubjectClaims.FromPrincipal(principal) is { } subject && ctx.RequestServices.GetService<UserDirectory>() is { } directory)
                await directory.UpsertAsync(subject, ctx.RequestAborted);
            return Results.Redirect(KeelUi.LocalReturnUrl(form["returnUrl"].ToString()));
        }).AllowAnonymous().DisableAntiforgery(); // validated explicitly above

        // The demo's "authenticate again": same person, new auth_time, same session (so it releases the idle lock of that session), exactly what a fresh IdP login does.
        app.MapPost(DemoMode.SignInPath + "/confirm", async (HttpContext ctx, IAntiforgery antiforgery, TimeProvider clock) =>
        {
            if (!await antiforgery.IsRequestValidAsync(ctx)) return Results.BadRequest();
            var form = await ctx.Request.ReadFormAsync();
            var previous = (await ctx.AuthenticateAsync(KeelUi.CookieScheme)).Principal;
            var user = previous?.Identity?.IsAuthenticated == true ? DemoUsers.Find(previous.FindFirst("preferred_username")?.Value) : null;
            if (user is null || previous is null) return Results.Redirect(DemoMode.SignInPath);
            var now = clock.GetUtcNow();
            var options = ctx.RequestServices.GetRequiredService<KeelUiOptions>();
            var sessionId = previous.FindFirst(KeelUi.SessionIdClaim)?.Value is { Length: > 0 } carried ? carried : Guid.NewGuid().ToString("N");
            // A step-up proves recent authentication; it does not extend the absolute session lifetime.
            var expires = long.TryParse(previous.FindFirst(KeelUi.SessionExpiresClaim)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exp)
                ? DateTimeOffset.FromUnixTimeSeconds(exp) : now.Add(options.SessionLifetime);
            var fresh = DemoUsers.CreatePrincipal(user, now, sessionId, expires);
            var verdict = StepUp.Check(previous, fresh);
            if (verdict is not (StepUp.Verdict.Accepted or StepUp.Verdict.NoPriorSession)) return Results.Redirect("/access-denied");
            var services = ctx.RequestServices;
            if (SubjectClaims.FromPrincipal(fresh) is { } subject)
            {
                if (services.GetService<IAuditLog>() is { } audit)
                    await audit.AppendAsync(new AuditEntry
                    {
                        Actor = ActorSnapshot.From(subject), Action = "session.step_up", Outcome = AuditOutcome.Success, ObjectType = "session",
                        Details = AuditDetails.Of(("authTime", subject.AuthTime?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))),
                    }, ctx.RequestAborted);
                if (services.GetService<UserDirectory>() is { } directory) await directory.UpsertAsync(subject, ctx.RequestAborted);
            }
            if (services.GetService<SessionLockRegistry>() is { } locks) await locks.UnlockAsync(sessionId, "step-up", ctx.RequestAborted);
            await ctx.SignInAsync(KeelUi.CookieScheme, fresh);
            return Results.Redirect(KeelUi.LocalReturnUrl(form["returnUrl"].ToString()));
        }).AllowAnonymous().DisableAntiforgery(); // validated explicitly above
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static StringBuilder Head(string title)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">")
          .Append("<title>").Append(E(title)).Append(" · Keel PLM</title>")
          .Append("<link rel=\"stylesheet\" href=\"/_content/Keel.Web/css/tokens.css\"><link rel=\"stylesheet\" href=\"/_content/Keel.Web/css/base.css\">")
          .Append("<link rel=\"stylesheet\" href=\"/_content/Keel.Web/css/layout.css\"><link rel=\"stylesheet\" href=\"/_content/Keel.Web/css/components.css\">")
          .Append("<link rel=\"stylesheet\" href=\"/_content/Keel.Web/css/demo.css\"></head><body>")
          .Append("<div class=\"demo-banner\" role=\"note\" data-demo-banner>").Append(E(DemoMode.BannerText)).Append("</div>")
          .Append("<main class=\"demo-signin\">");
        return sb;
    }

    private static string Hidden(AntiforgeryTokenSet token, string returnUrl) =>
        $"<input type=\"hidden\" name=\"{E(token.FormFieldName)}\" value=\"{E(token.RequestToken)}\"><input type=\"hidden\" name=\"returnUrl\" value=\"{E(returnUrl)}\">";

    internal static string ChoosePage(string returnUrl, AntiforgeryTokenSet token)
    {
        var sb = Head("Demo sign-in");
        sb.Append("<h1>Keel PLM demo sign-in</h1>")
          .Append("<p>Choose who to sign in as. There are no passwords: this is a demonstration with synthetic data, and everyone can be anyone. ")
          .Append("Each person has different roles, programs and export attributes, so the same page looks different for each of them.</p>")
          .Append("<ul class=\"demo-users\">");
        foreach (var u in DemoUsers.List)
        {
            sb.Append("<li class=\"demo-user\"><form method=\"post\" action=\"").Append(DemoMode.SignInPath).Append("\">").Append(Hidden(token, returnUrl))
              .Append("<input type=\"hidden\" name=\"username\" value=\"").Append(E(u.Username)).Append("\">")
              .Append("<div class=\"demo-user-head\"><strong>").Append(E(u.DisplayName)).Append("</strong> <code>").Append(E(u.Username)).Append("</code></div>")
              .Append("<div class=\"demo-user-roles\">").Append(E(string.Join(", ", u.Roles))).Append("</div>")
              .Append("<div class=\"demo-user-attrs\">").Append(E(u.Summary)).Append("</div>")
              .Append("<p class=\"demo-user-try\">").Append(E(u.WhatToTry)).Append("</p>")
              .Append("<button type=\"submit\">Sign in as ").Append(E(u.Username)).Append("</button></form></li>");
        }
        sb.Append("</ul><p class=\"demo-note\">Malware scanning of uploads is turned off in this demo (acknowledged); do not upload anything you would not want others to see.</p></main></body></html>");
        return sb.ToString();
    }

    internal static string ConfirmPage(DemoUser user, string returnUrl, AntiforgeryTokenSet token)
    {
        var sb = Head("Confirm it is you");
        sb.Append("<h1>Confirm it is you</h1>")
          .Append("<p>An electronic signature (or an unlock after inactivity) needs a fresh sign-in. In a real installation the identity provider asks for your credentials or smart card here. ")
          .Append("In this demo there are none: confirm that you are <strong>").Append(E(user.DisplayName)).Append("</strong> (<code>").Append(E(user.Username)).Append("</code>).</p>")
          .Append("<form method=\"post\" action=\"").Append(DemoMode.SignInPath).Append("/confirm\">").Append(Hidden(token, returnUrl))
          .Append("<button type=\"submit\" data-demo-confirm>Confirm it is me</button></form>")
          .Append("<p><a href=\"").Append(E(returnUrl)).Append("\">Cancel</a></p></main></body></html>");
        return sb.ToString();
    }
}
