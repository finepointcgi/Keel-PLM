using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Keel.SbomTool;

/// <summary>
/// Checks the licence and source part of an unpacked offline bundle (or of the staging directory it is made from), with files only: the SBOM is well formed, every
/// copyleft component in it points at a source archive that is in the bundle with the recorded SHA-256, <c>sources/SOURCES.txt</c> matches the files, the notices
/// file names the components and records the legal review as pending, and the licence texts are present. The bundle's own signed <c>SHA256SUMS</c> covers every
/// file again, so this check is about completeness and consistency, not authenticity.
/// </summary>
public static partial class BundleVerifier
{
    public static IReadOnlyList<string> Verify(string bundle)
    {
        var problems = new List<string>();
        void Fail(string message) => problems.Add(message);

        // ---- sbom
        var sbomPath = Path.Combine(bundle, "sbom", "keel-plm.cdx.json");
        JsonObject? sbom = null;
        if (!File.Exists(sbomPath)) Fail("sbom/keel-plm.cdx.json is missing");
        else
        {
            try { sbom = JsonNode.Parse(File.ReadAllText(sbomPath))!.AsObject(); }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { Fail("sbom/keel-plm.cdx.json is not valid JSON: " + e.Message); }
        }

        var copyleftNames = new List<string>();
        var listedSources = new Dictionary<string, string>(StringComparer.Ordinal); // relative path -> sha256 from the SBOM
        if (sbom is not null)
        {
            if (sbom["bomFormat"]?.GetValue<string>() != "CycloneDX") Fail("sbom: bomFormat is not CycloneDX");
            if (sbom["specVersion"]?.GetValue<string>() is not { } spec || !spec.StartsWith("1.", StringComparison.Ordinal)) Fail("sbom: specVersion is missing or not 1.x");
            if (sbom["serialNumber"]?.GetValue<string>() is not { } serial || !SerialPattern().IsMatch(serial)) Fail("sbom: serialNumber is missing or not a urn:uuid");
            if (sbom["metadata"]?["component"]?["name"] is null) Fail("sbom: metadata.component is missing");
            var components = sbom["components"] as JsonArray;
            if (components is null || components.Count == 0) Fail("sbom: has no components");
            else
            {
                var refs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var node in components.Select(n => n!.AsObject()))
                {
                    var name = node["name"]?.GetValue<string>();
                    var bomRef = node["bom-ref"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(name) || node["type"] is null) { Fail("sbom: a component has no name or type"); continue; }
                    if (string.IsNullOrEmpty(bomRef) || !refs.Add(bomRef)) Fail($"sbom: component {name} has a missing or duplicate bom-ref");
                    foreach (var h in (node["hashes"] as JsonArray)?.Select(h => h!.AsObject()) ?? [])
                        if (!HashLength.TryGetValue(h["alg"]?.GetValue<string>() ?? "", out var len) || h["content"]?.GetValue<string>() is not { } c || c.Length != len || !HexPattern().IsMatch(c))
                            Fail($"sbom: component {name} has a malformed hash");
                    var shipped = node["scope"]?.GetValue<string>() != "excluded";
                    var copyleft = (node["properties"] as JsonArray)?.Any(p => p!["name"]?.GetValue<string>() == "keel:copyleft" && p["value"]?.GetValue<string>() == "true") == true;
                    if (shipped && copyleft)
                    {
                        copyleftNames.Add(name);
                        var archives = (node["externalReferences"] as JsonArray)?.Select(r => r!.AsObject()).Where(r => r["type"]?.GetValue<string>() == "source-distribution").ToList() ?? [];
                        if (archives.Count == 0) Fail($"sbom: copyleft component {name} has no source-distribution reference");
                        foreach (var a in archives)
                        {
                            var url = a["url"]?.GetValue<string>() ?? "";
                            var sha = (a["hashes"] as JsonArray)?.FirstOrDefault()?["content"]?.GetValue<string>();
                            if (!url.StartsWith("sources/", StringComparison.Ordinal) || url.Contains("..", StringComparison.Ordinal)) { Fail($"sbom: the source reference of {name} is not a file under sources/"); continue; }
                            if (sha is null) { Fail($"sbom: the source reference {url} of {name} has no hash"); continue; }
                            listedSources[url] = sha;
                        }
                    }
                }
                foreach (var d in (sbom["dependencies"] as JsonArray)?.Select(d => d!.AsObject()) ?? [])
                {
                    var r = d["ref"]?.GetValue<string>();
                    if (r is null || (!refs.Contains(r) && r != sbom["metadata"]?["component"]?["bom-ref"]?.GetValue<string>())) Fail($"sbom: a dependency names the unknown ref {r}");
                    foreach (var dep in (d["dependsOn"] as JsonArray)?.Select(x => x!.GetValue<string>()) ?? [])
                        if (!refs.Contains(dep)) Fail($"sbom: {r} depends on the unknown ref {dep}");
                }
            }
        }
        if (sbom is not null && copyleftNames.Count == 0) Fail("sbom: no copyleft component is recorded, but the viewer ships occt-import-js (LGPL-2.1)");

