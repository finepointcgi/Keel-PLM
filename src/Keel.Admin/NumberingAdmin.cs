using System.Globalization;
using System.Text.RegularExpressions;
using Keel.Core;
using Npgsql;

namespace Keel.Admin;

public sealed record NumberingInfo(string ObjectType, string Prefix, int Width, bool PrefixEditable, long NextValue, string NextNumber);

/// <summary>
/// Number prefixes and widths per object type (A6). The counter is a database sequence that only moves forward, so a number is never issued
/// twice whatever the prefix; the preview shows what the next object would get (another user may take it first).
/// </summary>
public sealed partial class NumberingAdminService(AdminRunner run)
{
    public Task<Outcome<IReadOnlyList<NumberingInfo>>> ListAsync(Subject subject, CancellationToken ct = default) =>
        run.ReadAsync<IReadOnlyList<NumberingInfo>>(subject, "admin.numbering_list", null, conn => LoadAsync(conn, null, ct), ct);

    private static async Task<IReadOnlyList<NumberingInfo>> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var rows = new List<(string Type, string Prefix, int Width, bool Editable, string Seq)>();
        await using (var cmd = AdminRunner.Cmd(conn, tx, "SELECT object_type, prefix, width, prefix_editable, sequence_name FROM cfg.numbering ORDER BY prefix_editable DESC, object_type"))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add((r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetBoolean(3), r.GetString(4)));
        }
        var list = new List<NumberingInfo>();
        foreach (var row in rows)
        {
            var next = await NextValueAsync(conn, tx, row.Seq, ct);
            list.Add(new NumberingInfo(row.Type, row.Prefix, row.Width, row.Editable, next, Format(row.Prefix, row.Width, next)));
        }
        return list;
    }

    /// <summary>The value the next <c>nextval</c> returns, read without consuming it.</summary>
    private static async Task<long> NextValueAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sequence, CancellationToken ct)
    {
        await using var cmd = AdminRunner.Cmd(conn, tx, "SELECT pg_sequence_last_value(@s::regclass)", ("s", sequence));
        return await cmd.ExecuteScalarAsync(ct) is long last ? last + 1 : 1;
    }

    public static string Format(string prefix, int width, long value) => prefix + value.ToString("D" + width, CultureInfo.InvariantCulture);

    /// <summary>Changes the prefix and width of an object type. Only part and document prefixes are editable; the change prefixes are fixed by database format checks.</summary>
    public Task<Outcome<NumberingInfo>> UpdateAsync(Subject subject, string objectType, string prefix, int width, string? reason, string? correlationId, CancellationToken ct = default) =>
        run.ChangeAsync<NumberingInfo>(subject, "admin.numbering_update", objectType, correlationId, async (conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) return Step<NumberingInfo>.Invalid("A reason is required.");
            var current = (await LoadAsync(conn, tx, ct)).FirstOrDefault(n => n.ObjectType == objectType);
            if (current is null) return Step<NumberingInfo>.NotFound();
            prefix = prefix?.Trim() ?? "";
            var errors = new List<string>();
            if (!PrefixPattern().IsMatch(prefix)) errors.Add("The prefix must start with a capital letter or digit and use capitals, digits and . _ - (at most 16 characters).");
            if (width is < 4 or > 12) errors.Add("The width must be between 4 and 12 digits.");
            if (prefix != current.Prefix && !current.PrefixEditable) errors.Add($"The {objectType} prefix is fixed: the database format check of the change tables requires '{current.Prefix}' (ADR-0019).");
            if (current.NextValue.ToString(CultureInfo.InvariantCulture).Length > width) errors.Add($"The next value {current.NextValue} needs more than {width} digits.");
            if (errors.Count > 0) return Step<NumberingInfo>.Invalid(errors);
            var clash = await ExistsAsync(conn, tx, objectType, Format(prefix, width, current.NextValue), ct);
            if (clash) return Step<NumberingInfo>.Conflict($"{Format(prefix, width, current.NextValue)} already exists (an explicit number was used); advance the counter first.");
            await using (var cmd = AdminRunner.Cmd(conn, tx, "UPDATE cfg.numbering SET prefix = @p, width = @w, updated_at = @at, updated_by = @by WHERE object_type = @t",
                ("p", prefix), ("w", width), ("at", run.Clock.GetUtcNow()), ("by", AdminRunner.Who(subject)), ("t", objectType))) await cmd.ExecuteNonQueryAsync(ct);
            var after = current with { Prefix = prefix, Width = width, NextNumber = Format(prefix, width, current.NextValue) };
            return Step<NumberingInfo>.Ok(after, ("before", AdminRunner.ToJson(current)), ("after", AdminRunner.ToJson(after)), ("reason", reason.Trim()));
        }, ct);

    /// <summary>Moves the counter forward (never back), for example past explicit numbers imported from another system.</summary>
    public Task<Outcome<NumberingInfo>> AdvanceAsync(Subject subject, string objectType, long nextValue, string? reason, string? correlationId, CancellationToken ct = default) =>
        run.ChangeAsync<NumberingInfo>(subject, "admin.numbering_advance", objectType, correlationId, async (conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) return Step<NumberingInfo>.Invalid("A reason is required.");
            var current = (await LoadAsync(conn, tx, ct)).FirstOrDefault(n => n.ObjectType == objectType);
            if (current is null) return Step<NumberingInfo>.NotFound();
            if (nextValue <= current.NextValue) return Step<NumberingInfo>.Invalid($"The counter only moves forward: the next number is already {current.NextValue}.");
            if (nextValue.ToString(CultureInfo.InvariantCulture).Length > current.Width) return Step<NumberingInfo>.Invalid($"{nextValue} needs more than {current.Width} digits; widen the numbers first.");
            string sequence;
            await using (var s = AdminRunner.Cmd(conn, tx, "SELECT sequence_name FROM cfg.numbering WHERE object_type = @t", ("t", objectType))) sequence = (string)(await s.ExecuteScalarAsync(ct))!;
            if (await ExistsAsync(conn, tx, objectType, Format(current.Prefix, current.Width, nextValue), ct)) return Step<NumberingInfo>.Conflict($"{Format(current.Prefix, current.Width, nextValue)} already exists.");
            await using (var cmd = AdminRunner.Cmd(conn, tx, "SELECT setval(@s::regclass, @v, true)", ("s", sequence), ("v", nextValue - 1))) await cmd.ExecuteScalarAsync(ct);
            var after = current with { NextValue = nextValue, NextNumber = Format(current.Prefix, current.Width, nextValue) };
            return Step<NumberingInfo>.Ok(after, ("before", AdminRunner.ToJson(current)), ("after", AdminRunner.ToJson(after)), ("reason", reason.Trim()));
        }, ct);

    private static async Task<bool> ExistsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string objectType, string number, CancellationToken ct)
    {
        // Key lookups by number: whether a number is taken is not marked data (the numbers of hidden objects are a configuration-safety question here).
        var sql = objectType switch
        {
            "part" => "SELECT 1 FROM plm.part_master WHERE number = @n",
            "document" => "SELECT 1 FROM plm.document_master WHERE number = @n",
            _ => "SELECT 1 FROM chg.change WHERE number = @n UNION ALL SELECT 1 FROM chg.change_task WHERE number = @n",
        };
        await using var cmd = AdminRunner.Cmd(conn, tx, sql, ("n", number));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{0,15}$")]
    private static partial Regex PrefixPattern();
}
