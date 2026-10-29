using System.Globalization;
using Keel.SbomTool;

// keel-sbom: writes sbom/keel-plm.cdx.json (CycloneDX 1.5) and THIRD-PARTY-NOTICES.md from files in the repository, with no network.
//   keel-sbom --root <repo> --version <v> --out <dir> [--assets <project.assets.json>]... [--timestamp <ISO 8601>] [--strict]
// --strict turns every gap (a component without a licence, a copyleft component without a source archive) into a failure.
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
    var result = SbomGenerator.Generate(new SbomOptions(root, version, when, assets));
    Directory.CreateDirectory(Path.Combine(outDir, "sbom"));
    File.WriteAllText(Path.Combine(outDir, "sbom", "keel-plm.cdx.json"), result.SbomJson);
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
