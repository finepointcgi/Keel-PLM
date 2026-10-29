namespace Keel.Core;

/// <summary>What happened, for filter chips and icons. The page shows the kind as text as well as a symbol.</summary>
public static class TimelineKinds
{
    public const string Revision = "revision";
    public const string Iteration = "iteration";
    public const string Lifecycle = "lifecycle";
    public const string Signature = "signature";
    public const string Change = "change";
    public const string Baseline = "baseline";
    public const string Mbom = "mbom";
    public const string Link = "link";
    public const string Markings = "markings";
    public const string Restricted = "restricted";
    /// <summary>Nonconformance reports, corrective actions, deviations and waivers that name the object (8.1).</summary>
    public const string Quality = "quality";

    public static readonly IReadOnlyList<string> All =
        [Revision, Iteration, Lifecycle, Signature, Change, Baseline, Mbom, Link, Markings, Quality, Restricted];

    public static string Label(string kind) => kind switch
    {
        Revision => "Revisions",
        Iteration => "Check-ins",
        Lifecycle => "Lifecycle",
        Signature => "Signatures",
        Change => "Changes",
        Baseline => "Baselines",
        Mbom => "Manufacturing BOMs",
        Link => "Document links",
        Markings => "Markings",
        Quality => "Quality records",
        Restricted => "Restricted items",
        _ => kind,
    };
}

/// <summary>
/// One entry of an object's timeline. <see cref="Redacted"/> entries concern an object the caller may not read: they carry a time and the neutral
/// kind <see cref="TimelineKinds.Restricted"/> and nothing else (no number, name, markings or link).
/// <see cref="Order"/> breaks ties between entries with the same time, so a merged timeline is stable: it is (time, source rank, order).
/// <see cref="Markings"/> are those of the other object an entry names, so the page banner can cover it; the viewed object's own markings are not repeated.
/// </summary>
public sealed record TimelineEvent(
    DateTimeOffset At,
    string Kind,
    string Title,
    string? Detail,
    string? Actor,
    string? Revision,
    string? Href,
    bool Redacted,
    int SourceRank,
    long Order,
    Markings? Markings = null)
{
    public static TimelineEvent Restricted(DateTimeOffset at, int sourceRank, long order) =>
        new(at, TimelineKinds.Restricted, "A restricted item was involved", null, null, null, null, true, sourceRank, order);
}

/// <summary>
/// A source's answer. <see cref="Accessible"/> is true when this source itself found the viewed object and the caller may read it. The merged timeline is
/// available when at least one source says so (the sources that own the object); the entries of every source are then merged. When no source accepts the
/// object the page shows "not found" and every entry is dropped. Sources that only add entries about other objects (changes, baselines) answer
/// <c>Accessible = false</c> with their entries, and never authorize anything.
/// </summary>
public sealed record TimelineSlice(bool Accessible, IReadOnlyList<TimelineEvent> Events)
{
    public static readonly TimelineSlice NotAccessible = new(false, []);
    public static TimelineSlice Of(IEnumerable<TimelineEvent> events) => new(true, [.. events]);
}

/// <summary>
/// Gathers the timeline entries one module knows about an object (parts: check-ins and revisions; workflow: reviews and signatures; change: notices that
/// touched it; BOM: baselines, derived MBOMs and effectivity). Every implementation applies the caller's access itself, in its own module, and never
/// returns an identifier of an object the caller cannot read.
/// </summary>
public interface ITimelineSource
{
    /// <summary>Object kinds this source contributes to ("part", "document", "mbom").</summary>
    bool Handles(string objectType);

    /// <summary>Ordering rank among sources when two entries share a time.</summary>
    int Rank { get; }

    Task<TimelineSlice> GetAsync(Subject subject, string objectType, string key, string? correlationId, CancellationToken ct);
}
