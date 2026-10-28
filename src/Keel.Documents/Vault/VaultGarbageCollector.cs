using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Keel.Audit;
using Npgsql;

namespace Keel.Documents.Vault;

/// <summary>What one sweep is asked to do. <paramref name="Prefix"/> restricts it to keys that start with it (a slice of a huge bucket).</summary>
public sealed record GcRequest(GcMode Mode, TimeSpan Grace, int MaxDeletions, int PageSize, string? ContinuationToken, ActorSnapshot Actor, string? CorrelationId = null, string? Prefix = null);

/// <summary>Result of one sweep. Counts, bytes and the keys of up to <see cref="VaultGarbageCollector.SampleSize"/> candidates: never file names or markings.</summary>
public sealed record GcReport(Guid RunId, GcMode Mode, int GraceHours, int MaxDeletions, long ObjectsListed, long ObjectsReferenced, long ObjectsYoung,
    long ObjectsUnrecognized, long Candidates, long Deleted, long SkippedByRecheck, long BytesCandidates, long BytesDeleted, IReadOnlyList<string> SampleKeys,
    string ReportHash, string? Continuation, bool Complete, DateTimeOffset StartedAt, DateTimeOffset FinishedAt);

/// <summary>Another sweep holds the run lock (a second replica, a CLI run beside the hosted service).</summary>
public sealed class GcAlreadyRunningException() : Exception("Another vault garbage collection run is in progress.");

/// <summary>
/// Sweeps orphaned vault objects (ADR-0026). Document file rows are immutable and never deleted, so a true orphan comes from an upload whose
/// metadata never committed (or a staging leftover). An object is deleted only when it is (a) named by no reference source of
/// <see cref="VaultReferences"/>, (b) older than the grace period and (c) still unreferenced, unmarked and old when re-checked inside a
/// transaction that holds the key lock uploads also take. Dry-run is the default; a run is capped and resumable.
/// </summary>
public sealed class VaultGarbageCollector(ObjectVault vault, NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock)
{
    public const int SampleSize = 20;

    /// <summary>Test seam: runs inside the key lock, after the final reference check and before the object is deleted.</summary>
    internal Func<string, Task>? InsideLockHook { get; set; }
    /// <summary>Advisory lock key serializing sweeps: ASCII "KEELGCK1".</summary>
    private const long RunLockKey = 0x4B45454C47434B31;

    public async Task<GcReport> RunAsync(GcRequest request, CancellationToken ct = default)
    {
        if (request.Grace < TimeSpan.FromDays(GcOptions.MinGraceDays))
            throw new ArgumentOutOfRangeException(nameof(request), $"The grace period must be at least {GcOptions.MinGraceDays} day(s).");
        if (request.MaxDeletions < 1) throw new ArgumentOutOfRangeException(nameof(request), "MaxDeletions must be at least 1.");

        // One sweep at a time across replicas and tools: a session-level advisory lock on a dedicated connection.
        await using var lockConn = await dataSource.OpenConnectionAsync(ct);
        await using (var tryLock = new NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", lockConn))
        {
            tryLock.Parameters.AddWithValue("k", RunLockKey);
            if (!(bool)(await tryLock.ExecuteScalarAsync(ct))!) throw new GcAlreadyRunningException();
        }
        try
        {
            return await SweepAsync(request, ct);
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", lockConn);
            unlock.Parameters.AddWithValue("k", RunLockKey);
            await unlock.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    private async Task<GcReport> SweepAsync(GcRequest request, CancellationToken ct)
    {
        var started = clock.GetUtcNow();
        var delete = request.Mode == GcMode.Delete;
        long listed = 0, referenced = 0, young = 0, unrecognized = 0, candidates = 0, deleted = 0, skipped = 0, bytesCandidates = 0, bytesDeleted = 0;
        var sample = new List<string>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string? continuation = request.ContinuationToken;
        var complete = false;
        var capped = false;

        while (!complete && !capped)
        {
            var page = await vault.ListAsync(continuation, request.PageSize, request.Prefix, ct);
            var contentKeys = page.Objects.Where(o => ObjectVault.IsContentKey(o.Key)).Select(o => o.Key).ToList();
            HashSet<string> referencedKeys;
            await using (var conn = await dataSource.OpenConnectionAsync(ct))
                referencedKeys = await VaultReferences.ReferencedAsync(conn, null, contentKeys, ct);

            foreach (var o in page.Objects)
            {
                listed++;
                var isContent = ObjectVault.IsContentKey(o.Key);
                if (!isContent && !ObjectVault.IsStagingKey(o.Key)) { unrecognized++; continuation = o.Key; continue; }
                if (isContent && referencedKeys.Contains(o.Key)) { referenced++; continuation = o.Key; continue; }
                if (started - o.LastModified < request.Grace) { young++; continuation = o.Key; continue; }

                candidates++;
                bytesCandidates += o.SizeBytes;
                hash.AppendData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{o.Key}\t{o.SizeBytes}\n")));
                if (sample.Count < SampleSize) sample.Add(o.Key);
                if (delete)
                {
                    if (await TryDeleteAsync(o, request.Grace, ct)) { deleted++; bytesDeleted += o.SizeBytes; }
                    else skipped++;
                }
                continuation = o.Key;
                // The cap counts candidates handled, in dry-run and delete mode alike, so a dry-run previews exactly what a delete run with the same limit does.
                if (candidates >= request.MaxDeletions)
                {
                    // The page is not finished: the next run resumes after this key.
                    capped = true;
                    break;
                }
            }
            if (!capped && page.NextToken is null) { complete = true; continuation = null; }
        }
        hash.AppendData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"listed={listed};referenced={referenced};young={young};unrecognized={unrecognized};candidates={candidates};bytes={bytesCandidates};graceHours={(int)request.Grace.TotalHours};max={request.MaxDeletions}\n")));
        var reportHash = Convert.ToHexStringLower(hash.GetHashAndReset());

        var finished = clock.GetUtcNow();
        var report = new GcReport(Guid.CreateVersion7(), request.Mode, (int)request.Grace.TotalHours, request.MaxDeletions, listed, referenced, young, unrecognized,
            candidates, deleted, skipped, bytesCandidates, bytesDeleted, sample, reportHash, complete ? null : continuation, complete, started, finished);
        await RecordAsync(request, report, ct);
        return report;
    }

