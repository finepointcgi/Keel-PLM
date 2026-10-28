using System.Text.RegularExpressions;

namespace Keel.Siem;

public enum SiemDestinationType
{
    /// <summary>RFC 5424 messages over TLS with RFC 5425 octet-counting framing.</summary>
    SyslogTls,
    /// <summary>Newline-delimited JSON appended to a file that a forwarder (Splunk UF, filebeat, rsyslog imfile) ships.</summary>
    File,
}

public enum SyslogTransport
{
    /// <summary>TLS 1.2 or 1.3 (RFC 5425). The only transport production accepts.</summary>
    Tls,
    /// <summary>Plain TCP (RFC 6587). Only with <see cref="SiemDestinationOptions.AllowInsecureTcp"/>, never in production.</summary>
    Tcp,
    /// <summary>Not supported: see <see cref="SiemOptions.UdpNotSupportedMessage"/>.</summary>
    Udp,
}

/// <summary>Configuration section <c>Keel:Siem</c> (ADR-0026). Forwarding is off by default.</summary>
public sealed class SiemOptions
{
    public const string Section = "Keel:Siem";

    /// <summary>IANA documentation private enterprise number (RFC 5612): a placeholder. Replace it with the organization's own PEN when it has one.</summary>
    public const int PlaceholderEnterpriseNumber = 32473;

    public const string UdpNotSupportedMessage =
        "UDP syslog is not supported: it has no delivery guarantee (a lost datagram is a silent hole in the audit trail), no ordering, no authentication and no " +
        "encryption, so it cannot carry a tamper-evident, export-controlled audit log. Use syslog over TLS (RFC 5425) or the file sink.";

    public bool Enabled { get; set; }
    /// <summary>HOSTNAME in the syslog header. Empty = the machine name.</summary>
    public string Hostname { get; set; } = "";
    public string AppName { get; set; } = "keel-plm";
    /// <summary>Private enterprise number in the structured-data ID (<c>keel@PEN</c>, RFC 5424 section 7.2.2). The default is the documentation placeholder 32473.</summary>
    public int EnterpriseNumber { get; set; } = PlaceholderEnterpriseNumber;
    /// <summary>Syslog facility number (RFC 5424 table 1). 13 = log audit.</summary>
    public int Facility { get; set; } = 13;
    /// <summary>Events read and sent per batch.</summary>
    public int BatchSize { get; set; } = 200;
    /// <summary>Batches the reader may hold ahead of the sender (the bounded in-memory queue). A slow destination stops the reader; the database is the backlog.</summary>
    public int QueueBatches { get; set; } = 4;
    public int PollIntervalMilliseconds { get; set; } = 1000;
    public List<SiemDestinationOptions> Destinations { get; set; } = [];

    public string StructuredDataId => $"keel@{EnterpriseNumber}";

    /// <summary>Validates the section. Throws <see cref="NotSupportedException"/> for UDP; other problems are returned as messages.</summary>
    public IReadOnlyList<string> Validate(bool requireFips)
    {
        var errors = new List<string>();
        if (!Enabled) return errors;
        if (Destinations.Count == 0) errors.Add("Keel:Siem:Enabled is true but Keel:Siem:Destinations is empty.");
        if (Facility is < 0 or > 23) errors.Add("Keel:Siem:Facility must be between 0 and 23.");
        if (EnterpriseNumber < 1) errors.Add("Keel:Siem:EnterpriseNumber must be positive.");
        if (BatchSize is < 1 or > 5000) errors.Add("Keel:Siem:BatchSize must be between 1 and 5000.");
        if (QueueBatches is < 1 or > 100) errors.Add("Keel:Siem:QueueBatches must be between 1 and 100.");
        if (PollIntervalMilliseconds is < 10 or > 60_000) errors.Add("Keel:Siem:PollIntervalMilliseconds must be between 10 and 60000.");
        if (!Printable(AppName, 48)) errors.Add("Keel:Siem:AppName must be 1-48 printable ASCII characters without spaces.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Destinations.Count; i++) errors.AddRange(Destinations[i].Validate($"Keel:Siem:Destinations:{i}", names, requireFips));
        return errors;
    }

    internal static bool Printable(string value, int max) =>
        value.Length is > 0 && value.Length <= max && value.All(c => c is > ' ' and <= '~');
}

public sealed partial class SiemDestinationOptions
{
    public string Name { get; set; } = "";
    public SiemDestinationType Type { get; set; } = SiemDestinationType.SyslogTls;

