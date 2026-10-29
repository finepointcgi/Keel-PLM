namespace Keel.Core;

/// <summary>What the object store reports about S3 Object Lock. <see cref="Supported"/> is true only when the store answered that the bucket has object lock enabled.</summary>
public sealed record WormCapability(bool Supported, string Detail);

/// <summary>The retention that was applied to one object.</summary>
public sealed record WormApplied(string Mode, DateTimeOffset RetainUntil);

/// <summary>
/// Storage-level write-once protection (S3 Object Lock) as seen by modules that must not reference <c>Keel.Documents</c> (records management, 8.6). The
/// application never assumes it: <see cref="DetectAsync"/> asks the store, and a store that does not implement the API (or a bucket created without object
/// lock) answers <c>Supported = false</c>. Implemented by the vault over its S3 client.
/// </summary>
public interface IWormStorage
{
    Task<WormCapability> DetectAsync(CancellationToken ct = default);

    /// <summary>Sets a retention period on the stored object. <paramref name="mode"/> is <c>governance</c> or <c>compliance</c>. Throws when the store refuses.</summary>
    Task<WormApplied> ApplyRetentionAsync(string sha256, string mode, DateTimeOffset retainUntil, CancellationToken ct = default);
}
