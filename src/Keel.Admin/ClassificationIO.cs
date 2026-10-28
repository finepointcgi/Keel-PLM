using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Keel.Core;

namespace Keel.Admin;

/// <summary>An attribute in an export or import document (hierarchy or configuration). The name identifies it.</summary>
public sealed record AttributeDoc(
    string Name, string Label, string DataType, string? Unit = null, IReadOnlyList<string>? EnumValues = null, bool Required = false, int? MaxLength = null, int SortOrder = 0,
    string? HelpText = null, string? Group = null, string? Pattern = null, decimal? Min = null, decimal? Max = null, bool Retired = false)
{
    public AttributeDefinitionInput ToInput() => new(Name, Label, DataType, Unit, EnumValues, Required, MaxLength, SortOrder, HelpText, Group, Pattern, Min, Max);

    public static AttributeDoc From(AttributeDefinitionDto d) =>
        new(d.Name, d.Label, d.DataType, d.Unit, d.EnumValues, d.Required, d.MaxLength, d.SortOrder, d.HelpText, d.Group, d.Pattern, d.Min, d.Max, d.Retired);
}

public sealed record ClassNodeDoc(string Code, string Name, string? Description, string? Parent, int SortOrder, string Status, IReadOnlyList<AttributeDoc> Attributes);

/// <summary>The classification hierarchy as exchanged with other systems (format <c>keel-classification/1</c>, see docs/migration/classification-format.md).</summary>
public sealed record ClassHierarchyDoc(string Format, IReadOnlyList<ClassNodeDoc> Nodes)
{
    public const string CurrentFormat = "keel-classification/1";
}

public sealed record ImportLine(string Code, string Action, string Detail);

/// <summary>What an import did, or with a dry run would do. <see cref="Errors"/> is empty when the document can be applied.</summary>
public sealed record ClassImportReport(bool DryRun, string Sha256, IReadOnlyList<ImportLine> Lines, IReadOnlyList<string> Errors, int Created, int Updated, int Unchanged);

public static class ClassificationIO
{
    private static readonly JsonSerializerOptions Options = new(AdminRunner.Json) { WriteIndented = true };

    public static string ToJson(ClassHierarchyDoc doc) => JsonSerializer.Serialize(doc, Options);

