using Keel.Documents.Vault;
using Keel.VaultTool;

namespace Keel.IntegrationTests;

/// <summary>The <c>keel-vault gc</c> command line: target safety, explicit delete confirmation, report output.</summary>
[Collection(PostgresCollection.Name)]
public sealed class VaultToolTests(PostgresFixture pg)
{
    private static async Task<(int Code, string Out, string Err)> RunAsync(string[] args, Func<string, string?>? env = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await VaultCli.RunAsync(args, output, error, env ?? (_ => null), CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private static async Task<string[]> TargetAsync(TestDatabase db)
    {
        var vault = await VaultContainer.OptionsAsync();
        return ["--connection", db.AppConnectionString, "--vault-url", vault.ServiceUrl, "--vault-access-key", vault.AccessKey, "--vault-secret-key", vault.SecretKey,
            "--vault-bucket", vault.Bucket, "--prefix", "ffff"];
    }

    [Fact]
    public async Task Never_targets_the_dev_database_by_default()
    {
        // The repository's appsettings.Development.json exists on this machine; without --use-dev-stack it must not be used.
        var (code, _, err) = await RunAsync(["gc", "--dry-run"]);
        Assert.Equal(2, code);
        Assert.Contains("No database target", err, StringComparison.Ordinal);
        Assert.Contains("--use-dev-stack", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_needs_the_bucket_confirmation_and_rejects_ambiguous_modes()
    {
        var db = await pg.CreateDatabaseAsync();
        var target = await TargetAsync(db);

        var noConfirm = await RunAsync(["gc", "--delete", .. target]);
        Assert.Equal(2, noConfirm.Code);
        Assert.Contains("--confirm-bucket", noConfirm.Err, StringComparison.Ordinal);

        var wrong = await RunAsync(["gc", "--delete", "--confirm-bucket", "some-other-bucket", .. target]);
        Assert.Equal(2, wrong.Code);

        Assert.Equal(2, (await RunAsync(["gc", "--delete", "--dry-run", .. target])).Code);
        Assert.Equal(2, (await RunAsync(["gc", "--grace-days", "0", .. target])).Code);
        Assert.Equal(2, (await RunAsync(["gc", "--bogus"])).Code);
    }

    [Fact]
    public async Task Dry_run_prints_a_report_and_records_the_run()
    {
        var db = await pg.CreateDatabaseAsync();
        var target = await TargetAsync(db);

        var (code, output, err) = await RunAsync(["gc", "--dry-run", .. target]);

        Assert.True(code == 0, err);
        Assert.Contains("Mode: dry-run", output, StringComparison.Ordinal);
        Assert.Contains("Dry run: nothing was deleted.", output, StringComparison.Ordinal);
        Assert.Contains("Report hash:", output, StringComparison.Ordinal);
        await using var cmd = db.AppDataSource().CreateCommand("SELECT count(*) FROM vault.gc_run WHERE mode = 'dry-run'");
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());

        // A delete run with the matching confirmation works; young objects (and everything else here) stay.
        var vault = await VaultContainer.OptionsAsync();
        var (dcode, dout, derr) = await RunAsync(["gc", "--delete", "--confirm-bucket", vault.Bucket, "--json", .. target]);
        Assert.True(dcode == 0, derr);
        Assert.Contains("\"mode\": \"Delete\"", dout, StringComparison.Ordinal);
        Assert.Contains("\"deleted\": 0", dout, StringComparison.Ordinal);
    }
}
