using Npgsql;

namespace Keel.Documents.Vault;

/// <summary>A database column whose value is the content address (SHA-256) of a vault object that must be kept while the row exists.</summary>
/// <param name="LivePredicate">
/// Optional SQL over the row alias <c>r</c>: when set, a row keeps its object alive only while the predicate is true. Records management (8.6) uses it so the files of a
/// document disposed of under a retention schedule (and not under legal hold) stop counting as references and the existing sweeper, with its grace period and integrity
/// checks, removes the content.
/// </param>
/// <param name="SizeColumn">The column of the same row that holds the recorded size in bytes (the bulk verifier, <c>keel-vault verify</c>, checks size and hash against it).</param>
public sealed record VaultReferenceSource(string Schema, string Table, string Column, string Why, string? LivePredicate = null, string SizeColumn = "size_bytes")
{
    public string Qualified => $"{Schema}.{Table}.{Column}";
}

/// <summary>A hash column that is NOT a vault key (or names content without keeping it alive), with the reason the garbage collector may ignore it.</summary>
public sealed record NonReferenceColumn(string Schema, string Table, string Column, string Reason)
{
    public string Qualified => $"{Schema}.{Table}.{Column}";
}

/// <summary>
/// The explicit registry the vault garbage collector consults before it deletes anything (ADR-0026). Every column in the database that holds a
/// hash or digest must be listed in exactly one of the two lists; an integration test reads the live schema and fails when a migration adds
/// a hash column that appears in neither, so a new table that keeps vault content alive can never be forgotten by the sweeper.
/// </summary>
public static class VaultReferences
{
    /// <summary>Columns that keep a vault object alive. Rows are never deleted while the object may still be needed.</summary>
    public static readonly IReadOnlyList<VaultReferenceSource> Sources =
    [
        new("plm", "document_file", "sha256", "files of document iterations (immutable rows; also the files of migrated documents); content of a document disposed of under a retention schedule is released unless a legal hold covers it (8.6)",
            "NOT rec.iteration_content_released(r.iteration_id)"),
        new("plm", "document_checkout_file", "sha256", "files of working copies not yet checked in"),
        new("src", "response_file", "sha256", "files a supplier uploaded with a response in the supplier portal (immutable rows)"),
        new("rec", "archive", "package_sha256", "WORM-style archive packages (8.6): immutable rows, never garbage", null, "package_size"),
        new("rpt", "artifact", "sha256", "generated release package PDFs (8.9): immutable rows, content addressed"),
    ];

    /// <summary>Hash columns that do not name vault objects, or name content without keeping it alive.</summary>
    public static readonly IReadOnlyList<NonReferenceColumn> NotVaultContent =
    [
        new("plm", "file_scan", "sha256", "scan log of content; a scan result never keeps the bytes alive"),
        new("plm", "file_quarantine", "sha256", "quarantine flag per document and content; the file row itself is the reference"),
        new("req", "link", "reviewed_source_sha", "content hash of a requirement as last reviewed; requirements hold no files"),
        new("req", "link", "reviewed_target_sha", "content hash of a linked requirement as last reviewed; no files"),
        new("req", "requirement_version", "content_sha256", "content hash of requirement text; requirements hold no files"),
        new("req", "review_binding", "review_sha256", "digest bound to a release review; no files"),
        new("req", "spec_iteration", "content_sha256", "digest of a requirement set iteration; no files"),
        new("req", "verification", "requirement_sha256", "content hash of the requirement a verification judged; evidence is a document number, not a file"),
        new("req", "verification_signature", "content_hash", "signed content hash of a formal verification; no files"),
        new("req", "verification_signature", "signature_hash", "signature hash of a formal verification; no files"),
        new("vault", "pending_upload", "sha256", "upload marker; consulted separately under the key lock, expires on its own"),
        new("mig", "batch", "package_sha256", "hash of an import package on the operator's disk, not a vault object"),
        new("mig", "batch", "mapping_sha256", "hash of the mapping file used for an import"),
        new("mig", "batch", "dry_run_hash", "hash of a dry-run report"),
        new("mig", "id_map", "source_hash", "hash of a mapped source object, used to detect source changes"),
        new("cfg", "baseline", "content_sha256", "hash over the canonical item list of a baseline (ADR-0021), not a vault object"),
        new("chg", "change_submission", "snapshot_sha256", "hash of a change submission snapshot (ADR-0019), not a vault object"),
        new("qual", "submission", "snapshot_sha256", "hash of a quality review snapshot (ADR-0036), not a vault object"),
        new("prg", "submission", "snapshot_sha256", "hash of a gate review snapshot (ADR-0043), not a vault object"),
        new("wf", "signature", "content_hash", "hash over the canonical content of a signed iteration (ADR-0018), not a vault object"),
        new("wf", "signature", "signature_hash", "hash over the canonical signed statement (ADR-0018), not a vault object"),
        new("siem", "cursor", "last_hash", "audit chain hash at the forwarding position"),
        new("integration", "tdp_export", "manifest_sha256", "hash of the signed TDP manifest, which is stored in the database; package bytes are never kept (ADR-0025)"),
        new("mbom", "iteration", "content_sha256", "hash over the canonical lines and links of an MBOM iteration (ADR-0024), not a vault object"),
        new("mbom", "reconciliation_run", "content_sha256", "digest of a stored reconciliation result (ADR-0024), not a vault object"),
        new("mbom", "review_binding", "review_sha256", "hash binding an MBOM review to its content and reconciliation run (ADR-0024), not a vault object"),
        new("rec", "archive", "manifest_sha256", "hash of the signed manifest inside an archive package (8.6), not a vault object"),
        new("rec", "certificate", "body_sha256", "hash of a certificate of destruction body (8.6), not a vault object"),
        new("rec", "destruction_confirmation", "sha256", "names content that was destroyed; a confirmation never keeps bytes alive (8.6)"),
        new("proc", "plan_iteration", "content_sha256", "digest over the canonical content of a process plan iteration (ADR-0042), not a vault object"),
        new("proc", "plan_revision", "bound_mbom_sha", "content digest of the released MBOM revision a plan revision is written against (ADR-0042), not a vault object"),
        new("proc", "review_binding", "review_sha256", "hash binding a process plan review to its content and MBOM revision (ADR-0042), not a vault object"),
        new("rpt", "artifact_signature", "signed_digest", "SHA-256 of the signed byte ranges of a signed package PDF (ADR-0051); the artifact row is the vault reference, a signature record never keeps bytes alive"),
        new("ntf", "email_verification", "token_hash", "SHA-256 of a one-time e-mail confirmation token (ADR-0055); a token never refers to a vault object"),
        new("rdl", "review", "left_sha256", "names the bytes of the left document file that were compared; the document_file row is the reference, a review never keeps bytes alive (ADR-0053)"),
        new("rdl", "review", "right_sha256", "names the bytes of the right document file that were compared; the document_file row is the reference, a review never keeps bytes alive (ADR-0053)"),
        new("rdl", "annotation", "change_digest", "SHA-256 over the deleted and inserted text of one change of a word diff (WordDiff.ChangeDigest); not a vault object (ADR-0053)"),
        new("mdl", "iteration_metrics", "file_sha256", "names the bytes that were measured; the document_file row is the reference, a measurement never keeps bytes alive (ADR-0046)"),
        new("audit", "event", "hash", "audit chain"),
        new("audit", "event", "prev_hash", "audit chain"),
        new("audit", "checkpoint", "hash", "audit checkpoint"),
        new("vault", "gc_run", "report_hash", "hash of a garbage collection report"),
    ];

