using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Change;

/// <summary>
/// Change entries for the timeline of a part (UX-3, ADR-0029): each change that listed one of its revisions as affected (what it does to the revision, the
/// resulting revision, the effectivity it carries) and the moment such a change was released. The caller's read filter decides which changes are named; an
/// entry for a change the caller may not read is a neutral "A restricted item was involved" (time only). The part itself is authorized by the other sources:
/// this source never answers for the part, so it is never <see cref="TimelineSlice.Accessible"/> on its own.
/// </summary>
public sealed class ChangeTimelineSource(NpgsqlDataSource dataSource, IRevisionEffectivity? effectivity = null) : ITimelineSource
{
    public const int RankValue = 40;
    public int Rank => RankValue;
    public bool Handles(string objectType) => objectType == "part";

    private sealed record Row(Guid AffectedId, Guid ChangeId, string Action, string Revision, string? Resulting, DateTimeOffset AddedAt, bool Removed);

    public async Task<TimelineSlice> GetAsync(Subject subject, string objectType, string key, string? correlationId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        // The part's read access is established by the owning sources; here rows are looked up by the part's key.
        var rows = new List<Row>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT ao.id, ao.change_id, ao.change_action, ao.revision, ao.resulting_revision, ao.added_at, ao.removed_at IS NOT NULL
            FROM chg.affected_object ao
            WHERE ao.object_key = @k AND ao.object_type = 'part'
            ORDER BY ao.added_at, ao.id
            """, conn))
        {
            cmd.Parameters.AddWithValue("k", key);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new Row(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), new DateTimeOffset(r.GetDateTime(5), TimeSpan.Zero), r.GetBoolean(6)));
        }
        if (rows.Count == 0) return new TimelineSlice(false, []);

        var filter = AccessPolicy.BuildReadFilter(subject, "c");
        var readable = new Dictionary<Guid, (string Number, string Type, string Title, string State, Markings Markings, bool IsFinal)>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT c.id, c.number, c.change_type, c.title, COALESCE(ls.label, c.lifecycle_state), COALESCE(ls.is_final, false), {MarkingColumns.Select("c")}
            FROM chg.change c
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = c.lifecycle_id AND ls.state = c.lifecycle_state
            WHERE {filter.Sql} AND c.id = ANY(@ids)
            """, conn))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("ids", rows.Select(x => x.ChangeId).Distinct().ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                readable[r.GetGuid(0)] = (r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), MarkingColumns.Read(r, 6), r.GetBoolean(5));
        }

        IReadOnlyDictionary<Guid, EffectivityRange> ranges = new Dictionary<Guid, EffectivityRange>();
        if (effectivity is not null)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            ranges = await effectivity.GetForAffectedAsync(conn, tx, rows.Where(x => readable.ContainsKey(x.ChangeId)).Select(x => x.AffectedId).ToArray(), ct);
            await tx.CommitAsync(ct);
        }

        // Release moments of the readable changes (history action "Released", written when the notice releases).
        var released = new Dictionary<Guid, DateTimeOffset>();
        if (readable.Count > 0)
        {
            await using var cmd = new NpgsqlCommand("SELECT h.change_id, min(h.at) FROM chg.history h WHERE h.change_id = ANY(@ids) AND h.action = 'Released' GROUP BY h.change_id", conn);
            cmd.Parameters.AddWithValue("ids", readable.Keys.ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) released[r.GetGuid(0)] = new DateTimeOffset(r.GetDateTime(1), TimeSpan.Zero);
        }

        var events = new List<TimelineEvent>();
        long order = 0;
        foreach (var row in rows)
        {
            if (!readable.TryGetValue(row.ChangeId, out var c))
            {
                events.Add(TimelineEvent.Restricted(row.AddedAt, Rank, order++));
                continue;
            }
            var href = $"changes/{Uri.EscapeDataString(c.Number)}";
            var what = row.Action switch
            {
                "revise" => row.Resulting is null ? $"revise revision {row.Revision}" : $"revision {row.Revision} to {row.Resulting}",
                "obsolete" => $"obsolete revision {row.Revision}",
                _ => $"revision {row.Revision} (no change)",
            };
            var eff = ranges.TryGetValue(row.AffectedId, out var range) ? $" · effective {range.Describe()}" : "";
            events.Add(new TimelineEvent(row.AddedAt, TimelineKinds.Change, $"Listed in {c.Number}", $"{c.Title} · {what}{eff} · {c.State}", null, row.Revision, href, false, Rank, order++, c.Markings));
            if (released.TryGetValue(row.ChangeId, out var at) && !row.Removed && row.Action != "none")
                events.Add(new TimelineEvent(at, TimelineKinds.Change, $"{c.Number} released",
                    row.Action == "revise" && row.Resulting is not null ? $"Revision {row.Resulting} released, revision {row.Revision} superseded{eff}" : $"Revision {row.Revision} obsoleted", null,
                    row.Resulting ?? row.Revision, href, false, Rank, order++, c.Markings));
        }
        return new TimelineSlice(false, events);
    }
}
