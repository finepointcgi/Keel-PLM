using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Keel.Migration.Canonical;

namespace Keel.Migration.Sources;

/// <summary>
/// Streaming reader for the documented SUBSET of PLM XML described in docs/migration/plmxml-subset.md. The file is read with an
/// <see cref="XmlReader"/>; only one top-level element at a time is materialized, so memory depends on the extracted data, not on
/// the file size. Unknown elements inside a known element are preserved as properties and reported; unknown top-level elements are
/// counted and reported; known-but-not-migrated elements (geometry) are counted as ignored. Nothing disappears silently.
/// </summary>
public sealed class PlmXmlReader : IPackageReader
{
    public string Format => "plmxml";
    public string DefaultSourceSystem => "teamcenter";

    private const int MaxPreservedXmlChars = 16 * 1024;
    private static readonly HashSet<string> IgnoredTopLevel = new(StringComparer.Ordinal) { "Header", "Transform", "Bound", "ConfigurationContext", "Representation", "View", "Site" };
    // Children of the elements we interpret that are consumed explicitly (everything else is preserved as xml:* properties).
    private static readonly HashSet<string> ProductChildren = new(StringComparer.Ordinal) { "Description", "UserData" };
    private static readonly HashSet<string> RevisionChildren = new(StringComparer.Ordinal) { "Description", "UserData", "AssociatedDataSet", "AssociatedForm", "ApplicationRef" };
    private static readonly string[] ChangeSubTypes = ["ChangeNotice", "ChangeRequest", "ProblemReport", "ECN", "ECR"];

    private sealed class Prod
    {
        public string Id = "", Number = "", Name = "", SubType = "";
        public string? Description;
        public Dictionary<string, string?> Props = [];
        public DateTimeOffset? CreatedAt; public string? CreatedBy; public string? Unit, Source;
        public List<Rev> Revs = [];
        public int Order;
    }

    private sealed class Rev
    {
        public string Id = "", MasterRef = "", Label = "", File = "";
        public int Seq;
        public int Order;
        public string? Name, Description, SubType;
        public Dictionary<string, string?> Props = [];
        public DateTimeOffset? CreatedAt; public string? CreatedBy, Comment;
        public List<CState> States = [];
        public List<string> DataSetRefs = [], FormRefs = [], ViewRefs = [];
        public List<Dictionary<string, string?>> ClassData = [], AffectedData = [], LinkData = [];
        public Dictionary<string, string?> Extra = [];
    }

    private sealed class View { public string Id = "", RevisionRef = ""; public List<string> InstanceRefs = []; }
    private sealed class Inst { public string Id = "", PartRef = ""; public decimal Qty = 1; public int? Find; public string? Unit, Notes, Effectivity; public List<string> Designators = []; public Dictionary<string, string?> Extra = []; }
    private sealed class DataSet { public string Id = "", Name = "", Type = ""; public List<CFile> Files = []; public Dictionary<string, string?> Props = []; }

    private readonly Dictionary<string, Prod> _products = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Rev> _revs = new(StringComparer.Ordinal);
    private readonly List<Rev> _revOrder = [];
    private readonly Dictionary<string, View> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Inst> _insts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DataSet> _datasets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string?>> _forms = new(StringComparer.Ordinal);
    private int _order;
    private Dictionary<string, Prod> _productByKey = [];

    public async Task ReadAsync(PackageSource package, StagingSink sink, CancellationToken ct)
    {
        sink.Inventory.Format = Format;
        sink.Package.SourceSystem = DefaultSourceSystem;
        sink.Inventory.SourceSystem = DefaultSourceSystem;
        var files = package.ListFiles().Where(f => f.EndsWith(".plmxml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0)
        {
            sink.Report(Severity.Error, "SRC-NO-INPUT", "package", package.Location, "No .plmxml or .xml file found in the package.");
            return;
        }
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var stream = package.OpenRead(file);
                ReadFile(stream, file, sink, ct);
            }
            catch (XmlException e)
            {
                sink.Report(Severity.Error, "SRC-PARSE", "package", file, $"XML error at line {e.LineNumber}: {e.Message}", "The file is not well-formed XML; re-export it or repair it.");
            }
        }
        Resolve(sink, package);
    }

