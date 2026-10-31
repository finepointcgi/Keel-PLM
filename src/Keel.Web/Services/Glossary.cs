namespace Keel.Web.Services;

/// <summary>
/// Plain-language explanations of the terms that stay on screen because the architecture decisions fix them (ADR 0061). Shown as a line of text next to the
/// term (<c>GlossaryHint</c>), never as a tooltip: a tooltip cannot be read on a touch screen or with a keyboard. The full glossary is in the user manual.
/// </summary>
public static class Glossary
{
    private static readonly Dictionary<string, string> Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["version"] = "A version is one saved state of the item, for example A.1, then A.2 after the next save. Saving never changes an older version.",
        ["revision"] = "A revision (A, B, C) is a formal, controlled release of the item. A released revision is changed only by a change order, which creates the next revision.",
        ["working copy"] = "The working copy is your private draft. Nobody else sees it, and nobody else can edit the item while you hold it, until you save or discard it.",
        ["check out"] = "Checking out reserves the item for you so that you can change it. Edit does this for you; Save does the matching check in.",
        ["check in"] = "Checking in stores your draft as the next version and lets others edit the item again. Save does this for you.",
        ["markings"] = "Markings say who may see the item: export control, CUI category, distribution and program. Only people allowed under all of them can open it.",
        ["released"] = "A released part is locked. To change it, start a change order; the new revision is released with the order.",
        ["change control board"] = "The change control board (CCB) is the group that decides whether a Class I change goes ahead. Class II changes are smaller and do not need the board.",
        ["describes"] = "Describes: the document shows what the part is, such as its drawing. The part points at one revision of the document.",
        ["references"] = "References: the part follows the document, such as a specification. It always means the latest released revision.",
    };

    public static string? Explain(string term) => Entries.TryGetValue(term, out var text) ? text : null;

    public static IReadOnlyCollection<string> Terms => Entries.Keys;
}
