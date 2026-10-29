using System.IO.Compression;
using System.Text;
using Keel.Core.Geometry;

namespace Keel.UnitTests;

/// <summary>The in-house STL, OBJ and 3MF readers, the mesh analyzer and the fingerprint (ADR-0046): known shapes, re-exports, hostile and degenerate input.</summary>
public sealed class ModelGeometryTests
{
    private static readonly MeshLimits Limits = MeshLimits.Default;

    private static ModelMetrics Measure(string name, byte[] bytes, MeshLimits? limits = null) =>
        ModelAnalysis.Analyze(name, bytes, bytes.Length, limits ?? Limits);

    private static void Near(double expected, double? actual, double relative = 1e-9)
    {
        Assert.NotNull(actual);
        Assert.True(Math.Abs(expected - actual.Value) <= Math.Max(1e-9, Math.Abs(expected) * relative), $"expected {expected}, got {actual}");
    }

    // ---------------------------------------------------------------- known shapes

    [Fact]
    public void A_cube_has_exact_size_area_and_volume_and_is_watertight()
    {
        var m = Measure("cube.stl", SyntheticMesh.ToBinaryStl(SyntheticMesh.Box(10, 10, 10)));
        Assert.Equal("analysed", m.Status);
        Assert.Equal(12, m.Triangles);
        Assert.Equal(8, m.Vertices);
        Assert.Equal(0, m.DegenerateTriangles);
        Near(10, m.SizeX); Near(10, m.SizeY); Near(10, m.SizeZ);
        Near(600, m.SurfaceArea);
        Near(1000, m.SignedVolume, 1e-6);
        Assert.True(m.Watertight);
        Assert.Equal(64, m.GeometryFingerprint!.Length);
    }

    [Fact]
    public void A_box_away_from_the_origin_keeps_its_volume()
    {
        var m = Measure("box.stl", SyntheticMesh.ToBinaryStl(SyntheticMesh.Box(2, 3, 4, 1000, -500, 250)));
        Near(24, m.Volume, 1e-4);
        Near(52, m.SurfaceArea, 1e-6);
        Near(1000, m.MinX); Near(-500, m.MinY); Near(250, m.MinZ);
    }

    [Fact]
    public void A_sphere_approximation_is_close_to_the_analytic_values_and_the_polyhedron_is_inside_the_sphere()
    {
        const double r = 10;
        var m = Measure("sphere.stl", SyntheticMesh.ToBinaryStl(SyntheticMesh.Sphere(r, 96, 48)));
        Assert.True(m.Watertight);
        var exact = 4.0 / 3.0 * Math.PI * r * r * r;
        Assert.InRange(m.Volume!.Value, exact * 0.99, exact);
        var area = 4 * Math.PI * r * r;
        Assert.InRange(m.SurfaceArea!.Value, area * 0.99, area);
        Assert.InRange(m.SizeX!.Value, 19.9, 20.0001);
        Assert.InRange(m.SizeZ!.Value, 19.99, 20.0001);
        Assert.True(m.SignedVolume > 0, "outward-facing triangles give a positive signed volume");
    }

    [Fact]
    public void A_bracket_has_the_volume_its_dimensions_give()
    {
        var m = Measure("bracket.stl", SyntheticMesh.ToBinaryStl(SyntheticMesh.Bracket(40, 30, 4, 20)));
        Assert.True(m.Watertight);
        Near((40 * 4 + 4 * (30 - 4)) * 20.0, m.Volume, 1e-6);
        Near(20, m.SizeX); Near(40, m.SizeY); Near(30, m.SizeZ);
    }

    [Fact]
    public void An_open_mesh_is_not_watertight_and_claims_no_volume()
    {
        var open = SyntheticMesh.Box(10, 10, 10).Skip(2).ToList(); // the bottom face is missing
        var m = Measure("open.stl", SyntheticMesh.ToBinaryStl(open));
        Assert.Equal(false, m.Watertight);
        Assert.Null(m.SignedVolume);
        Assert.Null(m.Volume);
        Near(500, m.SurfaceArea);
    }

    [Fact]
    public void A_mesh_with_one_flipped_triangle_is_not_consistently_oriented_so_it_is_not_watertight()
    {
        var tris = SyntheticMesh.Box(10, 10, 10).ToList();
        var t = tris[0];
        tris[0] = new SyntheticMesh.Tri(t.Ax, t.Ay, t.Az, t.Cx, t.Cy, t.Cz, t.Bx, t.By, t.Bz);
        Assert.Equal(false, Measure("flipped.stl", SyntheticMesh.ToBinaryStl(tris)).Watertight);
    }

