using System.Text.RegularExpressions;

namespace Keel.UnitTests;

/// <summary>UX-1 rules enforced in CI: the strict CSP is never needed to be relaxed (no inline script or style, no external resources) and pages stay inside the shell's contract.</summary>
public sealed partial class UxArchitectureTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }

    private static string Web => Path.Combine(Root, "src", "Keel.Web");

    private static IEnumerable<string> Razor() =>
        Directory.EnumerateFiles(Web, "*.razor", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [GeneratedRegex(@"<script\b(?![^>]*\bsrc=)", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    [GeneratedRegex(@"<style\b|\sstyle\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex InlineStyle();

    [GeneratedRegex(@"\son(click|change|input|load|error|submit|keydown|keyup|mouse\w+|focus|blur)\s*=\s*""(?!@)")]
    private static partial Regex HtmlEventAttribute();

    [GeneratedRegex(@"javascript:", RegexOptions.IgnoreCase)]
    private static partial Regex JavascriptUrl();

    private static string StripComments(string text) => Regex.Replace(Regex.Replace(text, @"@\*.*?\*@", "", RegexOptions.Singleline), @"<!--.*?-->", "", RegexOptions.Singleline);

    [Fact]
    public void Razor_templates_have_no_inline_script_inline_style_or_html_event_attributes()
    {
        var offenders = new List<string>();
        foreach (var file in Razor())
        {
            var text = StripComments(File.ReadAllText(file));
            if (InlineScript().IsMatch(text)) offenders.Add(Path.GetRelativePath(Root, file) + ": inline <script>");
            if (InlineStyle().IsMatch(text)) offenders.Add(Path.GetRelativePath(Root, file) + ": style attribute or <style> (blocked by the CSP; use a class)");
            if (HtmlEventAttribute().IsMatch(text)) offenders.Add(Path.GetRelativePath(Root, file) + ": HTML event attribute (use @onclick)");
            if (JavascriptUrl().IsMatch(text)) offenders.Add(Path.GetRelativePath(Root, file) + ": javascript: URL");
        }
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    [Fact]
    public void The_shell_documents_and_assets_load_nothing_from_outside_the_application()
    {
        var offenders = new List<string>();
        var assets = new[] { "wwwroot/css/tokens.css", "wwwroot/css/base.css", "wwwroot/css/layout.css", "wwwroot/css/components.css", "wwwroot/bom.css", "wwwroot/css/awareness.css", "wwwroot/js/shell.js", "wwwroot/session-lock.js", "wwwroot/icons.svg" };
        foreach (var asset in assets)
        {
            var text = File.ReadAllText(Path.Combine(Web, asset));
            // The SVG namespace declaration is an identifier, not a request.
            text = text.Replace("http://www.w3.org/2000/svg", "", StringComparison.Ordinal);
            if (Regex.IsMatch(text, @"(https?:)?//[a-z0-9.-]+\.[a-z]{2,}", RegexOptions.IgnoreCase) || text.Contains("@import url(http", StringComparison.OrdinalIgnoreCase)) offenders.Add(asset + ": external URL");
            if (Regex.IsMatch(text, @"\beval\s*\(|new\s+Function\s*\(|\bimport\s*\(\s*['""]https?:", RegexOptions.IgnoreCase)) offenders.Add(asset + ": eval or a remote import");
            if (asset.EndsWith(".css", StringComparison.Ordinal) && Regex.IsMatch(text, @"@font-face", RegexOptions.IgnoreCase)) offenders.Add(asset + ": web font (system font stack only)");
        }
        foreach (var file in Razor().Concat(new[] { Path.Combine(Web, "Components", "App.razor") }))
        {
            var text = StripComments(File.ReadAllText(file));
            foreach (Match m in Regex.Matches(text, @"(src|href|action)\s*=\s*""(https?:)?//[^""]+""", RegexOptions.IgnoreCase))
                offenders.Add(Path.GetRelativePath(Root, file) + ": " + m.Value);
        }
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    [Fact]
    public void The_icons_are_an_inline_sprite_served_by_the_application_with_no_icon_font()
    {
        var sprite = File.ReadAllText(Path.Combine(Web, "wwwroot", "icons.svg"));
        Assert.True(Regex.Count(sprite, "<symbol id=\"i-") >= 30);
        Assert.DoesNotContain("style=", sprite, StringComparison.Ordinal); // an inline style attribute would be refused by the CSP when the sprite is loaded
        // Every icon a component names exists in the sprite.
        var ids = Regex.Matches(sprite, "<symbol id=\"i-([a-z-]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        var used = Razor().SelectMany(f => Regex.Matches(File.ReadAllText(f), "(?:<Icon Name|IconName)=\"([a-z-]+)\"").Select(m => m.Groups[1].Value))
            .Concat(Regex.Matches(File.ReadAllText(Path.Combine(Web, "Services", "NavModel.cs")), "\"([a-z]+)\"\\)").Select(m => m.Groups[1].Value).Where(ids.Contains)).ToHashSet();
        var missing = used.Where(u => !ids.Contains(u)).ToList();
        Assert.True(missing.Count == 0, "Icons used but not in the sprite: " + string.Join(", ", missing));
    }

    [Fact]
    public void Pages_do_not_declare_their_own_h1_because_the_page_frame_renders_the_title()
    {
        var offenders = Razor().Where(f => StripComments(File.ReadAllText(f)).Contains("@page \"", StringComparison.Ordinal) && Regex.IsMatch(StripComments(File.ReadAllText(f)), "<h1\\b"))
            .Select(f => Path.GetRelativePath(Root, f)).ToList();
        Assert.True(offenders.Count == 0, "Pages with their own h1: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_page_keeps_a_marking_banner_through_the_page_frame_and_the_new_pages_carry_the_default_policy()
    {
        var pagesDir = Path.Combine(Web, "Components", "Pages");
        var imports = File.ReadAllText(Path.Combine(pagesDir, "_Imports.razor"));
        Assert.Contains("@attribute [Authorize(Policy = Policies.AnyPlmRole)]", imports, StringComparison.Ordinal);
        foreach (var name in new[] { "Home.razor", "SearchPage.razor" })
        {
            var text = File.ReadAllText(Path.Combine(pagesDir, name));
            Assert.Contains("<PageFrame ", text, StringComparison.Ordinal); // the banner is required by the frame's signature
            Assert.DoesNotContain("AllowAnonymous", text, StringComparison.Ordinal);
            Assert.DoesNotContain("@attribute", text, StringComparison.Ordinal); // they inherit the folder default (AnyPlmRole) and cannot widen it
        }
        // PageFrame still requires both a banner and a title.
        var frame = File.ReadAllText(Path.Combine(Web, "Components", "Shared", "PageFrame.razor"));
        Assert.Contains("[Parameter, EditorRequired] public required string Banner", frame, StringComparison.Ordinal);
        Assert.Contains("[Parameter, EditorRequired] public required string Title", frame, StringComparison.Ordinal);
        Assert.Contains("<MarkingBanner Text=\"@Banner\" Position=\"top\"", frame, StringComparison.Ordinal);
        Assert.Contains("<MarkingBanner Text=\"@Banner\" Position=\"bottom\"", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void The_layout_keeps_the_banner_outlets_the_lock_screen_and_the_sign_out_form()
    {
        var layout = File.ReadAllText(Path.Combine(Web, "Components", "Layout", "MainLayout.razor"));
        Assert.Contains("SectionName=\"@PageFrame.TopBanner\"", layout, StringComparison.Ordinal);
        Assert.Contains("SectionName=\"@PageFrame.BottomBanner\"", layout, StringComparison.Ordinal);
        Assert.Contains("<LockedPanel", layout, StringComparison.Ordinal);
        Assert.Contains("id=\"signout-form\"", layout, StringComparison.Ordinal);
        // The palette and the rail exist only in the unlocked branch.
        var paletteAt = layout.IndexOf("<CommandPalette", StringComparison.Ordinal);
        Assert.True(layout.LastIndexOf("@if (!locked)", paletteAt, StringComparison.Ordinal) > 0);
        Assert.Contains("<main id=\"main\"", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"skip-link\"", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void Personal_workspace_queries_always_filter_on_the_signed_in_user()
    {
        // Every statement of the workspace service over its tables names user_sub in its WHERE (or inserts it): no method can reach another user's rows.
        var text = File.ReadAllText(Path.Combine(Root, "src", "Keel.Workspace", "WorkspaceService.cs"));
        foreach (Match m in Regex.Matches(text, @"""[^""]*\b(?:FROM|UPDATE|DELETE FROM)\s+ws\.\w+[^""]*"""))
            Assert.Contains("user_sub", m.Value, StringComparison.Ordinal);
        foreach (Match m in Regex.Matches(text, @"""\s*[^""]*\bDELETE FROM ws\.\w+[^""]*"""))
            Assert.Contains("user_sub = @u", m.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT * FROM ws.", text, StringComparison.Ordinal);
    }
}
