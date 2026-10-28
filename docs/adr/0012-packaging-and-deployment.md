# ADR-0012: Packaging, deployment and offline install

**Status:** Accepted · 2026-10-27

## Decision
- Container images built on Red Hat UBI 9 (`ubi9/dotnet-100-runtime` for the API, `ubi9/nginx` for the SPA), running non-root with read-only root filesystems and no shell in production images where feasible. Built to pass the DISA container hardening guide and STIG scans.
- Docker Compose for dev and small sites (A12), Helm chart for Kubernetes (Phase 5 hardening, chart started early).
- Object storage via the S3 API only (A13). The dev store choice is recorded in increment 1.3.
- **Offline bundle:** images (`docker save`/OCI archives), Helm chart, SBOMs (CycloneDX), SHA-256 manifests with a detached signature, and an install/upgrade script that needs no network.
- Configuration through environment variables and mounted files. Secrets via K8s secrets or files, never baked into images.
