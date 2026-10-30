using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Keel.Core;
using Keel.Security;
using Keel.Web.Components.Pages.Admin;
using Keel.Web.Services;
using Keel.Workflow;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.UnitTests;

/// <summary>Sign-in status (ADR-0058): the warning rules, the acr requirement for e-signatures, the discovery parsing and the read-only admin page.</summary>
public sealed class SignInStatusTests : BunitContext
{
    private static SignInSettings Prod(Func<SignInSettings, SignInSettings>? change = null)
    {
        var s = new SignInSettings
        {
            Environment = "Production", Authority = "https://idp.example.mil/realms/keel", ClientId = "keel-web", Audience = "keel-api",
            Method = SignInMethods.Certificate, StepUpAcr = "2", IdleLockMinutes = 15, RequireFips = true, FipsActive = true, ProxyEnabled = true, SecureCookies = true,
        };
        return change?.Invoke(s) ?? s;
    }

    private static List<string> Codes(SignInSettings s, SessionObservation? session = null, IdpProbeResult? idp = null) =>
        SignInAssessor.Assess(s, session, idp).Select(f => f.Code).ToList();

    [Fact]
    public void A_sound_certificate_production_profile_has_no_warnings()
    {
        Assert.Empty(SignInAssessor.Assess(Prod()));
    }

    [Fact]
    public void Password_sign_in_in_production_is_a_warning_but_only_a_note_in_development()
    {
        var prod = SignInAssessor.Assess(Prod(s => s with { Method = SignInMethods.Password }));
        Assert.Contains(prod, f => f.Code == "password.production" && f.Severity == SignInSeverity.Warning);
        var dev = SignInAssessor.Assess(Prod(s => s with { Method = SignInMethods.Password, Environment = "Development", RequireFips = false, ProxyEnabled = false }));
        Assert.Contains(dev, f => f.Code == "password.development" && f.Severity == SignInSeverity.Info);
        Assert.DoesNotContain(dev, f => f.Severity != SignInSeverity.Info);
    }

    [Fact]
    public void A_password_fallback_is_flagged_and_a_password_can_sign_when_no_level_is_required()
    {
        Assert.Contains("password.fallback", Codes(Prod(s => s with { PasswordFallback = true })));
        Assert.DoesNotContain("stepup.password-can-sign", Codes(Prod(s => s with { PasswordFallback = true })));
        Assert.Contains("stepup.password-can-sign", Codes(Prod(s => s with { PasswordFallback = true, StepUpAcr = "" })));
    }

    [Fact]
    public void Demo_http_authority_and_missing_idp_are_critical()
    {
        Assert.Contains(SignInAssessor.Assess(Prod(s => s with { Demo = true })), f => f.Code == "demo.enabled" && f.Severity == SignInSeverity.Critical);
        var http = SignInAssessor.Assess(Prod(s => s with { Authority = "http://idp.example.mil/realms/keel", RequireHttpsMetadata = false }));
        Assert.Contains(http, f => f.Code == "authority.http" && f.Severity == SignInSeverity.Critical);
        Assert.Contains(http, f => f.Code == "metadata.http" && f.Severity == SignInSeverity.Critical);
        Assert.Contains("authority.missing", Codes(Prod(s => s with { Authority = "" })));
        Assert.Contains("method.invalid", Codes(Prod(s => s with { Method = "Kerberos" })));
        Assert.Equal(SignInSeverity.Critical, SignInAssessor.Assess(Prod(s => s with { Demo = true }))[0].Severity); // worst first
    }

    [Fact]
    public void Production_hardening_gaps_are_warnings()
    {
        var codes = Codes(Prod(s => s with { RequireFips = false, IdleLockMinutes = 0, StepUpMaxAgeSeconds = 900, SecureCookies = false }));
        Assert.Contains("fips.not-required", codes);
        Assert.Contains("idle.off", codes);
        Assert.Contains("stepup.window", codes);
        Assert.Contains("cookie.insecure", codes);
    }

