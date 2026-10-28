namespace Keel.Api;

public sealed class KeelOptions
{
    public const string Section = "Keel";

    public DatabaseOptions Database { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
    public AuditOptions Audit { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();

    public sealed class DatabaseOptions
    {
        /// <summary>Connection as the least-privilege application role (keel_app).</summary>
        public string AppConnectionString { get; set; } = "";
        /// <summary>Connection as the schema owner (keel_migrator). Only used by --migrate.</summary>
        public string MigratorConnectionString { get; set; } = "";
        public string AppRole { get; set; } = "keel_app";
    }

    public sealed class AuthOptions
    {
        /// <summary>OIDC issuer, e.g. https://idp.example.mil/realms/keel. Discovery and JWKS are fetched from it (on-prem).</summary>
        public string Authority { get; set; } = "";
        public string Audience { get; set; } = "keel-api";
        public bool RequireHttpsMetadata { get; set; } = true;
        /// <summary>Confidential OIDC client used by the Blazor UI (ADR-0014).</summary>
        public string WebClientId { get; set; } = "keel-web";
        /// <summary>Client secret for <see cref="WebClientId"/>; supply via environment/secret, never in images.</summary>
        public string WebClientSecret { get; set; } = "";
    }

    public sealed class AuditOptions
    {
        /// <summary>PEM file holding the ECDSA P-256 checkpoint signing key. Checkpointing is disabled when empty.</summary>
        public string CheckpointPrivateKeyPath { get; set; } = "";
        /// <summary>PEM public keys trusted when verifying checkpoints (current and retired keys).</summary>
        public string[] CheckpointPublicKeyPaths { get; set; } = [];
        public TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromMinutes(15);
    }

    public sealed class SecurityOptions
    {
        /// <summary>Refuse to start unless the OS crypto module runs in FIPS mode (ADR-0008). Set true in production.</summary>
        public bool RequireFips { get; set; }
    }
}
