using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Keel.TestSupport;

/// <summary>
/// Small, deterministic, hand-written synthetic files for the viewer tests (increment 6.0): an ASCII and a binary STL cube, a glTF with two
/// boxes, a GLB, an OBJ cube, a STEP file of a box (a B-rep with six planar faces), a two-page PDF, a PNG, and an SVG that carries a script.
/// Nothing here is real engineering data.
/// </summary>
public static class ViewerSamples
{
    private static readonly (double X, double Y, double Z)[] Corners = [.. Enumerable.Range(0, 8).Select(i => ((double)(i & 1), (double)((i >> 1) & 1), (double)((i >> 2) & 1)))];

    // Quads, counter-clockwise seen from outside.
    private static readonly int[][] Quads = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static (double X, double Y, double Z) Scale((double X, double Y, double Z) p, double s) => (p.X * s, p.Y * s, p.Z * s);

    private static IEnumerable<((double X, double Y, double Z) A, (double X, double Y, double Z) B, (double X, double Y, double Z) C)> Triangles(double size)
    {
        foreach (var q in Quads)
        {
            yield return (Scale(Corners[q[0]], size), Scale(Corners[q[1]], size), Scale(Corners[q[2]], size));
            yield return (Scale(Corners[q[0]], size), Scale(Corners[q[2]], size), Scale(Corners[q[3]], size));
        }
    }

