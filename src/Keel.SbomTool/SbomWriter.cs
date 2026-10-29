using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Keel.SbomTool;

/// <summary>Serializes the inventory as CycloneDX 1.5 JSON. Deterministic: sorted components, a serial number derived from the content, and the caller's timestamp.</summary>
internal static class SbomWriter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Write(SbomOptions o, IReadOnlyList<SbomComponent> components, IReadOnlyList<SourceArchive> sources, IReadOnlyList<string> warnings)
    {
        var appRef = $"{SbomGenerator.ApplicationName}@{o.Version}";
        var comps = new JsonArray();
        foreach (var c in components) comps.Add(Component(c));

        var deps = new JsonArray();
        deps.Add(new JsonObject
        {
            ["ref"] = appRef,
            ["dependsOn"] = new JsonArray([.. components.Where(c => c.Shipped && c.Kind is not "container").Select(c => (JsonNode)JsonValue.Create(c.Ref)!)]),
        });
        foreach (var c in components.Where(c => c.DependsOn.Count > 0))
            deps.Add(new JsonObject { ["ref"] = c.Ref, ["dependsOn"] = new JsonArray([.. c.DependsOn.Order(StringComparer.Ordinal).Select(d => (JsonNode)JsonValue.Create(d)!)]) });

        var incomplete = warnings.Any(w => w.StartsWith("No project.assets.json", StringComparison.Ordinal));
        var doc = new JsonObject
        {
            ["bomFormat"] = "CycloneDX",
            ["specVersion"] = "1.5",
            ["serialNumber"] = "urn:uuid:" + SerialFrom(o, components),
            ["version"] = 1,
            ["metadata"] = new JsonObject
            {
                ["timestamp"] = o.Timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                ["tools"] = new JsonObject
                {
                    ["components"] = new JsonArray(new JsonObject { ["type"] = "application", ["name"] = "keel-sbom", ["version"] = "1.0.0", ["description"] = "Keel PLM offline SBOM generator (reads lock files, project files and vendored assets; no network)" }),
                },
                ["component"] = new JsonObject
                {
                    ["bom-ref"] = appRef, ["type"] = "application", ["name"] = SbomGenerator.ApplicationName, ["version"] = o.Version,
                    ["description"] = "Keel PLM: on-premises, air-gapped product lifecycle management",
                },
                ["properties"] = new JsonArray(
                    Prop("keel:scope", "Shipped .NET packages of Keel.Api, Keel.AuditCli and Keel.VaultTool; vendored viewer files; embedded fonts; pinned container images (OS packages are in sbom/image-*.cdx.json when generated)."),
                    Prop("keel:legal-review", "pending; this inventory is an engineering record, not legal advice")),
            },
            ["components"] = comps,
            ["dependencies"] = deps,
            ["compositions"] = new JsonArray(new JsonObject
            {
                ["aggregate"] = incomplete ? "incomplete" : "incomplete_third_party_only",
                ["assemblies"] = new JsonArray(appRef),
            }),
        };
        _ = sources;
        return doc.ToJsonString(Indented) + "\n";
    }

    private static JsonObject Prop(string name, string value) => new() { ["name"] = name, ["value"] = value };

    private static JsonObject Component(SbomComponent c)
    {
        var o = new JsonObject { ["bom-ref"] = c.Ref, ["type"] = c.Type, ["name"] = c.Name };
        if (c.Version is not null) o["version"] = c.Version;
        o["scope"] = c.Shipped ? "required" : "excluded";
        if (c.Supplier is { Length: > 0 }) o["supplier"] = new JsonObject { ["name"] = c.Supplier };
        if (c.Copyright is { Length: > 0 }) o["copyright"] = c.Copyright;
        if (c.Hashes.Count > 0) o["hashes"] = new JsonArray([.. c.Hashes.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => (JsonNode)new JsonObject { ["alg"] = h.Key, ["content"] = h.Value })]);
        if (c.Licenses.Count > 0) o["licenses"] = Licenses(c);
        if (c.Purl is not null) o["purl"] = c.Purl;
        var refs = new JsonArray();
        if (c.Url is { Length: > 0 } url) refs.Add(new JsonObject { ["type"] = "website", ["url"] = url });
        foreach (var r in c.References)
        {
            var e = new JsonObject { ["type"] = r.Type, ["url"] = r.Url };
            if (r.Comment is not null) e["comment"] = r.Comment;
            if (r.Sha256 is not null) e["hashes"] = new JsonArray(new JsonObject { ["alg"] = "SHA-256", ["content"] = r.Sha256 });
            refs.Add(e);
        }
        if (refs.Count > 0) o["externalReferences"] = refs;
        var props = new JsonArray();
        if (c.IsCopyleft) props.Add(Prop("keel:copyleft", "true"));
        foreach (var (k, v) in c.Properties) props.Add(Prop(k, v));
        if (props.Count > 0) o["properties"] = props;
        return o;
    }

    private static JsonArray Licenses(SbomComponent c)
    {
        var arr = new JsonArray();
        foreach (var l in c.Licenses)
        {
            if (l.StartsWith("LicenseRef-url:", StringComparison.Ordinal))
                arr.Add(new JsonObject { ["license"] = new JsonObject { ["name"] = "License given by URL only", ["url"] = l["LicenseRef-url:".Length..] } });
            else if (l.StartsWith("LicenseRef-", StringComparison.Ordinal))
                arr.Add(new JsonObject { ["license"] = new JsonObject { ["name"] = "License file in the package (see THIRD-PARTY-NOTICES.md)" } });
            else if (l.Contains(' ', StringComparison.Ordinal) || l.Contains('(', StringComparison.Ordinal))
                arr.Add(new JsonObject { ["expression"] = l });
            else
                arr.Add(new JsonObject { ["license"] = new JsonObject { ["id"] = l } });
        }
        return arr;
    }

    private static string SerialFrom(SbomOptions o, IReadOnlyList<SbomComponent> components)
    {
        var sb = new StringBuilder(o.Version).Append('|').Append(o.Timestamp.UtcTicks);
        foreach (var c in components) sb.Append('|').Append(c.Ref).Append(':').Append(string.Join(',', c.Hashes.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => h.Value)));
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        h[6] = (byte)((h[6] & 0x0F) | 0x50);
        h[8] = (byte)((h[8] & 0x3F) | 0x80);
        return new Guid(h.AsSpan(0, 16), bigEndian: true).ToString("D");
    }
}
