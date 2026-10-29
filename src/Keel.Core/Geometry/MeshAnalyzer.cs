using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Keel.Core.Geometry;

/// <summary>
/// What Keel computed from a model file (ADR-0046). Everything here is derived from the stored bytes by the server; nothing is supplied by a user.
/// <see cref="Status"/> is <c>analysed</c> (mesh numbers present), <c>facts_only</c> (a format Keel does not measure: only file facts and, for STEP, a product count)
/// or <c>failed</c> (the file could not be read; <see cref="Reason"/> says why in plain words).
/// </summary>
public sealed record ModelMetrics
{
    public const int AnalyserVersion = 1;

    public string Status { get; init; } = "facts_only";
    public string Format { get; init; } = "other";
    public string? Reason { get; init; }
    public string Unit { get; init; } = "units";
    /// <summary>The axis the format stands on ("z" or "y"); decides the words "taller" and "deeper" in summaries.</summary>
    public string Up { get; init; } = "z";
    public long? Triangles { get; init; }
    public long? DegenerateTriangles { get; init; }
    /// <summary>Distinct vertex positions (vertices at the same place are counted once).</summary>
    public long? Vertices { get; init; }
    public double? MinX { get; init; }
    public double? MinY { get; init; }
    public double? MinZ { get; init; }
    public double? MaxX { get; init; }
    public double? MaxY { get; init; }
    public double? MaxZ { get; init; }
    public double? SurfaceArea { get; init; }
    /// <summary>Signed volume of the closed surface; null when the mesh is not watertight (then no volume is claimed).</summary>
    public double? SignedVolume { get; init; }
    public bool? Watertight { get; init; }
    /// <summary>Hash of the normalized triangles: the same shape gives the same value whatever the file bytes, triangle order or vertex order.</summary>
    public string? GeometryFingerprint { get; init; }
    /// <summary>STEP only: number of products found by the STEP structure reader. Null when not read.</summary>
    public int? StepProducts { get; init; }

    public double? SizeX => MaxX - MinX;
    public double? SizeY => MaxY - MinY;
    public double? SizeZ => MaxZ - MinZ;
    public double? Volume => SignedVolume is { } v ? Math.Abs(v) : null;
    public bool IsAnalysed => Status == "analysed";
}

/// <summary>
/// Computes <see cref="ModelMetrics"/> from a triangle soup: bounding box, surface area, signed volume (only for a watertight, consistently oriented
/// mesh), unique vertex count and the geometry fingerprint. Positions are snapped to a grid of 1/100,000 of the model's diagonal to weld vertices and
/// to normalize the fingerprint; this makes the fingerprint robust to the rounding of a different exporter (borderline values can still land on the other
/// side of a grid line, in which case two files of the same shape are reported as different, never the opposite except by hash collision).
/// </summary>
public static class MeshAnalyzer
{
    private const double GridPerDiagonal = 1e5;
    private const string FingerprintTag = "keel.geometry.v1";

