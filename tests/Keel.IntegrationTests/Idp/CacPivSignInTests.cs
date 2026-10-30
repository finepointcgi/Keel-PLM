using System.Text.Json.Nodes;

namespace Keel.IntegrationTests.Idp;

/// <summary>
/// CAC/PIV sign-in through a REAL Keycloak configured by the shipped overlay (ADR-0058), with a generated TEST PKI: the certificate maps to the right Keel
/// user, and unknown, expired, revoked, untrusted or missing certificates are refused. This proves the configuration and the mapping, NOT real cards,
/// middleware, a DoD PKI or a customer's CRL/OCSP infrastructure (see docs/ops/cac-piv-sign-in.md).
/// </summary>
public sealed class CacPivSignInTests(KeycloakCertificateOnlyFixture kc) : IClassFixture<KeycloakCertificateOnlyFixture>
{
    private const string AliceSub = "d0f8df8c-51d0-5241-8c4f-f39864b95d21";

    private static string[] Roles(JsonObject claims) => [.. claims["roles"]!.AsArray().Select(r => r!.GetValue<string>())];

    [Fact]
    public async Task A_valid_certificate_signs_in_as_the_user_whose_edipi_it_carries()
    {
        using var browser = kc.Browser("alice");
        var result = await browser.SignInAsync();
        Assert.True(result.SignedIn, result.TransportError ?? result.Page);
        var claims = await browser.IdTokenAsync(result);
        Assert.Equal("alice", claims["preferred_username"]!.GetValue<string>());
        Assert.Equal(AliceSub, claims["sub"]!.GetValue<string>());
        // The same Keycloak user as a password login would be: roles and the ABAC attributes come with it, nothing from the certificate.
        Assert.Contains("plm-author", Roles(claims));
        Assert.True(claims["us_person"]!.GetValue<bool>());
        Assert.Equal("USA", claims["citizenship"]!.GetValue<string>());
        Assert.NotNull(claims["auth_time"]);
        Assert.Equal("1", claims["acr"]!.GetValue<string>());
    }

    [Fact]
    public async Task Another_certificate_signs_in_as_another_user()
    {
        using var browser = kc.Browser("bob");
        var result = await browser.SignInAsync();
        Assert.True(result.SignedIn, result.TransportError ?? result.Page);
        var claims = await browser.IdTokenAsync(result);
        Assert.Equal("bob", claims["preferred_username"]!.GetValue<string>());
        Assert.NotEqual(AliceSub, claims["sub"]!.GetValue<string>());
        Assert.Contains("plm-approver", Roles(claims));
    }