    [Fact]
    public void An_inside_out_closed_mesh_has_a_negative_signed_volume_and_a_positive_volume()
    {
        var inverted = SyntheticMesh.Box(10, 10, 10).Select(t => new SyntheticMesh.Tri(t.Ax, t.Ay, t.Az, t.Cx, t.Cy, t.Cz, t.Bx, t.By, t.Bz));
        var m = Measure("inside-out.stl", SyntheticMesh.ToBinaryStl(inverted));
        Assert.True(m.Watertight);
        Near(-1000, m.SignedVolume, 1e-6);
        Near(1000, m.Volume, 1e-6);
    }

    [Fact]
    public void A_triangle_that_collapses_to_a_point_is_counted_as_degenerate()
    {
        var tris = SyntheticMesh.Box(10, 10, 10).Append(new SyntheticMesh.Tri(1, 1, 1, 1, 1, 1, 1, 1, 1)).ToList();
        var m = Measure("point.stl", SyntheticMesh.ToBinaryStl(tris));
        Assert.Equal(13, m.Triangles);
        Assert.Equal(1, m.DegenerateTriangles);
        Assert.Equal(Measure("clean.stl", SyntheticMesh.ToBinaryStl(SyntheticMesh.Box(10, 10, 10))).GeometryFingerprint, m.GeometryFingerprint);
    }

    // ---------------------------------------------------------------- fingerprint

    [Fact]
    public void The_fingerprint_is_the_same_for_binary_and_ascii_exports_triangle_order_and_corner_order()
    {
        var tris = SyntheticMesh.Bracket(40, 30, 4, 20);
        var binary = Measure("b.stl", SyntheticMesh.ToBinaryStl(tris));
        var ascii = Measure("a.stl", SyntheticMesh.ToAsciiStl(tris, "0.0000000"));
        var shuffled = tris.OrderByDescending(t => t.Ax + 7 * t.Ay + 13 * t.Az).Select(t => new SyntheticMesh.Tri(t.Cx, t.Cy, t.Cz, t.Ax, t.Ay, t.Az, t.Bx, t.By, t.Bz)).ToList();
        var reordered = Measure("r.stl", SyntheticMesh.ToBinaryStl(shuffled));
        Assert.Equal(binary.GeometryFingerprint, ascii.GeometryFingerprint);
        Assert.Equal(binary.GeometryFingerprint, reordered.GeometryFingerprint);
        Assert.Equal(binary.Triangles, reordered.Triangles);
    }

    [Fact]
    public void The_same_triangles_as_OBJ_and_as_STL_are_recognised_as_the_same_geometry()
    {
        var stl = Measure("cube.stl", SyntheticMesh.ToBinaryStl(SyntheticMesh.Box(10, 10, 10)));
        var obj = Measure("cube.obj", Encoding.ASCII.GetBytes(TrianglesAsObj(SyntheticMesh.Box(10, 10, 10))));
        Assert.Equal(stl.GeometryFingerprint, obj.GeometryFingerprint);
        Near(1000, obj.Volume, 1e-6);
        // the same cube with quad faces is triangulated along other diagonals, so its triangles (and fingerprint) differ although the numbers agree
        var quads = Measure("quads.obj", Encoding.ASCII.GetBytes(CubeObj(10)));
        Near(1000, quads.Volume, 1e-6);
    }

    [Fact]
    public void A_different_shape_has_a_different_fingerprint_even_when_it_is_almost_the_same()
    {
        var a = Measure("a.stl", SyntheticMesh.ToBinaryStl(SyntheticMesh.Bracket(40, 30, 4, 20)));
        var taller = Measure("b.stl", SyntheticMesh.ToBinaryStl(SyntheticMesh.Bracket(40, 30.2, 4, 20)));
        var moved = Measure("c.stl", SyntheticMesh.ToBinaryStl(SyntheticMesh.Bracket(40, 30, 4, 20).Select(t => new SyntheticMesh.Tri(t.Ax + 1, t.Ay, t.Az, t.Bx + 1, t.By, t.Bz, t.Cx + 1, t.Cy, t.Cz))));
        Assert.NotEqual(a.GeometryFingerprint, taller.GeometryFingerprint);
        Assert.NotEqual(a.GeometryFingerprint, moved.GeometryFingerprint);
    }

