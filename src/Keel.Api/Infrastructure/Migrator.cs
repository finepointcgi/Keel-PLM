using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Keel.Api.Infrastructure;

/// <summary>
/// Applies db/migrations/NNNN_name.sql in order, each in its own transaction, as the schema owner.
/// Forward-only: an already-applied file whose content changed is an error, never silently re-run.
/// </summary>
public sealed class Migrator(string connectionString, string migrationsDirectory, string appRole, ILogger logger)
{
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "migrations");

    public static IReadOnlyList<(string Version, string Path)> Discover(string directory) =>
        Directory.GetFiles(directory, "*.sql")
            .Select(p => (Version: Path.GetFileNameWithoutExtension(p), Path: p))
            .OrderBy(m => m.Version, StringComparer.Ordinal)
            .ToList();

    public async Task<int> MigrateAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using (var bootstrap = new NpgsqlCommand(
            $"""
            CREATE TABLE IF NOT EXISTS public.schema_migrations (
                version    text PRIMARY KEY,
                sha256     text NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now());
            GRANT SELECT ON public.schema_migrations TO {Quote(appRole)};
            """, conn))
        {
            await bootstrap.ExecuteNonQueryAsync(ct);
        }

        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("SELECT version, sha256 FROM public.schema_migrations", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) applied[r.GetString(0)] = r.GetString(1);

        var count = 0;
        foreach (var (version, path) in Discover(migrationsDirectory))
        {
            var sql = await File.ReadAllTextAsync(path, ct);
            var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
            if (applied.TryGetValue(version, out var existing))
            {
                if (existing != sha) throw new InvalidOperationException($"Migration {version} was modified after it was applied.");
                continue;
            }

            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var run = new NpgsqlCommand(sql, conn, tx)) await run.ExecuteNonQueryAsync(ct);
            await using (var record = new NpgsqlCommand("INSERT INTO public.schema_migrations (version, sha256) VALUES (@v, @s)", conn, tx))
            {
                record.Parameters.AddWithValue("v", version);
                record.Parameters.AddWithValue("s", sha);
                await record.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            logger.LogInformation("Applied migration {Version}", version);
            count++;
        }
        return count;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
