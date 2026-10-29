using System.Globalization;
using System.IO.Compression;
using System.Xml;

namespace Keel.Core.Geometry;

/// <summary>
/// 3MF reader (core specification: objects with meshes, components, build items, transforms and units; extensions are ignored). A 3MF file is a ZIP
/// of XML parts, so the reader treats it as hostile: the number of entries is limited, a part is read through a counting stream that stops at the
/// limit whatever size the ZIP header claims (zip bombs), XML is read with DTDs and external resolution off, and components are expanded with an
/// explicit stack, a depth limit and an instance limit (no recursion, so a component that refers to itself is refused, not followed).
/// Coordinates are converted to millimetres. A mirrored transform has its triangles re-wound so the volume keeps its sign.
/// </summary>
public static class ThreeMfReader
{
    private sealed class Obj
    {
        public readonly List<float> Vertices = []; // x, y, z
        public readonly List<int> Triangles = [];  // v1, v2, v3
        public readonly List<(int Id, double[] M)> Components = [];
        public bool Skip;
    }

    public static MeshData Read(ReadOnlyMemory<byte> bytes, MeshLimits limits)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(new MemoryStream(bytes.ToArray(), writable: false), ZipArchiveMode.Read, leaveOpen: false); }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException) { throw new MeshFormatException("The file is not a valid 3MF package (it is not a readable ZIP archive)."); }
        using (zip)
        {
            if (zip.Entries.Count > limits.MaxZipEntries) throw new MeshFormatException("The 3MF package contains too many parts to be valid.");
            var modelPath = FindModelPart(zip, limits);
            var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, modelPath, StringComparison.OrdinalIgnoreCase))
                        ?? throw new MeshFormatException("The 3MF package has no 3D model part.");
            var xml = ReadBounded(entry, limits.MaxUncompressedBytes);
            return ParseModel(xml, limits);
        }
    }

    private static string FindModelPart(ZipArchive zip, MeshLimits limits)
    {
        var rels = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, "_rels/.rels", StringComparison.OrdinalIgnoreCase));
        if (rels is not null)
        {
            try
            {
                using var reader = XmlReader.Create(new MemoryStream(ReadBounded(rels, 1024 * 1024)), Settings(1024 * 1024));
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship") continue;
                    var type = reader.GetAttribute("Type") ?? "";
                    var target = reader.GetAttribute("Target");
                    if (target is not null && type.EndsWith("/3dmodel", StringComparison.OrdinalIgnoreCase)) return target.TrimStart('/');
                }
            }
            catch (XmlException) { throw new MeshFormatException("The 3MF package relationships are not valid XML."); }
        }
        return "3D/3dmodel.model";
    }

    /// <summary>Reads a part but never more than <paramref name="max"/> bytes: the size in the ZIP header is not trusted.</summary>
    private static byte[] ReadBounded(ZipArchiveEntry entry, long max)
    {
        if (entry.Length > max) throw new MeshFormatException("A part of the 3MF package is larger than Keel reads.");
        using var stream = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int n;
        try
        {
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += n;
                if (total > max) throw new MeshFormatException("A part of the 3MF package expands to more than Keel reads.");
                output.Write(buffer, 0, n);
            }
        }
        catch (InvalidDataException) { throw new MeshFormatException("The 3MF package is damaged (a part cannot be decompressed)."); }
        return output.ToArray();
    }

    private static XmlReaderSettings Settings(long maxChars) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = maxChars,
        MaxCharactersFromEntities = 0,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
    };

    private static MeshData ParseModel(byte[] xml, MeshLimits limits)
    {
        var objects = new Dictionary<int, Obj>();
        var items = new List<(int Id, double[] M)>();
        var unitFactor = 1.0;
        Obj? current = null;
        var currentId = 0;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml, writable: false), Settings(limits.MaxUncompressedBytes));
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                switch (reader.LocalName)
                {
                    case "model":
                        unitFactor = UnitFactor(reader.GetAttribute("unit"));
                        break;
                    case "object":
                        currentId = Int(reader.GetAttribute("id"), "object id");
                        current = new Obj { Skip = reader.GetAttribute("type") is "support" or "solidsupport" };
                        if (!objects.TryAdd(currentId, current)) throw new MeshFormatException("The 3MF model defines the same object id twice.");
                        if (objects.Count > 100_000) throw new MeshFormatException("The 3MF model defines too many objects.");
                        break;
                    case "vertex" when current is not null:
                        if (current.Vertices.Count / 3 >= limits.MaxVertices) throw new MeshFormatException("The 3MF object has more vertices than Keel analyses.");
                        current.Vertices.Add((float)Finite.Check(Num(reader.GetAttribute("x")), limits.MaxCoordinate));
                        current.Vertices.Add((float)Finite.Check(Num(reader.GetAttribute("y")), limits.MaxCoordinate));
                        current.Vertices.Add((float)Finite.Check(Num(reader.GetAttribute("z")), limits.MaxCoordinate));
                        break;
                    case "triangle" when current is not null:
                        if (current.Triangles.Count / 3 >= limits.MaxTriangles) throw new MeshFormatException($"The model has more than {limits.MaxTriangles.ToString("N0", CultureInfo.InvariantCulture)} triangles, more than Keel analyses.");
                        current.Triangles.Add(Int(reader.GetAttribute("v1"), "triangle vertex"));
                        current.Triangles.Add(Int(reader.GetAttribute("v2"), "triangle vertex"));
                        current.Triangles.Add(Int(reader.GetAttribute("v3"), "triangle vertex"));
                        break;
                    case "component" when current is not null:
                        if (reader.GetAttribute("path") is not null) throw new MeshFormatException("This 3MF file stores parts of the model in separate files (production extension), which Keel does not read.");
                        if (current.Components.Count >= 10_000) throw new MeshFormatException("A 3MF object has too many components.");
                        current.Components.Add((Int(reader.GetAttribute("objectid"), "component object id"), Transform(reader.GetAttribute("transform"))));
                        break;
                    case "item":
                        if (reader.GetAttribute("path") is not null) throw new MeshFormatException("This 3MF file stores parts of the model in separate files (production extension), which Keel does not read.");
                        if (items.Count >= limits.MaxInstances) throw new MeshFormatException("The 3MF build has too many items.");
                        items.Add((Int(reader.GetAttribute("objectid"), "build item object id"), Transform(reader.GetAttribute("transform"))));
                        break;
                    case "build":
                        current = null;
                        break;
                }
            }
        }
        catch (XmlException) { throw new MeshFormatException("The 3MF model part is not valid XML."); }

        if (items.Count == 0)
            foreach (var id in objects.Where(o => !o.Value.Skip && o.Value.Triangles.Count > 0).Select(o => o.Key).OrderBy(k => k)) items.Add((id, Identity));

        var buffer = new TriangleBuffer(limits.MaxTriangles);
        var stack = new Stack<(int Id, double[] M, int Depth)>();
        for (var i = items.Count - 1; i >= 0; i--) stack.Push((items[i].Id, items[i].M, 1));
        var instances = 0;
        while (stack.Count > 0)
        {
            var (id, m, depth) = stack.Pop();
            if (++instances > limits.MaxInstances) throw new MeshFormatException("The 3MF model expands to too many object instances.");
            if (depth > limits.MaxComponentDepth) throw new MeshFormatException("The 3MF components are nested too deeply (or refer to themselves).");
            if (!objects.TryGetValue(id, out var o)) throw new MeshFormatException("The 3MF model refers to an object that does not exist.");
            if (o.Skip) continue;
            if (o.Triangles.Count > 0) AddInstance(buffer, o, m, unitFactor);
            for (var c = o.Components.Count - 1; c >= 0; c--) stack.Push((o.Components[c].Id, Multiply(o.Components[c].M, m), depth + 1));
        }
        return buffer.ToMesh("3mf", "mm", "z");
    }

    private static void AddInstance(TriangleBuffer buffer, Obj o, double[] m, double scale)
    {
        var count = o.Vertices.Count / 3;
        var flip = Determinant(m) < 0;
        for (var t = 0; t < o.Triangles.Count; t += 3)
        {
            var a = o.Triangles[t]; var b = o.Triangles[t + 1]; var c = o.Triangles[t + 2];
            if ((uint)a >= (uint)count || (uint)b >= (uint)count || (uint)c >= (uint)count)
                throw new MeshFormatException("The 3MF model has a triangle that refers to a vertex that does not exist.");
            if (flip) (b, c) = (c, b);
            Apply(o.Vertices, a, m, scale, out var ax, out var ay, out var az);
            Apply(o.Vertices, b, m, scale, out var bx, out var by, out var bz);
            Apply(o.Vertices, c, m, scale, out var cx, out var cy, out var cz);
            buffer.Add(ax, ay, az, bx, by, bz, cx, cy, cz);
        }
    }

    private static void Apply(List<float> v, int i, double[] m, double scale, out double x, out double y, out double z)
    {
        double px = v[i * 3], py = v[i * 3 + 1], pz = v[i * 3 + 2];
        // Row-vector convention of the 3MF specification: [x y z 1] * M, with M given as m00 m01 m02 m10 m11 m12 m20 m21 m22 m30 m31 m32.
        x = (px * m[0] + py * m[3] + pz * m[6] + m[9]) * scale;
        y = (px * m[1] + py * m[4] + pz * m[7] + m[10]) * scale;
        z = (px * m[2] + py * m[5] + pz * m[8] + m[11]) * scale;
    }

    private static readonly double[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];

    /// <summary>Composition for row vectors: apply <paramref name="a"/> first, then <paramref name="b"/>.</summary>
    private static double[] Multiply(double[] a, double[] b)
    {
        var r = new double[12];
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
                r[row * 3 + col] = a[row * 3] * b[col] + a[row * 3 + 1] * b[3 + col] + a[row * 3 + 2] * b[6 + col];
        for (var col = 0; col < 3; col++)
            r[9 + col] = a[9] * b[col] + a[10] * b[3 + col] + a[11] * b[6 + col] + b[9 + col];
        return r;
    }

    private static double Determinant(double[] m) =>
        m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]);

    private static double[] Transform(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Identity;
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 12) throw new MeshFormatException("A 3MF transform must have twelve numbers.");
        var m = new double[12];
        for (var i = 0; i < 12; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out m[i]) || !double.IsFinite(m[i]) || Math.Abs(m[i]) > 1e9)
                throw new MeshFormatException("A 3MF transform contains an invalid number.");
        }
        return m;
    }

    private static double UnitFactor(string? unit) => (unit ?? "millimeter").ToLowerInvariant() switch
    {
        "micron" => 0.001,
        "millimeter" => 1,
        "centimeter" => 10,
        "inch" => 25.4,
        "foot" => 304.8,
        "meter" => 1000,
        _ => throw new MeshFormatException("The 3MF model states a unit Keel does not know."),
    };

    private static int Int(string? s, string what) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : throw new MeshFormatException($"The 3MF model has an invalid {what}.");

    private static double Num(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : throw new MeshFormatException("The 3MF model has a vertex with a missing or invalid coordinate.");
}
