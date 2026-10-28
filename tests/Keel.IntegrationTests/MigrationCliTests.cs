using Keel.Import;
using Keel.Migration.Fixtures;
using Keel.Migration.Mapping;

namespace Keel.IntegrationTests;

/// <summary>keel-import commands and exit codes against a real database and vault, run in process.</summary>
[Collection(PostgresCollection.Name)]
public sealed class MigrationCliTests(PostgresFixture pg)
{
    private sealed record Run(int Code, string Out, string Err);

    private static async Task<Run> Cli(Func<string, string?>? env, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await ImportCli.RunAsync(args, o, e, env, CancellationToken.None);
        return new Run(code, o.ToString(), e.ToString());
    }

    private static string[] Target(MigrationKit k, VaultTarget vault) =>
        ["--connection", k.Db.AppConnectionString, "--vault-url", vault.Url, "--vault-access-key", VaultContainer.AccessKey, "--vault-secret-key", VaultContainer.SecretKey, "--vault-bucket", VaultContainer.Bucket];

    private sealed record VaultTarget(string Url);

    [Fact]
    public async Task Offline_commands_need_no_database_and_report_usage_errors_with_exit_code_1()
    {
        using var k = await MigrationKit.Create(pg);
        var dir = k.Package(Samples.Build(salt: MigrationKit.Salt()));
        var mapping = Path.Combine(k.Root, "mapping.json");
        File.WriteAllText(mapping, Samples.MappingJson(Samples.Mapping()));

        Assert.Equal(ExitCodes.Usage, (await Cli(null)).Code);
        Assert.Equal(ExitCodes.Usage, (await Cli(null, "frobnicate")).Code);
        Assert.Equal(ExitCodes.Usage, (await Cli(null, "inspect")).Code);
        Assert.Equal(ExitCodes.Usage, (await Cli(null, "inspect", dir, "--nonsense")).Code);
        Assert.Equal(ExitCodes.Ok, (await Cli(null, "--help")).Code);

        var inspect = await Cli(null, "inspect", dir);
        Assert.Equal(ExitCodes.Ok, inspect.Code);
        Assert.Contains("items", inspect.Out, StringComparison.Ordinal);
        Assert.Contains("Package SHA-256", inspect.Out, StringComparison.Ordinal);

        var template = Path.Combine(k.Root, "template.json");
        Assert.Equal(ExitCodes.Ok, (await Cli(null, "map-template", dir, "--out", template)).Code);
        var text = File.ReadAllText(template);
        Assert.Contains("\"Released\"", text, StringComparison.Ordinal);
        Assert.Contains("\"jdoe\"", text, StringComparison.Ordinal);
        // the template parses, but nothing is decided for the user: validation fails until the empty values are filled
        Assert.NotNull(MappingConfig.Parse(text));
        var strict = await Cli(null, "validate", dir, "--mapping", template, "--offline", "--out", Path.Combine(k.Root, "out0"));
        Assert.Equal(ExitCodes.Invalid, strict.Code);

        var ok = await Cli(null, "validate", dir, "--mapping", mapping, "--offline", "--out", Path.Combine(k.Root, "out1"));
        Assert.Equal(ExitCodes.Ok, ok.Code);
        Assert.True(File.Exists(Path.Combine(k.Root, "out1", "validate-report.json")));
        Assert.True(File.Exists(Path.Combine(k.Root, "out1", "validate-report-problems.csv")));
    }

