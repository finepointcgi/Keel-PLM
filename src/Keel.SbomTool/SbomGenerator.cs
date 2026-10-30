using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Keel.SbomTool;

/// <summary>What to inventory. <see cref="AssetsFiles"/> are NuGet <c>project.assets.json</c> files of the shipped projects (empty: the conventional ones under <see cref="Root"/>).</summary>
public sealed record SbomOptions(string Root, string Version, DateTimeOffset Timestamp, IReadOnlyList<string>? AssetsFiles = null, bool IncludeDevelopment = false);

/// <summary>One inventoried component, as the SBOM and the notices file both need it.</summary>
public sealed class SbomComponent
{
    public required string Ref { get; init; }
    public required string Type { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    /// <summary>nuget, npm, vendored-file, embedded-file, bundled, container.</summary>
    public required string Kind { get; init; }
    /// <summary>False for build-time tooling that is not part of what is delivered (scope "excluded").</summary>
    public bool Shipped { get; init; } = true;
    public string? Purl { get; init; }
    public string? Supplier { get; set; }
    public string? Url { get; set; }
    public string? Copyright { get; set; }
    public List<string> Licenses { get; } = [];
    public Dictionary<string, string> Hashes { get; } = [];
    public List<(string Label, string Text)> LicenseTexts { get; } = [];
    public SortedDictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);
    public List<(string Type, string Url, string? Sha256, string? Comment)> References { get; } = [];
    public List<string> DependsOn { get; } = [];

    /// <summary>True when any licence is GPL, LGPL or AGPL (with or without an exception).</summary>
    public bool IsCopyleft => Licenses.Any(l => SbomGenerator.CopyleftPattern().IsMatch(l));
}

/// <summary>A pinned source archive (<c>deploy/offline/third-party-sources.json</c>).</summary>
public sealed record SourceArchive(string Id, string Component, string Version, string License, string File, string Url, string Sha256, long Size, IReadOnlyList<string> Covers, string Purpose);

public sealed record SbomResult(string SbomJson, string NoticesMarkdown, IReadOnlyList<SbomComponent> Components, IReadOnlyList<SourceArchive> Sources, IReadOnlyList<string> Warnings);

/// <summary>
/// Builds the CycloneDX 1.5 SBOM and the third-party notices of one Keel release from files in the repository only (no network, no package manager):
/// the NuGet restore results of the shipped projects (<c>project.assets.json</c> plus the packages' own nuspec and licence files), the npm lock file of the viewer
/// vendoring tool, the vendored viewer files recomputed from disk and checked against their manifest, the embedded fonts, the pinned container images and the
/// catalogue of copyleft source archives. Output is deterministic for the same inputs and timestamp.
/// </summary>
public sealed partial class SbomGenerator
{
    public const string ApplicationName = "keel-plm";
    private static readonly string[] ShippedProjects = ["Keel.Api", "Keel.AuditCli", "Keel.VaultTool"];

