# ADR-0020: Container images, configuration delivery and offline bundle details

**Status:** Accepted · 2026-10-28 · extends ADR-0012

## Context
ADR-0012 fixed the principles (UBI 9 images, compose and Helm, signed offline bundle). Building it surfaced decisions that ADR left open.

## Decision
- **Base images:** build on `registry.access.redhat.com/ubi9/dotnet-100` (SDK 10.0.113 at pin time), run on `ubi9/dotnet-100-aspnet`.
  The plain `dotnet-100-runtime` image has no ASP.NET Core; the ASP.NET image is the right one (ADR-0012 named the runtime image).
  Both exist and are multi-arch; pinned by manifest-list digest in `deploy/images.env`, the single source for the Dockerfile
  arguments, the compose example and the offline bundle.
- **Build ignores `global.json`:** the UBI SDK is a different feature band than the developer SDK pin. CI builds the solution with
  `-warnaserror` on the pinned SDK; the image build publishes with `TreatWarningsAsErrors=false`.
- **One image, three entrypoints:** web host (default), `--migrate` job, `keel-audit` at `/app/audit`. One artifact to scan and sign.
- **UID 1001, group 0**, `/var/lib/keel` writable by group 0 (arbitrary UIDs in group 0 work on OpenShift-style platforms).
- **Healthcheck:** `Keel.Api.dll --healthcheck` instead of installing curl. Fatal startup errors exit with code 1 through an
  `UnhandledException` handler: the runtime default is an abort, which triggered a slow, large core dump in a container.
- **Secrets as files:** `KEEL_SECRETS_DIR` adds a key-per-file configuration source for files named `Keel__Section__Key`. Fits Docker
  secrets and mounted Kubernetes Secrets; keeps credentials out of environment variables, manifests and images.
- **Production settings added to the host:** `Keel:DataProtection:KeyDirectory` (required in Production; a shared persistent key ring),
  `Keel:Proxy:Enabled/KnownNetworks` (forwarded headers from a trusted CIDR only, required for OIDC behind TLS termination),
  `Keel:Vault:RequestTimeoutSeconds` with `VaultUnavailableException` mapped to 503 + `Retry-After` (closes the known hang on a
  silent vault; a download already streaming cannot change its status).
- **TLS at the proxy/ingress; plain HTTP on the last hop** inside a private network by default; Kestrel TLS stays available through
  standard ASP.NET settings.
- **Proxy in the example:** Caddy (permissive licence, small, testable here). FIPS-sensitive sites use nginx on UBI (config shipped, not
  tested). **Vault in the example:** SeaweedFS as in dev, with TLS and telemetry off.
- **Helm:** migration as a pre-install/pre-upgrade hook Job with a separate secret holding only the migrator credential; `job` mode for
  dev clusters where dependencies do not exist before hooks run. `/metrics` is blocked at the ingress (endpoint-less Service) because
  NetworkPolicy cannot filter paths.
- **Bundle:** `docker save` archives, chart tgz, compose files, CycloneDX SBOMs, `SHA256SUMS` (sha256sum format) with a detached
  ECDSA P-256/SHA-256 signature by a per-release key (customer key via `KEEL_SIGNING_KEY`). `verify_bundle` checks the signature,
  every hash and the absence of unlisted files, and runs before anything is loaded or executed. The public key ships in the bundle,
  so its fingerprint must be pinned out of band (`--fingerprint`/`--pubkey`).
- **Rollback is restore** (forward-only migrations); `upgrade.sh` requires `--backup-done` and refuses downgrades.

## Consequences
- The UBI image build, the Helm install and the FIPS-enabled path were not exercised in the development environment (see
  `docs/increments/1.7-packaging.md`); the scripts and checks are there to run them where the resources exist.
- Images carry bash and coreutils from the UBI base; removing them needs a custom base and is a Phase 5 hardening option.
- The proxy-to-app hop is unencrypted by default.

## Alternatives
- Debian/Microsoft images (smaller, chiseled, no shell): not Red Hat supported and not FIPS-aligned with the RHEL hosts.
- `*_FILE` environment convention per setting: more code than a single key-per-file source.
- Separate audit CLI image: second artifact for no isolation gain.
