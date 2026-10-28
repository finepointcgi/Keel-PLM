# Offline (air-gapped) install

The bundle contains everything needed to install Keel with no network: image archives, Helm chart, compose example, SBOMs, a signed
SHA-256 manifest and install/upgrade scripts. `install.sh` and `upgrade.sh` use no network at any point.

## Contents
```
keel-offline-<version>/
  images/keel-api-<version>.tar, postgres.tar, vault.tar, proxy.tar   docker save archives (third-party ones pinned by digest)
  images.env                  the pins (deploy/images.env)
  chart/keel-<version>.tgz    Helm chart
  compose/                    compose.prod-example.yaml and prod-example/ (Caddyfile, nginx.conf, gen-*.sh, initdb, env.example)
  sbom/                       CycloneDX JSON (see "SBOMs")
  docs/                       docs/ops/*.md
  signing/release-public.pem  public key of this release
  SHA256SUMS, SHA256SUMS.sig  manifest of every file and its detached ECDSA P-256 / SHA-256 signature
  verify.sh install.sh upgrade.sh lib.sh   VERSION BUILD-INFO
```

## Build (connected machine)
```bash
scripts/package-offline.sh --version 1.7.0            # builds the UBI image, saves images, packages chart, SBOMs, signs
scripts/package-offline.sh --image keel/api:ci-123    # package an image built elsewhere
```
Output: `deploy/offline/out/keel-offline-<version>.tar`, its `.sha256`, and `.signing-key.fingerprint`.
Disk: the UBI build needs about 2 GB free in the Docker VM and the archives about 1.5 GB on the host.

**Signing key.** A fresh ECDSA P-256 key pair is generated per release in `$KEEL_SIGNING_DIR` (default
`~/.keel-release-signing/<version>/release-private.pem`, mode 0600); only the public key ships. Production signing keys belong to the
customer: set `KEEL_SIGNING_KEY=/path/to/their-private.pem` (the key may live in an HSM behind an openssl provider). Announce the
fingerprint (SHA-256 of the DER public key, printed at the end of the build) through a separate channel.

**SBOMs.** `sbom/keel-api.cdx.json` and `keel-audit.cdx.json` come from the CycloneDX .NET tool (6.3.1, installed into a temp
tool path). Image SBOMs (OS packages of the UBI base, `rpm` database) are generated with `syft` when it is installed on the build
machine; otherwise they are not generated, `BUILD-INFO` says so, and the site scanner works from the archives. `docker sbom` is not used.

## Transfer and scan
1. Copy the `.tar`, `.tar.sha256` and (separately announced) fingerprint to approved removable media or the site's transfer guard.
2. At the low side of the transfer: antivirus scan, compare the SHA-256 of the archive with the `.sha256` file.
3. Optionally feed `images/*.tar` and `sbom/*.json` to the site's vulnerability scanner (Trivy/Grype/OpenSCAP work offline with their
   own databases). Findings are the site's to adjudicate; patching means a new bundle (hardening.md, patch cadence).
4. Extract on the target and run `./verify.sh --fingerprint <announced hex>`. Without `--fingerprint` or `--pubkey` it can only
   show internal consistency with the key that shipped in the same archive, and says so.

## Verify
`verify.sh` checks (1) the signature of `SHA256SUMS`, (2) every listed file's hash, (3) that no unlisted file exists. `install.sh`
and `upgrade.sh` run it first and stop on any failure; nothing is loaded before that.
`scripts/verify-offline.sh <bundle.tar>` proves the checks work: modified chart, compose file, script and pins, edited manifest, a
manifest signed by another key, extra file, missing file, missing signature, wrong fingerprint and a foreign public key are all refused.

## Install, compose
```bash
./verify.sh --fingerprint <hex>
./install.sh --compose --public-host keel.example.mil \
   --oidc-authority https://idp.example.mil/realms/keel --oidc-client-secret-file /secure/keel-web.secret \
   --certs-dir /secure/keel-certs --target /opt/keel --fingerprint <hex>
```
Steps: verify, `docker load` (or podman/nerdctl/`ctr`; `KEEL_RUNTIME` overrides), install files into `/opt/keel`, certificates from
`--certs-dir` (`ca.crt proxy.* postgres.* vault.*`; `--test-pki` generates throwaway ones for a trial), secrets via `gen-secrets.sh`
(random credentials, your OIDC secret, new audit checkpoint key), `.env`, `compose config` validation, then
`up -d --pull never --wait` (migration job runs first). `--no-start` stops before starting. Afterwards: back up
`prod-example/secrets/`, especially `audit_checkpoint_key.pem` (offline), see backup-restore.md.

## Install, Kubernetes
```bash
./install.sh --k8s --namespace keel --values site-values.yaml --registry registry.example.mil:5000 \
   --secrets-dir /secure/keel-secrets --fingerprint <hex>
```
Pushes the image to your cluster registry (or preload it with `ctr -n k8s.io images import` on every node and omit `--registry`),
creates the `keel-app` and `keel-migrator` Secrets if missing, then `helm upgrade --install` (migration hook, `--wait`).
`--dry-run` prints every command without executing. This path was not executed against a cluster in this increment (see 1.7 notes).

## Upgrade
```bash
./upgrade.sh --compose --target /opt/keel --backup-done --fingerprint <hex>
./upgrade.sh --k8s --namespace keel --values site-values.yaml --registry ... --backup-done
```
Refuses without `--backup-done` (migrations are forward-only; rollback is a restore), refuses a downgrade or a re-install of the same
version, keeps certificates, secrets and `.env`, runs the migration job before the application starts. See upgrade.md.

## Using a different IdP, certificates or vault
They are inputs, never bundled: IdP through `--oidc-authority`, certificates from your CA, vault through the chart value
`config.vault.serviceUrl` or by editing the compose example's `vault` service out and setting `Keel__Vault__ServiceUrl`.
