using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Keel.Cad.Structure;

public sealed record StructureReadOptions(string? RootNumber = null);

/// <summary>Entry point of the importers: bytes and a format in, the validated neutral model out.</summary>
public static class StructureReader
{
    public static StructureModel Read(string format, ReadOnlyMemory<byte> content, StructureReadOptions? options = null)
    {
        options ??= new StructureReadOptions();
        if (content.Length == 0) throw new StructureFormatException("The file is empty.");
        if (content.Length > StructureLimits.MaxBytes) throw new StructureFormatException($"The file is larger than {StructureLimits.MaxBytes / (1024 * 1024)} MiB.");
        var model = format switch
        {
            StructureFormats.Json => JsonStructureReader.Read(content.Span),
            StructureFormats.Step => StepStructureReader.Read(Encoding.Latin1.GetString(content.Span), options),
            StructureFormats.Csv => CsvStructureReader.Read(DecodeText(content.Span), options),
            StructureFormats.Xml => XmlStructureReader.Read(content),
            _ => throw new StructureFormatException($"Format must be one of: {string.Join(", ", StructureFormats.All)}."),
        };
        return StructureValidator.Validate(model);
    }

    internal static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new StructureFormatException("The file is not valid UTF-8 text."); }
    }
}

/// <summary>The neutral JSON structure, schema <c>keel.cad.structure/v1</c> (docs/integration/cad/structure-v1.schema.json).</summary>
public static class JsonStructureReader
{
    public const string SchemaId = "keel.cad.structure/v1";

    public static StructureModel Read(ReadOnlySpan<byte> utf8)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow }); }
        catch (JsonException e) { throw new StructureFormatException("The file is not valid JSON: " + FirstLine(e.Message)); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new StructureFormatException("The JSON document must be an object.");
            if (Str(root, "schema") != SchemaId) throw new StructureFormatException($"'schema' must be \"{SchemaId}\".");
            string? system = root.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object ? Str(src, "system") : null;
            var parts = new List<StructurePart>();
            if (!root.TryGetProperty("parts", out var partsEl) || partsEl.ValueKind != JsonValueKind.Array) throw new StructureFormatException("'parts' must be an array.");
            foreach (var p in partsEl.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) throw new StructureFormatException("Every entry of 'parts' must be an object.");
                var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
                if (p.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object)
                    foreach (var kv in a.EnumerateObject())
                        attrs[kv.Name] = kv.Value.ValueKind switch
                        {
                            JsonValueKind.String => kv.Value.GetString()!,
                            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => kv.Value.GetRawText(),
                            _ => throw new StructureFormatException($"Attribute '{kv.Name}' must be text, a number or true/false."),
                        };
                var match = new List<MatchRule>();
                if (p.TryGetProperty("matchOn", out var m) && m.ValueKind == JsonValueKind.Array)
                    foreach (var r in m.EnumerateArray())
                        match.Add(new MatchRule(Str(r, "attribute") ?? throw new StructureFormatException("A matchOn rule needs 'attribute'."),
                            Str(r, "value") ?? throw new StructureFormatException("A matchOn rule needs 'value'.")));
                var @ref = Str(p, "ref") ?? throw new StructureFormatException("Every part needs a 'ref'.");
                parts.Add(new StructurePart(@ref, Str(p, "number"), Str(p, "name") ?? Str(p, "number") ?? @ref, Str(p, "partType"), Str(p, "unit"), Str(p, "source"), attrs, match));
            }
            var lines = new List<StructureLine>();
            if (root.TryGetProperty("lines", out var linesEl))
            {
                if (linesEl.ValueKind != JsonValueKind.Array) throw new StructureFormatException("'lines' must be an array.");
                foreach (var l in linesEl.EnumerateArray())
                {
                    if (l.ValueKind != JsonValueKind.Object) throw new StructureFormatException("Every entry of 'lines' must be an object.");
                    if (!l.TryGetProperty("quantity", out var q) || q.ValueKind != JsonValueKind.Number || !q.TryGetDecimal(out var qty))
                        throw new StructureFormatException("Every line needs a numeric 'quantity'.");
                    int? find = l.TryGetProperty("findNumber", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt32(out var fi) ? fi : null;
                    var refdes = new List<string>();
                    if (l.TryGetProperty("referenceDesignators", out var rd) && rd.ValueKind == JsonValueKind.Array)
                        foreach (var d in rd.EnumerateArray())
                            refdes.Add(d.ValueKind == JsonValueKind.String ? d.GetString()! : throw new StructureFormatException("Reference designators must be text."));
                    lines.Add(new StructureLine(Str(l, "parent") ?? throw new StructureFormatException("Every line needs 'parent'."),
                        Str(l, "child") ?? throw new StructureFormatException("Every line needs 'child'."), qty, Str(l, "unit"), find, refdes, Str(l, "notes")));
                }
            }
            return new StructureModel(system, Str(root, "root") ?? throw new StructureFormatException("'root' (the ref of the top part) is required."), parts, lines);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string FirstLine(string s) => s.Split('\n')[0];
}

