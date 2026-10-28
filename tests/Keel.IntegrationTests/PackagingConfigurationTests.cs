using Keel.Documents.Vault;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.IntegrationTests;

/// <summary>Production configuration paths added for packaging (increment 1.7): secrets as files, key ring, proxy trust.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PackagingConfigurationTests(PostgresFixture pg)
{
    [Fact]
    public async Task Secrets_directory_supplies_settings_and_ignores_unrelated_files()
    {
        var dir = Directory.CreateTempSubdirectory("keel-secrets-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Keel__Vault__Bucket"), "bucket-from-secret-file\n");
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Keel__Vault__SecretKey"), "secret-from-file");
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "audit_checkpoint_key"), "-----not a setting-----");
            await using var api = await ApiFactory.CreateAsync(pg);
            await using var configured = api.WithWebHostBuilder(b => b.UseSetting("KEEL_SECRETS_DIR", dir.FullName));
            var vault = configured.Services.GetRequiredService<VaultOptions>();
            Assert.Equal("bucket-from-secret-file", vault.Bucket);
            Assert.Equal("secret-from-file", vault.SecretKey);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Missing_secrets_directory_is_a_startup_error()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        await using var configured = api.WithWebHostBuilder(b => b.UseSetting("KEEL_SECRETS_DIR", "/nonexistent/keel-secrets"));
        var ex = Assert.Throws<InvalidOperationException>(() => configured.CreateClient());
        Assert.Contains("KEEL_SECRETS_DIR", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_requires_a_persistent_data_protection_key_directory()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        await using var production = api.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var ex = Assert.Throws<InvalidOperationException>(() => production.CreateClient());
        Assert.Contains("Keel:DataProtection:KeyDirectory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Proxy_trust_requires_networks_and_valid_cidr()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        await using var noNetworks = api.WithWebHostBuilder(b => b.UseSetting("Keel:Proxy:Enabled", "true"));
        Assert.Contains("KnownNetworks", Assert.Throws<InvalidOperationException>(() => noNetworks.CreateClient()).Message, StringComparison.Ordinal);

        await using var badCidr = api.WithWebHostBuilder(b =>
        {
            b.UseSetting("Keel:Proxy:Enabled", "true");
            b.UseSetting("Keel:Proxy:KnownNetworks:0", "not-a-cidr");
        });
        Assert.Contains("not a CIDR", Assert.Throws<InvalidOperationException>(() => badCidr.CreateClient()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Data_protection_keys_are_persisted_to_the_configured_directory()
    {
        var dir = Directory.CreateTempSubdirectory("keel-dp-");
        try
        {
            await using var api = await ApiFactory.CreateAsync(pg);
            await using var configured = api.WithWebHostBuilder(b => b.UseSetting("Keel:DataProtection:KeyDirectory", dir.FullName));
            var protector = configured.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>().CreateProtector("keel-test");
            Assert.Equal("synthetic", Microsoft.AspNetCore.DataProtection.DataProtectionCommonExtensions.Unprotect(protector, Microsoft.AspNetCore.DataProtection.DataProtectionCommonExtensions.Protect(protector, "synthetic")));
            Assert.NotEmpty(Directory.GetFiles(dir.FullName, "key-*.xml"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
