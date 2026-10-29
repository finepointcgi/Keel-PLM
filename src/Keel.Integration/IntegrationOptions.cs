namespace Keel.Integration;

/// <summary>
/// Configuration section <c>Keel:Integration</c> (ADR-0025). Nothing here is stored in the database: sink rows reference secrets, key
/// material and CA bundles by name, and this section (filled from environment, mounted files or the secrets directory) resolves the names.
/// </summary>
public sealed class IntegrationOptions
{
    public const string Section = "Keel:Integration";

    /// <summary>Runs the dispatcher in this process. Off by default so a replica set can dedicate one instance (or none, with the file sink polled elsewhere).</summary>
    public bool DispatcherEnabled { get; set; }
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Deliveries claimed per poll.</summary>
    public int BatchSize { get; set; } = 25;
    /// <summary>Attempts before a delivery becomes a dead letter.</summary>
    public int MaxAttempts { get; set; } = 8;
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan BackoffMax { get; set; } = TimeSpan.FromHours(1);
    /// <summary>A claimed delivery is retried by another dispatcher when its lease expires without a result.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Readiness reports Degraded when the oldest due delivery has waited longer than this.</summary>
    public int LagDegradedSeconds { get; set; } = 300;
    public int WebhookTimeoutSeconds { get; set; } = 10;

    /// <summary>Host names a sink URL may use: exact names or "*.suffix". Default deny: an empty list and no networks refuse every webhook.</summary>
    public string[] AllowedHosts { get; set; } = [];
    /// <summary>CIDR ranges a sink URL may resolve into, for example 10.20.0.0/16.</summary>
    public string[] AllowedNetworks { get; set; } = [];
    /// <summary>Development only: also allow loopback, link-local and cloud-metadata addresses (otherwise they are always refused).</summary>
    public bool AllowDangerousAddresses { get; set; }

    /// <summary>Directory under which file-drop sinks write. Mount a volume here; file-drop sinks are refused while it is empty.</summary>
    public string FileDropRoot { get; set; } = "";
    /// <summary>Default for a sink's <c>maxPendingFiles</c>: committed files waiting unconsumed in its directory before delivery pauses. 0 = unlimited.</summary>
    public int FileDropMaxPendingFiles { get; set; } = 10_000;

    /// <summary>Secret values by name (HMAC secrets). Provide as <c>Keel__Integration__Secrets__{name}</c> in the secrets directory or environment.</summary>
    public Dictionary<string, string> Secrets { get; set; } = new(StringComparer.Ordinal);
    /// <summary>PEM files holding a client certificate and its private key, by name (mutual TLS).</summary>
    public Dictionary<string, string> ClientCertificates { get; set; } = new(StringComparer.Ordinal);
    /// <summary>PEM bundles of the CA certificates a webhook endpoint's certificate must chain to, by name.</summary>
    public Dictionary<string, string> CaBundles { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Upper bound of lines of a one-level BOM event (the BOM module caps one parent at 5,000 children).</summary>
    public int BomPublishedMaxLines { get; set; } = 5000;
    /// <summary>Largest full structure emitted as <c>bom.structure.published</c>; a bigger structure is skipped and audited, never truncated.</summary>
    public int BomFullStructureMaxLines { get; set; } = 5000;

    /// <summary>PEM file with the ECDSA P-256 private key that signs TDP manifests. TDP export is refused while it is empty.</summary>
    public string TdpSigningKeyPath { get; set; } = "";
    /// <summary>PEM public keys trusted when verifying TDPs (current and retired signing keys).</summary>
    public string[] TdpTrustedPublicKeyPaths { get; set; } = [];
    /// <summary>Most parts and most documents one TDP may contain; a bigger package is refused with counts so it can be split.</summary>
    public int TdpMaxParts { get; set; } = 2000;
    public int TdpMaxDocuments { get; set; } = 5000;

    /// <summary>Retention of delivered outbox events (<c>Keel:Integration:Retention</c>); off by default.</summary>
    public OutboxRetentionOptions Retention { get; set; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        errors.AddRange(Retention.Validate());
        if (FileDropMaxPendingFiles is < 0 or > 1_000_000) errors.Add("Keel:Integration:FileDropMaxPendingFiles must be between 0 (unlimited) and 1000000.");
        if (BatchSize is < 1 or > 500) errors.Add("Keel:Integration:BatchSize must be between 1 and 500.");
        if (MaxAttempts is < 1 or > 100) errors.Add("Keel:Integration:MaxAttempts must be between 1 and 100.");
        if (BackoffBase <= TimeSpan.Zero || BackoffMax < BackoffBase) errors.Add("Keel:Integration:BackoffBase must be positive and not above BackoffMax.");
        if (PollInterval < TimeSpan.FromMilliseconds(50)) errors.Add("Keel:Integration:PollInterval must be at least 50 ms.");
        if (Lease < TimeSpan.FromSeconds(5)) errors.Add("Keel:Integration:Lease must be at least 5 seconds.");
        foreach (var n in AllowedNetworks)
            if (!System.Net.IPNetwork.TryParse(n, out _)) errors.Add($"Keel:Integration:AllowedNetworks entry '{n}' is not a CIDR range.");
        return errors;
    }
}
