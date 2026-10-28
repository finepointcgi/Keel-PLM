using System.Text;
using System.Text.Json;
using Keel.Core.Viewing;

namespace Keel.Viewing;

/// <summary>What the content (not the name, not the stored media type) says a file is.</summary>
/// <param name="Format">The detected format, <see cref="FileFormats.Unknown"/> when the content matches nothing the viewer knows.</param>
/// <param name="Claimed">The format the file name claims.</param>
/// <param name="Basis">How it was recognised ("magic bytes", "binary STL size check", "content pattern + extension", …) for audit and the UI.</param>
/// <param name="Warning">Set when the name and the content disagree, or the content is not what the name says.</param>
public sealed record Detection(FileFormat Format, FileFormat Claimed, string Basis, string? Warning)
{
    public bool Recognised => Format != FileFormats.Unknown;
}

/// <summary>
/// Type detection by magic bytes and content patterns, cross-checked with the extension (ADR-0027, VWR-3). Formats with a real signature (PDF, PNG, JPEG,
/// GIF, WebP, GLB, STEP) are recognised by content whatever the file is called, and a mismatch is reported. Formats without one (STL, OBJ, glTF JSON,
/// SVG, IGES) must pass a structural check AND carry their extension, so an arbitrary text file named .txt is never treated as a model. The stored media
/// type is never consulted: it is client-supplied.
/// </summary>
public static partial class FileSniffer
{
    /// <summary>Bytes needed from the start of a file for a head-only (advisory) detection.</summary>
    public const int HeadBytes = 4096;

    /// <param name="fileName">The stored file name (extension claim).</param>
    /// <param name="content">The file or its first <see cref="HeadBytes"/> bytes.</param>
    /// <param name="totalSize">The size of the whole file (binary STL is verified against it).</param>
    public static Detection Detect(string fileName, ReadOnlySpan<byte> content, long totalSize)
    {
        var claimed = FileFormats.FromFileName(fileName);
        var complete = content.Length >= totalSize;

        var (strong, basis) = Strong(content);
        if (strong is not null)
        {
            var warning = claimed == FileFormats.Unknown
                ? null
                : Same(strong, claimed) ? null : $"The file is named like a {claimed.Label} but its content is a {strong.Label}; it is shown as a {strong.Label}.";
            return new Detection(strong, claimed, basis, warning);
        }

        // Weak formats: the content must look right AND the extension must say so.
        if (claimed != FileFormats.Unknown)
        {
            var ok = claimed.Id switch
            {
                "stl" => IsStl(content, totalSize, out basis),
                "obj" => IsObj(content, out basis),
                "gltf" => IsGltfJson(content, complete, out basis),
                "svg" => IsSvg(content, out basis),
                "iges" => IsIges(content, out basis),
                _ => false,
            };
            if (ok) return new Detection(claimed, claimed, basis, null);
            return new Detection(FileFormats.Unknown, claimed, "content check failed",
                $"The file is named like a {claimed.Label} but its content is not a valid {claimed.Label}.");
        }
        return new Detection(FileFormats.Unknown, claimed, "no known signature", null);
    }

