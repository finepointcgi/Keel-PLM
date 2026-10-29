using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Keel.Cad;
using Keel.Cad.Structure;
using Keel.Core;
using Keel.TestSupport;

namespace Keel.UnitTests;

/// <summary>CAD structure importers, the proposal diff and the rules of the CAD module (ADR-0035).</summary>
public sealed class CadUnitTests
{
    private const string Step = """
        ISO-10303-21;
        HEADER;
        FILE_DESCRIPTION(('synthetic'),'2;1');
        FILE_NAME('gimbal.stp','2026-10-29T00:00:00',('x'),('x'),'p','NXSYNTH','');
        FILE_SCHEMA(('AP242_MANAGED_MODEL_BASED_3D_ENGINEERING_MIM_LF { 1 0 10303 442 1 1 4 }'));
        ENDSEC;
        DATA;
        /* a comment with ; and ' inside */
        #1=APPLICATION_CONTEXT('x');
        #10=PRODUCT('GIM-100','Gimbal ''assembly''','',(#1));
        #11=PRODUCT_DEFINITION_FORMATION('A','',#10);
        #12=PRODUCT_DEFINITION('design','',#11,#1);
        #20=PRODUCT('BRK-200','Bracket','',(#1));
        #21=PRODUCT_DEFINITION_FORMATION_WITH_SPECIFIED_SOURCE('A','',#20,.BOUGHT.);
        #22=PRODUCT_DEFINITION('design','',#21,#1);
        #30=PRODUCT('SCR-300','Screw M3','',(#1));
        #31=PRODUCT_DEFINITION_FORMATION('A','',#30);
        #32=PRODUCT_DEFINITION('design','',#31,#1);
        #40=NEXT_ASSEMBLY_USAGE_OCCURRENCE('10','','',#12,#22,$);
        #41=NEXT_ASSEMBLY_USAGE_OCCURRENCE('20','','',#12,#32,'S1');
        #42=NEXT_ASSEMBLY_USAGE_OCCURRENCE('21','','',#12,#32,'S2');
        #43=NEXT_ASSEMBLY_USAGE_OCCURRENCE('22','','',#12,#32,'S3 left');
        #50=CARTESIAN_POINT('',(0.,0.,0.));
        #60=(GEOMETRIC_REPRESENTATION_CONTEXT(3) GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT((#50)));
        ENDSEC;
        END-ISO-10303-21;
        """;

    [Fact]
    public void A_step_product_structure_is_read_with_quantities_designators_and_source()
    {
        var m = StructureReader.Read("step", Encoding.Latin1.GetBytes(Step));
        Assert.Equal("STEP AP242", m.SourceSystem);
        Assert.Equal(3, m.Parts.Count);
        var root = m.Parts.Single(p => p.Ref == m.RootRef);
        Assert.Equal(("GIM-100", "Gimbal 'assembly'", "Assembly"), (root.Number, root.Name, root.PartType));
        Assert.Equal("Buy", m.Parts.Single(p => p.Number == "BRK-200").Source);
        var screw = m.Lines.Single(l => m.Parts.Single(p => p.Ref == l.ChildRef).Number == "SCR-300");
        Assert.Equal(3m, screw.Quantity);
        Assert.Equal(["S1", "S2"], screw.ReferenceDesignators);
        Assert.Contains("S3 left", screw.Notes);
        Assert.Null(screw.FindNumber); // three occurrences: no single find number
        Assert.Equal(10, m.Lines.Single(l => l.Quantity == 1m).FindNumber);
    }

    [Fact]
    public void A_step_file_with_two_top_products_needs_a_root_and_a_subtree_can_be_chosen()
    {
        var two = Step.Replace("ENDSEC;\nEND-ISO", "#70=PRODUCT('OTHER','Other','',(#1));\n#71=PRODUCT_DEFINITION_FORMATION('A','',#70);\n#72=PRODUCT_DEFINITION('design','',#71,#1);\n#73=NEXT_ASSEMBLY_USAGE_OCCURRENCE('1','','',#72,#32,$);\nENDSEC;\nEND-ISO");
        var e = Assert.Throws<StructureFormatException>(() => StructureReader.Read("step", Encoding.Latin1.GetBytes(two)));
        Assert.Contains("top-level products", e.Message);
        var m = StructureReader.Read("step", Encoding.Latin1.GetBytes(two), new StructureReadOptions("OTHER"));
        Assert.Equal(2, m.Parts.Count);
    }

