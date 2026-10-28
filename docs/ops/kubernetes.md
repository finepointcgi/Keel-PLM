# Kubernetes (Helm chart)

Chart: `deploy/helm/keel`. PostgreSQL, the S3 vault and the OIDC IdP are **external**; the chart does not bundle them.
Validated with `scripts/verify-helm.sh` (helm lint, render, kubeconform on Kubernetes 1.30 schemas, security-property and
negative tests). It has **not** been installed on a cluster in this increment.

## External dependencies (recommended options)
- **PostgreSQL 17:** CloudNativePG or Crunchy PGO (operators with WAL archiving and PITR), or an on-prem Patroni cluster.
  TLS with a certificate from the internal CA, `hostssl` only, pgAudit, encrypted volumes. Roles as in `deploy/compose/initdb/00-roles.sql`.
- **Vault:** any S3-compatible store the accreditation allows (Ceph RGW, a SeaweedFS cluster, an appliance): TLS, versioning,
  SSE-S3, bucket replication (backup-restore.md).
- **IdP:** the site IdP with CAC/PIV; confidential client `keel-web` and audience `keel-api`.

## Prepare
```bash
kubectl create namespace keel
kubectl -n keel create secret generic keel-app \
  --from-file=Keel__Database__AppConnectionString --from-file=Keel__Auth__WebClientSecret \
  --from-file=Keel__Vault__AccessKey --from-file=Keel__Vault__SecretKey \
  --from-file=audit_checkpoint_key.pem --from-file=audit_checkpoint_key.pub.pem
kubectl -n keel create secret generic keel-migrator --from-file=Keel__Database__MigratorConnectionString
kubectl -n keel create configmap keel-internal-ca --from-file=ca.crt
kubectl -n keel create secret tls keel-tls --cert=tls.crt --key=tls.key
```
(`deploy/compose/prod-example/gen-secrets.sh` produces the files.) Use an external secret store (Vault Agent, External Secrets)
where you have one; the chart only references Secret names, never values.

## Install
Copy `ci/production-values.yaml` to a site values file and fill in the authority, vault URL, ingress host, proxy network (the ingress
controller's pod CIDR), NetworkPolicy egress peers and storage class, then:
```bash
helm install keel deploy/helm/keel -n keel -f site-values.yaml --set image.digest=sha256:<digest>
```
The chart refuses to render without the required values (also enforced by `values.schema.json`).

## What the chart does
- **Deployment:** rolling update (maxUnavailable 0), soft pod anti-affinity (`antiAffinity: hard` to require), non-root UID 1001,
  read-only root filesystem, all capabilities dropped, no privilege escalation, seccomp `RuntimeDefault`, no service account token,
  startup and liveness probes on `/health/live`, readiness on `/health/ready`, `/tmp` as emptyDir.
- **Migration:** Helm pre-install/pre-upgrade hook Job (`migration.mode: hook`) with its own service account and the
  **migrator** Secret; web pods never see migrator credentials. A failed migration fails the release before any pod rolls.
- **Data Protection keys:** a PVC (`helm.sh/resource-policy: keep`) mounted at `/var/lib/keel/dataprotection`. With `replicaCount > 1`
  the chart requires `ReadWriteMany` (or `existingClaim`); use an encrypted RWX class. Losing the key ring logs everybody out.
- **Ingress:** TLS only; ingress-nginx cookie affinity annotations (replace the map for other controllers; Blazor circuits are pinned
  to a pod), 5 GB bodies, long timeouts. `/metrics` is routed to an endpoint-less Service (503), so it is not reachable through the
  ingress.
- **NetworkPolicy:** default deny for web and migration pods. Web: ingress on 8080 from the ingress and monitoring namespaces; egress
  to DNS and to the three peers you name (PostgreSQL, vault, IdP). Layer 4 cannot separate `/metrics` from other paths on the same
  port, so the ingress block keeps users away from it, and only those two namespaces can reach the pod port at all. The migration job
  gets DNS and PostgreSQL only.
- **PodDisruptionBudget** (`minAvailable: 1`), optional **ServiceMonitor** (needs the Prometheus Operator CRDs; kubeconform skipped its
  schema), service account without token.

## Malware scanning, SIEM and session settings (increment 5.0; NOT RUN on a cluster)
The chart templates are unchanged by this increment; the new settings go in through `config.extraEnv` (plain) or the Secret (secrets). A production install (`requireFips: true`) is **refused at startup** unless scanning is configured or acknowledged:
```yaml
config:
  extraEnv:
    Keel__Vault__Scanning__Mode: Required
    Keel__Vault__Scanning__Host: 127.0.0.1        # clamd as a sidecar in the same pod (loopback, no network exposure)
    Keel__Session__IdleLockMinutes: "15"
    # Keel__Siem__Enabled: "true" ... see docs/ops/siem-onboarding.md (certificates as files in the application Secret)
```
ClamAV image: `clamav/clamav` (pin by digest in the site registry; not pulled or run here). The chart has no `extraContainers` value yet, so add the sidecar by patching the Deployment (container `clamav`, image above, `CLAMAV_NO_FRESHCLAMD=true`,
a volume with the offline signature databases, `clamd.conf` from `deploy/compose/prod-example/clamd.conf`), or run clamd as a separate Service and set `Host` plus `AllowRemotePlaintext: "true"` (and allow the egress in the NetworkPolicy; the link is unencrypted). The idle lock needs the cookie affinity annotations the chart already sets.

## Dev / test cluster
`values-dev.yaml` (`dev.enabled=true`) adds throwaway in-cluster PostgreSQL and SeaweedFS with random credentials, no TLS and no
persistence, and runs the migration as a plain per-revision Job that retries until the database is up. Use release name `keel`
(the vault URL in the file assumes it). kind/minikube only; the IdP is still external (any reachable OIDC issuer).

## Sizing
Start with 2 replicas, requests 250m CPU and 512 Mi, limit 2 Gi. Each connected user holds a circuit of a few hundred KB; plan memory
from concurrent users (A11: 500). Load-based sizing is Phase 5.
