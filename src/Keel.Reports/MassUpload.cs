using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Core.Reports;
using Keel.Reports.Tabular;

namespace Keel.Reports;

/// <summary>What an uploaded table asks for: the objects, and the parameters each row gives its own object. Nothing is looked up or changed here.</summary>
public sealed record MassUploadParse(string? Kind, string? Operation, IReadOnlyList<string> Keys, IReadOnlyDictionary<string, JsonObject> ItemParameters, int DataRows, IReadOnlyList<string> Errors);

/// <summary>
/// The CSV/Excel path into a mass change (migration 0029, ADR-0050). The first row names the columns; one column names the object (part, document, assembly, requirement, key, object or
/// number), optional columns state the kind and the operation, and every other column is a parameter of the operation for that row. A file cannot carry markings, a change order or
/// anything else that widens authority: the file only says which objects and which values; the preview still decides per object. Unknown columns are refused, never ignored.
/// </summary>
public static class MassUpload
{
    private static readonly string[] KeyColumns = ["key", "object", "number", "part", "document", "assembly", "parent", "requirement"];

    /// <summary>The parameter columns a file may carry, with the type of each.</summary>
    private static readonly Dictionary<string, string> ParameterColumns = new(StringComparer.Ordinal)
    {
        ["name"] = "text", ["value"] = "text", ["field"] = "text", ["description"] = "text", ["component"] = "text", ["replacement"] = "text",
        ["quantity"] = "number", ["unit"] = "text", ["find_number"] = "integer", ["designators"] = "list", ["notes"] = "text",
    };

    public static IReadOnlyList<string> Columns(string operation) => operation switch
    {
        MassOperations.SetAttribute => ["name", "value"],
        MassOperations.SetDescription => ["description"],
        MassOperations.AddComponent => ["component", "quantity", "unit", "find_number", "designators", "notes"],
        MassOperations.ReplaceComponent => ["component", "replacement", "quantity", "unit", "notes"],
        MassOperations.RemoveComponent => ["component"],
        MassOperations.SetRequirementField => ["field", "value"],
        _ => [],
    };

    public static string KeyColumn(string kind) => kind switch { MassKinds.Document => "document", MassKinds.Assembly => "assembly", MassKinds.Requirement => "requirement", _ => "part" };

    /// <summary>An empty template for an operation: the header row only (an example row could be uploaded by accident).</summary>
    public static byte[] Template(string kind, string operation) => Csv.Write([KeyColumn(kind), .. Columns(operation)], []);

    private static string Normalize(string h) => h.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    public static MassUploadParse Parse(IReadOnlyList<IReadOnlyList<string>> table)
    {
        var errors = new List<string>();
        var numbered = table.Select((r, i) => (Line: i + 1, Row: r)).Where(x => x.Row.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        var rows = numbered.Select(x => x.Row).ToList();
        if (rows.Count == 0) return new(null, null, [], new Dictionary<string, JsonObject>(), 0, ["The file has no rows."]);
        var header = rows[0].Select(Normalize).ToList();
        var keyIdx = header.FindIndex(h => KeyColumns.Contains(h));
        if (keyIdx < 0) errors.Add("The first row must name the objects in a column called part, document, assembly or requirement.");
        if (header.Count(h => KeyColumns.Contains(h)) > 1) errors.Add("Only one column may name the objects.");
        var kindIdx = header.IndexOf("kind");
        var opIdx = header.IndexOf("operation");
        var columns = new List<(int Index, string Name)>();
        for (var i = 0; i < header.Count; i++)
        {
            var h = header[i];
            if (h.Length == 0 || KeyColumns.Contains(h) || h is "kind" or "operation") continue;
            if (!ParameterColumns.ContainsKey(h)) { errors.Add($"Column '{rows[0][i].Trim()}' is not one of the columns a mass change file may have."); continue; }
            if (columns.Any(c => c.Name == h)) errors.Add($"Column '{h}' appears twice.");
            columns.Add((i, h));
        }
        if (errors.Count > 0) return new(null, null, [], new Dictionary<string, JsonObject>(), 0, errors);

        string? Cell(IReadOnlyList<string> r, int i) => i >= 0 && i < r.Count ? r[i].Trim() : null;
        var keys = new List<string>();
        var items = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        string? kind = null, operation = null;
        var data = numbered.Skip(1).ToList();
        if (data.Count == 0) errors.Add("The file has a header row and no objects.");
        if (data.Count > ReportsLimits.MaxMassObjects) errors.Add($"At most {ReportsLimits.MaxMassObjects} objects can be changed in one operation; the file has {data.Count}.");
        for (var n = 0; n < data.Count && n < ReportsLimits.MaxMassObjects && errors.Count < 20; n++)
        {
            var (line, r) = data[n]; // line is the spreadsheet row number
            var key = Cell(r, keyIdx);
            if (string.IsNullOrEmpty(key)) { errors.Add($"Row {line}: the object is empty."); continue; }
            if (key.Length > 64) { errors.Add($"Row {line}: the object is longer than 64 characters."); continue; }
            if (keys.Contains(key, StringComparer.Ordinal)) { errors.Add($"Row {line}: {key} is listed twice. List each object once."); continue; }
            var k = Cell(r, kindIdx); var o = Cell(r, opIdx);
            if (!string.IsNullOrEmpty(k)) { k = k.ToLowerInvariant(); if (kind is null) kind = k; else if (kind != k) errors.Add($"Row {line}: the kind '{k}' differs from '{kind}'; one file changes one kind of object."); }
            if (!string.IsNullOrEmpty(o)) { o = o.ToLowerInvariant().Replace(' ', '_'); if (operation is null) operation = o; else if (operation != o) errors.Add($"Row {line}: the operation '{o}' differs from '{operation}'; one file runs one operation."); }
            var p = new JsonObject();
            foreach (var (idx, name) in columns)
            {
                var v = Cell(r, idx);
                if (string.IsNullOrEmpty(v)) continue;
                switch (ParameterColumns[name])
                {
                    case "number":
                        if (!decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) || d <= 0) errors.Add($"Row {line}: {name} '{v}' is not a positive number.");
                        else p[name] = d;
                        break;
                    case "integer":
                        if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var iv) || iv <= 0) errors.Add($"Row {line}: {name} '{v}' is not a whole number above zero.");
                        else p[name] = iv;
                        break;
                    case "list":
                        p[name] = new JsonArray([.. v.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => (JsonNode?)x)]);
                        break;
                    default: p[name] = v; break;
                }
            }
            keys.Add(key);
            if (p.Count > 0) items[key] = p;
        }
        return new(kind, operation, keys, items, data.Count, errors);
    }
}
