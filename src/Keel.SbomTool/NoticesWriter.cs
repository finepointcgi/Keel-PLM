using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Keel.SbomTool;

/// <summary>
/// Writes THIRD-PARTY-NOTICES.md for the offline bundle: what is shipped under which licence, the copyleft components with the written offer and where their
/// corresponding source is in the bundle, how to replace them, and the licence texts. It claims no legal conclusion: legal review is recorded as pending.
/// </summary>
internal static class NoticesWriter
{
    public static string Write(SbomOptions o, IReadOnlyList<SbomComponent> all, IReadOnlyList<SourceArchive> sources, string root, IReadOnlyList<string> warnings)
    {
        var shipped = all.Where(c => c.Shipped && c.Kind is not "container").ToList();
        var sb = new StringBuilder();
        sb.Append("# Third-party notices: Keel PLM ").AppendLine(o.Version);
        sb.AppendLine();
        sb.AppendLine("This file lists the third-party software delivered with this release, its licences, and where the corresponding source of the copyleft components is. It is generated from the lock files, project files and vendored files of the release (`sbom/keel-plm.cdx.json` is the machine-readable form). It is an engineering inventory, not legal advice: **legal review of these notices and of the licence conclusions is pending** (see `docs/third-party/legal-review.md` in the repository). Nothing here is a statement that counsel has approved the delivery.");
        sb.AppendLine();
        sb.Append("Generated ").Append(o.Timestamp.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).AppendLine(" (UTC).");
        sb.AppendLine();

        // ---- copyleft
        var copyleft = shipped.Where(c => c.IsCopyleft && c.Kind is not "vendored-file").ToList();
        sb.AppendLine("## 1. Components under the GNU Lesser General Public License");
        sb.AppendLine();
        if (copyleft.Count == 0)
            sb.AppendLine("No LGPL or GPL component is delivered in the application itself.");
        else
        {
            sb.AppendLine("| Component | Version | Licence | Delivered as | Corresponding source in this bundle |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var c in copyleft)
            {
                var delivered = string.Join(", ", shipped.Where(f => f.Kind == "vendored-file" && f.DependsOn.Contains(c.Ref, StringComparer.Ordinal)).Select(f => "`" + f.Name + "`"));
                if (c.Properties.GetValueOrDefault("keel:compiled-into") is { Length: > 0 } into) delivered = "compiled into " + into;
                var src = string.Join("<br>", c.References.Where(r => r.Type == "source-distribution").Select(r => "`" + r.Url + "`"));
                sb.Append("| ").Append(c.Name).Append(" | ").Append(Cell(c.Version)).Append(" | ").Append(string.Join(" ", c.Licenses)).Append(" | ").Append(delivered.Length > 0 ? delivered : "see SBOM")
                    .Append(" | ").Append(src.Length > 0 ? src : "**none**").AppendLine(" |");
            }
            sb.AppendLine();
            sb.AppendLine("### Source archives");
            sb.AppendLine();
            sb.AppendLine("The archives below are in the `sources/` directory of this bundle. Their SHA-256 values are also in `sources/SOURCES.txt`, and every file of the bundle is covered by the signed `SHA256SUMS`.");
            sb.AppendLine();
            sb.AppendLine("| File | SHA-256 | What it is | Upstream |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var s in sources)
                sb.Append("| `sources/").Append(s.File).Append("` | `").Append(s.Sha256).Append("` | ").Append(s.Purpose).Append(" | ").Append(s.Url).AppendLine(" |");
            sb.AppendLine();
            sb.AppendLine("### Written offer and replacement");
            sb.AppendLine();
            sb.AppendLine("The corresponding source of the LGPL components above is delivered with the software in `sources/`, so no network request is needed to obtain it. For three years after delivery, the supplier of this bundle will also, on request and for a charge no more than the cost of physically performing the distribution, give any third party a machine-readable copy of that source on a medium customarily used for software interchange (LGPL 2.1, section 6). The wording of this offer is a draft pending legal review, and the supplier contact is the placeholder `[Company]` until a real one is entered before delivery.");
            sb.AppendLine();
            sb.AppendLine("The LGPL components are not linked into the Keel executables. They are two separate files, `occt-import-js.js` and `occt-import-js.wasm`, which the file viewer's web worker loads from the application's own origin and uses only through messages. A recipient may replace them with a modified build of the same interface: build the library from the archives above (the scripts in `tools/` of the occt-import-js archive drive the Emscripten build), then place the two resulting files over `/app/wwwroot/viewer/vendor/` of the application container (a read-only bind mount or volume in compose, or a `configMap`/volume in Kubernetes, replaces them without rebuilding the image). Setting `Keel:Viewer:EnableCadImport=false` switches the STEP and IGES viewer off entirely.");
            sb.AppendLine();
        }

        // ---- operating system layers
        sb.AppendLine("## 2. Operating system packages in the container images");
        sb.AppendLine();
        sb.AppendLine("The application image is built on Red Hat UBI 9, and the bundle can also carry the PostgreSQL, SeaweedFS and Caddy images. Those images contain Linux distribution packages under the GPL and LGPL (for example the C library and the shell). Their package lists are in `sbom/image-*.cdx.json` when the bundle was built with `syft`; the SBOM names each image by digest. The corresponding source of those packages is published by the image publishers. It is included in `sources/os/` only if the release engineer supplied it when building the bundle (`--os-sources-dir`); `sources/SOURCES.txt` states which case applies to this bundle. **This part is an open item** pending legal review: for an air-gapped site a link to a publisher's server is not a usable offer.");
        sb.AppendLine();

        // ---- inventory
        sb.AppendLine("## 3. Inventory of delivered components");
        sb.AppendLine();
        sb.AppendLine("| Component | Version | Licence | Kind |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var c in shipped.OrderBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal))
            sb.Append("| ").Append(c.Name).Append(" | ").Append(Cell(c.Version)).Append(" | ").Append(LicenseCell(c)).Append(" | ").Append(c.Kind).AppendLine(" |");
        sb.AppendLine();
        var excluded = all.Where(c => !c.Shipped).ToList();
        if (excluded.Count > 0)
        {
            sb.Append("Not delivered (build tooling of `tools/viewer-vendor`, listed in the SBOM with scope `excluded`): ")
              .Append(string.Join(", ", excluded.Select(c => c.Name + " " + c.Version).Take(6))).Append(excluded.Count > 6 ? $" and {excluded.Count - 6} more" : "").AppendLine(".");
            sb.AppendLine();
        }
        sb.AppendLine("Container images referenced by this release (pinned by digest in `images.env`): " + string.Join(", ", all.Where(c => c.Kind == "container").Select(c => $"`{c.Name}:{c.Version}`")) + ".");
        sb.AppendLine();

        // ---- licence texts
        sb.AppendLine("## 4. Licence texts");
        sb.AppendLine();
        sb.AppendLine("Texts shipped with the components themselves, each once. Where a component has no text of its own it is identified by its SPDX expression in section 3 and in the SBOM; the repository's `docs/third-party/viewer-licenses/` holds the texts for the viewer files.");
        sb.AppendLine();
        var texts = new SortedDictionary<string, (string Text, List<string> Used)>(StringComparer.Ordinal);
        foreach (var c in shipped)
            foreach (var (label, text) in c.LicenseTexts)
            {
                var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
                if (!texts.TryGetValue(key, out var entry)) texts[key] = entry = (text, []);
                entry.Used.Add($"{c.Name} {c.Version}".Trim() + (label.Length > 0 ? $" ({label})" : ""));
            }
        var viewerLicenses = Path.Combine(root, "docs", "third-party", "viewer-licenses");
        if (Directory.Exists(viewerLicenses))
            foreach (var f in Directory.EnumerateFiles(viewerLicenses, "*.txt").Order(StringComparer.Ordinal))
            {
                var text = File.ReadAllText(f).ReplaceLineEndings("\n").TrimEnd();
                var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
                if (!texts.TryGetValue(key, out var entry)) texts[key] = entry = (text, []);
                entry.Used.Add("viewer: " + Path.GetFileName(f));
            }
        var n = 0;
        foreach (var (_, (text, used)) in texts.OrderBy(t => t.Value.Used.Min(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            n++;
            sb.Append("### 4.").Append(n.ToString(CultureInfo.InvariantCulture)).Append(". ").AppendLine(string.Join("; ", used.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(8)) + (used.Count > 8 ? " and others" : ""));
            sb.AppendLine();
            sb.AppendLine("````text");
            sb.AppendLine(text);
            sb.AppendLine("````");
            sb.AppendLine();
        }
        if (warnings.Count > 0)
        {
            sb.AppendLine("## 5. Gaps found while generating this file");
            sb.AppendLine();
            foreach (var w in warnings.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) sb.Append("- ").AppendLine(w);
        }
        return sb.ToString().ReplaceLineEndings("\n");
    }

    private static string Cell(string? v) => string.IsNullOrEmpty(v) ? "" : (v.Length > 40 ? v[..12] : v);

    private static string LicenseCell(SbomComponent c) =>
        c.Kind == "first-party" ? "first-party (Keel)" : c.Licenses.Count == 0 ? "not declared" : string.Join(" ", c.Licenses.Select(l => l.StartsWith("LicenseRef-url:", StringComparison.Ordinal) ? l["LicenseRef-url:".Length..] : l.StartsWith("LicenseRef-", StringComparison.Ordinal) ? "license file in package" : l));
}
