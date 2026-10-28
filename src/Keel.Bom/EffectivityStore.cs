using System.Data.Common;
using Keel.Core;
using Npgsql;

namespace Keel.Bom;

/// <summary>SQL helpers for effectivity contexts and stored ranges, shared by the BOM editor, the context service and the change integration.</summary>
internal static class EffectivityStore
{
    public sealed record ContextRow(string Id, string Name, string Description, string? Program, IReadOnlyDictionary<string, int> Blocks);

    /// <summary>The context, or null when it does not exist or the subject may not see it (restricted to a program the subject does not hold).</summary>
    public static async Task<ContextRow?> LoadContextAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string id, Subject? subject, CancellationToken ct)
    {
        string name, description;
        string? program;
        await using (var cmd = new NpgsqlCommand("SELECT name, description, program FROM cfg.effectivity_context WHERE id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            (name, description, program) = (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
        }
        if (subject is not null && !Visible(subject, program)) return null;
        var blocks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new NpgsqlCommand("SELECT name, number FROM cfg.effectivity_block WHERE context_id = @id ORDER BY number", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) blocks[r.GetString(0)] = r.GetInt32(1);
        }
        return new ContextRow(id, name, description, program, blocks);
    }

    /// <summary>A context restricted to a program is visible only to subjects holding that program (the program name is itself a marking).</summary>
    public static bool Visible(Subject subject, string? program) => program is null || subject.Programs.Contains(program);

    /// <summary>Parses a typed effectivity against its context (existence, visibility, block names).</summary>
    public static async Task<(EffectivityRange? Range, string? Error)> ParseAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Subject? subject, EffectivityInput input, CancellationToken ct)
    {
        IReadOnlyDictionary<string, int>? blocks = null;
        var context = string.IsNullOrWhiteSpace(input.Context) ? null : input.Context.Trim().ToUpperInvariant();
        if (context is not null && !string.Equals(input.Kind?.Trim(), EffectivityKinds.Date, StringComparison.OrdinalIgnoreCase))
        {
            var row = await LoadContextAsync(conn, tx, context, subject, ct);
            if (row is null) return (null, $"The end-item context '{context}' was not found.");
            blocks = row.Blocks;
        }
        return EffectivityRules.Parse(input, blocks);
    }

    public static object Value(object? value) => value ?? DBNull.Value;

    public static EffectivityRange? ReadRange(DbDataReader r, int start) =>
        r.IsDBNull(start) ? null : new EffectivityRange(r.GetString(start), r.IsDBNull(start + 1) ? null : r.GetString(start + 1),
            r.IsDBNull(start + 2) ? null : r.GetInt64(start + 2), r.IsDBNull(start + 3) ? null : r.GetInt64(start + 3));

    public static void Bind(NpgsqlCommand cmd, EffectivityRange? range)
    {
        cmd.Parameters.AddWithValue("ek", NpgsqlTypes.NpgsqlDbType.Text, Value(range?.Kind));
        cmd.Parameters.AddWithValue("ec", NpgsqlTypes.NpgsqlDbType.Text, Value(range?.Context));
        cmd.Parameters.AddWithValue("ef", NpgsqlTypes.NpgsqlDbType.Bigint, Value(range?.From));
        cmd.Parameters.AddWithValue("et", NpgsqlTypes.NpgsqlDbType.Bigint, Value(range?.To));
    }
}
