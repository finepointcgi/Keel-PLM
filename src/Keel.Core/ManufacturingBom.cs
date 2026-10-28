namespace Keel.Core;

// ---------------------------------------------------------------- read API for other modules (ERP and TDP integration, Phase 4)

/// <summary>One EBOM line an MBOM line implements, with the declared relationship (matched, quantity_adjusted, substituted).</summary>
public sealed record ManufacturingBomLink(int EbomFindNumber, string Status, string? Reason);

/// <summary>
/// One line of a released MBOM with the child resolved: for a Make sub-assembly the released MBOM of the same plant it points at. A line the
/// caller may not read is redacted (A4): find number, quantity and unit only.
/// </summary>
public sealed record ManufacturingBomLine(
    int FindNumber, int Alt, bool Redacted, string? ChildNumber, string? ChildName, string? ChildSource, decimal Quantity, string Unit, decimal? ScrapFactor,
    bool Phantom, string? SupplyType, string? OperationReference, bool MbomOnly, IReadOnlyList<string> ReferenceDesignators, string? Notes, EffectivityRange? Effectivity,
    IReadOnlyList<ManufacturingBomLink> EbomLinks, string? ChildMbomNumber, string? ChildMbomRevision);

/// <summary>A released MBOM as an integration consumes it: header, the EBOM it was derived from, the reconciliation it was released with, and its lines.</summary>
public sealed record ManufacturingBomSnapshot(
    string MbomNumber, string PartNumber, string PartName, string Plant, string Revision, int Iteration, string State,
    string? DerivedFromEbom, string ContentSha256, Guid? ReconciliationRunId, string? ReconciliationStatus,
    Markings Markings, string Banner, IReadOnlyList<ManufacturingBomLine> Lines);

/// <summary>
/// Read access to released manufacturing BOMs for consumers that must not reference <c>Keel.Bom</c> (the integration module publishes MBOMs to ERP
/// through this, Phase 4). Implemented by Keel.Bom; applies the access policy and audits like every read.
/// </summary>
public interface IManufacturingBomSource
{
    /// <summary>
    /// The latest Released revision (or the named one) of the MBOM of a part in a plant, at its latest iteration, with resolved children.
    /// NotFound when there is none or the caller may not read it.
    /// </summary>
    Task<Outcome<ManufacturingBomSnapshot>> GetReleasedAsync(Subject subject, string partNumber, string plant, string? revision, string? correlationId, CancellationToken ct = default);
}

// ---------------------------------------------------------------- change impact (increment 2.3)

/// <summary>An MBOM derived from an EBOM that an open change revises. <see cref="StaleNow"/>: the part's EBOM has already moved past the iteration it was derived from.</summary>
public sealed record StaleMbom(
    string MbomNumber, string PartNumber, string Plant, string Revision, int Iteration, string State, string StateLabel,
    string DerivedFrom, string CurrentEbom, bool StaleNow, string Banner, Markings Markings);

/// <summary>MBOMs affected by a revised part, per plant. <see cref="Hidden"/> counts MBOMs the caller may not read (placeholders only).</summary>
public sealed record ManufacturingImpact(IReadOnlyList<StaleMbom> Mboms, int Hidden);

/// <summary>
/// Lets change management warn manufacturing engineering (ADR-0024): which MBOMs were derived from the EBOM of a part that a change revises and so
/// become stale. Implemented by Keel.Bom; <c>Keel.Change</c> only sees this interface.
/// </summary>
public interface IManufacturingImpactProvider
{
    string ObjectType { get; }

    Task<Outcome<ManufacturingImpact>> StaleMbomsAsync(Subject subject, string partKey, string? correlationId, CancellationToken ct = default);
}
