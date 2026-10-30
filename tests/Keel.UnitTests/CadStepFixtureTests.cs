using Keel.Cad.Structure;

namespace Keel.UnitTests;

/// <summary>
/// The STEP structure reader against REAL files written by Open CASCADE (tools/freecad-keel/make_fixtures.py): AP214, AP242 and AP203 variants,
/// parts and assemblies, a sub-assembly used twice, a part used at two levels. Fixtures are under Fixtures/cad.
/// </summary>
public sealed class CadStepFixtureTests
{
    private static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cad", name));

    private static StructureModel Read(string name, string? root = null) => StructureReader.Read("step", Bytes(name), root is null ? null : new StructureReadOptions(root));

    private static StructurePart Part(StructureModel m, string number) => m.Parts.Single(p => p.Number == number);

    private static decimal Qty(StructureModel m, string parent, string child) =>
        m.Lines.Where(l => Part(m, parent).Ref == l.ParentRef && Part(m, child).Ref == l.ChildRef).Sum(l => l.Quantity);

    [Theory]
    [InlineData("bracket.step", "KP-1001", "Bracket, L-shaped")]
    [InlineData("plate.step", "KP-1002", "Mounting plate")]
    [InlineData("bolt.step", "KF-2001", "Screw M5x16")]
    public void A_single_part_file_is_one_product_without_lines(string file, string number, string name)
    {
        var m = Read(file);
        var only = Assert.Single(m.Parts);
        Assert.Equal((number, name), (only.Number, only.Name));
        Assert.Empty(m.Lines);
        Assert.Equal(only.Ref, m.RootRef);
        Assert.NotEqual("Assembly", only.PartType);
    }

    [Fact]
    public void A_two_level_assembly_counts_its_occurrences()
    {
        var m = Read("bracket-asm.step");
        Assert.Equal(["KA-3001", "KF-2001", "KF-2002", "KP-1001"], m.Parts.Select(p => p.Number).Order().ToArray());
        Assert.Equal("KA-3001", m.Parts.Single(p => p.Ref == m.RootRef).Number);
        Assert.Equal(1m, Qty(m, "KA-3001", "KP-1001"));
        Assert.Equal(4m, Qty(m, "KA-3001", "KF-2001"));
        Assert.Equal(4m, Qty(m, "KA-3001", "KF-2002"));
        Assert.Equal(3, m.Lines.Count);
    }

    [Theory]
    [InlineData("frame-asm-ap214.step", "STEP AP214")]
    [InlineData("frame-asm-ap242.step", "STEP AP242")]
    [InlineData("frame-asm-ap203.step", "STEP AP203")]
    public void A_three_level_assembly_reads_the_same_in_every_protocol(string file, string protocol)
    {
        var m = Read(file);
        Assert.Equal(protocol, m.SourceSystem);
        Assert.Equal(["KA-3000", "KA-3001", "KF-2001", "KF-2002", "KP-1001", "KP-1002"], m.Parts.Select(p => p.Number).Order().ToArray());
        Assert.Equal("KA-3000", m.Parts.Single(p => p.Ref == m.RootRef).Number);
        Assert.Equal("Frame assembly", Part(m, "KA-3000").Name);
        Assert.Equal("Assembly", Part(m, "KA-3000").PartType);
        Assert.Equal("Assembly", Part(m, "KA-3001").PartType);
        // the same sub-assembly twice: one product, quantity 2
        Assert.Equal(2m, Qty(m, "KA-3000", "KA-3001"));
        Assert.Equal(1m, Qty(m, "KA-3000", "KP-1002"));
        // a screw used at two levels: 2 directly and 4 per bracket assembly
        Assert.Equal(2m, Qty(m, "KA-3000", "KF-2001"));
        Assert.Equal(4m, Qty(m, "KA-3001", "KF-2001"));
        Assert.Equal(1m, Qty(m, "KA-3001", "KP-1001"));
        Assert.Equal(4m, Qty(m, "KA-3001", "KF-2002"));
        Assert.Equal(6, m.Lines.Count);
        StructureValidator.Validate(m);
    }

