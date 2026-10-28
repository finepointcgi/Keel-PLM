using System.Net;

namespace Keel.Documents.Scanning;

public enum ScanMode
{
    /// <summary>No scanning. Refused at startup when <c>Keel:Security:RequireFips</c> is true unless <see cref="ScanningOptions.AcknowledgeUnscannedUploads"/> is set.</summary>
    Disabled,
    /// <summary>Every upload is scanned; when the scanner cannot give a verdict the upload is refused (503 with Retry-After).</summary>
    Required,
    /// <summary>Every upload is scanned when possible; when the scanner cannot give a verdict the upload is accepted and recorded as <c>unscanned</c>.</summary>
    BestEffort,
}

/// <summary>Configuration section <c>Keel:Vault:Scanning</c> (ADR-0026). The scanner is a clamd daemon reached over its INSTREAM protocol.</summary>
public sealed class ScanningOptions
{
    public const string Section = "Keel:Vault:Scanning";
    public const long DefaultMaxScanBytes = 2L * 1024 * 1024 * 1024;

    public ScanMode Mode { get; set; } = ScanMode.Disabled;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3310;
    /// <summary>Path of the clamd UNIX socket; when set it is used instead of Host/Port. Never leaves the host, so no network exposure.</summary>
    public string UnixSocket { get; set; } = "";
    public int ConnectTimeoutSeconds { get; set; } = 5;
    /// <summary>Bound for each read or write on the scanner connection.</summary>
    public int IoTimeoutSeconds { get; set; } = 30;
    /// <summary>Bound for the verdict after the last byte was sent (clamd finishes unpacking archives then).</summary>
    public int ResultTimeoutSeconds { get; set; } = 120;
    /// <summary>
    /// Largest stream sent to the scanner. Must not exceed clamd's <c>StreamMaxLength</c> (its default is 25 MB: raise it in clamd.conf).
    /// A larger upload cannot be scanned: refused in Required mode, accepted as <c>skipped</c> in BestEffort mode.
    /// </summary>
    public long MaxScanBytes { get; set; } = DefaultMaxScanBytes;
    /// <summary>Size of the chunks sent to clamd (each is prefixed with its 4-byte big-endian length).</summary>
    public int ChunkBytes { get; set; } = 256 * 1024;
    /// <summary>The INSTREAM protocol is not encrypted. A scanner on another host than the application needs this explicit acknowledgement that the link is protected by other means.</summary>
    public bool AllowRemotePlaintext { get; set; }
    /// <summary>Production (RequireFips) refuses <see cref="ScanMode.Disabled"/> unless this is true: the site accepts that uploads are not scanned.</summary>
    public bool AcknowledgeUnscannedUploads { get; set; }

    public TimeSpan ConnectTimeout => TimeSpan.FromSeconds(ConnectTimeoutSeconds);
    public TimeSpan IoTimeout => TimeSpan.FromSeconds(IoTimeoutSeconds);
    public TimeSpan ResultTimeout => TimeSpan.FromSeconds(ResultTimeoutSeconds);

    public IReadOnlyList<string> Validate(bool requireFips)
    {
        var errors = new List<string>();
        if (Mode == ScanMode.Disabled)
        {
            if (requireFips && !AcknowledgeUnscannedUploads)
                errors.Add("Keel:Vault:Scanning:Mode is Disabled while Keel:Security:RequireFips is true: set Mode to Required (or BestEffort), or set " +
                           "Keel:Vault:Scanning:AcknowledgeUnscannedUploads=true to run production without malware scanning on purpose.");
            return errors;
        }
        if (string.IsNullOrWhiteSpace(UnixSocket))
        {
            if (string.IsNullOrWhiteSpace(Host)) errors.Add("Keel:Vault:Scanning:Host is required (or set UnixSocket).");
            else if (Port is < 1 or > 65535) errors.Add("Keel:Vault:Scanning:Port must be between 1 and 65535.");
            else if (!IsLoopback(Host) && !AllowRemotePlaintext)
                errors.Add($"Keel:Vault:Scanning:Host '{Host}' is not a loopback address: the clamd INSTREAM protocol is not encrypted. Run clamd as a sidecar on localhost, " +
                           "use a UNIX socket, or set Keel:Vault:Scanning:AllowRemotePlaintext=true after protecting the link.");
        }
        if (ConnectTimeoutSeconds is < 1 or > 60) errors.Add("Keel:Vault:Scanning:ConnectTimeoutSeconds must be between 1 and 60.");
        if (IoTimeoutSeconds is < 1 or > 600) errors.Add("Keel:Vault:Scanning:IoTimeoutSeconds must be between 1 and 600.");
        if (ResultTimeoutSeconds is < 1 or > 3600) errors.Add("Keel:Vault:Scanning:ResultTimeoutSeconds must be between 1 and 3600.");
        if (MaxScanBytes is < 1 or > uint.MaxValue) errors.Add($"Keel:Vault:Scanning:MaxScanBytes must be between 1 and {uint.MaxValue} (clamd StreamMaxLength is at most 4 GB).");
        if (ChunkBytes is < 1024 or > 16 * 1024 * 1024) errors.Add("Keel:Vault:Scanning:ChunkBytes must be between 1 KiB and 16 MiB.");
        return errors;
    }

    internal static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
}
