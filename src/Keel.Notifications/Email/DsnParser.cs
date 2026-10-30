using System.Text;
using System.Text.RegularExpressions;

namespace Keel.Notifications;

/// <summary>A bounce report found in a mail, with the tag the returned headers carried (null when the mail returned none).</summary>
public sealed record ParsedBounce(BounceReport Report, string? ReturnedTag);

/// <summary>
/// A small reader for the two bounce mail formats a relay produces: delivery status notifications (RFC 3464, <c>multipart/report; report-type=delivery-status</c>) and abuse
/// feedback reports (RFC 5965, <c>report-type=feedback-report</c>). No dependency: it handles exactly the parts it needs (header unfolding, one level of multipart, the
/// status fields) and returns nothing for anything else. Classification: action <c>failed</c> with a 5.x.x status is a hard bounce, except 5.2.2 (mailbox full) which is soft;
/// <c>delayed</c> and every 4.x.x status are soft; a feedback report of type abuse, fraud or virus is a complaint; other actions (delivered, relayed, expanded) are not reports.
/// </summary>
public static partial class DsnParser
{
    public const int MaxBytes = 256 * 1024;

    [GeneratedRegex(@"boundary\s*=\s*(?:""([^""]{1,200})""|([^\s;]{1,200}))", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex BoundaryPattern();

    [GeneratedRegex(@"report-type\s*=\s*""?([a-z\-]+)""?", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ReportTypePattern();

    [GeneratedRegex(@"<?([^<>\s;]+@[^<>\s;]+)>?", RegexOptions.None, 1000)]
    private static partial Regex AddressPattern();

    [GeneratedRegex(@"^[245]\.[0-9]{1,3}\.[0-9]{1,3}$", RegexOptions.None, 1000)]
    private static partial Regex StatusPattern();

    [GeneratedRegex(@"\n[ \t]*\n", RegexOptions.None, 1000)]
    private static partial Regex BlankLine();

    public static IReadOnlyList<ParsedBounce> Parse(string raw)
    {
        if (raw.Length > MaxBytes) return [];
        var (headers, body) = Split(raw);
        var contentType = Header(headers, "Content-Type") ?? "";
        if (!contentType.StartsWith("multipart/report", StringComparison.OrdinalIgnoreCase)) return [];
        var type = ReportTypePattern().Match(contentType) is { Success: true } rt ? rt.Groups[1].Value.ToLowerInvariant() : "";
        var boundary = BoundaryPattern().Match(contentType) is { Success: true } b ? (b.Groups[1].Success ? b.Groups[1].Value : b.Groups[2].Value) : null;
        if (boundary is null) return [];
        var parts = SplitParts(body, boundary);

        string? tag = null;
        foreach (var part in parts)
        {
            var (ph, pb) = Split(part);
            var ct = Header(ph, "Content-Type") ?? "";
            if (ct.StartsWith("text/rfc822-headers", StringComparison.OrdinalIgnoreCase)) tag ??= Header(Unfold(pb.Split('\n')), BounceTags.Header);
            else if (ct.StartsWith("message/rfc822", StringComparison.OrdinalIgnoreCase)) tag ??= Header(Split(pb).Headers, BounceTags.Header);
        }

        var result = new List<ParsedBounce>();
        foreach (var part in parts)
        {
            var (ph, pb) = Split(part);
            var ct = Header(ph, "Content-Type") ?? "";
            if (type == "delivery-status" && ct.StartsWith("message/delivery-status", StringComparison.OrdinalIgnoreCase)) result.AddRange(DeliveryStatus(pb, tag));
            else if (type == "feedback-report" && ct.StartsWith("message/feedback-report", StringComparison.OrdinalIgnoreCase) && Complaint(pb, tag) is { } c) result.Add(c);
        }
        return result;
    }

    private static IEnumerable<ParsedBounce> DeliveryStatus(string body, string? tag)
    {
        // The first block describes the message, every following block one recipient.
        var blocks = BlankLine().Split(body.Replace("\r\n", "\n", StringComparison.Ordinal)).Where(x => x.Trim().Length > 0).Skip(1);
        foreach (var block in blocks)
        {
            var h = Unfold(block.Split('\n'));
            var recipient = Header(h, "Final-Recipient") ?? Header(h, "Original-Recipient");
            var action = (Header(h, "Action") ?? "").Trim().ToLowerInvariant();
            var status = Header(h, "Status")?.Trim();
            if (recipient is null) continue;
            var semi = recipient.IndexOf(';', StringComparison.Ordinal);
            if (AddressPattern().Match(semi >= 0 ? recipient[(semi + 1)..] : recipient) is not { Success: true } m) continue;
            if (status is not null && !StatusPattern().IsMatch(status)) status = null;
            string kind;
            if (action == "failed" && (status is null || status.StartsWith('5'))) kind = status == "5.2.2" ? BounceKinds.Soft : BounceKinds.Hard;
            else if (action == "delayed" || (action == "failed" && status is not null && status.StartsWith('4'))) kind = BounceKinds.Soft;
            else continue;
            yield return new ParsedBounce(new BounceReport(m.Groups[1].Value, kind, status, Header(h, "Diagnostic-Code")), tag);
        }
    }

    private static ParsedBounce? Complaint(string body, string? tag)
    {
        var h = Unfold(body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        var type = (Header(h, "Feedback-Type") ?? "").Trim().ToLowerInvariant();
        if (type is not ("abuse" or "fraud" or "virus")) return null;
        var recipient = Header(h, "Original-Rcpt-To") ?? Header(h, "Removal-Recipient");
        if (recipient is null || AddressPattern().Match(recipient) is not { Success: true } m) return null;
        return new ParsedBounce(new BounceReport(m.Groups[1].Value, BounceKinds.Complaint, null, "Feedback report: " + type), tag);
    }

    private static (List<KeyValuePair<string, string>> Headers, string Body) Split(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var cut = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        var head = cut < 0 ? normalized : normalized[..cut];
        return (Unfold(head.Split('\n')), cut < 0 ? "" : normalized[(cut + 2)..]);
    }

    private static List<KeyValuePair<string, string>> Unfold(IEnumerable<string> lines)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            if ((line[0] is ' ' or '\t') && list.Count > 0) list[^1] = new(list[^1].Key, list[^1].Value + " " + line.Trim());
            else if (line.IndexOf(':', StringComparison.Ordinal) is var i and > 0) list.Add(new(line[..i].Trim(), line[(i + 1)..].Trim()));
        }
        return list;
    }

    private static string? Header(List<KeyValuePair<string, string>> headers, string name) =>
        headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static List<string> SplitParts(string body, string boundary)
    {
        var parts = new List<string>();
        var marker = "--" + boundary;
        var sb = new StringBuilder();
        var inPart = false;
        foreach (var line in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (trimmed == marker || trimmed == marker + "--")
            {
                if (inPart) parts.Add(sb.ToString().TrimEnd('\n'));
                sb.Clear();
                inPart = trimmed == marker;
                continue;
            }
            if (inPart) sb.Append(line).Append('\n');
        }
        return parts;
    }
}