    [Fact]
    public void What_the_idp_says_about_itself_is_compared_with_the_configuration()
    {
        IdpProbeResult Idp(bool match = true, bool pkce = true, params string[] acr) => new(true, null, "https://idp.example.mil/realms/keel", match, acr, pkce, ["RS256"], true);
        Assert.Empty(Codes(Prod(), idp: Idp(acr: ["1", "2"])));
        Assert.Contains("idp.issuer", Codes(Prod(), idp: Idp(match: false)));
        Assert.Contains("idp.pkce", Codes(Prod(), idp: Idp(pkce: false)));
        Assert.Contains("idp.acr", Codes(Prod(), idp: Idp(acr: ["1"])));
        Assert.Contains("idp.acr.not-advertised", Codes(Prod(), idp: Idp()));
        Assert.Contains("idp.unreachable", Codes(Prod(), idp: new IdpProbeResult(false, "connection refused", null, false, [], false, [], true)));
        Assert.Contains("idp.alg", Codes(Prod(), idp: new IdpProbeResult(true, null, "https://idp.example.mil/realms/keel", true, [], true, ["HS256"], true)));
    }

    [Fact]
    public void The_discovery_document_is_parsed_as_the_idp_publishes_it()
    {
        using var doc = JsonDocument.Parse("""
            {"issuer":"https://idp.example.mil/realms/keel/","acr_values_supported":["0","1","2"],"code_challenge_methods_supported":["plain","S256"],"id_token_signing_alg_values_supported":["RS256","ES256"]}
            """);
        var r = IdpProbe.Parse(doc.RootElement, "https://idp.example.mil/realms/keel", true);
        Assert.True(r.Reached && r.IssuerMatches && r.Pkce);
        Assert.Equal(["0", "1", "2"], r.AcrValuesSupported);
        using var bare = JsonDocument.Parse("""{"issuer":"https://other.example.mil/realms/keel"}""");
        var b = IdpProbe.Parse(bare.RootElement, "https://idp.example.mil/realms/keel", true);
        Assert.False(b.IssuerMatches);
        Assert.False(b.Pkce);
        Assert.Empty(b.AcrValuesSupported);
    }

    private static ClaimsPrincipal Admin(params Claim[] extra) => new(new ClaimsIdentity(
        [new Claim("sub", "u-1"), new Claim("preferred_username", "ada"), new Claim("roles", Roles.Admin), .. extra], "test", "preferred_username", "roles"));

