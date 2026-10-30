using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Keel.SbomTool;

namespace Keel.UnitTests;

/// <summary>
/// ADR-0059: THIRD-PARTY-LICENSES.md is committed and must list every dependency. The dependencies are enumerated here from the repository's own files with code that is
/// independent of the generator (project assets, Directory.Packages.props, the npm lock file, the vendored manifest, fonts, image pins, the Python add-on's requirements and imports),
/// so a dependency added without regenerating the document makes these tests fail.
/// </summary>
public sealed partial class ThirdPartyLicensesTests
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

    private static string Committed => File.ReadAllText(Path.Combine(Root, LicensesWriter.FileName)).ReplaceLineEndings("\n");

    private static string Row(string name, string? version) => $"| {name.Replace("|", "\\|", StringComparison.Ordinal)} | {version} |";

    /// <summary>Every (kind, name, version) the repository depends on, enumerated without the generator.</summary>
    private static List<(string What, string Name, string? Version)> RequiredDependencies()
    {
        var list = new List<(string, string, string?)>();

        // NuGet: every package of every project's restore result (transitive included), shipped or not.
        var assetFiles = Directory.EnumerateFiles(Root, "project.assets.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine(".claude", ""), StringComparison.Ordinal) && !f.Contains("node_modules", StringComparison.Ordinal)
                        && (f.StartsWith(Path.Combine(Root, "src"), StringComparison.Ordinal) || f.StartsWith(Path.Combine(Root, "tests"), StringComparison.Ordinal)))
            .ToList();
        Assert.NotEmpty(assetFiles);   // run `dotnet restore Keel.slnx` first
        foreach (var f in assetFiles)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            foreach (var lib in doc.RootElement.GetProperty("libraries").EnumerateObject().Where(l => l.Value.GetProperty("type").GetString() == "package"))
            {
                var slash = lib.Name.IndexOf('/', StringComparison.Ordinal);
                list.Add(("NuGet package", lib.Name[..slash], lib.Name[(slash + 1)..]));
            }
        }

        // Central package versions (direct references).
        foreach (Match m in PackageVersion().Matches(File.ReadAllText(Path.Combine(Root, "Directory.Packages.props"))))
            list.Add(("central NuGet package", m.Groups["id"].Value, m.Groups["v"].Value));

        // npm lock file of the viewer vendoring tool.
        var lockJson = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "tools", "viewer-vendor", "package-lock.json")))!["packages"]!.AsObject();
        foreach (var (key, node) in lockJson.Where(p => p.Key.Length > 0))
            list.Add(("npm package", key[(key.LastIndexOf("node_modules/", StringComparison.Ordinal) + 13)..], node!["version"]!.GetValue<string>()));

        // Vendored viewer files.
        foreach (var f in JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "src", "Keel.Web", "wwwroot", "viewer", "manifest.json")))!["files"]!.AsArray().Where(n => n!["vendored"]?.GetValue<bool>() == true))
            list.Add(("vendored viewer file", "viewer/" + f!["path"]!.GetValue<string>(), null));

        // Embedded fonts.
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Root, "src", "Keel.Documents", "Marking", "Fonts"), "*.ttf"))
            list.Add(("embedded font", "Keel.Documents/Marking/Fonts/" + Path.GetFileName(f), null));

        // Image pins.
        foreach (Match m in ImagePin().Matches(File.ReadAllText(Path.Combine(Root, "deploy", "images.env"))))
            list.Add(("container image", m.Groups["repo"].Value, m.Groups["tag"].Value));

        // The Python add-on: requirements files and every third-party import.
        foreach (var line in File.ReadAllLines(Path.Combine(Root, "tools", "freecad-keel", "requirements-dev.txt")).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
            list.Add(("Python requirement", Regex.Match(line, "^[A-Za-z0-9_.-]+").Value, null));
        var stdlib = new HashSet<string>(StringComparer.Ordinal)
        {
            "__future__", "base64", "collections", "dataclasses", "hashlib", "io", "json", "os", "pathlib", "re", "shlex", "shutil", "ssl", "stat", "subprocess", "sys",
            "time", "typing", "urllib", "uuid", "zipfile", "keel_plm", "tests", "conftest", "importlib", "unittest", "tempfile", "threading", "http", "socket", "contextlib",
            "functools", "itertools", "datetime", "logging", "textwrap", "traceback", "types", "copy", "math", "struct", "gzip", "glob", "argparse", "enum", "string", "random",
            "platform", "secrets", "csv", "xml", "pytest", "jsonschema",   // the last two are checked through requirements-dev.txt above
        };
        var pyFiles = Directory.EnumerateFiles(Path.Combine(Root, "tools", "freecad-keel"), "*.py", SearchOption.AllDirectories).Where(f => !f.Contains("__pycache__", StringComparison.Ordinal)).ToList();
        var inRepo = pyFiles.Select(f => Path.GetFileNameWithoutExtension(f)).ToHashSet(StringComparer.Ordinal);   // modules that are files of the repository itself
        foreach (var py in pyFiles)
            foreach (Match m in PythonImport().Matches(File.ReadAllText(py)))
            {
                var top = m.Groups["mod"].Value;
                if (top == "FreeCADGui") top = "FreeCAD";   // one host application
                if (!stdlib.Contains(top) && !inRepo.Contains(top)) list.Add(("Python import", top, null));
            }
        return list;
    }

    /// <summary>The dependencies that have no row in <paramref name="document"/>.</summary>
    internal static List<string> Missing(string document)
    {
        var missing = new List<string>();
        foreach (var (what, name, version) in RequiredDependencies())
        {
            var found = what switch
            {
                "central NuGet package" => document.Contains($"| {name} | ", StringComparison.Ordinal),
                "Python requirement" or "Python import" => document.Contains($"| {name} | ", StringComparison.OrdinalIgnoreCase),
                "container image" => document.Contains($"| {name} | {version} |", StringComparison.Ordinal),
                _ => version is null ? document.Contains($"| {name} | ", StringComparison.Ordinal) : document.Contains(Row(name, version), StringComparison.Ordinal),
            };
            if (!found) missing.Add($"{what} {name} {version}".TrimEnd());
        }
        return missing.Distinct(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void Every_dependency_of_the_repository_has_a_row_in_the_committed_licence_document()
    {
        var missing = Missing(Committed);
        Assert.True(missing.Count == 0, "THIRD-PARTY-LICENSES.md has no row for: " + string.Join("; ", missing) + ". Run scripts/generate-sbom.sh --licenses and commit the result.");
    }

    [Theory]
    [InlineData("MudBlazor")]
    [InlineData("PdfPig")]
    [InlineData("PDFsharp")]
    [InlineData("three")]
    [InlineData("occt-import-js")]
    [InlineData("pdfjs-dist")]
    [InlineData("AWSSDK.S3")]
    [InlineData("Npgsql")]
    [InlineData("pytest")]
    [InlineData("axe-core")]
    public void The_named_components_are_listed_with_a_licence(string name)
    {
        var rows = Committed.Split('\n').Where(l => l.StartsWith($"| {name} | ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.DoesNotContain("not declared", r, StringComparison.Ordinal));
    }

    [Fact]
    public void The_check_fails_when_a_row_is_removed_so_a_forgotten_dependency_cannot_pass()
    {
        var withoutMudBlazor = string.Join('\n', Committed.Split('\n').Where(l => !l.StartsWith("| MudBlazor | ", StringComparison.Ordinal)));
        Assert.Contains(Missing(withoutMudBlazor), m => m.Contains("MudBlazor", StringComparison.Ordinal));
        var withoutThree = string.Join('\n', Committed.Split('\n').Where(l => !l.StartsWith("| three | ", StringComparison.Ordinal)));
        Assert.Contains(Missing(withoutThree), m => m.Contains("npm package three", StringComparison.Ordinal));
        var withoutPython = string.Join('\n', Committed.Split('\n').Where(l => !l.StartsWith("| jsonschema | ", StringComparison.Ordinal)));
        Assert.Contains(Missing(withoutPython), m => m.Contains("jsonschema", StringComparison.Ordinal));
        var stalePin = Committed.Replace("| caddy | 2.10 |", "| caddy | 2.9 |", StringComparison.Ordinal);
        Assert.Contains(Missing(stalePin), m => m.Contains("caddy", StringComparison.Ordinal));
    }

    [Fact]
    public void The_committed_document_equals_what_the_generator_writes_now_and_passes_the_licence_policy()
    {
        var (markdown, violations, _) = LicensesWriter.Generate(Root);
        Assert.Empty(violations);
        Assert.Equal(markdown, Committed);
    }

    [Fact]
    public void The_policy_refuses_strong_copyleft_source_available_and_undeclared_licences_but_allows_weak_copyleft()
    {
        static SbomComponent C(string name, params string[] licences)
        {
            var c = new SbomComponent { Ref = "x:" + name, Type = "library", Name = name, Version = "1", Kind = "nuget" };
            c.Licenses.AddRange(licences);
            return c;
        }
        Assert.Empty(LicensesWriter.Policy([C("a", "MIT"), C("b", "LGPL-2.1"), C("c", "MPL-2.0"), C("d", "LGPL-2.1-only WITH OCCT-exception-1.0"), C("e", "Apache-2.0 WITH LLVM-exception"), C("f", "GPL-2.0-only WITH Classpath-exception-2.0")]));
        foreach (var bad in new[] { "AGPL-3.0-only", "GPL-3.0-or-later", "GPL-2.0", "SSPL-1.0", "BUSL-1.1", "CC-BY-NC-4.0", "Commons-Clause" })
            Assert.Single(LicensesWriter.Policy([C("bad", bad)]));
        Assert.Single(LicensesWriter.Policy([C("none")]));
    }

    [Fact]
    public void The_python_add_on_catalogue_covers_its_requirements_and_the_add_on_declares_no_third_party_runtime_import()
    {
        var catalogue = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "deploy", "offline", "third-party-sources.json")))!["development"]!.AsArray()
            .Select(n => n!["name"]!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(Path.Combine(Root, "tools", "freecad-keel", "requirements-dev.txt")).Where(l => l.Trim().Length > 0))
            Assert.Contains(Regex.Match(line, "^[A-Za-z0-9_.-]+").Value, catalogue);
        // Only the fixture generator may import third-party modules; the add-on package itself uses the standard library and FreeCAD only.
        foreach (var py in Directory.EnumerateFiles(Path.Combine(Root, "tools", "freecad-keel", "keel_plm"), "*.py"))
        {
            var imports = PythonImport().Matches(File.ReadAllText(py)).Select(m => m.Groups["mod"].Value).Where(m => m is not ("FreeCAD" or "FreeCADGui" or "keel_plm" or "__future__")).ToList();
            Assert.DoesNotContain(imports, i => i is "requests" or "numpy" or "cadquery" or "OCP" or "yaml");
        }
    }

    [GeneratedRegex("<PackageVersion Include=\"(?<id>[^\"]+)\" Version=\"(?<v>[^\"]+)\"")]
    private static partial Regex PackageVersion();

    [GeneratedRegex(@"^[A-Z_]+=(?<repo>[^:@]+(?::[0-9]+)?(?:/[^:@]+)*):(?<tag>[^@]+)@sha256:[0-9a-f]{64}\s*$", RegexOptions.Multiline)]
    private static partial Regex ImagePin();

    [GeneratedRegex(@"^\s*(?:import|from)\s+(?<mod>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Multiline)]
    private static partial Regex PythonImport();
}