    [Fact]
    public async Task There_is_no_implicit_database_target()
    {
        using var k = await MigrationKit.Create(pg);
        var dir = k.Package(Samples.Build(salt: MigrationKit.Salt()));
        var mapping = Path.Combine(k.Root, "mapping.json");
        File.WriteAllText(mapping, Samples.MappingJson(Samples.Mapping()));
        var r = await Cli(_ => null, "dry-run", dir, "--mapping", mapping);
        Assert.Equal(ExitCodes.Refused, r.Code);
        Assert.Contains("No database target", r.Err, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Refused, (await Cli(_ => null, "validate", dir, "--mapping", mapping)).Code);
        // the environment is an explicit target; the password never appears in the output
        var viaEnv = await Cli(name => name == "KEEL_IMPORT_CONNECTION" ? k.Db.AppConnectionString : null, "validate", dir, "--mapping", mapping, "--out", Path.Combine(k.Root, "out"));
        Assert.Equal(ExitCodes.Ok, viaEnv.Code);
        Assert.Contains("Target database:", viaEnv.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("keel_app_dev", viaEnv.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_full_run_dry_run_load_reconcile_report_has_the_documented_exit_codes()
    {
        using var k = await MigrationKit.Create(pg);
        var vault = new VaultTarget(await VaultContainer.ServiceUrlAsync());
        var target = Target(k, vault);
        var dir = k.Package(Samples.Build(salt: MigrationKit.Salt()));
        var mapping = Path.Combine(k.Root, "mapping.json");
        File.WriteAllText(mapping, Samples.MappingJson(Samples.Mapping()));
        var outDir = Path.Combine(k.Root, "reports");

        var dry = await Cli(null, [.. new[] { "dry-run", dir, "--mapping", mapping, "--out", outDir }, .. target]);
        Assert.Equal(ExitCodes.Ok, dry.Code);
        Assert.Contains("Nothing was written", dry.Out, StringComparison.Ordinal);
        var report = Path.Combine(outDir, "dry-run-report.json");
        Assert.Equal(0, await k.Count("SELECT count(*) FROM plm.part_master"));

        // refused: no --yes
        Assert.Equal(ExitCodes.Usage, (await Cli(null, [.. new[] { "load", dir, "--mapping", mapping, "--dry-run-report", report }, .. target])).Code);
        // refused: a modified report
        var forged = Path.Combine(k.Root, "forged.json");
        File.WriteAllText(forged, File.ReadAllText(report).Replace("\"parts\": 13", "\"parts\": 1", StringComparison.Ordinal));
        var refused = await Cli(null, [.. new[] { "load", dir, "--mapping", mapping, "--dry-run-report", forged, "--yes" }, .. target]);
        Assert.Equal(ExitCodes.Refused, refused.Code);
        Assert.Contains("modified", refused.Err, StringComparison.Ordinal);
        Assert.Equal(0, await k.Count("SELECT count(*) FROM plm.part_master"));

        var load = await Cli(null, [.. new[] { "load", dir, "--mapping", mapping, "--dry-run-report", report, "--yes", "--out", outDir, "--operator", "cli-tester" }, .. target]);
        Assert.Equal(ExitCodes.Ok, load.Code);
        Assert.Equal(13, await k.Count("SELECT count(*) FROM plm.part_master"));
        Assert.Equal("cli-tester", await k.Text("SELECT started_by FROM mig.batch"));
        Assert.True(File.Exists(Path.Combine(outDir, "load-report.md")));

        var rec = await Cli(null, [.. new[] { "reconcile", dir, "--mapping", mapping, "--out", outDir }, .. target]);
        Assert.Equal(ExitCodes.Ok, rec.Code);
        Assert.Contains("RECONCILED", rec.Out, StringComparison.Ordinal);

        var rep = await Cli(null, [.. new[] { "report", "latest" }, .. target]);
        Assert.Equal(ExitCodes.Ok, rep.Code);
        Assert.Contains("Completed", rep.Out, StringComparison.Ordinal);

        // a database that disagrees with the source makes reconcile exit non-zero
        File.WriteAllText(Path.Combine(dir, "files", "spec.pdf"), "changed after the load");
        var bad = await Cli(null, [.. new[] { "reconcile", dir, "--mapping", mapping, "--out", outDir }, .. target]);
        Assert.Equal(ExitCodes.ReconcileDifferences, bad.Code);
    }

    [Fact]
    public async Task Invalid_packages_exit_with_2_and_quarantine_mode_loads_the_rest_with_exit_code_3()
    {
        using var k = await MigrationKit.Create(pg);
        var vault = new VaultTarget(await VaultContainer.ServiceUrlAsync());
        var target = Target(k, vault);
        var dir = k.Package(Samples.Build(Defect.BadUnit, salt: MigrationKit.Salt()));
        var mapping = Path.Combine(k.Root, "mapping.json");
        File.WriteAllText(mapping, Samples.MappingJson(Samples.Mapping()));
        var outDir = Path.Combine(k.Root, "reports");

        var dry = await Cli(null, [.. new[] { "dry-run", dir, "--mapping", mapping, "--out", outDir, "--max-errors", "2" }, .. target]);
        Assert.Equal(ExitCodes.Invalid, dry.Code);
        Assert.Contains("UNIT-UNMAPPED", dry.Out, StringComparison.Ordinal);
        Assert.Contains("BLOCKING", dry.Out, StringComparison.Ordinal);

        var q = await Cli(null, [.. new[] { "dry-run", dir, "--mapping", mapping, "--out", outDir, "--quarantine" }, .. target]);
        Assert.Equal(ExitCodes.Ok, q.Code);
        var report = Path.Combine(outDir, "dry-run-report.json");
        var load = await Cli(null, [.. new[] { "load", dir, "--mapping", mapping, "--dry-run-report", report, "--yes", "--quarantine", "--out", outDir }, .. target]);
        Assert.Equal(ExitCodes.CompletedWithIssues, load.Code);
        Assert.Equal(1, await k.Count("SELECT count(*) FROM mig.quarantine WHERE source_id = 'P-2003'"));
        Assert.Equal(0, await k.Count("SELECT count(*) FROM plm.part_master WHERE number IN ('P-2003', 'A-1100', 'A-1000')"));
    }

    [Fact]
    public async Task Revision_level_effectivity_exits_with_6_naming_the_follow_up()
    {
        using var k = await MigrationKit.Create(pg);
        var dir = k.Package(Samples.Build(salt: MigrationKit.Salt()));
        var cfg = Samples.Mapping();
        cfg.Effectivity.RevisionLevel = true;
        var mapping = Path.Combine(k.Root, "mapping.json");
        File.WriteAllText(mapping, Samples.MappingJson(cfg));
        var r = await Cli(null, "validate", dir, "--mapping", mapping, "--offline", "--out", Path.Combine(k.Root, "o"));
        Assert.Equal(ExitCodes.NotSupported, r.Code);
        Assert.Contains("FUP-3", r.Err, StringComparison.Ordinal);
    }
}
