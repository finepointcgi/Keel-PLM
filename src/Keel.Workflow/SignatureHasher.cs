using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Keel.Core;

namespace Keel.Workflow;

/// <summary>
/// Canonical JSON: object keys sorted by ordinal comparison, UTF-8, no insignificant whitespace, numbers kept as written.
/// The same document always serializes to the same bytes, so a hash over it can be recomputed years later (ADR-0005 style).
/// </summary>
public static class CanonicalJson
{
    public static string Serialize(JsonNode? node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            Write(writer, node);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter w, JsonNode? node)
    {
        switch (node)
        {
            case null:
                w.WriteNullValue();
                break;
            case JsonObject o:
                w.WriteStartObject();
                foreach (var (key, value) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    w.WritePropertyName(key);
                    Write(w, value);
                }
                w.WriteEndObject();
                break;
            case JsonArray a:
                w.WriteStartArray();
                foreach (var item in a) Write(w, item);
                w.WriteEndArray();
                break;
            default:
                node.WriteTo(w);
                break;
        }
    }
}

/// <summary>What a signature covers, taken from the stored iteration and the markings at signing time.</summary>
public sealed record SignedContent(
    string ObjectType, string ObjectKey, string Revision, Guid IterationId, string IterationLabel,
    string Description, string AttributesJson, Markings Markings);

/// <summary>The signed statement: content plus meaning, signer and time.</summary>
public sealed record SignedStatement(
    SignedContent Content, string MeaningCode, string Decision, string SignerSub, string? Comment, DateTimeOffset SignedAt);

/// <summary>
/// Signature hashing (A9, ADR-0007), SHA-256 only (ADR-0008). Two levels: the iteration content hash (description,
/// attributes, markings; the BOM and file links join it in increments 1.3 and 1.4) and the signature hash over the
/// canonical statement (object identity, iteration id, content hash, meaning, signer, decision, comment, time).
/// </summary>
public static class SignatureHasher
{
    /// <summary>Timestamps are stored with microsecond precision; hash exactly what the database will return.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset t) => new(t.UtcTicks - t.UtcTicks % 10, TimeSpan.Zero);

    public static string Timestamp(DateTimeOffset t) => Truncate(t).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    public static byte[] ContentHash(SignedContent c) => Sha256(CanonicalJson.Serialize(new JsonObject
    {
        ["description"] = c.Description,
        ["attributes"] = JsonNode.Parse(c.AttributesJson),
        ["markings"] = MarkingsJson(c.Markings),
    }));

    public static byte[] SignatureHash(SignedStatement s) => Sha256(CanonicalJson.Serialize(new JsonObject
    {
        ["object"] = new JsonObject
        {
            ["type"] = s.Content.ObjectType,
            ["key"] = s.Content.ObjectKey,
            ["revision"] = s.Content.Revision,
            ["iterationId"] = s.Content.IterationId.ToString("D"),
            ["iterationLabel"] = s.Content.IterationLabel,
        },
        ["contentHash"] = Convert.ToHexStringLower(ContentHash(s.Content)),
        ["meaning"] = s.MeaningCode,
        ["decision"] = s.Decision,
        ["signer"] = s.SignerSub,
        ["comment"] = s.Comment,
        ["signedAt"] = Timestamp(s.SignedAt),
    }));

    public static JsonObject MarkingsJson(Markings m) => new()
    {
        ["jurisdiction"] = m.ExportJurisdiction.ToString(),
        ["eccn"] = m.Eccn,
        ["usmlCategory"] = m.UsmlCategory,
        ["cui"] = new JsonArray([.. m.CuiCategories.Select(c => (JsonNode?)JsonValue.Create(c))]),
        ["distribution"] = m.DistributionStatement.ToString(),
        ["controllingOffice"] = m.ControllingOffice,
        ["distributionReason"] = m.DistributionReason,
        ["program"] = m.Program,
        ["owningOrg"] = m.OwningOrg,
        ["authorizations"] = new JsonArray([.. m.ExportAuthorizations.Select(c => (JsonNode?)JsonValue.Create(c))]),
    };

    public static Markings ParseMarkings(JsonNode node)
    {
        var o = node.AsObject();
        string? S(string k) => o[k]?.GetValue<string>();
        IEnumerable<string> L(string k) => o[k]!.AsArray().Select(x => x!.GetValue<string>());
        var result = Markings.Create(Enum.Parse<ExportJurisdiction>(S("jurisdiction")!), Enum.Parse<DistributionStatement>(S("distribution")!), S("owningOrg")!,
            S("eccn"), S("usmlCategory"), L("cui"), S("controllingOffice"), S("distributionReason"), S("program"), L("authorizations"));
        return result.IsSuccess ? result.Value : throw new InvalidOperationException("Stored signature markings are invalid: " + string.Join("; ", result.Errors));
    }

    /// <summary>"Signed by Alice Example (alice) on 2026-10-27 14:05:09 UTC — Approved" (A9 signature manifestation).</summary>
    public static string Manifestation(string displayName, string username, DateTimeOffset signedAt, string meaning) =>
        $"Signed by {displayName} ({username}) on {signedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC — {meaning}";

    private static byte[] Sha256(string canonical) => SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
}
