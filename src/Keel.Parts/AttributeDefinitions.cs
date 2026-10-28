using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Keel.Parts;

public sealed record AttributeDefinition(
    string Name, string Label, string DataType, string? Unit, IReadOnlyList<string>? EnumValues, bool Required, int? MaxLength, int SortOrder);

/// <summary>Validates admin-defined attribute values (ADR-0002). Unknown attributes are rejected, not silently stored.</summary>
public static class AttributeValidator
{
    public static (JsonObject Normalized, List<string> Errors) Validate(IReadOnlyList<AttributeDefinition> definitions, JsonObject? input)
    {
        var errors = new List<string>();
        var result = new JsonObject();
        var defs = definitions.ToDictionary(d => d.Name, StringComparer.Ordinal);
        input ??= new JsonObject();

        foreach (var (name, node) in input)
        {
            if (!defs.TryGetValue(name, out var def)) { errors.Add($"Unknown attribute '{name}'."); continue; }
            if (node is null || (node is JsonValue v0 && v0.GetValueKind() == JsonValueKind.String && string.IsNullOrWhiteSpace(v0.GetValue<string>())))
                continue; // empty means "not set"
            switch (def.DataType)
            {
                case "string":
                    if (node.GetValueKind() != JsonValueKind.String) { errors.Add($"{def.Label} must be text."); break; }
                    var s = node.GetValue<string>().Trim();
                    if (def.MaxLength is { } max && s.Length > max) errors.Add($"{def.Label} must be at most {max} characters.");
                    else result[name] = s;
                    break;
                case "number":
                    // Parse the JSON text so values built in code (double, int) and parsed from requests behave identically.
                    if (node.GetValueKind() == JsonValueKind.Number && decimal.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                        result[name] = n;
                    else if (node.GetValueKind() == JsonValueKind.String && decimal.TryParse(node.GetValue<string>(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) result[name] = d;
                    else errors.Add($"{def.Label} must be a number.");
                    break;
                case "boolean":
                    if (node.GetValueKind() is JsonValueKind.True or JsonValueKind.False) result[name] = node.GetValue<bool>();
                    else errors.Add($"{def.Label} must be true or false.");
                    break;
                case "enum":
                    if (node.GetValueKind() == JsonValueKind.String && def.EnumValues!.Contains(node.GetValue<string>(), StringComparer.Ordinal))
                        result[name] = node.GetValue<string>();
                    else errors.Add($"{def.Label} must be one of: {string.Join(", ", def.EnumValues!)}.");
                    break;
            }
        }
        foreach (var def in definitions.Where(d => d.Required && !result.ContainsKey(d.Name)))
            errors.Add($"{def.Label} is required.");

        // Stable key order so stored JSON and diffs are deterministic.
        var ordered = new JsonObject();
        foreach (var key in result.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList())
            ordered[key] = result[key]!.DeepClone();
        return (ordered, errors);
    }

    public static async Task<IReadOnlyList<AttributeDefinition>> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string objectType, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT name, label, data_type, unit, enum_values, required, max_length, sort_order FROM plm.attribute_definition WHERE object_type = @t ORDER BY sort_order, name",
            conn, tx);
        cmd.Parameters.AddWithValue("t", objectType);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AttributeDefinition>();
        while (await r.ReadAsync(ct))
            list.Add(new AttributeDefinition(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : (string[])r[4], r.GetBoolean(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.GetInt32(7)));
        return list;
    }
}