/// <summary>
/// CSV (RFC 4180 quoting; comma, semicolon or tab). Columns, matched case-insensitively: parent, child (required), name, quantity, unit, find,
/// refdes, notes, type, source, attr.&lt;attribute&gt; (attribute of the child), match.&lt;attribute&gt; (identifies an existing part). Without a
/// parent column the file is a flat BOM of <see cref="StructureReadOptions.RootNumber"/>.
/// </summary>
public static class CsvStructureReader
{
    public static StructureModel Read(string text, StructureReadOptions options)
    {
        var rows = Parse(text);
        if (rows.Count < 2) throw new StructureFormatException("The CSV needs a header row and at least one usage row.");
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));
        var parentCol = Col("parent", "parent_number", "assembly");
        var childCol = Col("child", "child_number", "part_number", "number", "component");
        if (childCol < 0) throw new StructureFormatException("The CSV needs a 'child' (or 'part_number') column.");
        var nameCol = Col("name", "child_name", "description");
        var qtyCol = Col("quantity", "qty");
        var unitCol = Col("unit", "uom");
        var findCol = Col("find", "find_number", "item");
        var refCol = Col("refdes", "reference_designators", "reference_designator");
        var notesCol = Col("notes", "note");
        var typeCol = Col("type", "part_type");
        var sourceCol = Col("source");
        var attrCols = header.Select((h, i) => (h, i)).Where(x => x.h.StartsWith("attr.", StringComparison.Ordinal)).ToList();
        var matchCols = header.Select((h, i) => (h, i)).Where(x => x.h.StartsWith("match.", StringComparison.Ordinal)).ToList();
        if (parentCol < 0 && string.IsNullOrWhiteSpace(options.RootNumber))
            throw new StructureFormatException("The CSV has no 'parent' column: give the number of the top part as the root.");

        var parts = new Dictionary<string, StructurePart>(StringComparer.Ordinal);
        var lines = new List<StructureLine>();
        string Cell(List<string> r, int c) => c >= 0 && c < r.Count ? r[c].Trim() : "";
        var parentOnly = new HashSet<string>(StringComparer.Ordinal);
        void Ensure(string number, string? name, List<string>? row, bool isChild)
        {
            if (parts.ContainsKey(number) && !(isChild && parentOnly.Contains(number))) return;
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            var match = new List<MatchRule>();
            if (isChild && row is not null)
            {
                foreach (var (h, i) in attrCols) if (Cell(row, i).Length > 0) attrs[h["attr.".Length..]] = Cell(row, i);
                foreach (var (h, i) in matchCols) if (Cell(row, i).Length > 0) match.Add(new MatchRule(h["match.".Length..], Cell(row, i)));
            }
            var r0 = row ?? [];
            parts[number] = new StructurePart(number, number, string.IsNullOrEmpty(name) ? number : name, isChild ? NullIfEmpty(Cell(r0, typeCol)) : null,
                isChild ? NullIfEmpty(Cell(r0, unitCol)) : null, isChild ? NullIfEmpty(Cell(r0, sourceCol)) : null, attrs, match);
            if (isChild) parentOnly.Remove(number); else parentOnly.Add(number);
        }

        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.All(c => string.IsNullOrWhiteSpace(c))) continue;
            var child = Cell(row, childCol);
            var parent = parentCol >= 0 ? Cell(row, parentCol) : options.RootNumber!.Trim();
            if (child.Length == 0 || parent.Length == 0) throw new StructureFormatException($"Row {r + 1}: parent and child are required.");
            Ensure(parent, null, null, isChild: false);
            Ensure(child, Cell(row, nameCol), row, isChild: true);
            var qtyText = Cell(row, qtyCol);
            var qty = qtyText.Length == 0 ? 1m : StructureValidator.ParseQuantity(qtyText, $"Row {r + 1}");
            var findText = Cell(row, findCol);
            lines.Add(new StructureLine(parent, child, qty, NullIfEmpty(Cell(row, unitCol)), findText.Length == 0 ? null : StructureValidator.ParseFind(findText, $"Row {r + 1}"),
                StructureValidator.SplitDesignators(Cell(row, refCol)), NullIfEmpty(Cell(row, notesCol))));
        }
        var childSet = lines.Select(l => l.ChildRef).ToHashSet(StringComparer.Ordinal);
        var root = options.RootNumber?.Trim();
        if (string.IsNullOrEmpty(root))
        {
            var roots = parts.Keys.Where(k => !childSet.Contains(k)).ToList();
            root = roots.Count == 1 ? roots[0] : throw new StructureFormatException(roots.Count == 0 ? "The structure has no top part." : "The structure has several top parts; give the number of the one to import as the root.");
        }
        // Parents become assemblies unless the file says otherwise.
        var parentSet = lines.Select(l => l.ParentRef).ToHashSet(StringComparer.Ordinal);
        var list = parts.Values.Select(p => p.PartType is null && parentSet.Contains(p.Ref) ? p with { PartType = "Assembly" } : p).ToList();
        return new StructureModel("CSV", root, list, lines);
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    internal static List<List<string>> Parse(string text)
    {
        var firstLine = text.AsSpan(0, Math.Max(0, text.IndexOfAny(['\r', '\n']) is var i and >= 0 ? i : text.Length));
        var delimiter = firstLine.Count(';') > firstLine.Count(',') ? ';' : firstLine.Count('\t') > firstLine.Count(',') ? '\t' : ',';
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var p = 0; p < text.Length; p++)
        {
            var c = text[p];
            if (quoted)
            {
                if (c == '"' && p + 1 < text.Length && text[p + 1] == '"') { cell.Append('"'); p++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == delimiter) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && p + 1 < text.Length && text[p + 1] == '\n') p++;
                row.Add(cell.ToString()); cell.Clear();
                rows.Add(row); row = [];
                if (rows.Count > StructureLimits.MaxLines + 1) throw new StructureFormatException($"The CSV has more than {StructureLimits.MaxLines} rows.");
            }
            else cell.Append(c);
        }
        if (quoted) throw new StructureFormatException("The CSV has an unterminated quoted value.");
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}