    /// <summary>
    /// SQL returning the subset of <c>@keys</c> (text[]) that any reference source still names. Built from <see cref="Sources"/> only:
    /// there is no other place that decides what keeps an object alive.
    /// </summary>
    public static string ReferencedKeysSql { get; } =
        "SELECT k FROM unnest(@keys::text[]) AS k WHERE " +
        string.Join(" OR ", Sources.Select(s => $"EXISTS (SELECT 1 FROM {s.Schema}.{s.Table} r WHERE r.{s.Column} = k{(s.LivePredicate is null ? "" : " AND " + s.LivePredicate)})"));

    /// <summary>Of <paramref name="keys"/>, those referenced by any source.</summary>
    public static async Task<HashSet<string>> ReferencedAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        if (keys.Count == 0) return referenced;
        await using var cmd = new NpgsqlCommand(ReferencedKeysSql, conn, tx);
        cmd.Parameters.AddWithValue("keys", keys.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) referenced.Add(r.GetString(0));
        return referenced;
    }

    /// <summary>SQL that takes the per-key lock shared by uploads and the sweeper (transaction scoped). Parameter <c>@k</c> = content key.</summary>
    internal const string LockKeySql = "SELECT pg_advisory_xact_lock(hashtextextended('keel-vault:' || @k, 0))";
}

/// <summary>Called by the vault once the content address of an upload is known and before it relies on that object (ADR-0026).</summary>
public interface IVaultPendingGuard
{
    /// <summary>Records that an upload is about to reference <paramref name="sha256"/>; the sweeper keeps the object while the marker lives.</summary>
    Task MarkPendingAsync(string sha256, CancellationToken ct = default);
}

/// <summary>
/// Pending-upload markers in <c>vault.pending_upload</c>. The marker is written under the same per-key advisory lock the sweeper takes
/// for its final reference check and delete, so the two serialize: either the marker is visible to the sweeper (it keeps the object),
/// or the sweeper already deleted the object before the upload looks for it (the upload then writes it again).
/// </summary>
public sealed class PostgresVaultPendingGuard(NpgsqlDataSource dataSource, TimeProvider clock) : IVaultPendingGuard
{
    /// <summary>How long a marker protects an object: far longer than the window between the vault write and the metadata commit.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromHours(2);

    public async Task MarkPendingAsync(string sha256, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand(VaultReferences.LockKeySql, conn, tx))
        {
            lockCmd.Parameters.AddWithValue("k", sha256);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }
        var now = clock.GetUtcNow();
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO vault.pending_upload (sha256, marked_at, expires_at) VALUES (@k, @now, @exp)
            ON CONFLICT (sha256) DO UPDATE SET marked_at = EXCLUDED.marked_at, expires_at = EXCLUDED.expires_at
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("k", sha256);
            cmd.Parameters.AddWithValue("now", now);
            cmd.Parameters.AddWithValue("exp", now + Lease);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
