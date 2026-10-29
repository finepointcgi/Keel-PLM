using System.Globalization;

namespace Keel.Core.Geometry;

/// <summary>
/// Wavefront OBJ reader (geometry only: <c>v</c> and <c>f</c>; normals, texture coordinates, groups, materials and lines are ignored). Faces with more
/// than three corners are fan-triangulated (correct for convex faces, the usual case for exported CAD meshes). Negative (relative) indices are
/// supported. Every index is range-checked; line length, vertex count and triangle count are limited before anything is allocated.
/// </summary>
public static class ObjReader
{
    private const int MaxCornersPerFace = 4096;

    public static MeshData Read(ReadOnlySpan<byte> bytes, MeshLimits limits)
    {
        var vertices = new List<float>(); // x, y, z per vertex
        var faces = new List<int>();       // resolved 0-based corner indices; -1 ends a face
        long triangles = 0;
        var pos = 0;
        while (pos < bytes.Length)
        {
            var end = bytes[pos..].IndexOf((byte)'\n');
            var lineEnd = end < 0 ? bytes.Length : pos + end;
            var line = bytes[pos..lineEnd];
            pos = lineEnd + 1;
            if (line.Length > limits.MaxLineBytes) throw new MeshFormatException("The OBJ file contains a line that is too long to be valid.");
            var at = 0;
            var key = StlReader.NextToken(line, ref at, limits.MaxLineBytes);
            if (key.Length == 1 && key[0] == (byte)'v')
            {
                if (vertices.Count / 3 >= limits.MaxVertices) throw new MeshFormatException($"The OBJ file has more than {limits.MaxVertices.ToString("N0", CultureInfo.InvariantCulture)} vertices, more than Keel analyses.");
                for (var i = 0; i < 3; i++)
                {
                    var t = StlReader.NextToken(line, ref at, limits.MaxLineBytes);
                    if (t.IsEmpty || !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        throw new MeshFormatException("The OBJ file has a vertex with a missing or invalid coordinate.");
                    vertices.Add((float)Finite.Check(v, limits.MaxCoordinate));
                }
            }
            else if (key.Length == 1 && key[0] == (byte)'f')
            {
                var corners = 0;
                while (true)
                {
                    var t = StlReader.NextToken(line, ref at, limits.MaxLineBytes);
                    if (t.IsEmpty) break;
                    var slash = t.IndexOf((byte)'/');
                    var head = slash < 0 ? t : t[..slash];
                    if (!int.TryParse(head, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index) || index == 0)
                        throw new MeshFormatException("The OBJ file has a face with an invalid vertex number.");
                    var known = vertices.Count / 3;
                    faces.Add(index > 0 ? index - 1 : known + index);
                    if (++corners > MaxCornersPerFace) throw new MeshFormatException("The OBJ file has a face with too many corners.");
                }
                if (corners < 3) throw new MeshFormatException("The OBJ file has a face with fewer than three corners.");
                triangles += corners - 2;
                if (triangles > limits.MaxTriangles) throw new MeshFormatException($"The model has more than {limits.MaxTriangles.ToString("N0", CultureInfo.InvariantCulture)} triangles, more than Keel analyses.");
                faces.Add(-1);
            }
        }
        if (triangles == 0) throw new MeshFormatException("The file contains no triangles.");

        var vertexCount = vertices.Count / 3;
        var buffer = new TriangleBuffer(limits.MaxTriangles);
        var corner = new List<int>();
        for (var i = 0; i < faces.Count; i++)
        {
            if (faces[i] >= 0)
            {
                if (faces[i] >= vertexCount) throw new MeshFormatException("The OBJ file has a face that refers to a vertex that does not exist.");
                corner.Add(faces[i]);
                continue;
            }
            for (var k = 1; k + 1 < corner.Count; k++) Add(buffer, vertices, corner[0], corner[k], corner[k + 1]);
            corner.Clear();
        }
        return buffer.ToMesh("obj", "units", "y");
    }

    private static void Add(TriangleBuffer b, List<float> v, int i, int j, int k) =>
        b.Add(v[i * 3], v[i * 3 + 1], v[i * 3 + 2], v[j * 3], v[j * 3 + 1], v[j * 3 + 2], v[k * 3], v[k * 3 + 1], v[k * 3 + 2]);
}
