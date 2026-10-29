using System.Text.Json.Nodes;

namespace Keel.Core.Reports;

// Output and bulk work (ADR-0044). Keel.Reports reaches parts, BOMs, documents, workflow and change data only through these ports, implemented by the host that
// composes the owning modules. Every implementation evaluates the access policy itself, through the owning module's own read-filtered queries.

// ---------------------------------------------------------------- release packages

public sealed record PackageObjectRef(string ObjectType, string Key);

public sealed record PackageObject(string ObjectType, string Key, string Name, string Revision, string State, Markings Markings);

public sealed record PackageBomLine(int Level, string Path, int FindNumber, string Number, string Name, string Revision, string State, decimal Quantity, string Unit, string? Effectivity, Markings Markings);

public sealed record PackageFile(string FileName, long SizeBytes, string Sha256);

public sealed record PackageDocument(string Number, string Name, string DocumentType, string Revision, string State, string LinkedTo, Markings Markings, IReadOnlyList<PackageFile> Files);

public sealed record PackageSignature(string ObjectKey, string Revision, string SignerName, string Meaning, string Decision, DateTimeOffset SignedAt, string? Comment);

public sealed record PackageChange(string Number, string Type, string Title, string State, string? ChangeClass, string Reason, IReadOnlyList<string> Affected);

/// <summary>Everything a release package prints. <see cref="Objects"/> lists every object whose data is included (with its markings), so the aggregate banner and the access check cover all of them.</summary>
public sealed record ReleasePackageContent(
    string Kind, string Key, string Title, string Revision, IReadOnlyList<PackageObject> Objects, IReadOnlyList<PackageBomLine> Bom,
    IReadOnlyList<PackageDocument> Documents, IReadOnlyList<PackageSignature> Signatures, IReadOnlyList<PackageChange> Changes);

public interface IReleasePackageSource
{
    /// <summary>
    /// Collects the content for a released part or assembly ("part"), an ECN ("ecn") or a baseline ("baseline"). Refuses (Forbidden, with a count and no names) when any object
    /// the package needs is unreadable to the caller: a package is never silently redacted. NotFound for an unknown or unreadable subject; Conflict when it is not released.
    /// </summary>
    Task<Outcome<ReleasePackageContent>> CollectAsync(Subject subject, string kind, string key, string? correlationId, CancellationToken ct = default);

    /// <summary>True when the caller may read every one of the objects now.</summary>
    Task<bool> CanReadAllAsync(Subject subject, IReadOnlyList<PackageObjectRef> objects, CancellationToken ct = default);
}

// ---------------------------------------------------------------- BOM exchange

public static class BomExchangeKinds
{
    public const string Ebom = "ebom";
    public const string Mbom = "mbom";
}

/// <param name="Kind">ebom (the number is a part) or mbom (the number is an MBOM).</param>
/// <param name="Spec">latest, released or effectivity (EBOM only); effectivity needs <paramref name="Context"/> or a date and a serial or lot.</param>
public sealed record BomExchangeRequest(string Kind, string Number, string? Variant = null, string Spec = "released", int? Depth = null, string? Context = null, long? Serial = null, long? Lot = null, DateOnly? Date = null);

public sealed record BomExchangeLine(
    int Level, string Path, int FindNumber, string? Number, string? Name, string? Revision, string? State, decimal Quantity, string Unit, string? Effectivity, string? Condition,
    string? Notes, IReadOnlyList<string> Designators, bool Redacted, string? Banner, Markings? Markings);

public sealed record BomExchangeResult(string RootNumber, string Kind, string Description, IReadOnlyList<BomExchangeLine> Lines, string Banner, int RedactedCount);

public interface IBomExchangeSource
{
    /// <summary>The resolved, read-filtered structure. Lines the caller may not read are placeholders (find number, quantity, unit), as everywhere else (A4).</summary>
    Task<Outcome<BomExchangeResult>> ResolveAsync(Subject subject, BomExchangeRequest request, string? correlationId, CancellationToken ct = default);
}

// ---------------------------------------------------------------- mass change

public static class MassOperations
{
    public const string SetAttribute = "set_attribute";
    public const string ChangeMarkings = "change_markings";
    public const string BaselineProposal = "baseline_proposal";
    public const string SubmitReview = "submit_review";
    public const string Watch = "watch";
    public const string Favorite = "favorite";
    public const string CheckRead = "check_read";
}

public sealed record MassVerdict(bool CanApply, string? Reason, string? Name = null, string? Banner = null, string? State = null);

public interface IMassChangeTarget
{
    /// <summary>The object kind this target handles (part).</summary>
    string Kind { get; }

    IReadOnlyCollection<string> Operations { get; }

    /// <summary>For each key: the verdict, without changing anything. A key the caller may not read is refused with the same words as one that does not exist.</summary>
    Task<IReadOnlyDictionary<string, MassVerdict>> PreviewAsync(Subject subject, string operation, JsonObject parameters, IReadOnlyList<string> keys, CancellationToken ct = default);

    /// <summary>Applies the operation to one object through its normal service (its own audit event). The value is the data needed to undo, or null when it cannot be undone.</summary>
    Task<Outcome<JsonObject?>> ApplyAsync(Subject subject, string operation, JsonObject parameters, string key, string correlationId, CancellationToken ct = default);

    Task<Outcome<string>> UndoAsync(Subject subject, string operation, string key, JsonObject undo, string correlationId, CancellationToken ct = default);

    /// <summary>The subset of keys the caller may read now.</summary>
    Task<IReadOnlySet<string>> ReadableAsync(Subject subject, IReadOnlyList<string> keys, CancellationToken ct = default);
}

// ---------------------------------------------------------------- report datasets

public sealed record DatasetColumn(string Key, string Label, string Type, bool Sortable = true, bool Filterable = true, bool Groupable = true);

public static class FilterOps
{
    public const string Eq = "eq";
    public const string Contains = "contains";
    public const string StartsWith = "starts";
    public const string AtLeast = "gte";
    public const string AtMost = "lte";
    public static readonly IReadOnlyList<string> All = [Eq, Contains, StartsWith, AtLeast, AtMost];
}

public sealed record DatasetFilter(string Column, string Op, string Value);

public sealed record DatasetQuery(string? Text, string? Parameter, int Limit);

/// <summary>One row, a cell per column of the dataset in column order, and the row's markings (null for unmarked reference data).</summary>
public sealed record DatasetRow(IReadOnlyList<string?> Cells, Markings? Markings);

public sealed record DatasetResult(IReadOnlyList<DatasetRow> Rows, long Total, bool Truncated);

public interface IReportDataset
{
    string Id { get; }
    string Title { get; }
    string Description { get; }
    IReadOnlyList<DatasetColumn> Columns { get; }
    /// <summary>Non-null when the dataset needs one input (for example the root part of a BOM).</summary>
    string? ParameterLabel { get; }

    /// <summary>
    /// Rows the caller may read, from the owning module's access-filtered query (never raw SQL). Filtering by column, sorting and grouping are applied by the report
    /// service over these rows only, so they cannot reach anything the query did not return.
    /// </summary>
    Task<Outcome<DatasetResult>> QueryAsync(Subject subject, DatasetQuery q, string? corr, CancellationToken ct = default);
}
