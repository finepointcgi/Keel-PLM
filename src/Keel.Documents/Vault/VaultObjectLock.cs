using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Keel.Core;

namespace Keel.Documents.Vault;

/// <summary>
/// S3 Object Lock for the vault bucket (records management, ADR-0041), feature-detected and never assumed. <see cref="DetectAsync"/> asks the store for the bucket's object lock
/// configuration: only an explicit "Enabled" answer counts as support, so a store that does not implement the API (many on-prem stores answer 404, 501 or an error), a bucket created
/// without object lock, or an unreachable store all read as "not supported" and the caller falls back to application-level immutability. Retention is set per object after it is stored.
/// </summary>
public sealed class VaultObjectLock(ObjectVault vault) : IWormStorage, IDisposable
{
    private readonly Lazy<AmazonS3Client> _client = new(() => ObjectVault.CreateClient(vault.Options));

    public async Task<WormCapability> DetectAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(vault.Options.RequestTimeout);
        try
        {
            var response = await _client.Value.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest { BucketName = vault.Options.Bucket }, timeout.Token);
            return response.ObjectLockConfiguration?.ObjectLockEnabled == ObjectLockEnabled.Enabled
                ? new WormCapability(true, "The bucket reports S3 Object Lock as enabled.")
                : new WormCapability(false, "The bucket was created without S3 Object Lock.");
        }
        catch (AmazonS3Exception e)
        {
            return new WormCapability(false, $"The store does not provide S3 Object Lock for this bucket (answer {(int)e.StatusCode} {e.ErrorCode}).");
        }
        catch (Exception e) when ((e is AmazonClientException or HttpRequestException or NotImplementedException) || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new WormCapability(false, $"The store could not be asked about S3 Object Lock ({e.GetType().Name}).");
        }
    }

    public async Task<WormApplied> ApplyRetentionAsync(string sha256, string mode, DateTimeOffset retainUntil, CancellationToken ct = default)
    {
        var lockMode = mode switch
        {
            "compliance" => ObjectLockRetentionMode.Compliance,
            "governance" => ObjectLockRetentionMode.Governance,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Use governance or compliance."),
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(vault.Options.RequestTimeout);
        await _client.Value.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = vault.Options.Bucket,
            Key = ObjectVault.KeyFor(sha256),
            Retention = new ObjectLockRetention { Mode = lockMode, RetainUntilDate = retainUntil.UtcDateTime },
        }, timeout.Token);
        return new WormApplied(mode, retainUntil);
    }

    public void Dispose()
    {
        if (_client.IsValueCreated) _client.Value.Dispose();
    }
}