    [GeneratedRegex(@"(^|[^A-Za-z])(A|L)?GPL(-|\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex CopyleftPattern();

    [GeneratedRegex(@"^(licen[cs]e|notice|third[-_ ]?party[-_ ]?notices?)(\.[a-z0-9]+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LicenseFileName();

    private readonly List<string> _warnings = [];
    private readonly List<SbomComponent> _components = [];

    public static SbomResult Generate(SbomOptions options) => new SbomGenerator().Run(options);

    /// <summary>The pinned source archives of the catalogue (for the packaging script, which fetches and verifies them).</summary>
    public static IReadOnlyList<SourceArchive> ReadSources(string root) => new SbomGenerator().LoadSources(Path.GetFullPath(root));

    private SbomResult Run(SbomOptions o)
    {
        var root = Path.GetFullPath(o.Root);
        var sources = LoadSources(root);
        AddNuGet(root, o.AssetsFiles, o.IncludeDevelopment);
        AddNpm(root);
        AddVendoredViewerFiles(root);
        AddFonts(root);
        AddBundled(root);
        AddImages(root);
        AddPythonAddon(root);
        if (o.IncludeDevelopment) AddDevelopment(root);
        AttachSources(sources);

        var ordered = _components.OrderBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Version, StringComparer.Ordinal).ToList();
        if (ordered.Select(c => c.Ref).Distinct(StringComparer.Ordinal).Count() != ordered.Count)
            throw new InvalidDataException("Two components have the same bom-ref: " + string.Join(", ", ordered.GroupBy(c => c.Ref).Where(g => g.Count() > 1).Select(g => g.Key)));
        var json = SbomWriter.Write(o, ordered, sources, _warnings);
        var notices = NoticesWriter.Write(o, ordered, sources, root, _warnings);
        return new SbomResult(json, notices, ordered, sources, _warnings);
    }

    // ---------------------------------------------------------------- catalogue of source archives and bundled components

    private JsonObject? _catalogue;

    private List<SourceArchive> LoadSources(string root)
    {
        var path = Path.Combine(root, "deploy", "offline", "third-party-sources.json");
        if (!File.Exists(path)) throw new FileNotFoundException("The source catalogue is missing.", path);
        _catalogue = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var list = new List<SourceArchive>();
        foreach (var s in _catalogue["sources"]!.AsArray().Select(n => n!.AsObject()))
            list.Add(new SourceArchive(Str(s, "id"), Str(s, "component"), Str(s, "version"), Str(s, "license"), Str(s, "file"), Str(s, "url"), Str(s, "sha256"),
                s["size"]!.GetValue<long>(), [.. s["covers"]!.AsArray().Select(c => c!.GetValue<string>())], Str(s, "purpose")));
        foreach (var s in list)
            if (!Regex.IsMatch(s.Sha256, "^[0-9a-f]{64}$") || s.File.Contains('/', StringComparison.Ordinal) || s.File.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException($"Source catalogue entry {s.Id} is malformed.");
        return list;
    }

    private static string Str(JsonObject o, string key) => o[key]?.GetValue<string>() ?? throw new InvalidDataException($"'{key}' is missing in the source catalogue.");

    private void AddBundled(string root)
    {
        foreach (var b in _catalogue!["bundled"]!.AsArray().Select(n => n!.AsObject()))
        {
            var name = Str(b, "name");
            var version = Str(b, "version");
            var c = new SbomComponent
            {
                Ref = $"bundled:{Slug(name)}@{version}", Type = "library", Name = name, Version = version, Kind = "bundled",
                Supplier = Str(b, "supplier"), Url = Str(b, "url"),
            };
            c.Licenses.Add(Str(b, "license"));
            c.Properties["keel:compiled-into"] = string.Join(", ", b["intoFiles"]!.AsArray().Select(f => f!.GetValue<string>()));
            c.Properties["keel:note"] = Str(b, "note");
            if (b["sourceId"]?.GetValue<string>() is { } sourceId) c.Properties["keel:source-id"] = sourceId;
            _components.Add(c);
        }
        _ = root;
    }

    // ---------------------------------------------------------------- NuGet

    private void AddNuGet(string root, IReadOnlyList<string>? assetsFiles, bool includeDevelopment)
    {
        var files = assetsFiles is { Count: > 0 }
            ? [.. assetsFiles]
            : ShippedProjects.Select(p => Path.Combine(root, "src", p, "obj", "project.assets.json")).Where(File.Exists).ToList();
        var shippedCount = files.Count;
        if (includeDevelopment && files.Count > 0)
        {
            // Every other project of the solution (tests, tools, importers): what building and testing Keel needs, never delivered.
            foreach (var area in new[] { "src", "tests" })
            {
                var dir = Path.Combine(root, area);
                if (!Directory.Exists(dir)) continue;
                foreach (var project in Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal))
                {
                    var f = Path.Combine(project, "obj", "project.assets.json");
                    if (File.Exists(f) && !files.Contains(f, StringComparer.OrdinalIgnoreCase)) files.Add(f);
                }
            }
        }
        if (files.Count == 0)
        {
            _warnings.Add("No project.assets.json found (run dotnet restore first): the NuGet inventory lists only direct package references and is marked incomplete.");
            AddNuGetFromProjects(root);
            return;
        }
        var seen = new Dictionary<string, SbomComponent>(StringComparer.OrdinalIgnoreCase);
        for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            var file = files[fileIndex];
            var shipped = fileIndex < shippedCount;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var rootEl = doc.RootElement;
            var folders = rootEl.TryGetProperty("packageFolders", out var pf) ? pf.EnumerateObject().Select(p => p.Name).ToList() : [];
            if (!rootEl.TryGetProperty("libraries", out var libs)) continue;
            foreach (var lib in libs.EnumerateObject())
            {
                if (lib.Value.GetProperty("type").GetString() != "package") continue;
                var slash = lib.Name.IndexOf('/', StringComparison.Ordinal);
                var id = lib.Name[..slash];
                var version = lib.Name[(slash + 1)..];
                if (seen.ContainsKey(lib.Name)) continue;
                var relPath = lib.Value.TryGetProperty("path", out var p) ? p.GetString() : $"{id.ToLowerInvariant()}/{version.ToLowerInvariant()}";
                var c = new SbomComponent
                {
                    Ref = $"pkg:nuget/{id}@{version}", Type = "library", Name = id, Version = version, Kind = "nuget", Shipped = shipped,
                    Purl = $"pkg:nuget/{id}@{version}", Url = $"https://www.nuget.org/packages/{id}/{version}",
                };
                if (!shipped) c.Properties["keel:reason"] = "referenced only by test, tooling or importer projects; not delivered";
                if (lib.Value.TryGetProperty("sha512", out var sha) && sha.GetString() is { Length: > 0 } b64)
                    c.Hashes["SHA-512"] = Convert.ToHexStringLower(Convert.FromBase64String(b64));
                foreach (var folder in folders)
                {
                    var dir = Path.Combine(folder, relPath!);
                    if (Directory.Exists(dir)) { ReadNuspec(c, dir, id); break; }
                }
                if (c.Licenses.Count == 0) _warnings.Add($"NuGet package {id} {version}: no licence declared in its nuspec (or the package folder was not available).");
                seen[lib.Name] = c;
            }
        }
        _components.AddRange(seen.Values);
    }

    private void AddNuGetFromProjects(string root)
    {
        var props = Path.Combine(root, "Directory.Packages.props");
        var versions = XDocument.Load(props).Descendants().Where(e => e.Name.LocalName == "PackageVersion")
            .ToDictionary(e => (string)e.Attribute("Include")!, e => (string)e.Attribute("Version")!, StringComparer.OrdinalIgnoreCase);
        var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in ShippedProjects)
            ids.UnionWith(ProjectPackageReferences(root, project, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        foreach (var id in ids)
        {
            var version = versions.TryGetValue(id, out var v) ? v : "unknown";
            var c = new SbomComponent { Ref = $"pkg:nuget/{id}@{version}", Type = "library", Name = id, Version = version, Kind = "nuget", Purl = $"pkg:nuget/{id}@{version}" };
            c.Properties["keel:inventory"] = "declared in a project file; transitive packages not resolved";
            _components.Add(c);
        }
    }

    private static IEnumerable<string> ProjectPackageReferences(string root, string project, HashSet<string> visited)
    {
        if (!visited.Add(project)) yield break;
        var file = Path.Combine(root, "src", project, project + ".csproj");
        if (!File.Exists(file)) yield break;
        var doc = XDocument.Load(file);
        foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            yield return (string)e.Attribute("Include")!;
        foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            foreach (var id in ProjectPackageReferences(root, Path.GetFileNameWithoutExtension((string)e.Attribute("Include")!), visited))
                yield return id;
    }

    private static void ReadNuspec(SbomComponent c, string dir, string id)
    {
        var nuspec = Directory.EnumerateFiles(dir, "*.nuspec").FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), id, StringComparison.OrdinalIgnoreCase))
                     ?? Directory.EnumerateFiles(dir, "*.nuspec").FirstOrDefault();
        string? licenseFile = null;
        if (nuspec is not null)
        {
            var meta = XDocument.Load(nuspec).Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
            string? Field(string name) => meta?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
            c.Copyright = Field("copyright");
            c.Supplier = Field("authors");
            if (meta?.Elements().FirstOrDefault(e => e.Name.LocalName == "projectUrl")?.Value.Trim() is { Length: > 0 } project) c.Url = project;
            var license = meta?.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
            if (license is not null && (string?)license.Attribute("type") == "expression") c.Licenses.Add(license.Value.Trim());
            else if (license is not null && (string?)license.Attribute("type") == "file") { licenseFile = license.Value.Trim(); c.Licenses.Add("LicenseRef-" + Slug(id) + "-file"); }
            else if (Field("licenseUrl") is { Length: > 0 } url) { c.Licenses.Add("LicenseRef-url:" + url); }
        }
        // The licence text the package itself ships, if any (offline sites need the text, not a link).
        var candidates = new List<string>();
        if (licenseFile is not null && File.Exists(Path.Combine(dir, licenseFile))) candidates.Add(Path.Combine(dir, licenseFile));
        candidates.AddRange(Directory.EnumerateFiles(dir).Where(f => LicenseFileName().IsMatch(Path.GetFileName(f))));
        foreach (var f in candidates.Distinct(StringComparer.OrdinalIgnoreCase).Take(3))
        {
            var info = new FileInfo(f);
            if (info.Length is 0 or > 2_000_000) continue;
            c.LicenseTexts.Add((Path.GetFileName(f), File.ReadAllText(f).ReplaceLineEndings("\n").TrimEnd()));
        }
    }

