using System.Text.RegularExpressions;

namespace Keel.Notifications;

public enum SegmentKind { Text, Mention, Reference }

/// <summary>A piece of a comment body: plain text, an <c>@login</c> mention or an object number such as <c>P-000015</c>.</summary>
public sealed record TextSegment(SegmentKind Kind, string Text);

/// <summary>
/// The shape of a comment: plain text with line breaks, at most <see cref="AwarenessLimits.CommentLength"/> characters. Nothing in it is ever interpreted as
/// markup: the UI renders segments as encoded text. Two things are recognized, both by form alone (resolving them happens later, per viewer):
/// <c>@login</c> and same-system object numbers (<c>P-000015</c>, <c>ECN-000002</c>).
/// </summary>
public static partial class CommentText
{
    // A login: starts with a letter or digit, then letters, digits, '.', '_' or '-'. Not preceded by a word character (an e-mail address is not a mention).
    [GeneratedRegex(@"(?<![\w@.])@(?<login>[A-Za-z0-9][A-Za-z0-9._-]{0,63})|(?<![\w-])(?<ref>[A-Z]{1,6}-[0-9]{3,10})(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();

    /// <summary>Why a body is not acceptable, or null when it is.</summary>
    public static string? Validate(string? body)
    {
        var text = body?.Trim() ?? "";
        if (text.Length == 0) return "Write something first.";
        if (text.Length > AwarenessLimits.CommentLength) return $"A comment can have at most {AwarenessLimits.CommentLength} characters.";
        foreach (var c in text)
            if (char.IsControl(c) && c is not ('\n' or '\r' or '\t')) return "A comment is plain text; it cannot contain control characters.";
        return null;
    }

    /// <summary>The text as stored: trimmed, line breaks as \n.</summary>
    public static string Normalize(string body) => body.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    public static IReadOnlyList<TextSegment> Segments(string body)
    {
        var segments = new List<TextSegment>();
        var last = 0;
        foreach (Match m in Tokens().Matches(body))
        {
            var (kind, token) = m.Groups["login"].Success ? (SegmentKind.Mention, "@" + TrimLogin(m.Groups["login"].Value)) : (SegmentKind.Reference, m.Groups["ref"].Value);
            if (m.Index > last) segments.Add(new TextSegment(SegmentKind.Text, body[last..m.Index]));
            segments.Add(new TextSegment(kind, token));
            // A mention's trailing punctuation ("@alice.") belongs to the sentence, not to the login.
            last = m.Index + token.Length;
        }
        if (last < body.Length) segments.Add(new TextSegment(SegmentKind.Text, body[last..]));
        return segments;
    }

    private static string TrimLogin(string login) => login.TrimEnd('.', '-', '_');

    /// <summary>The distinct logins mentioned (lower case), at most <see cref="AwarenessLimits.MaxMentionsPerComment"/>.</summary>
    public static IReadOnlyList<string> Mentions(string body) =>
        [.. Segments(body).Where(s => s.Kind == SegmentKind.Mention).Select(s => s.Text[1..].ToLowerInvariant()).Distinct(StringComparer.Ordinal).Take(AwarenessLimits.MaxMentionsPerComment)];

    /// <summary>The distinct object numbers named in the text, in order of appearance.</summary>
    public static IReadOnlyList<string> References(string body) =>
        [.. Segments(body).Where(s => s.Kind == SegmentKind.Reference).Select(s => s.Text).Distinct(StringComparer.Ordinal)];
}
