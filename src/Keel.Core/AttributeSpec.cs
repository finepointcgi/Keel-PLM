using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Keel.Core;

/// <summary>
/// An administrator-defined attribute (ADR-0002, ADR-0022), shared by part attributes (Keel.Parts) and classification attributes
/// (Keel.Admin) so both are validated by exactly the same rules. <see cref="Retired"/> attributes keep their stored values readable
/// but are no longer required, edited or validated.
/// </summary>
public sealed record AttributeSpec(
    string Name, string Label, string DataType, string? Unit, IReadOnlyList<string>? EnumValues, bool Required, int? MaxLength, int SortOrder,
    string? HelpText = null, string? Group = null, string? Pattern = null, decimal? Min = null, decimal? Max = null, bool Retired = false);

public static class AttributeSpecs
{
    public static readonly IReadOnlyList<string> DataTypes = ["string", "number", "boolean", "enum"];
    public static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>The whole value must match the administrator's pattern.</summary>
    public static Regex CompilePattern(string pattern) => new($"^(?:{pattern})$", RegexOptions.CultureInvariant, PatternTimeout);

    /// <summary>
    /// Validates input against the definitions: unknown names are rejected, empty values mean "not set", values are normalized
    /// (trimmed text, decimals) and returned with stable key order. Retired definitions keep a present value unchanged.
    /// </summary>
    public static (JsonObject Normalized, List<string> Errors) Validate(IReadOnlyList<AttributeSpec> definitions, JsonObject? input)
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
            if (def.Retired) { result[name] = node.DeepClone(); continue; } // retired: carried forward as stored, never edited
            switch (def.DataType)
            {
                case "string":
                    if (node.GetValueKind() != JsonValueKind.String) { errors.Add($"{def.Label} must be text."); break; }
                    var s = node.GetValue<string>().Trim();
                    if (def.MaxLength is { } max && s.Length > max) errors.Add($"{def.Label} must be at most {max} characters.");
                    else if (def.Pattern is { } pattern && !Matches(pattern, s)) errors.Add($"{def.Label} does not match the required format{(def.HelpText is { Length: > 0 } h ? $" ({h})" : "")}.");
                    else result[name] = s;
                    break;
                case "number":
                    // Parse the JSON text so values built in code (double, int) and parsed from requests behave identically.
                    decimal n;
                    if (node.GetValueKind() == JsonValueKind.Number && decimal.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) n = parsed;
                    else if (node.GetValueKind() == JsonValueKind.String && decimal.TryParse(node.GetValue<string>(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) n = d;
                    else { errors.Add($"{def.Label} must be a number."); break; }
                    if (def.Min is { } lo && n < lo) errors.Add($"{def.Label} must be at least {lo.ToString(CultureInfo.InvariantCulture)}.");
                    else if (def.Max is { } hi && n > hi) errors.Add($"{def.Label} must be at most {hi.ToString(CultureInfo.InvariantCulture)}.");
                    else result[name] = n;
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
        foreach (var def in definitions.Where(d => d.Required && !d.Retired && !result.ContainsKey(d.Name)))
            errors.Add($"{def.Label} is required.");

        // Stable key order so stored JSON and diffs are deterministic.
        var ordered = new JsonObject();
        foreach (var key in result.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList())
            ordered[key] = result[key]!.DeepClone();
        return (ordered, errors);
    }

    private static bool Matches(string pattern, string value)
    {
        try { return CompilePattern(pattern).IsMatch(value); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
