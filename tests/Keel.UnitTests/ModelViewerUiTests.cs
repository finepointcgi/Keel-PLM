using System.Text.RegularExpressions;

namespace Keel.UnitTests;

/// <summary>
/// What the Viewer tab (ADR-0052) must never do, checked on its sources: it runs under the strict global policy, so no inline script or style, no eval, no workers or
/// WebAssembly, no network origin, no colour literal outside the theme tokens; and it must keep its accessibility hooks.
/// </summary>
public sealed partial class ModelViewerUiTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    private static readonly string[] Pages = ["ModelViewerPanel.razor", "PartModelViewerPanel.razor"];

    [Fact]
    public void The_tab_has_no_inline_script_or_style_in_its_markup()
    {
        foreach (var name in Pages)
        {
            var text = Read("src", "Keel.Web", "Components", "Pages", "Cad", name);
            Assert.DoesNotMatch(@"<script", text);
            Assert.DoesNotMatch(@"<style|\sstyle\s*=", text);
            Assert.DoesNotMatch(@"\son[a-z]+\s*=\s*""[^@]", text); // no on* attribute handlers other than Blazor's @onclick
            Assert.DoesNotContain("MarkupString", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_script_uses_no_eval_no_worker_no_wasm_no_network_and_no_html_injection()
    {
        var js = Read("src", "Keel.Web", "Components", "Pages", "Cad", "ModelViewerPanel.razor.js");
        foreach (var banned in new[] { "eval(", "new Function", "Function(", "importScripts", "new Worker", "SharedWorker", "WebAssembly", "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "XMLHttpRequest", "WebSocket", "setAttribute('style'", "cssText" })
            Assert.DoesNotContain(banned, js, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"https?://", js);
        // every import and fetch is same-origin: the vendored bundle of the file viewer and the access-checked mesh route
        var imports = ImportPattern().Matches(js).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["/_content/Keel.Web/viewer/vendor/three-bundle.js"], imports);
        Assert.Equal(1, Regex.Count(js, @"\bfetch\("));
        Assert.Contains("credentials: 'same-origin'", js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stylesheet_uses_tokens_only_and_follows_forced_colours()
    {
        var css = Read("src", "Keel.Web", "wwwroot", "css", "model-viewer.css");
        var withoutComments = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b", withoutComments);
        Assert.DoesNotMatch(@"\b(rgb|rgba|hsl|hsla)\(", withoutComments);
        Assert.DoesNotContain("transition", withoutComments, StringComparison.Ordinal);
        Assert.DoesNotContain("animation", withoutComments, StringComparison.Ordinal);
        Assert.Contains("@media (forced-colors: active)", withoutComments, StringComparison.Ordinal);
        Assert.Contains("var(--focus-ring)", withoutComments, StringComparison.Ordinal);
        Assert.Contains("var(--target)", withoutComments, StringComparison.Ordinal);
        Assert.Contains("model-viewer.css", Read("src", "Keel.Web", "Components", "App.razor"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_picture_is_operable_by_keyboard_and_has_a_text_alternative()
    {
        var razor = Read("src", "Keel.Web", "Components", "Pages", "Cad", "ModelViewerPanel.razor");
        Assert.Contains("tabindex=\"0\"", razor, StringComparison.Ordinal);
        Assert.Contains("role=\"application\"", razor, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"@HintId\"", razor, StringComparison.Ordinal);
        Assert.Contains("aria-hidden=\"true\"", razor, StringComparison.Ordinal); // the canvas itself; the list is the alternative
        Assert.Contains("aria-pressed", razor, StringComparison.Ordinal);
        foreach (var label in new[] { "Orbit left", "Orbit right", "Orbit up", "Orbit down", "Zoom in", "Zoom out", "Reset view" })
            Assert.Contains(label, razor, StringComparison.Ordinal);
        var js = Read("src", "Keel.Web", "Components", "Pages", "Cad", "ModelViewerPanel.razor.js");
        foreach (var key in new[] { "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "'Home'", "'+'", "'-'" })
            Assert.Contains(key, js, StringComparison.Ordinal);
        Assert.Contains("(forced-colors: active)", js, StringComparison.Ordinal);
        Assert.Contains("data-theme", js, StringComparison.Ordinal);
        Assert.Contains("dispose", js, StringComparison.Ordinal); // GPU memory is released
    }

    [Fact]
    public void The_tab_is_offered_to_model_documents_and_to_parts_with_a_model_and_each_banner_joins_the_page()
    {
        var doc = Read("src", "Keel.Web", "Components", "Pages", "Documents", "DocumentPage.razor");
        Assert.Contains("new(\"viewer\", \"Viewer\"", doc, StringComparison.Ordinal);
        Assert.Contains("t.Id is not (\"versions\" or \"viewer\")", doc, StringComparison.Ordinal);
        Assert.Contains("tab == \"viewer\" ? viewerBanner", doc, StringComparison.Ordinal);
        var part = Read("src", "Keel.Web", "Components", "Pages", "Parts", "PartPage.razor");
        Assert.Contains("new(\"viewer\", \"Viewer\"", part, StringComparison.Ordinal);
        Assert.Contains("hasModel ? Tabs", part, StringComparison.Ordinal);
        Assert.Contains("tab == \"viewer\" ? viewerBanner", part, StringComparison.Ordinal);
    }

    [Fact]
    public void The_viewer_tab_adds_no_vendored_asset_and_leaves_the_viewer_policy_alone()
    {
        // the renderer is the file viewer's own bundle: its manifest is unchanged and no new file sits beside it
        var manifest = Read("src", "Keel.Web", "wwwroot", "viewer", "manifest.json");
        Assert.Contains("three-bundle.js", manifest, StringComparison.Ordinal);
        var vendor = Directory.GetFiles(Path.Combine(Root(), "src", "Keel.Web", "wwwroot", "viewer", "vendor")).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(["occt-import-js.js", "occt-import-js.wasm", "pdf.min.mjs", "pdf.worker.min.mjs", "three-bundle.js"], vendor);
        Assert.DoesNotContain("model-viewer", Read("src", "Keel.Viewer", "ViewerCsp.cs"), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^import\s+.*?from\s+'([^']+)'", RegexOptions.Multiline)]
    private static partial Regex ImportPattern();
}
