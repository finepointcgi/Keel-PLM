#!/usr/bin/env bash
# Verifies the Keel Helm chart without a cluster: schema, lint, render, Kubernetes API validation and the security properties
# the chart promises (docs/ops/kubernetes.md). Needs helm; uses kubeconform when it is on PATH or in $KUBECONFORM.
#
#   scripts/verify-helm.sh
set -euo pipefail
cd "$(dirname "$0")/.."
CHART=deploy/helm/keel
OUT=$(mktemp -d)
trap 'rm -rf "$OUT"' EXIT
KC=${KUBECONFORM:-$(command -v kubeconform || true)}
fail=0
ok() { echo "ok   $1"; }
bad() { echo "FAIL $1"; fail=1; }

helm lint "$CHART" -f "$CHART/ci/production-values.yaml" >/dev/null && ok "lint with production values" || bad "lint with production values"
helm lint "$CHART" -f "$CHART/values-dev.yaml" >/dev/null && ok "lint with dev values" || bad "lint with dev values"

helm template keel "$CHART" -n keel -f "$CHART/ci/production-values.yaml" > "$OUT/prod.yaml"
helm template keel "$CHART" -n keel -f "$CHART/values-dev.yaml" > "$OUT/dev.yaml"
helm template keel "$CHART" -n keel -f "$CHART/ci/production-values.yaml" --set migration.mode=job > "$OUT/prod-job.yaml"
ok "render production, dev and job-mode variants"

if [[ -n "$KC" ]]; then
  for f in prod dev prod-job; do
    "$KC" -strict -summary -ignore-missing-schemas -kubernetes-version 1.30.0 "$OUT/$f.yaml" >/dev/null && ok "kubeconform $f (Kubernetes 1.30 schemas, strict)" || { "$KC" -strict -ignore-missing-schemas -kubernetes-version 1.30.0 "$OUT/$f.yaml" || true; bad "kubeconform $f"; }
  done
else
  echo "skip kubeconform (not installed)"
fi

has() { grep -q -- "$2" "$OUT/$1.yaml"; }
count() { grep -c -- "$2" "$OUT/$1.yaml" || true; }
has prod "runAsNonRoot: true" && ok "runAsNonRoot" || bad "runAsNonRoot"
has prod "readOnlyRootFilesystem: true" && ok "readOnlyRootFilesystem" || bad "readOnlyRootFilesystem"
has prod "allowPrivilegeEscalation: false" && ok "allowPrivilegeEscalation false" || bad "allowPrivilegeEscalation"
has prod "type: RuntimeDefault" && ok "seccomp RuntimeDefault" || bad "seccomp"
[[ $(count prod "automountServiceAccountToken: false") -ge 3 ]] && ok "service account tokens not mounted (deployment, job, accounts)" || bad "automountServiceAccountToken"
has prod "helm.sh/hook: pre-install,pre-upgrade" && ok "migration is a pre-install/pre-upgrade hook" || bad "migration hook"
! has prod "migrate-1" && ok "hook mode does not name the job per revision" || bad "hook job naming"
has prod-job "migrate-1" && ok "job mode names the job per revision" || bad "job-mode naming"
has prod "kind: NetworkPolicy" && ok "network policies rendered" || bad "network policies"
has prod "kind: PodDisruptionBudget" && ok "pod disruption budget" || bad "pdb"
has prod "kind: ServiceMonitor" && ok "service monitor (enabled in the example values)" || bad "servicemonitor"
has prod "nginx.ingress.kubernetes.io/affinity: cookie" && ok "cookie session affinity annotation" || bad "affinity annotation"
has prod "metrics-blocked" && ok "/metrics blocked at the ingress" || bad "metrics block"
! grep -qiE "password|secretkey|clientsecret" "$OUT/prod.yaml" && ok "no credential values in the production render" || bad "credential-like text in production render"
# The application pods never mount the migrator secret and the job never mounts the application secret.
awk '/^kind: Deployment/,/^---/' "$OUT/prod.yaml" | grep -q "keel-migrator" && bad "deployment mounts the migrator secret" || ok "deployment does not mount the migrator secret"
awk '/^kind: Job/,/^---/' "$OUT/prod.yaml" | grep -q "secretName: keel-app" && bad "job mounts the application secret" || ok "job does not mount the application secret"

expect_failure() { # name, expected message fragment, helm args...
  local name=$1 fragment=$2; shift 2
  if msg=$(helm template keel "$CHART" -n keel "$@" 2>&1); then bad "$name was accepted"; elif grep -qi -- "$fragment" <<<"$msg"; then ok "$name is rejected"; else bad "$name failed for another reason: $msg"; fi
}
expect_failure "no values at all" "authority"
expect_failure "multiple replicas on a ReadWriteOnce key ring" "ReadWriteMany" -f "$CHART/ci/production-values.yaml" --set dataProtection.persistence.accessMode=ReadWriteOnce
expect_failure "multiple replicas without a persistent key ring" "persistence.enabled" -f "$CHART/ci/production-values.yaml" --set dataProtection.persistence.enabled=false
expect_failure "network policy without egress peers" "egress" -f "$CHART/ci/production-values.yaml" --set 'networkPolicy.egress.postgres.to=null'
expect_failure "plain-http authority" "authority" -f "$CHART/ci/production-values.yaml" --set config.auth.authority=idp.example.mil
expect_failure "migration mode typo" "mode" -f "$CHART/ci/production-values.yaml" --set migration.mode=sometimes
expect_failure "writable root filesystem" "readOnlyRootFilesystem" -f "$CHART/ci/production-values.yaml" --set securityContext.readOnlyRootFilesystem=false

[[ $fail -eq 0 ]] && echo "chart verified" || { echo "chart verification FAILED"; exit 1; }
