using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Keel.IntegrationTests.Idp;

/// <summary>scripts/idp/merge-realm-overlay.sh: the guards that keep a half-filled template out of a realm (needs bash and jq; no containers).</summary>
public sealed class MergeOverlayScriptTests
{
    private static readonly string Repo = FindRepoRoot();
    private static readonly string Base = Path.Combine(Repo, "deploy/compose/keycloak/keel-realm.json");
    private static readonly string Oidc = Path.Combine(Repo, "deploy/compose/keycloak/overlays/idp-broker/oidc-broker.overlay.json");

    private static readonly string[] AllValues =
    [
        "DISPLAY_NAME=Customer \"quoted\" IdP", "ISSUER=https://idp.example.mil/realms/x", "AUTHORIZATION_URL=https://idp.example.mil/auth", "TOKEN_URL=https://idp.example.mil/token",
        "JWKS_URL=https://idp.example.mil/certs", "CLIENT_ID=keel", "CLIENT_SECRET=s3cr3t\\with\"odd/chars", "GROUP_VIEWER=v", "GROUP_AUTHOR=a", "GROUP_APPROVER=p", "GROUP_ADMIN=d", "GROUP_AUDITOR=u",
    ];

    private static async Task<(int Exit, string Out, string Err)> RunAsync(string baseFile, string overlay, params string[] values)
    {
        var psi = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { Path.Combine(Repo, "scripts/idp/merge-realm-overlay.sh"), baseFile, overlay }.Concat(values)) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await o, await e);
    }

    [Fact]
    public async Task A_filled_template_merges_with_odd_characters_intact_and_leaves_the_base_users_and_clients()
    {
        var (exit, output, err) = await RunAsync(Base, Oidc, AllValues);
        Assert.True(exit == 0, err);
        var realm = JsonNode.Parse(output)!;
        var idp = realm["identityProviders"]!.AsArray().Single()!;
        Assert.Equal("customer-oidc", idp["alias"]!.GetValue<string>());
        Assert.Equal("Customer \"quoted\" IdP", idp["displayName"]!.GetValue<string>());
        Assert.Equal("s3cr3t\\with\"odd/chars", idp["config"]!["clientSecret"]!.GetValue<string>());
        Assert.Equal("https://idp.example.mil/auth", idp["config"]!["authorizationUrl"]!.GetValue<string>());
        Assert.Equal(11, realm["identityProviderMappers"]!.AsArray().Count);
        Assert.Null(realm["_comment"]);
        var original = JsonNode.Parse(File.ReadAllText(Base))!;
        Assert.Equal(original["users"]!.AsArray().Count, realm["users"]!.AsArray().Count);
        Assert.Equal(original["clients"]!.AsArray().Count, realm["clients"]!.AsArray().Count);
        // the shipped template must never grant by default: no mapper hands out a role without a group condition
        Assert.All(realm["identityProviderMappers"]!.AsArray().Where(m => m!["identityProviderMapper"]!.GetValue<string>().Contains("role", StringComparison.Ordinal)),
            m => Assert.False(string.IsNullOrEmpty(m!["config"]!["claim.value"]!.GetValue<string>())));
        Assert.False(idp["trustEmail"]!.GetValue<bool>());
        Assert.Equal("true", idp["config"]!["validateSignature"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_template_with_a_value_missing_is_refused_and_names_it()
    {
        var (exit, output, err) = await RunAsync(Base, Oidc, AllValues.Where(v => !v.StartsWith("CLIENT_SECRET=", StringComparison.Ordinal)).ToArray());
        Assert.NotEqual(0, exit);
        Assert.Contains("__CLIENT_SECRET__", err, StringComparison.Ordinal);
        Assert.Empty(output); // nothing is written
    }

    [Fact]
    public async Task An_alias_the_realm_already_has_is_refused()
    {
        var merged = Path.GetTempFileName();
        try
        {
            var (exit, output, err) = await RunAsync(Base, Oidc, AllValues);
            Assert.True(exit == 0, err);
            await File.WriteAllTextAsync(merged, output);
            var (exit2, _, err2) = await RunAsync(merged, Oidc, AllValues);
            Assert.NotEqual(0, exit2);
            Assert.Contains("customer-oidc", err2, StringComparison.Ordinal);
        }
        finally { File.Delete(merged); }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Keel.slnx) not found.");
    }
}
