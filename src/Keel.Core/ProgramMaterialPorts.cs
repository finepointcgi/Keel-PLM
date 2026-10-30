namespace Keel.Core;

/// <summary>One part of an exploded end item with the price the sourcing lists give for the quantity needed. <see cref="UnitPrice"/> is null when no readable, active, effective list entry prices it in the program currency.</summary>
public sealed record MaterialPartCost(string PartNumber, string Name, decimal Quantity, string Unit, decimal? UnitPrice, string? SourceEntry, string Basis);

/// <summary>
/// The material cost of building <see cref="BuildQuantity"/> of an end item, as the caller may read it (ADR-0049, A60). <see cref="Total"/> adds only priced parts;
/// <see cref="UnpricedLines"/> are parts without a usable price and <see cref="RestrictedLines"/> are structure lines the caller may not read (their cost is in no total).
/// <see cref="Markings"/> lists the markings of every object whose data went in, for the page banner.
/// </summary>
public sealed record MaterialEstimate(string PartNumber, string PartName, decimal BuildQuantity, decimal Total, int PricedLines, int UnpricedLines, int RestrictedLines,
    IReadOnlyList<MaterialPartCost> Lines, IReadOnlyList<Markings> Markings);

/// <summary>
/// Reads the material cost of an end item from the product structure and the sourcing price lists. Implemented by the host (which composes the BOM and sourcing modules), so
/// Keel.Programs references neither. Every implementation evaluates the access policy through the owning modules' read-filtered queries; an end item the caller cannot read is NotFound.
/// </summary>
public interface IProgramMaterialSource
{
    /// <summary>Prices the latest released structure of <paramref name="partNumber"/> (the latest revisions when none is released) for <paramref name="quantity"/> units on <paramref name="asOf"/>.</summary>
    Task<Outcome<MaterialEstimate>> EstimateAsync(Subject subject, string partNumber, decimal quantity, DateOnly asOf, string? correlationId, CancellationToken ct = default);
}