    public static (ClassHierarchyDoc? Doc, string? Error) FromJson(string json)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<ClassHierarchyDoc>(json, AdminRunner.Json);
            if (doc is null || doc.Nodes is null) return (null, "The document has no nodes.");
            return doc.Format == ClassHierarchyDoc.CurrentFormat ? (doc, null) : (null, $"Unsupported format '{doc.Format}' (expected {ClassHierarchyDoc.CurrentFormat}).");
        }
        catch (JsonException e)
        {
            return (null, "The JSON is invalid: " + e.Message);
        }
    }

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static readonly string[] CsvHeader =
    [
        "record", "code", "parent", "name", "description", "sort_order", "status", "attr_name", "attr_label", "data_type", "unit", "enum_values", "required",
        "max_length", "pattern", "min", "max", "help_text", "group", "attr_sort", "retired",
    ];

    /// <summary>One CSV for the whole hierarchy: a <c>class</c> row per node, then an <c>attribute</c> row per attribute (enum values separated by '|').</summary>
    public static string ToCsv(ClassHierarchyDoc doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', CsvHeader));
        foreach (var n in doc.Nodes)
        {
            sb.AppendLine(string.Join(',', new[] { "class", n.Code, n.Parent ?? "", n.Name, n.Description ?? "", n.SortOrder.ToString(CultureInfo.InvariantCulture), n.Status, "", "", "", "", "", "", "", "", "", "", "", "", "", "" }.Select(Csv)));
            foreach (var a in n.Attributes)
                sb.AppendLine(string.Join(',', new[]
                {
                    "attribute", n.Code, "", "", "", "", "", a.Name, a.Label, a.DataType, a.Unit ?? "", a.EnumValues is null ? "" : string.Join('|', a.EnumValues), a.Required ? "true" : "false",
                    a.MaxLength?.ToString(CultureInfo.InvariantCulture) ?? "", a.Pattern ?? "", a.Min?.ToString(CultureInfo.InvariantCulture) ?? "", a.Max?.ToString(CultureInfo.InvariantCulture) ?? "",
                    a.HelpText ?? "", a.Group ?? "", a.SortOrder.ToString(CultureInfo.InvariantCulture), a.Retired ? "true" : "false",
                }.Select(Csv)));
        }
        return sb.ToString();
    }

    public static (ClassHierarchyDoc? Doc, string? Error) FromCsv(string csv)
    {
        var rows = ParseCsv(csv);
        if (rows.Count == 0) return (null, "The CSV is empty.");
        var header = rows[0].Select(h => h.Trim()).ToList();
        var missing = CsvHeader.Where(h => !header.Contains(h)).ToList();
        if (missing.Count > 0) return (null, $"Missing column(s): {string.Join(", ", missing)}.");
        string Col(List<string> row, string name) => header.IndexOf(name) is var i && i >= 0 && i < row.Count ? row[i].Trim() : "";

        var nodes = new List<ClassNodeDoc>();
        var attributes = new Dictionary<string, List<AttributeDoc>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<(string Code, string Name, string? Description, string? Parent, int Sort, string Status)>();
        for (var line = 1; line < rows.Count; line++)
        {
            var row = rows[line];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            var record = Col(row, "record");
            var code = Col(row, "code");
            if (code.Length == 0) return (null, $"Line {line + 1}: the code is empty.");
            if (!int.TryParse(Col(row, record == "class" ? "sort_order" : "attr_sort") is { Length: > 0 } s ? s : "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out var sort))
                return (null, $"Line {line + 1}: the sort order is not a number.");
            switch (record)
            {
                case "class":
                    order.Add((code, Col(row, "name"), Col(row, "description"), Col(row, "parent") is { Length: > 0 } p ? p : null, sort, Col(row, "status") is { Length: > 0 } st ? st : "active"));
                    break;
                case "attribute":
                    if (!TryDecimal(Col(row, "min"), out var min) || !TryDecimal(Col(row, "max"), out var max)) return (null, $"Line {line + 1}: min or max is not a number.");
                    int? maxLength = int.TryParse(Col(row, "max_length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ml) ? ml : null;
                    var enums = Col(row, "enum_values");
                    if (!attributes.TryGetValue(code, out var list)) attributes[code] = list = [];
                    list.Add(new AttributeDoc(Col(row, "attr_name"), Col(row, "attr_label"), Col(row, "data_type"), NullIfEmpty(Col(row, "unit")), enums.Length == 0 ? null : enums.Split('|'),
                        Col(row, "required").Equals("true", StringComparison.OrdinalIgnoreCase), maxLength, sort, NullIfEmpty(Col(row, "help_text")), NullIfEmpty(Col(row, "group")),
                        NullIfEmpty(Col(row, "pattern")), min, max, Col(row, "retired").Equals("true", StringComparison.OrdinalIgnoreCase)));
                    break;
                default:
                    return (null, $"Line {line + 1}: the record type must be 'class' or 'attribute'.");
            }
        }
        foreach (var o in order) nodes.Add(new ClassNodeDoc(o.Code, o.Name, o.Description, o.Parent, o.Sort, o.Status, attributes.GetValueOrDefault(o.Code) ?? []));
        var orphan = attributes.Keys.FirstOrDefault(k => order.All(o => !o.Code.Equals(k, StringComparison.OrdinalIgnoreCase)));
        return orphan is not null ? (null, $"Attribute rows refer to class '{orphan}', which has no class row.") : (new ClassHierarchyDoc(ClassHierarchyDoc.CurrentFormat, nodes), null);
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static bool TryDecimal(string s, out decimal? value)
    {
        value = null;
        if (s.Length == 0) return true;
        if (!decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return false;
        value = d;
        return true;
    }

    private static string Csv(string s) => s.Contains(',', StringComparison.Ordinal) || s.Contains('"', StringComparison.Ordinal) || s.Contains('\n', StringComparison.Ordinal) || s.Contains('\r', StringComparison.Ordinal)
        ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;

    /// <summary>RFC 4180 parsing: quoted fields may contain commas, quotes ("") and line breaks.</summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}