/// <summary>
/// The neutral XML form: <c>&lt;structure root="ref" system="..."&gt;</c> with <c>&lt;part ref number name type unit source&gt;</c> (children
/// <c>&lt;attribute key value/&gt;</c> and <c>&lt;match attribute value/&gt;</c>) and <c>&lt;line parent child quantity unit find refdes notes/&gt;</c>.
/// DTDs and external entities are refused.
/// </summary>
public static class XmlStructureReader
{
    public static StructureModel Read(ReadOnlyMemory<byte> content)
    {
        XDocument doc;
        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = StructureLimits.MaxBytes });
            doc = XDocument.Load(reader);
        }
        catch (XmlException e) { throw new StructureFormatException("The file is not valid XML: " + e.Message.Split('\n')[0]); }
        var root = doc.Root ?? throw new StructureFormatException("The XML document is empty.");
        if (root.Name.LocalName != "structure") throw new StructureFormatException("The root element must be <structure>.");
        var parts = root.Elements().Where(e => e.Name.LocalName == "part").Select(p =>
        {
            var @ref = A(p, "ref") ?? throw new StructureFormatException("Every <part> needs a 'ref'.");
            var attrs = p.Elements().Where(e => e.Name.LocalName == "attribute").ToDictionary(
                e => A(e, "key") ?? throw new StructureFormatException("An <attribute> needs a 'key'."), e => A(e, "value") ?? "", StringComparer.Ordinal);
            var match = p.Elements().Where(e => e.Name.LocalName == "match").Select(e =>
                new MatchRule(A(e, "attribute") ?? throw new StructureFormatException("A <match> needs 'attribute'."), A(e, "value") ?? throw new StructureFormatException("A <match> needs 'value'."))).ToList();
            return new StructurePart(@ref, A(p, "number"), A(p, "name") ?? A(p, "number") ?? @ref, A(p, "type"), A(p, "unit"), A(p, "source"), attrs, match);
        }).ToList();
        var lines = root.Elements().Where(e => e.Name.LocalName == "line").Select((l, i) =>
            new StructureLine(A(l, "parent") ?? throw new StructureFormatException($"Line {i + 1} needs 'parent'."), A(l, "child") ?? throw new StructureFormatException($"Line {i + 1} needs 'child'."),
                A(l, "quantity") is { } q ? StructureValidator.ParseQuantity(q, $"Line {i + 1}") : 1m, A(l, "unit"),
                A(l, "find") is { } f ? StructureValidator.ParseFind(f, $"Line {i + 1}") : null, StructureValidator.SplitDesignators(A(l, "refdes")), A(l, "notes"))).ToList();
        return new StructureModel(A(root, "system") ?? "XML", A(root, "root") ?? throw new StructureFormatException("<structure> needs a 'root' (the ref of the top part)."), parts, lines);
    }

    private static string? A(XElement e, string name) => e.Attribute(name)?.Value;
}
