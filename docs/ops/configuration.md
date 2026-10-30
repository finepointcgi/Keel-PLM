# Configuration reference

Every setting of the Keel host (UI + REST API), where it is read from, its default, whether production needs it, and an example.
Settings come from, in increasing precedence: `appsettings.json` (shipped, secure defaults), environment variables
(`Keel__Section__Key`; `__` stands for `:`, array items are `Key__0`, `Key__1`), and files in `KEEL_SECRETS_DIR`.

## Secrets as files

Set `KEEL_SECRETS_DIR` to a directory. Every file in it whose name starts with `Keel__` becomes a setting: the name with `__`
replaced by `:` is the key, the file content (trailing line break removed) is the value. Other files in the directory are ignored.
This is the shape of a Docker/Compose secrets mount and of a mounted Kubernetes Secret, so no credential has to appear in an
environment variable (visible in `docker inspect` and in pod specs) or in an image. A missing directory is a startup error.

```
/run/secrets/keel/Keel__Database__AppConnectionString     -> Keel:Database:AppConnectionString
/run/secrets/keel/Keel__Auth__WebClientSecret             -> Keel:Auth:WebClientSecret
```

## Keel settings

"Prod" says whether a production deployment must set the value (R = required, O = optional, D = the default is right for production).
Secret-bearing settings are marked **secret**: supply them as files, never in an image, a values file or git.

### Database (`Keel:Database`)
| Setting | Default | Prod | Example / notes |
|---|---|---|---|
| `Keel__Database__AppConnectionString` | empty | R, **secret** | `Host=db.example.mil;Port=5432;Database=keel;Username=keel_app;Password=...;SSL Mode=VerifyFull;Root Certificate=/etc/keel/ca/ca.crt`. The least-privilege role `keel_app` (ADR-0005). Use `SSL Mode=VerifyFull` (ADR-0008). |
| `Keel__Database__MigratorConnectionString` | empty | R for the migration job only, **secret** | Same shape with `keel_migrator`, the schema owner. Only `--migrate` reads it; the web pods never receive it (compose: separate secret on the `migrate` service; chart: separate Secret mounted by the job only). |
| `Keel__Database__AppRole` | `keel_app` | D | Role that receives grants from the migrator. Change only if you rename the role. |

### Authentication (`Keel:Auth`)
| Setting | Default | Prod | Example / notes |
|---|---|---|---|
| `Keel__Auth__Authority` | empty | R | `https://idp.example.mil/realms/keel`: the issuer exactly as in the IdP discovery document. Discovery and JWKS are fetched from it at runtime, so the pod needs a network path (and the internal CA, see `SSL_CERT_FILE`). |
| `Keel__Auth__Audience` | `keel-api` | D | Audience the API validates on bearer tokens. |
| `Keel__Auth__RequireHttpsMetadata` | `true` | D | Set `false` only for a dev IdP on plain http. |
| `Keel__Auth__WebClientId` | `keel-web` | D | Confidential OIDC client of the UI (ADR-0014). Register `https://<public host>/signin-oidc` and `https://<public host>/signout-callback-oidc` as its redirect URIs. |
| `Keel__Auth__WebClientSecret` | empty | R, **secret** | The client secret the IdP issued for `keel-web`. |

### Audit (`Keel:Audit`)
| Setting | Default | Prod | Example / notes |
|---|---|---|---|
| `Keel__Audit__CheckpointPrivateKeyPath` | empty (checkpointing off) | R | `/run/secrets/keel/audit_checkpoint_key.pem`: ECDSA P-256 PKCS#8 PEM (`keel-audit keygen`). The database never sees it (ADR-0005). Without it no signed checkpoints are written and tampering by a DBA is only detectable by the hash chain, not proven by signature. |
| `Keel__Audit__CheckpointPublicKeyPaths__0..n` | none | R | Public keys trusted when verifying checkpoints: the current key and every retired one. The signing key's own public half is trusted automatically. Keep retired public keys forever: old checkpoints need them. |
| `Keel__Audit__CheckpointInterval` | `00:15:00` | D | Time between signed checkpoints (`hh:mm:ss`). |

