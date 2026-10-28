using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using Keel.Migration.Canonical;
using Keel.Migration.Mapping;

namespace Keel.Migration.Fixtures;

/// <summary>Defects that the generator can inject. Each maps to one validation code the importer must report.</summary>
[Flags]
public enum Defect
{
    None = 0,
    DanglingIdRef = 1,
    DuplicateNumber = 2,
    UnknownState = 4,
    BadUnit = 8,
    BomCycle = 16,
    MissingFile = 32,
    ShaMismatch = 64,
    MarkingsMissing = 128,
    MarkingsInvalid = 256,
    RevisionAmbiguous = 512,
    HugeAttribute = 1024,
    UnknownElement = 2048,
}

/// <summary>A logical sample product (canonical model) and the bytes of its files, from which every source format is written.</summary>
public sealed class SampleProduct
{
    public CanonicalPackage Package { get; init; } = new();
    public Dictionary<string, byte[]> Files { get; init; } = [];
}

/// <summary>
/// Deterministic generator of SYNTHETIC packages (nothing here comes from a real product or system). One logical product is written as a Keel
/// canonical package, a PLM XML package, a Windchill OData-JSON extract and a Windchill CSV extract, so tests can prove the readers agree.
/// </summary>
public static class Samples
{
    private static readonly DateTimeOffset Epoch = new(2020, 1, 15, 8, 0, 0, TimeSpan.Zero);
    public static DateTimeOffset Day(int n) => Epoch.AddDays(n);

    // ------------------------------------------------------------------------------------------------ logical product
    private static CUsage U(string child, decimal qty, int find, string unit = "each", params string[] designators) =>
        new() { ChildSourceId = child, Quantity = qty, FindNumber = find, Unit = unit, ReferenceDesignators = [.. designators] };

    private static CState S(string name, int day, string by = "jdoe") => new() { Name = name, At = Day(day), By = by };

    private static CIteration It(int seq, int day, string by, string comment, params CUsage[] usages) =>
        new() { Sequence = seq, CreatedAt = Day(day), CreatedBy = by, Comment = comment, Description = "Synthetic iteration " + seq, Usages = [.. usages] };

    private static CRevision Rev(string number, string label, int day, string state, int stateDay, params CIteration[] iterations) =>
        new() { SourceId = $"{number}/{label}", Label = label, CreatedAt = Day(day), CreatedBy = "jdoe", States = [S(state, stateDay)], Iterations = [.. iterations] };

    private static CObject Part(string number, string name, string type, string unit, string itar, string? project, params CRevision[] revisions)
    {
        var o = new CObject
        {
            SourceId = number, Number = number, Name = name, Type = type, Unit = unit, Source = "Make", Description = "Synthetic " + name, CreatedAt = Day(0), CreatedBy = "jdoe",
            Properties = new Dictionary<string, string?> { ["Material"] = "Aluminum 6061", ["Mass"] = "1.25" },
            Revisions = [.. revisions],
        };
        if (itar is not null) o.Properties["ITAR"] = itar;
        if (project is not null) o.Properties["Project"] = project;
        return o;
    }

    public static SampleProduct Build(Defect defects = Defect.None, string sourceSystem = "canonical", string salt = "")
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        CFile File(string name, string media, int size, bool primary, string folder = "")
        {
            var path = "files/" + folder + name;
            var bytes = new byte[size];
            var seed = Encoding.ASCII.GetBytes("SYNTHETIC FILE " + salt + name + " ");
            for (var i = 0; i < size; i++) bytes[i] = seed[i % seed.Length];
            files[path] = bytes;
            return new CFile { Name = name, MediaType = media, PackagePath = path, SizeBytes = size, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Primary = primary };
        }

        var pkg = new CanonicalPackage { SourceSystem = sourceSystem, SourceInstance = "synthetic", ExportedAt = Day(900) };