    /// <summary>
    /// The decision that matters: under the key lock uploads of the same bytes also take, look again at references, pending markers and age,
    /// then delete. Returns false when the object was kept (or already gone).
    /// </summary>
    private async Task<bool> TryDeleteAsync(ObjectVault.ObjectEntry entry, TimeSpan grace, CancellationToken ct)
    {
        if (ObjectVault.IsStagingKey(entry.Key))
        {
            var head = await vault.HeadAsync(entry.Key, ct);
            if (head is null || clock.GetUtcNow() - head.LastModified < grace) return false;
            await vault.DeleteObjectAsync(entry.Key, ct);
            return true;
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand(VaultReferences.LockKeySql, conn, tx))
        {
            lockCmd.Parameters.AddWithValue("k", entry.Key);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }
        if ((await VaultReferences.ReferencedAsync(conn, tx, [entry.Key], ct)).Count > 0) return false;
        await using (var pending = new NpgsqlCommand("SELECT 1 FROM vault.pending_upload WHERE sha256 = @k AND expires_at > @now", conn, tx))
        {
            pending.Parameters.AddWithValue("k", entry.Key);
            pending.Parameters.AddWithValue("now", clock.GetUtcNow());
            if (await pending.ExecuteScalarAsync(ct) is not null) return false;
        }
        var current = await vault.HeadAsync(entry.Key, ct);
        if (current is null || clock.GetUtcNow() - current.LastModified < grace) return false;
        if (InsideLockHook is { } hook) await hook(entry.Key);
        await vault.DeleteObjectAsync(entry.Key, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task RecordAsync(GcRequest request, GcReport r, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO vault.gc_run (id, started_at, finished_at, started_by, mode, grace_hours, max_deletions, objects_listed, objects_referenced, candidates,
                                      deleted, bytes_candidates, bytes_deleted, report_hash, continuation, complete, sample_keys)
            VALUES (@id, @s, @f, @by, @mode, @grace, @max, @listed, @ref, @cand, @del, @bc, @bd, @hash, @cont, @complete, @sample);
            DELETE FROM vault.pending_upload WHERE expires_at < @f;
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", r.RunId);
            cmd.Parameters.AddWithValue("s", r.StartedAt);
            cmd.Parameters.AddWithValue("f", r.FinishedAt);
            cmd.Parameters.AddWithValue("by", request.Actor.Id);
            cmd.Parameters.AddWithValue("mode", r.Mode == GcMode.Delete ? "delete" : "dry-run");
            cmd.Parameters.AddWithValue("grace", r.GraceHours);
            cmd.Parameters.AddWithValue("max", r.MaxDeletions);
            cmd.Parameters.AddWithValue("listed", r.ObjectsListed);
            cmd.Parameters.AddWithValue("ref", r.ObjectsReferenced);
            cmd.Parameters.AddWithValue("cand", r.Candidates);
            cmd.Parameters.AddWithValue("del", r.Deleted);
            cmd.Parameters.AddWithValue("bc", r.BytesCandidates);
            cmd.Parameters.AddWithValue("bd", r.BytesDeleted);
            cmd.Parameters.AddWithValue("hash", r.ReportHash);
            cmd.Parameters.AddWithValue("cont", (object?)r.Continuation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("complete", r.Complete);
            cmd.Parameters.AddWithValue("sample", r.SampleKeys.ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, new AuditEntry
        {
            Actor = request.Actor, Action = "vault.gc", Outcome = AuditOutcome.Success, ObjectType = "vault", ObjectId = r.RunId.ToString("D"),
            CorrelationId = request.CorrelationId,
            Details = AuditDetails.Of(
                ("mode", r.Mode == GcMode.Delete ? "delete" : "dry-run"), ("graceHours", Num(r.GraceHours)), ("maxDeletions", Num(r.MaxDeletions)),
                ("listed", Num(r.ObjectsListed)), ("referenced", Num(r.ObjectsReferenced)), ("young", Num(r.ObjectsYoung)), ("candidates", Num(r.Candidates)),
                ("deleted", Num(r.Deleted)), ("skippedByRecheck", Num(r.SkippedByRecheck)), ("bytesCandidates", Num(r.BytesCandidates)), ("bytesDeleted", Num(r.BytesDeleted)),
                ("reportHash", r.ReportHash), ("complete", r.Complete ? "true" : "false"), ("continuation", r.Continuation)),
        }, ct);
        await tx.CommitAsync(ct);
    }

    private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);
}
