using Keel.Core;
using Keel.Workspace;

namespace Keel.Web.Services;

/// <summary>
/// The starting values of the quick forms (ADR 0061, assumption A73): the person's last choice if it is still on the administrator's list, else the common
/// default if the list has it, else the first item of the list (so an administrator sets the default by ordering the list). Only the choice of a code from
/// a list is defaulted: nothing here bypasses a rule, and the server validates the values again.
/// </summary>
public static class QuickDefaults
{
    public const string PartType = "Component";
    public const string PartSource = "Make";
    public const string DocumentType = "Drawing";

    public static string Pick(IReadOnlyList<string> list, IReadOnlyDictionary<string, string> last, string key, string common)
    {
        if (last.TryGetValue(key, out var v) && list.Contains(v)) return v;
        if (list.Contains(common)) return common;
        return list.Count > 0 ? list[0] : common;
    }

    /// <summary>
    /// The markings a new object starts with: the standard export, CUI and distribution defaults of the form, with the program set to the person's own
    /// program (the first they belong to) and the owning organization to theirs. Defaulting to the person's own program means a new part is one they can
    /// read back; the markings are still validated as usual and shown on a chip with a Change button.
    /// </summary>
    public static MarkingsInput Markings(Subject subject)
    {
        var m = new MarkingsInput();
        m.Program = subject.Programs.FirstOrDefault() ?? "";
        if (!string.IsNullOrWhiteSpace(subject.Org)) m.OwningOrg = subject.Org.Trim();
        return m;
    }

    /// <summary>The chip text of the markings being entered, or a short sentence when they are not valid yet.</summary>
    public static string ChipText(MarkingsInput input)
    {
        var result = input.Effective().ToMarkings();
        return result.IsSuccess ? result.Value.BannerText() : "Markings incomplete";
    }

    public static string DefaultLinkType(IReadOnlyDictionary<string, string> last) =>
        last.TryGetValue(LastChoiceKeys.LinkType, out var v) && Keel.Documents.LinkTypes.All.Contains(v) ? v : Keel.Documents.LinkTypes.Describes;

    /// <summary>The two link types in the words people use (the domain names stay "describes" and "references", ADR-0016).</summary>
    public static readonly IReadOnlyList<(string Value, string Label, string Help)> LinkChoices =
    [
        (Keel.Documents.LinkTypes.Describes, "Describes this part (for example its drawing)", "Points at one revision of the document, so the part keeps showing the revision it was released with."),
        (Keel.Documents.LinkTypes.References, "The part follows this document (for example a specification)", "Always means the latest released revision of the document."),
    ];
}
