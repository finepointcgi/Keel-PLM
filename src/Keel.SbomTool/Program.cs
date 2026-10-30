using System.Globalization;
using Keel.SbomTool;

// keel-sbom: writes sbom/keel-plm.cdx.json (CycloneDX 1.5) and THIRD-PARTY-NOTICES.md from files in the repository, with no network.
//   keel-sbom --root <repo> --version <v> --out <dir> [--assets <project.assets.json>]... [--timestamp <ISO 8601>] [--strict]
// --strict turns every gap (a component without a licence, a copyleft component without a source archive) into a failure.
//   keel-sbom licenses --root <repo> [--out THIRD-PARTY-LICENSES.md | --check]   writes (or checks against the committed file) the licence inventory incl. development components
//   keel-sbom sources --root <repo>        prints "id<TAB>file<TAB>url<TAB>sha256<TAB>size" of every pinned source archive (for scripts/offline-compliance.sh)
//   keel-sbom verify --bundle <dir>        checks the licence and source part of an unpacked bundle or staging directory (exit 1 and the problems when it is incomplete)
static int Usage(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine("usage: keel-sbom [generate] --root <repo> --version <v> --out <dir> [--assets <project.assets.json>]... [--timestamp <ISO 8601>] [--strict]");
    Console.Error.WriteLine("       keel-sbom sources --root <repo>");
    Console.Error.WriteLine("       keel-sbom verify --bundle <dir>");
    return 2;
}

if (args.Length > 0 && args[0] == "licenses") return Licenses(args[1..]);
if (args.Length > 0 && args[0] is "sources" or "verify")
{
    var command = args[0];
    string? value = null;
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == (command == "sources" ? "--root" : "--bundle") && i + 1 < args.Length) value = args[++i];
        else return Usage($"unknown option {args[i]}");
    }
    if (value is null) return Usage(command == "sources" ? "--root is required." : "--bundle is required.");
    try
    {
        if (command == "sources")
        {
            foreach (var s in SbomGenerator.ReadSources(value)) Console.WriteLine($"{s.Id}\t{s.File}\t{s.Url}\t{s.Sha256}\t{s.Size}");
            return 0;
        }
        var problems = BundleVerifier.Verify(value);
        foreach (var p in problems) Console.Error.WriteLine("FAIL " + p);
        if (problems.Count == 0) Console.WriteLine("keel-sbom: licence and source checks passed");
        return problems.Count == 0 ? 0 : 1;
    }
    catch (Exception e) when (e is InvalidDataException or FileNotFoundException or DirectoryNotFoundException or System.Text.Json.JsonException)
    {
        Console.Error.WriteLine("error: " + e.Message);
        return 1;
    }
}
if (args.Length > 0 && args[0] == "generate") args = args[1..];

string? root = null, version = null, outDir = null, timestamp = null;
var assets = new List<string>();
var strict = false;
var includeDevelopment = false;
for (var i = 0; i < args.Length; i++)
{
    string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value.");
    switch (args[i])
    {
        case "--root": root = Next(); break;
        case "--version": version = Next(); break;
        case "--out": outDir = Next(); break;
        case "--assets": assets.Add(Next()); break;
        case "--timestamp": timestamp = Next(); break;
        case "--strict": strict = true; break;
        case "--include-development": includeDevelopment = true; break;
        case "-h" or "--help": return Usage("keel-sbom");
        default: return Usage($"unknown option {args[i]}");
    }
}
if (root is null || version is null || outDir is null) return Usage("--root, --version and --out are required.");
var when = timestamp is null
    ? (Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH") is { Length: > 0 } epoch ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(epoch, CultureInfo.InvariantCulture)) : DateTimeOffset.UtcNow)
    : DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

try
{
    var result = SbomGenerator.Generate(new SbomOptions(root, version, when, assets, includeDevelopment));
    Directory.CreateDirectory(Path.Combine(outDir, "sbom"));
    File.WriteAllText(Path.Combine(outDir, "sbom", includeDevelopment ? "keel-plm-full.cdx.json" : "keel-plm.cdx.json"), result.SbomJson);
    File.WriteAllText(Path.Combine(outDir, "THIRD-PARTY-NOTICES.md"), result.NoticesMarkdown);
    var shipped = result.Components.Count(c => c.Shipped);
    Console.WriteLine($"keel-sbom: {result.Components.Count} components ({shipped} delivered), {result.Sources.Count} source archives catalogued -> {outDir}");
    foreach (var w in result.Warnings.Distinct(StringComparer.Ordinal)) Console.Error.WriteLine("warning: " + w);
    return strict && result.Warnings.Count > 0 ? 1 : 0;
}
catch (Exception e) when (e is InvalidDataException or FileNotFoundException or DirectoryNotFoundException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 1;
}

static int Licenses(string[] a)
{
    string? root = null, outFile = null;
    var check = false;
    for (var i = 0; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "--root" when i + 1 < a.Length: root = a[++i]; break;
            case "--out" when i + 1 < a.Length: outFile = a[++i]; break;
            case "--check": check = true; break;
            default: return Usage($"unknown option {a[i]}");
        }
    }
    if (root is null || (outFile is null) == !check) return Usage("licenses needs --root and exactly one of --out <file> or --check.");
    try
    {
        var (markdown, violations, result) = LicensesWriter.Generate(Path.GetFullPath(root));
        foreach (var v in violations) Console.Error.WriteLine("FAIL policy: " + v);
        if (!check)
        {
            File.WriteAllText(outFile!, markdown);
            Console.WriteLine($"keel-sbom: {result.Components.Count} components -> {outFile}");
            return violations.Count == 0 ? 0 : 1;
        }
        var committed = Path.Combine(root, LicensesWriter.FileName);
        if (!File.Exists(committed)) { Console.Error.WriteLine($"FAIL {LicensesWriter.FileName} is missing; run scripts/generate-sbom.sh --licenses"); return 1; }
        var current = File.ReadAllText(committed).ReplaceLineEndings("\n");
        if (!string.Equals(current, markdown, StringComparison.Ordinal))
        {
            var have = current.Split('\n').ToHashSet(StringComparer.Ordinal);
            var want = markdown.Split('\n').ToHashSet(StringComparer.Ordinal);
            foreach (var l in want.Except(have).Where(l => l.StartsWith("| ", StringComparison.Ordinal)).Take(20)) Console.Error.WriteLine("FAIL missing or changed row: " + l);
            foreach (var l in have.Except(want).Where(l => l.StartsWith("| ", StringComparison.Ordinal)).Take(20)) Console.Error.WriteLine("FAIL stale row: " + l);
            Console.Error.WriteLine($"FAIL {LicensesWriter.FileName} is out of date; run scripts/generate-sbom.sh --licenses and commit the result");
            return 1;
        }
        Console.WriteLine($"keel-sbom: {LicensesWriter.FileName} is current ({result.Components.Count} components), policy {(violations.Count == 0 ? "passed" : "FAILED")}");
        return violations.Count == 0 ? 0 : 1;
    }
    catch (Exception e) when (e is InvalidDataException or FileNotFoundException or DirectoryNotFoundException or System.Text.Json.JsonException)
    {
        Console.Error.WriteLine("error: " + e.Message);
        return 1;
    }
}
