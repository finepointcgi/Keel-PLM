using Keel.Core;

namespace Keel.Web.Services;

public static class UiText
{
    private static readonly Dictionary<string, string> Reasons = new(StringComparer.Ordinal)
    {
        ["role.missing"] = "Your roles do not permit this action.",
        ["export.us_person_or_authorization_required"] = "Export-controlled: requires U.S.-person status or an export authorization listed on the object.",
        ["program.not_member"] = "You are not a member of the object's program.",
        ["org.not_owner"] = "Only the owning organization or program members may modify this object.",
        ["checkout.not_owner"] = "Only the user who checked it out (or an administrator) can do this.",
        ["number.explicit_requires_admin"] = "Only administrators may assign explicit numbers.",
    };

    public static string Reason(string code)
    {
        if (code.StartsWith("new_markings:", StringComparison.Ordinal)) return "With the new markings: " + Reason(code["new_markings:".Length..]);
        if (code.StartsWith("cui.category_missing:", StringComparison.Ordinal)) return $"You are not authorized for CUI category {code.Split(':')[1]}.";
        return Reasons.TryGetValue(code, out var text) ? text : code;
    }

    public static string Message<T>(Outcome<T> o) => o.Kind switch
    {
        OutcomeKind.Forbidden => string.Join(" ", o.Errors.Select(Reason)),
        OutcomeKind.NotFound => "Not found, or you are not authorized to see it.",
        _ => string.Join(" ", o.Errors),
    };

    public static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public const string Unclassified = "UNCLASSIFIED";
}
