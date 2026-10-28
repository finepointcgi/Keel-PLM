using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace Keel.Audit;

public sealed record VerificationResult(
    bool Valid,
    long EventsChecked,
    int CheckpointsChecked,
    long? LastSeq,
    string? LastHash,
    IReadOnlyList<string> Failures);

/// <summary>Walks the whole chain and every checkpoint and reports what is broken (ADR-0005).</summary>
public static class AuditVerifier
{
    private const int MaxReportedFailures = 100;

    /// <param name="trustedKeys">Public keys accepted for checkpoint signatures.</param>
    public static async Task<VerificationResult> VerifyAsync(NpgsqlConnection conn, IReadOnlyCollection<ECDsa> trustedKeys, CancellationToken ct = default)
    {
        var failures = new List<string>();
        void Fail(string message)
        {
            if (failures.Count < MaxReportedFailures) failures.Add(message);
        }

        var keys = trustedKeys.ToDictionary(CheckpointKeys.KeyId, k => k);
        var checkpoints = new Dictionary<long, List<AuditCheckpoint>>();
        await using (var cmd = new NpgsqlCommand("SELECT seq, hash, created_at, key_id, signature, id FROM audit.checkpoint ORDER BY id", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var cp = new AuditCheckpoint(r.GetInt64(0), (byte[])r[1], new DateTimeOffset(r.GetDateTime(2), TimeSpan.Zero), r.GetString(3), (byte[])r[4]);
                var id = r.GetInt64(5);
                if (!keys.TryGetValue(cp.KeyId, out var key))
                    Fail($"checkpoint {id}: signed by unknown key {cp.KeyId}");
                else if (!key.VerifyData(CheckpointKeys.Message(cp.Seq, cp.Hash, cp.CreatedAt), cp.Signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                    Fail($"checkpoint {id}: signature invalid");
                if (!checkpoints.TryGetValue(cp.Seq, out var list)) checkpoints[cp.Seq] = list = [];
                list.Add(cp);
            }
        }

        long expected = 1;
        long count = 0;
        var prev = AuditChain.GenesisHash;
        await using (var cmd = new NpgsqlCommand($"SELECT {AuditQueries.Columns} FROM audit.event ORDER BY seq", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var e = AuditQueries.Read(r);
                count++;
                if (e.Seq != expected) Fail($"seq {e.Seq}: expected {expected} (events missing or reordered)");
                if (!e.PrevHash.AsSpan().SequenceEqual(prev)) Fail($"seq {e.Seq}: prev_hash does not match the preceding event");
                var recomputed = AuditChain.ComputeHash(e.PrevHash, e.Seq, e.Payload);
                if (!recomputed.AsSpan().SequenceEqual(e.Hash)) Fail($"seq {e.Seq}: hash does not match payload");
                CheckColumns(e, Fail);
                if (checkpoints.TryGetValue(e.Seq, out var cps))
                    foreach (var cp in cps.Where(cp => !cp.Hash.AsSpan().SequenceEqual(e.Hash)))
                        Fail($"seq {e.Seq}: does not match checkpoint signed at {AuditChain.FormatTimestamp(cp.CreatedAt)} (chain rewritten)");
                prev = e.Hash;
                expected = e.Seq + 1;
            }
        }

        var lastSeq = count == 0 ? (long?)null : expected - 1;
        foreach (var seq in checkpoints.Keys.Where(s => lastSeq is null || s > lastSeq))
            Fail($"checkpoint at seq {seq} is beyond the last event {lastSeq?.ToString(CultureInfo.InvariantCulture) ?? "(none)"} (events deleted)");

        return new VerificationResult(failures.Count == 0, count, checkpoints.Values.Sum(l => l.Count), lastSeq,
            count == 0 ? null : Convert.ToHexStringLower(prev), failures);
    }

    private static void CheckColumns(AuditRecord e, Action<string> fail)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.Payload);
            var p = doc.RootElement;
            static string? S(JsonElement el, string name) => el.GetProperty(name).ValueKind == JsonValueKind.Null ? null : el.GetProperty(name).GetString();
            if (S(p, "id") != e.Id.ToString("D")) fail($"seq {e.Seq}: id column differs from payload");
            if (S(p, "occurredAt") != AuditChain.FormatTimestamp(e.OccurredAt)) fail($"seq {e.Seq}: occurred_at column differs from payload");
            if (S(p, "action") != e.Action) fail($"seq {e.Seq}: action column differs from payload");
            if (S(p, "outcome") != e.Outcome.ToString()) fail($"seq {e.Seq}: outcome column differs from payload");
            if (S(p, "objectType") != e.ObjectType) fail($"seq {e.Seq}: object_type column differs from payload");
            if (S(p, "objectId") != e.ObjectId) fail($"seq {e.Seq}: object_id column differs from payload");
            if (S(p, "correlationId") != e.CorrelationId) fail($"seq {e.Seq}: correlation_id column differs from payload");
            if (S(p.GetProperty("actor"), "id") != e.Actor) fail($"seq {e.Seq}: actor column differs from payload");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            fail($"seq {e.Seq}: payload is not a valid v{AuditChain.PayloadVersion} audit document");
        }
    }
}
