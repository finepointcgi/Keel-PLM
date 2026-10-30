using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Keel.IntegrationTests.Idp;

/// <summary>
/// Two real Keycloaks: a synthetic "customer IdP" (realm <c>customer</c>, upstream-realm.json) and the Keel realm with the shipped OIDC broker overlay
/// merged in by scripts/idp/merge-realm-overlay.sh. Proves the template end to end for OIDC brokering: roles and ABAC attributes arrive from the
/// customer's claims, and what the customer does not assert is not granted. Throwaway containers, ephemeral ports.
/// </summary>
public sealed class KeycloakBrokerFixture : IAsyncLifetime
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "keel-broker-" + Guid.NewGuid().ToString("N")[..8]);
    private IContainer? _upstream, _keel;

    internal string KeelOrigin { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        var repo = FindRepoRoot();
        Directory.CreateDirectory(_work);
        var upPort = FreePort();
        var upstreamOrigin = $"http://127.0.0.1:{upPort}"; // a different host name from "localhost": the two Keycloaks must not share cookies

        _upstream = new ContainerBuilder("quay.io/keycloak/keycloak:26.4")
            .WithCommand("start-dev", "--import-realm", $"--hostname={upstreamOrigin}", "--hostname-backchannel-dynamic=true")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin").WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", KeycloakCacFixture.AdminPassword)
            .WithResourceMapping(await File.ReadAllBytesAsync(Path.Combine(repo, "tests/Keel.IntegrationTests/Idp/upstream-realm.json")), "/opt/keycloak/data/import/customer-realm.json") // Keycloak insists the file is named after the realm
            .WithPortBinding(upPort, 8080)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/realms/customer/.well-known/openid-configuration")))
            .Build();
        var upstreamStart = _upstream.StartAsync();

        // The Keel realm: the shipped dev realm (with this run's redirect URI), plus the OIDC broker overlay with the customer's values.
        var realm = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(repo, "deploy/compose/keycloak/keel-realm.json")))!;
        var client = realm["clients"]!.AsArray().Single(c => c!["clientId"]!.GetValue<string>() == "keel-web")!;
        client["redirectUris"] = new JsonArray(IdpBrowser.RedirectUri);
        client["webOrigins"] = new JsonArray("http://localhost:59999");
        var baseFile = Path.Combine(_work, "base-realm.json");
        await File.WriteAllTextAsync(baseFile, realm.ToJsonString());
        var secretFile = Path.Combine(_work, "client-secret");
        await File.WriteAllTextAsync(secretFile, "upstream-secret");
        var merged = Path.Combine(_work, "keel-realm.json");
        var openid = $"/realms/customer/protocol/openid-connect";
        var args = new List<string>
        {
            Path.Combine(repo, "scripts/idp/merge-realm-overlay.sh"), baseFile, Path.Combine(repo, "deploy/compose/keycloak/overlays/idp-broker/oidc-broker.overlay.json"),
            "DISPLAY_NAME=Customer test IdP", $"ISSUER={upstreamOrigin}/realms/customer",
            $"AUTHORIZATION_URL={upstreamOrigin}{openid}/auth",
            $"TOKEN_URL=http://host.docker.internal:{upPort}{openid}/token", $"JWKS_URL=http://host.docker.internal:{upPort}{openid}/certs",
            "CLIENT_ID=keel-broker", "CLIENT_SECRET=@" + secretFile,
            "GROUP_VIEWER=PLM-Viewers", "GROUP_AUTHOR=PLM-Authors", "GROUP_APPROVER=PLM-Approvers", "GROUP_ADMIN=PLM-Admins", "GROUP_AUDITOR=PLM-Auditors",
        };
        await MergeAsync(args, merged);
        // The SAML template goes into the same realm (second merge): Keycloak validates it on import and then serves the service provider metadata.
        var samlMerged = Path.Combine(_work, "keel-realm-saml.json");
        await MergeAsync([Path.Combine(repo, "scripts/idp/merge-realm-overlay.sh"), merged, Path.Combine(repo, "deploy/compose/keycloak/overlays/idp-broker/saml-broker.overlay.json"),
            "DISPLAY_NAME=Customer SAML test IdP", "SP_ENTITY_ID=https://keel.example.test/saml", "IDP_ENTITY_ID=https://adfs.example.test/adfs/services/trust",
            "SSO_URL=https://adfs.example.test/adfs/ls/", "SLO_URL=https://adfs.example.test/adfs/ls/?wa=wsignout1.0", "SIGNING_CERTIFICATE=" + TestCertificateBase64(),
            "GROUP_VIEWER=PLM-Viewers", "GROUP_AUTHOR=PLM-Authors", "GROUP_APPROVER=PLM-Approvers", "GROUP_ADMIN=PLM-Admins", "GROUP_AUDITOR=PLM-Auditors"], samlMerged);
        File.Move(samlMerged, merged, overwrite: true);

        _keel = new ContainerBuilder("quay.io/keycloak/keycloak:26.4")
            .WithCommand("start-dev", "--import-realm")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin").WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", KeycloakCacFixture.AdminPassword)
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithResourceMapping(new FileInfo(merged), "/opt/keycloak/data/import/")
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/realms/keel/.well-known/openid-configuration")))
            .Build();
        await Task.WhenAll(upstreamStart, _keel.StartAsync());
        KeelOrigin = $"http://localhost:{_keel.GetMappedPublicPort(8080)}";
    }

    private static async Task MergeAsync(IEnumerable<string> args, string outputFile)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var err = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, "merge-realm-overlay.sh failed: " + await err);
        await File.WriteAllTextAsync(outputFile, await output);
    }

    // A real (self-signed, throwaway) certificate so Keycloak can parse the "signing certificate" setting of the SAML template.
    private static string TestCertificateBase64()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=test saml idp", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return Convert.ToBase64String(cert.RawData);
    }

    internal async Task<(int Status, string Body)> GetKeelAsync(string path)
    {
        using var http = new HttpClient();
        using var r = await http.GetAsync(KeelOrigin + path);
        return ((int)r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    internal IdpBrowser Browser() => new(KeelOrigin, null, null);

    internal async Task<string> KeelLogsAsync() { var (o, e) = await _keel!.GetLogsAsync(); return o + e; }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_keel is not null) await _keel.DisposeAsync();
        if (_upstream is not null) await _upstream.DisposeAsync();
        try { Directory.Delete(_work, true); } catch (IOException) { }
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Keel.slnx) not found.");
    }
}
