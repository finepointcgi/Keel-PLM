using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Documents.Scanning;
using Keel.Documents.Vault;
using Keel.Security;
using Npgsql;

namespace Keel.Documents;

/// <summary>What an administrator's rescan covers. By default only content whose newest scan is not <c>clean</c> (never scanned, skipped, unscanned).</summary>
public sealed record RescanRequest(int Limit = 500, bool All = false, string? AfterSha256 = null);

/// <summary>Outcome of a rescan: counts only. <see cref="Continuation"/> resumes the next call; null when everything selected was examined.</summary>
public sealed record RescanReport(long Examined, long Clean, long Infected, long NotScanned, long IntegrityFailures, long DocumentsQuarantined, string? Continuation);

/// <summary>Scan coverage of the stored files, for auditors (counts of file rows by the newest scan status of their content).</summary>
public sealed record ScanCoverage(long Files, long Clean, long Infected, long Unscanned, long Skipped, long Quarantined, string Mode, string? Scanner, string? ScannerVersion, DateTimeOffset? SignatureDatabaseDate);

public sealed partial class DocumentService
{
    private const string RescanAction = "document.rescan";

    // ---------------------------------------------------------------- refusals at upload time

    private async Task<Outcome<T>> RefuseInfectedAsync<T>(Subject subject, string operation, string? number, string fileName, UploadInfectedException e, string? correlationId, CancellationToken ct)
    {
        // The signature name goes to the authorized uploader (the caller passed the policy check before any byte was accepted) and to the audit log.
        await audit.AppendAsync(Entry(subject, "document.upload_infected", AuditOutcome.Failure, number, correlationId,
            ("operation", operation), ("file", fileName), ("size", e.SizeBytes.ToString(CultureInfo.InvariantCulture)), ("sha256", e.Sha256),
            ("signature", e.Signature), ("scanner", e.Scan.ScannerName), ("scannerVersion", e.Scan.ScannerVersion)), ct);
        return Outcome<T>.Unprocessable(e.Message, e.Signature);
    }

    private async Task<Outcome<T>> RefuseNotScannedAsync<T>(Subject subject, string operation, string? number, string fileName, UploadNotScannedException e, string? correlationId, CancellationToken ct)
    {
        await audit.AppendAsync(Entry(subject, "document.upload_refused", AuditOutcome.Failure, number, correlationId,
            ("operation", operation), ("file", fileName), ("reason", e.TooLargeToScan ? "scan.too_large" : "scan.unavailable"), ("detail", e.Reason)), ct);
        return e.TooLargeToScan ? Outcome<T>.Unprocessable(e.Message) : Outcome<T>.Unavailable(e.Message);
    }

