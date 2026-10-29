namespace Keel.Core;

/// <summary>
/// Reads a stored blob back, for modules that must not reference <c>Keel.Documents</c> (the supplier portal's response files, 8.2). Implemented by the vault's
/// content store. The stream is verified against its hash and size; the caller has already decided that the reader may have the file.
/// </summary>
public interface IContentReader
{
    Task<Stream> OpenReadAsync(string sha256, long sizeBytes, CancellationToken ct = default);
}

/// <summary>What the Quality module can say about a record a supplier points at. Never contains markings: the module decides readability for the caller.</summary>
public sealed record QualityReference(string Kind, string Id, string Label, string? Href, string StateLabel);

/// <summary>
/// Implemented by the Quality module (built in parallel to Sourcing) so the supplier page can show what an opaque quality reference (a nonconformance, a
/// corrective action, ...) is called, without Sourcing referencing Quality. A reference nobody resolves is shown by its kind and id only. Returns null when the
/// record does not exist or the caller may not read it (so existence is not disclosed, ADR-0004).
/// </summary>
public interface IQualityReferenceResolver
{
    /// <summary>The kinds this resolver knows (for example <c>ncr</c>).</summary>
    bool Handles(string kind);

    Task<QualityReference?> ResolveAsync(Subject subject, string kind, string id, CancellationToken ct = default);
}
