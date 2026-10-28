using System.Globalization;
using System.Text;
using System.Text.Json;
using Keel.Migration.Canonical;

namespace Keel.Migration.Sources;

/// <summary>One record of a Windchill extract table: column name to text. JSON rows are flattened ({"Value": x} objects to x).</summary>
public sealed class WRow : Dictionary<string, string?>
{
    public WRow() : base(StringComparer.OrdinalIgnoreCase) { }
    public string? Get(params string[] names)
    {
        foreach (var n in names) if (TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
        return null;
    }
}

/// <summary>The extract tables shared by the JSON and CSV forms (docs/migration/windchill-extract-spec.md).</summary>
public sealed class WindchillTables
{
    public List<WRow> Parts { get; } = [];
    public List<WRow> PartUses { get; } = [];
    public List<WRow> Documents { get; } = [];
    public List<WRow> DocumentFiles { get; } = [];
    public List<WRow> PartDocLinks { get; } = [];
    public List<(string Type, WRow Row)> Changes { get; } = [];
    public List<WRow> ChangeAffected { get; } = [];
    public List<WRow> Users { get; } = [];
    public List<WRow> Classifications { get; } = [];
}

/// <summary>Builds the canonical model from Windchill extract tables.</summary>
internal static class WindchillBuilder
{
    private static readonly HashSet<string> PartCore = new(StringComparer.OrdinalIgnoreCase)
    { "Number", "Name", "Version", "Iteration", "State", "CreatedOn", "CreatedBy", "LastModified", "ModifiedBy", "StateChangedOn", "StateChangedBy", "DefaultUnit", "Unit", "Source", "Description", "PartType", "Type", "Comment" };
    private static readonly HashSet<string> DocCore = new(StringComparer.OrdinalIgnoreCase)
    { "Number", "Name", "Version", "Iteration", "State", "CreatedOn", "CreatedBy", "LastModified", "ModifiedBy", "StateChangedOn", "StateChangedBy", "Description", "DocumentType", "Type", "Comment" };

