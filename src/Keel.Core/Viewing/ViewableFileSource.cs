namespace Keel.Core.Viewing;

/// <summary>
/// A document file as the viewer sees it: facts for the page plus the markings the banner and notices come from. Constructed only by the owning
/// module after it has evaluated read AND download rights (ADR-0027: viewing is downloading).
/// </summary>
public sealed record ViewableFile(
    string DocumentNumber,
    string DocumentName,
    Guid FileId,
    string FileName,
    string StoredMediaType,
    long SizeBytes,
    string Sha256,
    string IterationLabel,
    Markings Markings,
    string Banner,
    IReadOnlyList<string> Notices);

/// <summary>Verified bytes of a file for the viewer. <see cref="Marked"/> is true when the bytes are the stamped PDF a download would deliver.</summary>
public sealed record ViewContent(ViewableFile File, byte[] Bytes, bool Marked);

public enum ViewOutcome
{
    /// <summary>The file was delivered to the viewer.</summary>
    Delivered,
    /// <summary>The viewer refused after authorization (too large, no preview, a gate said no); written as a denial.</summary>
    Refused,
}

/// <summary>What was shown (or refused), written to the audit log as <c>document.view</c>.</summary>
public sealed record ViewRecord(string ViewerType, string Format, string Handling, ViewOutcome Outcome, string? Note = null);

/// <summary>The stored bytes of a file no longer match their recorded SHA-256 or size. Audited by the owner before it is thrown; the file is not delivered.</summary>
public sealed class StoredFileIntegrityException(string message) : Exception(message);

/// <summary>
/// The owning module's side of the file viewer (implemented in Keel.Documents; ADR-0027). It owns access control, integrity verification, PDF marking and
/// the audit trail of what is viewed; the viewer module owns type detection, limits and delivery.
/// </summary>
public interface IViewableFileSource
{
    /// <summary>
    /// Authorizes viewing (Read and Download on the document markings) and describes the file. Callers who may not read the document get NotFound, exactly
    /// as for downloads; any denial is audited as <c>document.view</c>. Success is not audited: the facts returned are a subset of the document page, which is.
    /// </summary>
    Task<Outcome<ViewableFile>> DescribeAsync(Subject subject, string number, Guid fileId, string? correlationId, CancellationToken ct = default);

    /// <summary>
    /// Authorizes again, reads the whole object verifying its SHA-256, and for PDFs returns the stamped bytes of the marked pipeline (a PDF that cannot be
    /// stamped is refused, never delivered unmarked). Throws <see cref="StoredFileIntegrityException"/> after auditing the failure.
    /// </summary>
    Task<Outcome<ViewContent>> ReadAsync(Subject subject, string number, Guid fileId, string? correlationId, CancellationToken ct = default);

    /// <summary>Reads the first bytes (unverified) so a file's type can be shown before its content is loaded. Advisory only; <see cref="ReadAsync"/> verifies.</summary>
    Task<byte[]> ReadHeadAsync(ViewableFile file, int count, CancellationToken ct = default);

    /// <summary>Appends the <c>document.view</c> audit event for a view that was delivered, or refused after authorization.</summary>
    Task RecordAsync(Subject subject, ViewableFile file, ViewRecord record, string? correlationId, CancellationToken ct = default);

    /// <summary>The primary file of the latest iteration of a document's latest (or the given) revision, after the same authorization as <see cref="DescribeAsync"/>.</summary>
    Task<Outcome<ViewableFile>> FindPrimaryAsync(Subject subject, string number, string? revision, string? correlationId, CancellationToken ct = default);
}

public sealed record ViewGateDecision(bool Allowed, string? Reason)
{
    public static readonly ViewGateDecision Allow = new(true, null);
    public static ViewGateDecision Refuse(string reason) => new(false, reason);
}

/// <summary>
/// A check that can refuse a view before any byte leaves the server (for example "this file is quarantined or not yet scanned", compliance wave).
/// Register implementations in DI; the viewer calls every gate in one place, <c>ViewerService</c>, and refuses on the first refusal.
/// </summary>
public interface IFileViewGate
{
    ValueTask<ViewGateDecision> CheckAsync(ViewableFile file, CancellationToken ct = default);
}
