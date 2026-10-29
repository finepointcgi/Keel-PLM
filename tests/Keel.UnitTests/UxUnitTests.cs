using System.Globalization;
using System.Text.RegularExpressions;
using Keel.Core;
using Keel.Web.Services;
using Keel.Workspace;

namespace Keel.UnitTests;

/// <summary>
/// WCAG 2.1 AA contrast of the design tokens, computed from the real wwwroot/css/tokens.css (not from a copy): text pairs need 4.5:1, focus ring and input
/// borders (non-text) 3:1. The marking banner colours are fixed in every theme and need 4.5:1 for their white text.
/// </summary>
public sealed partial class ThemeContrastTests
{
    private static string Tokens => File.ReadAllText(Path.Combine(RepoRoot, "src", "Keel.Web", "wwwroot", "css", "tokens.css"));

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }

    /// <summary>The declarations of the block that starts with <paramref name="anchor"/>.</summary>
    private static Dictionary<string, string> Block(string css, string anchor)
    {
        var start = css.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Block '{anchor}' not found in tokens.css");
        var open = css.IndexOf('{', start);
        var close = css.IndexOf('}', open);
        return Declaration().Matches(css[open..close]).ToDictionary(m => m.Groups["n"].Value, m => m.Groups["v"].Value, StringComparer.Ordinal);
    }

    [GeneratedRegex(@"(?<n>--[a-z0-9-]+):\s*(?<v>#[0-9a-fA-F]{6})\s*;")]
    private static partial Regex Declaration();

    private static double Luminance(string hex)
    {
        double Channel(int i)
        {
            var c = int.Parse(hex.AsSpan(1 + 2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
    }

    public static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static readonly (string Fg, string Bg)[] TextPairs =
    [
        ("--c-text", "--c-bg"), ("--c-text", "--c-surface"), ("--c-text", "--c-surface-2"),
        ("--c-muted", "--c-bg"), ("--c-muted", "--c-surface"), ("--c-muted", "--c-surface-2"),
        ("--c-link", "--c-bg"), ("--c-link", "--c-surface"), ("--c-link", "--c-surface-2"),
        ("--c-on-accent", "--c-accent"), ("--c-accent", "--c-surface"), ("--c-accent", "--c-bg"),
        ("--c-success", "--c-surface"), ("--c-success", "--c-bg"), ("--c-success", "--c-success-bg"),
        ("--c-warning", "--c-surface"), ("--c-warning", "--c-bg"), ("--c-warning", "--c-warning-bg"),
        ("--c-danger", "--c-surface"), ("--c-danger", "--c-bg"), ("--c-danger", "--c-danger-bg"),
        ("--c-info", "--c-surface"), ("--c-info", "--c-bg"), ("--c-info", "--c-info-bg"),
    ];

    private static readonly (string Fg, string Bg)[] NonTextPairs = [("--c-focus", "--c-bg"), ("--c-focus", "--c-surface"), ("--c-border-strong", "--c-surface"), ("--c-border-strong", "--c-bg")];

    public static TheoryData<string> Themes => new() { "light", "dark", "dark-system", "high-contrast" };

    private static Dictionary<string, string> ThemeBlock(string theme)
    {
        var css = Tokens;
        return theme switch
        {
            "light" => Block(css, ":root,\n:root[data-theme=\"light\"]"),
            "dark" => Block(css, ":root[data-theme=\"dark\"] {"),
            "dark-system" => Block(css, ":root:not([data-theme=\"light\"])"),
            "high-contrast" => Block(css, ":root[data-theme=\"high-contrast\"] {"),
            _ => throw new ArgumentOutOfRangeException(nameof(theme)),
        };
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Every_text_pair_of_every_theme_has_at_least_4_5_to_1(string theme)
    {
        var t = ThemeBlock(theme);
        var failures = new List<string>();
        foreach (var (fg, bg) in TextPairs)
        {
            Assert.True(t.ContainsKey(fg) && t.ContainsKey(bg), $"{theme}: {fg} or {bg} is not defined");
            var ratio = Contrast(t[fg], t[bg]);
            if (ratio < 4.5) failures.Add($"{fg} {t[fg]} on {bg} {t[bg]} = {ratio:F2}");
        }
        Assert.True(failures.Count == 0, $"{theme}: " + string.Join("; ", failures));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_focus_ring_and_input_borders_have_at_least_3_to_1(string theme)
    {
        var t = ThemeBlock(theme);
        foreach (var (fg, bg) in NonTextPairs) Assert.True(Contrast(t[fg], t[bg]) >= 3.0, $"{theme}: {fg} on {bg} = {Contrast(t[fg], t[bg]):F2}");
    }

    [Fact]
    public void The_dark_theme_is_the_same_whether_chosen_or_followed_from_the_system()
    {
        Assert.Equal(ThemeBlock("dark").OrderBy(p => p.Key), ThemeBlock("dark-system").OrderBy(p => p.Key));
    }

    [Fact]
    public void The_marking_banner_tones_are_fixed_across_themes_and_readable()
    {
        var css = Tokens;
        var fixedTones = Block(css, ":root {\n  --banner-unclassified");
        Assert.True(Contrast(fixedTones["--banner-text"], fixedTones["--banner-unclassified"]) >= 4.5);
        Assert.True(Contrast(fixedTones["--banner-text"], fixedTones["--banner-cui"]) >= 4.5);
        // No theme block redefines them: a banner means the same in light, dark and high contrast.
        foreach (var theme in new[] { "light", "dark", "dark-system", "high-contrast" })
            Assert.DoesNotContain(ThemeBlock(theme).Keys, k => k.StartsWith("--banner", StringComparison.Ordinal));
    }

    [Fact]
    public void Components_do_not_hardcode_colours_outside_the_tokens()
    {
        // components.css, layout.css and base.css take colours from var(--…); the few intentional literals are the session warning (a fixed yellow notice) and rgba overlays.
        foreach (var file in new[] { "base.css", "layout.css", "components.css", "awareness.css" })
        {
            var css = File.ReadAllText(Path.Combine(RepoRoot, "src", "Keel.Web", "wwwroot", "css", file));
            var literals = Regex.Matches(css, @"#[0-9a-fA-F]{3,8}\b").Select(m => m.Value.ToLowerInvariant()).Distinct().Except(["#fde68a", "#b45309", "#1b1f24", "#ffffff", "#000000"]).ToList();
            Assert.True(literals.Count == 0, $"{file} has colour literals outside the tokens: {string.Join(", ", literals)}");
        }
    }
}

/// <summary>Search string parsing, ranking and merging, palette actions and navigation by role, preferences and small helpers.</summary>
public sealed class UxLogicTests
{
    [Theory]
    [InlineData("bracket", "bracket", "part,document,change,baseline,mbom,supplier,ncr,capa,deviation", null)]
    [InlineData("p: bracket", "bracket", "part", null)]
    [InlineData("P:bracket", "bracket", "part", null)]
    [InlineData("d: spec 12", "spec 12", "document", null)]
    [InlineData("c: wall", "wall", "change", null)]
    [InlineData("ecn: wall", "wall", "change", "ECN")]
    [InlineData("ecr: wall", "wall", "change", "ECR")]
    [InlineData("pr: leak", "leak", "change", "PR")]
    [InlineData("b: orca", "orca", "baseline", null)]
    [InlineData("mb: plant", "plant", "mbom", null)]
    [InlineData("ncr: burr", "burr", "ncr", null)]
    [InlineData("capa: torque", "torque", "capa", null)]
    [InlineData("dev: finish", "finish", "deviation", null)]
    [InlineData("zz: nothing", "zz: nothing", "part,document,change,baseline,mbom,supplier,ncr,capa,deviation", null)] // not a prefix: searched as typed
    [InlineData("  P-000123  ", "P-000123", "part,document,change,baseline,mbom,supplier,ncr,capa,deviation", null)]
    public void Typed_prefixes_narrow_the_search(string raw, string text, string kinds, string? changeType)
    {
        var q = ParsedQuery.Parse(raw);
        Assert.Equal(text, q.Text);
        Assert.Equal(kinds.Split(','), q.Kinds);
        Assert.Equal(changeType, q.ChangeType);
    }

    [Fact]
    public void An_exact_number_ranks_before_a_prefix_before_a_text_match()
    {
        Assert.Equal(0, ParsedQuery.RankOf("P-000123", "p-000123"));
        Assert.Equal(1, ParsedQuery.RankOf("P-000123", "P-0001"));
        Assert.Equal(2, ParsedQuery.RankOf("P-000123", "bracket"));
        Assert.Equal(2, ParsedQuery.RankOf("P-000123", ""));
    }

    [Fact]
    public void Merged_results_are_ranked_then_grouped_by_kind_then_keep_the_services_order()
    {
        SearchHit H(string kind, string number, int rank) => new(kind, number, "n", "s", "UNCLASSIFIED", "x", rank);
        var merged = GlobalSearchService.Merge([H("document", "D-1", 2), H("part", "P-9", 2), H("part", "P-1", 2), H("change", "ECN-000001", 0), H("document", "D-2", 1)]);
        Assert.Equal(["ECN-000001", "D-2", "P-9", "P-1", "D-1"], merged.Select(h => h.Number));
    }

    [Theory]
    [InlineData("InWork", "In Work")]
    [InlineData("UnderReview", "Under Review")]
    [InlineData("Released", "Released")]
    [InlineData("SomeNewState", "Some New State")]
    public void State_codes_get_readable_labels(string code, string label) => Assert.Equal(label, StateText.Label(code));

    [Fact]
    public void The_navigation_is_grouped_and_admin_auditor_entries_follow_the_roles()
    {
        var viewer = NavModel.For([Roles.Viewer]);
        Assert.Equal(["Work", "Product data", "Change", "Quality", "Sourcing", "Tools"], viewer.Select(g => g.Title));
        Assert.Equal("Home", viewer[0].Items[0].Label);
        Assert.True(viewer[0].Items[0].ExactMatch);
        Assert.DoesNotContain(viewer.SelectMany(g => g.Items), i => i.Href is "admin" or "audit" or "workflows");

        var all = NavModel.For([Roles.Admin, Roles.Auditor]);
        Assert.Equal(["Work", "Product data", "Change", "Quality", "Sourcing", "Records", "Configuration", "Administration", "Tools"], all.Select(g => g.Title));
        Assert.Contains(all.SelectMany(g => g.Items), i => i.Href == "audit");
        Assert.Equal(all.SelectMany(g => g.Items).Count(), all.SelectMany(g => g.Items).Select(i => i.Href).Distinct().Count()); // no link twice
    }

    [Fact]
    public void Palette_actions_depend_on_what_the_user_may_do()
    {
        var viewer = PaletteActions.For([Roles.Viewer], canLock: false);
        Assert.DoesNotContain(viewer, a => a.Id.StartsWith("new:", StringComparison.Ordinal));
        Assert.DoesNotContain(viewer, a => a.Id == PaletteActions.LockNow);
        Assert.DoesNotContain(viewer, a => a.Id is "go:admin" or "go:audit");
        Assert.Contains(viewer, a => a.Id == PaletteActions.SignOut);

        var author = PaletteActions.For([Roles.Author], canLock: true);
        Assert.Contains(author, a => a.Id == "new:part");
        Assert.Contains(author, a => a.Id == PaletteActions.LockNow);

        Assert.Equal(["New part"], PaletteActions.Filter(author, "new part").Select(a => a.Label));
        Assert.Contains("Toggle theme", PaletteActions.Filter(author, "dark").Select(a => a.Label)); // keywords match too
        Assert.Empty(PaletteActions.Filter(author, "zebra"));
        Assert.Equal(author.Count, PaletteActions.Filter(author, "").Count);
    }

    [Fact]
    public void Unknown_preference_values_fall_back_to_defaults_so_a_damaged_row_cannot_break_the_shell()
    {
        Assert.Equal(new UserPreferences("dark", "compact", true), UserPreferences.Normalize("dark", "compact", true));
        Assert.Equal(UserPreferences.Default, UserPreferences.Normalize("<script>", "huge", false));
        Assert.Equal(UserPreferences.Default, UserPreferences.Normalize(null, null, false));
        Assert.All(ThemeNames.All, t => Assert.Equal(t, UserPreferences.Normalize(t, "comfortable", false).Theme));
    }

    [Fact]
    public void Table_ids_and_object_kinds_are_validated_and_routes_are_built_from_numbers_only()
    {
        Assert.True(WorkspaceService.ValidTableId("parts"));
        Assert.True(WorkspaceService.ValidTableId("admin.audit-log"));
        Assert.False(WorkspaceService.ValidTableId("Parts"));
        Assert.False(WorkspaceService.ValidTableId("a b"));
        Assert.False(WorkspaceService.ValidTableId(""));
        Assert.False(WorkspaceService.ValidTableId(new string('a', 65)));
        Assert.Equal("parts/P-000123", ObjectKinds.Path(ObjectKinds.Part, "P-000123"));
        Assert.Equal("parts/a%2Fb", ObjectKinds.Path(ObjectKinds.Part, "a/b")); // a stored number can never change the route
        Assert.Throws<ArgumentOutOfRangeException>(() => ObjectKinds.Path("secret", "x"));
        Assert.False(ObjectKinds.IsKnown(null));
    }

    [Fact]
    public void A_saved_search_is_only_ever_audited_as_a_hash()
    {
        var hash = WorkspaceService.QueryHash("USML XI(d) gimbal");
        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain("gimbal", hash, StringComparison.Ordinal);
        Assert.Equal(hash, WorkspaceService.QueryHash("USML XI(d) gimbal"));
        Assert.NotEqual(hash, WorkspaceService.QueryHash("USML XI(d) gimbal "));
    }

    [Fact]
    public void Raw_reason_codes_never_reach_a_user()
    {
        Assert.Equal("You are not allowed to do this.", UiText.Reason("some.new_code"));
        Assert.Equal("You are not allowed to do this.", UiText.Reason("area.detail:x"));
        Assert.Equal("Sentence reasons stay as they are.", UiText.Reason("Sentence reasons stay as they are."));
        Assert.Equal("You are not authorized to see the linked document.", UiText.Reason("document.read_denied"));
        Assert.True(UiText.IsReasonCode("role.missing"));
        Assert.False(UiText.IsReasonCode("Your roles do not permit this."));
    }
}