    public static DateTimeOffset? Date(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public static void Build(WindchillTables t, StagingSink sink)
    {
        sink.Inventory.SourceSystem = "windchill";
        sink.Package.SourceSystem = "windchill";
        var items = BuildObjects(t.Parts, PartCore, isDoc: false, sink);
        var docs = BuildObjects(t.Documents, DocCore, isDoc: true, sink);

        // usages
        foreach (var r in t.PartUses)
        {
            var parent = r.Get("ParentNumber"); var child = r.Get("ChildNumber");
            if (parent is null || child is null) { sink.Report(Severity.Error, "SRC-MISSING-FIELD", "usage", $"{parent}->{child}", "Part use needs ParentNumber and ChildNumber."); continue; }
            var it = FindIteration(items, parent, r.Get("ParentVersion"), r.Get("ParentIteration"));
            if (it is null) { sink.Report(Severity.Error, "SRC-DANGLING-REF", "usage", $"{parent}->{child}", $"Part use refers to parent {parent} {r.Get("ParentVersion")}.{r.Get("ParentIteration")}, which is not in parts."); continue; }
            if (!items.ContainsKey(child)) sink.Report(Severity.Error, "SRC-DANGLING-REF", "usage", $"{parent}->{child}", $"Part use refers to child {child}, which is not in parts.");
            var u = new CUsage { ChildSourceId = child, Unit = r.Get("Unit", "QuantityUnit"), Notes = r.Get("Notes"), Effectivity = r.Get("Effectivity") };
            if (decimal.TryParse(r.Get("Quantity") ?? "1", NumberStyles.Float, CultureInfo.InvariantCulture, out var q)) u.Quantity = q;
            else sink.Report(Severity.Error, "SRC-BAD-NUMBER", "usage", $"{parent}->{child}", $"Quantity '{r.Get("Quantity")}' is not a number.");
            var find = r.Get("FindNumber", "LineNumber");
            if (find is not null)
            {
                if (int.TryParse(find, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f)) u.FindNumber = f;
                else sink.Report(Severity.Error, "SRC-BAD-NUMBER", "usage", $"{parent}->{child}", $"Find number '{find}' is not an integer.");
            }
            u.ReferenceDesignators = (r.Get("ReferenceDesignators") ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            // Structured effectivity (one range per row; rows with the same find number are alternates): EffectivityType serial|lot|date, EffectivityUnit, From, To, EndCondition.
            if (r.Get("EffectivityType") is { } effType)
                u.EffectivityRanges.Add(new CEffectivity
                {
                    Kind = effType, Unit = r.Get("EffectivityUnit", "EffectivityContext"), From = r.Get("EffectivityFrom"), To = r.Get("EffectivityTo"), EndCondition = r.Get("EffectivityEndCondition"),
                });
            it.Usages.Add(u);
        }

        // document files
        foreach (var r in t.DocumentFiles)
        {
            var num = r.Get("DocumentNumber");
            var it = num is null ? null : FindIteration(docs, num, r.Get("DocumentVersion", "Version"), r.Get("DocumentIteration", "Iteration"));
            if (it is null) { sink.Report(Severity.Error, "SRC-DANGLING-REF", "file", $"{num}", $"Content row refers to document {num} {r.Get("DocumentVersion", "Version")}, which is not in documents."); continue; }
            var path = r.Get("Path");
            var f = new CFile
            {
                Name = r.Get("Name", "FileName") ?? Path.GetFileName(path ?? ""),
                MediaType = r.Get("ContentType", "MimeType"),
                PackagePath = PackageSource.Normalize(path),
                Sha256 = r.Get("Sha256")?.ToLowerInvariant(),
                SizeBytes = long.TryParse(r.Get("FileSize", "Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null,
                Primary = string.Equals(r.Get("Role"), "PRIMARY", StringComparison.OrdinalIgnoreCase),
            };
            if (path is not null && f.PackagePath is null) sink.Report(Severity.Error, "SRC-UNSAFE-PATH", "file", num!, $"File path '{path}' leaves the package and is refused.");
            it.Files.Add(f);
        }

        // part <-> document links
        foreach (var r in t.PartDocLinks)
        {
            var pn = r.Get("PartNumber"); var dn = r.Get("DocumentNumber");
            var it = pn is null ? null : FindIteration(items, pn, r.Get("PartVersion"), r.Get("PartIteration"));
            if (it is null || dn is null || !docs.ContainsKey(dn))
            {
                sink.Report(Severity.Error, "SRC-DANGLING-REF", "link", $"{pn}->{dn}", $"Link refers to part {pn} or document {dn}, which is not in the extract.");
                continue;
            }
            var kind = r.Get("LinkType") ?? "Describe";
            it.DocLinks.Add(new CDocLink { Type = kind.StartsWith("Ref", StringComparison.OrdinalIgnoreCase) ? "references" : "describes", DocumentSourceId = dn, DocumentRevisionLabel = r.Get("DocumentVersion") });
        }

        foreach (var o in items.Values) sink.Add(o, false);
        foreach (var o in docs.Values) sink.Add(o, true);

        // changes
        var changes = new Dictionary<string, CChange>(StringComparer.Ordinal);
        foreach (var (type, r) in t.Changes)
        {
            var num = r.Get("Number");
            if (num is null) { sink.Report(Severity.Error, "SRC-MISSING-FIELD", "change", "?", "Change row without Number."); continue; }
            var c = new CChange
            {
                SourceId = num, Number = num, Type = type, Title = r.Get("Name", "Title") ?? "", Description = r.Get("Description"), State = r.Get("State") ?? "",
                ChangeClass = r.Get("ChangeClass"), Priority = r.Get("Priority"), Reason = r.Get("Reason"), CreatedAt = Date(r.Get("CreatedOn")), CreatedBy = r.Get("CreatedBy"),
                StateChangedAt = Date(r.Get("StateChangedOn", "LastModified")), StateChangedBy = r.Get("StateChangedBy", "ModifiedBy"), ParentSourceId = r.Get("ParentNumber"),
            };
            foreach (var kv in r.Where(k => !ChangeCore.Contains(k.Key))) c.Properties[kv.Key] = kv.Value;
            if (!changes.TryAdd(num, c)) sink.Report(Severity.Error, "SRC-DUP-ID", "change", num, $"Change {num} appears twice in the extract.");
        }
        foreach (var r in t.ChangeAffected)
        {
            var cn = r.Get("ChangeNumber"); var item = r.Get("ItemNumber");
            if (cn is null || item is null || !changes.TryGetValue(cn, out var ch) || !items.ContainsKey(item))
            {
                sink.Report(Severity.Error, "SRC-DANGLING-REF", "change", $"{cn}", $"Affected-object row refers to change {cn} or item {item}, which is not in the extract.");
                continue;
            }
            ch.Affected.Add(new CAffected { ItemSourceId = item, RevisionLabel = r.Get("Version"), ResultingRevisionLabel = r.Get("ResultingVersion"), Action = (r.Get("Action") ?? "none").ToLowerInvariant() });
        }
        foreach (var c in changes.Values) sink.Add(c);

        foreach (var r in t.Users) if (r.Get("Name") is { } n) sink.Add(new CUser { Name = n, DisplayName = r.Get("FullName"), Groups = (r.Get("Groups") ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() });
        foreach (var r in t.Classifications)
        {
            var pn = r.Get("PartNumber"); var cc = r.Get("ClassCode");
            if (pn is null || cc is null || !items.ContainsKey(pn)) { sink.Report(Severity.Error, "SRC-DANGLING-REF", "classification", $"{pn}", $"Classification row refers to part {pn} or has no ClassCode."); continue; }
            sink.Add(new CClassAssignment { ItemSourceId = pn, ClassCode = cc, Attributes = r.Where(k => !ClassCore.Contains(k.Key)).ToDictionary(k => k.Key, k => k.Value) });
        }
    }

    private static readonly HashSet<string> ChangeCore = new(StringComparer.OrdinalIgnoreCase)
    { "Number", "Name", "Title", "Description", "State", "ChangeClass", "Priority", "Reason", "CreatedOn", "CreatedBy", "StateChangedOn", "LastModified", "StateChangedBy", "ModifiedBy", "ParentNumber" };
    private static readonly HashSet<string> ClassCore = new(StringComparer.OrdinalIgnoreCase) { "PartNumber", "ClassCode" };

    private static CIteration? FindIteration(Dictionary<string, CObject> objs, string number, string? version, string? iteration)
    {
        if (!objs.TryGetValue(number, out var o)) return null;
        var rev = version is null ? o.Revisions.LastOrDefault() : o.Revisions.FirstOrDefault(r => r.Label == version);
        if (rev is null) return null;
        if (iteration is not null && int.TryParse(iteration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return rev.Iterations.FirstOrDefault(i => i.Sequence == n);
        return rev.Iterations.LastOrDefault();
    }

    private static Dictionary<string, CObject> BuildObjects(List<WRow> rows, HashSet<string> core, bool isDoc, StagingSink sink)
    {
        var result = new Dictionary<string, CObject>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var number = r.Get("Number");
            var kind = isDoc ? "document" : "item";
            if (number is null) { sink.Report(Severity.Error, "SRC-MISSING-FIELD", kind, r.Get("ID") ?? "?", "Row without Number."); continue; }
            var version = r.Get("Version") ?? "";
            if (!result.TryGetValue(number, out var o))
            {
                o = new CObject
                {
                    SourceId = number, Number = number, Name = r.Get("Name") ?? "", Type = r.Get(isDoc ? "DocumentType" : "PartType", "Type") ?? (isDoc ? "Document" : "Part"),
                    Unit = isDoc ? null : r.Get("DefaultUnit", "Unit"), Source = isDoc ? null : r.Get("Source"), Description = r.Get("Description"),
                    CreatedAt = Date(r.Get("CreatedOn")), CreatedBy = r.Get("CreatedBy"),
                };
                result[number] = o;
            }
            else
            {
                var created = Date(r.Get("CreatedOn"));
                if (created is not null && (o.CreatedAt is null || created < o.CreatedAt)) { o.CreatedAt = created; o.CreatedBy = r.Get("CreatedBy") ?? o.CreatedBy; }
                // rows of one master must agree on identity attributes; the first row wins and a disagreement is reported.
                if ((r.Get("Name") ?? "") != o.Name && r.Get("Name") is not null)
                    sink.Report(Severity.Warning, "SRC-NAME-DRIFT", kind, number, $"Name differs between iterations ('{o.Name}' vs '{r.Get("Name")}'); the first is used and the other is kept as a property.");
            }
            var rev = o.Revisions.FirstOrDefault(x => x.Label == version);
            if (rev is null)
            {
                rev = new CRevision { SourceId = $"{number}/{version}", Label = version, CreatedAt = Date(r.Get("CreatedOn")), CreatedBy = r.Get("CreatedBy") };
                o.Revisions.Add(rev);
            }
            var seq = int.TryParse(r.Get("Iteration"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : rev.Iterations.Count + 1;
            var it = new CIteration
            {
                Sequence = seq, CreatedAt = Date(r.Get("LastModified", "CreatedOn")), CreatedBy = r.Get("ModifiedBy", "CreatedBy"), Comment = r.Get("Comment"), Description = r.Get("Description") ?? "",
            };
            foreach (var kv in r.Where(k => !core.Contains(k.Key))) it.Properties[kv.Key] = kv.Value;
            if (r.Get("Name") is { } nm && nm != o.Name) it.Properties["Name"] = nm;
            rev.Iterations.Add(it);
            // The state is per iteration in Windchill; the revision's current state is the one of its highest iteration.
            var state = r.Get("State");
            if (state is not null && rev.Iterations.Max(i => i.Sequence) == seq)
            {
                rev.States.Clear();
                rev.States.Add(new CState { Name = state, At = Date(r.Get("StateChangedOn", "LastModified")), By = r.Get("StateChangedBy", "ModifiedBy") });
            }
        }
        foreach (var o in result.Values) foreach (var rev in o.Revisions) rev.Iterations.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        return result;
    }
}

/// <summary>Reads the Windchill OData-shaped JSON extract (parts.json, part-uses.json, documents.json, ...).</summary>
public sealed class WindchillJsonReader : IPackageReader
{
    public string Format => "windchill-json";
    public string DefaultSourceSystem => "windchill";

    public async Task ReadAsync(PackageSource package, StagingSink sink, CancellationToken ct)
    {
        sink.Inventory.Format = Format;
        var t = new WindchillTables();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "parts.json", "part-uses.json", "documents.json", "part-doc-links.json", "change-notices.json", "change-requests.json", "problem-reports.json", "users.json", "classifications.json", ClassificationHierarchyReader.FileName };
        foreach (var f in package.ListFiles().Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !f.Contains('/')))
            if (!known.Contains(f)) sink.Unknown("file:" + f, f, preserved: false);

        async Task Load(string file, Action<WRow> add, Action<WRow, JsonElement>? nested = null)
        {
            if (!package.Exists(file)) return;
            try
            {
                await using var s = package.OpenRead(file);
                using var doc = await JsonDocument.ParseAsync(s, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }, ct);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in root.EnumerateObject().Where(p => p.Name is not ("value" or "@odata.context" or "@odata.count" or "@odata.nextLink")))
                        sink.Unknown("json:" + p.Name, file, preserved: false);
                    if (root.TryGetProperty("@odata.nextLink", out _))
                        sink.Report(Severity.Warning, "SRC-PAGED", "package", file, "The file has @odata.nextLink: the extract is paged and only this page is read.", "Export all pages into the file (or several files concatenated) before importing.");
                    root = root.TryGetProperty("value", out var v) ? v : root;
                }
                if (root.ValueKind != JsonValueKind.Array)
                {
                    sink.Report(Severity.Error, "SRC-PARSE", "package", file, "Expected an OData response with a value array, or a plain array.");
                    return;
                }
                foreach (var el in root.EnumerateArray())
                {
                    var row = new WRow();
                    foreach (var p in el.EnumerateObject())
                    {
                        if (nested is not null && p.Value.ValueKind == JsonValueKind.Array && (p.Name is "Files" or "AffectedItems")) continue;
                        row[p.Name] = Scalar(p.Value);
                    }
                    add(row);
                    nested?.Invoke(row, el);
                }
            }
            catch (JsonException e)
            {
                sink.Report(Severity.Error, "SRC-PARSE", "package", file, $"JSON error: {e.Message}");
            }
        }

        await Load("parts.json", t.Parts.Add);
        await Load("part-uses.json", t.PartUses.Add);
        await Load("documents.json", t.Documents.Add, (row, el) =>
        {
            if (!el.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Array) return;
            foreach (var f in files.EnumerateArray())
            {
                var fr = new WRow { ["DocumentNumber"] = row.Get("Number"), ["DocumentVersion"] = row.Get("Version"), ["DocumentIteration"] = row.Get("Iteration") };
                foreach (var p in f.EnumerateObject()) fr[p.Name] = Scalar(p.Value);
                t.DocumentFiles.Add(fr);
            }
        });
        await Load("part-doc-links.json", t.PartDocLinks.Add);
        foreach (var (file, type) in new[] { ("change-notices.json", "ECN"), ("change-requests.json", "ECR"), ("problem-reports.json", "PR") })
            await Load(file, r => t.Changes.Add((type, r)), (row, el) =>
            {
                if (!el.TryGetProperty("AffectedItems", out var ai) || ai.ValueKind != JsonValueKind.Array) return;
                foreach (var a in ai.EnumerateArray())
                {
                    var ar = new WRow { ["ChangeNumber"] = row.Get("Number") };
                    foreach (var p in a.EnumerateObject()) ar[p.Name] = Scalar(p.Value);
                    t.ChangeAffected.Add(ar);
                }
            });
        await Load("users.json", t.Users.Add);
        await Load("classifications.json", r => t.Classifications.Add(r));
        WindchillBuilder.Build(t, sink);
    }

    /// <summary>Scalars as text; {"Value": x, "Display": y} (OData enumeration shape) as x; other objects and arrays as compact JSON.</summary>
    private static string? Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Object when e.TryGetProperty("Value", out var v) && v.ValueKind != JsonValueKind.Object => Scalar(v),
        JsonValueKind.Object when e.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String => n.GetString(),
        _ => e.GetRawText(),
    };
}

/// <summary>Reads the Windchill loader-style CSV extract (parts.csv, part_uses.csv, documents.csv, ...). RFC 4180: quotes, doubled quotes, newlines in quoted fields.</summary>
public sealed class WindchillCsvReader : IPackageReader
{
    public string Format => "windchill-csv";
    public string DefaultSourceSystem => "windchill";

    public async Task ReadAsync(PackageSource package, StagingSink sink, CancellationToken ct)
    {
        sink.Inventory.Format = Format;
        var t = new WindchillTables();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "parts.csv", "part_uses.csv", "documents.csv", "document_files.csv", "part_doc_links.csv", "change_notices.csv", "change_requests.csv", "problem_reports.csv", "change_affected.csv", "users.csv", "classifications.csv" };
        foreach (var f in package.ListFiles().Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) && !f.Contains('/')))
            if (!known.Contains(f)) sink.Unknown("file:" + f, f, preserved: false);

