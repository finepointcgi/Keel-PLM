using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Keel.SbomTool;

/// <summary>
/// Writes THIRD-PARTY-LICENSES.md, the committed inventory of every third-party component the repository delivers or uses to build and test (ADR-0059): name, version,
/// licence expression and a hash for each. It is deterministic (no timestamp, no release version) so that a test can regenerate it and compare it with the committed file.
/// A new dependency without a row makes that test fail. It claims no legal conclusion.
/// </summary>
public static partial class LicensesWriter
{
    public const string FileName = "THIRD-PARTY-LICENSES.md";

    /// <summary>Licences that may not appear on any component without an explicit decision: strong copyleft and source-available terms that do not fit an on-premises delivery.</summary>
    [GeneratedRegex(@"(^|[^A-Za-z])(AGPL|SSPL|BUSL|Commons-Clause|CC-BY-NC|Elastic-2\.0)|(^|[^A-Za-z])GPL-[0-9](\.[0-9])?(-only|-or-later|\+)?(?!.*(Classpath|exception))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenPattern();

    /// <summary>Generates the document for the given repository and returns the text and the policy violations found.</summary>
    public static (string Markdown, IReadOnlyList<string> Violations, SbomResult Result) Generate(string root, IReadOnlyList<string>? assetsFiles = null)
    {
        var result = SbomGenerator.Generate(new SbomOptions(root, "0.0.0", DateTimeOffset.UnixEpoch, assetsFiles, IncludeDevelopment: true));
        var violations = Policy(result.Components);
        return (Render(result), violations, result);
    }

    /// <summary>Policy: every delivered component declares a licence, none is under a forbidden licence, delivered copyleft components have a source archive (a warning of the generator).</summary>
    public static IReadOnlyList<string> Policy(IReadOnlyList<SbomComponent> components)
    {
        var problems = new List<string>();
        foreach (var c in components.Where(c => c.Kind is not ("container" or "first-party")))
        {
            var label = $"{c.Name} {c.Version}".Trim();
            if (c.Licenses.Count == 0) problems.Add($"{label}: no licence declared" + (c.Shipped ? " (delivered component)" : ""));
            foreach (var l in c.Licenses.Where(l => ForbiddenPattern().IsMatch(l)))
                problems.Add($"{label}: licence {l} is not allowed (strong copyleft or source-available); remove the dependency or record a decision in docs/review/legal-open-source.md first");
        }
        return problems;
    }

    private static string Render(SbomResult r)
    {
        var all = r.Components;
        var sb = new StringBuilder();
        sb.AppendLine("# Third-party licences");
        sb.AppendLine();
        sb.AppendLine("Every third-party component that Keel PLM delivers, and every one it uses to build and test, with its licence and a hash. **This file is generated; do not edit it.** Regenerate with `scripts/generate-sbom.sh --licenses` after changing any dependency (NuGet, the viewer's npm lock file, a vendored file, an embedded font, an image pin, the Python add-on's requirements). `ThirdPartyLicensesTests` fails when a dependency has no row here or a row is stale, and `keel-sbom licenses --check` runs the same check in CI (ADR-0059).");
        sb.AppendLine();
        sb.AppendLine("It is an engineering inventory read from the repository's own files (project assets, lock files, manifests, nuspec metadata). It is not legal advice and nothing here says counsel has approved a licence conclusion: the legal packet is [docs/review/legal-open-source.md](docs/review/legal-open-source.md) and its status is pending. Texts of the licences that ship with the delivered components are in the offline bundle's `THIRD-PARTY-NOTICES.md` and in [docs/third-party/viewer-licenses](docs/third-party/viewer-licenses). The full SBOM (CycloneDX 1.5, SHA-256 and SHA-512 hashes) is produced by `scripts/generate-sbom.sh`.");
        sb.AppendLine();
        sb.AppendLine("Hash column: SHA-256 of the file for files; for NuGet and npm packages the first 32 hex characters of the registry's SHA-512 of the package archive (the SBOM carries all 128); the digest of the manifest for container images. A dash means the source gives none.");
        sb.AppendLine();

        void Table(string title, string intro, IEnumerable<SbomComponent> items)
        {
            var list = items.OrderBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Version, StringComparer.Ordinal).ToList();
            sb.Append("## ").AppendLine(title);
            sb.AppendLine();
            sb.AppendLine(intro);
            sb.AppendLine();
            sb.AppendLine("| Component | Version | Licence | Kind | Hash |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var c in list)
                sb.Append("| ").Append(Esc(c.Name)).Append(" | ").Append(Esc(c.Version ?? c.Properties.GetValueOrDefault("keel:version-constraint") ?? "")).Append(" | ").Append(Esc(LicenseText(c)))
                  .Append(" | ").Append(c.Kind).Append(" | ").Append(HashText(c)).AppendLine(" |");
            sb.AppendLine();
        }

        var shipped = all.Where(c => c.Shipped && c.Kind is not ("container" or "first-party")).ToList();
        Table($"1. Delivered with the product ({shipped.Count})",
            "NuGet packages of `Keel.Api`, `Keel.AuditCli` and `Keel.VaultTool` and everything they pull in (for example MudBlazor, PdfPig, PDFsharp, AWSSDK.S3, Npgsql), the vendored browser files (three.js, pdf.js, occt-import-js with OpenCascade), the npm packages those files came from, the embedded DejaVu fonts, and the components compiled into the wasm file.",
            shipped);
        var dev = all.Where(c => !c.Shipped && c.Kind is not "container").ToList();
        Table($"2. Used to build, test and maintain, not delivered ({dev.Count})",
            "Test and tooling NuGet packages, the viewer vendoring tool's build dependencies, the Python packages of the add-on's tests and fixture generator, the FreeCAD host application, and the test-only axe-core. The SBOM marks them with scope `excluded`.",
            dev);
        var images = all.Where(c => c.Kind == "container").OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        sb.AppendLine("## 3. Container images (pinned by digest)");
        sb.AppendLine();
        sb.AppendLine("The packages inside these images (distribution packages with GPL and LGPL parts, among others) are not listed here; they are in `sbom/image-*.cdx.json` of an offline bundle built with `syft`, and the corresponding-source question for them is an open item of the legal packet.");
        sb.AppendLine();
        sb.AppendLine("| Image | Tag | Pin | Digest (sha256) |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var c in images)
            sb.Append("| ").Append(Esc(c.Name)).Append(" | ").Append(Esc(c.Version ?? "")).Append(" | ").Append(c.Properties.GetValueOrDefault("keel:pin") ?? "").Append(" | ").Append(c.Hashes.GetValueOrDefault("SHA-256") ?? "-").AppendLine(" |");
        sb.AppendLine();

        var first = all.Where(c => c.Kind == "first-party").ToList();
        if (first.Count > 0)
        {
            sb.AppendLine("## 4. Keel's own add-on");
            sb.AppendLine();
            sb.AppendLine("First-party code that is delivered but is not third-party software (listed so that its dependency statement is on record).");
            sb.AppendLine();
            sb.AppendLine("| Component | Version | Runtime dependencies | Files digest (sha256) |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var c in first)
                sb.Append("| ").Append(c.Name).Append(" | ").Append(c.Version).Append(" | ").Append(Esc(c.Properties.GetValueOrDefault("keel:runtime-dependencies") ?? "")).Append(" | ").Append(c.Hashes.GetValueOrDefault("SHA-256") ?? "-").AppendLine(" |");
            sb.AppendLine();
        }

        sb.AppendLine("## 5. Licence expressions in use");
        sb.AppendLine();
        sb.AppendLine("| Licence | Delivered | Not delivered |");
        sb.AppendLine("|---|---|---|");
        var groups = all.Where(c => c.Kind is not ("container" or "first-party")).GroupBy(LicenseText, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var g in groups)
            sb.Append("| ").Append(Esc(g.Key)).Append(" | ").Append(g.Count(c => c.Shipped).ToString(CultureInfo.InvariantCulture)).Append(" | ").Append(g.Count(c => !c.Shipped).ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
        sb.AppendLine();
        sb.AppendLine("Policy checked by `keel-sbom licenses --check`: every component declares a licence; none is under AGPL, SSPL, BUSL, a non-commercial or a plain GPL licence (weak copyleft such as LGPL and MPL-2.0 is allowed only where this document and the legal packet already record it, and every delivered copyleft component needs a pinned source archive in `deploy/offline/third-party-sources.json`).");
        sb.AppendLine();
        return sb.ToString().ReplaceLineEndings("\n");
    }

    private static string LicenseText(SbomComponent c) => c.Licenses.Count == 0
        ? "not declared"
        : string.Join(" ", c.Licenses.Select(l => l.StartsWith("LicenseRef-url:", StringComparison.Ordinal) ? "see " + l["LicenseRef-url:".Length..]
            : l.StartsWith("LicenseRef-", StringComparison.Ordinal) ? "license file in package" : l));

    private static string HashText(SbomComponent c)
    {
        if (c.Hashes.TryGetValue("SHA-256", out var s)) return "sha256:" + s;
        if (c.Hashes.TryGetValue("SHA-512", out var l)) return "sha512:" + l[..32];
        return "-";
    }

    private static string Esc(string v) => v.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
