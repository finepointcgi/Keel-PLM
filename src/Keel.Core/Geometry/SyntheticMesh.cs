using System.Buffers.Binary;
using System.Text;

namespace Keel.Core.Geometry;

/// <summary>
/// Small synthetic models generated in code (no binary fixtures): used by the demo seed and by tests. Every mesh is closed, consistently oriented
/// (counter-clockwise seen from outside) and has a known volume and area, so tests can check the analyzer against exact values.
/// </summary>
public static class SyntheticMesh
{
    public readonly record struct Tri(double Ax, double Ay, double Az, double Bx, double By, double Bz, double Cx, double Cy, double Cz);

    /// <summary>An axis-aligned box from <paramref name="x0"/>,<paramref name="y0"/>,<paramref name="z0"/> with the given edge lengths (12 triangles).</summary>
    public static IReadOnlyList<Tri> Box(double sx, double sy, double sz, double x0 = 0, double y0 = 0, double z0 = 0)
    {
        double[] x = [x0, x0 + sx], y = [y0, y0 + sy], z = [z0, z0 + sz];
        (double X, double Y, double Z) P(int i, int j, int k) => (x[i], y[j], z[k]);
        // Quads, counter-clockwise seen from outside.
        (int, int, int)[][] quads =
        [
            [(0, 0, 0), (0, 1, 0), (1, 1, 0), (1, 0, 0)], // bottom (-z)
            [(0, 0, 1), (1, 0, 1), (1, 1, 1), (0, 1, 1)], // top (+z)
            [(0, 0, 0), (1, 0, 0), (1, 0, 1), (0, 0, 1)], // front (-y)
            [(0, 1, 0), (0, 1, 1), (1, 1, 1), (1, 1, 0)], // back (+y)
            [(0, 0, 0), (0, 0, 1), (0, 1, 1), (0, 1, 0)], // left (-x)
            [(1, 0, 0), (1, 1, 0), (1, 1, 1), (1, 0, 1)], // right (+x)
        ];
        var list = new List<Tri>(12);
        foreach (var q in quads)
        {
            var a = P(q[0].Item1, q[0].Item2, q[0].Item3); var b = P(q[1].Item1, q[1].Item2, q[1].Item3);
            var c = P(q[2].Item1, q[2].Item2, q[2].Item3); var d = P(q[3].Item1, q[3].Item2, q[3].Item3);
            list.Add(new Tri(a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z));
            list.Add(new Tri(a.X, a.Y, a.Z, c.X, c.Y, c.Z, d.X, d.Y, d.Z));
        }
        return list;
    }

    /// <summary>A UV sphere (poles on Z) with <paramref name="segments"/> around and <paramref name="rings"/> from pole to pole.</summary>
    public static IReadOnlyList<Tri> Sphere(double radius, int segments = 48, int rings = 24)
    {
        (double X, double Y, double Z) P(int ring, int seg)
        {
            var phi = Math.PI * ring / rings;
            var theta = 2 * Math.PI * (seg % segments) / segments;
            return (radius * Math.Sin(phi) * Math.Cos(theta), radius * Math.Sin(phi) * Math.Sin(theta), radius * Math.Cos(phi));
        }
        var list = new List<Tri>();
        for (var r = 0; r < rings; r++)
            for (var s = 0; s < segments; s++)
            {
                var a = P(r, s); var b = P(r + 1, s); var c = P(r + 1, s + 1); var d = P(r, s + 1);
                if (r > 0) list.Add(new Tri(a.X, a.Y, a.Z, b.X, b.Y, b.Z, d.X, d.Y, d.Z));
                if (r < rings - 1) list.Add(new Tri(d.X, d.Y, d.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z));
            }
        return list;
    }

