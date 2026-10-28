using System.Text.RegularExpressions;
using Keel.Core;
using Npgsql;

namespace Keel.Admin;

/// <summary>An attribute definition as the admin UI and API show it. <see cref="ValuesInUse"/> counts stored values (all iterations and working copies).</summary>
public sealed record AttributeDefinitionDto(
    string Owner, string Name, string Label, string DataType, string? Unit, IReadOnlyList<string>? EnumValues, bool Required, int? MaxLength, int SortOrder,
    string? HelpText, string? Group, string? Pattern, decimal? Min, decimal? Max, bool Retired, int Revision, long ValuesInUse)
{
    public AttributeSpec ToSpec() => new(Name, Label, DataType, Unit, EnumValues, Required, MaxLength, SortOrder, HelpText, Group, Pattern, Min, Max, Retired);
}

/// <summary>The editable part of a definition. The name identifies it and never changes.</summary>
public sealed record AttributeDefinitionInput(
    string Name, string Label, string DataType, string? Unit = null, IReadOnlyList<string>? EnumValues = null, bool Required = false, int? MaxLength = null,
    int SortOrder = 0, string? HelpText = null, string? Group = null, string? Pattern = null, decimal? Min = null, decimal? Max = null);

/// <summary>A definition change: the result plus non-blocking warnings the UI shows before the administrator confirms.</summary>
public sealed record AttributeChange(AttributeDefinitionDto Definition, IReadOnlyList<string> Warnings);

/// <summary>Pure rules for attribute definitions, shared by part attributes and classification attributes.</summary>
public static partial class AttributeRules
{
    public const int MaxEnumValues = 100;

    /// <summary>Checks the input on its own; when <paramref name="existing"/> is given also checks the change against stored values.</summary>
    public static List<string> Validate(AttributeDefinitionInput i, AttributeDefinitionDto? existing, IReadOnlySet<string>? enumValuesInUse = null)
    {
        var errors = new List<string>();
        if (existing is null && !NamePattern().IsMatch(i.Name ?? "")) errors.Add("The name must start with a lower-case letter and use only a-z, 0-9 and '_' (at most 63 characters).");
        if (string.IsNullOrWhiteSpace(i.Label) || i.Label.Trim().Length > 120) errors.Add("The label is required (at most 120 characters).");
        if (!AttributeSpecs.DataTypes.Contains(i.DataType)) errors.Add($"The data type must be one of: {string.Join(", ", AttributeSpecs.DataTypes)}.");
        if ((i.HelpText?.Length ?? 0) > 500) errors.Add("The help text must be at most 500 characters.");
        if ((i.Group?.Length ?? 0) > 60) errors.Add("The group must be at most 60 characters.");
        if (i.SortOrder is < -10000 or > 10000) errors.Add("The sort order must be between -10000 and 10000.");
        if ((i.Unit?.Length ?? 0) > 20) errors.Add("The unit must be at most 20 characters.");
        if (i.Unit is { Length: > 0 } && i.DataType != "number") errors.Add("Only number attributes have a unit.");

        var values = i.EnumValues?.Select(v => v?.Trim() ?? "").ToList() ?? [];
        if (i.DataType == "enum")
        {
            if (values.Count == 0) errors.Add("An enum attribute needs at least one value.");
            if (values.Count > MaxEnumValues) errors.Add($"An enum attribute has at most {MaxEnumValues} values.");
            if (values.Any(v => v.Length is 0 or > 100)) errors.Add("Enum values must not be empty and are at most 100 characters.");
            if (values.Distinct(StringComparer.Ordinal).Count() != values.Count) errors.Add("Enum values must be unique.");
        }
        else if (values.Count > 0) errors.Add("Only enum attributes have values.");

        if (i.MaxLength is { } ml && (i.DataType != "string" || ml is < 1 or > 4000)) errors.Add("The maximum length applies to text attributes and must be between 1 and 4000.");
        if (i.Pattern is { Length: > 0 } pattern)
        {
            if (i.DataType != "string") errors.Add("A format (regular expression) applies to text attributes only.");
            else if (pattern.Length > 500) errors.Add("The format must be at most 500 characters.");
            else
            {
                try { AttributeSpecs.CompilePattern(pattern).IsMatch("probe"); }
                catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException) { errors.Add("The format is not a valid regular expression."); }
            }
        }
        if ((i.Min is not null || i.Max is not null) && i.DataType != "number") errors.Add("A range applies to number attributes only.");
        if (i.Min is { } lo && i.Max is { } hi && lo > hi) errors.Add("The minimum must not exceed the maximum.");

