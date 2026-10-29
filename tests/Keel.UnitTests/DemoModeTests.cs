using System.Security.Claims;
using System.Text.Json;
using Keel.Security;
using Keel.Web.Services;
using Keel.Web.Services.Demo;
using Keel.Workflow;
using Microsoft.Extensions.Configuration;

namespace Keel.UnitTests;

/// <summary>Demo mode (ADR-0045): the guards that keep demo sign-in out of any real deployment, and the claims it produces.</summary>
public sealed class DemoModeTests
{
    private static IConfiguration Config(string? enabled) =>
        new ConfigurationBuilder().AddInMemoryCollection(enabled is null ? [] : new Dictionary<string, string?> { [DemoMode.EnabledKey] = enabled }).Build();

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    [InlineData("demo")] // the name is matched exactly: no near misses
    [InlineData("")]
    public void Enabled_outside_the_Demo_environment_refuses_to_start(string environment)
    {
        var e = Assert.Throws<InvalidOperationException>(() => DemoMode.IsEnabled(Config("true"), environment));
        Assert.Contains("Keel:Demo:Enabled", e.Message, StringComparison.Ordinal);
        Assert.Contains("refusing to start", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Demo_mode_needs_both_the_environment_and_the_setting()
    {
        Assert.True(DemoMode.IsEnabled(Config("true"), "Demo"));
        Assert.False(DemoMode.IsEnabled(Config(null), "Demo"));
        Assert.False(DemoMode.IsEnabled(Config("false"), "Demo"));
        Assert.False(DemoMode.IsEnabled(Config(null), "Production"));
        Assert.False(DemoMode.IsEnabled(Config("false"), "Production"));
    }

    [Fact]
    public void The_service_account_is_not_a_demo_sign_in()
    {
        var names = DemoUsers.List.Select(u => u.Username).ToList();
        Assert.DoesNotContain("erp-service", names);
        Assert.Equal(
            ["ada", "alice", "audrey", "bob", "carmen", "farid", "lena", "rex", "sup-alpha", "sup-bravo"],
            names.Order(StringComparer.Ordinal).ToArray());
        // The service account's subject is still a known demo subject: a demo database may contain its records.
        Assert.Contains("f5e2c581-d932-5984-ad94-830bc84bd39c", DemoUsers.KnownSubjects);
        Assert.All(DemoUsers.List, u => Assert.False(string.IsNullOrWhiteSpace(u.WhatToTry)));
    }

    /// <summary>The claims the dev realm's protocol mappers put in the token, read independently from the realm file.</summary>
    private static JsonElement RealmUser(string username)
    {
        var path = Path.Combine(RepoRoot(), "deploy", "compose", "keycloak", "keel-realm.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("users").EnumerateArray().Single(u => u.GetProperty("username").GetString() == username).Clone();
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    public static TheoryData<string> Everyone() => new(DemoUsers.List.Select(u => u.Username));

    [Theory]
    [MemberData(nameof(Everyone))]
    public void The_principal_carries_exactly_the_claims_of_the_realm_user(string username)
    {
        var realm = RealmUser(username);
        var attrs = realm.GetProperty("attributes");
        string[] Many(string key) => attrs.TryGetProperty(key, out var v) ? [.. v.EnumerateArray().Select(e => e.GetString()!)] : [];
        var now = DateTimeOffset.UtcNow;
        var principal = DemoUsers.CreatePrincipal(DemoUsers.Find(username)!, now, "session-1", now.AddHours(8));

        string One(string type) => principal.FindAll(type).Single().Value;
        Assert.Equal(realm.GetProperty("id").GetString(), One("sub"));
        Assert.Equal(username, One("preferred_username"));
        Assert.Equal($"{realm.GetProperty("firstName").GetString()} {realm.GetProperty("lastName").GetString()}", One("name"));
        Assert.Equal(Many("us_person").Single(), One("us_person"));
        Assert.Equal(Many("citizenship").Single(), One("citizenship"));
        Assert.Equal(Many("org").Single(), One("org"));
        foreach (var key in new[] { "programs", "export_authorizations", "cui_categories" })
            Assert.Equal(Many(key).Order(StringComparer.Ordinal), principal.FindAll(key).Select(c => c.Value).Order(StringComparer.Ordinal));
        var roles = realm.GetProperty("realmRoles").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(roles, principal.FindAll("roles").Select(c => c.Value).Order(StringComparer.Ordinal));
        Assert.All(roles, r => Assert.True(principal.IsInRole(r)));
        Assert.Equal(now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), One("auth_time"));
        Assert.Equal("session-1", One(KeelUi.SessionIdClaim));
        Assert.Equal(now.AddHours(8).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), One(KeelUi.SessionExpiresClaim));
        Assert.True(principal.Identity!.IsAuthenticated);

        // And it maps to the same subject the policy engine sees for a real sign-in.
        var subject = SubjectClaims.FromPrincipal(principal)!;
        Assert.Equal(realm.GetProperty("id").GetString(), subject.Id);
        Assert.Equal(string.Equals(Many("us_person").Single(), "true", StringComparison.OrdinalIgnoreCase), subject.UsPerson);
        Assert.Equal(Many("programs").Order(StringComparer.Ordinal), subject.Programs.Order(StringComparer.Ordinal));
        Assert.Equal(roles, subject.Roles.Order(StringComparer.Ordinal));
        Assert.Equal(now.ToUnixTimeSeconds(), subject.AuthTime!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void A_demo_step_up_passes_for_the_same_person_and_never_for_another()
    {
        var alice = DemoUsers.Find("alice")!;
        var now = DateTimeOffset.UtcNow;
        var before = DemoUsers.CreatePrincipal(alice, now.AddMinutes(-30), "s", now.AddHours(8));
        var after = DemoUsers.CreatePrincipal(alice, now, "s", now.AddHours(8));
        Assert.Equal(StepUp.Verdict.Accepted, StepUp.Check(before, after));
        var bob = DemoUsers.CreatePrincipal(DemoUsers.Find("bob")!, now, "s", now.AddHours(8));
        Assert.Equal(StepUp.Verdict.SubjectMismatch, StepUp.Check(before, bob));
    }

    [Fact]
    public void The_sign_in_page_lists_every_person_without_a_password_field_and_shows_the_banner()
    {
        var token = new Microsoft.AspNetCore.Antiforgery.AntiforgeryTokenSet("request", "cookie", "__RequestVerificationToken", "X-CSRF");
        var html = DemoSignIn.ChoosePage("/parts", token);
        Assert.Contains(DemoMode.BannerText, html, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"password\"", html, StringComparison.Ordinal);
        foreach (var u in DemoUsers.List) Assert.Contains($"value=\"{u.Username}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("erp-service", html, StringComparison.Ordinal);
        Assert.Contains("name=\"returnUrl\" value=\"/parts\"", html, StringComparison.Ordinal);
        // No inline script or style: the content security policy would block them.
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" style=", html, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The demo image is the last stage of the Dockerfile and uses only syntax Cloud Build's default builder accepts (ADR-0045).</summary>
public sealed class DemoPackagingTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    private static string Dockerfile() => File.ReadAllText(Path.Combine(Root(), "Dockerfile"));

    [Fact]
    public void The_last_stage_is_demo_and_the_production_stages_are_kept()
    {
        var stages = Dockerfile().Split('\n').Where(l => l.StartsWith("FROM ", StringComparison.Ordinal)).ToList();
        Assert.EndsWith(" AS demo", stages[^1], StringComparison.Ordinal);
        Assert.Contains(stages, s => s.EndsWith(" AS publish", StringComparison.Ordinal));
        Assert.Contains(stages, s => s.EndsWith(" AS runtime", StringComparison.Ordinal));
        // The offline bundle must build the production runtime, not the demo.
        Assert.Contains("docker build --target runtime", File.ReadAllText(Path.Combine(Root(), "scripts", "package-offline.sh")), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_plain_Dockerfile_syntax_is_used()
    {
        var text = Dockerfile();
        foreach (var banned in new[] { "--mount", "BUILDPLATFORM", "TARGETPLATFORM", "--platform", "# syntax", "<<EOF", "--link", "--chmod" })
            Assert.DoesNotContain(banned, text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_vault_binary_comes_from_the_same_pin_as_the_compose_stack()
    {
        var pin = File.ReadAllLines(Path.Combine(Root(), "deploy", "images.env")).Single(l => l.StartsWith("KEEL_VAULT_IMAGE=", StringComparison.Ordinal))["KEEL_VAULT_IMAGE=".Length..];
        Assert.Contains($"ARG WEED_IMAGE={pin}", Dockerfile(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_entrypoint_refuses_to_run_outside_demo_mode_and_the_image_sets_both_switches()
    {
        var script = File.ReadAllText(Path.Combine(Root(), "deploy", "demo", "entrypoint.sh"));
        Assert.Contains("ASPNETCORE_ENVIRONMENT:-}\" != \"Demo\"", script, StringComparison.Ordinal);
        Assert.Contains("Keel__Demo__Enabled:-}\" != \"true\"", script, StringComparison.Ordinal);
        var text = Dockerfile();
        var demoStage = text[text.LastIndexOf("AS demo", StringComparison.Ordinal)..];
        Assert.Contains("ASPNETCORE_ENVIRONMENT=Demo", demoStage, StringComparison.Ordinal);
        Assert.Contains("Keel__Demo__Enabled=true", demoStage, StringComparison.Ordinal);
        // The production runtime stage must never carry either.
        var production = text[text.IndexOf("AS runtime", StringComparison.Ordinal)..text.IndexOf("AS demo-publish", StringComparison.Ordinal)];
        Assert.DoesNotContain("ASPNETCORE_ENVIRONMENT=Demo", production, StringComparison.Ordinal);
        Assert.DoesNotContain("Keel__Demo", production, StringComparison.Ordinal);
    }
}
