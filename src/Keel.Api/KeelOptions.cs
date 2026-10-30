namespace Keel.Api;

public sealed class KeelOptions
{
    public const string Section = "Keel";

    public DatabaseOptions Database { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
    public AuditOptions Audit { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();
    public DataProtectionOptions DataProtection { get; set; } = new();
    public ProxyOptions Proxy { get; set; } = new();
    /// <summary>Idle lock and termination of UI sessions (<c>Keel:Session</c>, NIST 3.1.10/3.1.11, ADR-0026).</summary>
    public Keel.Web.Services.SessionLockOptions Session { get; set; } = new();

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
        /// <summary>
        /// The authentication level (<c>acr</c>) an e-signature needs, for example "2" for the certificate-only level of the CAC/PIV Keycloak flow
        /// (ADR-0058). Sent as <c>acr_values</c> at the step-up and required in the id token and in every signing token. Empty = any fresh authentication (A9).
        /// </summary>
        public string StepUpAcr { get; set; } = "";
        /// <summary>What the operator states the identity provider is set up for; shown on the Sign-in status page, never enforced against the IdP.</summary>
        public SignInOptions SignIn { get; set; } = new();
    }

    public sealed class SignInOptions
    {
        /// <summary>How people sign in at the IdP: <c>Password</c> (the dev/demo default), <c>Certificate</c> (CAC/PIV) or <c>Federated</c> (the customer's own IdP).</summary>
        public string Method { get; set; } = "Password";
        /// <summary>True when the IdP also offers a username/password form next to the certificate or the federated sign-in.</summary>
        public bool PasswordFallback { get; set; }
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

    public sealed class DataProtectionOptions
    {
        /// <summary>
        /// Directory that holds the ASP.NET Data Protection key ring (cookie sessions, antiforgery, Blazor circuit state).
        /// Required in Production so keys survive restarts and are shared by all replicas (ReadWriteMany volume or one
        /// replica). The directory must be on an encrypted volume and writable only by the service account.
        /// </summary>
        public string KeyDirectory { get; set; } = "";
    }

    public sealed class ProxyOptions
    {
        /// <summary>
        /// Honour X-Forwarded-For/-Proto/-Host from a TLS-terminating reverse proxy or ingress. Required behind one: without
        /// it the OIDC redirect URI and the cookie scheme would be computed as plain http.
        /// </summary>
        public bool Enabled { get; set; }
        /// <summary>CIDR ranges of the proxies whose forwarding headers are trusted, e.g. "10.0.0.0/8". Required when <see cref="Enabled"/>.</summary>
        public string[] KnownNetworks { get; set; } = [];
    }
}