    public static ModelMetrics Analyze(MeshData mesh)
    {
        var p = mesh.Positions;
        var n = mesh.TriangleCount;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        for (var i = 0; i < n * 9; i += 3)
        {
            double x = p[i], y = p[i + 1], z = p[i + 2];
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
            if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
        }
        var diagonal = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY) + (maxZ - minZ) * (maxZ - minZ));
        var grid = diagonal > 0 ? diagonal / GridPerDiagonal : 1e-9;
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2, cz = (minZ + maxZ) / 2;

        var ids = new Dictionary<(long, long, long), int>();
        var edges = new Dictionary<long, int>(); // key: (low << 32) | high; value: forward uses in the low 16 bits, reverse uses in the high 16 bits (saturating)
        double area = 0, volume = 0;
        long degenerate = 0, used = 0;
        ulong l0 = 0, l1 = 0, l2 = 0, l3 = 0;
        Span<byte> buffer = stackalloc byte[72];
        Span<(long X, long Y, long Z)> q = stackalloc (long, long, long)[3];
        Span<byte> h = stackalloc byte[32];
        var tri = new int[3];

        for (var t = 0; t < n; t++)
        {
            var o = t * 9;
            for (var k = 0; k < 3; k++)
            {
                var key = (Snap(p[o + k * 3], grid), Snap(p[o + k * 3 + 1], grid), Snap(p[o + k * 3 + 2], grid));
                q[k] = key;
                if (!ids.TryGetValue(key, out var id)) ids[key] = id = ids.Count;
                tri[k] = id;
            }
            if (tri[0] == tri[1] || tri[1] == tri[2] || tri[0] == tri[2]) { degenerate++; continue; }
            used++;

            // area and volume from the (unsnapped) float positions, relative to the box centre for accuracy
            double ax = p[o] - cx, ay = p[o + 1] - cy, az = p[o + 2] - cz;
            double bx = p[o + 3] - cx, by = p[o + 4] - cy, bz = p[o + 5] - cz;
            double qx = p[o + 6] - cx, qy = p[o + 7] - cy, qz = p[o + 8] - cz;
            double ux = bx - ax, uy = by - ay, uz = bz - az, vx = qx - ax, vy = qy - ay, vz = qz - az;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            area += 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
            volume += (ax * (by * qz - bz * qy) - ay * (bx * qz - bz * qx) + az * (bx * qy - by * qx)) / 6.0;

            CountEdge(edges, tri[0], tri[1]);
            CountEdge(edges, tri[1], tri[2]);
            CountEdge(edges, tri[2], tri[0]);

            // fingerprint: sorted snapped corners, hashed, summed lane-wise (order independent)
            Sort3(q);
            for (var k = 0; k < 3; k++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(buffer[(k * 24)..], q[k].X);
                BinaryPrimitives.WriteInt64LittleEndian(buffer[(k * 24 + 8)..], q[k].Y);
                BinaryPrimitives.WriteInt64LittleEndian(buffer[(k * 24 + 16)..], q[k].Z);
            }
            SHA256.HashData(buffer, h);
            l0 += BinaryPrimitives.ReadUInt64LittleEndian(h); l1 += BinaryPrimitives.ReadUInt64LittleEndian(h[8..]);
            l2 += BinaryPrimitives.ReadUInt64LittleEndian(h[16..]); l3 += BinaryPrimitives.ReadUInt64LittleEndian(h[24..]);
        }

        var watertight = used >= 4 && edges.Count > 0 && edges.Values.All(v => (v & 0xFFFF) == 1 && (v >> 16) == 1);
        var final = new byte[Encoding.ASCII.GetByteCount(FingerprintTag) + 8 + 32];
        var tag = Encoding.ASCII.GetBytes(FingerprintTag);
        tag.CopyTo(final, 0);
        BinaryPrimitives.WriteInt64LittleEndian(final.AsSpan(tag.Length), used);
        BinaryPrimitives.WriteUInt64LittleEndian(final.AsSpan(tag.Length + 8), l0); BinaryPrimitives.WriteUInt64LittleEndian(final.AsSpan(tag.Length + 16), l1);
        BinaryPrimitives.WriteUInt64LittleEndian(final.AsSpan(tag.Length + 24), l2); BinaryPrimitives.WriteUInt64LittleEndian(final.AsSpan(tag.Length + 32), l3);

        return new ModelMetrics
        {
            Status = "analysed",
            Format = mesh.Format,
            Unit = mesh.Unit,
            Up = mesh.Up,
            Triangles = n,
            DegenerateTriangles = degenerate,
            Vertices = ids.Count,
            MinX = minX, MinY = minY, MinZ = minZ, MaxX = maxX, MaxY = maxY, MaxZ = maxZ,
            SurfaceArea = area,
            SignedVolume = watertight ? volume : null,
            Watertight = watertight,
            GeometryFingerprint = Convert.ToHexStringLower(SHA256.HashData(final)),
        };
    }

    private static long Snap(float v, double grid) => (long)Math.Round(v / grid, MidpointRounding.AwayFromZero);

    private static void CountEdge(Dictionary<long, int> edges, int from, int to)
    {
        var forward = from < to;
        var key = ((long)Math.Min(from, to) << 32) | (uint)Math.Max(from, to);
        edges.TryGetValue(key, out var v);
        // a directed use in the "low to high" direction counts in the low half, the opposite direction in the high half; saturate instead of overflowing
        if (forward) v = (v & 0xFFFF) < 0xFFFF ? v + 1 : v;
        else v = (v >> 16) < 0xFFFF ? v + 0x10000 : v;
        edges[key] = v;
    }

    private static void Sort3(Span<(long X, long Y, long Z)> q)
    {
        if (Compare(q[0], q[1]) > 0) (q[0], q[1]) = (q[1], q[0]);
        if (Compare(q[1], q[2]) > 0) (q[1], q[2]) = (q[2], q[1]);
        if (Compare(q[0], q[1]) > 0) (q[0], q[1]) = (q[1], q[0]);
    }

    private static int Compare((long X, long Y, long Z) a, (long X, long Y, long Z) b)
    {
        var c = a.X.CompareTo(b.X);
        if (c != 0) return c;
        c = a.Y.CompareTo(b.Y);
        return c != 0 ? c : a.Z.CompareTo(b.Z);
    }
}
