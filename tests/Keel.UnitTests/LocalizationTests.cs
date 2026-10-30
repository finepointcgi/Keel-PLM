using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bunit;
using Keel.Core;
using Keel.Web.Components.Layout;
using Keel.Web.Components.Pages.Workflow;
using Keel.Web.Components.Shared;
using Keel.Web.Localization;
using Keel.Web.Resources;
using Keel.Web.Services;
using Keel.Workflow;
using Keel.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MudBlazor.Services;
using static Keel.UnitTests.ShellTestKit;

namespace Keel.UnitTests;

/// <summary>
/// Localization foundation (ADR 0057): the machinery that makes adding a language a translation-only job. The parity tests read the .resx files on disk, so a locale
/// that misses a key (or has an orphan, an empty value or different placeholders) fails the build; the scan tests keep sentences whole and keep the legal marking text
/// out of the resources; the component tests show a real second language and the pseudo-locale in the converted shell.
/// </summary>
public sealed partial class LocalizationTests : BunitContext
{
    private static readonly string[] ResourceNames = ["ShellStrings", "CommonStrings", "PageStrings"];

    public LocalizationTests()
    {
        Register(this, Roles.Viewer, Roles.Admin, Roles.Auditor);
        Services.AddMudServices();
    }

    /// <summary>Configuration with the pseudo-locale switched on.</summary>
    private sealed class PseudoOn : Microsoft.Extensions.Configuration.IConfiguration
    {
        public string? this[string key] { get => key == "Keel:Localization:PseudoLocale" ? "true" : null; set => throw new NotSupportedException(); }
        public IEnumerable<Microsoft.Extensions.Configuration.IConfigurationSection> GetChildren() => [];
        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() => throw new NotSupportedException();
        public Microsoft.Extensions.Configuration.IConfigurationSection GetSection(string key) => throw new NotSupportedException();
    }

    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }

    private static string ResourceDir => Path.Combine(Root, "src", "Keel.Web", "Resources");

    private static Dictionary<string, string> Read(string name, string? tag)
    {
        var path = Path.Combine(ResourceDir, tag is null ? $"{name}.resx" : $"{name}.{tag}.resx");
        Assert.True(File.Exists(path), $"{path} is missing: every shipped language needs every resource file.");
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in XDocument.Load(path).Root!.Elements("data"))
        {
            var key = (string)d.Attribute("name")!;
            Assert.True(keys.TryAdd(key, d.Element("value")!.Value), $"{name}.{tag}: duplicate key {key}");
        }
        return keys;
    }

    [GeneratedRegex(@"\{(\d+)(?:[:,][^}]*)?\}")]
    private static partial Regex Placeholder();

    private static string Placeholders(string v) => string.Join(",", Placeholder().Matches(v).Select(m => m.Groups[1].Value).Distinct().Order());

    // ---------------------------------------------------------------- the extraction / parity tests

    [Fact]
    public void Every_shipped_language_has_every_resource_file_and_every_resource_file_is_a_shipped_language()
    {
        var onDisk = Directory.GetFiles(ResourceDir, "*.resx").Select(Path.GetFileNameWithoutExtension).Select(n => n!.Split('.')).Where(p => p.Length == 2).Select(p => p[1]).ToHashSet();
        var shipped = Locales.Shipped.Where(l => l.Tag != Locales.Source).Select(l => l.Tag).ToHashSet();
        Assert.Equal(shipped.Order(), onDisk.Order()); // a file without a Locales entry (or the reverse) is a half-added language
        Assert.DoesNotContain(Locales.PseudoTag, onDisk); // the pseudo-locale is generated, never a file that can drift
    }

    [Theory]
    [InlineData("ShellStrings")]
    [InlineData("CommonStrings")]
    [InlineData("PageStrings")]
    public void A_shipped_language_has_exactly_the_keys_of_the_source_with_the_same_placeholders_and_no_empty_text(string resource)
    {
        var source = Read(resource, null);
        Assert.NotEmpty(source);
        foreach (var locale in Locales.Shipped.Where(l => l.Tag != Locales.Source))
        {
            var translated = Read(resource, locale.Tag);
            Assert.True(!source.Keys.Except(translated.Keys).Any(), $"{resource}.{locale.Tag} misses: {string.Join(", ", source.Keys.Except(translated.Keys))}");
            Assert.True(!translated.Keys.Except(source.Keys).Any(), $"{resource}.{locale.Tag} has keys the source does not: {string.Join(", ", translated.Keys.Except(source.Keys))}");
            foreach (var (key, english) in source)
            {
                Assert.False(string.IsNullOrWhiteSpace(translated[key]), $"{resource}.{locale.Tag}.{key} is empty");
                Assert.True(Placeholders(english) == Placeholders(translated[key]), $"{resource}.{locale.Tag}.{key}: placeholders {{{Placeholders(translated[key])}}} differ from the source {{{Placeholders(english)}}}");
            }
        }
        Assert.All(source, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"{resource}.{kv.Key} is empty in the source"));
    }

    [Fact]
    public void Plural_keys_come_in_pairs_in_every_language()
    {
        foreach (var resource in ResourceNames)
            foreach (var tag in new string?[] { null }.Concat(Locales.Shipped.Where(l => l.Tag != Locales.Source).Select(l => l.Tag)))
            {
                var keys = Read(resource, tag).Keys.ToHashSet();
                foreach (var k in keys.Where(k => k.EndsWith(".One", StringComparison.Ordinal))) Assert.Contains(k[..^4] + ".Other", keys);
                foreach (var k in keys.Where(k => k.EndsWith(".Other", StringComparison.Ordinal))) Assert.Contains(k[..^6] + ".One", keys);
            }
    }

    [Fact]
    public void Every_literal_key_the_converted_components_ask_for_exists_in_the_resource_it_names()
    {
        var resources = ResourceNames.ToDictionary(n => n, n => Read(n, null));
        var checkedKeys = 0;
        foreach (var file in ConvertedSources())
        {
            var text = File.ReadAllText(file);
            // "IStringLocalizer<PageStrings> P" (injected or a property) tells which resource the variable P reads.
            var vars = Regex.Matches(text, @"IStringLocalizer<(\w+)>\s+(\w+)").ToDictionary(m => m.Groups[2].Value, m => m.Groups[1].Value);
            foreach (var (variable, resource) in vars)
            {
                Assert.True(resources.ContainsKey(resource), $"{file}: unknown resource {resource}");
                foreach (Match m in Regex.Matches(text, $@"\b{variable}\[""([^""]+)""\s*[\],]"))
                {
                    Assert.True(resources[resource].ContainsKey(m.Groups[1].Value), $"{Path.GetFileName(file)}: key {m.Groups[1].Value} is not in {resource}.resx");
                    checkedKeys++;
                }
                foreach (Match m in Regex.Matches(text, $@"\b{variable}\.(?:Or|Count)\(""([^""]+)"""))
                {
                    var key = m.Groups[1].Value;
                    if (m.Value.Contains(".Count(", StringComparison.Ordinal)) { Assert.True(resources[resource].ContainsKey(key + ".One"), $"{Path.GetFileName(file)}: {key}.One missing"); checkedKeys++; }
                    else if (!key.EndsWith('.')) { Assert.True(resources[resource].ContainsKey(key), $"{Path.GetFileName(file)}: key {key} is not in {resource}.resx"); checkedKeys++; }
                }
            }
        }
        Assert.True(checkedKeys > 250, $"only {checkedKeys} keys were checked: the scan lost track of the converted components");
    }

    private static IEnumerable<string> ConvertedSources() =>
        Directory.GetFiles(Path.Combine(Root, "src", "Keel.Web"), "*.razor", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(Root, "src", "Keel.Web"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("IStringLocalizer<", StringComparison.Ordinal) && !f.EndsWith("KeelLocalizer.cs", StringComparison.Ordinal) && !f.EndsWith("LocalizationServices.cs", StringComparison.Ordinal));

    [Fact]
    public void Labels_that_come_from_data_have_a_translation_in_every_language()
    {
        // Navigation (groups and links, every role, the supplier portal), the palette (every role) and the kinds are looked up by a key built from the English text.
        string[][] roleSets = [[Roles.Viewer], [Roles.Admin, Roles.Auditor, Roles.RecordsManager], [Roles.Author]];
        var missing = new List<string>();
        var shell = Read("ShellStrings", null);
        var common = Read("CommonStrings", null);
        foreach (var roles in roleSets)
            foreach (var g in NavModel.For(roles).Concat(NavModel.ForSupplier()))
            {
                if (!shell.ContainsKey(LocalizationServices.Key("Nav.group", g.Title))) missing.Add("group " + g.Title);
                foreach (var i in g.Items) if (!shell.ContainsKey(LocalizationServices.Key("Nav.item", i.Label))) missing.Add("item " + i.Label);
            }
        foreach (var roles in roleSets)
            foreach (var a in PaletteActions.For(roles, canLock: true))
            {
                if (!shell.ContainsKey("Palette." + a.Id)) missing.Add("palette " + a.Id);
                if (!shell.ContainsKey(LocalizationServices.Key("Palette.group", a.Group))) missing.Add("palette group " + a.Group);
            }
        foreach (var kind in ObjectKinds.All) { if (!common.ContainsKey("Kind." + kind) || !common.ContainsKey("Kinds." + kind)) missing.Add("kind " + kind); }
        foreach (var s in new[] { "InWork", "UnderReview", "Released", "Obsolete" }) if (!common.ContainsKey(LocalizationServices.Key("State", StateText.Label(s)))) missing.Add("state " + s);
        foreach (var t in ThemeNames.All) if (!shell.ContainsKey("Theme." + t)) missing.Add("theme " + t);
        foreach (var d in DensityNames.All) if (!shell.ContainsKey("Density." + d)) missing.Add("density " + d);
        foreach (var u in UnitsOfMeasure.All) if (!common.ContainsKey("Unit." + u.ToLowerInvariant())) missing.Add("unit " + u);
        foreach (var code in new[] { "role.missing", "program.not_member", "org.not_owner", "stepup.required", "document.read_denied", "task.not_assignee", "sod.submitter" }) if (!common.ContainsKey("Reason." + code)) missing.Add("reason " + code);
        Assert.True(missing.Count == 0, "no resource key for: " + string.Join("; ", missing)); // the shared files are checked against Spanish by the parity test above
    }

    // ---------------------------------------------------------------- sentences stay whole

    [Fact]
    public void Converted_components_never_build_a_sentence_by_gluing_translated_pieces_together()
    {
        var offenders = new List<string>();
        foreach (var file in ConvertedSources())
        {
            var text = File.ReadAllText(file);
            var vars = Regex.Matches(text, @"IStringLocalizer<\w+>\s+(\w+)").Select(m => m.Groups[1].Value).Distinct().ToList();
            foreach (var v in vars)
            {
                // "P["a"] + x", "x + P["a"]", "$"... {P["a"]} ..."" and ".ToLower" on a whole translated sentence are all gluing.
                if (Regex.IsMatch(text, $@"\b{v}\[""[^""]+""(?:,[^\]]*)?\]\s*\+")) offenders.Add($"{Path.GetFileName(file)}: {v}[...] + ...");
                if (Regex.IsMatch(text, $@"\+\s*{v}\[""")) offenders.Add($"{Path.GetFileName(file)}: ... + {v}[...]");
                if (Regex.IsMatch(text, $@"\$""[^""\n]*\{{{v}\[")) offenders.Add($"{Path.GetFileName(file)}: interpolated {v}[...]");
            }
        }
        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    [Fact]
    public void The_legal_marking_text_is_never_in_the_resources_and_the_banner_components_do_not_translate_it()
    {
        // ADR 0057: marking banners, portion marks and classification text are produced by Markings and rendered exactly as the policy dictates, in every language.
        foreach (var resource in ResourceNames)
            foreach (var tag in new string?[] { null, "es" })
                foreach (var (key, value) in Read(resource, tag))
                {
                    Assert.DoesNotContain("UNCLASSIFIED", value, StringComparison.Ordinal);
                    Assert.DoesNotContain("DISTRIBUTION", value, StringComparison.Ordinal);
                    Assert.DoesNotContain("NOFORN", value, StringComparison.Ordinal);
                    Assert.DoesNotContain("//", value, StringComparison.Ordinal);
                }
        foreach (var name in new[] { "MarkingBanner.razor", "MarkingChip.razor" })
            Assert.DoesNotContain("IStringLocalizer", File.ReadAllText(Path.Combine(Root, "src", "Keel.Web", "Components", "Shared", name)), StringComparison.Ordinal);
        Assert.Equal("UNCLASSIFIED", UiText.Unclassified);

        var language = Services.GetRequiredService<UiLanguage>();
        language.Set("es");
        const string Banner = "CUI//SP-EXPT // DISTRIBUTION C";
        var cut = Render<MarkingBanner>(p => p.Add(x => x.Text, Banner).Add(x => x.Position, "top"));
        Assert.Equal(Banner, cut.Find(".marking-banner").TextContent);
        language.Set("qps-ploc");
        Assert.Equal(Banner, Render<MarkingBanner>(p => p.Add(x => x.Text, Banner).Add(x => x.Position, "top")).Find(".marking-banner").TextContent);
    }

    // ---------------------------------------------------------------- the languages

    [Fact]
    public void An_unknown_retired_or_pseudo_tag_reads_English_unless_the_pseudo_locale_is_switched_on()
    {
        Assert.Equal("es", Locales.Resolve("ES"));
        Assert.Equal("en", Locales.Resolve("fr"));
        Assert.Equal("en", Locales.Resolve(null));
        Assert.Equal("en", Locales.Resolve("<script>"));
        Assert.Equal("en", Locales.Resolve(Locales.PseudoTag));
        Assert.Equal(Locales.PseudoTag, Locales.Resolve(Locales.PseudoTag, pseudoEnabled: true));
        Assert.Equal(["en", "es"], new UiLanguage().Available.Select(l => l.Tag));
        Assert.Equal("Español", Locales.Shipped.Single(l => l.Tag == "es").NativeName);

        var language = new UiLanguage();
        language.Set("fr");
        Assert.Equal("en", language.Tag);
        language.Set("es");
        Assert.Equal("es", language.Tag);
    }

    [Fact]
    public void The_stored_language_is_normalized_and_defaults_to_English()
    {
        Assert.Equal("en", UserPreferences.Default.Language);
        Assert.Equal("es", UserPreferences.Normalize("dark", "compact", true, false, " ES ").Language);
        Assert.Equal("en", UserPreferences.Normalize(null, null, false, false, "'; DROP TABLE").Language);
        Assert.Equal("en", UserPreferences.Normalize(null, null, false).Language);
        Assert.Equal("qps-ploc", UserPreferences.Normalize(null, null, false, false, "qps-ploc").Language);
        Assert.Equal(new UserPreferences("dark", "compact", true), UserPreferences.Normalize("dark", "compact", true)); // old callers still compile and get English
    }

    [Fact]
    public void The_second_language_answers_for_every_converted_key_and_a_missing_key_is_reported_not_hidden()
    {
        var language = Services.GetRequiredService<UiLanguage>();
        var shell = Services.GetRequiredService<IStringLocalizer<ShellStrings>>();
        Assert.Equal("Sign out", shell["SignOut"].Value);
        language.Set("es");
        Assert.Equal("Cerrar sesión", shell["SignOut"].Value);
        foreach (var resource in ResourceNames)
        {
            var english = Read(resource, null);
            var spanish = Read(resource, "es");
            // The localizer and the files on disk agree for every key (and Spanish really differs from English for text, not for symbols and separators).
            var localizer = resource switch
            {
                "ShellStrings" => (IStringLocalizer)shell,
                "CommonStrings" => Services.GetRequiredService<IStringLocalizer<CommonStrings>>(),
                _ => Services.GetRequiredService<IStringLocalizer<PageStrings>>(),
            };
            foreach (var key in english.Keys) Assert.Equal(spanish[key], localizer[key].Value);
            Assert.True(english.Count(kv => kv.Value != spanish[kv.Key]) > english.Count / 2, $"{resource}: Spanish is barely different from English");
        }
        var missing = shell["No.Such.Key"];
        Assert.True(missing.ResourceNotFound);
        Assert.Equal("No.Such.Key", missing.Value);
    }

    [Fact]
    public void The_pseudo_locale_accents_pads_and_brackets_every_text_but_keeps_placeholders_and_formats()
    {
        var t = Pseudo.Transform("Created {0} {1}. Add the next one.");
        Assert.StartsWith("[", t, StringComparison.Ordinal);
        Assert.EndsWith("]", t, StringComparison.Ordinal);
        Assert.Contains("{0}", t, StringComparison.Ordinal);
        Assert.Contains("{1}", t, StringComparison.Ordinal);
        Assert.DoesNotContain("Created", t, StringComparison.Ordinal);
        Assert.True(t.Length > "Created {0} {1}. Add the next one.".Length * 1.25); // text grows, as translations do

        var language = Services.GetRequiredService<UiLanguage>();
        Assert.False(language.PseudoEnabled);
        language.Set(Locales.PseudoTag); // not offered, so it reads English
        Assert.Equal("en", language.Tag);
        var on = new UiLanguage(new PseudoOn());
        on.Set(Locales.PseudoTag);
        var shell = new KeelLocalizer<ShellStrings>(on);
        Assert.StartsWith("[", shell["Menu"].Value, StringComparison.Ordinal);
        Assert.Contains("{0}", shell["Palette.SeeAll", "{0}"].Value, StringComparison.Ordinal);
        foreach (var key in Read("ShellStrings", null).Keys)
        {
            var value = shell[key].Value;
            Assert.StartsWith("[", value, StringComparison.Ordinal);
            Assert.EndsWith("]", value, StringComparison.Ordinal);
        }
        Assert.Equal("Menu", new KeelLocalizer<ShellStrings>(new UiLanguage()).GetAllStrings(false).Single(s => s.Name == "Menu").Value);
        Assert.Equal(".", on.Culture.NumberFormat.NumberDecimalSeparator); // the pseudo-locale moves text, not separators
    }

    // ---------------------------------------------------------------- culture-aware dates, numbers and units

    [Fact]
    public void Dates_numbers_and_units_follow_the_language_while_the_thread_culture_stays_invariant()
    {
        var at = new DateTimeOffset(2026, 10, 30, 14, 5, 0, TimeSpan.FromHours(2)); // 12:05 UTC
        var en = new UiLanguage();
        var es = new UiLanguage();
        es.Set("es");
        Assert.Equal("2026-10-30 12:05", en.Utc(at));
        Assert.Equal("30/10/2026 12:05", es.Utc(at));
        Assert.Equal("2026-10-30", en.Date(at));
        Assert.Equal("30/10/2026", es.Date(at));
        Assert.Equal("30 October 2026", en.LongDate(at));
        Assert.Equal("30 de octubre de 2026", es.LongDate(at));
        Assert.Equal("1,234.5", en.Number(1234.5m));
        Assert.Equal("1.234,5", es.Number(1234.5m));
        Assert.Equal("0.125", en.Number(0.125m));
        Assert.Equal("0,125", es.Number(0.125m));
        Assert.Equal("12", es.Number(12m)); // no trailing zeros, no decimal separator
        var common = new KeelLocalizer<CommonStrings>(es);
        Assert.Equal("12,5 kg", es.Quantity(12.5m, "kg", common));
        Assert.Equal("3 unidad", es.Quantity(3m, "EA", common));
        Assert.Equal("3 each", en.Quantity(3m, "EA", new KeelLocalizer<CommonStrings>(en)));
        Assert.Equal("XYZ", UiLanguage.Unit("XYZ", common)); // a unit with no resource is shown as it is
        Assert.Equal("lunes", es.Culture.DateTimeFormat.GetDayName(DayOfWeek.Monday));
        Assert.Equal(DayOfWeek.Monday, es.Culture.DateTimeFormat.FirstDayOfWeek);
        // Parsing, exports, SQL parameters and hashes keep the invariant culture: choosing Spanish must not change how a number is written elsewhere.
        Assert.Equal(CultureInfo.InvariantCulture, CultureInfo.CurrentCulture);
        Assert.Equal("1234.5", 1234.5m.ToString(CultureInfo.CurrentCulture));
    }

    [Fact]
    public void Pluralization_uses_two_whole_sentences_not_a_suffix()
    {
        var es = new UiLanguage();
        es.Set("es");
        var p = new KeelLocalizer<PageStrings>(es);
        Assert.Equal("1 pieza a la que tiene acceso", p.Count("Parts.Count", 1));
        Assert.Equal("2 piezas a las que tiene acceso", p.Count("Parts.Count", 2));
        Assert.Equal("0 piezas a las que tiene acceso", p.Count("Parts.Count", 0));
        Assert.Equal("1 part you can access", new KeelLocalizer<PageStrings>(new UiLanguage()).Count("Parts.Count", 1));
    }

    [Fact]
    public void Reasons_are_translated_and_a_reason_without_a_translation_is_never_a_raw_code()
    {
        var es = new UiLanguage();
        es.Set("es");
        var common = new KeelLocalizer<CommonStrings>(es);
        Assert.Equal("Sus roles no permiten esta acción.", UiText.Reason("role.missing", common));
        Assert.Equal("Con las marcas nuevas: Sus roles no permiten esta acción.", UiText.Reason("new_markings:role.missing", common));
        Assert.Equal("Usted no tiene autorización para la categoría CUI SP-EXPT.", UiText.Reason("cui.category_missing:SP-EXPT", common));
        Assert.Equal("You are not allowed to do this.", UiText.Reason("some.unknown_code", new KeelLocalizer<CommonStrings>(new UiLanguage())));
        Assert.Equal("Solo la organización propietaria o los miembros del programa pueden modificar este objeto.", WorkflowText.Reason("org.not_owner", common));
        Assert.Equal("Esta tarea está asignada a otra persona.", WorkflowText.Reason("task.not_assignee", common));
        var due = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero);
        Assert.Equal("Vence el 02/11/2026 09:30 UTC", WorkflowText.Due(due, false, common, es));
        Assert.Equal("Vencida desde el 02/11/2026 09:30 UTC", WorkflowText.Due(due, true, common, es));
        Assert.Equal("Sin fecha de vencimiento", WorkflowText.Due(null, false, common, es));
    }

    // ---------------------------------------------------------------- the converted shell, in a second language and pseudo

    [Fact]
    public void The_rail_the_palette_and_the_shared_controls_speak_the_language_of_the_circuit()
    {
        Services.GetRequiredService<UiLanguage>().Set("es");
        var rail = Render<RailNav>(p => p.Add(x => x.Groups, NavModel.For([Roles.Viewer])));
        Assert.Equal("Principal", rail.Find("nav").GetAttribute("aria-label"));
        Assert.Contains("Datos de producto", rail.Markup, StringComparison.Ordinal);
        Assert.Contains("Contraer la navegación", rail.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Product data", rail.Markup, StringComparison.Ordinal);
        // The route, the group identity and the open/closed state are English identities; only the shown text changes.
        Assert.Contains(rail.FindAll(".rail-link a"), a => a.GetAttribute("href")!.TrimStart('/') == "tasks" && a.TextContent.Contains("Mis tareas", StringComparison.Ordinal));

        var crumbs = Render<Breadcrumbs>(p => p.Add(x => x.Items, [new Crumb("Piezas", "parts"), new Crumb("P-000001")]));
        Assert.Equal("Ruta de navegación", crumbs.Find("nav").GetAttribute("aria-label"));
        Assert.Equal("Inicio", crumbs.Find("a").TextContent);

        var pager = Render<Pager>(p => p.Add(x => x.Total, 1234L).Add(x => x.Offset, 50));
        Assert.Contains("Mostrando 51–100 de 1.234", pager.Markup, StringComparison.Ordinal);
        Assert.Contains("Anterior", pager.Markup, StringComparison.Ordinal);

        Assert.Contains("Cargando", Render<Skeleton>().Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sign_panel_is_translated_and_still_requires_the_comment_it_always_did()
    {
        Services.GetRequiredService<UiLanguage>().Set("es");
        ReasonCodeView[] codes = [new("Approved", "Aprobado", "approve", false), new("Rejected", "Rechazado", "reject", true)];
        SignChoice? sent = null;
        var cut = Render<SignPanel>(p => p.Add(x => x.ReasonCodes, codes).Add(x => x.CanSign, true).Add(x => x.StepUpRequired, true).Add(x => x.StepUpMaxAgeSeconds, 300)
            .Add(x => x.OnSign, EventCallback.Factory.Create<SignChoice>(this, c => sent = c)));
        Assert.Equal("Firmar", cut.Find("#sign-heading").TextContent);
        Assert.Contains("hace más de 5 minutos", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("button").Single(b => b.TextContent == "Rechazado").Click();
        Assert.Equal("Escriba un comentario para firmar como «Rechazado».", cut.Find("#sign-validation").TextContent);
        Assert.Null(sent);
        cut.FindAll("button").Single(b => b.TextContent == "Aprobado").Click();
        Assert.Equal("Approved", sent?.Code);
    }

    [Fact]
    public void The_user_menu_offers_the_languages_and_choosing_one_saves_it_and_reloads_the_page()
    {
        var store = (FakePreferenceStore)Services.GetRequiredService<IPreferenceStore>();
        var cut = Render<UserMenu>(p => p.Add(x => x.DisplayName, "Alice Example"));
        cut.Find("#user-menu-button").Click();
        var choices = cut.FindAll("[data-language-choice]");
        Assert.Equal(["en", "es"], choices.Select(c => c.GetAttribute("data-language-choice")));
        Assert.Equal("true", cut.Find("[data-language-choice=en]").GetAttribute("aria-pressed"));
        Assert.Equal("es", cut.Find("[data-language-choice=es]").GetAttribute("lang")); // the name of a language is read in that language
        Assert.Equal("Español", cut.Find("[data-language-choice=es]").TextContent);

        cut.Find("[data-language-choice=es]").Click();

        cut.WaitForAssertion(() => Assert.Equal("es", store.Preferences.Language));
        var nav = Services.GetRequiredService<NavigationManager>() as Bunit.TestDoubles.BunitNavigationManager;
        Assert.NotNull(nav);
        Assert.True(nav!.History.First().Options.ForceLoad); // a new circuit starts in the new language
    }

    [Fact]
    public void The_user_menu_in_Spanish_shows_Spanish_labels_and_the_pseudo_choice_is_hidden_by_default()
    {
        Services.GetRequiredService<UiLanguage>().Set("es");
        var cut = Render<UserMenu>(p => p.Add(x => x.DisplayName, "Alice Example"));
        cut.Find("#user-menu-button").Click();
        Assert.Equal("Menú de usuario", cut.Find("#user-menu-panel").GetAttribute("aria-label"));
        Assert.Equal("Idioma", cut.Find("#language-label").TextContent);
        Assert.Contains("Mi acceso", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Alto contraste", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(2, cut.FindAll("[data-language-choice]").Count);
        Assert.Equal("true", cut.Find("[data-language-choice=es]").GetAttribute("aria-pressed"));
        // The data attributes the tests and the browser scripts use stay the same in every language.
        Assert.NotNull(cut.Find("[data-theme-choice=dark]"));
        Assert.Equal("Oscuro", cut.Find("[data-theme-choice=dark]").TextContent);
    }
}
