using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Quality;

/// <summary>
/// Lets notifications, comments, watch lists and activity feeds (ADR-0030) find a quality record by number: its title, state and markings, as a KEY LOOKUP. It decides
/// nothing about access; the caller evaluates <c>AccessPolicy</c> on the returned markings.
/// </summary>
internal sealed class QualityObjectAccess(NpgsqlDataSource dataSource, string kind) : IObjectAccess
{
    public string Kind => kind;

    public async Task<ObjectInfo?> FindAsync(string number, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"""
            SELECT m.number, m.title, COALESCE(ls.label, m.lifecycle_state), {MarkingColumns.Select("m")}
            FROM {QualityKinds.Table(kind)} m
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = m.lifecycle_id AND ls.state = m.lifecycle_state
            WHERE m.number = @n
            """);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ObjectInfo(kind, r.GetString(0), r.GetString(1), r.GetString(2), MarkingColumns.Read(r, 3)) : null;
    }
}

/// <summary>
/// Answers the supplier page's question "what is this opaque quality reference?" (<see cref="IQualityReferenceResolver"/> of Keel.Core). The sourcing module keeps only a
/// kind and a number; this resolver returns a label only to a caller who may read the record, and null otherwise, so existence is not disclosed.
/// </summary>
internal sealed class QualityReferenceResolver(NpgsqlDataSource dataSource) : IQualityReferenceResolver
{
    public bool Handles(string kind) => QualityKinds.IsKnown(kind);

    public async Task<QualityReference?> ResolveAsync(Subject subject, string kind, string id, CancellationToken ct = default)
    {
        if (!QualityKinds.IsKnown(kind)) return null;
        var info = await new QualityObjectAccess(dataSource, kind).FindAsync(id, ct);
        if (info is null || !AccessPolicy.Evaluate(subject, info.Markings, PlmAction.Read).Permitted) return null;
        return new QualityReference(kind, info.Number, $"{info.Number}: {info.Name}", AwarenessKinds.Path(kind, info.Number), info.StateLabel);
    }
}

/// <summary>
/// The facts a supplier scorecard counts (<see cref="ISupplierQualityFacts"/>): the nonconformances and corrective actions a supplier is linked to, read through the SQL read
/// filter of the caller (so an unreadable record is not counted and leaves no sign). Cancelled records are returned flagged as cancelled. A record counts as closed when it is in the Closed state;
/// the time of the last state change is the time it closed (a closed record is locked).
/// </summary>
internal sealed class SupplierQualityFacts(NpgsqlDataSource dataSource) : ISupplierQualityFacts
{
    public async Task<IReadOnlyList<SupplierQualityFact>> ReadAsync(Subject subject, IReadOnlyList<(string Kind, string Id)> references, CancellationToken ct = default)
    {
        var facts = new List<SupplierQualityFact>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        foreach (var kind in new[] { QualityKinds.Ncr, QualityKinds.Capa })
        {
            var numbers = references.Where(r => r.Kind == kind).Select(r => r.Id).Distinct(StringComparer.Ordinal).ToArray();
            if (numbers.Length == 0) continue;
            var filter = AccessPolicy.BuildReadFilter(subject, "m");
            var severity = kind == QualityKinds.Ncr ? "m.severity" : "NULL::text";
            await using var cmd = new NpgsqlCommand(
                $"SELECT m.number, m.created_at, m.lifecycle_state, m.state_changed_at, {severity}, {MarkingColumns.Select("m")} FROM {QualityKinds.Table(kind)} m WHERE {filter.Sql} AND m.number = ANY(@n)", conn);
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("n", numbers);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                facts.Add(new SupplierQualityFact(kind, r.GetString(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2) == "Closed" ? r.GetFieldValue<DateTimeOffset>(3) : null,
                    r.IsDBNull(4) ? null : r.GetString(4), MarkingColumns.Read(r, 5), r.GetString(2) == "Cancelled"));
        }
        return facts;
    }
}

/// <summary>
/// Quality entries on the timeline of a part: reports raised against it and deviations or waivers that name it. Only records the caller may read are named (the SQL read
/// filter); the others are left out, so a part with restricted records shows no sign of them. It never authorizes the timeline: the part's own sources do.
/// </summary>
internal sealed class QualityTimelineSource(NpgsqlDataSource dataSource) : ITimelineSource
{
    public const int RankValue = 45;
    public int Rank => RankValue;
    public bool Handles(string objectType) => objectType == "part";

    public async Task<TimelineSlice> GetAsync(Subject subject, string objectType, string key, string? correlationId, CancellationToken ct)
    {
        var events = new List<TimelineEvent>();
        long order = 0;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var nFilter = AccessPolicy.BuildReadFilter(subject, "n");
        await using (var cmd = new NpgsqlCommand(
            $"SELECT n.number, n.title, n.severity, n.created_at, n.reporter_name, n.part_revision, {MarkingColumns.Select("n")} FROM qual.ncr n WHERE {nFilter.Sql} AND n.part_number = @k ORDER BY n.created_at, n.number", conn))
        {
            foreach (var (k, v) in nFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("k", key);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                events.Add(new TimelineEvent(r.GetFieldValue<DateTimeOffset>(3), TimelineKinds.Quality, $"{r.GetString(0)} reported", $"{r.GetString(1)} · {r.GetString(2).ToLowerInvariant()} severity", r.GetString(4), r.GetString(5),
                    AwarenessKinds.Path(AwarenessKinds.Ncr, r.GetString(0)), false, Rank, order++, MarkingColumns.Read(r, 6)));
        }
        var dFilter = AccessPolicy.BuildReadFilter(subject, "d");
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT DISTINCT d.number, d.title, d.dev_kind, h.at, h.action, h.actor_name, h.id, {MarkingColumns.Select("d")}
            FROM qual.deviation d
            JOIN qual.deviation_scope s ON s.deviation_id = d.id
            JOIN qual.history h ON h.object_kind = 'deviation' AND h.object_id = d.id AND h.action IN ('Approved', 'Expired', 'Revoked')
            WHERE {dFilter.Sql} AND s.part_number = @k
            ORDER BY h.at, h.id
            """, conn))
        {
            foreach (var (k, v) in dFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("k", key);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                events.Add(new TimelineEvent(r.GetFieldValue<DateTimeOffset>(3), TimelineKinds.Quality, $"{r.GetString(0)} {r.GetString(4).ToLowerInvariant()}", $"{r.GetString(1)} · {r.GetString(2).ToLowerInvariant()}",
                    r.GetString(5), null, AwarenessKinds.Path(AwarenessKinds.Deviation, r.GetString(0)), false, Rank, order++, MarkingColumns.Read(r, 7)));
        }
        return new TimelineSlice(false, events);
    }
}
