using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Keel.SbomTool;

namespace Keel.UnitTests;

/// <summary>
/// Licences and corresponding source of the offline bundle (ADR-0033): the SBOM generator reads only files of the repository, lists what is shipped with its
/// licences, ties every copyleft component to a pinned source archive, and the bundle checker refuses an incomplete or altered licence and source part.
/// </summary>
public sealed class OfflineComplianceTests : IDisposable
{
    private static readonly DateTimeOffset Stamp = new(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
    private readonly List<string> _dirs = [];

    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }

    private static SbomResult Real() => SbomGenerator.Generate(new SbomOptions(Root, "1.7.0", Stamp));

    public void Dispose()
    {
        foreach (var d in _dirs) if (Directory.Exists(d)) Directory.Delete(d, true);
    }

    private string Temp()
    {
        var d = Directory.CreateTempSubdirectory("keel-compliance-").FullName;
        _dirs.Add(d);
        return d;
    }

    // ---------------------------------------------------------------- the generator over the real repository

    [Fact]
    public void The_sbom_lists_the_viewer_libraries_with_their_licences_and_ties_the_lgpl_ones_to_source_archives()
    {
        var r = Real();
        var json = JsonNode.Parse(r.SbomJson)!.AsObject();
        Assert.Equal("CycloneDX", json["bomFormat"]!.GetValue<string>());
        Assert.Equal("1.5", json["specVersion"]!.GetValue<string>());
        Assert.Matches("^urn:uuid:[0-9a-f-]{36}$", json["serialNumber"]!.GetValue<string>());
        Assert.Equal("keel-plm", json["metadata"]!["component"]!["name"]!.GetValue<string>());

        SbomComponent Get(string name) => r.Components.Single(c => c.Name == name && c.Shipped);
        Assert.Equal(["LGPL-2.1"], Get("occt-import-js").Licenses);
        Assert.Equal(["MIT"], Get("three").Licenses);
        Assert.Equal(["Apache-2.0"], Get("pdfjs-dist").Licenses);
        Assert.Equal(["LGPL-2.1-only WITH OCCT-exception-1.0"], Get("OpenCascade Technology (OCCT)").Licenses);
        Assert.Equal(64, Get("viewer/vendor/occt-import-js.wasm").Hashes["SHA-256"].Length);

        // Every shipped copyleft component, including the two vendored files, names the archives that correspond to it.
        var copyleft = r.Components.Where(c => c.Shipped && c.IsCopyleft).ToList();
        Assert.Contains(copyleft, c => c.Name == "viewer/vendor/occt-import-js.wasm");
        Assert.Contains(copyleft, c => c.Name == "viewer/vendor/occt-import-js.js");
        Assert.All(copyleft, c => Assert.Contains(c.References, x => x.Type == "source-distribution" && x.Url.StartsWith("sources/", StringComparison.Ordinal) && x.Sha256 is { Length: 64 }));
        Assert.DoesNotContain(r.Warnings, w => w.StartsWith("Copyleft component", StringComparison.Ordinal));
        Assert.Equal(["occt-import-js-0.0.23.tar.gz", "occt-d2abb6d8.tar.gz", "occt-import-js-0.0.23.tgz"], r.Sources.Select(s => s.File));
    }

