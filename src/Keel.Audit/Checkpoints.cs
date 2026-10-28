using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Keel.Audit;

/// <summary>
/// Signs the current chain head with an ECDSA P-256 key the database never sees (ADR-0005), so a
/// database administrator who rewrites and re-hashes the chain is still detected.
/// </summary>
public sealed class CheckpointSigner(ECDsa privateKey, NpgsqlDataSource dataSource, TimeProvider clock)
{
    public string KeyId { get; } = CheckpointKeys.KeyId(privateKey);

    /// <summary>Returns the new checkpoint, or null when the log is empty or the head is already checkpointed.</summary>
    public async Task<AuditCheckpoint?> CreateAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@k)", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("k", PostgresAuditLog.ChainLockKey);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        long seq;
        byte[] hash;
        await using (var head = new NpgsqlCommand(
            "SELECT e.seq, e.hash, (SELECT max(seq) FROM audit.checkpoint) FROM audit.event e ORDER BY e.seq DESC LIMIT 1", conn, tx))
        await using (var r = await head.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            seq = r.GetInt64(0);
            hash = (byte[])r[1];
            if (!r.IsDBNull(2) && r.GetInt64(2) >= seq) return null;
        }

        var createdAt = AuditChain.Truncate(clock.GetUtcNow());
        var signature = privateKey.SignData(CheckpointKeys.Message(seq, hash, createdAt), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO audit.checkpoint (seq, hash, created_at, key_id, signature) VALUES (@seq, @hash, @at, @kid, @sig)", conn, tx))
        {
            insert.Parameters.AddWithValue("seq", seq);
            insert.Parameters.AddWithValue("hash", hash);
            insert.Parameters.AddWithValue("at", createdAt);
            insert.Parameters.AddWithValue("kid", KeyId);
            insert.Parameters.AddWithValue("sig", signature);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new AuditCheckpoint(seq, hash, createdAt, KeyId, signature);
    }
}

public sealed record AuditCheckpoint(long Seq, byte[] Hash, DateTimeOffset CreatedAt, string KeyId, byte[] Signature);

public static class CheckpointKeys
{
    /// <summary>Stable key identifier: first 16 hex characters of SHA-256 over the SubjectPublicKeyInfo.</summary>
    public static string KeyId(ECDsa key) =>
        Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16];

    public static byte[] Message(long seq, byte[] hash, DateTimeOffset createdAt) =>
        Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"keel-audit-checkpoint/v1\n{seq}\n{Convert.ToHexStringLower(hash)}\n{AuditChain.FormatTimestamp(createdAt)}"));

    public static ECDsa Generate() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static ECDsa LoadPem(string path)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(path));
        if (key.KeySize != 256) throw new CryptographicException("Checkpoint keys must be ECDSA P-256.");
        return key;
    }
}
