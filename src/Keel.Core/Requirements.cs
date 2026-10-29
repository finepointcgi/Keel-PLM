namespace Keel.Core;

// ---------------------------------------------------------------- requirements and the digital thread (ADR-0038)

/// <summary>
/// A requirement linked to an object (a part, document or change) as seen from that object. A <see cref="Redacted"/> entry stands for a requirement the caller may not read:
/// it carries nothing but the fact that a link exists (no specification, key, title, link type or count).
/// </summary>
public sealed record TracedRequirement(
    bool Redacted, string? SpecNumber, string? Key, string? Title, string? LinkType, string? LinkLabel, bool Suspect, string? VerificationStatus, string? Banner, Markings? Markings);

/// <summary>The requirements that trace to one object. The list is what the caller may read plus one placeholder per restricted link.</summary>
public sealed record RequirementImpact(IReadOnlyList<TracedRequirement> Requirements);

/// <summary>
/// Lets change management list the requirements an affected part carries (allocated-to links and their verification state) without referencing
/// <c>Keel.Requirements</c>. Implemented by Keel.Requirements; additive, like <see cref="IManufacturingImpactProvider"/>.
/// </summary>
public interface IRequirementImpactProvider
{
    Task<Outcome<RequirementImpact>> AffectedRequirementsAsync(Subject subject, string objectType, string key, string? correlationId, CancellationToken ct = default);
}

/// <summary>A part and revision that a baseline contains.</summary>
public sealed record BaselinePart(string Number, string Revision);

/// <summary>
/// The parts of a baseline the caller may read, so requirements coverage can be stated for a baseline. Implemented by Keel.Bom (the only module that reads baseline items);
/// parts the caller may not read are not returned and not counted.
/// </summary>
public interface IBaselineMembership
{
    Task<Outcome<IReadOnlyList<BaselinePart>>> ReadablePartsAsync(Subject subject, string baselineNumber, string? correlationId, CancellationToken ct = default);
}