        if (existing is not null)
        {
            if (i.DataType != existing.DataType && existing.ValuesInUse > 0)
                errors.Add($"The data type cannot change: {existing.ValuesInUse} stored value(s) use '{existing.DataType}'. Create a new attribute with the new type, " +
                           "copy the values with a check-out and check-in of the affected objects (or a migration import), then retire this one.");
            if (existing.DataType == "enum" && i.DataType == "enum" && enumValuesInUse is not null)
            {
                var removedInUse = existing.EnumValues!.Where(v => !values.Contains(v, StringComparer.Ordinal) && enumValuesInUse.Contains(v)).ToList();
                if (removedInUse.Count > 0) errors.Add($"Enum value(s) {string.Join(", ", removedInUse)} are stored on existing objects and cannot be removed; retire the attribute or keep the value.");
            }
        }
        return errors;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex NamePattern();
}

/// <summary>
/// Storage of attribute definitions for two owners with the same shape: an object type (<c>plm.attribute_definition</c>) and a classification
/// node (<c>cfg.class_attribute</c>). The SQL texts are constants; only values travel as parameters.
/// </summary>
internal sealed class AttributeTable(string table, string keyColumn)
{
    public static readonly AttributeTable ForObjectTypes = new("plm.attribute_definition", "object_type");
    public static readonly AttributeTable ForClasses = new("cfg.class_attribute", "node_id");

    private const string Columns = "name, label, data_type, unit, enum_values, required, max_length, sort_order, help_text, grp, pattern, min_value, max_value, retired, revision";

    private static object Key(object owner) => owner;

    public async Task<List<AttributeDefinitionDto>> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, object owner, bool includeRetired, CancellationToken ct)
    {
        var list = new List<AttributeDefinitionDto>();
        await using var cmd = AdminRunner.Cmd(conn, tx, $"SELECT {Columns} FROM {table} WHERE {keyColumn} = @k {(includeRetired ? "" : "AND NOT retired")} ORDER BY sort_order, name", ("k", Key(owner)));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AttributeDefinitionDto(owner.ToString()!, r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : (string[])r[4], r.GetBoolean(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.GetInt32(7),
                r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetDecimal(11), r.IsDBNull(12) ? null : r.GetDecimal(12), r.GetBoolean(13), r.GetInt32(14), 0));
        return list;
    }

    public async Task InsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, object owner, AttributeDefinitionInput i, string by, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = AdminRunner.Cmd(conn, tx,
            $"INSERT INTO {table} ({keyColumn}, name, label, data_type, unit, enum_values, required, max_length, sort_order, help_text, grp, pattern, min_value, max_value, updated_at, updated_by) " +
            "VALUES (@k, @name, @label, @type, @unit, @enums, @req, @max, @sort, @help, @grp, @pat, @min, @maxv, @at, @by)", Params(owner, i, by, at));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, object owner, AttributeDefinitionInput i, string by, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = AdminRunner.Cmd(conn, tx,
            $"UPDATE {table} SET label = @label, data_type = @type, unit = @unit, enum_values = @enums, required = @req, max_length = @max, sort_order = @sort, " +
            "help_text = @help, grp = @grp, pattern = @pat, min_value = @min, max_value = @maxv, revision = revision + 1, updated_at = @at, updated_by = @by " +
            $"WHERE {keyColumn} = @k AND name = @name", Params(owner, i, by, at));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetRetiredAsync(NpgsqlConnection conn, NpgsqlTransaction tx, object owner, string name, bool retired, string by, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = AdminRunner.Cmd(conn, tx,
            $"UPDATE {table} SET retired = @r, revision = revision + 1, updated_at = @at, updated_by = @by WHERE {keyColumn} = @k AND name = @name",
            ("k", Key(owner)), ("name", name), ("r", retired), ("at", at), ("by", by));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static (string, object?)[] Params(object owner, AttributeDefinitionInput i, string by, DateTimeOffset at) =>
    [
        ("k", Key(owner)), ("name", i.Name), ("label", i.Label.Trim()), ("type", i.DataType), ("unit", Blank(i.Unit)),
        ("enums", i.DataType == "enum" ? i.EnumValues!.Select(v => v.Trim()).ToArray() : null), ("req", i.Required),
        ("max", i.DataType == "string" ? i.MaxLength : null), ("sort", i.SortOrder), ("help", Blank(i.HelpText)), ("grp", Blank(i.Group)),
        ("pat", i.DataType == "string" ? Blank(i.Pattern) : null), ("min", i.DataType == "number" ? i.Min : null), ("maxv", i.DataType == "number" ? i.Max : null),
        ("at", at), ("by", by),
    ];

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static AttributeDefinitionInput ToInput(AttributeDefinitionDto d) =>
        new(d.Name, d.Label, d.DataType, d.Unit, d.EnumValues, d.Required, d.MaxLength, d.SortOrder, d.HelpText, d.Group, d.Pattern, d.Min, d.Max);
}