        // ---- source archives and their manifest
        var sourcesDir = Path.Combine(bundle, "sources");
        var manifest = Path.Combine(sourcesDir, "SOURCES.txt");
        var manifestHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] manifestLines = [];
        if (!File.Exists(manifest)) Fail("sources/SOURCES.txt is missing");
        else
        {
            manifestLines = File.ReadAllLines(manifest);
            foreach (var line in manifestLines)
            {
                var m = ManifestLine().Match(line);
                if (m.Success) manifestHashes[m.Groups["file"].Value] = m.Groups["sha"].Value;
            }
            if (manifestHashes.Count == 0) Fail("sources/SOURCES.txt lists no archive");
            if (!manifestLines.Any(l => l.StartsWith("os-sources:", StringComparison.Ordinal))) Fail("sources/SOURCES.txt does not state whether operating system package sources are included (os-sources:)");
        }
        foreach (var (file, sha) in manifestHashes)
        {
            var path = Path.Combine(sourcesDir, file);
            if (file.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(file)) { Fail($"sources/SOURCES.txt names a path outside sources/: {file}"); continue; }
            if (!File.Exists(path)) { Fail($"source archive {file} is listed in SOURCES.txt but missing"); continue; }
            if (Sha256(path) != sha) Fail($"source archive {file} does not match its SHA-256 in SOURCES.txt");
        }
        if (Directory.Exists(sourcesDir))
            foreach (var f in Directory.EnumerateFiles(sourcesDir).Select(Path.GetFileName).Where(f => f != "SOURCES.txt"))
                if (!manifestHashes.ContainsKey(f!)) Fail($"sources/{f} is not listed in SOURCES.txt");
        foreach (var (url, sha) in listedSources)
        {
            var file = url["sources/".Length..];
            if (!manifestHashes.TryGetValue(file, out var listed)) Fail($"{url} is referenced by the SBOM but not listed in SOURCES.txt");
            else if (listed != sha) Fail($"{url}: the SBOM and SOURCES.txt record different SHA-256 values");
        }
        // Operating system package sources, when the bundle claims to include them.
        if (manifestLines.Any(l => l.StartsWith("os-sources: INCLUDED", StringComparison.Ordinal)))
        {
            var sums = Path.Combine(sourcesDir, "os", "SHA256SUMS");
            if (!File.Exists(sums)) Fail("os-sources are declared included but sources/os/SHA256SUMS is missing");
            else
            {
                var listed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in File.ReadAllLines(sums).Where(l => l.Length > 0))
                {
                    var m = ManifestLine().Match(line);
                    if (!m.Success) { Fail("sources/os/SHA256SUMS has a malformed line"); continue; }
                    listed.Add(m.Groups["file"].Value);
                    var p = Path.Combine(sourcesDir, "os", m.Groups["file"].Value);
                    if (!File.Exists(p)) Fail($"sources/os/{m.Groups["file"].Value} is listed but missing");
                    else if (Sha256(p) != m.Groups["sha"].Value) Fail($"sources/os/{m.Groups["file"].Value} does not match SHA256SUMS");
                }
                foreach (var f in Directory.EnumerateFiles(Path.Combine(sourcesDir, "os")).Select(Path.GetFileName).Where(f => f != "SHA256SUMS"))
                    if (!listed.Contains(f!)) Fail($"sources/os/{f} is not listed in sources/os/SHA256SUMS");
            }
        }

        // ---- notices and licence texts
        var notices = Path.Combine(bundle, "THIRD-PARTY-NOTICES.md");
        if (!File.Exists(notices) || new FileInfo(notices).Length == 0) Fail("THIRD-PARTY-NOTICES.md is missing or empty");
        else
        {
            var text = File.ReadAllText(notices);
            foreach (var name in copyleftNames)
                if (!text.Contains(name, StringComparison.Ordinal)) Fail($"THIRD-PARTY-NOTICES.md does not mention the copyleft component {name}");
            if (!text.Contains("Written offer", StringComparison.Ordinal)) Fail("THIRD-PARTY-NOTICES.md has no written offer section");
            if (!text.Contains("legal review", StringComparison.OrdinalIgnoreCase) || !text.Contains("pending", StringComparison.OrdinalIgnoreCase)) Fail("THIRD-PARTY-NOTICES.md does not record the legal review as pending");
            foreach (var file in manifestHashes.Keys)
                if (!text.Contains(file, StringComparison.Ordinal)) Fail($"THIRD-PARTY-NOTICES.md does not list the source archive {file}");
            if (!text.Contains("Operating system packages", StringComparison.Ordinal)) Fail("THIRD-PARTY-NOTICES.md has no section on operating system packages");
        }
        foreach (var license in new[] { "occt-import-js-LGPL-2.1.txt", "opencascade-LGPL-2.1.txt", "three-MIT.txt", "pdfjs-Apache-2.0.txt", "DejaVu-LICENSE.txt" })
            if (!File.Exists(Path.Combine(bundle, "licenses", license))) Fail($"licenses/{license} is missing");
        return problems;
    }

    private static readonly Dictionary<string, int> HashLength = new(StringComparer.Ordinal) { ["SHA-256"] = 64, ["SHA-512"] = 128, ["SHA-1"] = 40, ["SHA-384"] = 96 };

    private static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    [GeneratedRegex("^urn:uuid:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex SerialPattern();

    [GeneratedRegex("^[0-9a-f]+$")]
    private static partial Regex HexPattern();

    /// <summary>"&lt;sha256&gt;  &lt;file&gt;" in the style of sha256sum.</summary>
    [GeneratedRegex(@"^(?<sha>[0-9a-f]{64})  (?<file>[A-Za-z0-9._+-]+)$")]
    private static partial Regex ManifestLine();
}
