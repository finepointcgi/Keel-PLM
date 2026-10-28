using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Keel.Documents;

/// <summary>Validation of client-supplied file names and media types. Names are stored and offered back on download.</summary>
public static partial class FileNames
{
    public const int MaxLength = 255;
    public const string DefaultMediaType = "application/octet-stream";

    /// <summary>Returns an error message, or null when <paramref name="name"/> is acceptable as given.</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "A file name is required.";
        if (name.Length > MaxLength) return $"File names must be at most {MaxLength} characters.";
        if (name != name.Trim()) return "File names must not start or end with whitespace.";
        if (name is "." or "..") return "'.' and '..' are not file names.";
        if (name.EndsWith('.')) return "File names must not end with a dot.";
        for (var i = 0; i < name.Length;)
        {
            if (Rune.DecodeFromUtf16(name.AsSpan(i), out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
                return "File names must be valid Unicode text.";
            i += consumed;
            if (rune.Value is '/' or '\\') return "File names must not contain path separators.";
            var category = Rune.GetUnicodeCategory(rune);
            // Control characters break headers and logs; format characters (e.g. U+202E right-to-left override) can
            // disguise an extension (an .exe shown as .pdf).
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
                return "File names must not contain control or formatting characters.";
        }
        return null;
    }

    /// <summary>Normalizes a media type ("Application/PDF; charset=x" → "application/pdf"); null when it is malformed.</summary>
    public static string? NormalizeMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType)) return DefaultMediaType;
        var bare = mediaType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return MediaTypePattern().IsMatch(bare) ? bare : null;
    }

    // RFC 6838 restricted-name characters.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9!#$&^_.+\-]{0,126}/[a-z0-9][a-z0-9!#$&^_.+\-]{0,126}$")]
    private static partial Regex MediaTypePattern();
}