### Security (`Keel:Security`)
| Setting | Default | Prod | Example / notes |
|---|---|---|---|
| `Keel__Security__RequireFips` | `true` (appsettings.json) | D | The host refuses to start unless `/proc/sys/crypto/fips_enabled` is `1` (ADR-0008). Set `false` only on non-FIPS test machines; `/health/ready` then reports the fips check as Degraded. |

### Data protection (`Keel:DataProtection`)
| Setting | Default | Prod | Example / notes |
|---|---|---|---|
| `Keel__DataProtection__KeyDirectory` | empty | R in Production (startup error if empty), image sets `/var/lib/keel/dataprotection` | Directory of the ASP.NET Data Protection key ring: signs and encrypts the session cookie, antiforgery tokens and Blazor circuit state. It must survive restarts and be the **same directory for every replica**. Keys are stored as XML, not encrypted by the framework: the volume must be encrypted (LUKS/encrypted PV) and readable only by the service account. |

### Reverse proxy (`Keel:Proxy`)
| Setting | Default | Prod | Example / notes |
|---|---|---|---|
| `Keel__Proxy__Enabled` | `false` | R behind a TLS-terminating proxy or ingress | Honours `X-Forwarded-For/-Proto/-Host`. Without it the OIDC redirect URI and cookie scheme are computed as plain http and sign-in fails. |
| `Keel__Proxy__KnownNetworks__0..n` | none | R when enabled | CIDR ranges of the proxies whose headers are trusted, e.g. `172.28.40.0/24` (compose `edge` network) or the ingress controller's pod CIDR. Enabled with no networks is a startup error. Narrow these ranges: a trusted network can spoof the client address and scheme. |

### Vault (`Keel:Vault`, ADR-0015)
| Setting | Default | Prod | Example / notes |
|---|---|---|---|
| `Keel__Vault__ServiceUrl` | empty | R | `https://vault.example.mil:8333`: S3 endpoint, path-style. `https://` in production. |
| `Keel__Vault__AccessKey` | empty | R, **secret** | Store credential scoped to the one bucket. |
| `Keel__Vault__SecretKey` | empty | R, **secret** | |
| `Keel__Vault__Bucket` | `keel-vault` | D | Must exist; the application never creates buckets. Enable versioning and, where the store supports it, object lock (the application never overwrites or deletes objects except its own `staging/` keys). |
| `Keel__Vault__Region` | `us-east-1` | D | Signing region only; on-prem stores accept any value. |
| `Keel__Vault__ServerSideEncryption` | `true` (appsettings.json) | D | SSE-S3 (`AES256`) on every object. The store must support it; if not, set `false` **and** encrypt the store volume. |
| `Keel__Vault__MaxUploadBytes` | `2147483648` | O | 1 to 5 GiB (server-side copy is single-request). |
| `Keel__Vault__PartSizeBytes` | `16777216` | O | 5 MiB to 512 MiB; the most memory one upload holds. |
| `Keel__Vault__MaxPdfStampBytes` | `268435456` | O | Larger PDFs are delivered in a marked ZIP instead of stamped in place. |
| `Keel__Vault__RequestTimeoutSeconds` | `30` | O | 1 to 600. Upper bound for one store request (retries included) and for each read of a streaming download. A store that does not answer makes the API reply `503` with `Retry-After: 5` instead of hanging. A download already streaming to the client cannot change its status code: it is cut. |

