using System.Globalization;
using Keel.Audit;
using Keel.Core;

namespace Keel.Web.Services.Visuals;

/// <summary>The merged timeline of one object. <see cref="Shown"/> are the markings of the OTHER objects some entries name, so a page banner can cover them.</summary>
public sealed record TimelineResult(string ObjectType, string Key, IReadOnlyList<TimelineEvent> Events, IReadOnlyList<Markings> Shown);

/// <summary>
/// Merges the entries every module contributes through <see cref="ITimelineSource"/> into one list ordered by time (ADR-0029). The order is total and stable:
/// (time, source rank, the source's own order), so two reads of the same data give the same list. Each source applies the caller's access in its own module; this
/// service adds nothing of its own except the decision that the whole timeline is "not found" when no owning source accepted the object, and the audit event.
/// </summary>
public sealed class TimelineService(IEnumerable<ITimelineSource> sources, IAuditLog audit)
{
    public const string Action = "timeline.read";

    public async Task<Outcome<TimelineResult>> GetAsync(Subject subject, string objectType, string key, string? correlationId, CancellationToken ct = default)
    {
        var accessible = false;
        var all = new List<TimelineEvent>();
        foreach (var source in sources.Where(s => s.Handles(objectType)).OrderBy(s => s.Rank))
        {
            var slice = await source.GetAsync(subject, objectType, key, correlationId, ct);
            accessible |= slice.Accessible;
            all.AddRange(slice.Events);
        }
        if (!accessible)
        {
            await audit.AppendAsync(Entry(subject, objectType, key, AuditOutcome.Denied, correlationId, ("reasons", "object.not_readable")), ct);
            return Outcome<TimelineResult>.NotFound();
        }

        var merged = Merge(all);
        var shown = merged.Where(e => e.Markings is not null).Select(e => e.Markings!).Distinct().ToList();
        await audit.AppendAsync(Entry(subject, objectType, key, AuditOutcome.Success, correlationId,
            ("events", merged.Count.ToString(CultureInfo.InvariantCulture)), ("restricted", merged.Count(e => e.Redacted).ToString(CultureInfo.InvariantCulture))), ct);
        return Outcome<TimelineResult>.Ok(new TimelineResult(objectType, key, merged, shown));
    }

    /// <summary>Time, then source rank, then the source's own order: stable for equal times.</summary>
    public static IReadOnlyList<TimelineEvent> Merge(IEnumerable<TimelineEvent> events) =>
        [.. events.OrderBy(e => e.At).ThenBy(e => e.SourceRank).ThenBy(e => e.Order)];

    private static AuditEntry Entry(Subject s, string objectType, string key, AuditOutcome outcome, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s),
        Action = Action,
        Outcome = outcome,
        ObjectType = objectType,
        ObjectId = key,
        CorrelationId = correlationId,
        Details = AuditDetails.Of(details),
    };
}
