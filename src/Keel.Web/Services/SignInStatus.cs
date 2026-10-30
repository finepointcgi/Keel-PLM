using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Keel.Web.Services;

/// <summary>
/// What the host was started with that decides how people sign in (ADR-0058). Built once by the host from its options and the
/// environment; the Sign-in status page shows it and <see cref="SignInAssessor"/> judges it. Never contains a secret.
/// </summary>
public sealed record SignInSettings
{
    public string Environment { get; init; } = "";
    public bool Demo { get; init; }
    public string Authority { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string Audience { get; init; } = "";
    public bool RequireHttpsMetadata { get; init; } = true;
    /// <summary><c>Password</c>, <c>Certificate</c> or <c>Federated</c>: what the operator states the IdP is set up for (Keel:Auth:SignIn:Method).</summary>
    public string Method { get; init; } = SignInMethods.Password;
    public bool PasswordFallback { get; init; }
    /// <summary>The <c>acr</c> an e-signature needs (Keel:Auth:StepUpAcr); empty = any fresh authentication.</summary>
    public string StepUpAcr { get; init; } = "";
    public int StepUpMaxAgeSeconds { get; init; } = 300;
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(8);
    public double IdleLockMinutes { get; init; }
    public bool RequireFips { get; init; }
    public bool FipsActive { get; init; }
    public bool ProxyEnabled { get; init; }
    public bool SecureCookies { get; init; }

    public bool Production => string.Equals(Environment, Environments.Production, StringComparison.OrdinalIgnoreCase);
}

public static class SignInMethods
{
    public const string Password = "Password";
    public const string Certificate = "Certificate";
    public const string Federated = "Federated";
    public static readonly IReadOnlyList<string> All = [Password, Certificate, Federated];

