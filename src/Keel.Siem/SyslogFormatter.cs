using System.Globalization;
using System.Text;
using System.Text.Json;
using Keel.Audit;

namespace Keel.Siem;

/// <summary>One event ready to send: the RFC 5424 text, its NDJSON form, and where it sits in its stream (audit seq or checkpoint id).</summary>
public sealed record SiemMessage(long Position, byte[] LastHash, string Syslog, string Json, bool IsAlert);

/// <summary>
/// RFC 5424 syslog messages for audit records, with the full chain data in the STRUCTURED-DATA element so a SIEM can verify continuity downstream
/// (sequence numbers gapless, every prevHash equal to the previous event's hash). The text carries identifiers, actions, outcomes and numbers only:
/// never an object's name, description, markings or the actor's attributes. Those stay in the audit log's payload, which is not forwarded.
/// </summary>
public sealed class SyslogFormatter(SiemOptions options, string hostname, int procId)
{
    public const int SeverityError = 3;
    public const int SeverityWarning = 4;
    public const int SeverityInfo = 6;
    public const int SeverityCritical = 2;

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    public static string DefaultHostname() => Sanitize(System.Net.Dns.GetHostName(), 255, "keel");

    /// <summary>Denied and Failure outcomes are louder than normal activity: Failure = err (3), Denied = warning (4), everything else info (6).</summary>
    public static int Severity(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Failure => SeverityError,
        AuditOutcome.Denied => SeverityWarning,
        _ => SeverityInfo,
    };

    public SiemMessage Format(AuditRecord r, bool chainBroken = false)
    {
        var severity = Severity(r.Outcome);
        var obj = r.ObjectType is null ? "" : r.ObjectId is null ? r.ObjectType : $"{r.ObjectType}:{r.ObjectId}";
        var summary = $"{r.Action} {r.Outcome}" + (obj.Length > 0 ? $" object={obj}" : "") + $" actor={r.Actor}";
        var sd = new List<(string, string)>
        {
            ("seq", r.Seq.ToString(CultureInfo.InvariantCulture)),
            ("hash", Convert.ToHexStringLower(r.Hash)),
            ("prevHash", Convert.ToHexStringLower(r.PrevHash)),
            ("id", r.Id.ToString("D")),
            ("actor", r.Actor),
            ("action", r.Action),
            ("outcome", r.Outcome.ToString()),
            ("object", obj),
            ("corr", r.CorrelationId ?? ""),
        };
        if (chainBroken) sd.Add(("chain", "break"));
        var syslog = Compose(severity, r.OccurredAt, MsgId(r.Action), sd, summary);

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("@timestamp", r.OccurredAt.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            w.WriteString("stream", "audit");
            w.WriteNumber("seq", r.Seq);
            w.WriteString("id", r.Id.ToString("D"));
            w.WriteString("hash", Convert.ToHexStringLower(r.Hash));
            w.WriteString("prevHash", Convert.ToHexStringLower(r.PrevHash));
            w.WriteString("actor", r.Actor);
            w.WriteString("action", r.Action);
            w.WriteString("outcome", r.Outcome.ToString());
            if (obj.Length > 0) w.WriteString("object", obj);
            if (r.CorrelationId is not null) w.WriteString("corr", r.CorrelationId);
            w.WriteString("summary", summary);
            w.WriteNumber("severity", severity);
            w.WriteString("host", hostname);
            w.WriteString("app", options.AppName);
            if (chainBroken) w.WriteString("chain", "break");
            w.WriteEndObject();
        }
        return new SiemMessage(r.Seq, r.Hash, syslog, Encoding.UTF8.GetString(buffer.ToArray()), false);
    }