    [Theory]
    [InlineData("not a step file")]
    [InlineData("ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\n#1=PRODUCT('A','B','',(#2)")]
    [InlineData("ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\n#1=PRODUCT('A','B\n")]
    [InlineData("ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;")]
    public void Malformed_step_is_refused_with_a_message_not_a_crash(string text) =>
        Assert.Throws<StructureFormatException>(() => StructureReader.Read("step", Encoding.Latin1.GetBytes(text)));

    [Fact]
    public void Deeply_nested_step_arguments_are_refused_without_overflowing_the_stack()
    {
        var text = "ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\n#1=PRODUCT(" + new string('(', 5000) + new string(')', 5000) + ");\nENDSEC;";
        Assert.Throws<StructureFormatException>(() => StructureReader.Read("step", Encoding.Latin1.GetBytes(text)));
    }

    [Fact]
    public void Csv_handles_quotes_delimiters_attributes_and_a_flat_bom()
    {
        const string csv = "parent;child;name;qty;uom;find;refdes;match.cage_code;attr.material\nTOP;P-1;\"Plate, 2 mm\";2;EA;10;R1 R2;1ABC2;Al\nTOP;P-2;Washer;4;;;;;\n";
        var m = StructureReader.Read("csv", Encoding.UTF8.GetBytes(csv));
        Assert.Equal("TOP", m.RootRef);
        var plate = m.Parts.Single(p => p.Ref == "P-1");
        Assert.Equal("Plate, 2 mm", plate.Name);
        Assert.Equal("Al", plate.Attributes["material"]);
        Assert.Equal(new MatchRule("cage_code", "1ABC2"), plate.MatchOn.Single());
        Assert.Equal("Assembly", m.Parts.Single(p => p.Ref == "TOP").PartType);
        Assert.Equal(["R1", "R2"], m.Lines[0].ReferenceDesignators);
        Assert.Equal(10, m.Lines[0].FindNumber);
        var flat = StructureReader.Read("csv", Encoding.UTF8.GetBytes("child,qty\nP-1,2\nP-2,1\n"), new StructureReadOptions("ASM-1"));
        Assert.Equal(2, flat.Lines.Count);
        Assert.Throws<StructureFormatException>(() => StructureReader.Read("csv", Encoding.UTF8.GetBytes("child,qty\nP-1,2\n")));
        Assert.Throws<StructureFormatException>(() => StructureReader.Read("csv", Encoding.UTF8.GetBytes("parent,child,qty\nA,B,zero\n")));
    }

    [Fact]
    public void Xml_is_read_and_a_dtd_with_an_external_entity_is_refused()
    {
        const string xml = """<structure root="a" system="Script"><part ref="a" number="P-1" name="Top"/><part ref="b" name="Child"><attribute key="material" value="Al"/></part><line parent="a" child="b" quantity="2" refdes="R1,R2"/></structure>""";
        var m = StructureReader.Read("xml", Encoding.UTF8.GetBytes(xml));
        Assert.Equal("Script", m.SourceSystem);
        Assert.Equal(2m, m.Lines.Single().Quantity);
        const string xxe = """<!DOCTYPE s [<!ENTITY x SYSTEM "file:///etc/passwd">]><structure root="a"><part ref="a" name="&x;"/></structure>""";
        Assert.Throws<StructureFormatException>(() => StructureReader.Read("xml", Encoding.UTF8.GetBytes(xxe)));
    }

