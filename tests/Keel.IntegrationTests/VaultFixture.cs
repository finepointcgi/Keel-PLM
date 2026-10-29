using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Keel.Documents.Vault;

namespace Keel.IntegrationTests;

/// <summary>
/// One SeaweedFS container (S3 API, Apache-2.0; ADR-0015) per test run, started exactly like the Compose dev stack
/// (same image, same dev-only identity file, same bucket). Started on first use and shared; Testcontainers' reaper
/// removes it when the run ends. Tests share the bucket safely because objects are content-addressed and immutable;
/// tests that tamper with objects use bytes nobody else writes.
/// </summary>
public static class VaultContainer
{
    public const string Image = "chrislusf/seaweedfs:4.47";
    public const string Bucket = "keel-vault";
    public const string AccessKey = "keel_vault_dev";
    public const string SecretKey = "keel_vault_dev_secret";
    private const int S3Port = 8333;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IContainer? _container;
    private static readonly string RepoRoot = FindRepoRoot();

    public static async Task<string> ServiceUrlAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_container is null)
            {
                var container = new ContainerBuilder(Image)
                    .WithResourceMapping(new FileInfo(WriteTestIdentityFile()), "/etc/seaweedfs/")
                    // Test data is throwaway: keep it in memory. SeaweedFS sizes its volume count from free disk space, and a
                    // nearly full Docker disk would otherwise leave it with no writable volumes.
                    .WithTmpfsMount("/data")
                    .WithCommand("mini", "-dir=/data", "-s3.config=/etc/seaweedfs/s3.json", $"-bucket={Bucket}", "-admin.ui=false", "-master.volumeSizeLimitMB=64")
                    .WithPortBinding(S3Port, assignRandomHostPort: true)
                    // Anonymous requests are refused once the S3 gateway and its identities are loaded.
                    .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(S3Port).ForPath("/" + Bucket).ForStatusCode(System.Net.HttpStatusCode.Forbidden)))
                    .Build();
                await container.StartAsync();
                _container = container;
                // The bucket is created asynchronously after the gateway starts; wait until the app identity can list it.
                await WaitForBucketAsync(Options($"http://{container.Hostname}:{container.GetMappedPublicPort(S3Port)}"));
            }
            return $"http://{_container.Hostname}:{_container.GetMappedPublicPort(S3Port)}";
        }
        finally
        {
            Gate.Release();
        }
    }

    // Test-only identity that may create buckets. The product identity (keel-app) stays exactly as in the dev stack.
    public const string AdminAccessKey = "keel_test_admin";
    public const string AdminSecretKey = "keel_test_admin_secret";

    /// <summary>The dev identity file plus a test-only admin identity, written to a temp directory as s3.json.</summary>
    private static string WriteTestIdentityFile()
    {
        var dev = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "deploy/compose/seaweedfs/s3.json")))!.AsObject();
        dev["identities"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["name"] = "keel-test-admin",
            ["credentials"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["accessKey"] = AdminAccessKey, ["secretKey"] = AdminSecretKey }),
            ["actions"] = new System.Text.Json.Nodes.JsonArray("Admin", "Read", "Write", "List", "Tagging"),
        });
        var dir = Directory.CreateTempSubdirectory("keel-s3-").FullName;
        var path = Path.Combine(dir, "s3.json");
        File.WriteAllText(path, dev.ToJsonString());
        return path;
    }

    /// <summary>
    /// Options for a bucket nobody else uses. Tests that LIST a whole bucket (the garbage collector) must not see objects other tests
    /// upload concurrently into the shared bucket: with a manual clock those look old and unreferenced and would be counted or swept.
    /// </summary>
    public static async Task<VaultOptions> IsolatedOptionsAsync()
    {
        var options = await OptionsAsync();
        options.AccessKey = AdminAccessKey;
        options.SecretKey = AdminSecretKey;
        options.Bucket = "gc-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));
        using var s3 = new Amazon.S3.AmazonS3Client(
            new Amazon.Runtime.BasicAWSCredentials(AdminAccessKey, AdminSecretKey),
            new Amazon.S3.AmazonS3Config { ServiceURL = options.ServiceUrl, ForcePathStyle = true, AuthenticationRegion = options.Region });
        await s3.PutBucketAsync(options.Bucket);
        return options;
    }

    public static async Task<VaultOptions> OptionsAsync(int partSizeBytes = 5 * 1024 * 1024, long maxUploadBytes = VaultOptions.DefaultMaxUploadBytes) =>
        Options(await ServiceUrlAsync(), partSizeBytes, maxUploadBytes);

    private static VaultOptions Options(string url, int partSizeBytes = 5 * 1024 * 1024, long maxUploadBytes = VaultOptions.DefaultMaxUploadBytes) => new()
    {
        ServiceUrl = url, AccessKey = AccessKey, SecretKey = SecretKey, Bucket = Bucket, PartSizeBytes = partSizeBytes, MaxUploadBytes = maxUploadBytes,
    };

    private static async Task WaitForBucketAsync(VaultOptions options)
    {
        using var vault = new ObjectVault(options);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await vault.PingAsync();
                return;
            }
            catch (Exception) when (attempt < 60)
            {
                await Task.Delay(500);
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Keel.slnx) not found.");
    }
}
