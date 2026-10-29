using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Documents;

/// <summary>
/// Lets notifications, comments and activity feeds (Keel.Notifications, ADR-0030) find a document by number: its name, state and markings, as a KEY LOOKUP. It decides
/// nothing about access; the caller evaluates <c>AccessPolicy</c> on the returned markings, so a document the viewer may not read looks like one that does not exist.
/// </summary>
public sealed class DocumentObjectAccess(NpgsqlDataSource dataSource) : IObjectAccess
{
    public string Kind => AwarenessKinds.Document;

    public async Task<ObjectInfo?> FindAsync(string number, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"""
            SELECT m.number, m.name, COALESCE(ls.label, r.lifecycle_state), {MarkingColumns.Select("m")}
            FROM plm.document_master m
            JOIN plm.document_revision r ON r.master_id = m.id AND r.is_latest
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = m.lifecycle_id AND ls.state = r.lifecycle_state
            WHERE m.number = @n
            """);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ObjectInfo(Kind, r.GetString(0), r.GetString(1), r.GetString(2), MarkingColumns.Read(r, 3)) : null;
    }
}