    /// <summary>
    /// An L-shaped bracket: a base of <paramref name="baseLength"/> along Y and an upright of <paramref name="height"/> along Z, both of
    /// <paramref name="thickness"/>, extruded <paramref name="width"/> along X. Volume = (baseLength x thickness + thickness x (height - thickness)) x width.
    /// </summary>
    public static IReadOnlyList<Tri> Bracket(double baseLength, double height, double thickness, double width)
    {
        double L = baseLength, H = height, t = thickness, D = width;
        (double U, double V)[] poly = [(0, 0), (L, 0), (L, t), (t, t), (t, H), (0, H)];
        int[][] cap = [[0, 1, 3], [1, 2, 3], [0, 3, 4], [0, 4, 5]];
        // (u, v, w) -> (x, y, z) = (w, u, v): a cyclic permutation, so the orientation is kept.
        Tri T(double au, double av, double aw, double bu, double bv, double bw, double cu, double cv, double cw) => new(aw, au, av, bw, bu, bv, cw, cu, cv);
        var list = new List<Tri>();
        foreach (var c in cap)
        {
            var (a, b, d) = (poly[c[0]], poly[c[1]], poly[c[2]]);
            list.Add(T(a.U, a.V, D, b.U, b.V, D, d.U, d.V, D));   // +w cap, counter-clockwise seen from +w
            list.Add(T(a.U, a.V, 0, d.U, d.V, 0, b.U, b.V, 0));   // -w cap, reversed
        }
        for (var i = 0; i < poly.Length; i++)
        {
            var a = poly[i]; var b = poly[(i + 1) % poly.Length];
            list.Add(T(a.U, a.V, 0, b.U, b.V, 0, b.U, b.V, D));
            list.Add(T(a.U, a.V, 0, b.U, b.V, D, a.U, a.V, D));
        }
        return list;
    }

    /// <summary>A binary STL (80-byte header, count, 50 bytes per triangle).</summary>
    public static byte[] ToBinaryStl(IEnumerable<Tri> triangles, string title = "Keel synthetic model")
    {
        var tris = triangles as IReadOnlyList<Tri> ?? [.. triangles];
        var bytes = new byte[84 + 50 * tris.Count];
        Encoding.ASCII.GetBytes(title.Length > 79 ? title[..79] : title).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), (uint)tris.Count);
        var at = 84;
        foreach (var t in tris)
        {
            double ux = t.Bx - t.Ax, uy = t.By - t.Ay, uz = t.Bz - t.Az, vx = t.Cx - t.Ax, vy = t.Cy - t.Ay, vz = t.Cz - t.Az;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 0) { nx /= len; ny /= len; nz /= len; }
            foreach (var f in new[] { nx, ny, nz, t.Ax, t.Ay, t.Az, t.Bx, t.By, t.Bz, t.Cx, t.Cy, t.Cz })
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), (float)f);
                at += 4;
            }
            at += 2;
        }
        return bytes;
    }

    /// <summary>An ASCII STL with the numbers written with <paramref name="format"/> (for example "0.######" or "0.000000e+00").</summary>
    public static byte[] ToAsciiStl(IEnumerable<Tri> triangles, string format = "0.#######")
    {
        var sb = new StringBuilder("solid keel\n");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var t in triangles)
        {
            sb.Append("facet normal 0 0 0\n outer loop\n");
            sb.Append(inv, $"  vertex {t.Ax.ToString(format, inv)} {t.Ay.ToString(format, inv)} {t.Az.ToString(format, inv)}\n");
            sb.Append(inv, $"  vertex {t.Bx.ToString(format, inv)} {t.By.ToString(format, inv)} {t.Bz.ToString(format, inv)}\n");
            sb.Append(inv, $"  vertex {t.Cx.ToString(format, inv)} {t.Cy.ToString(format, inv)} {t.Cz.ToString(format, inv)}\n");
            sb.Append(" endloop\nendfacet\n");
        }
        return Encoding.ASCII.GetBytes(sb.Append("endsolid keel\n").ToString());
    }
}
