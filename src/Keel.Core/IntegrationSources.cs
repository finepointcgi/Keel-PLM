using System.Data.Common;

namespace Keel.Core;

// Interfaces that let Keel.Integration (events and Technical Data Packages) read what the owning modules hold without referencing them.
// Each is implemented in a new file of the owning module (ADR-0001, ADR-0025).

/// <summary>One line of a released-BOM snapshot, as written into a <c>bom.published</c> event.</summary>
public sealed record PublishedBomLine(
    int Level,
    string Path,
    int FindNumber,
    int Alternate,
    decimal Quantity,
    decimal ExtendedQuantity,
    string Unit,
    IReadOnlyList<string> ReferenceDesignators,
    string? Notes,
    string? Effectivity,
    Guid ChildMasterId,
    string ChildNumber,
    string ChildName,
    string ChildPartType,
    /// <summary>Null when the child has no Released revision (<see cref="Unresolved"/>).</summary>
    string? ChildRevision,
    int? ChildIteration,
    string? ChildState,
    bool Unresolved,
    Markings ChildMarkings);

public sealed record PublishedBom(
    Guid AssemblyMasterId,
    Guid AssemblyRevisionId,
    string Number,
    string Name,
    string Revision,
    int Iteration,
    Markings AssemblyMarkings,
    IReadOnlyList<PublishedBomLine> Lines,
    /// <summary>True when the structure has more lines than the caller allowed; nothing is returned then (a partial BOM is never published).</summary>
    bool TooLarge);

/// <summary>
/// Builds the released-EBOM snapshot of an assembly revision inside the caller's transaction (the revision that was just released is not yet
/// visible to any other connection). Children are resolved at configuration spec LatestReleased. This is a system read: it returns every line
/// with its markings and leaves the clearance decision to the event's markings (ADR-0025).
/// </summary>
public interface IBomPublicationSource
{
    /// <summary>Null when the revision does not exist or its part is not an assembly. <paramref name="maxDepth"/> 1 = direct children only; null = whole structure.</summary>
    Task<PublishedBom?> BuildAsync(DbConnection conn, DbTransaction tx, Guid partRevisionId, int? maxDepth, int maxLines, CancellationToken ct);
}

// ---------------------------------------------------------------- Technical Data Package sources (all run as the exporting subject)

public sealed record TdpPart(
    Guid MasterId, Guid RevisionId, string Number, string Name, string PartType, string Source, string DefaultUnit,
    string Revision, string Iteration, string State, string Description, string AttributesJson, Markings Markings);

public interface ITdpPartSource
{
    /// <summary>Evaluates Read, audits the access (including a denial) and returns the part revision; NotFound when it is not readable.</summary>
    Task<Outcome<TdpPart>> GetAsync(Subject subject, string number, string? revision, string? correlationId, CancellationToken ct);
}

public sealed record TdpStructureLine(
    int Level, string Path, int FindNumber, decimal Quantity, decimal ExtendedQuantity, string Unit,
    string Number, string Name, string PartType, string? Revision, int? Iteration, string? State,
    IReadOnlyList<string> ReferenceDesignators, string? Notes, string? Effectivity, bool Unresolved, Markings Markings);

public sealed record TdpStructure(
    string RootNumber, string RootRevision, string Configuration, IReadOnlyList<TdpStructureLine> Lines, int RedactedCount, int UnresolvedCount);

public sealed record TdpDocumentRef(string Number, string Revision, string LinkType, string? PartNumber);

public sealed record TdpBaseline(
    string Number, string Name, string Type, string State, string RootNumber, string RootRevision, string? Milestone,
    string ContentSha256, Markings Markings, IReadOnlyList<TdpDocumentRef> Documents, int RedactedDocuments);

public interface ITdpStructureSource
{
    /// <summary>The structure of the part revision with every child at its latest Released revision. Lines the subject cannot read are counted in <see cref="TdpStructure.RedactedCount"/>.</summary>
    Task<Outcome<TdpStructure>> GetPartStructureAsync(Subject subject, string number, string? revision, string? correlationId, CancellationToken ct);

    /// <summary>The frozen items of a baseline.</summary>
    Task<Outcome<TdpStructure>> GetBaselineStructureAsync(Subject subject, string baselineNumber, string? correlationId, CancellationToken ct);

    Task<Outcome<TdpBaseline>> GetBaselineAsync(Subject subject, string baselineNumber, string? correlationId, CancellationToken ct);
}

public sealed record TdpDocumentFile(Guid FileId, string Role, string FileName, string MediaType, long SizeBytes, string Sha256);

public sealed record TdpDocument(
    string Number, string Name, string DocumentType, string Revision, string State, string IterationLabel,
    Markings Markings, IReadOnlyList<TdpDocumentFile> Files);

/// <summary>A marked file ready to stream: PDFs stamped with banners and the distribution statement, everything else inside the marked ZIP wrapper (A10).</summary>
public sealed record TdpMarkedFile(string FileName, string ContentType, string Banner, string Format, Func<Stream, CancellationToken, Task> WriteToAsync);

public interface ITdpDocumentSource
{
    /// <summary>Evaluates Read and audits; the files of the latest iteration of <paramref name="revision"/> (default: latest revision).</summary>
    Task<Outcome<TdpDocument>> GetAsync(Subject subject, string number, string? revision, string? correlationId, CancellationToken ct);

    /// <summary>The existing marked-download pipeline: needs Download, verifies the vault object, audits <c>document.download</c>.</summary>
    Task<Outcome<TdpMarkedFile>> OpenMarkedFileAsync(Subject subject, string number, Guid fileId, string? correlationId, CancellationToken ct);
}
