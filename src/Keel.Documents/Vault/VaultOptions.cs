using Keel.Documents.Scanning;

namespace Keel.Documents.Vault;

/// <summary>Configuration section <c>Keel:Vault</c> (ADR-0015). The store is reached through the S3 API only (A13).</summary>
public sealed class VaultOptions
{
    public const string Section = "Keel:Vault";
    public const long DefaultMaxUploadBytes = 2L * 1024 * 1024 * 1024;
    /// <summary>Server-side CopyObject is limited to 5 GiB; larger objects would need multipart copy.</summary>
    public const long MaxSupportedUploadBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>S3 endpoint, e.g. https://vault.site.example:8333. Path-style addressing is always used.</summary>
    public string ServiceUrl { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Bucket { get; set; } = "keel-vault";
    /// <summary>Signing region. On-prem stores accept any value; it is never used to locate an AWS endpoint.</summary>
    public string Region { get; set; } = "us-east-1";
    /// <summary>Request server-side encryption (SSE-S3, "AES256") on every object. Production sites set this (ADR-0008).</summary>
    public bool ServerSideEncryption { get; set; }
    public long MaxUploadBytes { get; set; } = DefaultMaxUploadBytes;
    /// <summary>Uploads are streamed in parts of this size; at most one part is held in memory per upload.</summary>
    public int PartSizeBytes { get; set; } = 16 * 1024 * 1024;
    /// <summary>PDFs up to this size are stamped in memory; larger ones are delivered in a marked ZIP instead.</summary>
    public long MaxPdfStampBytes { get; set; } = 256L * 1024 * 1024;
    /// <summary>
    /// Upper bound for one vault request (and for each read of a streaming download). A store that does not answer within
    /// this time makes the API answer 503 instead of hanging the request.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Malware scanning of uploads (<c>Keel:Vault:Scanning</c>, ADR-0026).</summary>
    public ScanningOptions Scanning { get; set; } = new();
    /// <summary>Garbage collection of orphaned objects (<c>Keel:Vault:Gc</c>, ADR-0026).</summary>
    public GcOptions Gc { get; set; } = new();

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(RequestTimeoutSeconds);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            errors.Add("Keel:Vault:ServiceUrl must be an absolute http(s) URL.");
        if (string.IsNullOrWhiteSpace(AccessKey) || string.IsNullOrWhiteSpace(SecretKey)) errors.Add("Keel:Vault:AccessKey and SecretKey are required.");
        if (string.IsNullOrWhiteSpace(Bucket)) errors.Add("Keel:Vault:Bucket is required.");
        if (MaxUploadBytes < 1 || MaxUploadBytes > MaxSupportedUploadBytes) errors.Add($"Keel:Vault:MaxUploadBytes must be between 1 and {MaxSupportedUploadBytes}.");
        // S3 requires every part but the last to be at least 5 MiB.
        if (PartSizeBytes < 5 * 1024 * 1024 || PartSizeBytes > 512 * 1024 * 1024) errors.Add("Keel:Vault:PartSizeBytes must be between 5 MiB and 512 MiB.");
        if (RequestTimeoutSeconds is < 1 or > 600) errors.Add("Keel:Vault:RequestTimeoutSeconds must be between 1 and 600.");
        if (MaxPdfStampBytes < 0) errors.Add("Keel:Vault:MaxPdfStampBytes must not be negative.");
        errors.AddRange(Gc.Validate());
        return errors;
    }
}

public enum GcMode
{
    /// <summary>Reports what would be deleted; deletes nothing.</summary>
    DryRun,
    /// <summary>Deletes unreferenced objects older than the grace period (at most <see cref="GcOptions.MaxDeletionsPerRun"/> per run).</summary>
    Delete,
}

/// <summary>Configuration section <c>Keel:Vault:Gc</c> (ADR-0026). The scheduled sweeper is off by default.</summary>
public sealed class GcOptions
{
    public const int MinGraceDays = 1;

    /// <summary>Run the sweeper as a hosted service. Off by default: the CLI (<c>keel-vault gc</c>) runs it on demand.</summary>
    public bool Enabled { get; set; }
    /// <summary>What the hosted service does. DryRun (default) only reports; Delete needs a deliberate setting.</summary>
    public GcMode Mode { get; set; } = GcMode.DryRun;
    public int IntervalHours { get; set; } = 24;
    /// <summary>An unreferenced object younger than this is kept. At least one day: it covers every upload in flight and every failed upload still being investigated.</summary>
    public int GraceDays { get; set; } = 7;
    /// <summary>Upper bound of deletions in one run; the run reports a continuation token when more candidates remain.</summary>
    public int MaxDeletionsPerRun { get; set; } = 1000;
    /// <summary>Objects listed per request.</summary>
    public int PageSize { get; set; } = 500;

    public TimeSpan Grace => TimeSpan.FromDays(GraceDays);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (GraceDays < MinGraceDays) errors.Add($"Keel:Vault:Gc:GraceDays must be at least {MinGraceDays}.");
        if (IntervalHours is < 1 or > 24 * 31) errors.Add("Keel:Vault:Gc:IntervalHours must be between 1 and 744.");
        if (MaxDeletionsPerRun is < 1 or > 1_000_000) errors.Add("Keel:Vault:Gc:MaxDeletionsPerRun must be between 1 and 1000000.");
        if (PageSize is < 10 or > 1000) errors.Add("Keel:Vault:Gc:PageSize must be between 10 and 1000.");
        return errors;
    }
}
