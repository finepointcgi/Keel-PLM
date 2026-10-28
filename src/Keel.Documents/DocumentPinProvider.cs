using System.Data.Common;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Documents;

/// <summary>
/// Resolves the documents linked to part iterations to concrete document revisions for baselines (<see cref="IDocumentPinProvider"/>, ADR-0021).
/// A <c>describes</c> link already pins a revision; a <c>references</c> link follows its master, so a baseline pins the latest Released revision at the
/// moment of creation, else the latest (A23). Documents the subject may not read come back flagged unreadable and without markings.
/// </summary>
public sealed class DocumentPinProvider : IDocumentPinProvider
{
    public async Task<IReadOnlyList<PinnedDocument>> ResolveAsync(DbConnection conn, DbTransaction tx, Subject subject, IReadOnlyCollection<Guid> partIterationIds, CancellationToken ct)
    {
        var result = new List<PinnedDocument>();
        if (partIterationIds.Count == 0) return result;
        var filter = AccessPolicy.BuildReadFilter(subject, "dm");
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT l.part_iteration_id, l.link_type, l.document_master_id,
                   CASE WHEN l.link_type = 'describes' THEN l.document_revision_id
                        ELSE (SELECT r.id FROM plm.document_revision r WHERE r.master_id = l.document_master_id
                              ORDER BY (r.lifecycle_state = 'Released') DESC, length(r.revision) DESC, r.revision DESC LIMIT 1) END,
                   dm.id, {MarkingColumns.Select("dm")}
            FROM plm.part_document_link l
            LEFT JOIN plm.document_master dm ON dm.id = l.document_master_id AND {filter.Sql}
            WHERE l.part_iteration_id = ANY(@iters)
            ORDER BY l.part_iteration_id, l.link_type, l.document_master_id
            """, (NpgsqlConnection)conn, (NpgsqlTransaction)tx);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("iters", partIterationIds.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var readable = !r.IsDBNull(4);
            if (r.IsDBNull(3)) continue; // a master without any revision cannot be pinned
            result.Add(new PinnedDocument(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetGuid(3), readable, readable ? MarkingColumns.Read(r, 5) : null));
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, PinnedDocumentInfo>> DescribeAsync(DbConnection conn, DbTransaction tx, Subject subject, IReadOnlyCollection<Guid> documentRevisionIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, PinnedDocumentInfo>();
        if (documentRevisionIds.Count == 0) return result;
        var filter = AccessPolicy.BuildReadFilter(subject, "dm");
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT r.id, dm.id, dm.number, dm.name, dm.document_type, r.revision, COALESCE(ls.label, r.lifecycle_state), {MarkingColumns.Select("dm")}
            FROM plm.document_revision r
            LEFT JOIN plm.document_master dm ON dm.id = r.master_id AND {filter.Sql}
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = dm.lifecycle_id AND ls.state = r.lifecycle_state
            WHERE r.id = ANY(@ids)
            """, (NpgsqlConnection)conn, (NpgsqlTransaction)tx);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("ids", documentRevisionIds.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetGuid(0);
            if (r.IsDBNull(1)) { result[id] = new PinnedDocumentInfo(id, false, null, null, null, null, null, null, null); continue; }
            var markings = MarkingColumns.Read(r, 7);
            result[id] = new PinnedDocumentInfo(id, true, r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), markings.BannerText(), markings);
        }
        return result;
    }
}
