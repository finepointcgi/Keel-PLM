using System.Collections.Concurrent;
using System.Globalization;
using System.Resources;
using System.Text;
using Microsoft.Extensions.Localization;

namespace Keel.Web.Localization;

/// <summary>
/// <see cref="IStringLocalizer{T}"/> over the ordinary .resx resources and their satellite assemblies (no external service). It reads the circuit's <see cref="UiLanguage"/>
/// rather than the thread culture, and answers the pseudo-locale from the English text. A key that is missing everywhere comes back as itself with
/// <see cref="LocalizedString.ResourceNotFound"/> set, which the key-parity test and the unconverted-text scan treat as a defect.
/// </summary>
public sealed class KeelLocalizer<T>(UiLanguage language) : IStringLocalizer<T>
{
    private static readonly ResourceManager Manager = new(typeof(T));

    public LocalizedString this[string name] => Get(name, []);

    public LocalizedString this[string name, params object[] arguments] => Get(name, arguments);

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
        Manager.GetResourceSet(language.IsPseudo ? CultureInfo.InvariantCulture : language.Culture, createIfNotExists: true, tryParents: true)?
            .Cast<System.Collections.DictionaryEntry>().Select(e => Get((string)e.Key, [])) ?? [];

    private LocalizedString Get(string name, object[] args)
    {
        var culture = language.Culture;
        string? raw = language.IsPseudo ? Manager.GetString(name, CultureInfo.InvariantCulture) : Manager.GetString(name, culture);
        if (raw is null) return new LocalizedString(name, name, resourceNotFound: true);
        if (language.IsPseudo && !name.StartsWith("Format.", StringComparison.Ordinal)) raw = Pseudo.Transform(raw);
        return new LocalizedString(name, args.Length == 0 ? raw : string.Format(culture, raw, args));
    }
}

/// <summary>The pseudo-locale transform: accents every letter, pads by about a third (translations are longer) and brackets the text, leaving {0}-style placeholders alone.</summary>
public static class Pseudo
{
    private const string From = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string To = "àƀçđéƒĝĥíĵķļɱñöþqŕšţûvŵxýžÀßÇÐÉFĜĤÍĴĶĻMÑÖÞQŔŠŢÛVŴXÝŽ";

    public static string Transform(string text)
    {
        var sb = new StringBuilder("[");
        var inPlaceholder = false;
        foreach (var ch in text)
        {
            if (ch == '{') inPlaceholder = true;
            if (inPlaceholder) sb.Append(ch);
            else { var i = From.IndexOf(ch, StringComparison.Ordinal); sb.Append(i >= 0 ? To[i] : ch); }
            if (ch == '}') inPlaceholder = false;
        }
        sb.Append(' ').Append('·', Math.Max(2, text.Length / 3)).Append(']');
        return sb.ToString();
    }
}

public static class LocalizerExtensions
{
    /// <summary>The translation of a key built from data (a navigation label, a palette command), or the given English text when no key exists for it.</summary>
    public static string Or(this IStringLocalizer localizer, string key, string english)
    {
        var s = localizer[key];
        return s.ResourceNotFound ? english : s.Value;
    }

    /// <summary>The label of a part, document or change state code ("InWork" shows "In Work", or "En trabajo" in Spanish). A state with no resource shows its English label.</summary>
    public static string StateLabel(this IStringLocalizer localizer, string state)
    {
        var english = Services.StateText.Label(state);
        return english.Length == 0 ? english : localizer.Or(LocalizationServices.Key("State", english), english);
    }

    /// <summary>Plural by two keys (<c>Key.One</c> for exactly one, <c>Key.Other</c> otherwise), never by gluing a suffix onto a sentence. The count is the first argument.</summary>
    public static string Count(this IStringLocalizer localizer, string key, long count) => localizer[count == 1 ? key + ".One" : key + ".Other", count].Value;
}