    [Fact]
    public void The_sbom_covers_the_shipped_dotnet_packages_the_font_the_images_and_leaves_build_tools_out_of_scope()
    {
        var r = Real();
        // Direct package references of the shipped projects.
        foreach (var id in new[] { "Npgsql", "AWSSDK.S3", "PDFsharp", "prometheus-net.AspNetCore", "Microsoft.AspNetCore.Authentication.OpenIdConnect" })
            Assert.Contains(r.Components, c => c.Kind == "nuget" && c.Name == id && c.Shipped);
        // Test-only packages are not part of what is delivered.
        Assert.DoesNotContain(r.Components, c => c.Name is "xunit.v3" or "bunit" or "Microsoft.Playwright" or "Testcontainers");
        Assert.All(r.Components.Where(c => c.Kind == "nuget"), c =>
        {
            Assert.Equal(128, c.Hashes["SHA-512"].Length);
            Assert.NotEmpty(c.Licenses);
        });
        Assert.Contains(r.Components, c => c.Kind == "embedded-file" && c.Name.EndsWith("DejaVuSans.ttf", StringComparison.Ordinal) && c.Licenses.Contains("Bitstream-Vera"));
        Assert.Equal(5, r.Components.Count(c => c.Kind == "container"));
        Assert.All(r.Components.Where(c => c.Kind == "container"), c => Assert.Equal(64, c.Hashes["SHA-256"].Length));
        // npm build tooling (esbuild and the platform binaries) is listed but excluded.
        Assert.All(r.Components.Where(c => c.Kind == "npm" && c.Name.Contains("esbuild", StringComparison.Ordinal)), c => Assert.False(c.Shipped));
        var json = JsonNode.Parse(r.SbomJson)!["components"]!.AsArray();
        Assert.Contains(json, c => c!["name"]!.GetValue<string>() == "esbuild" && c["scope"]!.GetValue<string>() == "excluded");
    }

    [Fact]
    public void The_sbom_is_well_formed_and_deterministic()
    {
        var a = Real();
        var b = Real();
        Assert.Equal(a.SbomJson, b.SbomJson);
        Assert.Equal(a.NoticesMarkdown, b.NoticesMarkdown);
        var json = JsonNode.Parse(a.SbomJson)!.AsObject();
        var refs = json["components"]!.AsArray().Select(c => c!["bom-ref"]!.GetValue<string>()).ToList();
        Assert.Equal(refs.Count, refs.Distinct(StringComparer.Ordinal).Count());
        foreach (var d in json["dependencies"]!.AsArray())
            foreach (var dep in d!["dependsOn"]!.AsArray()) Assert.Contains(dep!.GetValue<string>(), refs);
        // Dependencies of the vendored files point at their npm package.
        Assert.Contains(json["dependencies"]!.AsArray(), d => d!["ref"]!.GetValue<string>() == "file:viewer/vendor/occt-import-js.wasm" && d["dependsOn"]!.AsArray().Any(x => x!.GetValue<string>() == "pkg:npm/occt-import-js@0.0.23"));
        // The timestamp is the caller's, so a release build is reproducible from the same inputs.
        Assert.Equal("2026-10-29T00:00:00Z", json["metadata"]!["timestamp"]!.GetValue<string>());
        // A different version gives a different serial number.
        Assert.NotEqual(json["serialNumber"]!.GetValue<string>(), JsonNode.Parse(SbomGenerator.Generate(new SbomOptions(Root, "1.7.1", Stamp)).SbomJson)!["serialNumber"]!.GetValue<string>());
    }

