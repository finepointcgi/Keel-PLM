using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Documents.Marking;
using Keel.Documents.Vault;
using Keel.Security;
using Npgsql;

namespace Keel.Documents;

public sealed partial class DocumentService
{
    /// <summary>
    /// Delivers a file of the document, marked (A10): PDFs stamped in place, everything else wrapped in a ZIP with a
    /// cover sheet and manifest. Requires <see cref="PlmAction.Download"/>; callers who may not read the document get
    /// 404 and the denial is audited. The stored object is verified against its SHA-256 before anything is sent; a
    /// mismatch is audited as a failure and raises <see cref="VaultIntegrityException"/>.
    /// </summary>
    /// <param name="fileId">A file id of any iteration of this document, or null to select by <paramref name="sha256"/>.</param>
    /// <param name="sha256">With no <paramref name="fileId"/>: a file of the latest iteration of <paramref name="revision"/> (default: latest revision).</param>
    public async Task<Outcome<MarkedDownload>> DownloadAsync(Subject subject, string number, Guid? fileId, string? sha256, string? revision,
        string? correlationId, CancellationToken ct = default)
    {
        const string action = "document.download";
        if (fileId is null && string.IsNullOrEmpty(sha256)) return Outcome<MarkedDownload>.Invalid(["A file id or SHA-256 is required."]);

        Head head;
        DocumentFileView file;
        string iterationLabel, documentName;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            var loaded = await LoadHeadAsync(conn, tx, number, forUpdate: false, ct);
            if (loaded is null) return Outcome<MarkedDownload>.NotFound();
            head = loaded;
            var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
            var decision = read.Permitted ? AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Download) : read;
            if (!decision.Permitted)
            {
                await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId,
                    ("reasons", string.Join(",", decision.Reasons)), ("fileId", fileId?.ToString()), ("sha256", sha256)), ct);
                await tx.CommitAsync(ct);
                return read.Permitted ? Outcome<MarkedDownload>.Forbidden(decision.Reasons) : Outcome<MarkedDownload>.NotFound();
            }

            await using var cmd = new NpgsqlCommand(
                """
                SELECT f.id, f.role, f.file_name, f.media_type, f.size_bytes, f.sha256, f.added_at, f.added_by, r.revision, i.iteration, m.name
                FROM plm.document_file f
                JOIN plm.document_iteration i ON i.id = f.iteration_id
                JOIN plm.document_revision r ON r.id = i.revision_id
                JOIN plm.document_master m ON m.id = r.master_id
                WHERE m.id = @m
                  AND CASE WHEN @fid::uuid IS NOT NULL THEN f.id = @fid
                           ELSE f.sha256 = @sha AND i.iteration = r.latest_iteration AND (CASE WHEN @rev::text IS NULL THEN r.is_latest ELSE r.revision = @rev END) END
                ORDER BY f.role DESC
                LIMIT 1
                """, conn, tx);
            cmd.Parameters.AddWithValue("m", head.MasterId);
            cmd.Parameters.AddWithValue("fid", (object?)fileId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("sha", (object?)sha256?.ToLowerInvariant() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("rev", (object?)revision ?? DBNull.Value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Outcome<MarkedDownload>.NotFound();
            file = ReadFile(r, 0);
            iterationLabel = $"{r.GetString(8)}.{r.GetInt32(9)}";
            documentName = r.GetString(10);
        }

        var ctx = new MarkingContext(head.Markings, number, documentName, iterationLabel, file.FileName, file.MediaType, file.SizeBytes, file.Sha256,
            DisplayId(subject), clock.GetUtcNow());
        var options = vault.Options;
        MarkedDownload result;
        string? stampError = null;
        try
        {
            var pdfCandidate = file.SizeBytes <= options.MaxPdfStampBytes &&
                (file.MediaType == "application/pdf" || file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
            if (pdfCandidate || file.SizeBytes <= options.PartSizeBytes)
            {
                // Bounded size: one verified read into memory serves both the check and the output.
                var bytes = await vault.ReadAllAsync(file.Sha256, file.SizeBytes, ct);
                byte[]? stamped = null;
                if (PdfMarker.LooksLikePdf(bytes))
                {
                    try { stamped = PdfMarker.Stamp(bytes, ctx); }
                    catch (PdfStampException e) { stampError = e.Message; }
                }
                result = stamped is not null
                    ? new MarkedDownload(file.FileName, "application/pdf", ctx.Banner, "pdf", (s, c) => s.WriteAsync(stamped, c).AsTask())
                    : Zip(ctx, (s, c) => MarkedZip.WriteAsync(s, ctx, new MemoryStream(bytes, writable: false), c));
            }
            else
            {
                // Large file: verify in one pass, then stream it into the ZIP in a second, again verified, pass.
                await vault.VerifyAsync(file.Sha256, file.SizeBytes, ct);
                result = Zip(ctx, async (s, c) =>
                {
                    await using var original = await vault.OpenReadAsync(file.Sha256, file.SizeBytes, c);
                    await MarkedZip.WriteAsync(s, ctx, original, c);
                });
            }
        }
        catch (VaultIntegrityException e)
        {
            await audit.AppendAsync(Entry(subject, action, AuditOutcome.Failure, number, correlationId,
                ("file", file.FileName), ("fileId", file.Id.ToString()), ("sha256", file.Sha256), ("iteration", iterationLabel),
                ("markings", ctx.Banner), ("error", e.Message)), CancellationToken.None);
            throw;
        }

        await audit.AppendAsync(Entry(subject, action, AuditOutcome.Success, number, correlationId,
            ("file", file.FileName), ("fileId", file.Id.ToString()), ("sha256", file.Sha256), ("size", file.SizeBytes.ToString(CultureInfo.InvariantCulture)),
            ("iteration", iterationLabel), ("markings", ctx.Banner), ("format", result.Format), ("stampError", stampError)), ct);
        return Outcome<MarkedDownload>.Ok(result);
    }

    private static MarkedDownload Zip(MarkingContext ctx, Func<Stream, CancellationToken, Task> write) =>
        new(MarkedZip.ArchiveName(ctx.FileName), "application/zip", ctx.Banner, "zip", write);
}
