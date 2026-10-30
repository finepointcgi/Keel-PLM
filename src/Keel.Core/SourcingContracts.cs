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

/// <summary>
/// One nonconformance or corrective action as a supplier scorecard needs to count it (9.6): when it was opened, when it was closed, its severity or priority and its markings.
/// <see cref="Severity"/> is Minor, Major or Critical for a nonconformance and null for a corrective action. <see cref="Cancelled"/> records are returned (so the caller can tell a
/// cancelled record from one it may not read) and are never counted. Never a title or text.
/// </summary>
public sealed record SupplierQualityFact(string Kind, string Id, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, string? Severity, Markings Markings, bool Cancelled = false);

/// <summary>
/// Implemented by the Quality module so the supplier scorecard can count the records a supplier is linked to without Sourcing referencing Quality. Returns facts ONLY for records
/// the subject may read (the same read policy as the Quality module's own lists), so a record the caller cannot read is not counted and leaves no sign. Kinds it does not
/// know are ignored.
/// </summary>
public interface ISupplierQualityFacts
{
    Task<IReadOnlyList<SupplierQualityFact>> ReadAsync(Subject subject, IReadOnlyList<(string Kind, string Id)> references, CancellationToken ct = default);
}