        async Task Load(string file, Action<WRow> add)
        {
            if (!package.Exists(file)) return;
            await using var s = package.OpenRead(file);
            using var reader = new StreamReader(s, Encoding.UTF8, true);
            string[]? header = null;
            var line = 0;
            foreach (var rec in await ParseAsync(reader, ct))
            {
                line++;
                if (header is null) { header = rec.Select(h => h.Trim().TrimStart('﻿')).ToArray(); continue; }
                if (rec.Count == 1 && string.IsNullOrWhiteSpace(rec[0])) continue;
                if (rec.Count != header.Length)
                {
                    sink.Report(Severity.Error, "SRC-PARSE", "package", $"{file}:{line}", $"Record has {rec.Count} fields, the header has {header.Length}.", "Quote fields that contain commas or line breaks.");
                    continue;
                }
                var row = new WRow();
                for (var i = 0; i < header.Length; i++) row[header[i]] = rec[i].Length == 0 ? null : rec[i];
                add(row);
            }
        }

        await Load("parts.csv", t.Parts.Add);
        await Load("part_uses.csv", t.PartUses.Add);
        await Load("documents.csv", t.Documents.Add);
        await Load("document_files.csv", t.DocumentFiles.Add);
        await Load("part_doc_links.csv", t.PartDocLinks.Add);
        foreach (var (file, type) in new[] { ("change_notices.csv", "ECN"), ("change_requests.csv", "ECR"), ("problem_reports.csv", "PR") })
            await Load(file, r => t.Changes.Add((type, r)));
        await Load("change_affected.csv", t.ChangeAffected.Add);
        await Load("users.csv", t.Users.Add);
        await Load("classifications.csv", t.Classifications.Add);
        WindchillBuilder.Build(t, sink);
    }

    internal static async Task<List<List<string>>> ParseAsync(TextReader reader, CancellationToken ct)
    {
        var records = new List<List<string>>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var any = false;
        var buffer = new char[8192];
        int n;
        char prev = '\0';
        while ((n = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                var c = buffer[i];
                if (inQuotes)
                {
                    if (c == '"') inQuotes = false;
                    else sb.Append(c);
                    prev = c;
                    continue;
                }
                switch (c)
                {
                    case '"':
                        if (prev == '"' && any) sb.Append('"'); // doubled quote inside a quoted field
                        inQuotes = true; any = true;
                        break;
                    case ',': fields.Add(sb.ToString()); sb.Clear(); any = true; break;
                    case '\r': break;
                    case '\n':
                        fields.Add(sb.ToString()); sb.Clear();
                        records.Add(fields); fields = []; any = false;
                        break;
                    default: sb.Append(c); any = true; break;
                }
                prev = c;
            }
        }
        if (any || sb.Length > 0 || fields.Count > 0) { fields.Add(sb.ToString()); records.Add(fields); }
        return records;
    }
}
