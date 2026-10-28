using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Keel.TestSupport;

/// <summary>
/// A small JSON Schema (2020-12 subset) validator for the event payload schemas under docs/integration/schemas. Written instead of pulling a package
/// because the build is air-gapped and every dependency is supply-chain surface; the subset covers what the schemas use: type, required, properties,
/// additionalProperties, items, enum, const, pattern, minLength, minimum, maximum, minItems, oneOf, anyOf and local $ref to $defs. It is itself tested
/// (JsonSchemaLiteTests) so a validator bug cannot silently accept bad payloads. Unknown keywords other than annotations make validation fail loudly.
/// </summary>
public static class JsonSchemaLite
{
    private static readonly HashSet<string> Known =
    [
        "$schema", "$id", "$defs", "$ref", "title", "description", "type", "required", "properties", "additionalProperties", "items", "enum", "const", "pattern",
        "minLength", "maxLength", "minimum", "maximum", "minItems", "maxItems", "oneOf", "anyOf", "format",
    ];

    public static IReadOnlyList<string> Validate(JsonNode? instance, JsonNode schema)
    {
        var errors = new List<string>();
        Check(instance, schema, schema, "$", errors);
        return errors;
    }

    private static void Check(JsonNode? value, JsonNode schemaNode, JsonNode root, string path, List<string> errors)
    {
        if (schemaNode is JsonValue bv && bv.TryGetValue<bool>(out var allowed))
        {
            if (!allowed) errors.Add($"{path}: not allowed");
            return;
        }
        var schema = schemaNode.AsObject();
        foreach (var key in schema.Select(p => p.Key))
            if (!Known.Contains(key)) errors.Add($"{path}: schema keyword '{key}' is not supported by the validator");

        if (schema["$ref"] is JsonValue r && r.GetValue<string>() is { } reference)
        {
            if (!reference.StartsWith("#/", StringComparison.Ordinal)) { errors.Add($"{path}: only local $ref is supported"); return; }
            JsonNode? target = root;
            foreach (var part in reference[2..].Split('/')) target = target?[part];
            if (target is null) { errors.Add($"{path}: unresolved $ref {reference}"); return; }
            Check(value, target, root, path, errors);
            return;
        }

        if (schema["type"] is { } typeNode)
        {
            var types = typeNode is JsonArray ta ? ta.Select(t => t!.GetValue<string>()).ToList() : [typeNode.GetValue<string>()];
            if (!types.Any(t => IsType(value, t))) { errors.Add($"{path}: expected {string.Join(" or ", types)} but found {Describe(value)}"); return; }
        }
        if (schema["const"] is { } c && !JsonNode.DeepEquals(c, value)) errors.Add($"{path}: must equal {c.ToJsonString()}");
        if (schema["enum"] is JsonArray e && !e.Any(x => JsonNode.DeepEquals(x, value))) errors.Add($"{path}: {value?.ToJsonString()} is not one of {e.ToJsonString()}");

        if (value is JsonValue v)
        {
            if (v.TryGetValue<string>(out var str))
            {
                if (schema["pattern"] is { } p && !Regex.IsMatch(str, p.GetValue<string>())) errors.Add($"{path}: '{str}' does not match {p.GetValue<string>()}");
                if (schema["minLength"] is { } ml && str.Length < ml.GetValue<int>()) errors.Add($"{path}: shorter than {ml}");
                if (schema["maxLength"] is { } xl && str.Length > xl.GetValue<int>()) errors.Add($"{path}: longer than {xl}");
            }
            else if (value.GetValueKind() == JsonValueKind.Number)
            {
                var number = v.GetValue<decimal>();
                if (schema["minimum"] is { } min && number < min.GetValue<decimal>()) errors.Add($"{path}: {number} is below {min}");
                if (schema["maximum"] is { } max && number > max.GetValue<decimal>()) errors.Add($"{path}: {number} is above {max}");
            }
        }

        if (value is JsonObject o)
        {
            if (schema["required"] is JsonArray req)
                foreach (var name in req.Select(x => x!.GetValue<string>()))
                    if (!o.ContainsKey(name)) errors.Add($"{path}: missing required property '{name}'");
            var props = schema["properties"]?.AsObject();
            foreach (var (name, child) in o)
            {
                if (props is not null && props.TryGetPropertyValue(name, out var propSchema)) Check(child, propSchema!, root, $"{path}.{name}", errors);
                else if (schema["additionalProperties"] is { } extra) Check(child, extra, root, $"{path}.{name}", errors);
            }
        }

        if (value is JsonArray a)
        {
            if (schema["minItems"] is { } mi && a.Count < mi.GetValue<int>()) errors.Add($"{path}: fewer than {mi} items");
            if (schema["maxItems"] is { } xi && a.Count > xi.GetValue<int>()) errors.Add($"{path}: more than {xi} items");
            if (schema["items"] is { } items)
                for (var i = 0; i < a.Count; i++) Check(a[i], items, root, $"{path}[{i}]", errors);
        }

        if (schema["oneOf"] is JsonArray one)
        {
            var matches = one.Count(s => Validate(value, s!, root).Count == 0);
            if (matches != 1) errors.Add($"{path}: must match exactly one alternative but matches {matches}");
        }
        if (schema["anyOf"] is JsonArray any && !any.Any(s => Validate(value, s!, root).Count == 0)) errors.Add($"{path}: matches none of the alternatives");
    }

    private static List<string> Validate(JsonNode? value, JsonNode schema, JsonNode root)
    {
        var errors = new List<string>();
        Check(value, schema, root, "$", errors);
        return errors;
    }

    private static bool IsType(JsonNode? value, string type) => type switch
    {
        "null" => value is null,
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue s && s.GetValueKind() == JsonValueKind.String,
        "boolean" => value is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => value is JsonValue n && n.GetValueKind() == JsonValueKind.Number,
        "integer" => value is JsonValue i && i.GetValueKind() == JsonValueKind.Number && decimal.Truncate(i.GetValue<decimal>()) == i.GetValue<decimal>(),
        _ => false,
    };

    private static string Describe(JsonNode? v) => v switch { null => "null", JsonObject => "object", JsonArray => "array", _ => v.GetValueKind().ToString().ToLowerInvariant() };
}
