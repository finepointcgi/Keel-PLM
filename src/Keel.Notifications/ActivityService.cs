using System.Globalization;
using System.Text.Json;
using Keel.Audit;
using Keel.Core;
using Npgsql;

namespace Keel.Notifications;

/// <summary>
/// The activity feed of one object, read from the tamper-evident audit log (ADR-0030, ADR-0005). The object is read through its owning module first and the
/// viewer's access evaluated on its markings: a viewer who may not read it gets "not found" (audited as a denial) and no row at all. Only the audit rows of THAT
/// object (object type and id, through the existing index) and only the actions of <see cref="ActivityCatalog"/> are read; views, searches and downloads are never
/// shown to ordinary viewers (they would reveal what colleagues looked at), and an auditor can switch them on, which is itself audited.
/// </summary>
public sealed class ActivityService(NpgsqlDataSource dataSource, ObjectAccessResolver objects, IAuditLog audit) : IActivityFeed
{
    public const int MaxTake = 100;
    public const int WatchedObjectsScanned = 25;
    public static readonly TimeSpan CollapseWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The one statement that reads the audit log for an object's feed. The (object_type, object_id, seq) index of audit.event (migration 0001) serves it: the rows of one
    /// object are walked newest first and the action list and outcome filter them. A test runs EXPLAIN on this text against 100,000 events (docs/increments/7.3-awareness.md).
    /// </summary>
    internal const string EventsSql =
        """
        SELECT seq, occurred_at, actor, action, outcome, payload FROM audit.event
        WHERE object_type = @t AND object_id = @n AND seq < @before AND action = ANY(@actions) AND (@all OR outcome = 'Success')
        ORDER BY seq DESC LIMIT @limit
        """;

    private sealed record RawEvent(long Seq, DateTimeOffset At, string Actor, string Action, string Outcome, string Payload);

    public async Task<Outcome<ActivityPage>> GetAsync(Subject subject, string kind, string number, long? before, int take, bool includeReads, string? correlationId, CancellationToken ct = default)
    {
        var info = await objects.FindAsync(kind, number, ct);
        if (info is null) return Outcome<ActivityPage>.NotFound();
        var auditType = AwarenessKinds.AuditObjectType(kind, number);
        if (!ObjectAccessResolver.CanRead(subject, info.Markings))
        {
            await audit.AppendAsync(new AuditEntry
            {
                Actor = ActorSnapshot.From(subject), Action = "activity.read", Outcome = AuditOutcome.Denied, ObjectType = auditType ?? kind, ObjectId = number, CorrelationId = correlationId,
                Details = AuditDetails.Of(("reasons", "object.not_readable")),
            }, ct);
            return Outcome<ActivityPage>.NotFound();
        }
        var isAuditor = subject.Roles.Contains(Roles.Auditor);
        var withViews = includeReads && isAuditor;
        if (auditType is null) return Outcome<ActivityPage>.Ok(new ActivityPage([], null, isAuditor));

        take = Math.Clamp(take, 1, MaxTake);
        var (raw, more) = await ReadEventsAsync(auditType, number, before, take, withViews, ct);
        var entries = await MapAsync(subject, kind, number, raw, ct);
        if (withViews)
            await audit.AppendAsync(new AuditEntry
            {
                Actor = ActorSnapshot.From(subject), Action = "activity.read", Outcome = AuditOutcome.Success, ObjectType = auditType, ObjectId = number, CorrelationId = correlationId,
                Details = AuditDetails.Of(("includeViews", "true"), ("returned", raw.Count.ToString(CultureInfo.InvariantCulture))),
            }, ct);
        return Outcome<ActivityPage>.Ok(new ActivityPage(ActivityCollapse.Collapse(entries, CollapseWindow), more && raw.Count > 0 ? raw[^1].Seq : null, isAuditor));
    }