    /// <summary>A signed checkpoint was created: forward its position and hash (the signature stays in the database, the key id says which key signed).</summary>
    public SiemMessage FormatCheckpoint(long checkpointId, long seq, byte[] hash, DateTimeOffset createdAt, string keyId)
    {
        var sd = new List<(string, string)>
        {
            ("checkpoint", checkpointId.ToString(CultureInfo.InvariantCulture)),
            ("seq", seq.ToString(CultureInfo.InvariantCulture)),
            ("hash", Convert.ToHexStringLower(hash)),
            ("keyId", keyId),
        };
        var summary = $"audit.checkpoint Success seq={seq.ToString(CultureInfo.InvariantCulture)} key={keyId}";
        var syslog = Compose(SeverityInfo, createdAt, "audit.checkpoint", sd, summary);
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["@timestamp"] = createdAt.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture), ["stream"] = "checkpoint",
            ["checkpoint"] = checkpointId, ["seq"] = seq, ["hash"] = Convert.ToHexStringLower(hash), ["keyId"] = keyId, ["summary"] = summary,
            ["severity"] = SeverityInfo, ["host"] = hostname, ["app"] = options.AppName,
        });
        return new SiemMessage(checkpointId, hash, syslog, json, false);
    }

    /// <summary>
    /// The forwarder found the chain it reads broken (a gap, a changed link, a row that no longer matches its hash, or a tail that was deleted
    /// after it was forwarded). Sent in-band so the SIEM can alert on it.
    /// </summary>
    public SiemMessage FormatAlert(string kind, long position, string detail, DateTimeOffset now)
    {
        var sd = new List<(string, string)>
        {
            ("kind", kind),
            ("seq", position.ToString(CultureInfo.InvariantCulture)),
            ("detail", detail),
        };
        var summary = $"siem.chain_break Failure kind={kind} seq={position.ToString(CultureInfo.InvariantCulture)}";
        var syslog = Compose(SeverityCritical, now, "siem.chain_break", sd, summary);
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["@timestamp"] = now.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture), ["stream"] = "alert", ["alert"] = "chain_break", ["kind"] = kind,
            ["seq"] = position, ["detail"] = detail, ["summary"] = summary, ["severity"] = SeverityCritical, ["host"] = hostname, ["app"] = options.AppName,
        });
        return new SiemMessage(position, new byte[32], syslog, json, true);
    }

    /// <summary>RFC 5424: <c>&lt;PRI&gt;1 TIMESTAMP HOSTNAME APP-NAME PROCID MSGID STRUCTURED-DATA MSG</c>.</summary>
    private string Compose(int severity, DateTimeOffset timestamp, string msgId, IReadOnlyList<(string Name, string Value)> sd, string summary)
    {
        var pri = options.Facility * 8 + severity;
        var sb = new StringBuilder(256);
        sb.Append('<').Append(pri.ToString(CultureInfo.InvariantCulture)).Append(">1 ")
          .Append(timestamp.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture)).Append(' ')
          .Append(hostname).Append(' ')
          .Append(options.AppName).Append(' ')
          .Append(procId.ToString(CultureInfo.InvariantCulture)).Append(' ')
          .Append(msgId).Append(' ');
        sb.Append('[').Append(options.StructuredDataId);
        foreach (var (name, value) in sd) sb.Append(' ').Append(name).Append("=\"").Append(EscapeParamValue(value)).Append('"');
        sb.Append("] ").Append(Clean(summary));
        return sb.ToString();
    }

    /// <summary>RFC 5424 section 6.3.3: inside a PARAM-VALUE the characters '"', '\' and ']' must be escaped with a backslash. Control characters are replaced.</summary>
    public static string EscapeParamValue(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '"' or '\\' or ']') sb.Append('\\').Append(c);
            else if (char.IsControl(c)) sb.Append('?');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>MSGID: at most 32 printable ASCII characters without spaces (RFC 5424 section 6.2.7).</summary>
    public static string MsgId(string action) => Sanitize(action, 32, "audit");

    private static string Clean(string text) => string.Create(text.Length, text, (span, t) =>
    {
        for (var i = 0; i < t.Length; i++) span[i] = char.IsControl(t[i]) ? '?' : t[i];
    });

    private static string Sanitize(string value, int max, string fallback)
    {
        var chars = value.Where(c => c is > ' ' and <= '~').Take(max).ToArray();
        return chars.Length == 0 ? fallback : new string(chars);
    }

    /// <summary>RFC 5425 section 4.3: <c>SYSLOG-FRAME = MSG-LEN SP SYSLOG-MSG</c>, MSG-LEN the length of the message in octets (decimal).</summary>
    public static byte[] Frame(string syslog)
    {
        var body = Encoding.UTF8.GetBytes(syslog);
        var prefix = Encoding.ASCII.GetBytes(body.Length.ToString(CultureInfo.InvariantCulture) + " ");
        var frame = new byte[prefix.Length + body.Length];
        prefix.CopyTo(frame, 0);
        body.CopyTo(frame, prefix.Length);
        return frame;
    }
}
