using Npgsql;

namespace Keel.Seed;

/// <summary>
/// The only place the seed tool reads a marked table directly. It runs as a deliberate, local administrative probe with no end user
/// (it asks one yes/no question: "is this a fresh database?"), so it does not go through the per-user read filter; every other seed
/// action goes through the module services. The architecture test exempts exactly this file.
/// </summary>
public static class DatabaseState
{
    public static async Task<bool> HasPartsAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("SELECT EXISTS (SELECT 1 FROM plm.part_master)");
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