    private async Task<(List<RawEvent> Rows, bool More)> ReadEventsAsync(string auditType, string number, long? before, int take, bool withViews, CancellationToken ct)
    {
        string[] actions = withViews ? [.. ActivityCatalog.EventActions, .. ActivityCatalog.Views.Keys] : [.. ActivityCatalog.EventActions];
        await using var cmd = dataSource.CreateCommand(EventsSql);
        cmd.Parameters.AddWithValue("t", auditType);
        cmd.Parameters.AddWithValue("n", number);
        cmd.Parameters.AddWithValue("before", before ?? long.MaxValue);
        cmd.Parameters.AddWithValue("actions", actions);
        cmd.Parameters.AddWithValue("all", withViews);
        cmd.Parameters.AddWithValue("limit", take + 1);
        var rows = new List<RawEvent>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new RawEvent(r.GetInt64(0), new DateTimeOffset(r.GetDateTime(1), TimeSpan.Zero), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)));
        var more = rows.Count > take;
        if (more) rows.RemoveAt(rows.Count - 1);
        return (rows, more);
    }

    private async Task<List<ActivityEntry>> MapAsync(Subject viewer, string kind, string number, List<RawEvent> rows, CancellationToken ct)
    {
        var entries = new List<ActivityEntry>();
        var objectCache = new Dictionary<string, ObjectInfo?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var (actorName, details) = ParsePayload(row.Payload, row.Actor);
            var isView = ActivityCatalog.Views.TryGetValue(row.Action, out var viewSentence);
            var rule = ActivityCatalog.Find(row.Action);
            if (rule is null && !isView) continue;
            var shown = new List<ActivityDetail>();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            string sentence;
            string category;
            if (rule is not null)
            {
                foreach (var f in rule.Fields)
                {
                    if (!details.TryGetValue(f.Key, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;
                    var value = Format(f, raw);
                    string? href = null;
                    if (f.Kind == FieldKind.ObjectRef)
                    {
                        var readable = await ReadableAnyAsync(viewer, raw, objectCache, ct);
                        (value, href) = readable is null ? ("An object you cannot see", null) : (readable.Number, AwarenessKinds.Path(readable.Kind, readable.Number));
                    }
                    values[f.Key] = value;
                    shown.Add(new ActivityDetail(f.Label, value, href));
                }
                sentence = Render(rule, actorName, values);
                category = rule.Category;
            }
            else
            {
                sentence = viewSentence!.Replace("{actor}", actorName, StringComparison.Ordinal);
                category = "view";
                if (row.Outcome != "Success") sentence += " (refused)";
            }
            entries.Add(new ActivityEntry(row.Seq, row.At, actorName, sentence, category, isView, shown, LinkFor(kind, number, row.Action, category, details)));
        }
        return entries;
    }

    private async Task<ObjectInfo?> ReadableAnyAsync(Subject viewer, string number, Dictionary<string, ObjectInfo?> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(number, out var hit)) return hit;
        ObjectInfo? found = null;
        foreach (var kind in AwarenessKinds.All)
            if (await objects.ReadableAsync(viewer, kind, number, ct) is { } info) { found = info; break; }
        return cache[number] = found;
    }

    private static string? LinkFor(string kind, string number, string action, string category, IReadOnlyDictionary<string, string?> details) => category switch
    {
        "comment" when action == "comment.create" && details.TryGetValue("comment", out var id) && Guid.TryParse(id, out var g) => $"{AwarenessKinds.Path(kind, number)}?comment={g:D}#comments",
        "review" => $"{AwarenessKinds.Path(kind, number)}#{AwarenessKinds.ReviewTab(kind)}",
        _ => null,
    };

    internal static (string Actor, IReadOnlyDictionary<string, string?> Details) ParsePayload(string payload, string fallbackActor)
    {
        var details = new Dictionary<string, string?>(StringComparer.Ordinal);
        var actor = fallbackActor;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("actor", out var a) && a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } name) actor = name;
            if (doc.RootElement.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object)
                foreach (var p in d.EnumerateObject()) details[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
        }
        catch (JsonException) { /* a payload we cannot read still yields its sentence without details */ }
        return (actor, details);
    }

    internal static string Format(ActivityField field, string raw) => field.Kind switch
    {
        FieldKind.State => ActivityCatalog.Prettify(raw),
        _ => field.Key switch
        {
            "decision" => raw switch { "approve" => "approved", "reject" => "rejected", _ => raw },
            "outcome" => raw.ToLowerInvariant(),
            "before" or "after" when raw.StartsWith('{') => MarkingsBanner(raw),
            _ => raw,
        },
    };

    /// <summary>
    /// Parts and documents record the markings of a change as a JSON description of the fields. The feed shows the banner those fields make (the object's own banner, before
    /// and after), never the field-by-field description.
    /// </summary>
    internal static string MarkingsBanner(string described)
    {
        try
        {
            using var doc = JsonDocument.Parse(described);
            var root = doc.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            IEnumerable<string> List(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString()).OfType<string>() : [];
            if (!Enum.TryParse<ExportJurisdiction>(Text("jurisdiction"), out var jurisdiction) || !Enum.TryParse<DistributionStatement>(Text("distribution"), out var distribution)) return "Markings changed";
            var created = Markings.Create(jurisdiction, distribution, Text("OwningOrg") ?? "-", Text("Eccn"), Text("UsmlCategory"), List("cui"), Text("ControllingOffice"), Text("DistributionReason"), Text("Program"), List("authorizations"));
            return created.IsSuccess ? created.Value!.BannerText() : "Markings changed";
        }
        catch (JsonException) { return "Markings changed"; }
    }

    internal static string Render(ActivityRule rule, string actor, IReadOnlyDictionary<string, string> values)
    {
        var template = rule.RequiredKeys.All(values.ContainsKey) ? rule.Sentence : rule.Fallback ?? rule.Sentence;
        var text = template.Replace("{actor}", actor, StringComparison.Ordinal);
        foreach (var (key, value) in values) text = text.Replace("{" + key + "}", value, StringComparison.Ordinal);
        // A placeholder the fallback still carries (the rule had no fallback) is dropped rather than shown.
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s*\{[A-Za-z]+\}", "").Trim();
    }

    // ---------------------------------------------------------------- watched objects (Home card)

    public async Task<IReadOnlyList<WatchedActivity>> WatchedActivityAsync(Subject subject, int take, CancellationToken ct = default)
    {
        var watched = new List<(string Kind, string Number)>();
        await using (var cmd = dataSource.CreateCommand("SELECT object_type, object_number FROM ntf.watch WHERE user_sub = @u ORDER BY created_at DESC, id DESC LIMIT @l"))
        {
            cmd.Parameters.AddWithValue("u", subject.Id);
            cmd.Parameters.AddWithValue("l", WatchedObjectsScanned);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) watched.Add((r.GetString(0), r.GetString(1)));
        }
        var items = new List<WatchedActivity>();
        foreach (var (kind, number) in watched)
        {
            var info = await objects.ReadableAsync(subject, kind, number, ct);
            if (info is null || AwarenessKinds.AuditObjectType(kind, number) is not { } auditType) continue;
            var (rows, _) = await ReadEventsAsync(auditType, number, null, 3, withViews: false, ct);
            foreach (var entry in await MapAsync(subject, kind, number, rows, ct))
                items.Add(new WatchedActivity(kind, number, info.Name, info.Markings.BannerText(), info.Markings, AwarenessKinds.Path(kind, number), entry));
        }
        return [.. items.OrderByDescending(i => i.Entry.At).ThenBy(i => i.Number, StringComparer.Ordinal).Take(take)];
    }
}

/// <summary>Collapses runs of small, repeated edits by the same person into one line (pure, unit-tested).</summary>
public static class ActivityCollapse
{
    private static readonly string[] Collapsible = ["edit", "structure"];

    /// <summary>Entries come newest first. Adjacent entries of a collapsible category by the same actor, each within <paramref name="window"/> of the previous one, become one with a counter.</summary>
    public static IReadOnlyList<ActivityEntry> Collapse(IReadOnlyList<ActivityEntry> entries, TimeSpan window)
    {
        var result = new List<ActivityEntry>();
        var counts = new List<int>();
        foreach (var e in entries)
        {
            if (result.Count > 0 && Collapsible.Contains(e.Category) && result[^1].Category == e.Category && result[^1].ActorName == e.ActorName
                && result[^1].At - e.At <= window && e.At <= result[^1].At)
            {
                counts[^1]++;
                continue;
            }
            result.Add(e);
            counts.Add(1);
        }
        return [.. result.Select((e, i) => counts[i] == 1 ? e : e with { Sentence = e.Sentence + $" ({counts[i].ToString(CultureInfo.InvariantCulture)} times)" })];
    }
}