    // PDF, PNG, JPEG, GIF, WebP, GLB and STEP have signatures.
    private static (FileFormat? Format, string Basis) Strong(ReadOnlySpan<byte> b)
    {
        if (b.StartsWith("%PDF-"u8)) return (FileFormats.Pdf, "magic bytes %PDF-");
        if (b.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return (FileFormats.Png, "PNG signature");
        if (b.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return (FileFormats.Jpeg, "JPEG SOI marker");
        if (b.StartsWith("GIF87a"u8) || b.StartsWith("GIF89a"u8)) return (FileFormats.Gif, "GIF signature");
        if (b.Length >= 12 && b.StartsWith("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WEBP"u8)) return (FileFormats.WebP, "RIFF/WEBP signature");
        if (b.Length >= 12 && b.StartsWith("glTF"u8) && BitConverter.ToUInt32(b.Slice(4, 4)) == 2) return (FileFormats.Glb, "GLB header (glTF 2)");
        if (IsStep(b)) return (FileFormats.Step, "ISO-10303-21 header");
        return (null, "");
    }

    private static bool Same(FileFormat a, FileFormat b) => a == b;

    private static ReadOnlySpan<byte> SkipBomAndSpace(ReadOnlySpan<byte> b)
    {
        if (b.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) b = b[3..];
        var i = 0;
        while (i < b.Length && b[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        return b[i..];
    }

    private static bool IsStep(ReadOnlySpan<byte> b)
    {
        var t = SkipBomAndSpace(b);
        return t.StartsWith("ISO-10303-21"u8);
    }

    private static bool IsStl(ReadOnlySpan<byte> b, long totalSize, out string basis)
    {
        // Binary STL: 80-byte header, uint32 triangle count, 50 bytes per triangle. The size formula is the only reliable test, and some
        // binary files even begin with the word "solid".
        if (totalSize >= 84 && b.Length >= 84)
        {
            var count = BitConverter.ToUInt32(b.Slice(80, 4));
            if (count > 0 && 84L + 50L * count == totalSize) { basis = "binary STL size check"; return true; }
        }
        var t = SkipBomAndSpace(b);
        if (t.StartsWith("solid"u8) && IsMostlyText(b) && (b.IndexOf("facet"u8) >= 0 || b.IndexOf("endsolid"u8) >= 0))
        {
            basis = "ASCII STL keywords";
            return true;
        }
        basis = "";
        return false;
    }

    private static bool IsObj(ReadOnlySpan<byte> b, out string basis)
    {
        basis = "";
        if (!IsMostlyText(b)) return false;
        // A vertex line: "v x y z" (a number follows).
        if (!ObjVertex().IsMatch(Encoding.UTF8.GetString(b[..Math.Min(b.Length, HeadBytes)]))) return false;
        basis = "OBJ vertex lines";
        return true;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*v[ \t]+[-+]?[0-9.]", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex ObjVertex();

    private static bool IsGltfJson(ReadOnlySpan<byte> b, bool complete, out string basis)
    {
        basis = "";
        var t = SkipBomAndSpace(b);
        if (t.Length == 0 || t[0] != (byte)'{') return false;
        if (!complete)
        {
            // Head only (advisory): the verified read repeats this on the whole file, including the asset check.
            basis = "glTF JSON start (head only)";
            return true;
        }
        try
        {
            using var doc = JsonDocument.Parse(b.ToArray(), new JsonDocumentOptions { MaxDepth = 64, AllowTrailingCommas = false });
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("asset", out var asset)
                && asset.ValueKind == JsonValueKind.Object && asset.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
            {
                basis = "glTF JSON with asset.version";
                return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static bool IsSvg(ReadOnlySpan<byte> b, out string basis)
    {
        basis = "";
        if (!IsMostlyText(b)) return false;
        var text = Encoding.UTF8.GetString(SkipBomAndSpace(b));
        if (!(text.StartsWith("<?xml", StringComparison.Ordinal) || text.StartsWith("<svg", StringComparison.Ordinal) || text.StartsWith("<!--", StringComparison.Ordinal)
              || text.StartsWith("<!DOCTYPE svg", StringComparison.OrdinalIgnoreCase))) return false;
        if (text.IndexOf("<svg", StringComparison.Ordinal) < 0) return false;
        basis = "SVG root element";
        return true;
    }

    private static bool IsIges(ReadOnlySpan<byte> b, out string basis)
    {
        // IGES is fixed-format: every record is 80 columns and column 73 holds the section letter (S = start section), columns 74-80 the sequence number.
        basis = "";
        var t = SkipBomAndSpace(b);
        if (t.Length < 80 || t[72] != (byte)'S') return false;
        var seq = Encoding.ASCII.GetString(t.Slice(73, 7)).Trim();
        if (seq != "1") return false;
        for (var i = 0; i < 72; i++) if (t[i] is < 32 or > 126) return false;
        basis = "IGES fixed-format start section";
        return true;
    }

    /// <summary>No NUL bytes and almost all printable/whitespace: the cheap test for "this is text".</summary>
    private static bool IsMostlyText(ReadOnlySpan<byte> b)
    {
        var sample = b[..Math.Min(b.Length, HeadBytes)];
        var odd = 0;
        foreach (var c in sample)
        {
            if (c == 0) return false;
            if (c < 9 || (c is > 13 and < 32)) odd++;
        }
        return odd * 100 <= Math.Max(1, sample.Length);
    }
}
