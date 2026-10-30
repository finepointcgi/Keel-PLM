using System.Globalization;
using Npgsql;

namespace Keel.Documents.Vault;

/// <summary>One referenced object that failed verification.</summary>
/// <param name="Sha256">The content address the database refers to.</param>
/// <param name="SizeBytes">The size the database recorded.</param>
/// <param name="Kind"><c>missing</c> (no object under the key) or <c>damaged</c> (size or SHA-256 differs from what the key names).</param>
/// <param name="Detail">The vault's own explanation.</param>
/// <param name="Sources">The database columns that refer to the object.</param>
public sealed record VaultVerifyProblem(string Sha256, long SizeBytes, string Kind, string Detail, IReadOnlyList<string> Sources);

/// <summary>Outcome of a bulk integrity pass over every object the database refers to.</summary>
public sealed record VaultVerifyReport(int References, int Distinct, int Verified, long BytesVerified, IReadOnlyList<VaultVerifyProblem> Problems)
{
    public bool Intact => Problems.Count == 0;
}

/// <summary>
/// Bulk vault verifier (ADR-0060): after a restore, a replica promotion or a storage migration, proves that every object the database still needs (the registry in
/// <see cref="VaultReferences.Sources"/>, honouring each source's live predicate) exists and hashes to its key at the recorded size. Read-only: it writes no audit event and
/// deletes nothing. It reads every referenced byte once, so cost grows with the vault; <c>parallelism</c> bounds the load.
/// </summary>
public static class VaultVerifier
{
    public static async Task<VaultVerifyReport> VerifyAsync(ObjectVault vault, NpgsqlDataSource db, int parallelism, CancellationToken ct, IReadOnlyList<VaultReferenceSource>? sources = null, IProgress<int>? progress = null)
    {
        sources ??= VaultReferences.Sources;
        var objects = new Dictionary<(string Sha, long Size), List<string>>();
        var references = 0;
        foreach (var s in sources)
        {
            ValidateIdentifier(s.Schema); ValidateIdentifier(s.Table); ValidateIdentifier(s.Column); ValidateIdentifier(s.SizeColumn);
            var where = s.LivePredicate is null ? "" : " WHERE " + s.LivePredicate;
            await using var cmd = db.CreateCommand($"SELECT r.{s.Column}, r.{s.SizeColumn} FROM {s.Schema}.{s.Table} r{where}");
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                references++;
                var key = (reader.GetString(0), reader.GetInt64(1));
                if (!objects.TryGetValue(key, out var list)) objects[key] = list = [];
                if (!list.Contains(s.Qualified, StringComparer.Ordinal)) list.Add(s.Qualified);
            }
        }

        var problems = new System.Collections.Concurrent.ConcurrentBag<VaultVerifyProblem>();
        var verified = 0;
        long bytes = 0;
        var done = 0;
        await Parallel.ForEachAsync(objects, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(parallelism, 1, 32), CancellationToken = ct }, async (kv, token) =>
        {
            var ((sha, size), list) = (kv.Key, kv.Value);
            try
            {
                await vault.VerifyAsync(sha, size, token);
                Interlocked.Increment(ref verified);
                Interlocked.Add(ref bytes, size);
            }
            catch (VaultIntegrityException e)
            {
                var missing = e.Message.Contains("missing", StringComparison.OrdinalIgnoreCase);
                problems.Add(new VaultVerifyProblem(sha, size, missing ? "missing" : "damaged", e.Message, list));
            }
            progress?.Report(Interlocked.Increment(ref done));
        });
        return new VaultVerifyReport(references, objects.Count, verified, bytes, [.. problems.OrderBy(p => p.Sha256, StringComparer.Ordinal)]);
    }

    // The registry is code, not input, but identifiers are still checked before they are placed into SQL text.
    private static void ValidateIdentifier(string id)
    {
        if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"'{id}' is not a plain SQL identifier."), nameof(id));
    }
}
