using Keel.Core;
using Npgsql;

namespace Keel.Workspace;

/// <summary>The form fields whose last choice is remembered per user (ADR 0061). The set is closed (the table has a CHECK on it too).</summary>
public static class LastChoiceKeys
{
    public const string PartType = "part.type";
    public const string PartUnit = "part.unit";
    public const string PartSource = "part.source";
    public const string DocumentType = "document.type";
    public const string LinkType = "link.type";

    public static readonly IReadOnlyList<string> All = [PartType, PartUnit, PartSource, DocumentType, LinkType];
}

/// <summary>
/// The user's last pick for a few form fields, so the quick forms open with the values the person used last (ADR 0061). Convenience data only: a value is
/// a code from a list (never object data, never markings), it is checked against the live list before it is used, and failing to read or write it never
/// stops the form. Ownership is structural like the rest of the workspace: every statement filters on the subject's id.
/// </summary>
public sealed class LastChoiceStore(NpgsqlDataSource dataSource, TimeProvider clock)
{
    public async Task<IReadOnlyDictionary<string, string>> GetAsync(Subject subject, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT key, value FROM ws.last_choice WHERE user_sub = @u", conn);
        cmd.Parameters.AddWithValue("u", subject.Id);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    /// <summary>Remembers the choices (unknown keys and blank or oversized values are ignored).</summary>
    public async Task SetAsync(Subject subject, IReadOnlyDictionary<string, string> choices, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        foreach (var (key, value) in choices)
        {
            if (!LastChoiceKeys.All.Contains(key) || string.IsNullOrWhiteSpace(value) || value.Length > 100) continue;
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO ws.last_choice (user_sub, key, value, updated_at) VALUES (@u, @k, @v, @at)
                ON CONFLICT (user_sub, key) DO UPDATE SET value = @v, updated_at = @at
                """, conn);
            cmd.Parameters.AddWithValue("u", subject.Id);
            cmd.Parameters.AddWithValue("k", key);
            cmd.Parameters.AddWithValue("v", value);
            cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