    [Fact]
    public void The_notices_carry_the_written_offer_the_source_table_the_licence_texts_and_claim_no_legal_sign_off()
    {
        var text = Real().NoticesMarkdown;
        Assert.Contains("occt-import-js", text, StringComparison.Ordinal);
        Assert.Contains("OpenCascade Technology (OCCT)", text, StringComparison.Ordinal);
        Assert.Contains("### Written offer and replacement", text, StringComparison.Ordinal);
        Assert.Contains("sources/occt-d2abb6d8.tar.gz", text, StringComparison.Ordinal);
        Assert.Contains("2715d89a1bc44dfd34dab88f729c445ea93f6b57d6539b0c89614a80ae144a6c", text, StringComparison.Ordinal);
        Assert.Contains("## 2. Operating system packages in the container images", text, StringComparison.Ordinal);
        Assert.Contains("GNU LESSER GENERAL PUBLIC LICENSE", text, StringComparison.Ordinal);
        Assert.Contains("Apache License", text, StringComparison.Ordinal);
        Assert.Contains("legal review of these notices and of the licence conclusions is pending", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("approved by counsel", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`[Company]`", text, StringComparison.Ordinal);
        // The replacement path is stated: the two files are separate and can be replaced without rebuilding Keel.
        Assert.Contains("/app/wwwroot/viewer/vendor/", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_vendored_file_that_differs_from_its_manifest_stops_the_generator()
    {
        var root = Temp();
        CopyRepoInputs(root);
        var vendor = Path.Combine(root, "src", "Keel.Web", "wwwroot", "viewer", "vendor", "three-bundle.js");
        File.AppendAllText(vendor, "// changed");
        var e = Assert.Throws<InvalidDataException>(() => SbomGenerator.Generate(new SbomOptions(root, "1.7.0", Stamp)));
        Assert.Contains("three-bundle.js", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_copyleft_component_without_a_source_archive_is_reported()
    {
        var root = Temp();
        CopyRepoInputs(root);
        var catalogue = Path.Combine(root, "deploy", "offline", "third-party-sources.json");
        var json = JsonNode.Parse(File.ReadAllText(catalogue))!.AsObject();
        json["sources"] = new JsonArray(json["sources"]!.AsArray().Select(s => (JsonNode)s!.DeepClone()).Where(s => s["id"]!.GetValue<string>() == "occt-source").ToArray());
        File.WriteAllText(catalogue, json.ToJsonString());
        var r = SbomGenerator.Generate(new SbomOptions(root, "1.7.0", Stamp));
        Assert.Contains(r.Warnings, w => w.StartsWith("Copyleft component occt-import-js", StringComparison.Ordinal));
    }

    [Fact]
    public void The_source_catalogue_is_pinned_by_sha256_and_names_only_plain_file_names()
    {
        var sources = SbomGenerator.ReadSources(Root);
        Assert.NotEmpty(sources);
        Assert.All(sources, s =>
        {
            Assert.Matches("^[0-9a-f]{64}$", s.Sha256);
            Assert.StartsWith("https://", s.Url, StringComparison.Ordinal);
            Assert.DoesNotContain('/', s.File);
            Assert.True(s.Size > 0);
        });
        // The OpenCascade revision is the one the occt submodule of occt-import-js 0.0.23 pins.
        Assert.Contains(sources, s => s.Id == "occt-source" && s.Version == "d2abb6d844231cb8f29be6894440874a4700e4a5" && s.Url.EndsWith("d2abb6d844231cb8f29be6894440874a4700e4a5.tar.gz", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_copyleft_licensed_vendored_viewer_file_is_covered_by_a_pinned_source_archive()
    {
        // A guard for the next vendored library: a GPL/LGPL file added to the manifest must come with a catalogued source archive.
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "src", "Keel.Web", "wwwroot", "viewer", "manifest.json")))!["files"]!.AsArray();
        var covered = SbomGenerator.ReadSources(Root).SelectMany(s => s.Covers).ToHashSet(StringComparer.Ordinal);
        foreach (var f in manifest.Where(f => f!["vendored"]?.GetValue<bool>() == true && f["license"]!.GetValue<string>().Contains("GPL", StringComparison.Ordinal)))
            Assert.Contains("viewer/" + f!["path"]!.GetValue<string>(), covered);
    }

    // ---------------------------------------------------------------- the bundle checker

    private (string Bundle, string[] Archives) SyntheticBundle()
    {
        var bundle = Temp();
        Directory.CreateDirectory(Path.Combine(bundle, "sbom"));
        Directory.CreateDirectory(Path.Combine(bundle, "sources"));
        Directory.CreateDirectory(Path.Combine(bundle, "licenses"));
        var r = Real();
        var sbom = r.SbomJson;
        var manifest = new StringBuilder("# test\n");
        var names = new List<string>();
        foreach (var s in r.Sources)
        {
            var bytes = Encoding.UTF8.GetBytes("synthetic archive " + s.Id);
            File.WriteAllBytes(Path.Combine(bundle, "sources", s.File), bytes);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            sbom = sbom.Replace(s.Sha256, sha, StringComparison.Ordinal);
            manifest.Append(sha).Append("  ").Append(s.File).Append('\n');
            names.Add(s.File);
        }
        manifest.Append("os-sources: NOT INCLUDED (open item)\n");
        var notices = r.NoticesMarkdown;
        foreach (var s in r.Sources) notices = notices.Replace(s.Sha256, "x", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(bundle, "sbom", "keel-plm.cdx.json"), sbom);
        File.WriteAllText(Path.Combine(bundle, "sources", "SOURCES.txt"), manifest.ToString());
        File.WriteAllText(Path.Combine(bundle, "THIRD-PARTY-NOTICES.md"), notices);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Root, "docs", "third-party", "viewer-licenses"), "*.txt")) File.Copy(f, Path.Combine(bundle, "licenses", Path.GetFileName(f)));
        File.Copy(Path.Combine(Root, "src", "Keel.Documents", "Marking", "Fonts", "DejaVu-LICENSE.txt"), Path.Combine(bundle, "licenses", "DejaVu-LICENSE.txt"));
        return (bundle, [.. names]);
    }

    [Fact]
    public void A_complete_licence_and_source_part_passes_the_checker()
    {
        var (bundle, _) = SyntheticBundle();
        Assert.Empty(BundleVerifier.Verify(bundle));
    }

    [Fact]
    public void The_checker_refuses_each_way_of_breaking_the_source_offer()
    {
        var (bundle, archives) = SyntheticBundle();
        string P(params string[] parts) => Path.Combine([bundle, .. parts]);
        void Expect(string fragment)
        {
            var problems = BundleVerifier.Verify(bundle);
            Assert.Contains(problems, p => p.Contains(fragment, StringComparison.Ordinal));
        }

        // an archive altered, missing or added
        var archive = P("sources", archives[0]);
        var original = File.ReadAllBytes(archive);
        File.AppendAllText(archive, "x");
        Expect("does not match its SHA-256");
        File.Delete(archive);
        Expect("listed in SOURCES.txt but missing");
        File.WriteAllBytes(archive, original);
        File.WriteAllText(P("sources", "extra.tar.gz"), "x");
        Expect("is not listed in SOURCES.txt");
        File.Delete(P("sources", "extra.tar.gz"));
        Assert.Empty(BundleVerifier.Verify(bundle));

        // the manifest edited, or silent about the operating system sources
        var manifest = File.ReadAllText(P("sources", "SOURCES.txt"));
        File.WriteAllText(P("sources", "SOURCES.txt"), manifest.Replace("os-sources: NOT INCLUDED", "other: x", StringComparison.Ordinal));
        Expect("os-sources:");
        File.WriteAllText(P("sources", "SOURCES.txt"), manifest.Replace("os-sources: NOT INCLUDED (open item)", "os-sources: INCLUDED (3 files)", StringComparison.Ordinal));
        Expect("sources/os/SHA256SUMS is missing");
        File.WriteAllText(P("sources", "SOURCES.txt"), manifest);

        // the SBOM without source references, damaged, or disagreeing with the manifest
        var sbom = File.ReadAllText(P("sbom", "keel-plm.cdx.json"));
        File.WriteAllText(P("sbom", "keel-plm.cdx.json"), sbom.Replace("source-distribution", "other", StringComparison.Ordinal));
        Expect("no source-distribution reference");
        File.WriteAllText(P("sbom", "keel-plm.cdx.json"), "{ not json");
        Expect("not valid JSON");
        var firstHash = manifest.Split('\n').First(l => l.Length > 64 && l[64] == ' ')[..64];
        File.WriteAllText(P("sbom", "keel-plm.cdx.json"), sbom.Replace(firstHash, new string('0', 64), StringComparison.Ordinal));
        Assert.NotEmpty(BundleVerifier.Verify(bundle));
        File.WriteAllText(P("sbom", "keel-plm.cdx.json"), sbom);
        Assert.Empty(BundleVerifier.Verify(bundle));

        // the notices
        var notices = File.ReadAllText(P("THIRD-PARTY-NOTICES.md"));
        File.WriteAllText(P("THIRD-PARTY-NOTICES.md"), notices.Replace("Written offer", "Statement", StringComparison.Ordinal));
        Expect("no written offer");
        File.WriteAllText(P("THIRD-PARTY-NOTICES.md"), notices.Replace("pending", "done", StringComparison.Ordinal));
        Expect("legal review as pending");
        File.Delete(P("THIRD-PARTY-NOTICES.md"));
        Expect("THIRD-PARTY-NOTICES.md is missing");
        File.WriteAllText(P("THIRD-PARTY-NOTICES.md"), notices);

        // a licence text
        File.Delete(P("licenses", "opencascade-LGPL-2.1.txt"));
        Expect("licenses/opencascade-LGPL-2.1.txt is missing");
    }

    [Fact]
    public void Operating_system_sources_are_accepted_only_with_a_matching_manifest()
    {
        var (bundle, _) = SyntheticBundle();
        var os = Path.Combine(bundle, "sources", "os");
        Directory.CreateDirectory(os);
        var bytes = Encoding.UTF8.GetBytes("glibc source");
        File.WriteAllBytes(Path.Combine(os, "glibc-2.34.src.rpm"), bytes);
        var manifest = Path.Combine(bundle, "sources", "SOURCES.txt");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("os-sources: NOT INCLUDED (open item)", "os-sources: INCLUDED (1 files)", StringComparison.Ordinal));
        Assert.Contains(BundleVerifier.Verify(bundle), p => p.Contains("SHA256SUMS is missing", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(os, "SHA256SUMS"), Convert.ToHexStringLower(SHA256.HashData(bytes)) + "  glibc-2.34.src.rpm\n");
        Assert.Empty(BundleVerifier.Verify(bundle));
        File.AppendAllText(Path.Combine(os, "glibc-2.34.src.rpm"), "x");
        Assert.Contains(BundleVerifier.Verify(bundle), p => p.Contains("does not match SHA256SUMS", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- scripts and documents

    [Fact]
    public void The_packaging_script_calls_the_compliance_step_records_it_in_build_info_and_the_verifier_checks_it()
    {
        var package = File.ReadAllText(Path.Combine(Root, "scripts", "package-offline.sh"));
        Assert.Contains("scripts/offline-compliance.sh", package, StringComparison.Ordinal);
        Assert.Contains("--no-compliance", package, StringComparison.Ordinal);
        Assert.Contains("echo \"compliance $COMPLIANCE_NOTES\"", package, StringComparison.Ordinal);
        Assert.Contains("legal-review pending", package, StringComparison.Ordinal);
        var compliance = File.ReadAllText(Path.Combine(Root, "scripts", "offline-compliance.sh"));
        Assert.Contains("does not match the pinned SHA-256", compliance, StringComparison.Ordinal);
        Assert.Contains("--strict", compliance, StringComparison.Ordinal);
        Assert.Contains("os-sources: NOT INCLUDED", compliance, StringComparison.Ordinal);
        var verify = File.ReadAllText(Path.Combine(Root, "scripts", "verify-offline.sh"));
        foreach (var case_ in new[] { "a modified source archive is refused", "a missing source archive is refused", "an SBOM without the source references is refused", "missing notices are refused" })
            Assert.Contains(case_, verify, StringComparison.Ordinal);
    }

    [Fact]
    public void The_third_party_documents_record_legal_review_as_pending_and_the_old_open_item_is_closed()
    {
        var legal = File.ReadAllText(Path.Combine(Root, "docs", "third-party", "legal-review.md"));
        Assert.Contains("pending", legal, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no legal sign-off", legal, StringComparison.OrdinalIgnoreCase);
        var viewer = File.ReadAllText(Path.Combine(Root, "docs", "third-party", "viewer.md"));
        Assert.DoesNotContain("**Open item:** the source archives are not stored", viewer, StringComparison.Ordinal);
        Assert.Contains("third-party-sources.json", viewer, StringComparison.Ordinal);
    }

    private static void CopyRepoInputs(string target)
    {
        foreach (var rel in new[] { "deploy/offline/third-party-sources.json", "deploy/images.env", "tools/viewer-vendor/package-lock.json", "Directory.Packages.props" })
        {
            var to = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine(Root, rel), to);
        }
        foreach (var dir in new[] { "src/Keel.Web/wwwroot/viewer", "src/Keel.Documents/Marking/Fonts", "docs/third-party/viewer-licenses" })
            CopyDirectory(Path.Combine(Root, dir), Path.Combine(target, dir));
        foreach (var project in new[] { "Keel.Api", "Keel.AuditCli", "Keel.VaultTool" })
        {
            var from = Path.Combine(Root, "src", project, "obj", "project.assets.json");
            if (!File.Exists(from)) continue;
            Directory.CreateDirectory(Path.Combine(target, "src", project, "obj"));
            File.Copy(from, Path.Combine(target, "src", project, "obj", "project.assets.json"));
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
