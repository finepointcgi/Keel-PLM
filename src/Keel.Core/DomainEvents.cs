using System.Data.Common;
using System.Text.Json.Nodes;

namespace Keel.Core;

/// <summary>
/// The event types of the ERP-facing contract (ADR-0025). Each type is versioned: a payload change that is not additive publishes a new
/// <see cref="CurrentVersion"/> and keeps the old schema under docs/integration/schemas. Adding a type (<c>mbom.released</c> was added this way)
/// is additive: put it here, give it a schema file and an emission point.
/// </summary>
public static class EventTypes
{
    public const string PartReleased = "part.released";
    public const string PartObsoleted = "part.obsoleted";
    public const string DocumentReleased = "document.released";
    public const string DocumentObsoleted = "document.obsoleted";
    public const string ChangeApproved = "change.approved";
    public const string ChangeReleased = "change.released";
    public const string BaselineEstablished = "baseline.established";
    public const string BomPublished = "bom.published";
    /// <summary>Optional multi-level snapshot, delivered only to sinks that opted in (<c>full_structure</c>).</summary>
    public const string BomStructurePublished = "bom.structure.published";
    /// <summary>A manufacturing BOM revision was released through the review workflow (additive, FUP-1).</summary>
    public const string MbomReleased = "mbom.released";
    /// <summary>A named variant of a product (a valid combination of options) was released (additive, ADR-0039).</summary>
    public const string VariantReleased = "variant.released";
    /// <summary>A program milestone was reached: a gate review was approved or a milestone was completed (additive, ADR-0043).</summary>
    public const string ProgramMilestoneReached = "program.milestone_reached";

    public static readonly IReadOnlyList<string> All =
    [
        PartReleased, PartObsoleted, DocumentReleased, DocumentObsoleted, ChangeApproved, ChangeReleased,
        BaselineEstablished, BomPublished, BomStructurePublished, MbomReleased, VariantReleased, ProgramMilestoneReached,
    ];

    /// <summary>Types a sink with an empty subscription list receives.</summary>
    public static readonly IReadOnlyList<string> Default = [.. All.Where(t => t != BomStructurePublished)];

    public static int CurrentVersion(string eventType) => All.Contains(eventType)
        ? 1
        : throw new ArgumentException($"Unknown event type '{eventType}'.", nameof(eventType));
}

/// <summary>
/// One domain event to write to the outbox. <see cref="Markings"/> must cover everything in <see cref="Payload"/>: they decide which
/// readers see the event and which sinks it may be delivered to (outbound release check).
/// </summary>
public sealed record DomainEvent(
    string EventType,
    string AggregateType,
    Guid? AggregateId,
    string AggregateNumber,
    string? AggregateRevision,
    Markings Markings,
    JsonObject Payload,
    DateTimeOffset OccurredAt,
    string Actor,
    string? CorrelationId = null)
{
    public int EventVersion { get; init; } = Keel.Core.EventTypes.CurrentVersion(EventType);
}

/// <summary>
/// Implemented by Keel.Integration. Modules call it inside the transaction of the business change they report, so the event commits or
/// rolls back with the change (transactional outbox, ADR-0011). A module that has no sink registered skips emission; the host registers one.
/// </summary>
public interface IDomainEventSink
{
    Task AppendAsync(DbConnection conn, DbTransaction tx, DomainEvent domainEvent, CancellationToken ct);
}