    private static JsonObject ValidDocument() => JsonNode.Parse("""
        {"schema":"keel.cad.structure/v1","source":{"system":"NX"},"root":"a",
         "parts":[{"ref":"a","number":"P-000001","name":"Top"},{"ref":"b","name":"Bolt","attributes":{"cage_code":"1ABC2"},"matchOn":[{"attribute":"cage_code","value":"1ABC2"}]}],
         "lines":[{"parent":"a","child":"b","quantity":4,"findNumber":10,"referenceDesignators":["B1","B2","B3","B4"]}]}
        """)!.AsObject();

    [Fact]
    public void The_neutral_json_is_read_and_the_published_schema_accepts_it_and_rejects_bad_documents()
    {
        var doc = ValidDocument();
        var schema = JsonNode.Parse(CadModule.StructureSchema())!;
        Assert.Empty(JsonSchemaLite.Validate(doc, schema));
        var m = StructureReader.Read("json", Encoding.UTF8.GetBytes(doc.ToJsonString()));
        Assert.Equal(4m, m.Lines.Single().Quantity);

        var wrongSchema = ValidDocument(); wrongSchema["schema"] = "other";
        Assert.NotEmpty(JsonSchemaLite.Validate(wrongSchema, schema));
        Assert.Throws<StructureFormatException>(() => StructureReader.Read("json", Encoding.UTF8.GetBytes(wrongSchema.ToJsonString())));
        var zero = ValidDocument(); zero["lines"]![0]!["quantity"] = JsonNode.Parse("0");
        Assert.NotEmpty(JsonSchemaLite.Validate(zero, schema));
        Assert.Throws<StructureFormatException>(() => StructureReader.Read("json", Encoding.UTF8.GetBytes(zero.ToJsonString())));
        var extra = ValidDocument(); extra["surprise"] = JsonNode.Parse("1");
        Assert.NotEmpty(JsonSchemaLite.Validate(extra, schema));
    }

