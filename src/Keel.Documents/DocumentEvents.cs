using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Documents;

/// <summary>Reports document revision releases and obsolescence to the integration outbox (ADR-0025), in the transaction of the state change.</summary>
internal static class DocumentEvents
{
    /// <summary>How the state change came about: an administrator override or obsolescence ("admin"), or the review workflow (7.6, ADR-0033).</summary>
    public const string ViaAdmin = "admin";
    public const string ViaWorkflow = "workflow";

    public static async Task EmitAsync(IDomainEventSink? sink, DbConnection conn, DbTransaction tx, Guid revisionId, string eventType, Subject actor, DateTimeOffset now, string? correlationId, CancellationToken ct, string via = ViaAdmin)
    {
        if (sink is null) return;
        var c = (NpgsqlConnection)conn;
        var t = (NpgsqlTransaction)tx;
        Guid masterId, iterationId;
        string number, revision;
        JsonObject payload;
        Markings markings;
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT m.id, m.number, m.name, m.document_type, r.revision, r.latest_iteration, r.lifecycle_state, i.id, {MarkingColumns.Select("m")}
            FROM plm.document_revision r
            JOIN plm.document_master m ON m.id = r.master_id
            JOIN plm.document_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            WHERE r.id = @rid
            """, c, t))
        {
            cmd.Parameters.AddWithValue("rid", revisionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new InvalidOperationException("The document revision disappeared before its event was written.");
            (masterId, number, revision, iterationId) = (r.GetGuid(0), r.GetString(1), r.GetString(4), r.GetGuid(7));
            markings = MarkingColumns.Read(r, 8);
            payload = new JsonObject
            {
                ["documentId"] = masterId.ToString("D"),
                ["revisionId"] = revisionId.ToString("D"),
                ["number"] = number,
                ["name"] = r.GetString(2),
                ["documentType"] = r.GetString(3),
                ["revision"] = revision,
                ["iteration"] = $"{revision}.{r.GetInt32(5).ToString(CultureInfo.InvariantCulture)}",
                ["state"] = r.GetString(6),
                ["via"] = via,
            };
        }
        var files = new JsonArray();
        await using (var cmd = new NpgsqlCommand("SELECT f.role, f.file_name, f.media_type, f.size_bytes, f.sha256 FROM plm.document_file f WHERE f.iteration_id = @i ORDER BY f.role DESC, f.file_name", c, t))
        {
            cmd.Parameters.AddWithValue("i", iterationId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                files.Add(new JsonObject { ["role"] = r.GetString(0), ["fileName"] = r.GetString(1), ["mediaType"] = r.GetString(2), ["sizeBytes"] = r.GetInt64(3), ["sha256"] = r.GetString(4) });
        }
        payload["files"] = files;
        await sink.AppendAsync(conn, tx, new DomainEvent(eventType, "document", masterId, number, revision, markings, payload, now, actor.Username ?? actor.Id, correlationId), ct);
    }
}

/// <summary>Document data and marked files for Technical Data Packages, through <see cref="DocumentService"/> so access is evaluated and audited as for any user.</summary>
public sealed class TdpDocumentSource(DocumentService documents) : ITdpDocumentSource
{
    public async Task<Outcome<TdpDocument>> GetAsync(Subject subject, string number, string? revision, string? correlationId, CancellationToken ct)
    {
        var o = await documents.GetAsync(subject, number, revision, correlationId, ct);
        if (!o.IsOk) return new Outcome<TdpDocument>(o.Kind, default, o.Errors);
        var d = o.Value!;
        return Outcome<TdpDocument>.Ok(new TdpDocument(d.Number, d.Name, d.DocumentType, d.Revision.Revision, d.Revision.StateLabel, d.Iteration.Label, d.Markings,
            [.. d.Iteration.Files.Select(f => new TdpDocumentFile(f.Id, f.Role, f.FileName, f.MediaType, f.SizeBytes, f.Sha256))]));
    }

    public async Task<Outcome<TdpMarkedFile>> OpenMarkedFileAsync(Subject subject, string number, Guid fileId, string? correlationId, CancellationToken ct)
    {
        var o = await documents.DownloadAsync(subject, number, fileId, null, null, correlationId, ct);
        if (!o.IsOk) return new Outcome<TdpMarkedFile>(o.Kind, default, o.Errors);
        var m = o.Value!;
        return Outcome<TdpMarkedFile>.Ok(new TdpMarkedFile(m.FileName, m.ContentType, m.Banner, m.Format, m.WriteToAsync));
    }
}