    [Fact]
    public async Task A_trusted_certificate_whose_edipi_belongs_to_nobody_is_refused()
    {
        using var browser = kc.Browser("unknown");
        var result = await browser.SignInAsync();
        Assert.False(result.SignedIn);
        Assert.False(result.PasswordForm); // certificate only: no password form to fall back to
        Assert.Contains("user_not_found", await kc.LogsAsync(), StringComparison.Ordinal);
        Assert.Contains("1999999999", await kc.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_certificate_is_refused()
    {
        using var browser = kc.Browser("expired");
        var result = await browser.SignInAsync();
        Assert.False(result.SignedIn); // refused already at the TLS handshake or by the authenticator: either way no code
        Assert.True(result.TransportError is not null || result.Status >= 400, "expected a TLS refusal or an error page");
        Assert.False(result.PasswordForm);
    }

    [Fact]
    public async Task A_certificate_listed_in_the_crl_is_refused_even_though_its_dates_and_identity_are_good()
    {
        using var browser = kc.Browser("revoked");
        var result = await browser.SignInAsync();
        Assert.False(result.SignedIn);
        Assert.False(result.PasswordForm);
        // The revoked certificate carries alice's EDIPI and is inside its dates: only the CRL can be the reason, and Keycloak says so.
        Assert.Contains("Certificate has been revoked", await kc.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_certificate_from_a_ca_the_truststore_does_not_hold_is_refused()
    {
        using var browser = kc.Browser("untrusted");
        var result = await browser.SignInAsync();
        Assert.False(result.SignedIn);
        Assert.False(result.PasswordForm);
    }

    [Fact]
    public async Task Without_a_certificate_there_is_no_way_in()
    {
        using var browser = kc.Browser(null);
        var result = await browser.SignInAsync();
        Assert.False(result.SignedIn);
        Assert.False(result.PasswordForm, "a certificate-only realm must not offer a password form");
    }

    [Fact]
    public async Task The_step_up_for_an_e_signature_returns_a_fresh_authentication_at_the_certificate_level()
    {
        using var browser = kc.Browser("alice");
        var first = await browser.SignInAsync();
        var firstClaims = await browser.IdTokenAsync(first);
        Assert.Equal("1", firstClaims["acr"]!.GetValue<string>());
        await Task.Delay(TimeSpan.FromSeconds(2)); // auth_time has a resolution of one second

        // What the Keel host sends for a step-up: prompt=login, max_age=0 and (Keel:Auth:StepUpAcr=2) acr_values=2.
        var stepUp = await browser.SignInAsync("&prompt=login&max_age=0&acr_values=2");
        Assert.True(stepUp.SignedIn, stepUp.TransportError ?? stepUp.Page);
        var claims = await browser.IdTokenAsync(stepUp);
        Assert.Equal("alice", claims["preferred_username"]!.GetValue<string>());
        Assert.Equal(firstClaims["sub"]!.GetValue<string>(), claims["sub"]!.GetValue<string>()); // the same person, which Keel checks
        Assert.Equal("2", claims["acr"]!.GetValue<string>());
        Assert.True(claims["auth_time"]!.GetValue<long>() > firstClaims["auth_time"]!.GetValue<long>(), "the step-up must be a new authentication, not the old session");
    }

    [Fact]
    public async Task The_overlay_script_is_repeatable_and_the_credential_script_validates_its_input()
    {
        Assert.Equal(0, (await kc.ExecAsync("bash", "/overlay/configure-cac-piv.sh")).ExitCode);
        Assert.NotEqual(0, (await kc.ExecAsync("bash", "/overlay/map-credential.sh", "alice", "12345")).ExitCode);   // not 10 digits
        Assert.NotEqual(0, (await kc.ExecAsync("bash", "/overlay/map-credential.sh", "no-such-user", "1000000009")).ExitCode);
        var taken = await kc.ExecAsync("bash", "/overlay/map-credential.sh", "carmen", "1000000001"); // alice's EDIPI
        Assert.NotEqual(0, taken.ExitCode);
        Assert.Contains("already assigned", taken.Stderr, StringComparison.Ordinal);
        using var browser = kc.Browser("alice");
        Assert.True((await browser.SignInAsync()).SignedIn); // still works after a second run
    }
}

/// <summary>The weaker policy: a password form beside the certificate. Shows what it does and, above all, what it cannot do (satisfy level 2).</summary>
public sealed class CacPivPasswordFallbackTests(KeycloakPasswordFallbackFixture kc) : IClassFixture<KeycloakPasswordFallbackFixture>
{
    [Fact]
    public async Task A_person_without_a_working_certificate_gets_the_password_form_at_level_one_only()
    {
        using var browser = kc.Browser(null);
        var result = await browser.SignInAsync(passwordLogin: ("alice", "keel-dev-alice")); // the synthetic dev password of the shipped realm
        Assert.True(result.SignedIn, result.TransportError ?? result.Page);
        var claims = await browser.IdTokenAsync(result);
        Assert.Equal("alice", claims["preferred_username"]!.GetValue<string>());
        Assert.Equal("1", claims["acr"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_password_can_never_satisfy_the_certificate_level_an_e_signature_asks_for()
    {
        using var browser = kc.Browser(null);
        var result = await browser.SignInAsync("&prompt=login&max_age=0&acr_values=2", ("alice", "keel-dev-alice"));
        Assert.False(result.SignedIn, "level 2 is the certificate only; a correct password must not be enough");
    }

    [Fact]
    public async Task With_a_certificate_the_same_realm_still_reaches_level_two()
    {
        using var browser = kc.Browser("alice");
        var result = await browser.SignInAsync("&prompt=login&max_age=0&acr_values=2");
        Assert.True(result.SignedIn, result.TransportError ?? result.Page);
        Assert.Equal("2", (await browser.IdTokenAsync(result))["acr"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_refused_certificate_still_offers_the_password_form_in_this_policy()
    {
        using var browser = kc.Browser("unknown");
        var result = await browser.SignInAsync();
        Assert.False(result.SignedIn);
        Assert.True(result.PasswordForm, "this is the weakness of the fallback policy, and the Sign-in page warns about it");
    }
}
