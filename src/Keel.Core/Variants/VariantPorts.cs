namespace Keel.Core.Variants;

/// <summary>
/// Gives modules that must not reference <c>Keel.Variants</c> (the Technical Data Package export, the pull API) the option choices of a named variant. The answer is for a
/// caller who may read the product; a product the caller cannot read, an unknown product and an unknown variant all answer NotFound.
/// </summary>
public interface IVariantSelectionSource
{
    Task<Outcome<VariantSelection>> ResolveAsync(Subject subject, string productNumber, string variantName, string? correlationId, CancellationToken ct);
}
