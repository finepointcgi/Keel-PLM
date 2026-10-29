using System.Globalization;
using System.Text.Json;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Parts;

/// <summary>
/// The part's own life for its timeline (UX-3, ADR-0029): revisions created, check-ins with their comments, manual lifecycle changes and markings changes.
/// The caller must be allowed to read the part (otherwise the slice is not accessible and nothing is returned). Lifecycle and markings entries come from
/// the audit log (successful part.transition, part.set_state and part.markings_change events of this part); the reason texts of those events are not shown.
/// </summary>
public sealed class PartTimelineSource(NpgsqlDataSource dataSource) : ITimelineSource
{
    public const int RankValue = 10;
    public int Rank => RankValue;
    public bool Handles(string objectType) => objectType == PartService.ObjectType;

    public async Task<TimelineSlice> GetAsync(Subject subject, string objectType, string key, string? correlationId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        Guid masterId;
        string lifecycleId;
        await using (var cmd = new NpgsqlCommand($"SELECT m.id, m.lifecycle_id, {MarkingColumns.Select("m")} FROM plm.part_master m WHERE m.number = @n", conn))
        {
            cmd.Parameters.AddWithValue("n", key);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return TimelineSlice.NotAccessible;
            var markings = MarkingColumns.Read(r, 2);
            if (!AccessPolicy.Evaluate(subject, markings, PlmAction.Read).Permitted) return TimelineSlice.NotAccessible;
            (masterId, lifecycleId) = (r.GetGuid(0), r.GetString(1));
        }

        var lifecycle = await Lifecycle.LoadAsync(conn, null, lifecycleId, ct);
        string Label(string? state) => state is null ? "" : lifecycle?.State(state)?.Label ?? state;
        var events = new List<TimelineEvent>();
        long order = 0;
        var href = $"parts/{Uri.EscapeDataString(key)}";

        await using (var cmd = new NpgsqlCommand(
            """
            SELECT r.revision, r.created_at, r.created_by, i.iteration, i.created_at, i.created_by, i.comment
            FROM plm.part_revision r
            JOIN plm.part_iteration i ON i.revision_id = r.id
            WHERE r.master_id = @m
            ORDER BY r.created_at, r.id, i.iteration
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
                var label = $"{rev}.{r.GetInt32(3).ToString(CultureInfo.InvariantCulture)}";
                events.Add(new TimelineEvent(At(r, 4), TimelineKinds.Iteration, $"Version {label} checked in", r.IsDBNull(6) ? null : r.GetString(6), r.GetString(5), rev, revHref, false, Rank, order++));
            }
        }

        await using (var cmd = new NpgsqlCommand(
            """
            SELECT occurred_at, action, payload FROM audit.event
            WHERE object_type = 'part' AND object_id = @n AND outcome = 'Success' AND action IN ('part.transition', 'part.set_state', 'part.markings_change')
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
                var revHref = rev is null ? href : $"{href}?revision={Uri.EscapeDataString(rev)}#overview";
                var action = r.GetString(1);
                events.Add(action == "part.markings_change"
                    ? new TimelineEvent(at, TimelineKinds.Markings, "Security markings changed", null, actor, rev, $"{href}#security", false, Rank, order++)
                    : new TimelineEvent(at, TimelineKinds.Lifecycle,
                        $"{(action == "part.set_state" ? "State set to" : "Moved to")} {Label(D("to"))}", $"From {Label(D("from"))}{(action == "part.set_state" ? " (administrator override)" : "")}",
                        actor, rev, revHref, false, Rank, order++));
            }
        }
        return TimelineSlice.Of(events);
    }

    private static DateTimeOffset At(NpgsqlDataReader r, int i) => new(r.GetDateTime(i), TimeSpan.Zero);
}