    // ---------------------------------------------------------------- the FreeCAD add-on (Python) and development-only components

    private static readonly string[] AddonDirectories = ["keel_plm", "resources"];

    /// <summary>The add-on itself, with a digest of its files. It uses the Python standard library at run time; the packages the repository's own scripts need are catalogued as development components.</summary>
    private void AddPythonAddon(string root)
    {
        var dir = Path.Combine(root, "tools", "freecad-keel");
        var manifest = Path.Combine(dir, "package.xml");
        if (!File.Exists(manifest)) { _warnings.Add("tools/freecad-keel/package.xml not found: the FreeCAD add-on is not inventoried."); return; }
        var meta = XDocument.Load(manifest).Root!.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value.Trim(), StringComparer.Ordinal);
        var files = new List<string> { manifest };
        foreach (var f in new[] { "Init.py", "InitGui.py" }) if (File.Exists(Path.Combine(dir, f))) files.Add(Path.Combine(dir, f));
        foreach (var sub in AddonDirectories)
            if (Directory.Exists(Path.Combine(dir, sub)))
                files.AddRange(Directory.EnumerateFiles(Path.Combine(dir, sub), "*", SearchOption.AllDirectories).Where(f => !f.Contains("__pycache__", StringComparison.Ordinal) && !f.EndsWith(".pyc", StringComparison.Ordinal)));
        var lines = files.Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/') + "  " + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f)))).Order(StringComparer.Ordinal).ToList();
        var c = new SbomComponent
        {
            Ref = "addon:keel-freecad-addon@" + meta["version"], Type = "application", Name = "keel-freecad-addon", Version = meta["version"], Kind = "first-party",
            Supplier = "Keel PLM team", Purl = "pkg:generic/keel-freecad-addon@" + meta["version"],
        };
        c.Hashes["SHA-256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")));
        c.Properties["keel:first-party"] = "true";
        c.Properties["keel:files"] = lines.Count.ToString(CultureInfo.InvariantCulture);
        c.Properties["keel:digest"] = "SHA-256 over the sorted lines 'path  sha256' of package.xml, Init*.py, keel_plm/ and resources/ (tests excluded)";
        c.Properties["keel:runtime-dependencies"] = "Python standard library and the FreeCAD host application only (no third-party Python package is imported by the add-on)";
        _components.Add(c);
    }

    /// <summary>Components that only developers, tests and maintainers use: the Python packages of the add-on's tests and fixture generator, the FreeCAD host, and the test-only vendored axe-core.</summary>
    private void AddDevelopment(string root)
    {
        foreach (var d in _catalogue!["development"]!.AsArray().Select(n => n!.AsObject()))
        {
            var name = Str(d, "name");
            var kind = Str(d, "kind");
            var version = d["version"]?.GetValue<string>();
            var constraint = d["specifier"]?.GetValue<string>();
            var c = new SbomComponent
            {
                Ref = kind == "python" ? $"pkg:pypi/{name}{(version is null ? "" : "@" + version)}" : $"{kind}:{Slug(name)}@{version ?? "unpinned"}",
                Type = kind == "vendored-file" ? "file" : "library", Name = name, Version = version, Kind = kind, Shipped = false,
                Supplier = Str(d, "supplier"), Url = Str(d, "url"),
                Purl = kind == "python" ? $"pkg:pypi/{name.ToLowerInvariant()}{(version is null ? "" : "@" + version)}" : null,
            };
            c.Licenses.Add(Str(d, "license"));
            if (constraint is not null) c.Properties["keel:version-constraint"] = constraint;
            c.Properties["keel:reason"] = Str(d, "note");
            c.Properties["keel:licence-basis"] = Str(d, "licenseBasis");
            if (d["file"]?.GetValue<string>() is { } file)
            {
                var full = Path.Combine(root, file);
                if (!File.Exists(full)) throw new FileNotFoundException($"The development component {name} names {file}, which does not exist.", full);
                c.Hashes["SHA-256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full)));
                c.Properties["keel:file"] = file;
            }
            _components.Add(c);
        }
    }

    // ---------------------------------------------------------------- npm (viewer vendoring tool lock file)

    private void AddNpm(string root)
    {
        var lockFile = Path.Combine(root, "tools", "viewer-vendor", "package-lock.json");
        if (!File.Exists(lockFile)) { _warnings.Add("tools/viewer-vendor/package-lock.json not found: no npm inventory."); return; }
        var lockJson = JsonNode.Parse(File.ReadAllText(lockFile))!.AsObject();
        var packages = lockJson["packages"]!.AsObject();
        var direct = packages[""]!["dependencies"]!.AsObject().Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, node) in packages)
        {
            if (key.Length == 0) continue;
            var p = node!.AsObject();
            var name = key[(key.LastIndexOf("node_modules/", StringComparison.Ordinal) + "node_modules/".Length)..];
            var version = p["version"]!.GetValue<string>();
            var shipped = direct.Contains(name);
            var c = new SbomComponent
            {
                Ref = $"pkg:npm/{name}@{version}", Type = "library", Name = name, Version = version, Kind = "npm", Shipped = shipped,
                Purl = $"pkg:npm/{name.Replace("@", "%40", StringComparison.Ordinal)}@{version}",
                Url = $"https://www.npmjs.com/package/{name}/v/{version}",
            };
            if (p["license"]?.GetValue<string>() is { Length: > 0 } lic) c.Licenses.Add(lic);
            else _warnings.Add($"npm package {name} {version}: the lock file declares no licence.");
            if (p["integrity"]?.GetValue<string>() is { } integrity && integrity.StartsWith("sha512-", StringComparison.Ordinal))
                c.Hashes["SHA-512"] = Convert.ToHexStringLower(Convert.FromBase64String(integrity["sha512-".Length..]));
            c.Properties["keel:shipped"] = shipped ? "true" : "false";
            if (!shipped)
                c.Properties["keel:reason"] = p["dev"]?.GetValue<bool>() == true ? "build tool of tools/viewer-vendor; not delivered"
                    : "optional dependency of a vendored library used only by Node; not vendored";
            _components.Add(c);
        }
    }

    private static readonly (string Prefix, string Package)[] VendorOrigins =
    [
        ("occt-import-js", "occt-import-js"), ("pdf.", "pdfjs-dist"), ("three-bundle", "three"),
    ];

    private void AddVendoredViewerFiles(string root)
    {
        var viewer = Path.Combine(root, "src", "Keel.Web", "wwwroot", "viewer");
        var manifestPath = Path.Combine(viewer, "manifest.json");
        if (!File.Exists(manifestPath)) { _warnings.Add("The viewer asset manifest is missing: no vendored files inventoried."); return; }
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        foreach (var f in manifest["files"]!.AsArray().Select(n => n!.AsObject()).Where(f => f["vendored"]?.GetValue<bool>() == true))
        {
            var rel = f["path"]!.GetValue<string>();
            var full = Path.Combine(viewer, rel);
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full)));
            if (!string.Equals(actual, f["sha256"]!.GetValue<string>(), StringComparison.Ordinal))
                throw new InvalidDataException($"Vendored viewer file {rel} differs from its manifest entry; regenerate the manifest (tools/viewer-vendor) before building a release.");
            var name = "viewer/" + rel;
            var c = new SbomComponent { Ref = "file:" + name, Type = "file", Name = name, Kind = "vendored-file", Supplier = null };
            c.Licenses.Add(f["license"]!.GetValue<string>());
            c.Hashes["SHA-256"] = actual;
            c.Properties["keel:component"] = f["component"]?.GetValue<string>() ?? "";
            c.Properties["keel:size-bytes"] = new FileInfo(full).Length.ToString(CultureInfo.InvariantCulture);
            var origin = VendorOrigins.FirstOrDefault(v => Path.GetFileName(rel).StartsWith(v.Prefix, StringComparison.Ordinal));
            var package = _components.FirstOrDefault(x => x.Kind == "npm" && x.Name == origin.Package);
            if (package is not null) c.DependsOn.Add(package.Ref);
            _components.Add(c);
        }
    }

    // ---------------------------------------------------------------- embedded fonts

    private void AddFonts(string root)
    {
        var dir = Path.Combine(root, "src", "Keel.Documents", "Marking", "Fonts");
        if (!Directory.Exists(dir)) return;
        var licenseText = File.Exists(Path.Combine(dir, "DejaVu-LICENSE.txt")) ? File.ReadAllText(Path.Combine(dir, "DejaVu-LICENSE.txt")).ReplaceLineEndings("\n").TrimEnd() : null;
        foreach (var font in Directory.EnumerateFiles(dir, "*.ttf").Order(StringComparer.Ordinal))
        {
            var name = "Keel.Documents/Marking/Fonts/" + Path.GetFileName(font);
            var c = new SbomComponent { Ref = "file:" + name, Type = "file", Name = name, Kind = "embedded-file", Supplier = "DejaVu fonts project (Bitstream Vera derivative)", Url = "https://dejavu-fonts.github.io/" };
            c.Licenses.Add("Bitstream-Vera");
            c.Hashes["SHA-256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(font)));
            c.Properties["keel:use"] = "embedded resource of Keel.Documents; stamps marking banners into PDFs";
            if (licenseText is not null) c.LicenseTexts.Add(("DejaVu-LICENSE.txt", licenseText));
            _components.Add(c);
        }
    }

    // ---------------------------------------------------------------- container images (pins)

    private void AddImages(string root)
    {
        var pins = Path.Combine(root, "deploy", "images.env");
        if (!File.Exists(pins)) return;
        foreach (var line in File.ReadAllLines(pins))
        {
            var m = Regex.Match(line, @"^(?<key>[A-Z_]+)=(?<repo>[^:@]+(?::[0-9]+)?(?:/[^:@]+)*):(?<tag>[^@]+)@sha256:(?<digest>[0-9a-f]{64})\s*$");
            if (!m.Success) continue;
            var repo = m.Groups["repo"].Value;
            var tag = m.Groups["tag"].Value;
            var digest = m.Groups["digest"].Value;
            var c = new SbomComponent
            {
                Ref = $"image:{repo}:{tag}@{digest[..12]}", Type = "container", Name = repo, Version = tag, Kind = "container",
                Purl = $"pkg:oci/{repo[(repo.LastIndexOf('/') + 1)..]}@sha256%3A{digest}?repository_url={repo}&tag={tag}",
            };
            c.Hashes["SHA-256"] = digest;
            c.Properties["keel:pin"] = m.Groups["key"].Value;
            c.Properties["keel:os-packages"] = "Listed in sbom/image-*.cdx.json when the bundle was built with syft; distribution packages carry GPL/LGPL licences (see THIRD-PARTY-NOTICES.md, section on operating system packages).";
            _components.Add(c);
        }
    }

    // ---------------------------------------------------------------- source archives attach to the components they correspond to

    private void AttachSources(List<SourceArchive> sources)
    {
        foreach (var s in sources)
        {
            foreach (var c in _components.Where(c => c.Shipped && (string.Equals(c.Name, s.Component, StringComparison.Ordinal)
                                                                    || c.Properties.GetValueOrDefault("keel:source-id") == s.Id
                                                                    || (c.Kind == "vendored-file" && s.Covers.Contains(c.Name, StringComparer.Ordinal)))))
            {
                c.References.Add(("source-distribution", "sources/" + s.File, s.Sha256, $"{s.Purpose} Upstream: {s.Url}"));
                c.Properties["keel:copyleft-source"] = "sources/" + s.File;
            }
            if (!_components.Any(c => c.Shipped && (c.Name == s.Component || c.Properties.GetValueOrDefault("keel:source-id") == s.Id)))
                throw new InvalidDataException($"Source archive {s.Id} names component '{s.Component}', which is not part of the inventory.");
        }
        foreach (var c in _components.Where(c => c.Shipped && c.IsCopyleft && !c.References.Any(r => r.Type == "source-distribution")))
            _warnings.Add($"Copyleft component {c.Name} {c.Version} ({string.Join(", ", c.Licenses)}) has no source archive in deploy/offline/third-party-sources.json.");
    }

    internal static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
}