### Integration (`Keel:Integration`, ADR-0025, [runbook](integration.md))
| Setting | Default | Prod | Example / notes |
|---|---|---|---|
| `Keel__Integration__DispatcherEnabled` | `false` | O | Runs the delivery dispatcher in this process. Enable in at least one replica to deliver events. |
| `Keel__Integration__PollInterval` / `BatchSize` / `Lease` | `00:00:02` / `25` / `00:02:00` | O | Dispatcher polling, deliveries per poll, how long a claim holds before another dispatcher may take it over. |
| `Keel__Integration__MaxAttempts` | `8` | O | Attempts before a delivery becomes a dead letter. |
| `Keel__Integration__BackoffBase` / `BackoffMax` | `00:00:05` / `01:00:00` | O | Exponential backoff with jitter. |
| `Keel__Integration__LagDegradedSeconds` | `300` | O | Readiness reports Degraded when the oldest due delivery waited longer. |
| `Keel__Integration__WebhookTimeoutSeconds` | `10` | O | Default per-request timeout of a webhook sink (a sink can set its own, 1 to 120). |
| `Keel__Integration__AllowedHosts__0..n` | none (deny) | R for webhooks | Host names a sink URL may use: exact or `*.suffix`. |
| `Keel__Integration__AllowedNetworks__0..n` | none | O | CIDR ranges a sink URL may resolve into. |
| `Keel__Integration__AllowDangerousAddresses` | `false` | D | Development only: allows loopback, link-local and metadata addresses. Never in production. |
| `Keel__Integration__FileDropRoot` | empty (file sinks refused) | R for file sinks | Directory (mounted volume) under which file-drop sinks write. |
| `Keel__Integration__Secrets__<name>` | none | R per sink, **secret** | HMAC secret referenced by a sink's `secretName`. Names: letters, digits, `_`, `-`. |
| `Keel__Integration__ClientCertificates__<name>` | none | O | Path of a PEM file with the client certificate and its private key (mTLS), mounted from a secret. |
| `Keel__Integration__CaBundles__<name>` | none | O | Path of a PEM bundle the endpoint's certificate must chain to. |
| `Keel__Integration__BomPublishedMaxLines` / `BomFullStructureMaxLines` | `5000` / `5000` | O | Larger structures are skipped and audited, never truncated. |
| `Keel__Integration__TdpSigningKeyPath` | empty (export answers 503) | R for TDP | `/run/secrets/keel/tdp_signing_key.pem`: ECDSA P-256 PKCS#8 PEM. |
| `Keel__Integration__TdpTrustedPublicKeyPaths__0..n` | none | O | Public keys trusted when verifying packages (retired signing keys). |
| `Keel__Integration__TdpMaxParts` / `TdpMaxDocuments` | `2000` / `5000` | O | Larger packages are refused so they can be split. |
### Vault malware scanning (`Keel:Vault:Scanning`, ADR-0026)
| Setting | Default | Prod | Example / notes |
|---|---|---|---|
| `Keel__Vault__Scanning__Mode` | `Disabled` | R | `Required` (refuse uploads the scanner cannot judge), `BestEffort` (accept as `unscanned`), `Disabled`. **`Disabled` with `RequireFips=true` is a startup error** unless `AcknowledgeUnscannedUploads=true`. |
| `Keel__Vault__Scanning__Host` / `Port` | `127.0.0.1` / `3310` | O | clamd TCP. A non-loopback host needs `AllowRemotePlaintext=true` (INSTREAM is not encrypted). |
| `Keel__Vault__Scanning__UnixSocket` | empty | O | `/run/clamav/clamd.sock`; used instead of Host/Port when set. |
| `Keel__Vault__Scanning__ConnectTimeoutSeconds` / `IoTimeoutSeconds` / `ResultTimeoutSeconds` | `5` / `30` / `120` | O | Bounds for connecting, each read/write, and the verdict after the last byte. |
| `Keel__Vault__Scanning__MaxScanBytes` | `2147483648` | O | At most clamd `StreamMaxLength`. Larger files: refused (Required) or `skipped` (BestEffort). |
| `Keel__Vault__Scanning__ChunkBytes` | `262144` | O | INSTREAM chunk size. |
| `Keel__Vault__Scanning__AllowRemotePlaintext` | `false` | O | Acknowledges an unencrypted link to a scanner on another host. |
| `Keel__Vault__Scanning__AcknowledgeUnscannedUploads` | `false` | O | The site accepts running production without scanning. |

