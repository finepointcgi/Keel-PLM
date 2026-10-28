using System.Text.Json.Nodes;
using Keel.Core;
using Npgsql;

namespace Keel.Parts;

public sealed record AttributeDefinition(
    string Name, string Label, string DataType, string? Unit, IReadOnlyList<string>? EnumValues, bool Required, int? MaxLength, int SortOrder,
    string? HelpText = null, string? Group = null, string? Pattern = null, decimal? Min = null, decimal? Max = null, bool Retired = false)
{
    public AttributeSpec ToSpec() => new(Name, Label, DataType, Unit, EnumValues, Required, MaxLength, SortOrder, HelpText, Group, Pattern, Min, Max, Retired);
}

/// <summary>Validates admin-defined attribute values (ADR-0002). Unknown attributes are rejected, not silently stored. The rules live in Keel.Core so classification attributes use the same ones.</summary>
public static class AttributeValidator
{
    public static (JsonObject Normalized, List<string> Errors) Validate(IReadOnlyList<AttributeDefinition> definitions, JsonObject? input) =>
        AttributeSpecs.Validate([.. definitions.Select(d => d.ToSpec())], input);

    public static async Task<IReadOnlyList<AttributeDefinition>> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string objectType, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT name, label, data_type, unit, enum_values, required, max_length, sort_order, help_text, grp, pattern, min_value, max_value, retired FROM plm.attribute_definition WHERE object_type = @t ORDER BY sort_order, name",
            conn, tx);
        cmd.Parameters.AddWithValue("t", objectType);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AttributeDefinition>();
        while (await r.ReadAsync(ct))
            list.Add(new AttributeDefinition(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : (string[])r[4], r.GetBoolean(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.GetInt32(7),
                r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetDecimal(11), r.IsDBNull(12) ? null : r.GetDecimal(12), r.GetBoolean(13)));
        return list;
    }
}
