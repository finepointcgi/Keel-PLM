using System.Collections.Concurrent;
using System.Globalization;
using System.Resources;
using Keel.Web.Resources;

namespace Keel.Web.Localization;

/// <summary>
/// The language of one circuit (scoped). It is set once from the person's stored preference by the layout, before any page renders; a language change reloads the
/// page, so a circuit never changes language half way. It is deliberately NOT the thread's CurrentCulture: parsing, exports, SQL parameters and hashes keep the
/// invariant culture, and only screen text and display formatting follow the language (ADR 0057).
/// </summary>
public sealed class UiLanguage(Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
{
    private string _tag = Locales.Source;

    /// <summary>Whether the generated pseudo-locale may be chosen (a test and review aid, off by default).</summary>
    public bool PseudoEnabled { get; } = bool.TryParse(configuration?["Keel:Localization:PseudoLocale"], out var on) && on;

    public string Tag => _tag;

    public bool IsPseudo => string.Equals(_tag, Locales.PseudoTag, StringComparison.OrdinalIgnoreCase);

    /// <summary>The culture whose resources are read and whose number and date formats are used for display.</summary>
    public CultureInfo Culture => Cultures.For(_tag);

    public IReadOnlyList<Locale> Available => Locales.Available(PseudoEnabled);

    public void Set(string? tag) => _tag = Locales.Resolve(tag, PseudoEnabled);

    // ---- display formatting (the culture's own separators and patterns; time is always shown in UTC, as before) ----

    /// <summary>A moment as date and time in UTC, in the language's order ("2026-10-30 14:05" in English, "30/10/2026 14:05" in Spanish).</summary>
    public string Utc(DateTimeOffset t) => t.UtcDateTime.ToString(Culture.DateTimeFormat.ShortDatePattern + " " + Culture.DateTimeFormat.ShortTimePattern, Culture);

    /// <summary>The UTC date alone.</summary>
    public string Date(DateTimeOffset t) => t.UtcDateTime.ToString(Culture.DateTimeFormat.ShortDatePattern, Culture);

    /// <summary>The UTC date written out with the month name ("30 October 2026", "30 de octubre de 2026").</summary>
    public string LongDate(DateTimeOffset t) => t.UtcDateTime.ToString(Culture.DateTimeFormat.LongDatePattern, Culture);

    /// <summary>A number with the language's separators, at most <paramref name="maxDecimals"/> decimals and no trailing zeros.</summary>
    public string Number(decimal value, int maxDecimals = 3) => value.ToString("#,##0." + new string('#', maxDecimals), Culture);

    /// <summary>A quantity and its unit, for example "12,5 kg". The unit symbol comes from the resources when the language has one for it (EA, SET, in, ft).</summary>
    public string Quantity(decimal value, string unitCode, Microsoft.Extensions.Localization.IStringLocalizer common, int maxDecimals = 3) => Number(value, maxDecimals) + " " + Unit(unitCode, common);

    /// <summary>The unit's display symbol in this language; a code with no resource is shown as it is.</summary>
    public static string Unit(string code, Microsoft.Extensions.Localization.IStringLocalizer common) => LocalizerExtensions.Or(common, "Unit." + code.ToLowerInvariant(), code);
}

/// <summary>
/// Display cultures built without OS culture data (the images run with invariant globalization, and an air-gapped host has no ICU database to rely on): the
/// separators, date patterns and month names of a language are ordinary resource strings (<c>Format.*</c> in CommonStrings), so a translator supplies them with the text.
/// </summary>
public static class Cultures
{
    private static readonly ConcurrentDictionary<string, CultureInfo> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ResourceManager Common = new(typeof(CommonStrings));

    public static CultureInfo For(string tag) => Cache.GetOrAdd(tag, Build);

    private static CultureInfo Build(string tag)
    {
        // The source language and the pseudo-locale keep the neutral (English) formats; the pseudo-locale exists to reveal text, not to move separators.
        var resolved = Locales.Resolve(tag, pseudoEnabled: true);
        var lookup = string.Equals(resolved, Locales.PseudoTag, StringComparison.OrdinalIgnoreCase) ? CultureInfo.InvariantCulture : new CultureInfo(resolved);
        var culture = new CultureInfo(resolved);
        string Get(string key) => Common.GetString(key, lookup) ?? throw new InvalidOperationException($"Resource key {key} is missing from CommonStrings.");
        var number = culture.NumberFormat;
        number.NumberDecimalSeparator = Get("Format.DecimalSeparator");
        number.NumberGroupSeparator = Get("Format.GroupSeparator");
        number.CurrencyDecimalSeparator = number.NumberDecimalSeparator;
        number.CurrencyGroupSeparator = number.NumberGroupSeparator;
        number.PercentDecimalSeparator = number.NumberDecimalSeparator;
        number.PercentGroupSeparator = number.NumberGroupSeparator;
        var date = culture.DateTimeFormat;
        date.ShortDatePattern = Get("Format.DatePattern");
        date.LongDatePattern = Get("Format.LongDatePattern");
        date.ShortTimePattern = Get("Format.TimePattern");
        date.MonthNames = Twelve(Get("Format.MonthNames"));
        date.MonthGenitiveNames = date.MonthNames;
        date.AbbreviatedMonthNames = Twelve(Get("Format.MonthNamesShort"));
        date.AbbreviatedMonthGenitiveNames = date.AbbreviatedMonthNames;
        date.DayNames = Seven(Get("Format.DayNames"));
        date.AbbreviatedDayNames = Seven(Get("Format.DayNamesShort"));
        date.FirstDayOfWeek = Enum.Parse<DayOfWeek>(Get("Format.FirstDayOfWeek"));
        return CultureInfo.ReadOnly(culture);
    }

    // The invariant calendar has a thirteenth, empty month name; arrays given to DateTimeFormatInfo must keep that shape.
    private static string[] Twelve(string list) => [.. Split(list, 12), ""];

    private static string[] Seven(string list) => Split(list, 7);

    private static string[] Split(string list, int count)
    {
        var parts = list.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == count ? parts : throw new InvalidOperationException($"A Format name list needs {count} entries separated by commas, found {parts.Length}.");
    }
}
