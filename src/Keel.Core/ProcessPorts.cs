namespace Keel.Core;

/// <summary>A revision of an MBOM as manufacturing process planning sees it (increment 8.7, ADR-0042).</summary>
public sealed record MbomRevisionRef(string Revision, string State, int Iteration, string ContentSha256);

/// <summary>The header of an MBOM the caller may read, with its latest Released revision.</summary>
public sealed record MbomForProcess(
    Guid MbomId, string MbomNumber, string PartNumber, string PartName, string Plant, Markings Markings, Markings PartMarkings, string Banner, MbomRevisionRef? LatestReleased);

/// <summary>One line of an MBOM revision. A line the caller may not read is redacted: find number, alternate, quantity and unit only.</summary>
public sealed record MbomLineRef(
    int Find, int Alt, bool Redacted, Guid? ChildMasterId, string? ChildNumber, string? ChildName, decimal Quantity, string Unit, bool Phantom, string? SupplyType, Markings? Markings = null);

/// <summary>
/// What process planning (Keel.Process) may ask of the MBOM module (Keel.Bom owns the <c>mbom</c> schema). Every method applies the access policy on the MBOM
/// and on its part; an MBOM the caller may not read looks like a missing one.
/// </summary>
public interface IMbomProcessSource
{
    /// <summary>The MBOM with this number, or null when it does not exist or the caller may not read it (denial audited by the owner).</summary>
    Task<MbomForProcess?> FindAsync(Subject subject, string mbomNumber, string? correlationId, CancellationToken ct = default);

    /// <summary>Batch form for lists: the readable MBOMs among the numbers (no audit; the caller audits its own list read).</summary>
    Task<IReadOnlyDictionary<string, MbomForProcess>> ReadableAsync(Subject subject, IReadOnlyCollection<string> mbomNumbers, CancellationToken ct = default);

    /// <summary>System check without a caller (the review engine releasing a plan): the latest Released revision of the MBOM, or null. Shown to nobody.</summary>
    Task<MbomRevisionRef?> LatestReleasedAsync(string mbomNumber, CancellationToken ct = default);

    /// <summary>The active plant codes (the plants of the MBOM module), for work centers and tools.</summary>
    Task<IReadOnlyList<string>> ActivePlantsAsync(CancellationToken ct = default);

    /// <summary>The lines of a Released or Obsolete revision (latest iteration), with redaction. NotFound when unreadable, missing or a draft.</summary>
    Task<Outcome<IReadOnlyList<MbomLineRef>>> LinesAsync(Subject subject, string mbomNumber, string revision, string? correlationId, CancellationToken ct = default);
}
