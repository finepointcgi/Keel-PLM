namespace Keel.Web.Localization;

/// <summary>One interface language: its tag (also the culture name of the satellite resources) and the name people see for it, written in that language.</summary>
public sealed record Locale(string Tag, string NativeName, bool Pseudo = false);

/// <summary>
/// The languages Keel ships (ADR 0057). Adding a language is a translation job: add <c>Resources/*.&lt;tag&gt;.resx</c> next to every English resource file, then one line here.
/// The key-parity test (LocalizationTests) fails until the new files hold every key with the same placeholders. The pseudo-locale is a test aid, not a language: it is generated
/// from the English text at run time and offered in the picker only when <c>Keel:Localization:PseudoLocale</c> is on.
/// </summary>
public static class Locales
{
    /// <summary>The language of the source text (the neutral resource files).</summary>
    public const string Source = "en";

    /// <summary>The generated pseudo-locale: accented, padded English that makes untranslated text, clipped layouts and glued-together sentences visible.</summary>
    public const string PseudoTag = "qps-ploc";

    public static readonly IReadOnlyList<Locale> Shipped = [new(Source, "English"), new("es", "Español")];

    public static readonly Locale Pseudo = new(PseudoTag, "Pseudo (testing)", Pseudo: true);

    /// <summary>The tags a person may choose.</summary>
    public static IReadOnlyList<Locale> Available(bool pseudoEnabled) => pseudoEnabled ? [.. Shipped, Pseudo] : Shipped;

    /// <summary>A stored tag that is not (or no longer) offered reads English: a damaged or retired value never breaks a page.</summary>
    public static string Resolve(string? tag, bool pseudoEnabled = false) =>
        Available(pseudoEnabled).FirstOrDefault(l => string.Equals(l.Tag, tag, StringComparison.OrdinalIgnoreCase))?.Tag ?? Source;
}