    [Theory]
    [InlineData("""{"schema":"keel.cad.structure/v1","root":"a","parts":[{"ref":"a","name":"A"},{"ref":"b","name":"B"}],"lines":[{"parent":"a","child":"b","quantity":1},{"parent":"b","child":"a","quantity":1}]}""", "used by another part")]
    [InlineData("""{"schema":"keel.cad.structure/v1","root":"a","parts":[{"ref":"a","name":"A"},{"ref":"b","name":"B"},{"ref":"c","name":"C"}],"lines":[{"parent":"a","child":"b","quantity":1},{"parent":"b","child":"c","quantity":1},{"parent":"c","child":"b","quantity":1}]}""", "cycle")]
    [InlineData("""{"schema":"keel.cad.structure/v1","root":"a","parts":[{"ref":"a","name":"A"},{"ref":"a","name":"B"}]}""", "twice")]
    [InlineData("""{"schema":"keel.cad.structure/v1","root":"z","parts":[{"ref":"a","name":"A"}]}""", "root")]
    [InlineData("""{"schema":"keel.cad.structure/v1","root":"a","parts":[{"ref":"a","name":"A"}],"lines":[{"parent":"a","child":"q","quantity":1}]}""", "not in the structure")]
    public void Structural_errors_are_named(string json, string expected)
    {
        var e = Assert.Throws<StructureFormatException>(() => StructureReader.Read("json", Encoding.UTF8.GetBytes(json)));
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    private static ResolvedLine R(string child, decimal qty, int? find = null, string? unit = null, params string[] refs) => new("r-" + child, child, child, qty, unit, find, refs, null, true);
    private static CadBomLine C(string child, decimal qty, int find, string unit = "EA", params string[] refs) => new(child, null, qty, unit, find, refs, null);

    [Fact]
    public void The_diff_pairs_by_child_and_find_number_and_reports_added_removed_changed_and_unchanged()
    {
        var current = new[] { C("P-1", 2, 10), C("P-2", 1, 20), C("P-3", 5, 30, "EA", "R1", "R2") };
        var proposed = new[] { R("P-1", 2, 10), R("P-2", 3, 20), R("P-3", 5, null, null, "R2", "R1"), R("P-4", 1) };
        var pairs = ProposalDiff.Compute(proposed, [.. current, C("P-9", 1, 40)]);
        Assert.Equal([DiffKind.Unchanged, DiffKind.Changed, DiffKind.Unchanged, DiffKind.Added, DiffKind.Removed], pairs.Select(p => p.Kind));
        var edits = ProposalDiff.Edits(pairs, applyRemovals: false);
        Assert.Equal([CadBomEditKind.Update, CadBomEditKind.Add], edits.Select(e => e.Kind));
        Assert.Equal(3m, edits[0].Quantity);
        Assert.Equal(20, edits[0].CurrentFindNumber);
        Assert.Equal([CadBomEditKind.Remove, CadBomEditKind.Update, CadBomEditKind.Add], ProposalDiff.Edits(pairs, applyRemovals: true).Select(e => e.Kind));
    }

    [Fact]
    public void The_same_child_on_two_lines_is_paired_by_find_number_and_unresolved_children_produce_no_edit()
    {
        var current = new[] { C("P-1", 1, 10), C("P-1", 2, 20) };
        var pairs = ProposalDiff.Compute([R("P-1", 2, 20), R("P-1", 1, 10)], current);
        Assert.All(pairs, p => Assert.Equal(DiffKind.Unchanged, p.Kind));
        var hidden = new ResolvedLine("x", null, "New part", 1, null, null, [], null, Resolvable: false);
        Assert.Empty(ProposalDiff.Edits(ProposalDiff.Compute([hidden], []), true));
    }

    [Fact]
    public async Task The_verifying_stream_fails_at_the_end_when_the_hash_differs()
    {
        var bytes = Encoding.UTF8.GetBytes("hello cad");
        var good = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var ok = new VerifyingStream(new MemoryStream(bytes), good);
        await ok.CopyToAsync(Stream.Null);
        var bad = new VerifyingStream(new MemoryStream(bytes), new string('0', 64));
        await Assert.ThrowsAsync<CadIntegrityException>(() => bad.CopyToAsync(Stream.Null));
    }

    [Fact]
    public void Default_roles_follow_the_document_type_and_roles_derive_staleness()
    {
        Assert.Equal("drawing", CadGraphService.DefaultRole("Drawing"));
        Assert.Equal("model", CadGraphService.DefaultRole("CAD"));
        Assert.True(CadRoles.IsDerived("derived-pdf"));
        Assert.False(CadRoles.IsDerived("model"));
    }

    /// <summary>The CAD module writes audit entries with numbers and counts only: no names, titles or attribute values reach the audit log.</summary>
    [Fact]
    public void Cad_audit_entries_never_name_parts_or_documents_by_their_text()
    {
        var root = FindRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "Keel.Cad"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/", StringComparison.Ordinal)))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"CadGraphService\.Entry\([^;]*;", RegexOptions.Singleline))
                if (Regex.IsMatch(m.Value, @"\(""(name|title|description|attributes?|cadName)""", RegexOptions.IgnoreCase))
                    offenders.Add(Path.GetFileName(file) + ": " + m.Value[..Math.Min(80, m.Value.Length)]);
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    /// <summary>Lists of CAD data go through the read filter; the module reads document and part rows only by key or through the filter.</summary>
    [Fact]
    public void Cad_queries_over_marked_tables_use_the_read_filter_or_a_key_lookup()
    {
        var root = FindRoot();
        var checkedStatements = 0;
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "Keel.Cad"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"(FROM|JOIN)\s+(plm\.part_master|plm\.document_master|cad\.bom_proposal)\b[^\n]*", RegexOptions.IgnoreCase))
            {
                checkedStatements++;
                var statement = text.Substring(m.Index, Math.Min(700, text.Length - m.Index));
                var keyed = Regex.IsMatch(statement, @"WHERE\s+(\w+\.)?(number|id)\s*=\s*@\w+", RegexOptions.IgnoreCase);
                if (!keyed && !Regex.IsMatch(statement, @"\w+\.Sql\}"))
                    offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
            }
        }
        Assert.True(checkedStatements >= 8);
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
