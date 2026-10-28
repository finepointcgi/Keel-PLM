using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Keel.Audit;

/// <summary>Canonical payload format and hash-chain arithmetic of ADR-0005. Changing either breaks verification of existing logs.</summary>
public static class AuditChain
{
    public const int PayloadVersion = 1;
    public static readonly byte[] GenesisHash = new byte[32];

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    /// <summary>PostgreSQL stores microseconds; truncating up front keeps the payload and the column identical.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset t)
    {
        var utc = t.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    public static string FormatTimestamp(DateTimeOffset t) => Truncate(t).UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>Serializes the event with a fixed property order, no whitespace, ordinal-sorted collections.</summary>
    public static string CanonicalPayload(Guid id, DateTimeOffset occurredAt, AuditEntry e)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", PayloadVersion);
            w.WriteString("id", id.ToString("D"));
            w.WriteString("occurredAt", FormatTimestamp(occurredAt));
            w.WriteString("action", e.Action);
            w.WriteString("outcome", e.Outcome.ToString());
            WriteNullable(w, "objectType", e.ObjectType);
            WriteNullable(w, "objectId", e.ObjectId);
            WriteNullable(w, "correlationId", e.CorrelationId);
            w.WriteStartObject("actor");
            w.WriteString("id", e.Actor.Id);
            w.WriteBoolean("usPerson", e.Actor.UsPerson);
            WriteNullable(w, "citizenship", e.Actor.Citizenship);
            WriteNullable(w, "org", e.Actor.Org);
            WriteSet(w, "programs", e.Actor.Programs);
            WriteSet(w, "exportAuthorizations", e.Actor.ExportAuthorizations);
            WriteSet(w, "roles", e.Actor.Roles);
            w.WriteEndObject();
            w.WriteStartObject("details");
            foreach (var (key, value) in e.Details.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                WriteNullable(w, key, value);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>hash = SHA-256(prev_hash ‖ seq as 8-byte big-endian ‖ UTF-8 payload).</summary>
    public static byte[] ComputeHash(ReadOnlySpan<byte> prevHash, long seq, string payload)
    {
        if (prevHash.Length != 32) throw new ArgumentException("Previous hash must be 32 bytes.", nameof(prevHash));
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var input = new byte[32 + 8 + payloadBytes.Length];
        prevHash.CopyTo(input);
        BinaryPrimitives.WriteInt64BigEndian(input.AsSpan(32, 8), seq);
        payloadBytes.CopyTo(input, 40);
        return SHA256.HashData(input);
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    private static void WriteSet(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values.Order(StringComparer.Ordinal)) w.WriteStringValue(v);
        w.WriteEndArray();
    }
}
