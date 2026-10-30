using System.Text;
using Keel.Cad.Structure;

namespace Keel.UnitTests;

/// <summary>
/// Placements and bounding boxes read from STEP (ADR-0052, A67-A69): the real Open CASCADE fixtures in every protocol, and small synthetic files for the conventions
/// real files do not exercise (the other order of the two representations, a rotated placement, a missing placement, arcs in both senses).
/// </summary>
public sealed class CadPlacementTests
{
    private static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cad", name));
    private static StructureModel Read(string name) => StructureReader.Read("step", Bytes(name));
    private static StructurePart Part(StructureModel m, string number) => m.Parts.Single(p => p.Number == number);

    private static void Near(double expected, double actual, double tolerance = 1e-6) => Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected}, got {actual}");

    private static (double X, double Y, double Z) Apply(IReadOnlyList<double> m, double x, double y, double z) =>
        (m[0] * x + m[1] * y + m[2] * z + m[3], m[4] * x + m[5] * y + m[6] * z + m[7], m[8] * x + m[9] * y + m[10] * z + m[11]);

    // ---------------------------------------------------------------- real files

    [Theory]
    [InlineData("plate.step", 120, 80, 4)]
    [InlineData("bracket.step", 40, 20, 3)]
    [InlineData("bolt.step", 8, 6.928203, 19)]
    public void A_part_file_has_its_own_box_in_millimetres(string file, double x, double y, double z)
    {
        var m = Read(file);
        Assert.Equal("mm", m.LengthUnit);
        Assert.Equal(1d, m.LengthUnitMm);
        var box = Assert.Single(m.Parts).Box!;
        Near(x, box.SizeX); Near(y, box.SizeY, 1e-4); Near(z, box.SizeZ);
        Assert.Empty(m.Occurrences); // a part has no usages
    }

    [Fact]
    public void Every_occurrence_of_a_two_level_assembly_has_the_placement_the_file_states()
    {
        var m = Read("bracket-asm.step");
        Assert.Equal(9, m.Occurrences.Count);
        Assert.All(m.Occurrences, o => Assert.NotNull(o.Transform));
        var bracket = m.Occurrences.Single(o => o.OccurrenceId == "1");
        Assert.Equal(AssemblyLayouts.Identity, bracket.Transform);
        // the four screws: x = 5, 11, 17, 23, y = 5, z = 3; the four nuts: same x and y, z = -4 (a pure translation; the exporter writes axes along the global axes)
        var screws = m.Occurrences.Where(o => o.ChildRef == Part(m, "KF-2001").Ref).Select(o => o.Transform!).ToList();
        Assert.Equal([5d, 11, 17, 23], screws.Select(t => t[3]).Order().ToArray());
        Assert.All(screws, t => { Near(5, t[7]); Near(3, t[11]); Near(1, t[0]); Near(1, t[5]); Near(1, t[10]); });
        var nuts = m.Occurrences.Where(o => o.ChildRef == Part(m, "KF-2002").Ref).Select(o => o.Transform!).ToList();
        Assert.All(nuts, t => Near(-4, t[11]));
    }

    [Theory]
    [InlineData("frame-asm-ap214.step")]
    [InlineData("frame-asm-ap242.step")]
    [InlineData("frame-asm-ap203.step")]
    public void A_three_level_assembly_expands_to_world_positions_in_every_protocol(string file)
    {
        var m = Read(file);
        Assert.Equal(14, m.Occurrences.Count);
        Assert.All(m.Occurrences, o => Assert.NotNull(o.Transform));
        var layout = AssemblyLayouts.Expand(m);
        Assert.Equal(24, layout.Instances.Count); // the frame, 2 bracket assemblies x (itself + bracket + 4 screws + 4 nuts), the plate and 2 loose screws
        Assert.Equal(0, layout.Unplaced);
        Assert.False(layout.Truncated);
        var second = layout.Instances.Single(i => i.Path == "0/2");
        Assert.Equal("KA-3001", second.Number);
        Near(40, second.World[7]); Near(4, second.World[11]); // the second bracket assembly sits 40 mm along y and 4 mm up
        // a screw of the second bracket: its own (5, 5, 3) plus the assembly's (0, 40, 4)
        var screw = layout.Instances.First(i => i.Path.StartsWith("0/2/", StringComparison.Ordinal) && i.Number == "KF-2001");
        var origin = Apply(screw.World, 0, 0, 0);
        Assert.Equal((5d, 45d, 7d), (Math.Round(origin.X, 6), Math.Round(origin.Y, 6), Math.Round(origin.Z, 6)));
        // the whole frame: the plate (120 x 80 x 4, centred) and the highest screw end define the box
        var box = layout.WorldBox!;
        Near(120, box.SizeX); Near(90, box.SizeY); Near(28, box.SizeZ);
        Near(-40, box.MinY); Near(50, box.MaxY);
        // an assembly's box is the union of what it holds
        Assert.True(layout.Instances.Single(i => i.Path == "0/1").WorldBox!.SizeX >= 40);
    }

    [Fact]
    public void The_three_protocols_agree_on_every_placement()
    {
        var a = Read("frame-asm-ap214.step"); var b = Read("frame-asm-ap242.step"); var c = Read("frame-asm-ap203.step");
        static string[] Key(StructureModel m) => [.. m.Occurrences.Select(o => string.Join(",", o.Transform!))];
        Assert.Equal(Key(a), Key(b));
        Assert.Equal(Key(a), Key(c));
    }

    [Fact]
    public void Placements_can_be_switched_off_and_the_structure_is_the_same()
    {
        var on = StructureReader.Read("step", Bytes("frame-asm-ap214.step"));
        var off = StructureReader.Read("step", Bytes("frame-asm-ap214.step"), new StructureReadOptions(Placements: false));
        Assert.Empty(off.Occurrences);
        Assert.All(off.Parts, p => Assert.Null(p.Box));
        Assert.Equal(on.Parts.Select(p => (p.Ref, p.Number)), off.Parts.Select(p => (p.Ref, p.Number)));
        Assert.Equal(on.Lines.Count, off.Lines.Count);
    }

    // ---------------------------------------------------------------- synthetic files

    private static string Step(string data) =>
        "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }'));\nENDSEC;\nDATA;\n" + data + "\nENDSEC;\nEND-ISO-10303-21;\n";

    private const string Products =
        """
        #10=PRODUCT('ASM','Assembly','',(#99));
        #11=PRODUCT_DEFINITION_FORMATION('','',#10);
        #12=PRODUCT_DEFINITION('design','',#11,#98);
        #20=PRODUCT('CHILD','Child','',(#99));
        #21=PRODUCT_DEFINITION_FORMATION('','',#20);
        #22=PRODUCT_DEFINITION('design','',#21,#98);
        #30=NEXT_ASSEMBLY_USAGE_OCCURRENCE('1','n','',#12,#22,$);
        #31=PRODUCT_DEFINITION_SHAPE('','',#12);
        #32=SHAPE_DEFINITION_REPRESENTATION(#31,#33);
        #33=SHAPE_REPRESENTATION('',(#40),#97);
        #34=PRODUCT_DEFINITION_SHAPE('','',#22);
        #35=SHAPE_DEFINITION_REPRESENTATION(#34,#36);
        #36=SHAPE_REPRESENTATION('',(#41),#97);
        #40=AXIS2_PLACEMENT_3D('',#50,$,$);
        #41=AXIS2_PLACEMENT_3D('',#50,$,$);
        #50=CARTESIAN_POINT('',(0.,0.,0.));
        #51=CARTESIAN_POINT('',(10.,0.,0.));
        #52=DIRECTION('',(0.,0.,1.));
        #53=DIRECTION('',(0.,1.,0.));
        #42=AXIS2_PLACEMENT_3D('',#51,#52,#53);
        #60=PRODUCT_DEFINITION_SHAPE('Placement','',#30);
        #61=CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#62,#60);
        """;

    [Theory]
    [InlineData("#36,#33", "#41,#42")] // Open CASCADE: rep_1 is the child, item_1 its frame
    [InlineData("#33,#36", "#42,#41")] // the other order: rep_1 is the parent
    public void The_two_orders_of_the_representations_give_the_same_rotated_placement(string reps, string items)
    {
        var text = Step(Products + $"\n#62=( REPRESENTATION_RELATIONSHIP('','',{reps}) REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#63) SHAPE_REPRESENTATION_RELATIONSHIP() );\n#63=ITEM_DEFINED_TRANSFORMATION('','',{items});");
        var m = StructureReader.Read("step", Encoding.ASCII.GetBytes(text));
        var t = Assert.Single(m.Occurrences).Transform!;
        // z = (0,0,1), x = the reference direction (0,1,0), so y = (-1,0,0): the child's x axis points along the parent's y axis
        var p = Apply(t, 1, 0, 0);
        Assert.Equal((10d, 1d, 0d), (Math.Round(p.X, 9), Math.Round(p.Y, 9), Math.Round(p.Z, 9)));
        var q = Apply(t, 0, 1, 0);
        Assert.Equal((9d, 0d, 0d), (Math.Round(q.X, 9), Math.Round(q.Y, 9), Math.Round(q.Z, 9)));
    }

    [Fact]
    public void A_simple_representation_relationship_with_transformation_is_understood_too()
    {
        var text = Step(Products + "\n#62=REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION('','',#36,#33,#63);\n#63=ITEM_DEFINED_TRANSFORMATION('','',#41,#42);");
        var t = Assert.Single(StructureReader.Read("step", Encoding.ASCII.GetBytes(text)).Occurrences).Transform!;
        Near(10, t[3]);
    }

    [Fact]
    public void An_occurrence_without_a_usable_placement_has_none_and_is_never_given_an_invented_one()
    {
        // no CONTEXT_DEPENDENT_SHAPE_REPRESENTATION at all
        var none = StructureReader.Read("step", Encoding.ASCII.GetBytes(Step(Products.Replace("#61=CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#62,#60);", ""))));
        Assert.Null(Assert.Single(none.Occurrences).Transform);
        var layout = AssemblyLayouts.Expand(none);
        Assert.Equal(1, layout.Unplaced);
        Assert.False(layout.Instances[1].Placed);
        Assert.Equal(AssemblyLayouts.Identity, layout.Instances[1].World); // at the parent's origin, flagged

        // representations that do not belong to the usage's two products: not guessed
        var foreign = StructureReader.Read("step", Encoding.ASCII.GetBytes(Step(Products +
            "\n#62=( REPRESENTATION_RELATIONSHIP('','',#33,#33) REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#63) SHAPE_REPRESENTATION_RELATIONSHIP() );\n#63=ITEM_DEFINED_TRANSFORMATION('','',#41,#42);")));
        Assert.Null(Assert.Single(foreign.Occurrences).Transform);

        // a placement whose axes are missing or degenerate
        var broken = StructureReader.Read("step", Encoding.ASCII.GetBytes(Step(Products.Replace("#52=DIRECTION('',(0.,0.,1.));", "#52=DIRECTION('',(0.,0.,0.));") +
            "\n#62=( REPRESENTATION_RELATIONSHIP('','',#36,#33) REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#63) SHAPE_REPRESENTATION_RELATIONSHIP() );\n#63=ITEM_DEFINED_TRANSFORMATION('','',#41,#42);")));
        Assert.Null(Assert.Single(broken.Occurrences).Transform);
        var dangling = StructureReader.Read("step", Encoding.ASCII.GetBytes(Step(Products +
            "\n#62=( REPRESENTATION_RELATIONSHIP('','',#36,#33) REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#63) SHAPE_REPRESENTATION_RELATIONSHIP() );\n#63=ITEM_DEFINED_TRANSFORMATION('','',#41,#9999);")));
        Assert.Null(Assert.Single(dangling.Occurrences).Transform);
    }

    private static StructureModel Arc(bool sameSense, string v2 = "#72")
    {
        // one part whose only edge is a circle of radius 5 about the origin from (5,0,0) to (0,5,0): a quarter turn counter-clockwise, or the other three quarters
        var text = Step($$"""
            #10=PRODUCT('P','Arc part','',(#99));
            #11=PRODUCT_DEFINITION_FORMATION('','',#10);
            #12=PRODUCT_DEFINITION('design','',#11,#98);
            #31=PRODUCT_DEFINITION_SHAPE('','',#12);
            #32=SHAPE_DEFINITION_REPRESENTATION(#31,#33);
            #33=ADVANCED_BREP_SHAPE_REPRESENTATION('',(#34),#97);
            #34=MANIFOLD_SOLID_BREP('',#35);
            #35=CLOSED_SHELL('',(#36));
            #36=ADVANCED_FACE('',(#37),#99,.T.);
            #37=FACE_OUTER_BOUND('',#38,.T.);
            #38=EDGE_LOOP('',(#39));
            #39=ORIENTED_EDGE('',*,*,#40,.T.);
            #40=EDGE_CURVE('',#70,{{v2}},#41,{{(sameSense ? ".T." : ".F.")}});
            #41=CIRCLE('',#42,5.);
            #42=AXIS2_PLACEMENT_3D('',#50,$,$);
            #50=CARTESIAN_POINT('',(0.,0.,0.));
            #51=CARTESIAN_POINT('',(5.,0.,0.));
            #52=CARTESIAN_POINT('',(0.,5.,0.));
            #70=VERTEX_POINT('',#51);
            #72=VERTEX_POINT('',#52);
            """);
        return StructureReader.Read("step", Encoding.ASCII.GetBytes(text));
    }

    [Fact]
    public void An_arc_contributes_only_the_part_of_the_circle_between_its_vertices()
    {
        var quarter = Assert.Single(Arc(sameSense: true).Parts).Box!;
        Assert.Equal((0d, 0d, 5d, 5d), (quarter.MinX, quarter.MinY, quarter.MaxX, quarter.MaxY));
        // the same edge against the direction of its curve is the long way round: it reaches all four extremes
        var reverse = Assert.Single(Arc(sameSense: false).Parts).Box!;
        Assert.Equal((-5d, -5d, 5d, 5d), (reverse.MinX, reverse.MinY, reverse.MaxX, reverse.MaxY));
    }

    [Fact]
    public void A_closed_circle_edge_reaches_all_four_extremes()
    {
        var m = Arc(sameSense: true, v2: "#70");
        var b = Assert.Single(m.Parts).Box!;
        Assert.Equal((-5d, -5d, 5d, 5d), (b.MinX, b.MinY, b.MaxX, b.MaxY));
    }

    [Fact]
    public void A_file_with_a_mixed_or_unknown_length_unit_says_so_by_having_none()
    {
        var m = StructureReader.Read("step", Bytes("plate.step"));
        Assert.NotNull(m.LengthUnit);
        var mixed = Step(Products + "\n#97=( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.) );\n#96=( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT($,.METRE.) );");
        Assert.Null(StructureReader.Read("step", Encoding.ASCII.GetBytes(mixed)).LengthUnit);
        var inches = Step(Products + "\n#97=( CONVERSION_BASED_UNIT('INCH',#5) LENGTH_UNIT() NAMED_UNIT(#6) );");
        var im = StructureReader.Read("step", Encoding.ASCII.GetBytes(inches));
        Assert.Equal(("in", 25.4), (im.LengthUnit, im.LengthUnitMm));
    }

    [Fact]
    public void Hostile_geometry_does_not_throw_or_hang()
    {
        // a loop of faces referring to each other and a huge radius: the walk is iterative and visits each entity once
        var text = Step("""
            #10=PRODUCT('P','Loop','',(#99));
            #11=PRODUCT_DEFINITION_FORMATION('','',#10);
            #12=PRODUCT_DEFINITION('design','',#11,#98);
            #31=PRODUCT_DEFINITION_SHAPE('','',#12);
            #32=SHAPE_DEFINITION_REPRESENTATION(#31,#33);
            #33=SHAPE_REPRESENTATION('',(#34),#97);
            #34=CLOSED_SHELL('',(#35));
            #35=CLOSED_SHELL('',(#34,#34,#35));
            """);
        var m = StructureReader.Read("step", Encoding.ASCII.GetBytes(text));
        Assert.Null(Assert.Single(m.Parts).Box);
    }
}