        var top = Part("A-1000", "Synthetic top assembly", "Assembly", "each", "Y", "ORCA",
            Rev("A-1000", "A", 1, "Released", 20, It(1, 1, "jdoe", "initial", U("A-1100", 1, 10), U("A-1200", 2, 20), U("P-2001", 4, 30))),
            Rev("A-1000", "B", 40, "InWork", 40,
                It(1, 40, "jdoe", "revise", U("A-1100", 1, 10), U("A-1200", 2, 20), U("P-2001", 4, 30)),
                It(2, 41, "asmith", "add bracket", U("A-1100", 1, 10), U("A-1200", 2, 20), U("P-2001", 4, 30), U("P-2002", 1, 40))));
        var sub1 = Part("A-1100", "Synthetic sub assembly", "Assembly", "each", "Y", "ORCA",
            Rev("A-1100", "A", 2, "Released", 20, It(1, 2, "jdoe", "initial", U("A-1110", 1, 10), U("P-2003", 2, 20))));
        var sub2 = Part("A-1200", "Synthetic sub assembly two", "Assembly", "each", "N", null,
            Rev("A-1200", "A", 3, "Released", 21, It(1, 3, "asmith", "initial", U("P-2004", 1, 10), U("P-3001", 2.5m, 20, "kg"))));
        var mech = Part("A-1110", "Synthetic mechanism", "Assembly", "each", "Y", "ORCA",
            Rev("A-1110", "A", 4, "Released", 22, It(1, 4, "jdoe", "initial",
                U("P-2005", 8, 10, "each", "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8"), U("P-2006", 1, 20), U("P-2007", 250, 30, "mm"))));
        var p1 = Part("P-2001", "Synthetic bracket", "Part", "each", "Y", "ORCA",
            Rev("P-2001", "A", 5, "Released", 20, It(1, 5, "jdoe", "initial"), It(2, 6, "jdoe", "fix hole")),
            Rev("P-2001", "B", 50, "Released", 60, It(1, 50, "asmith", "rev B")));
        var p2 = Part("P-2002", "Synthetic plate", "Part", "each", "Y", "ORCA",
            Rev("P-2002", "01", 7, "Released", 20, It(1, 7, "jdoe", "initial")),
            Rev("P-2002", "02", 70, "InWork", 70, It(1, 70, "jdoe", "second")));
        var p3 = Part("P-2003", "Synthetic shaft", "Part", "each", "Y", "ORCA", Rev("P-2003", "A", 8, "Released", 20, It(1, 8, "jdoe", "initial")));
        var p4 = Part("P-2004", "Synthetic cover", "Part", "each", "N", null, Rev("P-2004", "A", 9, "Released", 20, It(1, 9, "jdoe", "initial")));
        var p5 = Part("P-2005", "Synthetic resistor", "Part", "each", "N", null, Rev("P-2005", "A", 10, "Released", 20, It(1, 10, "jdoe", "initial")));
        var p6 = Part("P-2006", "Synthetic housing", "Part", "each", "Y", "ORCA", Rev("P-2006", "A", 11, "Released", 20, It(1, 11, "jdoe", "initial")));
        var p7 = Part("P-2007", "Synthetic cable", "Part", "each", "N", null, Rev("P-2007", "A", 12, "Released", 20, It(1, 12, "jdoe", "initial")));
        var raw = Part("P-3001", "Synthetic sheet stock", "Material", "kg", "N", null, Rev("P-3001", "A", 13, "Released", 20, It(1, 13, "jdoe", "initial")));
        var p8 = Part("P-2008", "Synthetic standalone part", "Part", "each", "N", null, Rev("P-2008", "A", 14, "Obsolete", 80, It(1, 14, "jdoe", "initial")));
        pkg.Items.AddRange([top, sub1, sub2, mech, p1, p2, p3, p4, p5, p6, p7, raw, p8]);

        // documents: a specification (one file) and a drawing (revisions A and B, files per iteration)
        var spec = new CObject
        {
            SourceId = "D-9001", Number = "D-9001", Name = "Synthetic specification", Type = "Specification", CreatedAt = Day(1), CreatedBy = "asmith",
            Properties = new Dictionary<string, string?> { ["ITAR"] = "Y", ["Project"] = "ORCA" },
            Revisions = [new CRevision { SourceId = "D-9001/A", Label = "A", CreatedAt = Day(1), CreatedBy = "asmith", States = [S("Released", 25, "asmith")],
                Iterations = [new CIteration { Sequence = 1, CreatedAt = Day(1), CreatedBy = "asmith", Comment = "initial", Description = "Spec", Files = [File("spec.pdf", "application/pdf", 3000, true)] }] }],
        };
        var drawing = new CObject
        {
            SourceId = "D-9002", Number = "D-9002", Name = "Synthetic drawing", Type = "Drawing", CreatedAt = Day(2), CreatedBy = "jdoe",
            Properties = new Dictionary<string, string?> { ["ITAR"] = "Y", ["Project"] = "ORCA" },
            Revisions =
            [
                new CRevision { SourceId = "D-9002/A", Label = "A", CreatedAt = Day(2), CreatedBy = "jdoe", States = [S("Released", 26)],
                    Iterations = [new CIteration { Sequence = 1, CreatedAt = Day(2), CreatedBy = "jdoe", Description = "Drawing", Files = [File("drawing-a.pdf", "application/pdf", 5000, true)] }] },
                new CRevision { SourceId = "D-9002/B", Label = "B", CreatedAt = Day(45), CreatedBy = "jdoe", States = [S("InWork", 45)],
                    Iterations =
                    [
                        new CIteration { Sequence = 1, CreatedAt = Day(45), CreatedBy = "jdoe", Description = "Drawing B", Files = [File("drawing-b.pdf", "application/pdf", 5200, true, "it1/")] },
                        new CIteration { Sequence = 2, CreatedAt = Day(46), CreatedBy = "jdoe", Description = "Drawing B", Files = [File("drawing-b.pdf", "application/pdf", 5400, true, "it2/"), File("notes.txt", "text/plain", 300, false, "it2/")] },
                    ] },
            ],
        };
        pkg.Documents.AddRange([spec, drawing]);
        top.Revisions[0].Iterations[0].DocLinks.AddRange([new CDocLink { Type = "describes", DocumentSourceId = "D-9002", DocumentRevisionLabel = "A" }, new CDocLink { Type = "references", DocumentSourceId = "D-9001" }]);
        top.Revisions[1].Iterations[1].DocLinks.Add(new CDocLink { Type = "describes", DocumentSourceId = "D-9002", DocumentRevisionLabel = "B" });

