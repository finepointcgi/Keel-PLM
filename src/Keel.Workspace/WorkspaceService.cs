using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Keel.Audit;
using Keel.Core;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Keel.Workspace;

/// <summary>
/// Persistence of one user's personal workspace data (ADR-0028): display preferences, table layouts, recently viewed objects, favorites and saved
/// searches. Ownership is structural: every method takes the signed-in <see cref="Subject"/> and every statement filters on its id, so no method
/// can return another user's rows. The service stores pointers only; whether a pointer may still be shown is decided by the callers (Keel.Web),
/// which re-read each object through the module services under the viewer's current access.
/// </summary>
public sealed class WorkspaceService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock) : IPreferenceStore
{
    // ---------------------------------------------------------------- preferences

    public async Task<UserPreferences> GetPreferencesAsync(Subject subject, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT theme, density, nav_collapsed FROM ws.preference WHERE user_sub = @u", conn);
        cmd.Parameters.AddWithValue("u", subject.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? UserPreferences.Normalize(r.GetString(0), r.GetString(1), r.GetBoolean(2)) : UserPreferences.Default;
    }

    /// <summary>Same lookup by the bare subject id, for the static page shell, which has the id from the session cookie but no <see cref="Subject"/>.</summary>
    public async Task<UserPreferences> GetPreferencesForShellAsync(string userSub, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userSub) || userSub.Length > 200) return UserPreferences.Default;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT theme, density, nav_collapsed FROM ws.preference WHERE user_sub = @u", conn);
        cmd.Parameters.AddWithValue("u", userSub);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? UserPreferences.Normalize(r.GetString(0), r.GetString(1), r.GetBoolean(2)) : UserPreferences.Default;
    }

    public async Task<UserPreferences> SetPreferencesAsync(Subject subject, UserPreferences preferences, CancellationToken ct = default)
    {
        var p = UserPreferences.Normalize(preferences.Theme, preferences.Density, preferences.NavCollapsed);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO ws.preference (user_sub, theme, density, nav_collapsed, updated_at) VALUES (@u, @t, @d, @n, @at)
            ON CONFLICT (user_sub) DO UPDATE SET theme = @t, density = @d, nav_collapsed = @n, updated_at = @at
            """, conn);
        cmd.Parameters.AddWithValue("u", subject.Id);
        cmd.Parameters.AddWithValue("t", p.Theme);
        cmd.Parameters.AddWithValue("d", p.Density);
        cmd.Parameters.AddWithValue("n", p.NavCollapsed);
        cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
        await cmd.ExecuteNonQueryAsync(ct);
        return p;
    }

    // ---------------------------------------------------------------- table preferences

    public async Task<TablePreferences> GetTablePreferencesAsync(Subject subject, string tableId, CancellationToken ct = default)
    {
        if (!ValidTableId(tableId)) return TablePreferences.Default;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT hidden_columns, density, sort_column, sort_descending FROM ws.table_preference WHERE user_sub = @u AND table_id = @t", conn);
        cmd.Parameters.AddWithValue("u", subject.Id);
        cmd.Parameters.AddWithValue("t", tableId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return TablePreferences.Default;
        var density = r.IsDBNull(1) ? null : r.GetString(1);
        return new TablePreferences((string[])r.GetValue(0), density is not null && DensityNames.All.Contains(density) ? density : null,
            r.IsDBNull(2) ? null : r.GetString(2), r.GetBoolean(3));
    }

    /// <summary>Saves a table layout. Returns false when the user already keeps layouts for the maximum number of tables.</summary>
    public async Task<bool> SetTablePreferencesAsync(Subject subject, string tableId, TablePreferences prefs, CancellationToken ct = default)
    {
        if (!ValidTableId(tableId)) return false;
        var hidden = prefs.HiddenColumns.Where(c => c.Length is > 0 and <= 64).Distinct(StringComparer.Ordinal).Take(WorkspaceLimits.HiddenColumns).ToArray();
        var density = prefs.Density is not null && DensityNames.All.Contains(prefs.Density) ? prefs.Density : null;
        var sort = prefs.SortColumn is { Length: > 0 and <= 64 } s ? s : null;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockUserAsync(conn, tx, subject.Id, ct);
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM ws.table_preference WHERE user_sub = @u AND table_id <> @t", conn, tx))
        {
            count.Parameters.AddWithValue("u", subject.Id);
            count.Parameters.AddWithValue("t", tableId);
            if ((long)(await count.ExecuteScalarAsync(ct))! >= WorkspaceLimits.TablePreferences) return false;
        }
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO ws.table_preference (user_sub, table_id, hidden_columns, density, sort_column, sort_descending, updated_at)
            VALUES (@u, @t, @h, @d, @s, @sd, @at)
            ON CONFLICT (user_sub, table_id) DO UPDATE SET hidden_columns = @h, density = @d, sort_column = @s, sort_descending = @sd, updated_at = @at
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("u", subject.Id);
            cmd.Parameters.AddWithValue("t", tableId);
            cmd.Parameters.Add(new NpgsqlParameter("h", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = hidden });
            cmd.Parameters.AddWithValue("d", (object?)density ?? DBNull.Value);
            cmd.Parameters.AddWithValue("s", (object?)sort ?? DBNull.Value);
            cmd.Parameters.AddWithValue("sd", prefs.SortDescending);
            cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }

    public static bool ValidTableId(string? id) =>
        id is { Length: > 0 and <= 64 } && char.IsAsciiLetterLower(id[0]) && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-');

    // ---------------------------------------------------------------- recently viewed

    /// <summary>
    /// Notes that the user opened an object: one row per object, the newest 50 kept. A view of the same object within the debounce window does not
    /// write. Views are not audited here; the read of the object is audited by the module that served it.
    /// </summary>
    public async Task RecordViewAsync(Subject subject, string kind, string number, CancellationToken ct = default)
    {
        if (!ObjectKinds.IsKnown(kind) || !ValidNumber(number)) return;
        var now = clock.GetUtcNow();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockUserAsync(conn, tx, subject.Id, ct);
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO ws.recent_item (user_sub, object_type, object_number, last_viewed) VALUES (@u, @k, @n, @at)
            ON CONFLICT (user_sub, object_type, object_number) DO UPDATE SET last_viewed = @at WHERE ws.recent_item.last_viewed < @cutoff
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("u", subject.Id);
            cmd.Parameters.AddWithValue("k", kind);
            cmd.Parameters.AddWithValue("n", number);
            cmd.Parameters.AddWithValue("at", now);
            cmd.Parameters.AddWithValue("cutoff", now - WorkspaceLimits.ViewDebounce);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var trim = new NpgsqlCommand(
            """
            DELETE FROM ws.recent_item WHERE user_sub = @u AND (object_type, object_number) IN
                (SELECT object_type, object_number FROM ws.recent_item WHERE user_sub = @u ORDER BY last_viewed DESC, object_number OFFSET @keep)
            """, conn, tx))
        {
            trim.Parameters.AddWithValue("u", subject.Id);
            trim.Parameters.AddWithValue("keep", WorkspaceLimits.RecentItems);
            await trim.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ObjectPointer>> ListRecentAsync(Subject subject, int limit = WorkspaceLimits.RecentItems, CancellationToken ct = default) =>
        await ListPointersAsync("SELECT object_type, object_number, last_viewed FROM ws.recent_item WHERE user_sub = @u ORDER BY last_viewed DESC, object_number LIMIT @l",
            subject, limit, WorkspaceLimits.RecentItems, ct);

    public async Task ClearRecentAsync(Subject subject, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM ws.recent_item WHERE user_sub = @u", conn);
        cmd.Parameters.AddWithValue("u", subject.Id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- favorites

    public async Task<IReadOnlyList<ObjectPointer>> ListFavoritesAsync(Subject subject, int limit = WorkspaceLimits.Favorites, CancellationToken ct = default) =>
        await ListPointersAsync("SELECT object_type, object_number, created_at FROM ws.favorite WHERE user_sub = @u ORDER BY created_at DESC, object_number LIMIT @l",
            subject, limit, WorkspaceLimits.Favorites, ct);

    public async Task<bool> IsFavoriteAsync(Subject subject, string kind, string number, CancellationToken ct = default)
    {
        if (!ObjectKinds.IsKnown(kind) || !ValidNumber(number)) return false;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM ws.favorite WHERE user_sub = @u AND object_type = @k AND object_number = @n)", conn);
        cmd.Parameters.AddWithValue("u", subject.Id);
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("n", number);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Stars an object. Returns false when the user already has the maximum number of favorites. The caller has checked that the user can read the object.</summary>
    public async Task<bool> AddFavoriteAsync(Subject subject, string kind, string number, CancellationToken ct = default)
    {
        if (!ObjectKinds.IsKnown(kind) || !ValidNumber(number)) return false;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockUserAsync(conn, tx, subject.Id, ct);
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM ws.favorite WHERE user_sub = @u AND NOT (object_type = @k AND object_number = @n)", conn, tx))
        {
            count.Parameters.AddWithValue("u", subject.Id);
            count.Parameters.AddWithValue("k", kind);
            count.Parameters.AddWithValue("n", number);
            if ((long)(await count.ExecuteScalarAsync(ct))! >= WorkspaceLimits.Favorites) return false;
        }
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO ws.favorite (user_sub, object_type, object_number, created_at) VALUES (@u, @k, @n, @at) ON CONFLICT DO NOTHING", conn, tx))
        {
            cmd.Parameters.AddWithValue("u", subject.Id);
            cmd.Parameters.AddWithValue("k", kind);
            cmd.Parameters.AddWithValue("n", number);
            cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task RemoveFavoriteAsync(Subject subject, string kind, string number, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM ws.favorite WHERE user_sub = @u AND object_type = @k AND object_number = @n", conn);
        cmd.Parameters.AddWithValue("u", subject.Id);
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("n", number);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- saved searches

    public async Task<IReadOnlyList<SavedSearch>> ListSavedSearchesAsync(Subject subject, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, name, scope, query, filters::text, created_at FROM ws.saved_search WHERE user_sub = @u ORDER BY created_at DESC, name", conn);
        cmd.Parameters.AddWithValue("u", subject.Id);
        var list = new List<SavedSearch>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new SavedSearch(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5)));
        return list;
    }

    private static readonly string[] SearchScopes = ["all", .. ObjectKinds.All];

    /// <summary>
    /// Saves (or replaces, when the name exists) a search of the user. The audit record names the scope and a SHA-256 of the text, never the text:
    /// a saved search may contain a sensitive term.
    /// </summary>
    public async Task<SaveSearchResult> SaveSearchAsync(Subject subject, string name, string scope, string query, string? filtersJson, string? correlationId, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        query = (query ?? "").Trim();
        filtersJson = string.IsNullOrWhiteSpace(filtersJson) ? "{}" : filtersJson;
        if (name.Length is 0 or > 80 || query.Length is 0 or > 200 || !SearchScopes.Contains(scope) || filtersJson.Length > 1500 || !IsJsonObject(filtersJson))
            return SaveSearchResult.Invalid;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockUserAsync(conn, tx, subject.Id, ct);
        Guid? existing;
        await using (var find = new NpgsqlCommand("SELECT id FROM ws.saved_search WHERE user_sub = @u AND name = @n", conn, tx))
        {
            find.Parameters.AddWithValue("u", subject.Id);
            find.Parameters.AddWithValue("n", name);
            existing = (Guid?)await find.ExecuteScalarAsync(ct);
        }
        var id = existing ?? Guid.CreateVersion7();
        if (existing is null)
        {
            await using var count = new NpgsqlCommand("SELECT count(*) FROM ws.saved_search WHERE user_sub = @u", conn, tx);
            count.Parameters.AddWithValue("u", subject.Id);
            if ((long)(await count.ExecuteScalarAsync(ct))! >= WorkspaceLimits.SavedSearches) return SaveSearchResult.LimitReached;
        }
        await using (var cmd = new NpgsqlCommand(
            existing is null
                ? "INSERT INTO ws.saved_search (id, user_sub, name, scope, query, filters, created_at) VALUES (@id, @u, @n, @s, @q, @f::jsonb, @at)"
                : "UPDATE ws.saved_search SET scope = @s, query = @q, filters = @f::jsonb WHERE id = @id AND user_sub = @u AND name = @n", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("u", subject.Id);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("s", scope);
            cmd.Parameters.AddWithValue("q", query);
            cmd.Parameters.AddWithValue("f", filtersJson);
            cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, SearchEntry(subject, existing is null ? "workspace.saved_search.create" : "workspace.saved_search.replace", id, scope, query, correlationId), ct);
        await tx.CommitAsync(ct);
        return existing is null ? SaveSearchResult.Saved : SaveSearchResult.Replaced;
    }

    public async Task<bool> DeleteSavedSearchAsync(Subject subject, Guid id, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        string? scope, query;
        await using (var cmd = new NpgsqlCommand("DELETE FROM ws.saved_search WHERE id = @id AND user_sub = @u RETURNING scope, query", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("u", subject.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return false;
            scope = r.GetString(0);
            query = r.GetString(1);
        }
        await audit.AppendAsync(tx, SearchEntry(subject, "workspace.saved_search.delete", id, scope, query, correlationId), ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>The only trace of a saved search's text in the audit log.</summary>
    public static string QueryHash(string query) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(query)));

    private static AuditEntry SearchEntry(Subject s, string action, Guid id, string scope, string query, string? correlationId) => new()
    {
        Actor = ActorSnapshot.From(s),
        Action = action,
        Outcome = AuditOutcome.Success,
        ObjectType = "saved_search",
        ObjectId = id.ToString("D"),
        CorrelationId = correlationId,
        Details = AuditDetails.Of(("scope", scope), ("queryHash", QueryHash(query))),
    };

    // ---------------------------------------------------------------- helpers

    private static bool ValidNumber(string? n) => n is { Length: > 0 and <= 64 };

    private static bool IsJsonObject(string json)
    {
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.ValueKind == JsonValueKind.Object; }
        catch (JsonException) { return false; }
    }

    private async Task<IReadOnlyList<ObjectPointer>> ListPointersAsync(string sql, Subject subject, int limit, int max, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("u", subject.Id);
        cmd.Parameters.AddWithValue("l", Math.Clamp(limit, 1, max));
        var list = new List<ObjectPointer>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ObjectPointer(r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2)));
        return list;
    }

    /// <summary>Serializes the writes of one user so the caps cannot be exceeded by concurrent requests (a transaction-scoped advisory lock).</summary>
    private static async Task LockUserAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string userSub, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", conn, tx);
        cmd.Parameters.AddWithValue("k", "ws:" + userSub);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public static class WorkspaceModule
{
    public static IServiceCollection AddKeelWorkspace(this IServiceCollection services) =>
        services.AddSingleton(sp => new WorkspaceService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>()))
            .AddSingleton<IPreferenceStore>(sp => sp.GetRequiredService<WorkspaceService>());
}