    private static async Task RecordScanAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sha256, long sizeBytes, ScanRecord scan, string source, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO plm.file_scan (sha256, size_bytes, status, scanner_name, scanner_version, signature_db_date, signature, reason, source, scanned_at)
            VALUES (@sha, @size, @status, @name, @version, @db, @sig, @reason, @source, @at)
            """, conn, tx);
        cmd.Parameters.AddWithValue("sha", sha256);
        cmd.Parameters.AddWithValue("size", sizeBytes);
        cmd.Parameters.AddWithValue("status", scan.Status);
        cmd.Parameters.AddWithValue("name", (object?)scan.ScannerName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("version", (object?)scan.ScannerVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("db", (object?)scan.SignatureDatabaseDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sig", (object?)scan.Signature ?? DBNull.Value);
        cmd.Parameters.AddWithValue("reason", (object?)scan.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("at", scan.ScannedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The first of <paramref name="hashes"/> that is quarantined for this document (a short form of its SHA-256), or null.</summary>
    private static async Task<string?> QuarantinedAmongAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid masterId, string[] hashes, CancellationToken ct)
    {
        if (hashes.Length == 0) return null;
        await using var cmd = new NpgsqlCommand("SELECT sha256 FROM plm.file_quarantine WHERE master_id = @m AND released_at IS NULL AND sha256 = ANY(@h) LIMIT 1", conn, tx);
        cmd.Parameters.AddWithValue("m", masterId);
        cmd.Parameters.AddWithValue("h", hashes);
        return await cmd.ExecuteScalarAsync(ct) is string sha ? sha[..12] : null;
    }

    // ---------------------------------------------------------------- administration

    /// <summary>
    /// Streams stored files back through the scanner and records the result (administrators). Content found infected is quarantined for every
    /// document that holds it: downloads are refused while the bytes and the audit trail stay. A stored file is read verified, so a rescan
    /// also surfaces vault corruption (counted, not scanned).
    /// </summary>
    public async Task<Outcome<RescanReport>> RescanAsync(Subject subject, RescanRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Administer))
        {
            await audit.AppendAsync(Entry(subject, "vault", RescanAction, AuditOutcome.Denied, null, correlationId, ("reasons", "role.missing")), ct);
            return Outcome<RescanReport>.Forbidden(["role.missing"]);
        }
        var scanning = vault.Scanning;
        if (scanning is null || scanning.Options.Mode == ScanMode.Disabled)
            return Outcome<RescanReport>.Invalid(["Malware scanning is not configured (Keel:Vault:Scanning:Mode is Disabled)."]);
        try
        {
            await scanning.Scanner.GetInfoAsync(ct);
        }
        catch (ScannerUnavailableException e)
        {
            return Outcome<RescanReport>.Unavailable(e.Message);
        }

        var limit = Math.Clamp(request.Limit, 1, 10_000);
        var targets = new List<(string Sha, long Size)>();
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT f.sha256, max(f.size_bytes)
            FROM plm.document_file f
            WHERE f.sha256 > @after
              AND (@all OR COALESCE((SELECT x.status FROM plm.file_scan x WHERE x.sha256 = f.sha256 ORDER BY x.id DESC LIMIT 1), 'unscanned') <> 'clean')
            GROUP BY f.sha256 ORDER BY f.sha256 LIMIT @limit
            """, conn))
        {
            cmd.Parameters.AddWithValue("after", request.AfterSha256 ?? "");
            cmd.Parameters.AddWithValue("all", request.All);
            cmd.Parameters.AddWithValue("limit", limit);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) targets.Add((r.GetString(0), r.GetInt64(1)));
        }

        long clean = 0, infected = 0, notScanned = 0, integrity = 0, quarantinedDocs = 0;
        foreach (var (sha, size) in targets)
        {
            ScanResult result;
            try
            {
                await using var stream = await vault.OpenReadAsync(sha, size, ct);
                result = await scanning.ScanStreamAsync(stream, ct);
            }
            catch (VaultIntegrityException)
            {
                integrity++;
                continue;
            }
            var now = scanning.Clock.GetUtcNow();
            if (result.Verdict == ScanVerdict.Error) { notScanned++; continue; }
            var record = new ScanRecord(result.Verdict == ScanVerdict.Clean ? ScanStatus.Clean : ScanStatus.Infected, result.Scanner?.Name, result.Scanner?.Version,
                result.Scanner?.SignatureDatabaseDate, now, null, result.Signature);
            await using var wconn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await wconn.BeginTransactionAsync(ct);
            await RecordScanAsync(wconn, tx, sha, size, record, "rescan", ct);
            if (result.Verdict == ScanVerdict.Clean) clean++;
            else
            {
                infected++;
                quarantinedDocs += await QuarantineAsync(wconn, tx, subject, sha, result.Signature!, correlationId, ct);
            }
            await tx.CommitAsync(ct);
        }

        var complete = targets.Count < limit;
        var report = new RescanReport(targets.Count, clean, infected, notScanned, integrity, quarantinedDocs, complete || targets.Count == 0 ? null : targets[^1].Sha);
        await audit.AppendAsync(Entry(subject, "vault", RescanAction, AuditOutcome.Success, null, correlationId,
            ("examined", Num(report.Examined)), ("clean", Num(clean)), ("infected", Num(infected)), ("notScanned", Num(notScanned)), ("integrityFailures", Num(integrity)),
            ("documentsQuarantined", Num(quarantinedDocs)), ("all", request.All ? "true" : "false"), ("continuation", report.Continuation)), ct);
        return Outcome<RescanReport>.Ok(report);
    }

    /// <summary>Quarantines <paramref name="sha256"/> for every document that holds it; one audit event per newly flagged document. Returns how many were flagged.</summary>
    private async Task<long> QuarantineAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string sha256, string signature, string? correlationId, CancellationToken ct)
    {
        var flagged = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO plm.file_quarantine (master_id, sha256, signature, quarantined_at, quarantined_by)
            SELECT DISTINCT m.id, @sha, @sig, @at, @by
            FROM plm.document_file f
            JOIN plm.document_iteration i ON i.id = f.iteration_id
            JOIN plm.document_revision r ON r.id = i.revision_id
            JOIN plm.document_master m ON m.id = r.master_id
            WHERE f.sha256 = @sha
              AND NOT EXISTS (SELECT 1 FROM plm.file_quarantine q WHERE q.master_id = m.id AND q.sha256 = @sha AND q.released_at IS NULL)
            RETURNING (SELECT number FROM plm.document_master WHERE id = master_id)
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("sha", sha256);
            cmd.Parameters.AddWithValue("sig", signature);
            cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
            cmd.Parameters.AddWithValue("by", subject.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) flagged.Add(r.GetString(0));
        }
        foreach (var number in flagged)
            await audit.AppendAsync(tx, Entry(subject, "document.quarantine", AuditOutcome.Success, number, correlationId,
                ("sha256", sha256), ("signature", signature), ("source", "rescan")), ct);
        return flagged.Count;
    }

    /// <summary>Administrator releases a quarantined file of a document (false positive), with a reason. The record stays.</summary>
    public async Task<Outcome<bool>> ReleaseQuarantineAsync(Subject subject, string number, string sha256, string reason, string? correlationId, CancellationToken ct = default)
    {
        const string action = "document.quarantine_release";
        if (string.IsNullOrWhiteSpace(reason)) return Outcome<bool>.Invalid(["A reason is required to release a quarantined file."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await LoadHeadAsync(conn, tx, number, forUpdate: true, ct);
        if (head is null) return Outcome<bool>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        var decision = read.Permitted ? AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Administer) : read;
        if (!decision.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", decision.Reasons))), ct);
            await tx.CommitAsync(ct);
            return read.Permitted ? Outcome<bool>.Forbidden(decision.Reasons) : Outcome<bool>.NotFound();
        }
        await using var cmd = new NpgsqlCommand(
            "UPDATE plm.file_quarantine SET released_at = @at, released_by = @by, release_reason = @reason WHERE master_id = @m AND sha256 = @sha AND released_at IS NULL", conn, tx);
        cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
        cmd.Parameters.AddWithValue("by", subject.Id);
        cmd.Parameters.AddWithValue("reason", reason.Trim());
        cmd.Parameters.AddWithValue("m", head.MasterId);
        cmd.Parameters.AddWithValue("sha", sha256.ToLowerInvariant());
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Outcome<bool>.NotFound();
        await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Success, number, correlationId, ("sha256", sha256), ("reason", reason.Trim())), ct);
        await tx.CommitAsync(ct);
        return Outcome<bool>.Ok(true);
    }

    /// <summary>Scan coverage of stored files (administrators and auditors): how much of the vault has been looked at, by what, and how current its signatures are.</summary>
    public async Task<Outcome<ScanCoverage>> GetScanCoverageAsync(Subject subject, string? correlationId, CancellationToken ct = default)
    {
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Administer) && !subject.Roles.Contains(Roles.Auditor))
        {
            await audit.AppendAsync(Entry(subject, "vault", "vault.scan_coverage", AuditOutcome.Denied, null, correlationId, ("reasons", "role.missing")), ct);
            return Outcome<ScanCoverage>.Forbidden(["role.missing"]);
        }
        long files = 0, clean = 0, infected = 0, unscanned = 0, skipped = 0, quarantined = 0;
        DateTimeOffset? newest = null;
        string? scanner = null, version = null;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        {
            await using (var cmd = new NpgsqlCommand(
                "SELECT count(*), count(*) FILTER (WHERE scan_status = 'clean'), count(*) FILTER (WHERE scan_status = 'infected'), count(*) FILTER (WHERE scan_status = 'unscanned'), count(*) FILTER (WHERE scan_status = 'skipped'), count(*) FILTER (WHERE quarantined) FROM plm.document_file_scan_status", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                await r.ReadAsync(ct);
                (files, clean, infected, unscanned, skipped, quarantined) = (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5));
            }
            await using var latest = new NpgsqlCommand("SELECT scanner_name, scanner_version, signature_db_date FROM plm.file_scan WHERE scanner_name IS NOT NULL ORDER BY id DESC LIMIT 1", conn);
            await using var lr = await latest.ExecuteReaderAsync(ct);
            if (await lr.ReadAsync(ct))
            {
                scanner = lr.GetString(0);
                version = lr.IsDBNull(1) ? null : lr.GetString(1);
                newest = lr.IsDBNull(2) ? null : new DateTimeOffset(lr.GetDateTime(2), TimeSpan.Zero);
            }
        }
        var coverage = new ScanCoverage(files, clean, infected, unscanned, skipped, quarantined, (vault.Scanning?.Options.Mode ?? ScanMode.Disabled).ToString(), scanner, version, newest);
        await audit.AppendAsync(Entry(subject, "vault", "vault.scan_coverage", AuditOutcome.Success, null, correlationId, ("files", Num(files)), ("unscanned", Num(unscanned))), ct);
        return Outcome<ScanCoverage>.Ok(coverage);
    }

    private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);
}