    private static (double X, double Y, double Z) Normal((double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c)
    {
        (double X, double Y, double Z) u = (b.X - a.X, b.Y - a.Y, b.Z - a.Z), v = (c.X - a.X, c.Y - a.Y, c.Z - a.Z);
        var n = (X: u.Y * v.Z - u.Z * v.Y, Y: u.Z * v.X - u.X * v.Z, Z: u.X * v.Y - u.Y * v.X);
        var l = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
        return l == 0 ? n : (n.X / l, n.Y / l, n.Z / l);
    }

    /// <summary>An ASCII STL cube of the given edge length (12 triangles).</summary>
    public static byte[] AsciiStlCube(double size = 10)
    {
        var sb = new StringBuilder("solid cube\n");
        foreach (var (a, b, c) in Triangles(size))
        {
            var n = Normal(a, b, c);
            sb.Append(CultureInfo.InvariantCulture, $"  facet normal {F(n.X)} {F(n.Y)} {F(n.Z)}\n    outer loop\n");
            foreach (var v in new[] { a, b, c }) sb.Append(CultureInfo.InvariantCulture, $"      vertex {F(v.X)} {F(v.Y)} {F(v.Z)}\n");
            sb.Append("    endloop\n  endfacet\n");
        }
        return Encoding.ASCII.GetBytes(sb.Append("endsolid cube\n").ToString());
    }

    /// <summary>A binary STL cube. The header starts with the word "solid" on purpose: only the size formula identifies a binary STL.</summary>
    public static byte[] BinaryStlCube(double size = 10)
    {
        var tris = Triangles(size).ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var header = new byte[80];
        Encoding.ASCII.GetBytes("solid binary cube exported by a tool").CopyTo(header, 0);
        w.Write(header);
        w.Write((uint)tris.Count);
        foreach (var (a, b, c) in tris)
        {
            var n = Normal(a, b, c);
            foreach (var f in new[] { n.X, n.Y, n.Z }) w.Write((float)f);
            foreach (var v in new[] { a, b, c }) { w.Write((float)v.X); w.Write((float)v.Y); w.Write((float)v.Z); }
            w.Write((ushort)0);
        }
        return ms.ToArray();
    }

    public static byte[] ObjCube(double size = 10)
    {
        var sb = new StringBuilder("# synthetic cube\no cube\n");
        foreach (var c in Corners.Select(p => Scale(p, size))) sb.Append(CultureInfo.InvariantCulture, $"v {F(c.X)} {F(c.Y)} {F(c.Z)}\n");
        foreach (var q in Quads) sb.Append(CultureInfo.InvariantCulture, $"f {q[0] + 1} {q[1] + 1} {q[2] + 1} {q[3] + 1}\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static (string Json, byte[] Bin) GltfParts(bool embedUri)
    {
        var bin = new MemoryStream();
        var views = new List<object>();
        var accessors = new List<object>();
        var meshes = new List<object>();
        foreach (var offset in new[] { 0.0, 2.0 })
        {
            var positions = new List<float>();
            var indices = new List<ushort>();
            foreach (var q in Quads)
            {
                var start = (ushort)(positions.Count / 3);
                foreach (var i in q) { positions.Add((float)(Corners[i].X + offset)); positions.Add((float)Corners[i].Y); positions.Add((float)Corners[i].Z); }
                indices.AddRange([start, (ushort)(start + 1), (ushort)(start + 2), start, (ushort)(start + 2), (ushort)(start + 3)]);
            }
            var posOffset = (int)bin.Length;
            foreach (var f in positions) bin.Write(BitConverter.GetBytes(f));
            views.Add(new { buffer = 0, byteOffset = posOffset, byteLength = positions.Count * 4, target = 34962 });
            accessors.Add(new
            {
                bufferView = views.Count - 1, componentType = 5126, count = positions.Count / 3, type = "VEC3",
                min = new[] { offset, 0.0, 0.0 }, max = new[] { offset + 1, 1.0, 1.0 },
            });
            var idxOffset = (int)bin.Length;
            foreach (var i in indices) bin.Write(BitConverter.GetBytes(i));
            views.Add(new { buffer = 0, byteOffset = idxOffset, byteLength = indices.Count * 2, target = 34963 });
            accessors.Add(new { bufferView = views.Count - 1, componentType = 5123, count = indices.Count, type = "SCALAR" });
            meshes.Add(new { primitives = new[] { new { attributes = new { POSITION = accessors.Count - 2 }, indices = accessors.Count - 1 } } });
        }
        var data = bin.ToArray();
        object buffer = embedUri
            ? new { byteLength = data.Length, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(data) }
            : new { byteLength = data.Length };
        var json = JsonSerializer.Serialize(new
        {
            asset = new { version = "2.0" }, scene = 0, scenes = new[] { new { nodes = new[] { 0, 1 } } },
            nodes = new object[] { new { mesh = 0, name = "left" }, new { mesh = 1, name = "right" } },
            meshes, accessors, bufferViews = views, buffers = new[] { buffer },
        });
        return (json, data);
    }

    /// <summary>A glTF 2.0 file with two unit boxes as two top-level nodes (so it can be exploded), buffers embedded as a data URI.</summary>
    public static byte[] GltfTwoBoxes() => Encoding.UTF8.GetBytes(GltfParts(embedUri: true).Json);

    /// <summary>The same model as a GLB.</summary>
    public static byte[] GlbTwoBoxes()
    {
        var (json, bin) = GltfParts(embedUri: false);
        var jsonBytes = Pad(Encoding.UTF8.GetBytes(json), 0x20);
        var binBytes = Pad(bin, 0);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + jsonBytes.Length + 8 + binBytes.Length));
        w.Write((uint)jsonBytes.Length); w.Write(0x4E4F534Au); w.Write(jsonBytes);
        w.Write((uint)binBytes.Length); w.Write(0x004E4942u); w.Write(binBytes);
        return ms.ToArray();
    }

    private static byte[] Pad(byte[] bytes, byte fill)
    {
        var padded = new byte[(bytes.Length + 3) / 4 * 4];
        Array.Fill(padded, fill);
        bytes.CopyTo(padded, 0);
        return padded;
    }

    /// <summary>A STEP (ISO 10303-21, AP203) file of one rectangular box with six planar faces, millimetres, corner at the origin.</summary>
    public static byte[] StepBox(double dx = 40, double dy = 20, double dz = 10)
    {
        var lines = new List<string>();
        var n = 100;
        int E(string s) { n++; lines.Add($"#{n}={s};"); return n; }
        var points = Enumerable.Range(0, 8).Select(i => (X: (i & 1) * dx, Y: ((i >> 1) & 1) * dy, Z: ((i >> 2) & 1) * dz)).ToArray();
        var vertex = new (int Point, int Vertex)[8];
        for (var i = 0; i < 8; i++)
        {
            var p = E($"CARTESIAN_POINT('',({F(points[i].X)},{F(points[i].Y)},{F(points[i].Z)}))");
            vertex[i] = (p, E($"VERTEX_POINT('',#{p})"));
        }
        var edges = new Dictionary<(int, int), int>();
        for (var a = 0; a < 8; a++)
            for (var b = a + 1; b < 8; b++)
            {
                if (int.PopCount(a ^ b) != 1) continue;
                var d = (X: points[b].X - points[a].X, Y: points[b].Y - points[a].Y, Z: points[b].Z - points[a].Z);
                var len = Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
                var dir = E($"DIRECTION('',({F(d.X / len)},{F(d.Y / len)},{F(d.Z / len)}))");
                var vec = E($"VECTOR('',#{dir},{F(len)})");
                var line = E($"LINE('',#{vertex[a].Point},#{vec})");
                edges[(a, b)] = E($"EDGE_CURVE('',#{vertex[a].Vertex},#{vertex[b].Vertex},#{line},.T.)");
            }
        var faces = new List<int>();
        foreach (var q in Quads)
        {
            var oriented = new List<int>();
            for (var i = 0; i < 4; i++)
            {
                int u = q[i], v = q[(i + 1) % 4];
                oriented.Add(E($"ORIENTED_EDGE('',*,*,#{edges[(Math.Min(u, v), Math.Max(u, v))]},{(u < v ? ".T." : ".F.")})"));
            }
            var loop = E($"EDGE_LOOP('',({string.Join(",", oriented.Select(o => "#" + o))}))");
            var bound = E($"FACE_OUTER_BOUND('',#{loop},.T.)");
            var normal = Normal(points[q[0]], points[q[1]], points[q[2]]);
            var origin = E($"CARTESIAN_POINT('',({F(points[q[0]].X)},{F(points[q[0]].Y)},{F(points[q[0]].Z)}))");
            var axis = E($"DIRECTION('',({F(normal.X)},{F(normal.Y)},{F(normal.Z)}))");
            var r = (X: points[q[1]].X - points[q[0]].X, Y: points[q[1]].Y - points[q[0]].Y, Z: points[q[1]].Z - points[q[0]].Z);
            var rl = Math.Sqrt(r.X * r.X + r.Y * r.Y + r.Z * r.Z);
            var reference = E($"DIRECTION('',({F(r.X / rl)},{F(r.Y / rl)},{F(r.Z / rl)}))");
            var placement = E($"AXIS2_PLACEMENT_3D('',#{origin},#{axis},#{reference})");
            var plane = E($"PLANE('',#{placement})");
            faces.Add(E($"ADVANCED_FACE('',(#{bound}),#{plane},.T.)"));
        }
        var shell = E($"CLOSED_SHELL('',({string.Join(",", faces.Select(f => "#" + f))}))");
        var solid = E($"MANIFOLD_SOLID_BREP('Box',#{shell})");
        var head = new[]
        {
            "ISO-10303-21;", "HEADER;", "FILE_DESCRIPTION(('Keel synthetic box'),'2;1');",
            "FILE_NAME('box.step','2026-01-01T00:00:00',('keel'),('keel'),'keel','keel','');", "FILE_SCHEMA(('CONFIG_CONTROL_DESIGN'));", "ENDSEC;", "DATA;",
            "#1=APPLICATION_CONTEXT('configuration controlled 3d designs of mechanical parts and assemblies');",
            "#2=APPLICATION_PROTOCOL_DEFINITION('international standard','config_control_design',1994,#1);",
            "#3=MECHANICAL_CONTEXT('none',#1,'mechanical');", "#4=PRODUCT('BOX','BOX','',(#3));",
            "#5=PRODUCT_DEFINITION_FORMATION_WITH_SPECIFIED_SOURCE('1','',#4,.NOT_KNOWN.);",
            "#6=PRODUCT_DEFINITION_CONTEXT('part definition',#1,'design');", "#7=PRODUCT_DEFINITION('design','',#5,#6);",
            "#8=PRODUCT_DEFINITION_SHAPE('','',#7);", "#9=SHAPE_DEFINITION_REPRESENTATION(#8,#10);",
            $"#10=SHAPE_REPRESENTATION('',(#11,#{solid}),#20);", "#11=AXIS2_PLACEMENT_3D('',#12,#13,#14);",
            "#12=CARTESIAN_POINT('',(0.,0.,0.));", "#13=DIRECTION('',(0.,0.,1.));", "#14=DIRECTION('',(1.,0.,0.));",
            "#20=(GEOMETRIC_REPRESENTATION_CONTEXT(3)GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT((#21))GLOBAL_UNIT_ASSIGNED_CONTEXT((#22,#23,#24))REPRESENTATION_CONTEXT('Context','3D'));",
            "#21=UNCERTAINTY_MEASURE_WITH_UNIT(LENGTH_MEASURE(1.E-07),#22,'distance_accuracy_value','confusion accuracy');",
            "#22=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.));", "#23=(NAMED_UNIT(*)PLANE_ANGLE_UNIT()SI_UNIT($,.RADIAN.));",
            "#24=(NAMED_UNIT(*)SI_UNIT($,.STERADIAN.)SOLID_ANGLE_UNIT());",
        };
        return Encoding.ASCII.GetBytes(string.Join("\n", head.Concat(lines).Concat(["ENDSEC;", "END-ISO-10303-21;", ""])));
    }

    /// <summary>A STEP file with a valid header and garbage data section: the sniffer accepts it, the importer must fail cleanly.</summary>
    public static byte[] CorruptStep() => Encoding.ASCII.GetBytes("ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('CONFIG_CONTROL_DESIGN'));\nENDSEC;\nDATA;\n#1=THIS_IS_NOT_STEP(((;\nENDSEC;\nEND-ISO-10303-21;\n");

    /// <summary>A minimal PNG (RGB gradient) of the given size.</summary>
    public static byte[] Png(int width = 64, int height = 32)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++) { raw.WriteByte((byte)(x * 4 % 256)); raw.WriteByte((byte)(y * 8 % 256)); raw.WriteByte(160); }
        }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw.ToArray());
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", z.ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(body);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, Crc32(body));
        s.Write(len);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>An SVG whose script would change the page title if it ran. The viewer must show the picture and never run the script.</summary>
    public static byte[] SvgWithScript() => Encoding.UTF8.GetBytes(
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"60\"><script>document.title='pwned'</script><rect width=\"120\" height=\"60\" fill=\"teal\"/><circle cx=\"60\" cy=\"30\" r=\"20\" fill=\"orange\"/></svg>");

    /// <summary>A valid PDF with the given number of pages (base-14 Helvetica); each page says "Synthetic sheet i of n" and carries a unique tag.</summary>
    public static byte[] Pdf(int pages = 2, string tag = "")
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{3 + 2 * i} 0 R"))}] /Count {pages} >>" };
        for (var i = 1; i <= pages; i++)
        {
            var pageObject = 3 + 2 * (i - 1);
            var content = $"BT /F1 18 Tf 30 100 Td (Synthetic sheet {i} of {pages} {tag}) Tj ET 0 0 1 rg 30 20 100 40 re f";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 200] /Contents {pageObject + 1} 0 R /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
