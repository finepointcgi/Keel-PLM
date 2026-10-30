using Keel.Core;
using Microsoft.Extensions.Localization;

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
        ["document.read_denied"] = "You are not authorized to see the linked document.",
        ["child.not_readable"] = "You are not authorized to see a part this change would involve.",
        ["sourcing.foreign_supplier_itar_part"] = "A foreign supplier is never selected for, or sent data about, an ITAR-marked part.",
        ["portal.not_linked"] = "Your account is not linked to a supplier.",
        ["portal.no_clearance_profile"] = "The supplier has no clearance profile, so no data may be released to it.",
        ["stepup.required"] = "Signing needs a fresh sign-in. You will be asked to sign in again.",
    };

    // A machine reason code looks like "area.some_reason" (optionally with ":detail"); it is never shown to people as it is.
    private static readonly System.Text.RegularExpressions.Regex CodeShape = new(@"^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+(:.*)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>True when the text is a machine reason code rather than a sentence.</summary>
    public static bool IsReasonCode(string text) => CodeShape.IsMatch(text);

    public static string Reason(string code)
    {
        if (code.StartsWith("new_markings:", StringComparison.Ordinal)) return "With the new markings: " + Reason(code["new_markings:".Length..]);
        if (code.StartsWith("cui.category_missing:", StringComparison.Ordinal)) return $"You are not authorized for CUI category {code.Split(':')[1]}.";
        if (Reasons.TryGetValue(code, out var text)) return text;
        // A code nobody wrote text for is still not shown raw: the audit log has the exact code for support.
        return IsReasonCode(code) ? "You are not allowed to do this." : code;
    }

    /// <summary>
    /// The reason in the person's language (resource keys <c>Reason.&lt;code&gt;</c> in CommonStrings, ADR 0057). A code with no translation falls back to the English
    /// text above, so an untranslated reason is shown in English, never as a raw code. The reason code in the audit log is the same in every language.
    /// </summary>
    public static string Reason(string code, IStringLocalizer common)
    {
        if (code.StartsWith("new_markings:", StringComparison.Ordinal)) return common["Reason.NewMarkings", Reason(code["new_markings:".Length..], common)].Value;
        if (code.StartsWith("cui.category_missing:", StringComparison.Ordinal)) return common["Reason.CuiCategory", code.Split(':')[1]].Value;
        var fallback = Reason(code);
        return Reasons.ContainsKey(code) ? Localization.LocalizerExtensions.Or(common, "Reason." + code, fallback) : fallback;
    }

    /// <summary>The message of an outcome in the person's language. Messages that come from a module's validation are that module's own text and are shown as written.</summary>
    public static string Message<T>(Outcome<T> o, IStringLocalizer common) => o.Kind switch
    {
        OutcomeKind.Forbidden => string.Join(" ", o.Errors.Select(e => Reason(e, common))),
        OutcomeKind.NotFound => common["Reason.NotFound"].Value,
        _ => string.Join(" ", o.Errors),
    };

    public static string Message<T>(Outcome<T> o) => o.Kind switch
    {
        OutcomeKind.Forbidden => string.Join(" ", o.Errors.Select(Reason)),
        OutcomeKind.NotFound => "Not found, or you are not authorized to see it.",
        _ => string.Join(" ", o.Errors),
    };

    public static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public const string Unclassified = "UNCLASSIFIED";
}
