using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

/// <summary>
/// Lets notifications, comments and activity feeds (Keel.Notifications, ADR-0030) find a baseline by number: name, state and markings, as a KEY LOOKUP. It decides
/// nothing about access; the caller evaluates <c>AccessPolicy</c> on the returned markings, so a baseline the viewer may not read looks like one that does not exist.
/// </summary>
public sealed class BaselineObjectAccess(NpgsqlDataSource dataSource) : IObjectAccess
{
    public string Kind => AwarenessKinds.Baseline;

    public async Task<ObjectInfo?> FindAsync(string number, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"""
            SELECT m.number, m.name, COALESCE(ls.label, m.lifecycle_state), {MarkingColumns.Select("m")}
            FROM cfg.baseline m
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = m.lifecycle_id AND ls.state = m.lifecycle_state
            WHERE m.number = @n
            """);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ObjectInfo(Kind, r.GetString(0), r.GetString(1), r.GetString(2), MarkingColumns.Read(r, 3)) : null;
    }
}

/// <summary>The same lookup for manufacturing BOMs: the MBOM's own markings (they dominate the part's), named like "Bracket (PLANT-A)".</summary>
public sealed class MbomObjectAccess(NpgsqlDataSource dataSource) : IObjectAccess
{
    public string Kind => AwarenessKinds.Mbom;

    public async Task<ObjectInfo?> FindAsync(string number, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"""
            SELECT mm.number, pm.name, mm.plant, COALESCE(ls.label, r.lifecycle_state), {MarkingColumns.Select("mm")}
            FROM mbom.mbom mm
            JOIN plm.part_master pm ON pm.id = mm.part_master_id
            JOIN mbom.revision r ON r.mbom_id = mm.id AND r.is_latest
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = mm.lifecycle_id AND ls.state = r.lifecycle_state
            WHERE mm.number = @n
            """);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ObjectInfo(Kind, r.GetString(0), $"{r.GetString(1)} ({r.GetString(2)})", r.GetString(3), MarkingColumns.Read(r, 4)) : null;
    }
}
