using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;

namespace Keel.IntegrationTests.Idp;

/// <summary>What a scripted browser ended up with at the identity provider.</summary>
internal sealed record SignInResult(string? Code, int Status, string Page, string? Feedback, bool PasswordForm, string? TransportError, string Verifier)
{
    public bool SignedIn => Code is not null;
}

/// <summary>
/// A scripted browser for the Keycloak authorization-code flow of the Keel web client (the same client, PKCE and redirect handling the
/// Blazor host uses), optionally presenting an X.509 client certificate at TLS. It stops at the redirect to the Keel callback and
/// exchanges the code itself, so what is checked is what Keycloak would hand Keel: the ID token claims.
/// </summary>
internal sealed class IdpBrowser : IDisposable
{
    public const string RedirectUri = "http://localhost:59999/signin-oidc";
    public const string ClientId = "keel-web";
    public const string ClientSecret = "keel-web-dev-secret";

    private readonly HttpClient _http;
    private readonly HttpClientHandler _handler;
    private readonly string _realmBase;
    private readonly List<string> _trace = [];
    // A tiny cookie jar keyed by host only (browsers send Secure cookies to http://localhost; HttpClient's container does not, and Keycloak marks its
    // session cookies Secure). Paths and expiry are ignored except that a cookie set to empty or expired is dropped.
    private readonly Dictionary<string, Dictionary<string, string>> _jar = [];

    public IdpBrowser(string httpsOrigin, X509Certificate2? clientCertificate, X509Certificate2? trustedCa)
    {
        _realmBase = httpsOrigin + "/realms/keel/protocol/openid-connect";
        _handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        if (clientCertificate is not null)
        {
            _handler.ClientCertificateOptions = ClientCertificateOption.Manual;
            _handler.ClientCertificates.Add(clientCertificate);
        }
        // Trust exactly the test CA for the server certificate (the production truststore is not touched).
        if (trustedCa is not null) _handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
        {
            if (cert is null) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(trustedCa);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(cert);
        };
        _http = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public static X509Certificate2 LoadClient(string pkiDir, string name) =>
        X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(pkiDir, name + ".p12"), "keel-dev-test", X509KeyStorageFlags.Exportable);

    /// <summary>Runs the sign-in. <paramref name="extra"/> is appended to the authorization request, e.g. "&amp;acr_values=2&amp;prompt=login&amp;max_age=0".</summary>
    public async Task<SignInResult> SignInAsync(string extra = "", (string User, string Password)? passwordLogin = null)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var url = _realmBase + "/auth?" + $"client_id={ClientId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&response_type=code&scope=openid"
            + $"&code_challenge={challenge}&code_challenge_method=S256&state=s&nonce=n" + extra;
        try
        {
            for (var hop = 0; hop < 15; hop++)
            {
                using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url));
                Note("GET", url, response);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307)
                {
                    url = new Uri(new Uri(url), response.Headers.Location!).ToString();
                    if (url.StartsWith(RedirectUri, StringComparison.Ordinal)) return Done(url, verifier);
                    continue;
                }
                var html = await response.Content.ReadAsStringAsync();
                if (passwordLogin is { } login && HasPasswordForm(html))
                {
                    var action = HttpUtility.HtmlDecode(Regex.Match(html, "<form[^>]*action=\"([^\"]*)\"").Groups[1].Value);
                    using var post = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(url), action))
                    { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = login.User, ["password"] = login.Password, ["credentialId"] = "" }) };
                    using var posted = await SendAsync(post);
                    Note("POST", post.RequestUri!.ToString(), posted);
                    if ((int)posted.StatusCode is 302 or 303)
                    {
                        url = new Uri(new Uri(url), posted.Headers.Location!).ToString();
                        if (url.StartsWith(RedirectUri, StringComparison.Ordinal)) return Done(url, verifier);
                        continue;
                    }
                    html = await posted.Content.ReadAsStringAsync();
                    return Page((int)posted.StatusCode, html, verifier);
                }
                return Page((int)response.StatusCode, html, verifier);
            }
            return new SignInResult(null, 0, "", "too many redirects", false, null, verifier);
        }
        catch (HttpRequestException e)
        {
            // A client certificate the TLS layer refuses (unknown CA, expired) ends the connection before any page.
            return new SignInResult(null, 0, "", null, false, e.GetBaseException().Message, verifier);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        var host = request.RequestUri!.Host;
        if (_jar.TryGetValue(host, out var cookies) && cookies.Count > 0)
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies.Select(kv => kv.Key + "=" + kv.Value)));
        var response = await _http.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var set))
            foreach (var header in set)
            {
                var parts = header.Split(';');
                var pair = parts[0].Split('=', 2);
                if (pair.Length != 2) continue;
                var jar = _jar.TryGetValue(host, out var h) ? h : _jar[host] = [];
                var expired = pair[1].Length == 0 || parts.Any(p => p.Trim().Equals("Max-Age=0", StringComparison.OrdinalIgnoreCase));
                if (expired) jar.Remove(pair[0]); else jar[pair[0]] = pair[1];
            }
        return response;
    }

    // A short log of the hops, kept for failure messages (cookie names only, never values).
    private void Note(string method, string url, HttpResponseMessage r)
    {
        var uri = new Uri(url);
        var cookies = r.Headers.TryGetValues("Set-Cookie", out var c) ? string.Join(",", c.Select(x => x.Split('=')[0])) : "";
        _trace.Add($"{method} {uri.Host}:{uri.Port}{uri.AbsolutePath} -> {(int)r.StatusCode} set-cookie[{cookies}] sent[{string.Join(",", (_jar.TryGetValue(uri.Host, out var j) ? j.Keys : []))}]");
    }

    /// <summary>The hops of the last sign-in, for assertion messages.</summary>
    public string Trace => string.Join("\n", _trace);

    private static SignInResult Done(string redirect, string verifier)
    {
        var query = HttpUtility.ParseQueryString(new Uri(redirect).Query);
        return new SignInResult(query["code"], 302, "", null, false, null, verifier);
    }

    private static bool HasPasswordForm(string html) => html.Contains("name=\"password\"", StringComparison.Ordinal);

    private static SignInResult Page(int status, string html, string verifier)
    {
        var feedback = Regex.Match(html, "(?:kc-feedback-text|alert-error)[^>]*>(.*?)<", RegexOptions.Singleline);
        return new SignInResult(null, status, Regex.Replace(HttpUtility.HtmlDecode(html), "<[^>]+>", " "), feedback.Success ? HttpUtility.HtmlDecode(feedback.Groups[1].Value).Trim() : null, HasPasswordForm(html), null, verifier);
    }

    /// <summary>Exchanges the authorization code (with the PKCE verifier) and returns the ID token claims.</summary>
    public async Task<JsonObject> IdTokenAsync(SignInResult result)
    {
        Assert.NotNull(result.Code);
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Post, _realmBase + "/token") { Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = result.Code!, ["redirect_uri"] = RedirectUri, ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret, ["code_verifier"] = result.Verifier,
        }) });
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.True(response.IsSuccessStatusCode, body.ToJsonString());
        return Claims(body["id_token"]!.GetValue<string>());
    }

    public static JsonObject Claims(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonNode.Parse(Convert.FromBase64String(payload))!.AsObject();
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() { _http.Dispose(); _handler.Dispose(); }
}