### Vault garbage collection (`Keel:Vault:Gc`, ADR-0026)
| Setting | Default | Prod | Example / notes |
|---|---|---|---|
| `Keel__Vault__Gc__Enabled` | `false` | O | Run the sweeper as a hosted service. The CLI `keel-vault gc` needs no setting. |
| `Keel__Vault__Gc__Mode` | `DryRun` | O | `Delete` makes the service delete. |
| `Keel__Vault__Gc__IntervalHours` | `24` | O | 1 to 744. |
| `Keel__Vault__Gc__GraceDays` | `7` | O | At least 1 (enforced). Unreferenced objects younger than this are kept. |
| `Keel__Vault__Gc__MaxDeletionsPerRun` | `1000` | O | Caps candidates handled per run; a continuation token resumes. |
| `Keel__Vault__Gc__PageSize` | `500` | O | 10 to 1000 objects per listing request. |

### Session lock (`Keel:Session`, ADR-0026)
| Setting | Default | Prod | Example / notes |
|---|---|---|---|
| `Keel__Session__IdleLockMinutes` | `15` | D | 1 to 120 in production; `0` (off) is refused when `RequireFips` is true or the environment is Production. |
| `Keel__Session__WarningSeconds` | `60` | D | Shorter than the idle timeout. |
| `Keel__Session__LockedTerminateMinutes` | `15` | D | Locked this long = session terminated and signed out. |
| `Keel__Session__ActivityThrottleSeconds` | `5` | D | Browser activity report interval (1 to 60). |
| `Keel__Session__Store` | `Database` | D | `Database` shares lock state between replicas and keeps it across restarts (migration 0016). `Memory` is refused in production. |

### Gap wave B settings (ADR-0032)
Outbox retention (`Keel:Integration:Retention`, `Keel:Integration:FileDropMaxPendingFiles`) and the e-mail channel (`Keel:Notifications:Email`) are documented with their tables in [integration.md](integration.md) and [notifications.md](notifications.md).

### SIEM forwarding (`Keel:Siem`, ADR-0026, siem-onboarding.md)
| Setting | Default | Prod | Example / notes |
|---|---|---|---|
| `Keel__Siem__Enabled` | `false` | R (recommended) | Needs at least one destination. UDP is refused (NotSupportedException); plain TCP needs `AllowInsecureTcp` and is refused with `RequireFips=true`. |
| `Keel__Siem__Hostname` / `AppName` / `Facility` | machine / `keel-plm` / `13` | O | Syslog header fields. |
| `Keel__Siem__EnterpriseNumber` | `32473` | R | Private enterprise number of `[keel@PEN ...]`; 32473 is the documentation placeholder, replace it. |
| `Keel__Siem__BatchSize` / `QueueBatches` / `PollIntervalMilliseconds` | `200` / `4` / `1000` | O | Batch, bounded read-ahead queue, idle poll. |
| `Keel__Siem__Destinations__N__Name` / `Type` | required / `SyslogTls` | R | `SyslogTls` or `File`. |
| `Keel__Siem__Destinations__N__Host` / `Port` / `ServerName` / `Transport` | / `6514` / Host / `Tls` | R | TLS 1.2/1.3 syslog with octet counting. |
| `Keel__Siem__Destinations__N__CaBundlePath` | system store | R | PEM CA(s); relative paths resolve against `KEEL_SECRETS_DIR`. |
| `Keel__Siem__Destinations__N__ClientCertificatePath` / `ClientKeyPath` / `ClientKeyPassword` | empty | O, **secret** (password) | mutual TLS. |
| `Keel__Siem__Destinations__N__FilePath` | empty | | NDJSON sink for a forwarder. |
| `Keel__Siem__Destinations__N__ConnectTimeoutSeconds` / `SendTimeoutSeconds` / `BackoffMinSeconds` / `BackoffMaxSeconds` | `10` / `30` / `1` / `60` | O | Delivery timing. |

