using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Change;

/// <summary>
/// Lets notifications, comments and activity feeds (Keel.Notifications, ADR-0030) find a change (problem report, change request or change notice) by number: its title,
/// state and markings, as a KEY LOOKUP. It decides nothing about access; the caller evaluates <c>AccessPolicy</c> on the returned markings.
/// </summary>
public sealed class ChangeObjectAccess(NpgsqlDataSource dataSource) : IObjectAccess
{
    public string Kind => AwarenessKinds.Change;

    public async Task<ObjectInfo?> FindAsync(string number, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"""
            SELECT m.number, m.title, COALESCE(ls.label, m.lifecycle_state), {MarkingColumns.Select("m")}
            FROM chg.change m
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = m.lifecycle_id AND ls.state = m.lifecycle_state
            WHERE m.number = @n
            """);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ObjectInfo(Kind, r.GetString(0), r.GetString(1), r.GetString(2), MarkingColumns.Read(r, 3)) : null;
    }
}
