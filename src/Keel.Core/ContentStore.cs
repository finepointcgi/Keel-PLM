namespace Keel.Core;

/// <summary>A stored blob: its SHA-256 (lowercase hex) is its address.</summary>
public sealed record ContentInfo(string Sha256, long SizeBytes);

/// <summary>
/// Content-addressed blob storage as seen by modules that must not reference Keel.Documents (the migration importer, ADR-0023).
/// Keel.Documents implements it over the object vault (ADR-0015).
/// </summary>
public interface IContentStore
{
    /// <summary>Streams the content in, hashing while it flows; returns the SHA-256 and size it stored.</summary>
    Task<ContentInfo> PutAsync(Stream content, CancellationToken ct = default);

    /// <summary>True when an object with this address and size exists.</summary>
    Task<bool> ExistsAsync(string sha256, long sizeBytes, CancellationToken ct = default);

    /// <summary>Reads the stored object back and checks hash and size (throws when it does not match).</summary>
    Task VerifyAsync(string sha256, long sizeBytes, CancellationToken ct = default);
}

/// <summary>The store refused the content (malware scan found a signature, or scanning is required and could not complete). Nothing was stored.</summary>
public sealed class ContentRejectedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A classification assignment staged by the migration importer: an item (by Keel part number) gets a class and attribute values.</summary>
public sealed record ClassAssignment(string PartNumber, string ClassCode, IReadOnlyDictionary<string, string?> Attributes);

/// <summary>
/// Loads migrated classification assignments. Classification itself is delivered with the admin module (Phase 2b); until it exists
/// the registered implementation throws <see cref="NotSupportedException"/> naming the phase (ADR-0023).
/// </summary>
public interface IClassificationLoader
{
    Task LoadAsync(Guid batchId, IReadOnlyList<ClassAssignment> assignments, CancellationToken ct = default);
}

/// <summary>Default <see cref="IClassificationLoader"/>: classification is not available yet.</summary>
public sealed class UnsupportedClassificationLoader : IClassificationLoader
{
    public Task LoadAsync(Guid batchId, IReadOnlyList<ClassAssignment> assignments, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Loading classification assignments needs the classification services of Phase 2b (Keel.Admin). " +
            "The assignments are staged and counted in the report; load them after Keel.Admin is merged by registering an IClassificationLoader.");
}