    [Fact]
    public void Choosing_a_sub_assembly_as_root_keeps_only_its_subtree()
    {
        var m = Read("frame-asm-ap242.step", "KA-3001");
        Assert.Equal(["KA-3001", "KF-2001", "KF-2002", "KP-1001"], m.Parts.Select(p => p.Number).Order().ToArray());
        Assert.Equal(3, m.Lines.Count);
    }

    [Fact]
    public void The_exploded_total_of_a_part_follows_from_the_lines()
    {
        var m = Read("frame-asm-ap214.step");
        decimal Total(string parentRef, string childRef) =>
            m.Lines.Where(l => l.ParentRef == parentRef).Sum(l => l.ChildRef == childRef ? l.Quantity : l.Quantity * Total(l.ChildRef, childRef));
        Assert.Equal(10m, Total(m.RootRef, Part(m, "KF-2001").Ref)); // 2 + 2 * 4
        Assert.Equal(8m, Total(m.RootRef, Part(m, "KF-2002").Ref)); // 2 * 4
    }

    [Fact]
    public void The_product_counter_agrees_with_the_reader()
    {
        Assert.Equal(6, new StepProductCounter().CountProducts(Bytes("frame-asm-ap242.step")));
    }

    // ---- reader fixes found with the real files (docs/increments/9.2-open-source-cad.md)

    [Fact]
    public void Non_ascii_names_written_as_raw_utf8_by_open_cascade_are_read_correctly()
    {
        var m = Read("unicode-name-ap242.step");
        Assert.Equal("Halterung für Ø5 Welle", Part(m, "KA-4000").Name);
    }

    [Fact]
    public void Two_products_with_one_number_become_one_part_and_one_line()
    {
        var m = Read("two-products-one-number.step");
        Assert.Equal(["KA-5000", "KF-2001"], m.Parts.Select(p => p.Number).Order().ToArray());
        var line = Assert.Single(m.Lines);
        Assert.Equal(3m, line.Quantity);
        StructureValidator.Validate(m);
    }

    // ---- synthetic, hand-written forms of AP242 that Open CASCADE does not write (clearly not real exporter output)

    private static string Synthetic(string data, string schema = "AP242_MANAGED_MODEL_BASED_3D_ENGINEERING_MIM_LF { 1 0 10303 442 1 1 4 }", string dataKeyword = "DATA;", string fileName = "x.stp") => $$"""
        ISO-10303-21;
        HEADER;
        FILE_DESCRIPTION(('synthetic'),'2;1');
        FILE_NAME('{{fileName}}','2026-10-29T00:00:00',('x'),('x'),'p','synthetic','');
        FILE_SCHEMA(('{{schema}}'));
        ENDSEC;
        {{dataKeyword}}
        #10=PRODUCT('TOP','Top','',(#1));
        #11=PRODUCT_DEFINITION_FORMATION('A','',#10);
        #12=PRODUCT_DEFINITION('design','',#11,#1);
        #20=PRODUCT('PIN','Pin','',(#1));
        #21=PRODUCT_DEFINITION_FORMATION('A','',#20);
        #22=PRODUCT_DEFINITION('design','',#21,#1);
        {{data}}
        ENDSEC;
        END-ISO-10303-21;
        """;

    private static StructureModel ReadText(string text, string? root = null) =>
        StructureReader.Read("step", System.Text.Encoding.UTF8.GetBytes(text), root is null ? null : new StructureReadOptions(root));

    [Fact]
    public void An_explicit_count_quantity_is_applied_for_the_simple_and_the_complex_form()
    {
        var simple = ReadText(Synthetic("#30=MEASURE_WITH_UNIT(COUNT_MEASURE(6.),#99);\n#31=QUANTIFIED_ASSEMBLY_COMPONENT_USAGE('1','','',#12,#22,'P1',#30);"));
        Assert.Equal(6m, Assert.Single(simple.Lines).Quantity);
        Assert.Equal(["P1"], Assert.Single(simple.Lines).ReferenceDesignators);

        var complex = ReadText(Synthetic("#30=MEASURE_WITH_UNIT(COUNT_MEASURE(3.),#99);\n#31=( ASSEMBLY_COMPONENT_USAGE($) NEXT_ASSEMBLY_USAGE_OCCURRENCE() PRODUCT_DEFINITION_RELATIONSHIP('7','','',#12,#22) QUANTIFIED_ASSEMBLY_COMPONENT_USAGE(#30) );\n#32=( BOUNDED_CURVE() B_SPLINE_CURVE(2,(#1,#2),.UNSPECIFIED.,.F.,.F.) );"));
        var line = Assert.Single(complex.Lines);
        Assert.Equal(3m, line.Quantity);
        Assert.Equal(7, line.FindNumber);
    }

