using System.Text.RegularExpressions;

namespace Keel.UnitTests;

/// <summary>
/// ADR-0059 and ADR-0060: facts about the pipeline and the production bundle that must not rot. The workflows are checked structurally by scripts/ci/lint-workflows.rb and the compose file
/// by scripts/verify-packaging.sh; these tests keep the pieces that tie them together honest without needing Ruby or Docker.
/// </summary>
public sealed partial class ProductionReadinessTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    [Fact]
    public void Every_script_a_workflow_calls_exists_and_is_executable()
    {
        var workflows = Directory.GetFiles(Path.Combine(Root, ".github", "workflows"), "*.yml");
        Assert.True(workflows.Length >= 7, "expected the pr, fast, e2e, axe, main, nightly, image and drills workflows");
        foreach (var w in workflows)
            foreach (Match m in ScriptReference().Matches(File.ReadAllText(w)))
            {
                var path = Path.Combine(Root, m.Groups["p"].Value);
                Assert.True(File.Exists(path), $"{Path.GetFileName(w)} calls {m.Groups["p"].Value}, which does not exist");
                if (!OperatingSystem.IsWindows())
                    Assert.True((File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0, $"{m.Groups["p"].Value} is not executable");
            }
    }

    [Fact]
    public void Pull_requests_run_the_fast_set_only_and_the_browser_suite_runs_on_main_and_nightly()
    {
        var pr = Read(".github", "workflows", "pr.yml");
        Assert.Contains("fast.yml", pr, StringComparison.Ordinal);
        Assert.DoesNotContain("e2e.yml", pr, StringComparison.Ordinal);
        Assert.DoesNotContain("drills.yml", pr, StringComparison.Ordinal);
        Assert.Contains("e2e.yml", Read(".github", "workflows", "main.yml"), StringComparison.Ordinal);
        var nightly = Read(".github", "workflows", "nightly.yml");
        Assert.Contains("e2e.yml", nightly, StringComparison.Ordinal);
        Assert.Contains("drills.yml", nightly, StringComparison.Ordinal);
        Assert.Contains("schedule:", nightly, StringComparison.Ordinal);
        var fast = Read(".github", "workflows", "fast.yml");
        foreach (var needle in new[] { "-warnaserror", "tests/Keel.UnitTests", "tests/Keel.IntegrationTests", "update-openapi.sh --check", "check-md-links.py", "secret-scan.py", "generate-sbom.sh --check", "generate-sbom.sh --out", "dependency-audit.sh", "verify-helm.sh", "verify-packaging.sh" })
            Assert.Contains(needle, fast, StringComparison.Ordinal);
        Assert.Contains("ms-playwright", Read(".github", "workflows", "e2e.yml"), StringComparison.Ordinal);   // the browser is cached
        Assert.Contains("*AccessibilityTests", Read(".github", "workflows", "axe.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_action_is_pinned_to_a_commit_and_no_workflow_runs_untrusted_code_with_secrets()
    {
        foreach (var w in Directory.GetFiles(Path.Combine(Root, ".github", "workflows"), "*.yml"))
        {
            var text = File.ReadAllText(w);
            foreach (Match m in UsesLine().Matches(text))
                Assert.Matches(@"@[0-9a-f]{40}\b", m.Value);
            Assert.DoesNotContain("pull_request_target", text, StringComparison.Ordinal);
            Assert.DoesNotContain("workflow_run:", text, StringComparison.Ordinal);
            Assert.Contains("permissions:", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_bundle_ships_the_preflight_and_the_versions_manifest_and_install_and_upgrade_run_the_preflight()
    {
        var package = Read("scripts", "package-offline.sh");
        Assert.Contains("preflight.sh", package, StringComparison.Ordinal);
        Assert.Contains("versions.json", package, StringComparison.Ordinal);
        Assert.Contains("preflight.sh", Read("deploy", "offline", "install.sh"), StringComparison.Ordinal);
        Assert.Contains("preflight.sh", Read("deploy", "offline", "upgrade.sh"), StringComparison.Ordinal);
        Assert.Contains("--preflight-only", Read("deploy", "offline", "install.sh"), StringComparison.Ordinal);
        Assert.Contains("versions.json", Read("scripts", "verify-offline.sh"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_compose_bundle_declares_limits_and_healthchecks_for_every_long_running_service()
    {
        var compose = Read("deploy", "compose", "compose.prod-example.yaml");
        foreach (var limit in new[] { "limits-app", "limits-small", "limits-db", "limits-vault" })
            Assert.Contains("&" + limit, compose, StringComparison.Ordinal);
        foreach (var service in new[] { "proxy", "app", "postgres", "vault" })
            Assert.Matches($@"(?m)^  {service}:\r?\n    <<: \[\*hardened, \*limits-", compose);
        Assert.Equal(3, Regex.Count(compose, @"(?m)^    healthcheck:"));   // proxy, postgres, vault; the application's is the image's HEALTHCHECK
        Assert.Contains("HEALTHCHECK", Read("Dockerfile"), StringComparison.Ordinal);
        Assert.Contains("vault: { condition: service_healthy }", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void The_operations_proof_is_documented_with_its_results_and_the_decision_records_exist()
    {
        foreach (var f in new[] { "backup-restore.md", "load-smoke.md", "upgrade-rehearsal.md" })
            Assert.True(File.Exists(Path.Combine(Root, "docs", "ops", "drill-results", f)), $"docs/ops/drill-results/{f} is missing");
        foreach (var f in new[] { "backup-restore.md", "upgrade-rehearsal.md" })
            Assert.Contains("failed: 0.", Read("docs", "ops", "drill-results", f), StringComparison.Ordinal);   // a recorded run that failed must not be committed as the result
        Assert.True(File.Exists(Path.Combine(Root, "docs", "adr", "0059-ci-and-supply-chain.md")));
        Assert.True(File.Exists(Path.Combine(Root, "docs", "adr", "0060-packaging-backup-restore-and-upgrade.md")));
        var assumptions = Read("docs", "assumptions.md");
        foreach (var id in new[] { "A91", "A92", "A93" }) Assert.Contains($"| {id} |", assumptions, StringComparison.Ordinal);
        // the legal packet's status stays pending: this increment adds facts only
        Assert.Contains("pending counsel review", Read("docs", "review", "legal-open-source.md"), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"(?<![\w./-])(?<p>scripts/[\w./-]+\.(?:sh|py|rb))")]
    private static partial Regex ScriptReference();

    [GeneratedRegex(@"(?m)^\s*-?\s*uses:\s*(?!\./)\S+.*$")]
    private static partial Regex UsesLine();
}
