using System.Buffers.Binary;
using System.Globalization;

namespace Keel.Core.Geometry;

/// <summary>
/// STL reader, binary and ASCII, written for Keel (no third-party code). Binary is recognised by its exact size (84 + 50 x count); a count that
/// does not match the size is never trusted. ASCII is read token by token with bounded lengths. Non-finite or huge coordinates, a vertex count
/// that is not a multiple of three, and files over the limits are refused with a plain message.
/// </summary>
public static class StlReader
{
    public static MeshData Read(ReadOnlySpan<byte> bytes, MeshLimits limits)
    {
        if (bytes.Length < 15) throw new MeshFormatException("The file is too short to be an STL model.");
        if (bytes.Length >= 84)
        {
            var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[80..]);
            if (84L + 50L * count == bytes.Length) return ReadBinary(bytes, count, limits);
        }
        var start = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) start = 3;
        while (start < bytes.Length && StlReader.IsSpace(bytes[start])) start++;
        if (bytes.Length - start >= 5 && bytes.Slice(start, 5).SequenceEqual("solid"u8)) return ReadAscii(bytes[start..], limits);
        throw new MeshFormatException("The file is not a valid STL model: it is neither a binary STL of the stated size nor an ASCII STL.");
    }

    private static MeshData ReadBinary(ReadOnlySpan<byte> bytes, uint declared, MeshLimits limits)
    {
        if (declared == 0) throw new MeshFormatException("The file contains no triangles.");
        if (declared > limits.MaxTriangles) throw new MeshFormatException($"The model has {declared.ToString("N0", CultureInfo.InvariantCulture)} triangles, more than the limit of {limits.MaxTriangles.ToString("N0", CultureInfo.InvariantCulture)}.");
        var count = (int)declared;
        var positions = new float[count * 9];
        var at = 84;
        for (var t = 0; t < count; t++)
        {
            at += 12; // normal
            for (var i = 0; i < 9; i++)
            {
                var v = BinaryPrimitives.ReadSingleLittleEndian(bytes[at..]);
                positions[t * 9 + i] = (float)Finite.Check(v, limits.MaxCoordinate);
                at += 4;
            }
            at += 2; // attribute bytes
        }
        return new MeshData(positions, count, "stl", "units", "z");
    }

    private static MeshData ReadAscii(ReadOnlySpan<byte> bytes, MeshLimits limits)
    {
        var buffer = new TriangleBuffer(limits.MaxTriangles);
        Span<double> corner = stackalloc double[9];
        var have = 0;
        var pos = 0;
        while (true)
        {
            var token = NextToken(bytes, ref pos, limits.MaxLineBytes);
            if (token.IsEmpty) break;
            if (!token.SequenceEqual("vertex"u8)) continue;
            for (var i = 0; i < 3; i++)
            {
                var number = NextToken(bytes, ref pos, limits.MaxLineBytes);
                if (number.IsEmpty || !double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    throw new MeshFormatException("The ASCII STL has a vertex with a missing or invalid coordinate.");
                corner[have * 3 + i] = Finite.Check(v, limits.MaxCoordinate);
            }
            if (++have == 3)
            {
                buffer.Add(corner[0], corner[1], corner[2], corner[3], corner[4], corner[5], corner[6], corner[7], corner[8]);
                have = 0;
            }
        }
        if (have != 0) throw new MeshFormatException("The ASCII STL ends inside a triangle.");
        return buffer.ToMesh("stl", "units", "z");
    }

    /// <summary>Next whitespace-delimited token; empty at the end. A token longer than <paramref name="max"/> is refused.</summary>
    internal static ReadOnlySpan<byte> NextToken(ReadOnlySpan<byte> bytes, ref int pos, int max)
    {
        while (pos < bytes.Length && IsSpace(bytes[pos])) pos++;
        var start = pos;
        while (pos < bytes.Length && !IsSpace(bytes[pos]))
        {
            pos++;
            if (pos - start > max) throw new MeshFormatException("The file contains a token that is too long to be valid.");
        }
        return bytes[start..pos];
    }

    internal static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0x0B or 0x0C;
}
