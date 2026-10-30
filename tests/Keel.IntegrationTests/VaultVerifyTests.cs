using System.Security.Cryptography;
using System.Text;
using Amazon.S3.Model;
using Keel.Documents.Vault;
using Keel.VaultTool;

namespace Keel.IntegrationTests;

/// <summary>ADR-0060: the bulk vault verifier finds a missing object and a damaged one, passes an intact vault, and honours a live predicate. Read-only.</summary>
[Collection(PostgresCollection.Name)]
public sealed class VaultVerifyTests(PostgresFixture pg)
{
    private static byte[] Unique(string label) => Encoding.UTF8.GetBytes($"{label} {Guid.NewGuid()}");

    [Fact]
    public async Task Verify_passes_an_intact_vault_and_reports_a_missing_and_a_damaged_object_with_the_columns_that_name_them()
    {
        var db = await pg.CreateDatabaseAsync();
        var options = await VaultContainer.OptionsAsync();
        using var vault = new ObjectVault(options, null, null);
        await db.ExecuteAsSuperuserAsync("CREATE TABLE public.verify_refs (sha256 text NOT NULL, size_bytes bigint NOT NULL, released boolean NOT NULL DEFAULT false); GRANT SELECT ON public.verify_refs TO keel_app;");

        var good = Unique("intact");
        var goodStored = await vault.PutAsync(new MemoryStream(good));
        var damaged = Unique("damaged");
        var damagedStored = await vault.PutAsync(new MemoryStream(damaged));
        var neverStored = Unique("missing");
        var neverSha = Convert.ToHexStringLower(SHA256.HashData(neverStored));
        var releasedBytes = Unique("released");
        var releasedSha = Convert.ToHexStringLower(SHA256.HashData(releasedBytes));   // never uploaded, but its row is "released": must not count

        await db.ExecuteAsSuperuserAsync($"""
            INSERT INTO public.verify_refs (sha256, size_bytes) VALUES
              ('{goodStored.Sha256}', {good.Length}), ('{goodStored.Sha256}', {good.Length}),
              ('{damagedStored.Sha256}', {damaged.Length}), ('{neverSha}', {neverStored.Length});
            INSERT INTO public.verify_refs (sha256, size_bytes, released) VALUES ('{releasedSha}', {releasedBytes.Length}, true);
            """);
        var sources = new[] { new VaultReferenceSource("public", "verify_refs", "sha256", "test", "NOT r.released") };
        await using var ds = db.AppDataSource();

        // 1. intact: only the good object is referenced
        await db.ExecuteAsSuperuserAsync($"CREATE VIEW public.verify_refs_good AS SELECT * FROM public.verify_refs WHERE sha256 = '{goodStored.Sha256}'; GRANT SELECT ON public.verify_refs_good TO keel_app;");
        var intact = await VaultVerifier.VerifyAsync(vault, ds, 2, CancellationToken.None, [new VaultReferenceSource("public", "verify_refs_good", "sha256", "test")]);
        Assert.True(intact.Intact);
        Assert.Equal((2, 1, 1, (long)good.Length), (intact.References, intact.Distinct, intact.Verified, intact.BytesVerified));

        // 2. someone with store access swaps the bytes of one object
        using var s3 = ObjectVault.CreateClient(options);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = options.Bucket, Key = damagedStored.Sha256, InputStream = new MemoryStream(Unique("damaged (altered)")) });
        var report = await VaultVerifier.VerifyAsync(vault, ds, 2, CancellationToken.None, sources);

        Assert.False(report.Intact);
        Assert.Equal(3, report.Distinct);          // the released row does not count
        Assert.Equal(1, report.Verified);
        Assert.Equal(2, report.Problems.Count);
        var missing = Assert.Single(report.Problems, p => p.Sha256 == neverSha);
        Assert.Equal("missing", missing.Kind);
        Assert.Equal(["public.verify_refs.sha256"], missing.Sources);
        Assert.Equal("damaged", Assert.Single(report.Problems, p => p.Sha256 == damagedStored.Sha256).Kind);
    }

    [Fact]
    public async Task The_command_line_checks_the_real_reference_registry_and_refuses_options_that_could_change_things()
    {
        var db = await pg.CreateDatabaseAsync();
        var vault = await VaultContainer.OptionsAsync();
        string[] target = ["--connection", db.AppConnectionString, "--vault-url", vault.ServiceUrl, "--vault-access-key", vault.AccessKey, "--vault-secret-key", vault.SecretKey, "--vault-bucket", vault.Bucket];
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await VaultCli.RunAsync(["verify", "--parallel", "2", .. target], output, error, _ => null, CancellationToken.None);
        Assert.True(code == 0, error.ToString());
        Assert.Contains("RESULT: INTACT", output.ToString(), StringComparison.Ordinal);   // empty database: nothing referenced, nothing missing

        Assert.Equal(2, await VaultCli.RunAsync(["verify", "--delete", .. target], new StringWriter(), new StringWriter(), _ => null, CancellationToken.None));
        Assert.Equal(2, await VaultCli.RunAsync(["verify", "--parallel", "0", .. target], new StringWriter(), new StringWriter(), _ => null, CancellationToken.None));
        var noTarget = new StringWriter();
        Assert.Equal(2, await VaultCli.RunAsync(["verify"], new StringWriter(), noTarget, _ => null, CancellationToken.None));
        Assert.Contains("No database target", noTarget.ToString(), StringComparison.Ordinal);
    }
}