    [Fact]
    public void A_missing_triangle_changes_the_fingerprint()
    {
        var all = SyntheticMesh.Box(10, 10, 10);
        Assert.NotEqual(Measure("a.stl", SyntheticMesh.ToBinaryStl(all)).GeometryFingerprint, Measure("b.stl", SyntheticMesh.ToBinaryStl(all.Skip(1))).GeometryFingerprint);
    }

    // ---------------------------------------------------------------- STL: hostile and degenerate input

    [Fact]
    public void Garbage_and_tiny_files_are_refused_with_a_plain_message()
    {
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[] { 1, 2, 3 }, new byte[200], Encoding.ASCII.GetBytes("this is not an stl file at all, nothing to see") })
        {
            var m = Measure("x.stl", bytes);
            Assert.Equal("failed", m.Status);
            Assert.False(string.IsNullOrWhiteSpace(m.Reason));
        }
    }

    [Fact]
    public void A_binary_header_that_claims_far_more_triangles_than_the_file_holds_is_not_trusted()
    {
        var bytes = new byte[84 + 50];
        BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, 80);
        Assert.Equal("failed", Measure("liar.stl", bytes).Status);
        BitConverter.GetBytes(5_000_000u).CopyTo(bytes, 80);
        Assert.Equal("failed", Measure("liar2.stl", bytes).Status);
    }

    [Fact]
    public void A_binary_stl_over_the_triangle_limit_is_refused_before_it_is_read()
    {
        var bytes = SyntheticMesh.ToBinaryStl(SyntheticMesh.Box(1, 1, 1));
        var m = Measure("big.stl", bytes, new MeshLimits { MaxTriangles = 10 });
        Assert.Equal("failed", m.Status);
        Assert.Contains("triangles", m.Reason!);
    }

    [Fact]
    public void Non_finite_and_enormous_coordinates_are_refused()
    {
        var bytes = SyntheticMesh.ToBinaryStl(SyntheticMesh.Box(1, 1, 1));
        BitConverter.GetBytes(float.NaN).CopyTo(bytes, 84 + 12);
        Assert.Equal("failed", Measure("nan.stl", bytes).Status);
        bytes = SyntheticMesh.ToBinaryStl(SyntheticMesh.Box(1, 1, 1));
        BitConverter.GetBytes(float.PositiveInfinity).CopyTo(bytes, 84 + 12 + 4);
        Assert.Equal("failed", Measure("inf.stl", bytes).Status);
        bytes = SyntheticMesh.ToBinaryStl(SyntheticMesh.Box(1, 1, 1));
        BitConverter.GetBytes(3e20f).CopyTo(bytes, 84 + 12);
        Assert.Equal("failed", Measure("huge.stl", bytes).Status);
    }

    [Fact]
    public void Ascii_stl_with_a_broken_vertex_a_partial_triangle_or_a_huge_token_is_refused()
    {
        Assert.Equal("failed", Measure("a.stl", Encoding.ASCII.GetBytes("solid x\nfacet normal 0 0 0\nouter loop\nvertex 1 2\nendloop\nendfacet\nendsolid x\n")).Status);
        Assert.Equal("failed", Measure("b.stl", Encoding.ASCII.GetBytes("solid x\nvertex 0 0 0\nvertex 1 0 0\nendsolid x\n")).Status);
        Assert.Equal("failed", Measure("c.stl", Encoding.ASCII.GetBytes("solid x\nvertex 0 0 0\nvertex 1 0 abc\nvertex 0 1 0\nendsolid x\n")).Status);
        Assert.Equal("failed", Measure("d.stl", Encoding.ASCII.GetBytes("solid " + new string('x', 200_000))).Status);
        Assert.Equal("failed", Measure("e.stl", Encoding.ASCII.GetBytes("solid empty\nendsolid empty\n")).Status);
        Assert.Equal("failed", Measure("f.stl", Encoding.ASCII.GetBytes("solid x\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 NaN\nendsolid x\n")).Status);
    }

    [Fact]
    public void Ascii_stl_over_the_triangle_limit_stops_reading()
    {
        var text = SyntheticMesh.ToAsciiStl(SyntheticMesh.Sphere(5, 24, 12));
        var m = Measure("many.stl", text, new MeshLimits { MaxTriangles = 100 });
        Assert.Equal("failed", m.Status);
    }

    [Fact]
    public void A_file_over_the_size_limit_is_not_read_at_all()
    {
        var m = ModelAnalysis.Analyze("huge.stl", ReadOnlyMemory<byte>.Empty, 500L * 1024 * 1024, Limits);
        Assert.Equal("failed", m.Status);
        Assert.Contains("too large", m.Reason!);
    }

    [Fact]
    public void Ascii_stl_accepts_a_byte_order_mark_scientific_notation_and_windows_line_ends()
    {
        var text = "﻿solid x\r\nfacet normal 0 0 1\r\nouter loop\r\nvertex 0E0 0 0\r\nvertex 1.0e+1 0 0\r\nvertex 0 1E1 0\r\nendloop\r\nendfacet\r\nendsolid x\r\n";
        var m = Measure("crlf.stl", new UTF8Encoding(true).GetBytes(text));
        Assert.Equal("analysed", m.Status);
        Assert.Equal(1, m.Triangles);
        Near(50, m.SurfaceArea);
        Assert.Equal(false, m.Watertight);
    }

    // ---------------------------------------------------------------- OBJ

    private static string CubeObj(double s)
    {
        var sb = new StringBuilder("# cube\no cube\n");
        foreach (var (x, y, z) in new[] { (0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0), (0, 0, 1), (1, 0, 1), (1, 1, 1), (0, 1, 1) })
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"v {x * s} {y * s} {z * s}\n");
        sb.Append("vn 0 0 1\nvt 0 0\n");
        sb.Append("f 1//1 4//1 3//1 2//1\nf 5/1/1 6/1/1 7/1/1 8/1/1\nf 1 2 6 5\nf 2 3 7 6\nf 3 4 8 7\nf 4 1 5 8\n");
        return sb.ToString();
    }

    private static string TrianglesAsObj(IEnumerable<SyntheticMesh.Tri> tris)
    {
        var sb = new StringBuilder();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var n = 0;
        foreach (var t in tris)
        {
            sb.Append(inv, $"v {t.Ax} {t.Ay} {t.Az}\nv {t.Bx} {t.By} {t.Bz}\nv {t.Cx} {t.Cy} {t.Cz}\nf {n + 1} {n + 2} {n + 3}\n");
            n += 3;
        }
        return sb.ToString();
    }

    [Fact]
    public void An_obj_cube_with_quads_and_slash_forms_is_triangulated_and_measured()
    {
        var m = Measure("cube.obj", Encoding.ASCII.GetBytes(CubeObj(10)));
        Assert.Equal(12, m.Triangles);
        Assert.Equal(8, m.Vertices);
        Assert.True(m.Watertight);
        Near(1000, m.Volume, 1e-6);
        Assert.Equal("y", m.Up);
    }

    [Fact]
    public void An_obj_with_negative_indices_is_read_relative_to_the_vertices_seen_so_far()
    {
        var m = Measure("tri.obj", Encoding.ASCII.GetBytes("v 0 0 0\nv 4 0 0\nv 0 3 0\nf -3 -2 -1\n"));
        Assert.Equal(1, m.Triangles);
        Near(6, m.SurfaceArea);
    }

    [Theory]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 9\n")]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 0\n")]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2\n")]
    [InlineData("v 0 0 0\nv 1 0 nan\nv 0 1 0\nf 1 2 3\n")]
    [InlineData("v 0 0 0\nv 1 0\nv 0 1 0\nf 1 2 3\n")]
    [InlineData("v 0 0 0\nf a b c\n")]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf -9 -2 -1\n")]
    [InlineData("# nothing but a comment\n")]
    [InlineData("v 1e30 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n")]
    public void Broken_obj_files_are_refused(string text)
    {
        var m = Measure("bad.obj", Encoding.ASCII.GetBytes(text));
        Assert.Equal("failed", m.Status);
    }

    [Fact]
    public void An_obj_over_the_vertex_triangle_or_line_limits_is_refused()
    {
        Assert.Equal("failed", Measure("a.obj", Encoding.ASCII.GetBytes(CubeObj(1)), new MeshLimits { MaxTriangles = 5 }).Status);
        Assert.Equal("failed", Measure("b.obj", Encoding.ASCII.GetBytes(CubeObj(1)), new MeshLimits { MaxVertices = 4 }).Status);
        Assert.Equal("failed", Measure("c.obj", Encoding.ASCII.GetBytes("v " + new string('1', 100_000) + " 0 0\n")).Status);
        var fan = "v 0 0 0\n" + string.Concat(Enumerable.Repeat("v 1 1 1\n", 5000)) + "f " + string.Join(' ', Enumerable.Range(1, 5000)) + "\n";
        Assert.Equal("failed", Measure("d.obj", Encoding.ASCII.GetBytes(fan)).Status);
    }

    // ---------------------------------------------------------------- 3MF

    private static byte[] ThreeMf(string model, string? rels = null, Action<ZipArchive>? extra = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            Add(zip, "[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"model\" ContentType=\"application/vnd.ms-package.3dmanufacturing-3dmodel+xml\"/></Types>");
            Add(zip, "_rels/.rels", rels ?? "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Target=\"/3D/3dmodel.model\" Id=\"rel0\" Type=\"http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel\"/></Relationships>");
            Add(zip, "3D/3dmodel.model", model);
            extra?.Invoke(zip);
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private const string CubeMesh = """
        <mesh><vertices>
        <vertex x="0" y="0" z="0"/><vertex x="1" y="0" z="0"/><vertex x="1" y="1" z="0"/><vertex x="0" y="1" z="0"/>
        <vertex x="0" y="0" z="1"/><vertex x="1" y="0" z="1"/><vertex x="1" y="1" z="1"/><vertex x="0" y="1" z="1"/>
        </vertices><triangles>
        <triangle v1="0" v2="2" v3="1"/><triangle v1="0" v2="3" v3="2"/>
        <triangle v1="4" v2="5" v3="6"/><triangle v1="4" v2="6" v3="7"/>
        <triangle v1="0" v2="1" v3="5"/><triangle v1="0" v2="5" v3="4"/>
        <triangle v1="1" v2="2" v3="6"/><triangle v1="1" v2="6" v3="5"/>
        <triangle v1="2" v2="3" v3="7"/><triangle v1="2" v2="7" v3="6"/>
        <triangle v1="3" v2="0" v3="4"/><triangle v1="3" v2="4" v3="7"/>
        </triangles></mesh>
        """;

    private static string Model(string resources, string build, string unit = "millimeter") =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><model unit=\"{unit}\" xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\"><resources>{resources}</resources><build>{build}</build></model>";

    [Fact]
    public void A_3mf_cube_is_read_converted_to_millimetres_and_measured()
    {
        var bytes = ThreeMf(Model($"<object id=\"1\" type=\"model\">{CubeMesh}</object>", "<item objectid=\"1\" transform=\"10 0 0 0 10 0 0 0 10 5 0 0\"/>", "millimeter"));
        var m = Measure("cube.3mf", bytes);
        Assert.Equal("analysed", m.Status);
        Assert.Equal(12, m.Triangles);
        Assert.True(m.Watertight);
        Near(1000, m.Volume, 1e-6);
        Near(5, m.MinX);
        Assert.Equal("mm", m.Unit);
        Near(600, m.SurfaceArea);
    }

    [Fact]
    public void A_3mf_unit_is_converted_to_millimetres()
    {
        var bytes = ThreeMf(Model($"<object id=\"1\">{CubeMesh}</object>", "<item objectid=\"1\"/>", "centimeter"));
        var m = Measure("cm.3mf", bytes);
        Near(10, m.SizeX);
        Near(1000, m.Volume, 1e-6);
    }

    [Fact]
    public void Components_are_expanded_with_their_transforms_and_a_mirrored_instance_keeps_a_positive_volume()
    {
        var model = Model(
            $"<object id=\"1\">{CubeMesh}</object><object id=\"2\"><components><component objectid=\"1\"/><component objectid=\"1\" transform=\"-1 0 0 0 1 0 0 0 1 5 0 0\"/></components></object>",
            "<item objectid=\"2\"/>");
        var m = Measure("assembly.3mf", ThreeMf(model));
        Assert.Equal(24, m.Triangles);
        Near(2, m.Volume, 1e-6);
        Assert.True(m.Volume > 0 && m.SignedVolume > 0, "the mirrored copy is re-wound so both copies add up");
        Near(5, m.MaxX);
        Near(0, m.MinX);
    }

    [Fact]
    public void Objects_without_a_build_section_are_all_included()
    {
        var model = $"<?xml version=\"1.0\"?><model xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\"><resources><object id=\"7\">{CubeMesh}</object></resources></model>";
        Assert.Equal(12, Measure("nobuild.3mf", ThreeMf(model)).Triangles);
    }

    [Fact]
    public void A_component_that_refers_to_itself_or_nests_too_deeply_is_refused_without_recursion()
    {
        var self = Model("<object id=\"1\"><components><component objectid=\"1\"/></components></object>", "<item objectid=\"1\"/>");
        var m = Measure("loop.3mf", ThreeMf(self));
        Assert.Equal("failed", m.Status);
        var pair = Model("<object id=\"1\"><components><component objectid=\"2\"/></components></object><object id=\"2\"><components><component objectid=\"1\"/></components></object>", "<item objectid=\"1\"/>");
        Assert.Equal("failed", Measure("loop2.3mf", ThreeMf(pair)).Status);
    }

    [Fact]
    public void A_component_bomb_hits_the_instance_limit()
    {
        // 30 objects each with two components of the next one: 2^30 instances if followed.
        var sb = new StringBuilder($"<object id=\"0\">{CubeMesh}</object>");
        for (var i = 1; i <= 30; i++) sb.Append($"<object id=\"{i}\"><components><component objectid=\"{i - 1}\"/><component objectid=\"{i - 1}\"/></components></object>");
        var m = Measure("bomb.3mf", ThreeMf(Model(sb.ToString(), "<item objectid=\"30\"/>")), new MeshLimits { MaxComponentDepth = 64 });
        Assert.Equal("failed", m.Status);
    }

    [Fact]
    public void A_zip_bomb_part_is_cut_off_at_the_limit_whatever_the_header_says()
    {
        var padding = new string(' ', 5_000_000);
        var model = Model($"<object id=\"1\">{CubeMesh}</object>", "<item objectid=\"1\"/>") + padding;
        var bytes = ThreeMf(model);
        Assert.True(bytes.Length < 100_000, "the padding compresses to almost nothing");
        var m = Measure("bomb.3mf", bytes, new MeshLimits { MaxUncompressedBytes = 100_000 });
        Assert.Equal("failed", m.Status);
        Assert.Contains("Keel reads", m.Reason!);
    }

    [Fact]
    public void A_zip_header_that_understates_the_size_does_not_get_past_the_limit_either()
    {
        var padding = new string(' ', 5_000_000);
        var bytes = ThreeMf(Model($"<object id=\"1\">{CubeMesh}</object>", "<item objectid=\"1\"/>") + padding);
        // central directory entries start with PK\x01\x02; the uncompressed size is at offset 24
        for (var i = 0; i + 28 < bytes.Length; i++)
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 1 && bytes[i + 3] == 2) BitConverter.GetBytes(100u).CopyTo(bytes, i + 24);
        var m = Measure("lying.3mf", bytes, new MeshLimits { MaxUncompressedBytes = 100_000 });
        Assert.Equal("failed", m.Status);
    }

    [Fact]
    public void A_3mf_with_a_doctype_is_refused_and_nothing_is_resolved()
    {
        var model = "<?xml version=\"1.0\"?><!DOCTYPE model [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><model xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\"><resources><object id=\"1\"><mesh><vertices><vertex x=\"&xxe;\" y=\"0\" z=\"0\"/></vertices></mesh></object></resources></model>";
        var m = Measure("xxe.3mf", ThreeMf(model));
        Assert.Equal("failed", m.Status);
        Assert.DoesNotContain("root:", m.Reason!);
    }

    [Fact]
    public void Broken_3mf_packages_are_refused()
    {
        Assert.Equal("failed", Measure("a.3mf", Encoding.ASCII.GetBytes("PK not really a zip")).Status);
        Assert.Equal("failed", Measure("b.3mf", ThreeMf("<model>")).Status);
        Assert.Equal("failed", Measure("c.3mf", ThreeMf(Model("<object id=\"1\"><mesh><vertices><vertex x=\"0\" y=\"0\" z=\"0\"/></vertices><triangles><triangle v1=\"0\" v2=\"1\" v3=\"2\"/></triangles></mesh></object>", "<item objectid=\"1\"/>"))).Status);
        Assert.Equal("failed", Measure("d.3mf", ThreeMf(Model("<object id=\"1\"/>", "<item objectid=\"9\"/>"))).Status);
        Assert.Equal("failed", Measure("e.3mf", ThreeMf(Model($"<object id=\"1\">{CubeMesh}</object>", "<item objectid=\"1\" transform=\"1 2 3\"/>"))).Status);
        Assert.Equal("failed", Measure("f.3mf", ThreeMf(Model($"<object id=\"1\">{CubeMesh}</object>", "<item objectid=\"1\"/>", "furlong"))).Status);
        Assert.Equal("failed", Measure("g.3mf", ThreeMf(Model($"<object id=\"1\">{CubeMesh}</object><object id=\"1\">{CubeMesh}</object>", "<item objectid=\"1\"/>"))).Status);
        Assert.Equal("failed", Measure("h.3mf", ThreeMf(Model($"<object id=\"1\">{CubeMesh}</object>", "<item objectid=\"1\" path=\"/3D/other.model\"/>"))).Status);
        Assert.Equal("failed", Measure("i.3mf", ThreeMf(Model("<object id=\"1\"><mesh><vertices><vertex x=\"1\" y=\"1\" z=\"NaN\"/></vertices></mesh></object>", "<item objectid=\"1\"/>"))).Status);
    }

    [Fact]
    public void A_3mf_with_too_many_zip_entries_is_refused()
    {
        var bytes = ThreeMf(Model($"<object id=\"1\">{CubeMesh}</object>", "<item objectid=\"1\"/>"), extra: zip => { for (var i = 0; i < 20; i++) Add(zip, $"Metadata/{i}.txt", "x"); });
        Assert.Equal("failed", Measure("many.3mf", bytes, new MeshLimits { MaxZipEntries = 10 }).Status);
        Assert.Equal("analysed", Measure("many.3mf", bytes).Status);
    }

    // ---------------------------------------------------------------- other formats: file facts only, never invented numbers

    [Fact]
    public void Step_and_gltf_get_file_facts_only_and_no_geometry_numbers()
    {
        foreach (var name in new[] { "part.step", "part.stp", "model.glb", "scene.gltf", "part.iges" })
        {
            var m = ModelAnalysis.Analyze(name, ReadOnlyMemory<byte>.Empty, 1234, Limits);
            Assert.Equal("facts_only", m.Status);
            Assert.Null(m.Triangles); Assert.Null(m.SignedVolume); Assert.Null(m.SurfaceArea); Assert.Null(m.MinX); Assert.Null(m.GeometryFingerprint); Assert.Null(m.Watertight);
            Assert.Contains("only file facts", m.Reason!);
        }
    }

    private sealed class FixedCounter(int? n) : IStepProductCounter
    {
        public int? CountProducts(ReadOnlyMemory<byte> content) => n;
    }

    [Fact]
    public void A_step_product_count_is_reported_only_when_the_counter_read_it()
    {
        var bytes = new byte[] { 1 };
        Assert.Equal(3, ModelAnalysis.Analyze("a.step", bytes, 1, Limits, new FixedCounter(3)).StepProducts);
        var unread = ModelAnalysis.Analyze("a.step", bytes, 1, Limits, new FixedCounter(null));
        Assert.Null(unread.StepProducts);
        Assert.Contains("could not be read", unread.Reason!);
        Assert.Null(ModelAnalysis.Analyze("a.step", bytes, 1, Limits).StepProducts);
    }

    [Theory]
    [InlineData("a.STL", "stl")]
    [InlineData("a.obj", "obj")]
    [InlineData("a.3mf", "3mf")]
    [InlineData("a.stp", "step")]
    [InlineData("a.glb", "glb")]
    [InlineData("a.gltf", "gltf")]
    [InlineData("a.igs", "iges")]
    public void Model_formats_are_recognised_by_extension(string name, string format) => Assert.Equal(format, ModelFormats.FromFileName(name));

    [Fact]
    public void Other_files_are_not_models() => Assert.All(new[] { "a.pdf", "a", "", "a.txt", ".stl.bak" }, n => Assert.False(ModelFormats.IsModel(n)));

    // ---------------------------------------------------------------- delta and wording

    private static ModelMetrics Sized(double x, double y, double z, double volume, double area = 1000, long triangles = 100, string up = "z", string unit = "mm") => new()
    {
        Status = "analysed", Format = "stl", Unit = unit, Up = up, Triangles = triangles, Vertices = 50, MinX = 0, MinY = 0, MinZ = 0, MaxX = x, MaxY = y, MaxZ = z,
        SurfaceArea = area, SignedVolume = volume, Watertight = true, GeometryFingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
    };

    [Fact]
    public void The_summary_says_taller_and_volume_down_in_plain_words()
    {
        var delta = ModelDeltas.Compare(Sized(20, 40, 50, 1000), Sized(20, 40, 52, 959), sameBytes: false);
        Assert.Equal("Taller by 2.0 mm, volume down 4.1 %", delta.Summary);
        Assert.False(delta.SameGeometry);
        var z = delta.Rows.Single(r => r.Key == "sizeZ");
        Near(2, z.Change); Near(4, z.Percent);
        var v = delta.Rows.Single(r => r.Key == "volume");
        Near(-4.1, v.Percent, 1e-9);
    }

    [Fact]
    public void The_words_follow_the_up_axis_of_the_format()
    {
        var zUp = ModelDeltas.Compare(Sized(20, 40, 50, 1000), Sized(22, 38, 50, 1000), false).Summary;
        Assert.Equal("Wider by 2.0 mm, shallower by 2.0 mm", zUp);
        var yUp = ModelDeltas.Compare(Sized(20, 40, 50, 1000, up: "y"), Sized(20, 42, 48, 1000, up: "y"), false).Summary;
        Assert.Equal("Taller by 2.0 mm, shallower by 2.0 mm", yUp);
        Assert.Equal("Narrower by 2.0 mm, shorter by 2.0 mm", ModelDeltas.Compare(Sized(20, 40, 50, 1000), Sized(18, 40, 48, 1000), false).Summary);
    }

    [Fact]
    public void Small_changes_use_three_decimals_and_noise_is_not_reported()
    {
        Assert.Equal("Taller by 0.050 mm", ModelDeltas.Compare(Sized(20, 40, 50, 1000), Sized(20, 40, 50.05, 1000), false).Summary);
        Assert.StartsWith("No measurable difference", ModelDeltas.Compare(Sized(20, 40, 50, 1000), Sized(20, 40, 50.0000001, 1000.00001), false).Summary);
    }

    [Fact]
    public void Units_other_than_millimetres_are_not_called_millimetres()
    {
        Assert.Equal("Taller by 2.0 units", ModelDeltas.Compare(Sized(20, 40, 50, 1000, unit: "units"), Sized(20, 40, 52, 1000, unit: "units"), false).Summary);
    }

    [Fact]
    public void Open_meshes_get_no_volume_phrase_and_a_note()
    {
        var a = Sized(20, 40, 50, 0) with { SignedVolume = null, Watertight = false };
        var b = Sized(20, 40, 52, 0) with { SignedVolume = null, Watertight = false };
        var delta = ModelDeltas.Compare(a, b, false);
        Assert.Equal("Taller by 2.0 mm", delta.Summary.Split(',')[0]);
        Assert.DoesNotContain("volume", delta.Summary);
        Assert.NotNull(delta.Rows.Single(r => r.Key == "volume").Note);
        Assert.Null(delta.Rows.Single(r => r.Key == "volume").A);
    }

    [Fact]
    public void Identical_bytes_and_a_re_export_of_the_same_shape_are_told_apart()
    {
        var tris = SyntheticMesh.Bracket(40, 30, 4, 20);
        var binary = Measure("a.stl", SyntheticMesh.ToBinaryStl(tris));
        var ascii = Measure("a.stl", SyntheticMesh.ToAsciiStl(tris));
        Assert.Equal("The two versions are the same file.", ModelDeltas.Compare(binary, binary, true).Summary);
        var reexport = ModelDeltas.Compare(binary, ascii, false);
        Assert.True(reexport.SameGeometry);
        Assert.StartsWith("Same geometry", reexport.Summary);
    }

    [Fact]
    public void Triangle_count_changes_are_mentioned()
    {
        var delta = ModelDeltas.Compare(Sized(20, 40, 50, 1000, triangles: 100), Sized(20, 40, 52, 1000, triangles: 1100), false);
        Assert.Equal("Taller by 2.0 mm, 1,000 more triangles", delta.Summary);
    }

    [Fact]
    public void Versions_that_are_not_measured_cannot_be_compared_and_say_so()
    {
        var step = ModelAnalysis.Analyze("a.step", ReadOnlyMemory<byte>.Empty, 10, Limits);
        var delta = ModelDeltas.Compare(step, Sized(1, 1, 1, 1), false);
        Assert.False(delta.Comparable);
        Assert.Empty(delta.Rows);
        Assert.Contains("not measured", delta.Summary);
    }

    [Fact]
    public void The_bracket_example_of_the_demo_reads_naturally()
    {
        var v1 = Measure("b.stl", SyntheticMesh.ToBinaryStl(SyntheticMesh.Bracket(40, 30, 4, 20)));
        var v2 = Measure("b.stl", SyntheticMesh.ToBinaryStl(SyntheticMesh.Bracket(40, 32, 4, 20)));
        var delta = ModelDeltas.Compare(v1, v2, false);
        Assert.StartsWith("Taller by 2.0 units, volume up 3.0 %", delta.Summary);
    }
}
