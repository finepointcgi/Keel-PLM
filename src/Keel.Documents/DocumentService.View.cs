using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Core.Viewing;
using Keel.Documents.Marking;
using Keel.Documents.Vault;
using Keel.Security;
using Npgsql;

namespace Keel.Documents;

/// <summary>
/// The documents module's side of the in-browser file viewer (increment 6.0, ADR-0027). Viewing streams a file's bytes to the browser, so it is
/// authorized exactly like a download (Read, then Download on the document's markings), reads through the same integrity-verified vault path, and
/// PDFs go through the same marking pipeline as downloads, so what is on screen is what a download would contain.
/// </summary>
public sealed partial class DocumentService : IViewableFileSource
{
    public const string ViewAction = "document.view";

    public Task<Outcome<ViewableFile>> DescribeAsync(Subject subject, string number, Guid fileId, string? correlationId, CancellationToken ct = default) =>
        LoadViewableAsync(subject, number, fileId, null, correlationId, ct);

    public Task<Outcome<ViewableFile>> FindPrimaryAsync(Subject subject, string number, string? revision, string? correlationId, CancellationToken ct = default) =>
        LoadViewableAsync(subject, number, null, revision ?? "", correlationId, ct);

    public async Task<Outcome<ViewContent>> ReadAsync(Subject subject, string number, Guid fileId, string? correlationId, CancellationToken ct = default)
    {
        var described = await LoadViewableAsync(subject, number, fileId, null, correlationId, ct);
        if (!described.IsOk) return new Outcome<ViewContent>(described.Kind, null, described.Errors);
        var file = described.Value!;

        var options = vault.Options;
        var ctx = new MarkingContext(file.Markings, file.DocumentNumber, file.DocumentName, file.IterationLabel, file.FileName, file.StoredMediaType,
            file.SizeBytes, file.Sha256, DisplayId(subject), clock.GetUtcNow());
        byte[] bytes;
        try
        {
            bytes = await vault.ReadAllAsync(file.Sha256, file.SizeBytes, ct);
        }
        catch (VaultIntegrityException e)
        {
            await audit.AppendAsync(Entry(subject, ViewAction, AuditOutcome.Failure, number, correlationId,
                ("file", file.FileName), ("fileId", file.FileId.ToString()), ("sha256", file.Sha256), ("iteration", file.IterationLabel),
                ("markings", file.Banner), ("error", e.Message)), CancellationToken.None);
            throw new StoredFileIntegrityException(e.Message);
        }

        // The marked pipeline of downloads: a PDF is stamped in memory, and a PDF that cannot be stamped (too large for the stamper, encrypted,
        // damaged) is refused rather than shown without its banner and notices. Everything else is returned as stored; the viewer draws its overlay.
        // Stricter than downloads (which accept the header anywhere in the first KiB): the viewer treats only a file that STARTS with the PDF header as a PDF.
        if (bytes.AsSpan().StartsWith("%PDF-"u8))
        {
            if (file.SizeBytes > options.MaxPdfStampBytes)
                return Outcome<ViewContent>.Invalid([$"This PDF is too large to be marked for viewing (limit {options.MaxPdfStampBytes / (1024 * 1024)} MiB). Download the marked copy instead."]);
            try
            {
                return Outcome<ViewContent>.Ok(new ViewContent(file, PdfMarker.Stamp(bytes, ctx), Marked: true));
            }
            catch (PdfStampException e)
            {
                return Outcome<ViewContent>.Invalid(["This PDF cannot be marked for viewing (" + e.Message + "). Download the marked ZIP instead."]);
            }
        }
        return Outcome<ViewContent>.Ok(new ViewContent(file, bytes, Marked: false));
    }

    public async Task<byte[]> ReadHeadAsync(ViewableFile file, int count, CancellationToken ct = default)
    {
        var length = (int)Math.Min(count, file.SizeBytes);
        if (length <= 0) return [];
        try
        {
            await using var s = await vault.OpenReadAsync(file.Sha256, file.SizeBytes, ct);
            var buffer = new byte[length];
            var filled = 0;
            while (filled < length)
            {
                var n = await s.ReadAsync(buffer.AsMemory(filled), ct);
                if (n == 0) break;
                filled += n;
            }
            return buffer[..filled];
        }
        catch (VaultIntegrityException)
        {
            // Only advisory: the verified read in ReadAsync reports (and audits) the integrity failure.
            return [];
        }
    }

    public Task RecordAsync(Subject subject, ViewableFile file, ViewRecord record, string? correlationId, CancellationToken ct = default) =>
        audit.AppendAsync(Entry(subject, ViewAction, record.Outcome == ViewOutcome.Delivered ? AuditOutcome.Success : AuditOutcome.Denied, file.DocumentNumber, correlationId,
            ("file", file.FileName), ("fileId", file.FileId.ToString()), ("sha256", file.Sha256), ("size", file.SizeBytes.ToString(CultureInfo.InvariantCulture)),
            ("iteration", file.IterationLabel), ("banner", file.Banner), ("viewer", record.ViewerType), ("format", record.Format),
            ("handling", record.Handling), ("note", record.Note)), ct);

    /// <summary>
    /// Read rule then Download rule on the document markings, as in <see cref="DownloadAsync"/>: a caller who may not read gets NotFound, one who may
    /// read but not download gets Forbidden, and both are audited. <paramref name="primaryRevision"/> (empty = latest) selects the primary file instead of <paramref name="fileId"/>.
    /// </summary>
    private async Task<Outcome<ViewableFile>> LoadViewableAsync(Subject subject, string number, Guid? fileId, string? primaryRevision, string? correlationId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await LoadHeadAsync(conn, tx, number, forUpdate: false, ct);
        if (head is null) return Outcome<ViewableFile>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        var decision = read.Permitted ? AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Download) : read;
        if (!decision.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, ViewAction, AuditOutcome.Denied, number, correlationId,
                ("reasons", string.Join(",", decision.Reasons)), ("fileId", fileId?.ToString())), ct);
            await tx.CommitAsync(ct);
            return read.Permitted ? Outcome<ViewableFile>.Forbidden(decision.Reasons) : Outcome<ViewableFile>.NotFound();
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
                       ELSE f.role = 'primary' AND i.iteration = r.latest_iteration AND (CASE WHEN @rev::text = '' THEN r.is_latest ELSE r.revision = @rev END) END
            ORDER BY f.role DESC
            LIMIT 1
            """, conn, tx);
        cmd.Parameters.AddWithValue("m", head.MasterId);
        cmd.Parameters.AddWithValue("fid", (object?)fileId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rev", (object?)primaryRevision ?? DBNull.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return Outcome<ViewableFile>.NotFound();
        var file = ReadFile(r, 0);
        var iterationLabel = $"{r.GetString(8)}.{r.GetInt32(9)}";
        var documentName = r.GetString(10);
        return Outcome<ViewableFile>.Ok(new ViewableFile(head.Number, documentName, file.Id, file.FileName, file.MediaType, file.SizeBytes, file.Sha256,
            iterationLabel, head.Markings, head.Markings.BannerText(), MarkingStatements.Notices(head.Markings)));
    }
}
