using Keel.Core;
using Keel.Core.Geometry;

namespace Keel.Documents;

/// <summary>
/// One version (iteration) of a model document as the version history shows it (ADR-0046). <see cref="Metrics"/> is what the server measured in the
/// primary file; it is null while the measurement is still pending (the first view measures a bounded number of versions) and for files that are not models.
/// </summary>
public sealed record ModelVersionRow(
    Guid IterationId,
    string Revision,
    int Iteration,
    string Label,
    bool IsCurrent,
    string State,
    string StateLabel,
    bool RevisionLocked,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string? ChangeNote,
    Guid? FileId,
    string? FileName,
    long? SizeBytes,
    string? Sha256,
    ModelMetrics? Metrics,
    bool SameGeometryAsPrevious);

public sealed record ModelVersionHistory(
    string Number,
    string Name,
    string Banner,
    Markings Markings,
    IReadOnlyList<ModelVersionRow> Versions,
    string CurrentLabel,
    string LatestRevision,
    string LatestRevisionState,
    bool LatestRevisionLocked,
    bool CheckedOut,
    string? CheckedOutBy,
    bool CheckedOutByMe,
    int LatestIteration,
    bool CanModify,
    bool CanDownload,
    bool CanAdminister,
    int PendingMeasurements);

/// <summary>One side of a comparison: the version and what was measured in it.</summary>
public sealed record ModelCompareSide(
    Guid IterationId, string Label, string State, DateTimeOffset CreatedAt, string CreatedBy, string? ChangeNote,
    Guid? FileId, string? FileName, long? SizeBytes, string? Sha256, ModelMetrics Metrics);

public sealed record ModelComparison(
    string Number,
    string Name,
    string Banner,
    Markings Markings,
    ModelCompareSide A,
    ModelCompareSide B,
    ModelDelta Delta,
    bool SameBytes,
    /// <summary>The visual comparison needs both versions to be STL, OBJ or 3MF and the caller to be allowed to download.</summary>
    bool VisualAvailable,
    string? VisualUnavailableReason);