        // change history: PR -> ECR (class I, implemented) -> ECN (class I, implemented)
        var changeProps = new Dictionary<string, string?> { ["ITAR"] = "Y", ["Project"] = "ORCA" };
        pkg.Changes.AddRange(
        [
            new CChange { SourceId = "PR-2021-001", Number = "PR-2021-001", Type = "PR", Title = "Bracket cracks", Description = "Synthetic problem", State = "Closed", Priority = "High", CreatedAt = Day(30), CreatedBy = "asmith", StateChangedAt = Day(35), StateChangedBy = "jdoe", Properties = new(changeProps) },
            new CChange { SourceId = "ECR-2021-003", Number = "ECR-2021-003", Type = "ECR", Title = "Strengthen bracket", Description = "Synthetic request", State = "Implemented", ChangeClass = "I", Priority = "High", Reason = "Cracking", CreatedAt = Day(36), CreatedBy = "jdoe", StateChangedAt = Day(58), StateChangedBy = "jdoe", ParentSourceId = "PR-2021-001", Properties = new(changeProps) },
            new CChange
            {
                SourceId = "ECN-2021-007", Number = "ECN-2021-007", Type = "ECN", Title = "Release bracket rev B", Description = "Synthetic notice", State = "Implemented", ChangeClass = "I", Priority = "High", Reason = "Cracking",
                CreatedAt = Day(40), CreatedBy = "jdoe", StateChangedAt = Day(60), StateChangedBy = "asmith", ParentSourceId = "ECR-2021-003", Properties = new(changeProps),
                Affected = [new CAffected { ItemSourceId = "P-2001", RevisionLabel = "A", ResultingRevisionLabel = "B", Action = "revise" }, new CAffected { ItemSourceId = "A-1000", RevisionLabel = "A", Action = "none" }],
            },
        ]);
        pkg.Users.AddRange([new CUser { Name = "jdoe", DisplayName = "J Doe" }, new CUser { Name = "asmith", DisplayName = "A Smith" }]);
        pkg.Classifications.Add(new CClassAssignment { ItemSourceId = "P-2001", ClassCode = "FASTENERS.BRACKET", Attributes = { ["Width"] = "20" } });

