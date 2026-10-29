using System.Globalization;
using System.Text.Json;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Documents;

/// <summary>
/// Timeline entries owned by the Documents module (UX-3, ADR-0029). For a document: revisions, check-ins, manual lifecycle changes and markings changes.
/// For a part: the document links that appeared or disappeared between its iterations; a linked document the caller may not read is a neutral
/// "A restricted item was involved" entry with no number or name. Access to the viewed object is checked here, with the same policy the services use.
/// </summary>
public sealed class DocumentTimelineSource(NpgsqlDataSource dataSource) : ITimelineSource
{
    public const int RankValue = 20;
    public int Rank => RankValue;
    public bool Handles(string objectType) => objectType is DocumentService.ObjectType or "part";

    public Task<TimelineSlice> GetAsync(Subject subject, string objectType, string key, string? correlationId, CancellationToken ct) =>
        objectType == "part" ? PartLinksAsync(subject, key, ct) : DocumentLifeAsync(subject, key, ct);

    private async Task<TimelineSlice> DocumentLifeAsync(Subject subject, string key, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        Guid masterId;
        string lifecycleId;
        await using (var cmd = new NpgsqlCommand($"SELECT m.id, m.lifecycle_id, {MarkingColumns.Select("m")} FROM plm.document_master m WHERE m.number = @n", conn))
        {
            cmd.Parameters.AddWithValue("n", key);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return TimelineSlice.NotAccessible;
            if (!AccessPolicy.Evaluate(subject, MarkingColumns.Read(r, 2), PlmAction.Read).Permitted) return TimelineSlice.NotAccessible;
            (masterId, lifecycleId) = (r.GetGuid(0), r.GetString(1));
        }
        var lifecycle = await Lifecycle.LoadAsync(conn, null, lifecycleId, ct);
        string Label(string? state) => state is null ? "" : lifecycle?.State(state)?.Label ?? state;
        var events = new List<TimelineEvent>();
        long order = 0;
        var href = $"documents/{Uri.EscapeDataString(key)}";

        await using (var cmd = new NpgsqlCommand(
            """
            SELECT r.revision, r.created_at, r.created_by, i.iteration, i.created_at, i.created_by, i.comment
            FROM plm.document_revision r JOIN plm.document_iteration i ON i.revision_id = r.id
            WHERE r.master_id = @m ORDER BY r.created_at, r.id, i.iteration
            """, conn))
        {
            cmd.Parameters.AddWithValue("m", masterId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (await r.ReadAsync(ct))
            {
                var rev = r.GetString(0);
                var revHref = $"{href}?revision={Uri.EscapeDataString(rev)}#history";
                if (seen.Add(rev))
                    events.Add(new TimelineEvent(At(r, 1), TimelineKinds.Revision, $"Revision {rev} created", null, r.GetString(2), rev, revHref, false, Rank, order++));
                events.Add(new TimelineEvent(At(r, 4), TimelineKinds.Iteration, $"Version {rev}.{r.GetInt32(3).ToString(CultureInfo.InvariantCulture)} checked in",
                    r.IsDBNull(6) ? null : r.GetString(6), r.GetString(5), rev, revHref, false, Rank, order++));
            }
        }

        await using (var cmd = new NpgsqlCommand(
            """
            SELECT occurred_at, action, payload FROM audit.event
            WHERE object_type = 'document' AND object_id = @n AND outcome = 'Success' AND action IN ('document.transition', 'document.set_state', 'document.markings_change')
            ORDER BY seq
            """, conn))
        {
            cmd.Parameters.AddWithValue("n", key);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                using var doc = JsonDocument.Parse(r.GetString(2));
                var actor = doc.RootElement.TryGetProperty("actor", out var a) && a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                var details = doc.RootElement.GetProperty("details");
                string? D(string name) => details.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var at = new DateTimeOffset(r.GetDateTime(0), TimeSpan.Zero);
                var rev = D("revision");
                var action = r.GetString(1);
                events.Add(action == "document.markings_change"
                    ? new TimelineEvent(at, TimelineKinds.Markings, "Security markings changed", null, actor, rev, $"{href}#security", false, Rank, order++)
                    : new TimelineEvent(at, TimelineKinds.Lifecycle,
                        $"{(action == "document.set_state" ? "State set to" : "Moved to")} {Label(D("to"))}", $"From {Label(D("from"))}{(action == "document.set_state" ? " (administrator override)" : "")}",
                        actor, rev, rev is null ? href : $"{href}?revision={Uri.EscapeDataString(rev)}#overview", false, Rank, order++));
            }
        }
        return TimelineSlice.Of(events);
    }

