using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Keel.Requirements;

/// <summary>One requirement as read from a file. Blank fields mean "keep what is there" on an update and "use the default" on a new requirement.</summary>
public sealed record ImportRow(
    int Line, string? SourceId, string? ParentRef, string? Title, string? Text, string? Type, string? Priority, string? Method, string? Rationale, string? Status);

public sealed record ParsedFile(IReadOnlyList<ImportRow> Rows, IReadOnlyList<string> Errors);

public static class ImportFormats
{
    public const string Csv = "csv", Excel = "excel", ReqIf = "reqif";
    public static readonly IReadOnlyList<string> All = [Csv, Excel, ReqIf];
    public static string Extension(string format) => format == ReqIf ? "reqif" : "csv";
    public static string ContentType(string format) => format == ReqIf ? "application/xml" : "text/csv";
    public static string Label(string format) => format switch { Csv => "CSV (comma separated, one requirement per row)", Excel => "Spreadsheet CSV (as saved by Excel)", ReqIf => "ReqIF-style XML", _ => format };
}

/// <summary>
/// Reading and writing requirement files (ADR-0038): CSV, spreadsheet-style CSV (any of comma, semicolon or tab, quotes, optional byte-order mark, header names as spreadsheets
/// call them) and a ReqIF-style XML (spec objects with string attributes and a specification hierarchy). The XML reader refuses DTDs and external entities. Exports guard
/// spreadsheet cells against formula injection. Pure: no database.
/// </summary>
public static class RequirementIo
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxRows = RequirementRules.MaxRequirements;

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["key"] = "key", ["id"] = "key", ["requirementid"] = "key", ["reqid"] = "key", ["identifier"] = "key",
        ["parent"] = "parent", ["parentid"] = "parent", ["parentkey"] = "parent", ["parentrequirement"] = "parent",
        ["title"] = "title", ["name"] = "title", ["summary"] = "title", ["heading"] = "title",
        ["text"] = "text", ["requirement"] = "text", ["requirementtext"] = "text", ["description"] = "text", ["body"] = "text", ["statement"] = "text",
        ["type"] = "type", ["category"] = "type", ["requirementtype"] = "type",
        ["priority"] = "priority",
        ["method"] = "method", ["verification"] = "method", ["verificationmethod"] = "method",
        ["rationale"] = "rationale", ["reason"] = "rationale",
        ["status"] = "status", ["state"] = "status",
    };

    private static string Norm(string header) => new([.. header.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    // ---------------------------------------------------------------- CSV

    public static ParsedFile ParseCsv(string content, bool spreadsheet)
    {
        var errors = new List<string>();
        if (content.Length > 0 && content[0] == '﻿') content = content[1..];
        var lines = new List<(int Line, List<string> Fields)>();
        var delimiter = spreadsheet ? DetectDelimiter(content) : ',';
        var field = new StringBuilder();
        var current = new List<string>();
        var inQuotes = false;
        var line = 1;
        var recordStart = 1;
        var wasQuoted = false;
        void EndField() { current.Add(field.ToString()); field.Clear(); wasQuoted = false; }
        void EndRecord()
        {
            EndField();
            if (!(current.Count == 1 && current[0].Length == 0)) lines.Add((recordStart, current));
            current = [];
        }
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"') { if (i + 1 < content.Length && content[i + 1] == '"') { field.Append('"'); i++; } else inQuotes = false; }
                else { if (c == '\n') line++; field.Append(c); }
                continue;
            }
            if (c == '"' && field.Length == 0 && !wasQuoted) { inQuotes = true; wasQuoted = true; }
            else if (c == delimiter) EndField();
            else if (c == '\r') { }
            else if (c == '\n') { EndRecord(); line++; recordStart = line; }
            else field.Append(c);
        }
        if (inQuotes) { errors.Add("The file ends inside a quoted value."); return new ParsedFile([], errors); }
        if (field.Length > 0 || current.Count > 0) EndRecord();
        lines = [.. lines.Where(l => !(l.Fields.Count > 0 && l.Fields[0].StartsWith('#')))]; // a leading '#' line is a comment (the export puts its marking banner there)
        if (lines.Count == 0) { errors.Add("The file has no header row."); return new ParsedFile([], errors); }
        var header = lines[0].Fields.Select(h => Aliases.GetValueOrDefault(Norm(h))).ToList();
        if (!header.Contains("title") || !header.Contains("text")) errors.Add("The header row needs at least a title column and a text column.");
        if (header.Where(h => h is not null).GroupBy(h => h).Any(g => g.Count() > 1)) errors.Add("Two columns mean the same thing; keep one.");
        if (errors.Count > 0) return new ParsedFile([], errors);
        var rows = new List<ImportRow>();
        foreach (var (ln, fields) in lines.Skip(1))
        {
            if (rows.Count >= MaxRows) { errors.Add($"The file has more than {MaxRows} requirements."); break; }
            string? Get(string name) { var i = header.IndexOf(name); return i >= 0 && i < fields.Count && fields[i].Trim().Length > 0 ? Unguard(fields[i].Trim()) : null; }
            rows.Add(new ImportRow(ln, Get("key"), Get("parent"), Get("title"), Get("text"), Get("type")?.ToLowerInvariant(), Get("priority")?.ToLowerInvariant(), Get("method")?.ToLowerInvariant(), Get("rationale"), Get("status")?.ToLowerInvariant()));
        }
        return new ParsedFile(rows, errors);
    }

    private static char DetectDelimiter(string content)
    {
        var inQuotes = false;
        int commas = 0, semis = 0, tabs = 0;
        // The header row decides; comment lines before it (the export's marking banner) do not.
        while (content.StartsWith('#')) { var nl = content.IndexOf('\n', StringComparison.Ordinal); if (nl < 0) break; content = content[(nl + 1)..]; }
        foreach (var c in content)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (!inQuotes && c == '\n') break;
            else if (!inQuotes) { if (c == ',') commas++; else if (c == ';') semis++; else if (c == '\t') tabs++; }
        }
        return tabs > commas && tabs >= semis ? '\t' : semis > commas ? ';' : ',';
    }

    // A cell exported with a leading apostrophe (formula guard) is read back without it.
    private static string Unguard(string v) => v.Length > 1 && v[0] == '\'' && v[1] is '=' or '+' or '-' or '@' ? v[1..] : v;

    private static string Guard(string v) => v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + v : v;

    private static string CsvField(string v, char delimiter, bool guard)
    {
        if (guard) v = Guard(v);
        return v.Contains(delimiter, StringComparison.Ordinal) || v.Contains('"', StringComparison.Ordinal) || v.Contains('\n', StringComparison.Ordinal) || v.Contains('\r', StringComparison.Ordinal)
            ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }

    public static string ExportCsv(IEnumerable<RequirementRow> rows, string banner, bool spreadsheet)
    {
        var nl = spreadsheet ? "\r\n" : "\n";
        var d = spreadsheet ? ';' : ',';
        var sb = new StringBuilder();
        if (spreadsheet) sb.Append('﻿');
        sb.Append("# ").Append(banner).Append(nl);
        sb.Append(string.Join(d, "key", "parent", "title", "text", "type", "priority", "method", "rationale", "status")).Append(nl);
        foreach (var r in rows.Where(r => !r.Redacted))
            sb.Append(string.Join(d, new[] { r.Key, r.ParentKey ?? "", r.Title, r.Text, r.Type, r.Priority, r.Method, r.Rationale ?? "", r.Status }.Select(v => CsvField(v ?? "", d, true)))).Append(nl);
        return sb.ToString();
    }

    // ---------------------------------------------------------------- ReqIF-style XML

    private const string Ns = "http://www.omg.org/spec/ReqIF/20110401/reqif.xsd";
    private static readonly string[] Attributes = ["title", "text", "type", "priority", "method", "rationale", "status"];

    public static ParsedFile ParseReqIf(string content)
    {
        var errors = new List<string>();
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes * 2L });
            doc = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException e)
        {
            return new ParsedFile([], [$"The XML cannot be read: {e.Message}"]);
        }
        var objects = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var o in doc.Descendants().Where(e => e.Name.LocalName == "SPEC-OBJECT"))
        {
            var id = (string?)o.Attribute("IDENTIFIER");
            if (string.IsNullOrWhiteSpace(id)) { errors.Add($"Line {Line(o)}: a spec object has no identifier."); continue; }
            if (!objects.TryAdd(id, o)) errors.Add($"Line {Line(o)}: the identifier {id} is used twice.");
        }
        // The hierarchy gives parents and order; spec objects outside it are roots, in document order.
        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        var order = new List<string>();
        void Walk(XElement children, string? parent)
        {
            foreach (var h in children.Elements().Where(e => e.Name.LocalName == "SPEC-HIERARCHY"))
            {
                var reference = h.Elements().FirstOrDefault(e => e.Name.LocalName == "OBJECT")?.Elements().FirstOrDefault(e => e.Name.LocalName == "SPEC-OBJECT-REF")?.Value.Trim();
                if (reference is null || !objects.ContainsKey(reference)) { errors.Add($"Line {Line(h)}: the hierarchy names an object that does not exist."); continue; }
                if (parents.ContainsKey(reference)) { errors.Add($"Line {Line(h)}: {reference} appears twice in the hierarchy."); continue; }
                parents[reference] = parent;
                order.Add(reference);
                if (h.Elements().FirstOrDefault(e => e.Name.LocalName == "CHILDREN") is { } sub) Walk(sub, reference);
            }
        }
        foreach (var spec in doc.Descendants().Where(e => e.Name.LocalName == "SPECIFICATION"))
            if (spec.Elements().FirstOrDefault(e => e.Name.LocalName == "CHILDREN") is { } c) Walk(c, null);
        foreach (var id in objects.Keys.Where(k => !parents.ContainsKey(k))) { parents[id] = null; order.Add(id); }
        var rows = new List<ImportRow>();
        foreach (var id in order)
        {
            if (rows.Count >= MaxRows) { errors.Add($"The file has more than {MaxRows} requirements."); break; }
            var o = objects[id];
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var v in o.Descendants().Where(e => e.Name.LocalName.StartsWith("ATTRIBUTE-VALUE-", StringComparison.Ordinal)))
            {
                var name = v.Descendants().FirstOrDefault(e => e.Name.LocalName.EndsWith("-REF", StringComparison.Ordinal))?.Value.Trim().ToLowerInvariant();
                var value = (string?)v.Attribute("THE-VALUE") ?? v.Value;
                if (name is not null && Aliases.TryGetValue(Norm(name), out var canon)) values[canon] = value.Trim();
            }
            string? Get(string n) => values.TryGetValue(n, out var s) && s.Length > 0 ? s : null;
            rows.Add(new ImportRow(Line(o), id, parents[id], Get("title"), Get("text"), Get("type")?.ToLowerInvariant(), Get("priority")?.ToLowerInvariant(), Get("method")?.ToLowerInvariant(), Get("rationale"), Get("status")?.ToLowerInvariant()));
        }
        if (rows.Count == 0 && errors.Count == 0) errors.Add("The file contains no spec objects.");
        return new ParsedFile(rows, errors);
    }

    private static int Line(XObject o) => o is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

    public static string ExportReqIf(IEnumerable<RequirementRow> rows, string specNumber, string banner)
    {
        var list = rows.Where(r => !r.Redacted).ToList();
        XNamespace ns = Ns;
        XElement Object(RequirementRow r) => new(ns + "SPEC-OBJECT", new XAttribute("IDENTIFIER", r.Key!),
            new XElement(ns + "VALUES", new[] { ("title", r.Title), ("text", r.Text), ("type", r.Type), ("priority", r.Priority), ("method", r.Method), ("rationale", r.Rationale), ("status", r.Status) }
                .Select(a => new XElement(ns + "ATTRIBUTE-VALUE-STRING", new XAttribute("THE-VALUE", a.Item2 ?? ""), new XElement(ns + "DEFINITION", new XElement(ns + "ATTRIBUTE-DEFINITION-STRING-REF", a.Item1))))));
        var byParent = list.GroupBy(r => r.ParentKey ?? "").ToDictionary(g => g.Key, g => g.ToList());
        XElement Children(string parent) => new(ns + "CHILDREN", (byParent.GetValueOrDefault(parent) ?? []).Select(r =>
        {
            var h = new XElement(ns + "SPEC-HIERARCHY", new XAttribute("IDENTIFIER", "H-" + r.Key), new XElement(ns + "OBJECT", new XElement(ns + "SPEC-OBJECT-REF", r.Key)));
            if (byParent.ContainsKey(r.Key!)) h.Add(Children(r.Key!));
            return h;
        }));
        // A readable requirement whose parent is restricted is a root of the export.
        var known = list.Select(r => r.Key!).ToHashSet(StringComparer.Ordinal);
        var roots = list.Where(r => r.ParentKey is null || !known.Contains(r.ParentKey)).ToList();
        byParent[""] = roots;
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XComment(" " + banner.Replace("--", "- -", StringComparison.Ordinal) + " "),
            new XElement(ns + "REQ-IF",
                new XElement(ns + "THE-HEADER", new XElement(ns + "REQ-IF-HEADER", new XAttribute("IDENTIFIER", specNumber), new XElement(ns + "COMMENT", banner), new XElement(ns + "REQ-IF-TOOL-ID", "Keel PLM"))),
                new XElement(ns + "CORE-CONTENT", new XElement(ns + "REQ-IF-CONTENT",
                    new XElement(ns + "SPEC-OBJECTS", list.Select(Object)),
                    new XElement(ns + "SPECIFICATIONS", new XElement(ns + "SPECIFICATION", new XAttribute("IDENTIFIER", specNumber), Children("")))))));
        return doc.Declaration + "\n" + doc.ToString(SaveOptions.None);
    }

    // ---------------------------------------------------------------- digest of a plan

    public static string Digest(IEnumerable<string> lines) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("req-import/1\n" + string.Join('\n', lines))));

    internal static string Fmt(int n) => n.ToString(CultureInfo.InvariantCulture);
}
