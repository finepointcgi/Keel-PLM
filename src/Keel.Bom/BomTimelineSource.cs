using System.Globalization;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

/// <summary>
/// Timeline entries owned by the BOM module (UX-3, ADR-0029).
/// For a part: the baselines that include it (named only when the caller may read the baseline; a baseline the caller cannot read is left out entirely, because
/// a placeholder would only reveal that some baseline exists) and the manufacturing BOMs derived from it (a neutral entry when the caller may not read the MBOM).
/// For a manufacturing BOM: its revisions, check-ins with the EBOM version each was derived from, and the state each revision reached.
/// </summary>
public sealed class BomTimelineSource(NpgsqlDataSource dataSource) : ITimelineSource
{
    public const int RankValue = 50;
    public int Rank => RankValue;
    public bool Handles(string objectType) => objectType is BomService.ObjectType or MbomService.ObjectType;

    public Task<TimelineSlice> GetAsync(Subject subject, string objectType, string key, string? correlationId, CancellationToken ct) =>
        objectType == MbomService.ObjectType ? MbomLifeAsync(subject, key, ct) : PartStructureAsync(subject, key, ct);

    private async Task<TimelineSlice> PartStructureAsync(Subject subject, string key, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        Guid masterId;
        await using (var cmd = new NpgsqlCommand($"SELECT m.id, {MarkingColumns.Select("m")} FROM plm.part_master m WHERE m.number = @n", conn))
        {
            cmd.Parameters.AddWithValue("n", key);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return new TimelineSlice(false, []);
            if (!AccessPolicy.Evaluate(subject, MarkingColumns.Read(r, 1), PlmAction.Read).Permitted) return new TimelineSlice(false, []);
            masterId = r.GetGuid(0);
        }

        var events = new List<TimelineEvent>();
        long order = 0;

        // Baselines that include this part, through the read filter on the baseline.
        var filter = AccessPolicy.BuildReadFilter(subject, "b");
        var partFilter = AccessPolicy.BuildReadFilter(subject, "pm");
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT b.number, b.name, COALESCE(ls.label, b.lifecycle_state), b.created_at, b.created_by, b.established_at, r.revision, i.iteration, {MarkingColumns.Select("b")}
            FROM cfg.baseline b
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = b.lifecycle_id AND ls.state = b.lifecycle_state
            JOIN cfg.baseline_item bi ON bi.baseline_id = b.id
            JOIN plm.part_master pm ON pm.id = bi.master_id AND {partFilter.Sql}
            JOIN plm.part_revision r ON r.id = bi.revision_id
            JOIN plm.part_iteration i ON i.id = bi.iteration_id
            WHERE {filter.Sql} AND pm.number = @n
            ORDER BY b.created_at, b.number
            """, conn))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            foreach (var (k, v) in partFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("n", key);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (await r.ReadAsync(ct))
            {
                var number = r.GetString(0);
                if (!seen.Add(number)) continue; // a part used on several lines of one baseline is one entry
                var rev = r.GetString(6);
                var version = $"{rev}.{r.GetInt32(7).ToString(CultureInfo.InvariantCulture)}";
                var href = $"baselines/{Uri.EscapeDataString(number)}";
                events.Add(new TimelineEvent(new DateTimeOffset(r.GetDateTime(3), TimeSpan.Zero), TimelineKinds.Baseline, $"Included in baseline {number}",
                    $"{r.GetString(1)} · version {version} · {r.GetString(2)}", r.GetString(4), rev, href, false, Rank, order++, MarkingColumns.Read(r, 8)));
                if (!r.IsDBNull(5))
                    events.Add(new TimelineEvent(new DateTimeOffset(r.GetDateTime(5), TimeSpan.Zero), TimelineKinds.Baseline, $"Baseline {number} established", $"Version {version}", null, rev, href,
                        false, Rank, order++, MarkingColumns.Read(r, 8)));
            }
        }

        // Manufacturing BOMs of this part, one entry per MBOM and EBOM version it was derived from (the first check-in that used it).
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT mb.number, mb.plant, pr.revision, pit.iteration, i.created_at, i.created_by, {MarkingColumns.Select("mb")}
            FROM mbom.mbom mb
            JOIN mbom.revision r ON r.mbom_id = mb.id
            JOIN mbom.iteration i ON i.revision_id = r.id
            JOIN plm.part_iteration pit ON pit.id = i.derived_part_iteration_id
            JOIN plm.part_revision pr ON pr.id = pit.revision_id
            WHERE mb.part_master_id = @m
            ORDER BY i.created_at, mb.number, i.id
            """, conn))
        {
            cmd.Parameters.AddWithValue("m", masterId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var seen = new HashSet<(string, string, int)>();
            while (await r.ReadAsync(ct))
            {
                var number = r.GetString(0);
                var rev = r.GetString(2);
                var iteration = r.GetInt32(3);
                if (!seen.Add((number, rev, iteration))) continue;
                var at = new DateTimeOffset(r.GetDateTime(4), TimeSpan.Zero);
                var markings = MarkingColumns.Read(r, 6);
                if (!AccessPolicy.Evaluate(subject, markings, PlmAction.Read).Permitted)
                {
                    events.Add(TimelineEvent.Restricted(at, Rank, order++));
                    continue;
                }
                events.Add(new TimelineEvent(at, TimelineKinds.Mbom, $"Manufacturing BOM {number} derived", $"Plant {r.GetString(1)} · from version {rev}.{iteration.ToString(CultureInfo.InvariantCulture)}",
                    r.GetString(5), rev, $"mboms/{Uri.EscapeDataString(number)}", false, Rank, order++, markings));
            }
        }
        return new TimelineSlice(false, events);
    }

    private async Task<TimelineSlice> MbomLifeAsync(Subject subject, string key, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        Guid id;
        string lifecycleId;
        await using (var cmd = new NpgsqlCommand($"SELECT mb.id, mb.lifecycle_id, {MarkingColumns.Select("mb")} FROM mbom.mbom mb WHERE mb.number = @n", conn))
        {
            cmd.Parameters.AddWithValue("n", key);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return TimelineSlice.NotAccessible;
            if (!AccessPolicy.Evaluate(subject, MarkingColumns.Read(r, 2), PlmAction.Read).Permitted) return TimelineSlice.NotAccessible;
            (id, lifecycleId) = (r.GetGuid(0), r.GetString(1));
        }
        var lifecycle = await Lifecycle.LoadAsync(conn, null, lifecycleId, ct);
        var initial = lifecycle?.States.FirstOrDefault(s => s.IsInitial)?.State;

        var events = new List<TimelineEvent>();
        long order = 0;
        var href = $"mboms/{Uri.EscapeDataString(key)}";
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT r.revision, r.created_at, r.created_by, r.lifecycle_state, r.state_changed_at, r.state_changed_by,
                   i.iteration, i.created_at, i.created_by, i.comment, pr.revision, pit.iteration
            FROM mbom.revision r
            JOIN mbom.iteration i ON i.revision_id = r.id
            LEFT JOIN plm.part_iteration pit ON pit.id = i.derived_part_iteration_id
            LEFT JOIN plm.part_revision pr ON pr.id = pit.revision_id
            WHERE r.mbom_id = @m
            ORDER BY r.created_at, r.id, i.iteration
            """, conn))
        {
            cmd.Parameters.AddWithValue("m", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (await r.ReadAsync(ct))
            {
                var rev = r.GetString(0);
                var revHref = $"{href}?revision={Uri.EscapeDataString(rev)}#overview";
                if (seen.Add(rev))
                {
                    events.Add(new TimelineEvent(At(r, 1), TimelineKinds.Revision, $"Revision {rev} created", null, r.GetString(2), rev, revHref, false, Rank, order++));
                    var state = r.GetString(3);
                    if (state != initial)
                        events.Add(new TimelineEvent(At(r, 4), TimelineKinds.Lifecycle, $"Revision {rev} moved to {lifecycle?.State(state)?.Label ?? state}", null, r.GetString(5), rev, revHref, false, Rank, order++));
                }
                var derived = r.IsDBNull(10) ? "" : $"Derived from EBOM version {r.GetString(10)}.{r.GetInt32(11).ToString(CultureInfo.InvariantCulture)}";
                var comment = r.IsDBNull(9) ? null : r.GetString(9);
                events.Add(new TimelineEvent(At(r, 7), TimelineKinds.Iteration, $"Version {rev}.{r.GetInt32(6).ToString(CultureInfo.InvariantCulture)} checked in",
                    string.Join(" · ", new[] { comment, derived }.Where(x => !string.IsNullOrEmpty(x))), r.GetString(8), rev, revHref, false, Rank, order++));
            }
        }
        return TimelineSlice.Of(events);
    }

    private static DateTimeOffset At(NpgsqlDataReader r, int i) => new(r.GetDateTime(i), TimeSpan.Zero);
}