    /// <summary>One document link of a part iteration. <see cref="Number"/> is null when the caller may not read the document (the read filter removed it from the join).</summary>
    private sealed record LinkRow(string Revision, string Type, Guid DocMasterId, string? Number, string? Name, Markings? Markings);

    private async Task<TimelineSlice> PartLinksAsync(Subject subject, string key, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        Guid masterId;
        await using (var cmd = new NpgsqlCommand($"SELECT m.id, {MarkingColumns.Select("m")} FROM plm.part_master m WHERE m.number = @n", conn))
        {
            cmd.Parameters.AddWithValue("n", key);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return TimelineSlice.NotAccessible;
            if (!AccessPolicy.Evaluate(subject, MarkingColumns.Read(r, 1), PlmAction.Read).Permitted) return TimelineSlice.NotAccessible;
            masterId = r.GetGuid(0);
        }

        // Iterations in life order, each with its links. The read filter sits in the join: a document the caller may not read comes back with a link row but no
        // document columns, so it can only become a neutral entry (ADR-0029).
        var filter = AccessPolicy.BuildReadFilter(subject, "d");
        var iterations = new List<(string Revision, int Iteration, DateTimeOffset At, string By, List<LinkRow> Links)>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT r.revision, i.iteration, i.created_at, i.created_by, l.link_type, l.document_master_id, d.id, d.number, d.name, {MarkingColumns.Select("d")}
            FROM plm.part_revision r
            JOIN plm.part_iteration i ON i.revision_id = r.id
            LEFT JOIN plm.part_document_link l ON l.part_iteration_id = i.id
            LEFT JOIN plm.document_master d ON d.id = l.document_master_id AND {filter.Sql}
            WHERE r.master_id = @m
            ORDER BY r.created_at, r.id, i.iteration, l.link_type, l.document_master_id
            """, conn))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("m", masterId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var rev = r.GetString(0);
                var it = r.GetInt32(1);
                if (iterations.Count == 0 || iterations[^1].Revision != rev || iterations[^1].Iteration != it)
                    iterations.Add((rev, it, new DateTimeOffset(r.GetDateTime(2), TimeSpan.Zero), r.GetString(3), []));
                if (r.IsDBNull(4)) continue;
                var readable = !r.IsDBNull(6);
                iterations[^1].Links.Add(new LinkRow(rev, r.GetString(4), r.GetGuid(5), readable ? r.GetString(7) : null, readable ? r.GetString(8) : null,
                    readable ? MarkingColumns.Read(r, 9) : null));
            }
        }

        var events = new List<TimelineEvent>();
        long order = 0;
        var href = $"parts/{Uri.EscapeDataString(key)}";
        var previous = new Dictionary<(string, Guid), LinkRow>();
        foreach (var (rev, it, at, by, links) in iterations)
        {
            var current = links.ToDictionary(l => (l.Type, l.DocMasterId));
            var label = $"{rev}.{it.ToString(CultureInfo.InvariantCulture)}";
            var linkHref = $"{href}?revision={Uri.EscapeDataString(rev)}#documents";
            foreach (var l in links.Where(l => !previous.ContainsKey((l.Type, l.DocMasterId))))
                events.Add(Make(l, "Linked document", $"added in version {label}", at, by, linkHref, ref order));
            foreach (var gone in previous.Where(p => !current.ContainsKey(p.Key)).Select(p => p.Value))
                events.Add(Make(gone, "Unlinked document", $"removed in version {label}", at, by, linkHref, ref order));
            previous = current;
        }
        return TimelineSlice.Of(events);
    }

    private TimelineEvent Make(LinkRow l, string title, string what, DateTimeOffset at, string by, string href, ref long order) =>
        l.Number is null
            ? TimelineEvent.Restricted(at, Rank, order++)
            : new TimelineEvent(at, TimelineKinds.Link, $"{title} {l.Number}", $"{(l.Type == "describes" ? "Describes the part" : "Referenced by the part")} · {l.Name} · {what}", by,
                l.Revision, href, false, Rank, order++, l.Markings);

    private static DateTimeOffset At(NpgsqlDataReader r, int i) => new(r.GetDateTime(i), TimeSpan.Zero);
}
