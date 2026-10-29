# ADR-0045: A guarded, self-contained demo on Google Cloud Run

Status: accepted (increment 9.0, 2026-10-29). Builds on ADR-0007 (identity and e-signature step-up), ADR-0012 and ADR-0020 (packaging), ADR-0014 (UI auth),
ADR-0015 (vault) and ADR-0026 (idle lock and malware scanning). Assumptions are DEMO-n in [increments/9.0-cloud-run-demo.md](../increments/9.0-cloud-run-demo.md).
Operations guide: [ops/cloud-run-demo.md](../ops/cloud-run-demo.md).

## Context
Keel normally needs PostgreSQL 17, an S3 vault and an OIDC identity provider (Keycloak in dev). The owner wants a link to show the product, built straight from the GitHub
repository by Cloud Build and run as ONE container on Cloud Run, the way the sister product Workbench is. A public, password-free demo is also the most dangerous thing this
code base can do: it must be impossible to switch on by accident next to real data.

## Decisions
1. **One image, last Dockerfile stage `demo`.** Cloud Build's default build uses the last stage; production stays `--target runtime` (`scripts/package-offline.sh` now says so).
   The stage is plain Dockerfile syntax (no BuildKit-only features; a test enforces it). Base `mcr.microsoft.com/dotnet/aspnet:10.0-noble` (Debian family) because
   PostgreSQL 17 is available from the PGDG repository there; the UBI production base is untouched. Packages are fetched at build time only. The build stage is the
   existing `publish` stage (portable output runs on any runtime), plus a `demo-publish` stage for the seeder.
2. **Vault: SeaweedFS, the same store as the dev stack.** `weed` is copied from the image pinned in `deploy/images.env` (a test keeps the Dockerfile's pin equal to it). It is
   Apache-2.0 and already proven with the vault code. A smaller store (for example MinIO, AGPL, or Garage) would add a second S3 dialect to verify for no real saving:
   measured memory of the whole container is about 250-300 MiB. The real vault code path is used (uploads, hashing, marked downloads, GC, viewer): no fake store.
3. **Demo mode needs BOTH `ASPNETCORE_ENVIRONMENT=Demo` and `Keel:Demo:Enabled=true`.** `Enabled=true` in any other environment ends the start with an error (before any
   other setup). The Demo environment without `Enabled` is an ordinary host. The host also refuses to start against a database with non-demo identities (user directory or
   audit actors that are not one of the dev-realm subjects or `system:*`). The entrypoint independently refuses to run unless both switches are set. Nothing of demo
   sign-in is registered or mapped otherwise (`DemoMode.IsEnabled` gates `AddKeelDemo` and `MapKeelDemo`); production configuration validation is not changed.
4. **Identity without an IdP.** A scheme registered under the OpenID Connect scheme name replaces the remote challenge: a challenge redirects to `/account/demo-signin`, a
   sign-out redirects back. Everything else (cookie ForwardChallenge, `/account/step-up`, session-ended, logout) works unchanged. The page lists the dev-realm people (not
   `erp-service`) with roles, attributes and what to try; no passwords. The principal is built from the embedded realm file (`deploy/compose/keycloak/keel-realm.json`)
   with exactly the claims the realm's mappers issue, plus `auth_time`, the session-expiry claim and a session id, so ABAC, markings, the idle lock and the audit log behave
   identically (unit tests compare every claim with the realm file; an integration test compares the resulting subjects with the seed's realm users).
5. **Step-up and unlock.** In demo mode `/account/step-up` shows a "Confirm it is you" page for the signed-in person only; one click re-issues the cookie with a new
   `auth_time`, the same session id and the same absolute expiry (as the OIDC path does), writes the `session.step_up` audit event and releases the idle lock of that session.
6. **REST API.** In demo mode the policy scheme sends `/api/*` requests to the cookie scheme when there is no bearer header and the demo session cookie is present; everything
   else (a bearer header, no cookie) still goes to JWT validation unchanged, and a forged token is still 401. No token-minting endpoint is added: it would be a second way to
   become any user with no cookie protections. Consequence: scripts use a cookie jar (see `deploy/demo/smoke-test.sh`). The cookie is HttpOnly, SameSite=Lax.
7. **Cloud Run shape.** Single instance (`--max-instances=1`): the database is per instance. CPU always allocated and `--min-instances=1` for a warm demo (PostgreSQL and the
   idle-lock timer need CPU between requests). Data lives in the container file system (memory-backed on Cloud Run) and is reseeded on every cold start (about 10 s).
   Forwarded headers are trusted from any address, which is safe only because the container is reachable solely through Cloud Run's front end.
8. **Scanning.** ClamAV is not in the image: `Scanning:Mode=Disabled` with the existing `AcknowledgeUnscannedUploads=true`; the sign-in page says uploads are not scanned.

## Consequences
- The demo is a deliberately weak target: anyone who reaches it is any user. It is only ever to be run with synthetic data, ideally behind IAM (see the operations guide).
- Cloud Run instances are ephemeral; nothing is backed up. Surprise restarts reset the demo, which the banner states.
- The Dockerfile's production publish stage now also copies two build inputs it was missing (the CAD JSON schema under `docs/`, the realm file); the first was a latent
  build break.
- Not verified by this work: Cloud Build and Cloud Run themselves.