    // ---- syslog
    public string Host { get; set; } = "";
    public int Port { get; set; } = 6514;
    public SyslogTransport Transport { get; set; } = SyslogTransport.Tls;
    /// <summary>Name the server certificate must carry. Empty = <see cref="Host"/>.</summary>
    public string ServerName { get; set; } = "";
    /// <summary>PEM file with the CA certificate(s) that sign the SIEM's server certificate. Empty = the operating system's trust store. Relative paths resolve against KEEL_SECRETS_DIR.</summary>
    public string CaBundlePath { get; set; } = "";
    /// <summary>mTLS: PEM client certificate and its private key (relative paths resolve against KEEL_SECRETS_DIR). Both or neither.</summary>
    public string ClientCertificatePath { get; set; } = "";
    public string ClientKeyPath { get; set; } = "";
    /// <summary>Password of an encrypted client key. Secret: supply it as a file in KEEL_SECRETS_DIR (<c>Keel__Siem__Destinations__0__ClientKeyPassword</c>).</summary>
    public string ClientKeyPassword { get; set; } = "";
    /// <summary>Plain TCP syslog for a development collector. Refused when Keel:Security:RequireFips is true.</summary>
    public bool AllowInsecureTcp { get; set; }
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int SendTimeoutSeconds { get; set; } = 30;

    // ---- file
    /// <summary>NDJSON file. The directory is created; each batch is one append, and the file is reopened per batch so external rotation (logrotate) is safe.</summary>
    public string FilePath { get; set; } = "";

    // ---- delivery
    public int BackoffMinSeconds { get; set; } = 1;
    public int BackoffMaxSeconds { get; set; } = 60;

    internal IReadOnlyList<string> Validate(string path, HashSet<string> names, bool requireFips)
    {
        var errors = new List<string>();
        if (!NamePattern().IsMatch(Name)) errors.Add($"{path}:Name must be 1-64 characters of a-z, 0-9, '-' and '_'.");
        else if (!names.Add(Name)) errors.Add($"{path}:Name '{Name}' is used twice.");
        if (BackoffMinSeconds < 1 || BackoffMaxSeconds < BackoffMinSeconds || BackoffMaxSeconds > 3600) errors.Add($"{path}: BackoffMinSeconds must be at least 1 and not above BackoffMaxSeconds (at most 3600).");
        if (Type == SiemDestinationType.File)
        {
            if (string.IsNullOrWhiteSpace(FilePath)) errors.Add($"{path}:FilePath is required for a file destination.");
            return errors;
        }
        if (Transport == SyslogTransport.Udp) throw new NotSupportedException($"{path}:Transport: {SiemOptions.UdpNotSupportedMessage}");
        if (string.IsNullOrWhiteSpace(Host)) errors.Add($"{path}:Host is required for a syslog destination.");
        if (Port is < 1 or > 65535) errors.Add($"{path}:Port must be between 1 and 65535.");
        if (ConnectTimeoutSeconds is < 1 or > 120) errors.Add($"{path}:ConnectTimeoutSeconds must be between 1 and 120.");
        if (SendTimeoutSeconds is < 1 or > 600) errors.Add($"{path}:SendTimeoutSeconds must be between 1 and 600.");
        if (Transport == SyslogTransport.Tcp)
        {
            if (!AllowInsecureTcp) errors.Add($"{path}: plain TCP syslog sends audit records unencrypted. Use Transport=Tls, or set AllowInsecureTcp=true for a development collector.");
            else if (requireFips) errors.Add($"{path}:AllowInsecureTcp is refused while Keel:Security:RequireFips is true: production forwards over TLS only.");
        }
        else if (AllowInsecureTcp) errors.Add($"{path}:AllowInsecureTcp only applies to Transport=Tcp.");
        if (string.IsNullOrEmpty(ClientCertificatePath) != string.IsNullOrEmpty(ClientKeyPath)) errors.Add($"{path}: ClientCertificatePath and ClientKeyPath go together (mTLS).");
        if (Transport == SyslogTransport.Tcp && !string.IsNullOrEmpty(ClientCertificatePath)) errors.Add($"{path}: a client certificate needs Transport=Tls.");
        return errors;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex NamePattern();
}