    [Fact]
    public void Your_own_session_shows_levels_and_which_security_claims_arrived()
    {
        var user = Admin(new Claim("acr", "1"), new Claim("amr", "[\"pwd\"]"), new Claim("auth_time", "1793371058"), new Claim("us_person", "true"), new Claim("org", "ENG"));
        var obs = SignInAssessor.Observe(user);
        Assert.Equal("1", obs.Acr);
        Assert.Equal(["pwd"], obs.Amr);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793371058), obs.AuthTime);
        Assert.Equal(["us_person", "org"], obs.ClaimsPresent);
        Assert.Contains("citizenship", obs.ClaimsMissing);
        var codes = Codes(Prod(), obs);
        Assert.Contains("session.acr", codes);
        Assert.Contains("session.password", codes);
        Assert.Contains("claims.missing", codes);
        Assert.Contains(SignInAssessor.Assess(Prod(), SignInAssessor.Observe(Admin(new Claim("acr", "2")))), f => f.Code == "session.acr" && f.Message.Contains("carries the level required", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the acr requirement for e-signatures

    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static Subject Who(string? acr, int ageSeconds = 10) => new() { Id = "u", DisplayName = "U", AuthTime = Now.AddSeconds(-ageSeconds), Acr = acr };

    [Fact]
    public void A_required_level_must_be_the_level_of_the_signing_sign_in()
    {
        var window = TimeSpan.FromSeconds(300);
        Assert.True(StepUp.IsFresh(Who("1"), Now, window, null));      // nothing required: any fresh sign-in (A9)
        Assert.True(StepUp.IsFresh(Who("1"), Now, window, ""));
        Assert.True(StepUp.IsFresh(Who("2"), Now, window, "2"));
        Assert.False(StepUp.IsFresh(Who("1"), Now, window, "2"));      // a password sign-in a moment ago is fresh but not enough
        Assert.False(StepUp.IsFresh(Who(null), Now, window, "2"));     // an IdP that releases no acr cannot satisfy a required one
        Assert.False(StepUp.IsFresh(Who("2", 301), Now, window, "2")); // the level does not excuse an old sign-in
        Assert.False(StepUp.IsFresh(Who("22"), Now, window, "2"));     // exact match, not a prefix
    }

    private static ClaimsPrincipal Principal(string sub, params Claim[] claims) => new(new ClaimsIdentity([new Claim("sub", sub), new Claim("auth_time", "1793371058"), .. claims], "test"));

    [Fact]
    public void A_step_up_that_returns_another_level_is_refused()
    {
        var before = Principal("u");
        Assert.Equal(StepUp.Verdict.Accepted, StepUp.Check(before, Principal("u", new Claim("acr", "2")), "2"));
        Assert.Equal(StepUp.Verdict.AcrNotSatisfied, StepUp.Check(before, Principal("u", new Claim("acr", "1")), "2"));
        Assert.Equal(StepUp.Verdict.AcrNotSatisfied, StepUp.Check(before, Principal("u"), "2"));
        Assert.Equal(StepUp.Verdict.Accepted, StepUp.Check(before, Principal("u", new Claim("acr", "1")), null));
        Assert.Equal(StepUp.Verdict.SubjectMismatch, StepUp.Check(before, Principal("v", new Claim("acr", "2")), "2"));
        Assert.Equal(StepUp.Verdict.NoPriorSession, StepUp.Check(null, Principal("u", new Claim("acr", "2")), "2"));
    }

    [Fact]
    public void The_acr_claim_reaches_the_subject()
    {
        Assert.Equal("2", SubjectClaims.FromPrincipal(Principal("u", new Claim("acr", "2")))!.Acr);
        Assert.Null(SubjectClaims.FromPrincipal(Principal("u"))!.Acr);
    }

    [Fact]
    public void The_methods_are_matched_case_insensitively_and_nothing_else_passes()
    {
        Assert.Equal("Certificate", SignInMethods.Normalize(" certificate "));
        Assert.Equal("Federated", SignInMethods.Normalize("FEDERATED"));
        Assert.Null(SignInMethods.Normalize("Kerberos"));
        Assert.Null(SignInMethods.Normalize(null));
    }

    // ---------------------------------------------------------------- the page

    [Fact]
    public void The_page_is_read_only_and_shows_the_configuration_the_warnings_and_the_session()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = AddAuthorization();
        auth.SetAuthorized("ada");
        auth.SetClaims(Admin(new Claim("acr", "1"), new Claim("us_person", "true")).Claims.ToArray());
        Services.AddSingleton(Prod(s => s with { Method = SignInMethods.Password }));
        Services.AddScoped<CurrentUser>();
        Services.AddSingleton(new IdpProbe(new NoHttp(), Prod()));
        var cut = Render<SignInPage>();
        Assert.Equal("Administration: sign-in", cut.Find("h1").TextContent);
        Assert.Contains("password.production", cut.Find("[data-testid=signin-findings]").InnerHtml, StringComparison.Ordinal);
        Assert.Contains("Keel:Auth:SignIn:Method", cut.Find("[data-testid=signin-settings]").TextContent, StringComparison.Ordinal);
        Assert.Contains("acr 2", cut.Find("[data-testid=signin-findings]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("citizenship=", cut.Markup, StringComparison.Ordinal);
        // read-only: the only control is the discovery check, and there is no form
        Assert.Empty(cut.FindAll("form"));
        Assert.Empty(cut.FindAll("input"));
        Assert.Single(cut.FindAll("main button, .stack button"));
        Assert.DoesNotContain("secret", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_discovery_check_shows_what_the_idp_answered_and_a_failure_as_a_message()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = AddAuthorization();
        auth.SetAuthorized("ada");
        auth.SetClaims(Admin().Claims.ToArray());
        Services.AddSingleton(Prod());
        Services.AddScoped<CurrentUser>();
        Services.AddSingleton(new IdpProbe(new NoHttp(), Prod())); // nothing answers
        var cut = Render<SignInPage>();
        cut.Find("[data-testid=signin-probe]").Click();
        await cut.WaitForAssertionAsync(() => Assert.Contains("Reached", cut.Find("[data-testid=signin-idp]").TextContent, StringComparison.Ordinal));
        Assert.Contains("No:", cut.Find("[data-testid=signin-idp]").TextContent, StringComparison.Ordinal);
        Assert.Contains("idp.unreachable", cut.Find("[data-testid=signin-findings]").InnerHtml, StringComparison.Ordinal);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Refuse());
        private sealed class Refuse : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("connection refused");
        }
    }
}
