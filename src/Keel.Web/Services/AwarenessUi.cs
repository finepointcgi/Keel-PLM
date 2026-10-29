using System.Globalization;
using Keel.Notifications;

namespace Keel.Web.Services;

/// <summary>"just now", "5 min ago", "3 h ago", then the date. The exact UTC time is always in the tooltip of <c>TimeStamp</c>.</summary>
public static class RelativeTime
{
    public static string Format(DateTimeOffset at, DateTimeOffset now)
    {
        var age = now - at;
        if (age < TimeSpan.FromSeconds(45)) return "just now";
        if (age < TimeSpan.FromMinutes(60)) return $"{Math.Max(1, (int)Math.Round(age.TotalMinutes)).ToString(CultureInfo.InvariantCulture)} min ago";
        if (age < TimeSpan.FromHours(24)) return $"{((int)age.TotalHours).ToString(CultureInfo.InvariantCulture)} h ago";
        return at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>The heading of a day group on the notifications page: "Today", "Yesterday" or the date.</summary>
    public static string DayHeading(DateTimeOffset at, DateTimeOffset now)
    {
        var day = DateOnly.FromDateTime(at.UtcDateTime);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Comment drafts, kept in memory for the circuit so that switching tabs (or opening another object and coming back) does not lose what was being typed. Not persisted:
/// closing the browser or ending the session discards them, and nothing about a draft is ever stored on the server beyond this object's lifetime. Keyed by object.
/// </summary>
public sealed class CommentDrafts
{
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);

    private static string Key(string kind, string number, Guid? replyTo) => $"{kind}|{number}|{replyTo?.ToString("D") ?? "new"}";

    public string Get(string kind, string number, Guid? replyTo = null) => _drafts.GetValueOrDefault(Key(kind, number, replyTo), "");

    public void Set(string kind, string number, string text, Guid? replyTo = null)
    {
        if (string.IsNullOrEmpty(text)) _drafts.Remove(Key(kind, number, replyTo));
        else _drafts[Key(kind, number, replyTo)] = text.Length > AwarenessLimits.CommentLength ? text[..AwarenessLimits.CommentLength] : text;
    }

    public void Clear(string kind, string number, Guid? replyTo = null) => _drafts.Remove(Key(kind, number, replyTo));
}
