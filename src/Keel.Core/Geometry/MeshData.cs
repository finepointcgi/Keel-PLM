using System.Globalization;

namespace Keel.Core.Geometry;

/// <summary>Hard limits of the in-house mesh readers (ADR-0046). Every reader checks them BEFORE it allocates for the data they bound.</summary>
public sealed record MeshLimits
{
    /// <summary>Largest file read (bytes). Larger files are refused as "too large to analyse".</summary>
    public long MaxBytes { get; init; } = 100L * 1024 * 1024;
    /// <summary>Most triangles a mesh may have (after fan triangulation and component expansion).</summary>
    public int MaxTriangles { get; init; } = 2_000_000;
    /// <summary>Most distinct vertices an OBJ or 3MF object may declare.</summary>
    public int MaxVertices { get; init; } = 6_000_000;
    /// <summary>3MF: most entries in the ZIP container.</summary>
    public int MaxZipEntries { get; init; } = 256;
    /// <summary>3MF: most bytes any one part may expand to (the declared size is never trusted; reading stops here).</summary>
    public long MaxUncompressedBytes { get; init; } = 256L * 1024 * 1024;
    /// <summary>3MF: most object instances produced by expanding components (no recursion; an explicit stack).</summary>
    public int MaxInstances { get; init; } = 100_000;
    /// <summary>3MF: deepest component nesting.</summary>
    public int MaxComponentDepth { get; init; } = 8;
    /// <summary>OBJ and ASCII STL: longest line / token read.</summary>
    public int MaxLineBytes { get; init; } = 64 * 1024;
    /// <summary>Largest absolute coordinate accepted (guards the arithmetic of area, volume and the fingerprint).</summary>
    public double MaxCoordinate { get; init; } = 1e9;

    public static MeshLimits Default { get; } = new();
}

/// <summary>A file the mesh readers refuse. The message is written for the person who uploaded it and never contains file content.</summary>
public sealed class MeshFormatException(string message) : Exception(message);

/// <summary>
/// A triangle soup: <see cref="Positions"/> holds nine floats per triangle (x, y, z of three vertices). <see cref="Unit"/> is "mm" when the file states
/// (3MF) or defines its unit, otherwise "units"; <see cref="Up"/> is the axis the format conventionally stands on ("z" for STL and 3MF, "y" for OBJ).
/// </summary>
public sealed class MeshData(float[] positions, int triangleCount, string format, string unit, string up)
{
    public float[] Positions { get; } = positions;
    public int TriangleCount { get; } = triangleCount;
    public string Format { get; } = format;
    public string Unit { get; } = unit;
    public string Up { get; } = up;
}

/// <summary>Growable float buffer with a hard cap; the cap is checked before every growth.</summary>
internal sealed class TriangleBuffer(int maxTriangles)
{
    private float[] _data = new float[9 * 64];
    private int _count;

    public int Count => _count;

    public void Add(double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz)
    {
        if (_count >= maxTriangles) throw new MeshFormatException($"The model has more than {maxTriangles.ToString("N0", CultureInfo.InvariantCulture)} triangles, more than Keel analyses.");
        var at = _count * 9;
        if (at + 9 > _data.Length)
        {
            var next = (long)_data.Length * 2;
            var cap = 9L * maxTriangles;
            Array.Resize(ref _data, (int)Math.Min(Math.Max(next, at + 9), cap));
        }
        _data[at] = (float)ax; _data[at + 1] = (float)ay; _data[at + 2] = (float)az;
        _data[at + 3] = (float)bx; _data[at + 4] = (float)by; _data[at + 5] = (float)bz;
        _data[at + 6] = (float)cx; _data[at + 7] = (float)cy; _data[at + 8] = (float)cz;
        _count++;
    }

    public MeshData ToMesh(string format, string unit, string up)
    {
        if (_count == 0) throw new MeshFormatException("The file contains no triangles.");
        var result = new float[_count * 9];
        Array.Copy(_data, result, result.Length);
        return new MeshData(result, _count, format, unit, up);
    }
}

internal static class Finite
{
    /// <summary>Validates one coordinate: finite and within the configured range.</summary>
    public static double Check(double value, double max)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > max) throw new MeshFormatException("The file contains coordinates that are not valid numbers or are out of range.");
        return value;
    }
}
