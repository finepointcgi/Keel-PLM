using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Parts;

namespace Keel.Web.Services;

/// <summary>Attribute values as typed in the form; converted to JSON for the server-side validator.</summary>
public sealed class AttributeInput
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public JsonObject ToJson(IReadOnlyList<AttributeDefinition> defs)
    {
        var json = new JsonObject();
        foreach (var d in defs)
        {
            if (!Values.TryGetValue(d.Name, out var v) || string.IsNullOrWhiteSpace(v)) continue;
            json[d.Name] = d.DataType == "boolean" ? v == "true" : v.Trim();
        }
        return json;
    }

    public static AttributeInput From(JsonObject? attributes)
    {
        var input = new AttributeInput();
        foreach (var (k, v) in attributes ?? [])
            if (v is not null) input.Values[k] = v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
        return input;
    }

    public static (string Label, string Value) Format(IReadOnlyList<AttributeDefinition> defs, string name, JsonNode? value)
    {
        var d = defs.FirstOrDefault(x => x.Name == name);
        var text = value?.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => "Yes",
            System.Text.Json.JsonValueKind.False => "No",
            System.Text.Json.JsonValueKind.String => value.GetValue<string>(),
            System.Text.Json.JsonValueKind.Number => decimal.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            _ => "",
        };
        return (d?.Label ?? name, d?.Unit is { } unit ? $"{text} {unit}" : text);
    }
}
