using System.Data.Common;
using System.Text.Json.Nodes;

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

/// <summary>A class of the classification hierarchy as it exists in Keel: its own attributes (the effective set adds the ancestors').</summary>
public sealed record ClassDefinition(string Code, string? ParentCode, bool Active, IReadOnlyList<AttributeSpec> OwnAttributes);

/// <summary>A class the migration wants created (from the staged hierarchy, format keel-classification/1).</summary>
public sealed record ClassNodeSpec(string Code, string Name, string? Description, string? ParentCode, int SortOrder, IReadOnlyList<AttributeSpec> Attributes);

/// <summary>The classification a part iteration is frozen with.</summary>
public sealed record ClassAssignment(string ClassCode, JsonObject Attributes);

/// <summary>The stored class data of one part iteration (class code, canonical values JSON, number of typed value rows), for reconciliation.</summary>
public sealed record ClassifiedIteration(Guid IterationId, string ClassCode, string AttributesJson, int ValueRows);

/// <summary>The loader refused an assignment (unknown or retired class, invalid values). The importer turns it into a rejected object.</summary>
public sealed class ClassificationLoadException(string message) : Exception(message);

/// <summary>
/// Loads migrated classification into Keel (ADR-0023, ADR-0022), implemented by Keel.Admin against the real classification tables so imported rows are the same
/// rows native check-in writes. All calls run in the importer's transaction (the dry run's transaction is rolled back).
/// </summary>
public interface IClassificationLoader
{
    /// <summary>The classes and their own attributes that exist now, for validation and conflict reports.</summary>
    Task<IReadOnlyList<ClassDefinition>> LoadDefinitionsAsync(DbConnection conn, DbTransaction? tx, CancellationToken ct);

    /// <summary>Creates the classes (and their attributes) that do not exist yet, parents first. Existing classes are never changed. Returns the codes created.</summary>
    Task<IReadOnlyList<string>> CreateMissingClassesAsync(DbConnection conn, DbTransaction tx, IReadOnlyList<ClassNodeSpec> nodes, string actor, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Freezes class and values into a part iteration (the class data is iteration-owned): the class row and one typed value row per value, exactly as check-in does.
    /// Must be called before the revision is locked. Values are validated against the class's effective attribute set again.
    /// </summary>
    Task AssignAsync(DbConnection conn, DbTransaction tx, Guid partIterationId, ClassAssignment assignment, CancellationToken ct);

    /// <summary>The stored class data of the given iterations (iterations without a class are not returned), for reconciliation.</summary>
    Task<IReadOnlyList<ClassifiedIteration>> ReadAsync(DbConnection conn, IReadOnlyCollection<Guid> partIterationIds, CancellationToken ct);
}