### Workflow (`Keel:Workflow`)
| Setting | Default | Prod | Example / notes |
|---|---|---|---|
| `Keel__Workflow__StepUpMaxAgeSeconds` | `300` | O | 30 to 900. How recently the signer must have authenticated for an e-signature (A9). |
| `Keel__Workflow__DirectoryRefreshSeconds` | `300` | O | Minimum interval between user-directory writes for an unchanged user. |

## Package signing (optional; ADR-0051)

The signing identity itself is configured in the browser (Administration > Package signing); these settings only bound it. Guide: [package-signing.md](package-signing.md).

| Setting | Default | Notes |
|---|---|---|
| `Keel__Reports__Signing__AllowedKeyDirectory` | unset | When set, key, PKCS#12 and password files must lie below this directory. Recommended in production (for example the mount of a Kubernetes Secret). |
| `Keel__Reports__Signing__AllowDevelopmentCertificate` | `true` on Development and Demo hosts, ignored elsewhere | `false` hides the dev-only certificate generator on a development host; a production host never offers it. |
| `Keel__Reports__Signing__DevelopmentKeyDirectory` | `keel-dev-signing` in the temp directory | Where the generator writes its key files (development hosts only). |

## Process and platform settings

| Setting | Default (image) | Notes |
|---|---|---|
| `KEEL_SECRETS_DIR` | unset | Directory of `Keel__*` secret files (above). |
| `ASPNETCORE_URLS` | `http://+:8080` | The image listens on plain HTTP 8080: TLS ends at the proxy/ingress. To encrypt the last hop too, set `ASPNETCORE_URLS=https://+:8443` and `ASPNETCORE_Kestrel__Certificates__Default__Path`/`KeyPath` to a mounted certificate and key. |
| `ASPNETCORE_ENVIRONMENT` | `Production` | Production enables HSTS and requires the data protection key directory. Never `Development` outside a developer machine (it serves the dev-only settings). |
| `SSL_CERT_FILE` | unset | PEM bundle of the site's internal CA. .NET on Linux then trusts exactly that bundle for PostgreSQL (verify-full), the vault and the IdP. Compose mounts it from `certs/ca.crt`, the chart from `tls.caBundle.existingConfigMap`. |
| `Logging__LogLevel__Default` | `Information` | Logs are JSON on stdout with a correlation id. |
| `DOTNET_EnableDiagnostics` | `0` (image) | Disables the diagnostics socket and dump generation. |

## Command line

| Invocation | Purpose |
|---|---|
| `Keel.Api.dll` (image default) | Web host: UI + REST API. |
| `Keel.Api.dll --migrate` | Applies `db/migrations` with the migrator connection and exits. Forward-only, hash-checked (`docs/ops/upgrade.md`). |
| `Keel.Api.dll --healthcheck [url]` | Exit 0 when `/health/live` (default `http://127.0.0.1:8080/health/live`) answers 2xx. This is the image `HEALTHCHECK`; there is no curl in the image. |
| `dotnet /app/audit/keel-audit.dll verify --public-key <pem>...` | Offline audit chain verifier; reads `KEEL_AUDIT_CONNECTION`. |
| `dotnet /app/vault/keel-vault.dll gc [--dry-run \| --delete --confirm-bucket B] ...` | Vault garbage collection (compliance-features.md). No implicit database target. |

## Endpoints for operators

| Path | Auth | Meaning |
|---|---|---|
| `/health/live` | none | The process serves requests. Use for liveness. |
| `/health/ready` | none | Database reachable and schema current, vault bucket reachable, FIPS posture. JSON with one entry per check; `Degraded` (HTTP 200) when FIPS is off and not required, `Unhealthy` (503) otherwise. |
| `/metrics` | none | Prometheus metrics. **Never expose to users:** the example proxy and the chart's ingress block it, and the chart's NetworkPolicy limits who can reach the pod port. |
