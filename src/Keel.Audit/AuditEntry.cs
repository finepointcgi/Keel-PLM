using System.Collections.Immutable;
using Keel.Core;

namespace Keel.Audit;

public enum AuditOutcome
{
    Success,
    Denied,
    Failure,
}

/// <summary>Security-relevant attributes of the actor at the time of the event.</summary>
public sealed record ActorSnapshot(
    string Id,
    string? Name,
    bool UsPerson,
    string? Citizenship,
    string? Org,
    ImmutableSortedSet<string> Programs,
    ImmutableSortedSet<string> ExportAuthorizations,
    ImmutableSortedSet<string> Roles)
{
    /// <summary>Actor for events raised by the system itself (migrations, checkpoints, background jobs).</summary>
    public static readonly ActorSnapshot System = new("system:keel", "Keel system", true, null, null, [], [], []);

    public static ActorSnapshot From(Subject s) =>
        new(s.Id, s.Username, s.UsPerson, s.Citizenship, s.Org, s.Programs, s.ExportAuthorizations, s.Roles);
}

/// <summary>
/// One audit event to append. <see cref="Action"/> is a dotted verb such as <c>part.read</c>,
/// <c>document.download</c>, <c>workflow.sign</c>, <c>access.denied</c>.
/// </summary>
public sealed record AuditEntry
{
    public required ActorSnapshot Actor { get; init; }
    public required string Action { get; init; }
    public required AuditOutcome Outcome { get; init; }
    public string? ObjectType { get; init; }
    public string? ObjectId { get; init; }
    public string? CorrelationId { get; init; }
    public ImmutableSortedDictionary<string, string?> Details { get; init; } = ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal);
}

/// <summary>An event as stored in <c>audit.event</c>.</summary>
public sealed record AuditRecord(
    long Seq,
    Guid Id,
    DateTimeOffset OccurredAt,
    string Actor,
    string Action,
    string? ObjectType,
    string? ObjectId,
    AuditOutcome Outcome,
    string? CorrelationId,
    string Payload,
    byte[] PrevHash,
    byte[] Hash);