    private void ReadFile(Stream stream, string file, StagingSink sink, CancellationToken ct)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true, MaxCharactersFromEntities = 0 };
        using var xr = XmlReader.Create(stream, settings);
        xr.MoveToContent();
        if (xr.LocalName != "PLMXML")
        {
            sink.Report(Severity.Error, "SRC-PARSE", "package", file, $"Root element is '{xr.LocalName}', expected PLMXML.");
            return;
        }
        if (DateTimeOffset.TryParse((xr.GetAttribute("date") + " " + xr.GetAttribute("time")).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exported))
            sink.Package.ExportedAt ??= exported;
        var author = xr.GetAttribute("author");
        if (!string.IsNullOrEmpty(author)) sink.Package.SourceInstance ??= author;

        if (xr.IsEmptyElement) return;
        xr.Read();
        while (xr.NodeType != XmlNodeType.EndElement && !xr.EOF)
        {
            ct.ThrowIfCancellationRequested();
            if (xr.NodeType != XmlNodeType.Element) { xr.Read(); continue; }
            var name = xr.LocalName;
            switch (name)
            {
                case "Product": case "ProductRevision": case "ProductRevisionView": case "ProductInstance":
                case "DataSet": case "Form": case "Person": case "Role":
                    Handle((XElement)XNode.ReadFrom(xr), name, file, sink);
                    break;
                default:
                    if (IgnoredTopLevel.Contains(name)) sink.Ignored(name);
                    else sink.Unknown(name, "/PLMXML/" + name, preserved: false);
                    xr.Skip();
                    break;
            }
        }
    }

    private static string Local(XElement e) => e.Name.LocalName;
    private static string? A(XElement e, string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;
    private static string StripRef(string? r) => (r ?? "").Trim().TrimStart('#');
    private static List<string> Refs(string? r) => (r ?? "").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(StripRef).ToList();
    private static DateTimeOffset? Date(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private void Handle(XElement e, string name, string file, StagingSink sink)
    {
        switch (name)
        {
            case "Product": ReadProduct(e, file, sink); break;
            case "ProductRevision": ReadRevision(e, file, sink); break;
            case "ProductRevisionView":
            {
                var v = new View { Id = A(e, "id") ?? "", RevisionRef = StripRef(A(e, "revisionRef")), InstanceRefs = Refs(A(e, "instanceRefs")) };
                if (v.Id.Length == 0) { sink.Report(Severity.Error, "SRC-NO-ID", "view", file, "ProductRevisionView without id."); break; }
                _views[v.Id] = v;
                foreach (var c in e.Elements()) NoteUnknownChild(c, "ProductRevisionView", sink, ["UserData"]);
                break;
            }
            case "ProductInstance": ReadInstance(e, file, sink); break;
            case "DataSet": ReadDataSet(e, file, sink); break;
            case "Form":
            {
                var id = A(e, "id");
                if (id is null) break;
                var props = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var ud in e.Elements().Where(c => Local(c) == "UserData")) UserData(ud, props, null);
                _forms[id] = props.ToDictionary(p => "form:" + (A(e, "name") ?? id) + "." + p.Key, p => p.Value);
                foreach (var c in e.Elements()) NoteUnknownChild(c, "Form", sink, ["UserData"]);
                break;
            }
            case "Person":
            {
                var u = new CUser { Name = A(e, "userName") ?? A(e, "name") ?? "", DisplayName = A(e, "name") };
                foreach (var r in e.Elements().Where(c => Local(c) == "Role")) u.Groups.Add(A(r, "name") ?? r.Value.Trim());
                if (A(e, "role") is { Length: > 0 } role) u.Groups.Add(role);
                if (u.Name.Length > 0) sink.Add(u); else sink.Report(Severity.Warning, "SRC-PERSON", "user", A(e, "id") ?? file, "Person without userName/name skipped.");
                break;
            }
            case "Role": sink.Ignored("Role"); break;
        }
    }

    private static void NoteUnknownChild(XElement c, string owner, StagingSink sink, string[] known)
    {
        if (!known.Contains(Local(c), StringComparer.Ordinal)) sink.Unknown(Local(c), "/PLMXML/" + owner + "/" + Local(c), preserved: false);
    }

    /// <summary>UserData / UserValue pairs. Titles listed in <paramref name="consumed"/> are returned through the dictionary under their own name.</summary>
    private static void UserData(XElement ud, Dictionary<string, string?> into, string? prefixOverride)
    {
        var type = prefixOverride ?? A(ud, "type");
        var plain = string.IsNullOrEmpty(type) || type is "Attributes" or "Properties";
        foreach (var uv in ud.Elements().Where(c => Local(c) == "UserValue"))
        {
            var title = A(uv, "title") ?? A(uv, "name");
            if (string.IsNullOrEmpty(title)) continue;
            var key = plain ? title : type + "." + title;
            var raw = A(uv, "value") ?? uv.Value;
            into[key] = string.IsNullOrEmpty(raw) ? null : raw; // an empty value is an absent value
        }
    }

    private void Preserve(XElement parent, HashSet<string> known, string ownerPath, Dictionary<string, string?> into, StagingSink sink)
    {
        var n = 0;
        foreach (var c in parent.Elements().Where(c => !known.Contains(Local(c))))
        {
            var xml = c.ToString(SaveOptions.DisableFormatting);
            var truncated = xml.Length > MaxPreservedXmlChars;
            into[$"xml:{Local(c)}[{n++}]"] = truncated ? xml[..MaxPreservedXmlChars] + "...[truncated]" : xml;
            sink.Unknown(Local(c), ownerPath + "/" + Local(c), preserved: true);
            if (truncated) sink.Report(Severity.Warning, "SRC-XML-TRUNCATED", "element", A(parent, "id") ?? "?", $"Unknown element {Local(c)} exceeds {MaxPreservedXmlChars} characters; the preserved copy is truncated.");
        }
    }

    private void ReadProduct(XElement e, string file, StagingSink sink)
    {
        var id = A(e, "id");
        if (string.IsNullOrEmpty(id)) { sink.Report(Severity.Error, "SRC-NO-ID", "item", file, "Product without id."); return; }
        var p = new Prod { Id = id, Number = A(e, "productId") ?? "", Name = A(e, "name") ?? "", SubType = A(e, "subType") ?? "", Order = _order++ };
        p.Description = e.Elements().FirstOrDefault(c => Local(c) == "Description")?.Value;
        foreach (var ud in e.Elements().Where(c => Local(c) == "UserData")) UserData(ud, p.Props, null);
        p.CreatedAt = Date(Take(p.Props, "creation_date"));
        p.CreatedBy = Take(p.Props, "owning_user");
        p.Unit = Take(p.Props, "uom") ?? Take(p.Props, "unit");
        p.Source = Take(p.Props, "make_buy");
        Preserve(e, ProductChildren, "/PLMXML/Product", p.Props, sink);
        _products[id] = p;
    }

    private static string? Take(Dictionary<string, string?> d, string key)
    {
        if (!d.Remove(key, out var v)) return null;
        return v;
    }

    private void ReadRevision(XElement e, string file, StagingSink sink)
    {
        var id = A(e, "id");
        if (string.IsNullOrEmpty(id)) { sink.Report(Severity.Error, "SRC-NO-ID", "revision", file, "ProductRevision without id."); return; }
        var r = new Rev { Id = id, File = file, MasterRef = StripRef(A(e, "masterRef")), Label = A(e, "revision") ?? "", Name = A(e, "name"), SubType = A(e, "subType"), Order = _order++ };
        r.Description = e.Elements().FirstOrDefault(c => Local(c) == "Description")?.Value;
        foreach (var ud in e.Elements().Where(c => Local(c) == "UserData"))
        {
            var type = A(ud, "type");
            var vals = new Dictionary<string, string?>(StringComparer.Ordinal);
            UserData(ud, vals, "");
            switch (type)
            {
                case "ReleaseStatus":
                    r.States.Add(new CState { Name = vals.GetValueOrDefault("name") ?? "", At = Date(vals.GetValueOrDefault("date_released")), By = vals.GetValueOrDefault("user") });
                    break;
                case "Classification": r.ClassData.Add(vals); break;
                case "ChangeAffected": r.AffectedData.Add(vals); break;
                case "DocumentLink": r.LinkData.Add(vals); break;
                default: UserData(ud, r.Props, null); break;
            }
        }
        r.CreatedAt = Date(Take(r.Props, "creation_date"));
        r.CreatedBy = Take(r.Props, "owning_user");
        r.Comment = Take(r.Props, "comment");
        r.Seq = int.TryParse(Take(r.Props, "sequence_id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq) ? seq : 0;
        r.DataSetRefs = e.Elements().Where(c => Local(c) == "AssociatedDataSet").Select(c => StripRef(A(c, "dataSetRef"))).ToList();
        r.FormRefs = e.Elements().Where(c => Local(c) == "AssociatedForm").Select(c => StripRef(A(c, "formRef"))).ToList();
        r.ViewRefs = Refs(A(e, "viewRefs"));
        foreach (var ar in e.Elements().Where(c => Local(c) == "ApplicationRef"))
            r.Props[$"appref:{A(ar, "application")}"] = A(ar, "label");
        Preserve(e, RevisionChildren, "/PLMXML/ProductRevision", r.Extra, sink);
        foreach (var kv in r.Extra) r.Props[kv.Key] = kv.Value;
        _revs[id] = r;
        _revOrder.Add(r);
    }

    private void ReadInstance(XElement e, string file, StagingSink sink)
    {
        var id = A(e, "id");
        if (string.IsNullOrEmpty(id)) { sink.Report(Severity.Error, "SRC-NO-ID", "usage", file, "ProductInstance without id."); return; }
        var i = new Inst { Id = id, PartRef = StripRef(A(e, "partRef")) };
        if (A(e, "quantity") is { } q)
        {
            if (decimal.TryParse(q, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) i.Qty = d;
            else sink.Report(Severity.Error, "SRC-BAD-NUMBER", "usage", id, $"Quantity '{q}' is not a number.");
        }
        foreach (var ud in e.Elements().Where(c => Local(c) == "UserData"))
        {
            var vals = new Dictionary<string, string?>(StringComparer.Ordinal);
            UserData(ud, vals, "");
            foreach (var kv in vals)
            {
                switch (kv.Key)
                {
                    case "sequence_no" or "find_number": if (int.TryParse(kv.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f)) i.Find = f; else sink.Report(Severity.Error, "SRC-BAD-NUMBER", "usage", id, $"Find number '{kv.Value}' is not an integer."); break;
                    case "unit" or "uom": i.Unit = kv.Value; break;
                    case "reference_designators": i.Designators.AddRange((kv.Value ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)); break;
                    case "notes": i.Notes = kv.Value; break;
                    case "effectivity": i.Effectivity = kv.Value; break;
                    default: i.Extra[kv.Key] = kv.Value; break;
                }
            }
        }
        foreach (var occ in e.Elements().Where(c => Local(c) == "Occurrence"))
            foreach (var ud in occ.Elements().Where(c => Local(c) == "UserData"))
            {
                var vals = new Dictionary<string, string?>(StringComparer.Ordinal);
                UserData(ud, vals, "");
                if (vals.TryGetValue("reference_designator", out var rd) && !string.IsNullOrWhiteSpace(rd)) i.Designators.Add(rd.Trim());
            }
        foreach (var c in e.Elements().Where(c => Local(c) is not ("UserData" or "Occurrence")))
        {
            if (Local(c) == "Transform") sink.Ignored("Transform");
            else
            {
                i.Extra[$"xml:{Local(c)}"] = c.ToString(SaveOptions.DisableFormatting);
                sink.Unknown(Local(c), "/PLMXML/ProductInstance/" + Local(c), preserved: true);
            }
        }
        _insts[id] = i;
    }

    private void ReadDataSet(XElement e, string file, StagingSink sink)
    {
        var id = A(e, "id");
        if (string.IsNullOrEmpty(id)) { sink.Report(Severity.Error, "SRC-NO-ID", "dataset", file, "DataSet without id."); return; }
        var ds = new DataSet { Id = id, Name = A(e, "name") ?? id, Type = A(e, "type") ?? "" };
        foreach (var ud in e.Elements().Where(c => Local(c) == "UserData")) UserData(ud, ds.Props, null);
        var sha = Take(ds.Props, "sha256");
        var media = Take(ds.Props, "media_type");
        long? size = long.TryParse(Take(ds.Props, "size_bytes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sz) ? sz : null;
        var n = 0;
        foreach (var ef in e.Elements().Where(c => Local(c) == "ExternalFile"))
        {
            var loc = A(ef, "locationRef");
            ds.Files.Add(new CFile
            {
                Name = Path.GetFileName((loc ?? A(ef, "id") ?? ds.Name).Replace('\\', '/')),
                PackagePath = PackageSource.Normalize(loc),
                MediaType = A(ef, "format") is { Length: > 0 } fmt && fmt.Contains('/') ? fmt : media,
                Sha256 = A(ef, "sha256")?.ToLowerInvariant() ?? (n == 0 ? sha?.ToLowerInvariant() : null),
                SizeBytes = long.TryParse(A(ef, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s2) ? s2 : (n == 0 ? size : null),
                Primary = n == 0,
            });
            if (loc is not null && PackageSource.Normalize(loc) is null)
                sink.Report(Severity.Error, "SRC-UNSAFE-PATH", "file", id, $"ExternalFile location '{loc}' leaves the package and is refused.");
            n++;
        }
        foreach (var c in e.Elements().Where(c => Local(c) is not ("UserData" or "ExternalFile")))
        {
            ds.Props[$"xml:{Local(c)}"] = c.ToString(SaveOptions.DisableFormatting);
            sink.Unknown(Local(c), "/PLMXML/DataSet/" + Local(c), preserved: true);
        }
        _datasets[id] = ds;
    }

    // ------------------------------------------------------------------ resolution (IDREFs are resolved after the whole file set was read)
    private void Resolve(StagingSink sink, PackageSource package)
    {
        // Items whose subType says document are documents; change subTypes are change objects; everything else is a part.
        var byProduct = _revOrder.GroupBy(r => r.MasterRef).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var r in _revOrder)
            if (!_products.ContainsKey(r.MasterRef))
                sink.Report(Severity.Error, "SRC-DANGLING-REF", "revision", r.Id, $"ProductRevision {r.Id} refers to Product '{r.MasterRef}', which is not in the package.", "Re-export with the master Product, or remove the revision.");

        // product id (the master reference) for any partRef (revision or product)
        string? MasterOf(string partRef)
        {
            if (_products.ContainsKey(partRef)) return partRef;
            if (_revs.TryGetValue(partRef, out var rv)) return rv.MasterRef;
            if (_views.TryGetValue(partRef, out var vw) && _revs.TryGetValue(vw.RevisionRef, out var rv2)) return rv2.MasterRef;
            return null;
        }

        var viewsByRevision = _views.Values.GroupBy(v => StripRef(v.RevisionRef)).ToDictionary(g => g.Key, g => g.Select(v => v.Id).ToList(), StringComparer.Ordinal);
        var productByKey = new Dictionary<string, Prod>(StringComparer.Ordinal);
        foreach (var pr in _products.Values.OrderBy(x => x.Order)) { productByKey.TryAdd(pr.Id, pr); productByKey.TryAdd(pr.Number, pr); }
        _productByKey = productByKey;
        var syntheticDocs = new List<CObject>();
        foreach (var p in _products.Values.OrderBy(p => p.Order))
        {
            byProduct.TryGetValue(p.Id, out var revs);
            revs ??= [];
            var isDoc = p.SubType.StartsWith("Document", StringComparison.OrdinalIgnoreCase);
            var isChange = ChangeSubTypes.Any(t => p.SubType.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (isChange) { AddChange(p, revs, sink, MasterOf); continue; }

            var obj = new CObject
            {
                SourceId = p.Id, Number = p.Number, Name = p.Name, Type = p.SubType.Length > 0 ? p.SubType : "Item",
                Unit = isDoc ? null : p.Unit, Source = isDoc ? null : p.Source, Description = p.Description, CreatedAt = p.CreatedAt, CreatedBy = p.CreatedBy,
                Properties = p.Props,
            };
            foreach (var group in revs.GroupBy(r => r.Label).Select(g => g.ToList()))
            {
                var first = group[0];
                var rev = new CRevision
                {
                    SourceId = $"{p.Id}/{first.Label}", Label = first.Label, CreatedAt = group.Min(g => g.CreatedAt), CreatedBy = first.CreatedBy,
                    Properties = new Dictionary<string, string?>(),
                };
                var states = group.SelectMany(g => g.States).ToList();
                rev.States = states.Select((s, i) => (s, i)).OrderBy(t => t.s.At ?? DateTimeOffset.MinValue).ThenBy(t => t.i).Select(t => t.s).ToList();
                var seqUsed = new HashSet<int>();
                var next = 1;
                foreach (var x in group.OrderBy(g => g.Seq == 0 ? int.MaxValue : g.Seq).ThenBy(g => g.Order))
                {
                    var seq = x.Seq > 0 ? x.Seq : next;
                    while (!seqUsed.Add(seq)) seq++;
                    next = seq + 1;
                    var it = new CIteration { Sequence = seq, CreatedAt = x.CreatedAt, CreatedBy = x.CreatedBy, Comment = x.Comment, Description = x.Description ?? "", Properties = new Dictionary<string, string?>(x.Props) };
                    if (x.Name is not null && x.Name != p.Name) it.Properties["revision_name"] = x.Name;
                    foreach (var f in x.FormRefs.Where(f => f.Length > 0))
                    {
                        if (!_forms.TryGetValue(f, out var fp)) { sink.Report(Severity.Error, "SRC-DANGLING-REF", "revision", x.Id, $"AssociatedForm refers to '{f}', which is not in the package."); continue; }
                        foreach (var kv in fp) it.Properties[kv.Key] = kv.Value;
                    }
                    // structure: views owned by this revision element
                    var viewIds = (viewsByRevision.TryGetValue(x.Id, out var owned) ? owned : []).Concat(x.ViewRefs).Distinct().ToList();
                    foreach (var vid in viewIds)
                    {
                        if (!_views.TryGetValue(vid, out var view)) { sink.Report(Severity.Error, "SRC-DANGLING-REF", "revision", x.Id, $"viewRefs refers to '{vid}', which is not in the package."); continue; }
                        foreach (var iref in view.InstanceRefs)
                        {
                            if (!_insts.TryGetValue(iref, out var inst)) { sink.Report(Severity.Error, "SRC-DANGLING-REF", "usage", $"{x.Id}->{iref}", $"ProductRevisionView {view.Id} refers to ProductInstance '{iref}', which is not in the package."); continue; }
                            var child = MasterOf(inst.PartRef);
                            if (child is null) { sink.Report(Severity.Error, "SRC-DANGLING-REF", "usage", inst.Id, $"ProductInstance {inst.Id} refers to '{inst.PartRef}', which is neither a Product nor a ProductRevision of the package.", "Re-export the child, or remove the instance."); child = inst.PartRef; }
                            var u = new CUsage { ChildSourceId = child, Quantity = inst.Qty, Unit = inst.Unit, FindNumber = inst.Find, ReferenceDesignators = inst.Designators, Notes = inst.Notes, Effectivity = inst.Effectivity };
                            foreach (var kv in inst.Extra) it.Properties[$"usage[{inst.Id}].{kv.Key}"] = kv.Value;
                            it.Usages.Add(u);
                        }
                    }
                    // datasets: documents attach their files to the iteration; items get a synthetic file document (decision SYNTHETIC-DOC)
                    var files = new List<CFile>();
                    foreach (var dref in x.DataSetRefs)
                    {
                        if (!_datasets.TryGetValue(dref, out var ds)) { sink.Report(Severity.Error, "SRC-DANGLING-REF", "revision", x.Id, $"AssociatedDataSet refers to '{dref}', which is not in the package."); continue; }
                        files.AddRange(ds.Files);
                        foreach (var kv in ds.Props) it.Properties[$"dataset[{ds.Name}].{kv.Key}"] = kv.Value;
                        if (ds.Type.Length > 0) it.Properties[$"dataset[{ds.Name}].type"] = ds.Type;
                    }
                    if (isDoc)
                    {
                        for (var fi = 0; fi < files.Count; fi++) files[fi].Primary = fi == 0; // documented rule: the first file of the first dataset is primary
                        it.Files = files.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                    }
                    else if (files.Count > 0)
                    {
                        var docNumber = $"{p.Number}-FILES";
                        var doc = syntheticDocs.FirstOrDefault(d => d.SourceId == $"{p.Id}#files") ?? new CObject
                        {
                            SourceId = $"{p.Id}#files", Number = docNumber, Name = $"{p.Name} files", Type = "Other", CreatedAt = p.CreatedAt, CreatedBy = p.CreatedBy,
                            Properties = new Dictionary<string, string?> { ["synthetic"] = "true", ["syntheticFor"] = p.Number, ["markings_from_item"] = "true" },
                        };
                        if (!syntheticDocs.Contains(doc)) syntheticDocs.Add(doc);
                        // copy the item's own properties so the marking rules see the same inputs for the synthetic document
                        foreach (var kv in p.Props) doc.Properties.TryAdd(kv.Key, kv.Value);
                        var drev = doc.Revisions.FirstOrDefault(r => r.Label == first.Label) ?? new CRevision
                        {
                            SourceId = $"{doc.SourceId}/{first.Label}", Label = first.Label, CreatedAt = rev.CreatedAt, CreatedBy = rev.CreatedBy,
                            States = rev.States.Select(s => new CState { Name = s.Name, At = s.At, By = s.By }).ToList(),
                        };
                        if (!doc.Revisions.Contains(drev)) doc.Revisions.Add(drev);
                        drev.Iterations.Add(new CIteration { Sequence = seq, CreatedAt = it.CreatedAt, CreatedBy = it.CreatedBy, Comment = "Files of " + p.Number, Description = "", Files = files.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList() });
                        it.DocLinks.Add(new CDocLink { Type = "describes", DocumentSourceId = doc.SourceId, DocumentRevisionLabel = first.Label });
                    }
                    foreach (var ld in x.LinkData)
                    {
                        var target = ld.GetValueOrDefault("document") ?? "";
                        var doc = productByKey.GetValueOrDefault(target);
                        if (doc is null) { sink.Report(Severity.Error, "SRC-DANGLING-REF", "link", x.Id, $"DocumentLink refers to document '{target}', which is not in the package."); continue; }
                        it.DocLinks.Add(new CDocLink { Type = ld.GetValueOrDefault("type") ?? "describes", DocumentSourceId = doc.Id, DocumentRevisionLabel = ld.GetValueOrDefault("revision") });
                    }
                    if (x.ClassData.Count > 0)
                        foreach (var cd in x.ClassData)
                        {
                            var attrs = cd.Where(k => k.Key != "class").ToDictionary(k => k.Key, k => k.Value);
                            sink.Add(new CClassAssignment { ItemSourceId = p.Id, ClassCode = cd.GetValueOrDefault("class") ?? "", Attributes = attrs });
                        }
                    rev.Iterations.Add(it);
                }
                if (rev.States.Count == 0)
                    sink.Report(Severity.Warning, "SRC-NO-STATE", "revision", rev.SourceId, $"Revision {first.Label} of {p.Number} has no ReleaseStatus; the mapping must define a default state.");
                obj.Revisions.Add(rev);
            }
            sink.Add(obj, isDoc);
        }
        foreach (var d in syntheticDocs) sink.Add(d, true);

        foreach (var ds in _datasets.Values) CheckFiles(ds, package, sink);
    }

    private static void CheckFiles(DataSet ds, PackageSource package, StagingSink sink)
    {
        foreach (var f in ds.Files)
            if (f.PackagePath is not null && !package.Exists(f.PackagePath))
                sink.Report(Severity.Error, "FILE-MISSING", "file", ds.Id, $"File '{f.PackagePath}' of dataset '{ds.Name}' is not in the package.", "Add the file to the package or re-export with files.");
    }

    private void AddChange(Prod p, List<Rev> revs, StagingSink sink, Func<string, string?> masterOf)
    {
        var rev = revs.Count > 0 ? revs[^1] : null;
        var c = new CChange
        {
            SourceId = p.Id, Number = p.Number, Title = p.Name, Description = p.Description, CreatedAt = p.CreatedAt, CreatedBy = p.CreatedBy,
            Type = p.SubType.Contains("Request", StringComparison.OrdinalIgnoreCase) || p.SubType.Contains("ECR", StringComparison.Ordinal) ? "ECR"
                 : p.SubType.Contains("Problem", StringComparison.OrdinalIgnoreCase) ? "PR" : "ECN",
            Properties = new Dictionary<string, string?>(p.Props),
        };
        if (rev is not null)
        {
            var last = rev.States.LastOrDefault();
            c.State = last?.Name ?? "";
            c.StateChangedAt = last?.At; c.StateChangedBy = last?.By;
            foreach (var kv in rev.Props) c.Properties[kv.Key] = kv.Value;
            foreach (var a in rev.AffectedData)
            {
                var item = a.GetValueOrDefault("item") ?? "";
                var prod = _productByKey.GetValueOrDefault(item);
                if (prod is null) { sink.Report(Severity.Error, "SRC-DANGLING-REF", "change", p.Id, $"Change {p.Number} lists affected item '{item}', which is not in the package."); continue; }
                c.Affected.Add(new CAffected { ItemSourceId = prod.Id, RevisionLabel = a.GetValueOrDefault("revision"), ResultingRevisionLabel = a.GetValueOrDefault("resulting_revision"), Action = a.GetValueOrDefault("action") ?? "none" });
            }
        }
        if (c.Properties.Remove("change_class", out var cls)) c.ChangeClass = cls;
        if (c.Properties.Remove("priority", out var pr)) c.Priority = pr;
        if (c.Properties.Remove("reason", out var rs)) c.Reason = rs;
        if (c.Properties.Remove("parent", out var parent)) c.ParentSourceId = (_productByKey.TryGetValue(parent!, out var pp) ? pp.Id : parent);
        _ = masterOf;
        sink.Add(c);
    }
}
