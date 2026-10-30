using System.Text.Json.Nodes;

namespace Keel.IntegrationTests.Idp;

/// <summary>
/// The OIDC broker overlay (deploy/compose/keycloak/overlays/idp-broker) against a real second Keycloak acting as a customer IdP (ADR-0058): claim, group and
/// attribute mapping into Keel's roles and ABAC attributes, just-in-time user creation, and what must NOT happen (no grant for the unmapped, no account
/// takeover by matching e-mail). SAML is covered only by the import and metadata checks below, not by a live SAML IdP.
/// </summary>
public sealed class OidcBrokerTests(KeycloakBrokerFixture kc) : IClassFixture<KeycloakBrokerFixture>
{
    private const string Hint = "&kc_idp_hint=customer-oidc";

    private static string[] Strings(JsonNode? n) => n is JsonArray a ? [.. a.Select(x => x!.GetValue<string>())] : n is null ? [] : [n.GetValue<string>()];

    [Fact]
    public async Task A_customer_user_is_created_just_in_time_with_the_roles_and_attributes_the_customer_asserts()
    {
        using var browser = kc.Browser();
        var result = await browser.SignInAsync(Hint, ("jane", "jane-upstream-pw"));
        Assert.True(result.SignedIn, (result.TransportError ?? result.Page[^Math.Min(400, result.Page.Length)..]) + "\n" + browser.Trace);
        var claims = await browser.IdTokenAsync(result);
        Assert.Equal("jane", claims["preferred_username"]!.GetValue<string>());
        var roles = Strings(claims["roles"]);
        Assert.Contains("plm-author", roles);
        Assert.Contains("plm-approver", roles);
        Assert.DoesNotContain("plm-admin", roles);
        Assert.DoesNotContain("plm-viewer", roles); // only the mapped groups
        // ABAC attributes come from the customer's claims (Keel:A2): U.S.-person status is an explicit assertion, never inferred.
        Assert.True(claims["us_person"]!.GetValue<bool>());
        Assert.Equal("USA", claims["citizenship"]!.GetValue<string>());
        Assert.Equal("ENG", claims["org"]!.GetValue<string>());
        Assert.Equal(["FALCON", "ORCA"], Strings(claims["programs"]).Order().ToArray());
    }

    [Fact]
    public async Task A_customer_user_in_no_mapped_group_gets_no_plm_role_and_no_security_attributes()
    {
        using var browser = kc.Browser();
        var result = await browser.SignInAsync(Hint, ("eve", "eve-upstream-pw"));
        Assert.True(result.SignedIn, (result.TransportError ?? result.Page[^Math.Min(400, result.Page.Length)..]) + "\n" + browser.Trace); // Keycloak creates the user; Keel then denies it (no plm role)
        var claims = await browser.IdTokenAsync(result);
        Assert.DoesNotContain(Strings(claims["roles"]), r => r.StartsWith("plm-", StringComparison.Ordinal));
        Assert.Null(claims["us_person"]);   // absent: Keel treats it as "not a U.S. person"
        Assert.Null(claims["programs"]);
    }

    [Fact]
    public async Task A_customer_user_whose_email_matches_a_local_account_is_not_silently_linked_to_it()
    {
        using var browser = kc.Browser();
        var result = await browser.SignInAsync(Hint, ("alicia", "alicia-upstream-pw")); // alicia@... is alice@keel.test, a local user, and claims plm-admin
        Assert.False(result.SignedIn, "an upstream account must not take over the local account that shares its e-mail address");
        // The reason is Keycloak's "account already exists" step (it wants the local account's own credentials), not some unrelated failure.
        Assert.Contains("already exists", result.Page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_saml_template_imports_into_the_realm_and_the_realm_publishes_service_provider_metadata()
    {
        // Keycloak validated the merged realm (OIDC and SAML templates) on import. No SAML IdP exists in this repository's tests, so a SAML SIGN-IN is not exercised.
        var (status, xml) = await kc.GetKeelAsync("/realms/keel/broker/customer-saml/endpoint/descriptor");
        Assert.Equal(200, status);
        Assert.Contains("EntityDescriptor", xml, StringComparison.Ordinal);
        Assert.Contains("https://keel.example.test/saml", xml, StringComparison.Ordinal);
        Assert.Contains("AssertionConsumerService", xml, StringComparison.Ordinal);
    }
}