        Apply(pkg, files, defects);
        return new SampleProduct { Package = pkg, Files = files };
    }

    private static void Apply(CanonicalPackage pkg, Dictionary<string, byte[]> files, Defect d)
    {
        CObject Item(string n) => pkg.Items.First(i => i.Number == n);
        if (d.HasFlag(Defect.DuplicateNumber)) pkg.Items.Add(Part("P-2001", "Synthetic duplicate", "Part", "each", "N", null, Rev("P-2001", "A", 5, "Released", 20, It(1, 5, "jdoe", "x"))) .WithSourceId("P-2001-DUP"));
        if (d.HasFlag(Defect.UnknownState)) Item("P-2004").Revisions[0].States[0].Name = "Frozen";
        if (d.HasFlag(Defect.BadUnit)) Item("P-2003").Unit = "furlong";
        if (d.HasFlag(Defect.BomCycle)) Item("A-1110").Revisions[0].Iterations[0].Usages.Add(U("A-1000", 1, 40));
        if (d.HasFlag(Defect.MissingFile)) files.Remove("files/spec.pdf");
        if (d.HasFlag(Defect.ShaMismatch)) pkg.Documents[1].Revisions[0].Iterations[0].Files[0].Sha256 = new string('0', 64);
        if (d.HasFlag(Defect.MarkingsMissing)) { Item("P-2008").Properties.Remove("ITAR"); Item("P-2008").Properties.Remove("Project"); }
        if (d.HasFlag(Defect.MarkingsInvalid)) { Item("P-2007").Properties["ITAR"] = "Y"; Item("P-2007").Properties["Project"] = "OR CA!"; }
        if (d.HasFlag(Defect.RevisionAmbiguous))
        {
            var o = Part("P-2009", "Synthetic ambiguous revisions", "Part", "each", "N", null);
            foreach (var l in new[] { "A", "01" })
                o.Revisions.Add(new CRevision { SourceId = "P-2009/" + l, Label = l, States = [new CState { Name = "Released" }], Iterations = [new CIteration { Sequence = 1, Description = "x" }] });
            o.CreatedAt = null;
            pkg.Items.Add(o);
        }
        if (d.HasFlag(Defect.HugeAttribute))
        {
            Item("P-2001").Properties["Material"] = new string('M', 5000);
            Item("P-2001").Properties["Notes"] = new string('N', 100_000);
        }
    }

    private static CObject WithSourceId(this CObject o, string id)
    {
        o.SourceId = id;
        foreach (var r in o.Revisions) r.SourceId = id + "/" + r.Label;
        return o;
    }

    // ------------------------------------------------------------------------------------------------ mapping for the samples
    public static MappingConfig Mapping(bool defaultMarkings = false)
    {
        var cfg = new MappingConfig { Name = "synthetic-sample", AuditGranularity = "batch" };
        foreach (var s in new[] { "Released", "InWork", "Obsolete", "UnderReview" }) cfg.Lifecycle.States[s] = s;
        cfg.PartTypes.Map["Assembly"] = "Assembly"; cfg.PartTypes.Map["Part"] = "Component"; cfg.PartTypes.Map["Material"] = "RawMaterial";
        cfg.Units.Map["each"] = "EA";
        cfg.DocumentTypes.Map["Specification"] = "Specification"; cfg.DocumentTypes.Map["Drawing"] = "Drawing";
        cfg.Attributes.Map["Material"] = "material"; cfg.Attributes.Map["Mass"] = "mass";
        cfg.Users.Map["jdoe"] = "alice"; cfg.Users.Map["asmith"] = "bob";
        cfg.Markings.Rules.Add(new MarkingRule
        {
            Name = "itar-flag", When = new MarkingCondition { Property = "ITAR", EqualTo = "Y" },
            Set = new MarkingSpec { ExportJurisdiction = "Itar", UsmlCategory = "XI(d)", Cui = ["EXPT"], DistributionStatement = "D", ControllingOffice = "ACME", DistributionReason = "Critical technology", ProgramFromProperty = "Project", OwningOrg = "ENG" },
        });
        cfg.Markings.Rules.Add(new MarkingRule
        {
            Name = "not-itar", When = new MarkingCondition { Property = "ITAR", EqualTo = "N" },
            Set = new MarkingSpec { ExportJurisdiction = "None", DistributionStatement = "A", OwningOrg = "ENG" },
        });
        if (defaultMarkings)
            cfg.Markings.DefaultMarkings = new MarkingSpec { ExportJurisdiction = "Itar", UsmlCategory = "XI(d)", Cui = ["EXPT"], DistributionStatement = "F", ControllingOffice = "ACME", DistributionReason = "Unreviewed migration", Program = "ORCA", OwningOrg = "ENG" };
        return cfg;
    }

    public static string MappingJson(MappingConfig cfg) => JsonSerializer.Serialize(cfg, MappingConfig.Json);

    // ------------------------------------------------------------------------------------------------ writers
    private static void WriteFiles(SampleProduct p, string dir)
    {
        foreach (var (path, bytes) in p.Files)
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }
    }

    public static void WriteCanonical(SampleProduct p, string dir)
    {
        Directory.CreateDirectory(dir);
        Keel.Migration.Sources.CanonicalPackageReader.Write(p.Package, dir);
        WriteFiles(p, dir);
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string Iso(DateTimeOffset? t) => t?.ToString("O", CultureInfo.InvariantCulture) ?? "";

    public static void WritePlmXml(SampleProduct p, string dir, Defect defects = Defect.None)
    {
        Directory.CreateDirectory(dir);
        using (var w = XmlWriter.Create(Path.Combine(dir, "export.plmxml"), new XmlWriterSettings { Indent = true }))
            WritePlmXmlTo(p.Package, w, defects);
        WriteFiles(p, dir);
    }

    private static void UserData(XmlWriter w, string type, IEnumerable<KeyValuePair<string, string?>> values)
    {
        w.WriteStartElement("UserData");
        w.WriteAttributeString("type", type);
        foreach (var (k, v) in values)
        {
            w.WriteStartElement("UserValue");
            w.WriteAttributeString("title", k);
            w.WriteAttributeString("value", v ?? "");
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private static void WritePlmXmlTo(CanonicalPackage pkg, XmlWriter w, Defect defects)
    {
        const string Ns = "http://www.plmxml.org/Schemas/PLMXMLSchema";
        w.WriteStartDocument();
        w.WriteStartElement("PLMXML", Ns);
        w.WriteAttributeString("schemaVersion", "6");
        w.WriteAttributeString("author", "synthetic exporter");
        w.WriteAttributeString("date", pkg.ExportedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "2020-01-01");
        w.WriteAttributeString("time", "00:00:00");
        w.WriteStartElement("Header"); w.WriteAttributeString("id", "hdr"); w.WriteEndElement();
        foreach (var u in pkg.Users)
        {
            w.WriteStartElement("Person"); w.WriteAttributeString("id", "u_" + u.Name); w.WriteAttributeString("userName", u.Name); w.WriteAttributeString("name", u.DisplayName ?? u.Name); w.WriteEndElement();
        }
        if (defects.HasFlag(Defect.UnknownElement)) { w.WriteStartElement("Annotation"); w.WriteAttributeString("id", "ann1"); w.WriteString("synthetic note"); w.WriteEndElement(); }

        var seq = 0;
        var danglingDone = false;
        var viewCounter = 0;
        var instanceCounter = 0;
        var datasetCounter = 0;
        void WriteObject(CObject o, bool isDoc)
        {
            w.WriteStartElement("Product");
            w.WriteAttributeString("id", o.SourceId); w.WriteAttributeString("name", o.Name); w.WriteAttributeString("productId", o.Number);
            w.WriteAttributeString("subType", isDoc ? "Document" : o.Type);
            if (o.Description is not null) w.WriteElementString("Description", o.Description);
            var props = new List<KeyValuePair<string, string?>> { new("creation_date", Iso(o.CreatedAt)), new("owning_user", o.CreatedBy) };
            if (o.Unit is not null) props.Add(new("uom", o.Unit));
            if (o.Source is not null) props.Add(new("make_buy", o.Source));
            props.AddRange(o.Properties);
            UserData(w, "Attributes", props);
            if (defects.HasFlag(Defect.UnknownElement) && !isDoc && o.Number == "P-2001") { w.WriteStartElement("Weird"); w.WriteAttributeString("a", "1"); w.WriteString("kept as a property"); w.WriteEndElement(); }
            w.WriteEndElement();
            foreach (var r in o.Revisions)
                foreach (var it in r.Iterations)
                {
                    var revId = $"R{++seq}";
                    w.WriteStartElement("ProductRevision");
                    w.WriteAttributeString("id", revId); w.WriteAttributeString("masterRef", "#" + o.SourceId); w.WriteAttributeString("revision", r.Label); w.WriteAttributeString("name", o.Name);
                    w.WriteElementString("Description", it.Description ?? "");
                    var a = new List<KeyValuePair<string, string?>> { new("creation_date", Iso(it.CreatedAt)), new("owning_user", it.CreatedBy), new("sequence_id", it.Sequence.ToString(CultureInfo.InvariantCulture)), new("comment", it.Comment) };
                    a.AddRange(it.Properties);
                    UserData(w, "Attributes", a);
                    if (it == r.Iterations[^1])
                        foreach (var s in r.States) UserData(w, "ReleaseStatus", [new("name", s.Name), new("date_released", Iso(s.At)), new("user", s.By)]);
                    foreach (var l in it.DocLinks) UserData(w, "DocumentLink", [new("document", l.DocumentSourceId), new("type", l.Type), new("revision", l.DocumentRevisionLabel)]);
                    var dsIds = new List<string>();
                    if (it.Files.Count > 0)
                        foreach (var f in it.Files)
                        {
                            var id = $"DS{++datasetCounter}";
                            dsIds.Add(id);
                        }
                    for (var i = 0; i < dsIds.Count; i++)
                    {
                        w.WriteStartElement("AssociatedDataSet"); w.WriteAttributeString("dataSetRef", "#" + dsIds[i]); w.WriteAttributeString("role", "IMAN_specification"); w.WriteEndElement();
                    }
                    w.WriteEndElement();
                    // datasets of this iteration
                    for (var i = 0; i < dsIds.Count; i++)
                    {
                        var f = it.Files[i];
                        w.WriteStartElement("DataSet"); w.WriteAttributeString("id", dsIds[i]); w.WriteAttributeString("name", f.Name); w.WriteAttributeString("type", "Binary");
                        UserData(w, "Attributes", [new("sha256", f.Sha256), new("size_bytes", f.SizeBytes?.ToString(CultureInfo.InvariantCulture)), new("media_type", f.MediaType)]);
                        w.WriteStartElement("ExternalFile"); w.WriteAttributeString("id", "EF" + dsIds[i]); w.WriteAttributeString("locationRef", f.PackagePath); w.WriteEndElement();
                        w.WriteEndElement();
                    }
                    if (it.Usages.Count > 0)
                    {
                        var viewId = $"V{++viewCounter}";
                        var instIds = new List<string>();
                        foreach (var u in it.Usages)
                        {
                            var id = $"I{++instanceCounter}";
                            instIds.Add(id);
                        }
                        w.WriteStartElement("ProductRevisionView"); w.WriteAttributeString("id", viewId); w.WriteAttributeString("revisionRef", "#" + revId);
                        w.WriteAttributeString("instanceRefs", string.Join(' ', instIds.Select(i => "#" + i)));
                        w.WriteEndElement();
                        for (var i = 0; i < it.Usages.Count; i++)
                        {
                            var u = it.Usages[i];
                            w.WriteStartElement("ProductInstance"); w.WriteAttributeString("id", instIds[i]);
                            var part = defects.HasFlag(Defect.DanglingIdRef) && !danglingDone ? "#missing-product" : "#" + u.ChildSourceId;
                            danglingDone |= defects.HasFlag(Defect.DanglingIdRef);
                            w.WriteAttributeString("partRef", part);
                            w.WriteAttributeString("quantity", u.Quantity.ToString(CultureInfo.InvariantCulture));
                            UserData(w, "Occurrence", [new("sequence_no", u.FindNumber?.ToString(CultureInfo.InvariantCulture)), new("unit", u.Unit)]);
                            w.WriteStartElement("Transform"); w.WriteString("1 0 0 0 0 1 0 0 0 0 1 0 0 0 0 1"); w.WriteEndElement();
                            foreach (var dsg in u.ReferenceDesignators)
                            {
                                w.WriteStartElement("Occurrence"); w.WriteAttributeString("id", $"O{instIds[i]}{dsg}");
                                UserData(w, "Occurrence", [new("reference_designator", dsg)]);
                                w.WriteEndElement();
                            }
                            w.WriteEndElement();
                        }
                    }
                }
        }
        foreach (var o in pkg.Items) WriteObject(o, false);
        foreach (var o in pkg.Documents) WriteObject(o, true);
        foreach (var c in pkg.Changes)
        {
            w.WriteStartElement("Product");
            w.WriteAttributeString("id", c.SourceId); w.WriteAttributeString("name", c.Title); w.WriteAttributeString("productId", c.Number);
            w.WriteAttributeString("subType", c.Type switch { "PR" => "ProblemReport", "ECR" => "ChangeRequest", _ => "ChangeNotice" });
            w.WriteElementString("Description", c.Description ?? "");
            UserData(w, "Attributes", [new("creation_date", Iso(c.CreatedAt)), new("owning_user", c.CreatedBy)]);
            w.WriteEndElement();
            w.WriteStartElement("ProductRevision");
            w.WriteAttributeString("id", $"R{++seq}"); w.WriteAttributeString("masterRef", "#" + c.SourceId); w.WriteAttributeString("revision", "A"); w.WriteAttributeString("name", c.Title);
            var props = new List<KeyValuePair<string, string?>> { new("change_class", c.ChangeClass), new("priority", c.Priority), new("reason", c.Reason), new("parent", c.ParentSourceId) };
            props.AddRange(c.Properties);
            UserData(w, "Attributes", props.Where(x => x.Value is not null));
            UserData(w, "ReleaseStatus", [new("name", c.State), new("date_released", Iso(c.StateChangedAt)), new("user", c.StateChangedBy)]);
            foreach (var a in c.Affected) UserData(w, "ChangeAffected", [new("item", a.ItemSourceId), new("revision", a.RevisionLabel), new("resulting_revision", a.ResultingRevisionLabel), new("action", a.Action)]);
            w.WriteEndElement();
        }
        foreach (var cl in pkg.Classifications)
        {
            // classification is carried as UserData on the item's last ProductRevision in real exports; the sample keeps it simple and omits it from PLM XML
            _ = cl;
        }
        w.WriteEndElement();
        w.WriteEndDocument();
    }

    // ---- Windchill: one logical row model, written as JSON (OData shape) or CSV
    private sealed record Tables(List<Dictionary<string, object?>> Parts, List<Dictionary<string, object?>> Uses, List<Dictionary<string, object?>> Docs,
        List<Dictionary<string, object?>> DocFiles, List<Dictionary<string, object?>> Links, List<Dictionary<string, object?>> Prs, List<Dictionary<string, object?>> Ecrs,
        List<Dictionary<string, object?>> Ecns, List<Dictionary<string, object?>> Affected, List<Dictionary<string, object?>> Users, List<Dictionary<string, object?>> Classes);

    private static Tables WindchillTables(CanonicalPackage pkg, bool json)
    {
        var t = new Tables([], [], [], [], [], [], [], [], [], [], []);
        object? Enum(string v) => json ? new Dictionary<string, object?> { ["Value"] = v, ["Display"] = v } : v;
        foreach (var o in pkg.Items)
            foreach (var r in o.Revisions)
                foreach (var it in r.Iterations)
                {
                    var row = new Dictionary<string, object?>
                    {
                        ["ID"] = $"OR:wt.part.WTPart:{o.Number}.{r.Label}.{it.Sequence}", ["Number"] = o.Number, ["Name"] = o.Name, ["Version"] = r.Label, ["Iteration"] = it.Sequence.ToString(CultureInfo.InvariantCulture),
                        ["State"] = Enum(r.States.Count > 0 ? r.States[^1].Name : ""), ["StateChangedOn"] = Iso(r.States.LastOrDefault()?.At), ["StateChangedBy"] = r.States.LastOrDefault()?.By,
                        ["CreatedOn"] = Iso(it.Sequence == 1 && r == o.Revisions[0] ? o.CreatedAt : r.CreatedAt), ["CreatedBy"] = r == o.Revisions[0] && it.Sequence == 1 ? o.CreatedBy : r.CreatedBy,
                        ["LastModified"] = Iso(it.CreatedAt), ["ModifiedBy"] = it.CreatedBy, ["DefaultUnit"] = Enum(o.Unit ?? ""), ["Source"] = Enum(o.Source ?? ""), ["PartType"] = o.Type, ["Description"] = it.Description, ["Comment"] = it.Comment,
                    };
                    foreach (var (k, v) in o.Properties) row[k] = v;
                    foreach (var (k, v) in it.Properties) row[k] = v;
                    t.Parts.Add(row);
                    foreach (var u in it.Usages)
                        t.Uses.Add(new() { ["ParentNumber"] = o.Number, ["ParentVersion"] = r.Label, ["ParentIteration"] = it.Sequence.ToString(CultureInfo.InvariantCulture), ["ChildNumber"] = u.ChildSourceId, ["Quantity"] = u.Quantity.ToString(CultureInfo.InvariantCulture), ["Unit"] = u.Unit, ["FindNumber"] = u.FindNumber?.ToString(CultureInfo.InvariantCulture), ["ReferenceDesignators"] = string.Join(',', u.ReferenceDesignators), ["Notes"] = u.Notes });
                    foreach (var l in it.DocLinks)
                        t.Links.Add(new() { ["PartNumber"] = o.Number, ["PartVersion"] = r.Label, ["PartIteration"] = it.Sequence.ToString(CultureInfo.InvariantCulture), ["DocumentNumber"] = l.DocumentSourceId, ["DocumentVersion"] = l.DocumentRevisionLabel, ["LinkType"] = l.Type == "references" ? "Reference" : "Describe" });
                }
        foreach (var o in pkg.Documents)
            foreach (var r in o.Revisions)
                foreach (var it in r.Iterations)
                {
                    var row = new Dictionary<string, object?>
                    {
                        ["ID"] = $"OR:wt.doc.WTDocument:{o.Number}.{r.Label}.{it.Sequence}", ["Number"] = o.Number, ["Name"] = o.Name, ["Version"] = r.Label, ["Iteration"] = it.Sequence.ToString(CultureInfo.InvariantCulture),
                        ["State"] = Enum(r.States.Count > 0 ? r.States[^1].Name : ""), ["StateChangedOn"] = Iso(r.States.LastOrDefault()?.At), ["StateChangedBy"] = r.States.LastOrDefault()?.By,
                        ["CreatedOn"] = Iso(it.Sequence == 1 && r == o.Revisions[0] ? o.CreatedAt : r.CreatedAt), ["CreatedBy"] = r == o.Revisions[0] && it.Sequence == 1 ? o.CreatedBy : r.CreatedBy,
                        ["LastModified"] = Iso(it.CreatedAt), ["ModifiedBy"] = it.CreatedBy, ["DocumentType"] = o.Type, ["Description"] = it.Description, ["Comment"] = it.Comment,
                    };
                    foreach (var (k, v) in o.Properties) row[k] = v;
                    t.Docs.Add(row);
                    foreach (var f in it.Files)
                        t.DocFiles.Add(new() { ["DocumentNumber"] = o.Number, ["DocumentVersion"] = r.Label, ["DocumentIteration"] = it.Sequence.ToString(CultureInfo.InvariantCulture), ["Name"] = f.Name, ["ContentType"] = f.MediaType, ["Path"] = f.PackagePath, ["FileSize"] = f.SizeBytes?.ToString(CultureInfo.InvariantCulture), ["Sha256"] = f.Sha256, ["Role"] = f.Primary ? "PRIMARY" : "SECONDARY" });
                }
        foreach (var c in pkg.Changes)
        {
            var row = new Dictionary<string, object?>
            {
                ["Number"] = c.Number, ["Name"] = c.Title, ["Description"] = c.Description, ["State"] = Enum(c.State), ["ChangeClass"] = c.ChangeClass, ["Priority"] = c.Priority, ["Reason"] = c.Reason,
                ["CreatedOn"] = Iso(c.CreatedAt), ["CreatedBy"] = c.CreatedBy, ["StateChangedOn"] = Iso(c.StateChangedAt), ["StateChangedBy"] = c.StateChangedBy, ["ParentNumber"] = c.ParentSourceId,
            };
            foreach (var (k, v) in c.Properties) row[k] = v;
            (c.Type == "PR" ? t.Prs : c.Type == "ECR" ? t.Ecrs : t.Ecns).Add(row);
            foreach (var a in c.Affected) t.Affected.Add(new() { ["ChangeNumber"] = c.Number, ["ItemNumber"] = a.ItemSourceId, ["Version"] = a.RevisionLabel, ["ResultingVersion"] = a.ResultingRevisionLabel, ["Action"] = a.Action });
        }
        foreach (var u in pkg.Users) t.Users.Add(new() { ["Name"] = u.Name, ["FullName"] = u.DisplayName });
        foreach (var c in pkg.Classifications) { var row = new Dictionary<string, object?> { ["PartNumber"] = c.ItemSourceId, ["ClassCode"] = c.ClassCode }; foreach (var (k, v) in c.Attributes) row[k] = v; t.Classes.Add(row); }
        return t;
    }

    public static void WriteWindchillJson(SampleProduct p, string dir)
    {
        Directory.CreateDirectory(dir);
        var t = WindchillTables(p.Package, json: true);
        void Write(string file, List<Dictionary<string, object?>> rows, Action<Dictionary<string, object?>, Dictionary<string, object?>>? nest = null)
        {
            var value = rows.Select(r =>
            {
                var o = new Dictionary<string, object?>(r);
                nest?.Invoke(o, r);
                return o;
            }).ToList();
            File.WriteAllText(Path.Combine(dir, file), JsonSerializer.Serialize(new Dictionary<string, object?> { ["@odata.context"] = "synthetic", ["value"] = value }, Indented));
        }
        Write("parts.json", t.Parts);
        Write("part-uses.json", t.Uses);
        // Files nest inside the document iteration, as the extract spec allows
        Write("documents.json", t.Docs, (o, r) =>
            o["Files"] = t.DocFiles.Where(f => (string?)f["DocumentNumber"] == (string?)r["Number"] && (string?)f["DocumentVersion"] == (string?)r["Version"] && (string?)f["DocumentIteration"] == (string?)r["Iteration"])
                .Select(f => f.Where(kv => !kv.Key.StartsWith("Document", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value)).ToList());
        Write("part-doc-links.json", t.Links);
        Write("problem-reports.json", t.Prs);
        Write("change-requests.json", t.Ecrs);
        Write("change-notices.json", t.Ecns, (o, r) =>
            o["AffectedItems"] = t.Affected.Where(a => (string?)a["ChangeNumber"] == (string?)r["Number"]).Select(a => a.Where(kv => kv.Key != "ChangeNumber").ToDictionary(kv => kv.Key, kv => kv.Value)).ToList());
        Write("users.json", t.Users);
        Write("classifications.json", t.Classes);
        WriteFiles(p, dir);
    }

    public static void WriteWindchillCsv(SampleProduct p, string dir)
    {
        Directory.CreateDirectory(dir);
        var t = WindchillTables(p.Package, json: false);
        void Write(string file, List<Dictionary<string, object?>> rows)
        {
            if (rows.Count == 0) return;
            var cols = rows.SelectMany(r => r.Keys).Distinct().ToList();
            var sb = new StringBuilder(string.Join(',', cols)).Append('\n');
            foreach (var r in rows)
                sb.Append(string.Join(',', cols.Select(c => CsvCell(r.GetValueOrDefault(c)?.ToString()))))
                    .Append('\n');
            File.WriteAllText(Path.Combine(dir, file), sb.ToString());
        }
        Write("parts.csv", t.Parts); Write("part_uses.csv", t.Uses); Write("documents.csv", t.Docs); Write("document_files.csv", t.DocFiles);
        Write("part_doc_links.csv", t.Links); Write("problem_reports.csv", t.Prs); Write("change_requests.csv", t.Ecrs); Write("change_notices.csv", t.Ecns);
        Write("change_affected.csv", t.Affected); Write("users.csv", t.Users); Write("classifications.csv", t.Classes);
        WriteFiles(p, dir);
    }

    private static string CsvCell(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        return v.Contains(',') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    // ------------------------------------------------------------------------------------------------ scale
    /// <summary>
    /// A large synthetic product: <paramref name="assemblies"/> single-revision assemblies with <paramref name="linesPerAssembly"/> BOM lines each (children
    /// always have a higher index: acyclic), the rest components with two revisions, and <paramref name="documents"/> documents with one file each.
    /// </summary>
    public static SampleProduct BuildScale(int parts, int assemblies, int linesPerAssembly, int documents, int fileBytes = 2048)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var pkg = new CanonicalPackage { SourceSystem = "canonical", ExportedAt = Day(900) };
        string N(int i) => $"S-{i:D6}";
        for (var i = 0; i < parts; i++)
        {
            var isAssembly = i < assemblies;
            var o = new CObject
            {
                SourceId = N(i), Number = N(i), Name = "Scale part " + i, Type = isAssembly ? "Assembly" : "Part", Unit = "each", Source = "Make", CreatedAt = Day(0), CreatedBy = "jdoe",
                Properties = new Dictionary<string, string?> { ["ITAR"] = i % 3 == 0 ? "Y" : "N", ["Material"] = "Steel" },
            };
            if (i % 3 == 0) o.Properties["Project"] = "ORCA";
            var rev = new CRevision { SourceId = N(i) + "/A", Label = "A", CreatedAt = Day(1), CreatedBy = "jdoe", States = [S("Released", 10)] };
            var it = new CIteration { Sequence = 1, CreatedAt = Day(1), CreatedBy = "jdoe", Description = "Scale iteration" };
            if (isAssembly)
                for (var l = 0; l < linesPerAssembly; l++)
                {
                    var child = assemblies + ((i * 7 + l * 13) % Math.Max(1, parts - assemblies));
                    if (child <= i) child = Math.Min(parts - 1, i + 1 + l);
                    if (child > i && it.Usages.All(u => u.ChildSourceId != N(child))) it.Usages.Add(U(N(child), 1 + l % 3, 10 * (l + 1)));
                }
            rev.Iterations.Add(it);
            o.Revisions.Add(rev);
            if (!isAssembly)
            {
                o.Revisions.Add(new CRevision { SourceId = N(i) + "/B", Label = "B", CreatedAt = Day(20), CreatedBy = "asmith", States = [S("InWork", 20)],
                    Iterations = [new CIteration { Sequence = 1, CreatedAt = Day(20), CreatedBy = "asmith", Description = "B1" }, new CIteration { Sequence = 2, CreatedAt = Day(21), CreatedBy = "asmith", Description = "B2" }] });
            }
            pkg.Items.Add(o);
        }
        for (var d = 0; d < documents; d++)
        {
            var name = $"scale-{d:D4}.txt";
            var bytes = Encoding.ASCII.GetBytes($"SYNTHETIC {d} ").Concat(Enumerable.Repeat((byte)(65 + d % 26), fileBytes)).ToArray();
            files["files/" + name] = bytes;
            pkg.Documents.Add(new CObject
            {
                SourceId = $"SD-{d:D4}", Number = $"SD-{d:D4}", Name = "Scale doc " + d, Type = "Specification", CreatedAt = Day(1), CreatedBy = "jdoe",
                Properties = new Dictionary<string, string?> { ["ITAR"] = "N" },
                Revisions = [new CRevision { SourceId = $"SD-{d:D4}/A", Label = "A", CreatedAt = Day(1), CreatedBy = "jdoe", States = [S("Released", 10)],
                    Iterations = [new CIteration { Sequence = 1, CreatedAt = Day(1), CreatedBy = "jdoe", Description = "d", Files = [new CFile { Name = name, MediaType = "text/plain", PackagePath = "files/" + name, SizeBytes = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Primary = true }] }] }],
            });
        }
        return new SampleProduct { Package = pkg, Files = files };
    }

    /// <summary>
    /// Streams a PLM XML file of roughly <paramref name="targetBytes"/> to disk without building it in memory (for the memory-flatness test). Each part
    /// carries ignorable bulk (a Representation element) so the file is large while the extracted data stays small, like real exports full of geometry.
    /// </summary>
    public static int WriteLargePlmXml(string path, long targetBytes)
    {
        using var w = XmlWriter.Create(path, new XmlWriterSettings { Indent = false, CloseOutput = true });
        w.WriteStartDocument();
        w.WriteStartElement("PLMXML", "http://www.plmxml.org/Schemas/PLMXMLSchema");
        w.WriteAttributeString("schemaVersion", "6"); w.WriteAttributeString("date", "2020-01-01"); w.WriteAttributeString("time", "00:00:00");
        var bulk = new string('x', 16 * 1024);
        var count = 0;
        var seq = 0;
        while (true)
        {
            w.Flush();
            if (w.Settings is not null && new FileInfo(path).Length >= targetBytes) break;
            var n = $"L-{count:D7}";
            w.WriteStartElement("Product"); w.WriteAttributeString("id", n); w.WriteAttributeString("name", "Large " + count); w.WriteAttributeString("productId", n); w.WriteAttributeString("subType", "Part");
            w.WriteStartElement("UserData"); w.WriteAttributeString("type", "Attributes");
            foreach (var (k, v) in new[] { ("ITAR", "N"), ("uom", "each"), ("creation_date", "2020-01-01T00:00:00Z") }) { w.WriteStartElement("UserValue"); w.WriteAttributeString("title", k); w.WriteAttributeString("value", v); w.WriteEndElement(); }
            w.WriteEndElement(); w.WriteEndElement();
            w.WriteStartElement("ProductRevision"); w.WriteAttributeString("id", "LR" + (++seq)); w.WriteAttributeString("masterRef", "#" + n); w.WriteAttributeString("revision", "A");
            w.WriteStartElement("UserData"); w.WriteAttributeString("type", "ReleaseStatus");
            foreach (var (k, v) in new[] { ("name", "Released"), ("date_released", "2020-02-01T00:00:00Z") }) { w.WriteStartElement("UserValue"); w.WriteAttributeString("title", k); w.WriteAttributeString("value", v); w.WriteEndElement(); }
            w.WriteEndElement(); w.WriteEndElement();
            w.WriteStartElement("Representation"); w.WriteAttributeString("id", "REP" + seq); w.WriteString(bulk); w.WriteEndElement();
            count++;
        }
        w.WriteEndElement();
        w.WriteEndDocument();
        return count;
    }
}
