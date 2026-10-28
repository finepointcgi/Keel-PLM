using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Keel.Viewing;

namespace Keel.UnitTests;

/// <summary>
/// The viewer's Content-Security-Policies (ADR-0027) and its vendored assets: what differs from the strict global policy is exactly what is documented, only the
/// CAD worker may use 'unsafe-eval', and a vendored file cannot change without the manifest and the third-party notices being updated.
/// </summary>
public sealed partial class ViewerCspAndAssetsTests
{
    // The literal policy UseSecurityHeaders writes today (Keel.Api HttpPipeline.cs) with an IdP origin; the integration tests assert the live header equals it.
    private const string Global = "default-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self' http://idp.test";

    private static Dictionary<string, string> Directives(string policy) =>
        policy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToDictionary(d => d.Split(' ', 2)[0], d => d.Contains(' ', StringComparison.Ordinal) ? d.Split(' ', 2)[1] : "", StringComparer.Ordinal);

    [Fact]
    public void Viewer_page_policy_differs_from_the_global_policy_only_by_the_documented_directives()
    {
        var global = Directives(Global);
        var page = Directives(ViewerCsp.Page);
        var changed = page.Keys.Union(global.Keys).Where(k => !page.TryGetValue(k, out var v) || !global.TryGetValue(k, out var g) || v != g).OrderBy(k => k).ToList();
        Assert.Equal(ViewerCsp.PageDifferences.OrderBy(k => k).ToList(), changed);
        // Unchanged: the page may load nothing but its own origin, may not be framed, and base-uri is still locked.
        Assert.Equal("'self'", page["default-src"]);
        Assert.Equal("'none'", page["frame-ancestors"]);
        Assert.Equal("'self'", page["base-uri"]);
        Assert.False(page.ContainsKey("script-src"), "script-src must not be widened for the page: it falls back to default-src 'self'.");
        Assert.Equal("'self'", page["worker-src"]);
        Assert.DoesNotContain("blob:", page["worker-src"], StringComparison.Ordinal);
        Assert.Equal("'none'", page["object-src"]);
        Assert.Equal("'none'", page["frame-src"]);
    }