    /// <summary>The canonical spelling of <paramref name="value"/>, or null when it is none of the three (the host refuses to start on that).</summary>
    public static string? Normalize(string? value) => All.FirstOrDefault(m => string.Equals(m, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public enum SignInSeverity { Info, Warning, Critical }

public sealed record SignInFinding(SignInSeverity Severity, string Code, string Message);

/// <summary>One line of the effective configuration: the value, and the setting that controls it.</summary>
public sealed record SignInSetting(string Name, string Value, string Key);

/// <summary>What the identity provider says about itself at its OIDC discovery address. Only the configured authority is ever contacted.</summary>
public sealed record IdpProbeResult(bool Reached, string? Error, string? Issuer, bool IssuerMatches, IReadOnlyList<string> AcrValuesSupported,
    bool Pkce, IReadOnlyList<string> SigningAlgorithms, bool Https);

/// <summary>What the signed-in administrator's own session shows (claim names and levels, never claim values of marked attributes).</summary>
public sealed record SessionObservation(string? Acr, IReadOnlyList<string> Amr, DateTimeOffset? AuthTime, IReadOnlyList<string> ClaimsPresent, IReadOnlyList<string> ClaimsMissing);

public sealed record SignInReport(IReadOnlyList<SignInSetting> Settings, IReadOnlyList<SignInFinding> Findings, SessionObservation Session);

/// <summary>
/// Reads the effective sign-in configuration and the warnings that follow from it. Pure: the same inputs always give the same report, so
/// every rule is unit-tested. It judges what Keel itself can see (its own options, the environment, the IdP's public discovery document and
/// the administrator's own token). It cannot see the IdP's flows or truststore: what it says about those is what the operator declared.
/// </summary>
public static class SignInAssessor
{
    /// <summary>Claims Keel reads for access decisions (A2). Absent ones fail closed (no U.S.-person status, no program, no authorization).</summary>
    public static readonly IReadOnlyList<string> SecurityClaims = ["us_person", "citizenship", "org", "programs", "export_authorizations", "cui_categories"];

    public static SessionObservation Observe(ClaimsPrincipal user)
    {
        var present = SecurityClaims.Where(c => user.HasClaim(x => x.Type == c)).ToList();
        var missing = SecurityClaims.Except(present).ToList();
        var amr = user.FindAll("amr").SelectMany(c => ExpandAmr(c.Value)).Distinct().ToList();
        DateTimeOffset? authTime = long.TryParse(user.FindFirst("auth_time")?.Value, out var s) && s > 0 && s < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
        return new SessionObservation(user.FindFirst("acr")?.Value, amr, authTime, present, missing);
    }

    // The amr claim is a JSON array in a token, but a single claim may also carry the array as text.
    private static string[] ExpandAmr(string value)
    {
        if (value.StartsWith('[')) { try { return JsonSerializer.Deserialize<string[]>(value) ?? []; } catch (JsonException) { } }
        return [value];
    }

    public static IReadOnlyList<SignInSetting> Describe(SignInSettings s) =>
    [
        new("Environment", s.Environment, "ASPNETCORE_ENVIRONMENT"),
        new("Sign-in method (declared)", s.Method, "Keel:Auth:SignIn:Method"),
        new("Password fallback at the IdP (declared)", s.PasswordFallback ? "Yes" : "No", "Keel:Auth:SignIn:PasswordFallback"),
        new("Identity provider (issuer)", s.Authority, "Keel:Auth:Authority"),
        new("Web client", s.ClientId, "Keel:Auth:WebClientId"),
        new("API audience", s.Audience, "Keel:Auth:Audience"),
        new("Require HTTPS for IdP metadata", s.RequireHttpsMetadata ? "Yes" : "No", "Keel:Auth:RequireHttpsMetadata"),
        new("Level required for e-signatures (acr)", s.StepUpAcr.Length == 0 ? "Not set: any fresh sign-in" : s.StepUpAcr, "Keel:Auth:StepUpAcr"),
        new("Fresh sign-in window for e-signatures", $"{s.StepUpMaxAgeSeconds} seconds", "Keel:Workflow:StepUpMaxAgeSeconds"),
        new("Session lifetime", $"{s.SessionLifetime.TotalHours:0.##} hours", "KeelUiOptions.SessionLifetime"),
        new("Idle lock", s.IdleLockMinutes > 0 ? $"{s.IdleLockMinutes:0.##} minutes" : "Off", "Keel:Session:IdleLockMinutes"),
        new("FIPS 140 required", s.RequireFips ? "Yes" : "No", "Keel:Security:RequireFips"),
        new("FIPS mode active in the OS", s.FipsActive ? "Yes" : "No", "(detected)"),
        new("Behind a TLS reverse proxy", s.ProxyEnabled ? "Yes" : "No", "Keel:Proxy:Enabled"),
        new("Secure session cookie", s.SecureCookies ? "Yes" : "No", "(follows the environment)"),
        new("Demo sign-in", s.Demo ? "ON" : "Off", "Keel:Demo:Enabled"),
    ];

    public static IReadOnlyList<SignInFinding> Assess(SignInSettings s, SessionObservation? session = null, IdpProbeResult? idp = null)
    {
        var f = new List<SignInFinding>();
        void Add(SignInSeverity sev, string code, string text) => f.Add(new SignInFinding(sev, code, text));

        if (s.Demo)
            Add(SignInSeverity.Critical, "demo.enabled", "Demo sign-in is on: anyone can sign in as any demo user without a password. It must never run against real data.");

        if (SignInMethods.Normalize(s.Method) is null)
            Add(SignInSeverity.Critical, "method.invalid", $"Keel:Auth:SignIn:Method is '{s.Method}'; it must be Password, Certificate or Federated.");
        else if (s.Method.Equals(SignInMethods.Password, StringComparison.OrdinalIgnoreCase) && !s.Demo)
        {
            if (s.Production)
                Add(SignInSeverity.Warning, "password.production", "Password sign-in is the declared method in a Production profile. People sign in with a username and password at the identity provider. Set up CAC/PIV (docs/ops/cac-piv-sign-in.md) or your site single sign-on (docs/ops/sso-integration.md) and then set Keel:Auth:SignIn:Method.");
            else
                Add(SignInSeverity.Info, "password.development", "Password sign-in is the declared method. That is expected for the development and demo stacks; do not use it in production.");
        }

        if (s.PasswordFallback && !s.Demo)
        {
            Add(s.Production ? SignInSeverity.Warning : SignInSeverity.Info, "password.fallback",
                "A password fallback is declared next to the " + s.Method.ToLowerInvariant() + " sign-in. Anyone whose certificate or federated sign-in fails can use a password, which is weaker than the primary method; keep it for break-glass accounts or turn it off.");
            if (s.StepUpAcr.Length == 0)
                Add(SignInSeverity.Warning, "stepup.password-can-sign", "A password fallback exists and Keel:Auth:StepUpAcr is not set, so a password sign-in made in the last " + s.StepUpMaxAgeSeconds + " seconds can sign. Set Keel:Auth:StepUpAcr to the certificate level (\"2\" in the shipped CAC/PIV flow).");
        }
        if (s.Method.Equals(SignInMethods.Certificate, StringComparison.OrdinalIgnoreCase) && s.StepUpAcr.Length == 0 && !s.PasswordFallback)
            Add(SignInSeverity.Info, "stepup.acr.unset", "E-signatures accept any fresh sign-in. With a certificate-only IdP that is a fresh certificate sign-in; setting Keel:Auth:StepUpAcr would make Keel check that explicitly.");

        if (string.IsNullOrWhiteSpace(s.Authority))
            Add(SignInSeverity.Critical, "authority.missing", "No identity provider is configured (Keel:Auth:Authority).");
        else if (s.Production)
        {
            if (!s.Authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                Add(SignInSeverity.Critical, "authority.http", "The identity provider address is not https:// in a Production profile; tokens and the sign-in redirect would travel unprotected.");
            if (!s.RequireHttpsMetadata)
                Add(SignInSeverity.Critical, "metadata.http", "Keel:Auth:RequireHttpsMetadata is off in a Production profile.");
            if (!s.SecureCookies)
                Add(SignInSeverity.Warning, "cookie.insecure", "The session cookie is not marked Secure.");
        }
        if (s.Production && !s.RequireFips)
            Add(SignInSeverity.Warning, "fips.not-required", "Keel:Security:RequireFips is off in a Production profile; sign-in does not depend on it, but the platform's cryptography should be FIPS-validated.");
        if (s.StepUpMaxAgeSeconds > 300)
            Add(SignInSeverity.Warning, "stepup.window", $"The fresh sign-in window for e-signatures is {s.StepUpMaxAgeSeconds} seconds (the default is 300).");
        if (s.Production && s.IdleLockMinutes <= 0)
            Add(SignInSeverity.Warning, "idle.off", "The idle session lock is off.");

        if (idp is not null)
        {
            if (!idp.Reached)
                Add(SignInSeverity.Warning, "idp.unreachable", "The identity provider's discovery document could not be read: " + (idp.Error ?? "no answer") + ".");
            else
            {
                if (!idp.IssuerMatches)
                    Add(SignInSeverity.Critical, "idp.issuer", $"The discovery document's issuer is '{idp.Issuer}', which is not the configured authority; Keel will reject its tokens.");
                if (!idp.Pkce)
                    Add(SignInSeverity.Warning, "idp.pkce", "The identity provider does not advertise PKCE with S256, which Keel uses for every sign-in.");
                if (s.StepUpAcr.Length > 0 && idp.AcrValuesSupported.Count > 0 && !idp.AcrValuesSupported.Contains(s.StepUpAcr))
                    Add(SignInSeverity.Warning, "idp.acr", $"The identity provider advertises acr values {string.Join(", ", idp.AcrValuesSupported)} but not '{s.StepUpAcr}', the level Keel requires for e-signatures.");
                else if (s.StepUpAcr.Length > 0 && idp.AcrValuesSupported.Count == 0)
                    Add(SignInSeverity.Info, "idp.acr.not-advertised", "The identity provider does not list the acr values it supports; whether it can return the required level is shown only by a real e-signature step-up (see the verification checklist).");
                if (idp.SigningAlgorithms.Count > 0 && idp.SigningAlgorithms.All(a => a.Equals("none", StringComparison.OrdinalIgnoreCase) || a.StartsWith("HS", StringComparison.OrdinalIgnoreCase)))
                    Add(SignInSeverity.Critical, "idp.alg", "The identity provider signs ID tokens only with symmetric or no algorithms.");
            }
        }

        if (session is not null)
        {
            if (s.StepUpAcr.Length > 0)
                Add(SignInSeverity.Info, "session.acr", string.Equals(session.Acr, s.StepUpAcr, StringComparison.Ordinal)
                    ? $"Your current sign-in carries the level required for e-signatures (acr {session.Acr})."
                    : $"Your current sign-in carries acr {(session.Acr ?? "(none)")}; an e-signature will ask for a fresh sign-in at acr {s.StepUpAcr}.");
            if (session.Amr.Any(a => a.Equals("pwd", StringComparison.OrdinalIgnoreCase)) && !s.Method.Equals(SignInMethods.Password, StringComparison.OrdinalIgnoreCase))
                Add(SignInSeverity.Warning, "session.password", "Your own session was authenticated with a password (amr pwd) although the declared method is " + s.Method + ".");
            if (session.ClaimsMissing.Count > 0)
                Add(SignInSeverity.Info, "claims.missing", "Your token does not carry: " + string.Join(", ", session.ClaimsMissing) + ". Keel treats an absent claim as the most restrictive value (no U.S.-person status, no program, no authorization). Check the IdP mapping if that is not intended (docs/ops/sso-integration.md).");
        }
        return f.OrderByDescending(x => x.Severity).ThenBy(x => x.Code, StringComparer.Ordinal).ToList();
    }

    public static SignInReport Report(SignInSettings s, ClaimsPrincipal user, IdpProbeResult? idp = null)
    {
        var session = Observe(user);
        return new SignInReport(Describe(s), Assess(s, session, idp), session);
    }
}

/// <summary>Reads the OIDC discovery document of the configured authority, and only that address (nothing a user typed is ever fetched).</summary>
public sealed class IdpProbe(IHttpClientFactory http, SignInSettings settings)
{
    public async Task<IdpProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        var authority = settings.Authority.TrimEnd('/');
        var https = authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var client = http.CreateClient("keel-idp-probe");
            using var response = await client.GetAsync(authority + "/.well-known/openid-configuration", cts.Token);
            if (!response.IsSuccessStatusCode) return new IdpProbeResult(false, $"HTTP {(int)response.StatusCode}", null, false, [], false, [], https);
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
            return Parse(doc.RootElement, authority, https);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException)
        {
            return new IdpProbeResult(false, e is TaskCanceledException or OperationCanceledException ? "no answer within 5 seconds" : e.Message, null, false, [], false, [], https);
        }
    }

    public static IdpProbeResult Parse(JsonElement root, string authority, bool https)
    {
        static IReadOnlyList<string> Strings(JsonElement root, string name) =>
            root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Array ? [.. e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)] : [];
        var issuer = root.TryGetProperty("issuer", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
        return new IdpProbeResult(true, null, issuer, string.Equals(issuer?.TrimEnd('/'), authority.TrimEnd('/'), StringComparison.Ordinal),
            Strings(root, "acr_values_supported"), Strings(root, "code_challenge_methods_supported").Contains("S256"),
            Strings(root, "id_token_signing_alg_values_supported"), https);
    }
}

public static class SignInStatusRegistration
{
    /// <summary>Registers the settings the Sign-in status page shows and the discovery probe behind its button.</summary>
    public static IServiceCollection AddKeelSignInStatus(this IServiceCollection services, SignInSettings settings)
    {
        services.AddSingleton(settings);
        services.AddHttpClient("keel-idp-probe");
        services.AddSingleton<IdpProbe>();
        return services;
    }
}