    [Fact]
    public void An_explicit_quantity_adds_to_plain_occurrences_and_a_non_count_measure_is_noted_not_applied()
    {
        var m = ReadText(Synthetic("#30=MEASURE_WITH_UNIT(COUNT_MEASURE(4.),#99);\n#31=QUANTIFIED_ASSEMBLY_COMPONENT_USAGE('1','','',#12,#22,$,#30);\n#32=NEXT_ASSEMBLY_USAGE_OCCURRENCE('2','','',#12,#22,$);"));
        Assert.Equal(5m, Assert.Single(m.Lines).Quantity);
        var length = ReadText(Synthetic("#30=MEASURE_WITH_UNIT(LENGTH_MEASURE(2.5),#99);\n#31=QUANTIFIED_ASSEMBLY_COMPONENT_USAGE('1','','',#12,#22,$,#30);"));
        var line = Assert.Single(length.Lines);
        Assert.Equal(1m, line.Quantity);
        Assert.Contains("LENGTH_MEASURE", line.Notes);
    }

    [Fact]
    public void An_edition_3_data_section_with_parameters_and_a_second_data_section_are_read()
    {
        var text = Synthetic("#31=NEXT_ASSEMBLY_USAGE_OCCURRENCE('1','','',#12,#22,$);", dataKeyword: "DATA('part',('AP242'));")
            .Replace("ENDSEC;\nEND-ISO", "ENDSEC;\nANCHOR;\nENDSEC;\nDATA('more',('AP242'));\n#32=NEXT_ASSEMBLY_USAGE_OCCURRENCE('2','','',#12,#22,$);\nENDSEC;\nEND-ISO");
        Assert.Equal(2m, Assert.Single(ReadText(text).Lines).Quantity);
    }

    [Fact]
    public void The_protocol_comes_from_the_schema_not_from_other_header_text()
    {
        var m = ReadText(Synthetic("#31=NEXT_ASSEMBLY_USAGE_OCCURRENCE('1','','',#12,#22,$);", schema: "CONFIG_CONTROL_DESIGN", fileName: "bracket-442.stp"));
        Assert.Equal("STEP AP203", m.SourceSystem);
    }

    [Fact]
    public void The_edition_1_escape_for_high_characters_is_decoded()
    {
        var m = ReadText(Synthetic("#31=NEXT_ASSEMBLY_USAGE_OCCURRENCE('1','','',#12,#22,$);").Replace("'Top'", "'Gr\\S\\|\\S\\_e'"));
        Assert.Equal("Grüße", Part(m, "TOP").Name);
    }

    [Fact]
    public void The_kicad_recipe_output_is_read_by_the_csv_importer()
    {
        // tools/cad-recipes/kicad_bom_to_keel_csv.py turned the synthetic kicad-board-bom.csv into this file (pytest asserts the same bytes)
        var m = StructureReader.Read("csv", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cad", "kicad-board-keel.csv")));
        Assert.Equal(5, m.Parts.Count);
        var resistor = m.Lines.Single(l => m.Parts.Single(p => p.Ref == l.ChildRef).Number == "KR-0603-10K");
        Assert.Equal(3m, resistor.Quantity);
        Assert.Equal(["R1", "R2", "R3"], resistor.ReferenceDesignators);
        Assert.Equal(20, resistor.FindNumber);
    }

    [Fact]
    public void Fixtures_stay_small()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cad"), "*.step"))
            Assert.True(new FileInfo(f).Length < 200_000, f);
    }
}
