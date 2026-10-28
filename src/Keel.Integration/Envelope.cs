using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Core;

namespace Keel.Integration;

/// <summary>
/// The signed envelope every sink receives and the pull API returns (docs/integration/erp-contract.md). The body bytes of one event are
/// identical for every attempt and every sink; only the transport headers (timestamp, signature, attempt) change.
/// </summary>
public static class Envelope
{
    public const string Source = "keel-plm";

    /// <summary>Marking summary written into the envelope so a receiver can apply its own handling rules; the banner is the human-readable form.</summary>
    public static JsonObject MarkingsSummary(Markings m) => new()
    {
        ["banner"] = m.BannerText(),
        ["exportJurisdiction"] = m.ExportJurisdiction.ToString(),
        ["eccn"] = m.Eccn,
        ["usmlCategory"] = m.UsmlCategory,
        ["cuiCategories"] = new JsonArray([.. m.CuiCategories.Select(c => (JsonNode?)c)]),
        ["distributionStatement"] = m.DistributionStatement.ToString(),
        ["controllingOffice"] = m.ControllingOffice,
        ["program"] = m.Program,
        ["owningOrg"] = m.OwningOrg,
        ["exportAuthorizations"] = new JsonArray([.. m.ExportAuthorizations.Select(c => (JsonNode?)c)]),
    };

    public static JsonObject ToJson(OutboxRow row) => new()
    {
        ["id"] = row.Id.ToString("D"),
        ["type"] = row.EventType,
        ["version"] = row.EventVersion,
        ["occurredAt"] = row.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
        ["source"] = Source,
        ["aggregate"] = new JsonObject { ["type"] = row.AggregateType, ["number"] = row.AggregateNumber, ["revision"] = row.AggregateRevision },
        ["correlationId"] = row.CorrelationId,
        ["actor"] = row.Actor,
        ["markings"] = MarkingsSummary(row.Markings),
        ["data"] = JsonNode.Parse(row.PayloadJson),
    };

    public static byte[] Body(OutboxRow row) => Encoding.UTF8.GetBytes(ToJson(row).ToJsonString());
}

/// <summary>
/// HMAC-SHA-256 webhook signatures: <c>X-Keel-Signature: v1=&lt;hex&gt;[,v1=&lt;hex&gt;]</c>, each over <c>{timestamp}.{body}</c>. While a secret
/// is being rotated the header carries one value per active secret and a receiver accepts any match (ADR-0025). HMAC-SHA-256 is FIPS-approved.
/// </summary>
public static class WebhookSignature
{
    public const string Scheme = "v1";

    public static string Sign(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.ASCII.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));
    }

    public static string Header(IEnumerable<string> secrets, long timestamp, ReadOnlySpan<byte> body)
    {
        var parts = new List<string>();
        foreach (var s in secrets) parts.Add($"{Scheme}={Sign(s, timestamp, body)}");
        return string.Join(',', parts);
    }

    /// <summary>True when any signature in <paramref name="header"/> matches one of <paramref name="secrets"/> and the timestamp is within <paramref name="tolerance"/> of <paramref name="now"/>.</summary>
    public static bool Verify(IEnumerable<string> secrets, string? header, long timestamp, ReadOnlySpan<byte> body, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        if (Math.Abs((now - DateTimeOffset.FromUnixTimeSeconds(timestamp)).TotalSeconds) > tolerance.TotalSeconds) return false;
        var candidates = header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.StartsWith(Scheme + "=", StringComparison.Ordinal)).Select(p => p[(Scheme.Length + 1)..]).ToList();
        var ok = false;
        foreach (var secret in secrets)
        {
            var expected = Encoding.ASCII.GetBytes(Sign(secret, timestamp, body));
            foreach (var c in candidates)
                ok |= CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(c)); // no early exit: constant work per candidate
        }
        return ok;
    }
}