    [Fact]
    public void No_viewer_policy_allows_inline_script_or_style_or_any_network_origin()
    {
        foreach (var policy in new[] { ViewerCsp.Page, ViewerCsp.Content, ViewerCsp.StepWorker, ViewerCsp.PdfWorker })
        {
            Assert.DoesNotContain("'unsafe-inline'", policy, StringComparison.Ordinal);
            Assert.DoesNotContain("http:", policy, StringComparison.Ordinal);
            Assert.DoesNotContain("https:", policy, StringComparison.Ordinal);
            Assert.DoesNotContain("*", policy, StringComparison.Ordinal);
            Assert.Contains("base-uri", policy, StringComparison.Ordinal);
        }
        Assert.Contains("frame-ancestors 'none'", ViewerCsp.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_cad_import_worker_may_use_unsafe_eval_and_only_workers_get_wasm()
    {
        Assert.Contains("'unsafe-eval'", ViewerCsp.StepWorker, StringComparison.Ordinal);
        Assert.Contains("'wasm-unsafe-eval'", ViewerCsp.StepWorker, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-eval'", ViewerCsp.PdfWorker, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", ViewerCsp.Page, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", ViewerCsp.Content, StringComparison.Ordinal);
        Assert.Contains("sandbox", ViewerCsp.Content, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", ViewerCsp.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/viewer/documents/D-000001/files/x", ViewerCsp.Page)]
    [InlineData("/viewer/meta/documents/D-000001/files/x", ViewerCsp.Page)]
    [InlineData("/_content/Keel.Web/viewer/viewer.js", ViewerCsp.Page)]
    [InlineData("/_content/Keel.Web/viewer/vendor/occt-import-js.wasm", ViewerCsp.Page)]
    [InlineData("/viewer/content/documents/D-000001/files/x", ViewerCsp.Content)]
    [InlineData("/VIEWER/CONTENT/documents/D-000001/files/x", ViewerCsp.Content)]
    [InlineData(ViewerCsp.StepWorkerPath, ViewerCsp.StepWorker)]
    [InlineData(ViewerCsp.PdfWorkerPath, ViewerCsp.PdfWorker)]
    [InlineData("/documents", null)]
    [InlineData("/downloads/documents/D-000001/files/x", null)]
    [InlineData("/api/v1/documents", null)]
    [InlineData("/_content/Keel.Web/app.css", null)]
    [InlineData("/_framework/blazor.web.js", null)]
    [InlineData("/viewerx/anything", null)]
    [InlineData("/", null)]
    public void Policy_is_chosen_by_path_and_everything_else_keeps_the_global_policy(string path, string? expected) =>
        Assert.Equal(expected, ViewerCsp.ForPath(path));

    // ---------------------------------------------------------------- assets

    private static string ViewerAssetsDirectory => Path.Combine(RepoRoot(), "src", "Keel.Web", "wwwroot", "viewer");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Manifest_matches_every_served_viewer_file()
    {
        var root = ViewerAssetsDirectory;
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var listed = manifest.RootElement.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!, f => f, StringComparer.Ordinal);
        var onDisk = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Where(p => p != "manifest.json").OrderBy(p => p, StringComparer.Ordinal).ToList();

        Assert.Equal(listed.Keys.OrderBy(p => p, StringComparer.Ordinal).ToList(), onDisk);
        foreach (var path in onDisk)
        {
            var entry = listed[path];
            Assert.True(Sha256(Path.Combine(root, path)) == entry.GetProperty("sha256").GetString(),
                $"{path} changed without the manifest being updated: run `node tools/viewer-vendor/manifest.mjs`, then update docs/third-party/viewer.md for vendored files.");
            Assert.Equal(new FileInfo(Path.Combine(root, path)).Length, entry.GetProperty("size").GetInt64());
        }
    }

    [Fact]
    public void Every_vendored_file_is_listed_in_the_third_party_notices_with_its_hash_and_license()
    {
        var root = ViewerAssetsDirectory;
        var notices = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "third-party", "viewer.md"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var vendored = manifest.RootElement.GetProperty("files").EnumerateArray().Where(f => f.GetProperty("vendored").GetBoolean()).ToList();
        Assert.True(vendored.Count >= 5, "The five vendored files must be in the manifest.");
        foreach (var f in vendored)
        {
            var path = f.GetProperty("path").GetString()!;
            Assert.True(notices.Contains(f.GetProperty("sha256").GetString()!, StringComparison.Ordinal),
                $"docs/third-party/viewer.md does not list the current SHA-256 of {path}; update the notices when a vendored file changes.");
            Assert.Contains(f.GetProperty("license").GetString()!, notices, StringComparison.Ordinal);
        }
        // The license texts ship with the repository.
        foreach (var name in new[] { "three-MIT.txt", "pdfjs-Apache-2.0.txt", "occt-import-js-LGPL-2.1.txt", "opencascade-LGPL-2.1.txt" })
            Assert.True(File.Exists(Path.Combine(RepoRoot(), "docs", "third-party", "viewer-licenses", name)), name);
    }

    [Fact]
    public void The_viewers_own_files_use_no_external_urls_inline_styles_or_dynamic_code()
    {
        var root = ViewerAssetsDirectory;
        var own = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "vendor" + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !f.EndsWith("manifest.json", StringComparison.Ordinal)).ToList();
        Assert.True(own.Count >= 8);
        foreach (var file in own)
        {
            var text = File.ReadAllText(file);
            Assert.False(ExternalUrl().IsMatch(text), $"{Path.GetFileName(file)} refers to an external URL (no CDNs, no outbound internet).");
            Assert.False(DynamicCode().IsMatch(text), $"{Path.GetFileName(file)} uses eval or the Function constructor.");
            Assert.DoesNotContain("innerHTML", text, StringComparison.Ordinal); // DOM is built with createElement/textContent only
            Assert.DoesNotContain("setAttribute('style'", text, StringComparison.Ordinal);
            Assert.DoesNotContain("cssText", text, StringComparison.Ordinal);
        }
    }

    [GeneratedRegex(@"https?://(?!www\.w3\.org/2000/svg)", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalUrl();

    [GeneratedRegex(@"\beval\s*\(|new\s+Function\s*\(|\bFunction\s*\(")]
    private static partial Regex DynamicCode();
}
