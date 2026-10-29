# Keel PLM: working notes for future sessions

Keel is an on-prem, air-gapped PLM platform replacing Teamcenter/Windchill for `[Company]`.
Read this file, then [assumptions.md](assumptions.md) and [adr/README.md](adr/README.md), before changing code.

## Status
- **Phase 1:** 1.1 Foundations, 1.2 Parts, 1.3 Documents and vault, 1.4 BOM, **Phase 1 is complete (2026-10-28)**: 1.1-1.7 merged (foundations, parts, documents/vault, BOM, workflow/e-signature, change management, seed data + E2E + demo script, container image/Helm/offline bundle). Phase 3 migration importers merged (2026-10-28; docs in docs/migration/). Phase 2a baselines + effectivity merged (migration 0008, notes in increments/2.1-configuration.md). Phase 2b admin UI + classification merged (migration 0009; 0008 and 0009 share schema `cfg`: 0009 uses CREATE SCHEMA IF NOT EXISTS and its own immutability function, and seeds the `baseline-default` lifecycle family). Follow-ups: wire classification into the migration importer (IClassificationLoader is NotSupported until then; format in docs/migration/classification-format.md), register the classification participant in Keel.Seed, then map migrated effectivity text (kept in BOM line notes) into real effectivity once real exports are available. Phase 2c EBOM/MBOM merged (migration 0011, notes increments/2.3-mbom.md). Phase 4 integrations merged (migration 0012: outbox, webhook/file sinks, clearance profiles, pull API, signed TDP export; docs/integration/). Follow-up: emit `mbom.released` (additive: event constant, schema, emission point) now that MBOM and the outbox are both merged. Phase 5 compliance gaps merged (migration 0013: idle session lock, upload malware scanning via clamd, vault GC (`keel-vault`), SIEM forwarding (`Keel.Siem`); ops docs in docs/ops/compliance-features.md and siem-onboarding.md). Production now refuses to start with scanning Disabled unless acknowledged (CMP-13). UX-2 file viewer merged (`Keel.Viewer`, namespace `Keel.Viewing`; standalone /viewer pages with their own CSP; 3D via three.js + occt-import-js (LGPL, see docs/third-party/viewer.md), PDF via pdf.js, images). Follow-ups merged (notes increments/7.1-followups.md): `mbom.released` event + MBOM pull endpoint, importer loads classification and effectivity (mapping `classification.*`, `effectivity.*`; default off), richer demo seed (classes, baselines, effectivity, MBOMs, TDP record, integration profiles). UX-1 merged (migration 0014 `ws` schema, `Keel.Workspace`, design system under Keel.Web/wwwroot/css, shell/rail/breadcrumbs, Home dashboard at `/`, Ctrl/Cmd+K palette and `/search`, object-page header+stepper+tabs, toasts/confirm/empty/skeleton, DataTable; see docs/ux/design-system.md). UX-4 merged (migration 0015 schema `ntf`, `Keel.Notifications`, bell/notifications/watch/activity/comments; ADR-0030, notes increments/7.3-awareness.md, docs/ops/notifications.md). UX-3 merged (no migration; impact map, BOM treemap, structure diff, timelines via `ITimelineSource`, workflow progress map; ADR-0029, notes increments/7.2-visuals.md). Gap wave A merged (no migration; server-side sorting, MBOM markings + supply-type admin, shared confirm dialog, heading outline test, `?tab=` deep links, `Markings` value equality fix; ADR-0031, notes increments/7.4-completeness.md). Gap wave B merged (migration 0016: DB-backed idle lock, outbox retention, SMTP e-mail channel, spool sink hardening; broker sink deferred; ADR-0032, notes increments/7.5-platform.md). Gap wave C merged (migration 0017: document review via workflow, `document-release-review` template; offline bundle sources/SBOM/notices via `Keel.SbomTool`; ADR-0033, notes increments/7.6-doc-review-licenses.md). Gap wave D (accessibility, performance) in progress.
- Per-increment notes (delivered, tests, decisions, deferred): [1.3](increments/1.3-documents.md), [1.4](increments/1.4-bom.md), [1.5](increments/1.5-workflow.md), [1.6](increments/1.6-change.md), [1.7 demo](increments/1.7-demo.md), [1.7 packaging](increments/1.7-packaging.md), [2.1 configuration](increments/2.1-configuration.md), [2.2 admin/classification](increments/2.2-admin-classification.md), [2.3 MBOM](increments/2.3-mbom.md), [4.0 integration](increments/4.0-integration.md), [5.0 compliance](increments/5.0-compliance.md), [6.0 viewer](increments/6.0-viewer.md), [7.0 UX shell](increments/7.0-ux-shell.md), [7.1 follow-ups](increments/7.1-followups.md), [3.0 migration](increments/3.0-migration.md). Demo walkthrough: [demo-script.md](demo-script.md). Operations: [docs/ops](ops/).
- Phase plan: 1.1 Foundations → 1.2 Parts → 1.3 Documents and vault → 1.4 BOM → 1.5 Review and release → 1.6 Change → 1.7 Demo-ready.
  Later phases: 2 Configuration management, 3 Migration, 4 Integrations, 5 Production operations.

## Repo layout
```
src/Keel.Core        domain primitives shared by all modules (Subject, Markings, Result types)
src/Keel.Security    ABAC policy: in-memory evaluator + SQL filter builder (ADR-0004)
src/Keel.Audit       hash-chained audit log, checkpoints, verifier (ADR-0005)
src/Keel.Documents   documents, vault (S3), marked downloads, part-document links
src/Keel.Bom         BOM structure, explosion, where-used, compare, CSV
src/Keel.Admin       admin configuration (attributes, versioned lifecycles, lists, numbering, templates), classification, config export/import
src/Keel.Integration outbox, sinks (webhook/file), outbound release check, pull API, TDP export
src/Keel.Migration   migration importers: readers, mapping, dry-run, load, reconcile (CLI: src/Keel.Import)
src/Keel.Change      change management: PR/ECR/ECN/CT, revise-in-change, impact analysis
src/Keel.Workflow    workflow engine, e-signature with step-up, user directory
src/Keel.Parts       parts: master/revision/iteration, lifecycle, attributes, check-out/check-in, search
src/Keel.Api         ASP.NET Core host: REST API + Blazor UI, auth, health, metrics
src/Keel.Web         Blazor (Interactive Server) UI components and pages (ADR-0014)
src/Keel.AuditCli    `keel-audit verify` command-line verifier
db/migrations        plain SQL migrations, applied in order by the migrator (Keel.Api --migrate)
scripts/             test.sh (CI entry point), smoke.sh (against running dev stack)
deploy/compose       Docker Compose dev stack (Postgres, Keycloak with synthetic realm)
tests/*              xUnit unit + integration (Testcontainers) tests
docs/                ADRs, assumptions, setup, this file
```

## Conventions
- .NET 10, C# latest, nullable enabled, warnings as errors. File-scoped namespaces. `sealed` by default.
- UI is Blazor Interactive Server without prerendering (ADR-0014). Pages call module services in-process with `CurrentUser.GetAsync()`; every page renders through `<PageFrame Banner=… Title=…>` (enforced by an architecture test). Never put business rules in components.
- Modules reference `Keel.Core` only; `Keel.Api` composes them. Do not reference one module's internals from another.
- SQL: hand-written migrations in `db/migrations/NNNN_name.sql`, forward-only, applied by the migrator role `keel_migrator`. The app runs as `keel_app` with least privilege.
- Every read path over marked data **must** use `AccessPolicy.BuildReadFilter`. Every single-object action **must** call `AccessPolicy.Evaluate` and write an audit event (including denials).
- Business change + its audit event go in **one transaction** (`IAuditLog.AppendAsync(tx, …)`).
- No placeholder logic. If something is out of scope, it throws `NotSupportedException` with the phase that delivers it, and is listed in the increment summary.
- Assumptions are written as `// ASSUMPTION(A<n>): …` in code and kept in `docs/assumptions.md`.
- Only FIPS-approved algorithms (ADR-0008). Never MD5/SHA-1.
- Test data is synthetic. Never commit real export-controlled data.
- Use mixed case for UI text and docs. Reserve all caps for marking banners and portion marks, where format requires it.

## Domain glossary
| Term | Meaning |
|---|---|
| Master | Identity of an object (number, name), shared by all versions. |
| Revision | Formal, change-controlled version (A, B, …; ASME Y14.35). Lifecycle state lives here. |
| Iteration | Working save inside a revision (A.1, A.2). Immutable once superseded. |
| Check-out / check-in | Exclusive lock creating a working copy; check-in produces the next iteration. |
| Usage link | BOM line: parent iteration → child master, with qty, UoM, find number, reference designators. |
| Config spec | Rule that picks which child version a usage link resolves to (Latest, LatestReleased, Effectivity, Baseline). |
| EBOM / MBOM | Engineering vs manufacturing structure; distinct, linked by reconciliation (Phase 2). |
| Effectivity | Applicability by serial number, lot/block or date (Phase 2). |
| PR / ECR / ECN / CT | Problem Report, Engineering Change Request, Engineering Change Notice, Change Task. |
| Affected / resulting objects | Versions an ECN changes / the new versions it produces. |
| Baseline | Frozen set of specific versions for audits/milestones (Phase 2). |
| Markings | Export jurisdiction (ITAR/EAR + USML/ECCN), CUI categories, distribution statement (DoDI 5230.24), program, owning org, export authorizations. |
| Subject | Authenticated user + security attributes (us_person, citizenship, programs, authorizations, CUI categories, roles). |
| Redacted placeholder | A BOM/where-used line the user cannot see; shows qty/find number only. |
| TDP | Technical Data Package (MIL-STD-31000). |

## How to run
See [setup.md](setup.md).

## Increment log

### 1.1 Foundations (2026-10-27)
**Delivered**
- ADR-0001…0013, assumptions register A1–A21 (all user-approved defaults, unconfirmed).
- `Markings` value object with ITAR/EAR/CUI/distribution invariants; `Subject`; `AccessPolicy` (in-memory `Evaluate` + SQL `BuildReadFilter`).
- Hash-chained audit log (`audit.event`), immutability triggers, least-privilege `keel_app` role, ECDSA P-256 signed checkpoints, `AuditVerifier`, `keel-audit` CLI (keygen/verify).
- API host: OIDC JWT validation, claim→Subject mapping (fail closed), role policies, audited role denials, correlation IDs, OWASP headers, JSON logs, `/health/live|ready` (DB, schema, FIPS posture), Prometheus `/metrics`, migrator.
- Endpoints: `GET /api/v1/config`, `GET /api/v1/me`, `POST /api/v1/access/evaluate`, `GET /api/v1/audit/events`, `POST /api/v1/audit/verify`, `POST /api/v1/audit/checkpoints`.
- Compose dev stack (Postgres 17, Keycloak 26.4 realm with 7 synthetic users), React shell with top/bottom marking banners, profile, access check, audit log pages.

**Tests:** 22 unit, 21 integration (Testcontainers Postgres, incl. policy/SQL agreement over 288 subjects × valid marking combinations, tamper detection), 3 web, `scripts/smoke.sh` (9 checks against real Keycloak).

**Known gaps / deferred (explicit)**
- API and web are not yet containerized (UBI images, Helm): increment 1.7 / Phase 5.
- E-signature step-up (`auth_time`) is designed (ADR-0007) but lands with signing in 1.5.
- Architecture test enforcing module references and filter usage: added in 1.2 when the first marked table exists.
- Unauthenticated (401) requests are logged, not chained into the audit log (flood resistance), per ADR-0005.
- Tail deletion *after* the last checkpoint is only detectable via SIEM forwarding (Phase 5) — checkpoint interval bounds the window.

### 1.2 Parts (2026-10-27)
**Delivered**
- Migration `0002_parts.sql`: `plm` schema; data-defined lifecycle (`part-default`: In Work → Under Review → Released → Obsolete, transitions tagged manual/workflow/change);
  admin-defined attribute definitions (string/number/boolean/enum, seeded for parts); part master/revision/iteration; check-out table holding the private working copy.
- Database guards: iterations immutable; no inserts/check-outs on locked revisions; locked revisions can only change state; masters/revisions never deleted; part identity immutable.
- `Keel.Parts.PartService`: create (auto `P-nnnnnn`, explicit numbers admin-only), get (404 when unauthorized, denial audited), search (FTS + trigram, SQL access filter, aggregate page banner), check-out / save working copy / check-in / undo (owner or admin), manual transitions, admin "set state" override with reason, markings change with reason and self-lockout prevention. All per-part mutations serialize on `SELECT … FOR UPDATE` of the master.
- `RevisionScheme` (ASME Y14.35), `AttributeValidator`, `Markings.AggregateBanner`, shared `MarkingColumns` contract in Keel.Security.
- API: `/api/v1/parts` (GET search, POST create), `/{number}` GET, `/checkout`, `/working-copy`, `/checkin`, `/undo-checkout`, `/state`, `/markings`, plus `/api/v1/metadata/parts`. Outcome → HTTP mapping (400/403 with reason codes/404/409).
- Web: parts search (per-row marking chips, page banner = aggregate), create form (attributes from metadata, markings), detail (check-out/edit/check-in, transitions with reasons, admin set-state, markings change, history, revision switcher). Access tokens now renew via refresh token.
- Architecture tests: module reference rules; list queries over `plm.part_master` must interpolate `filter.Sql` or be key lookups.

**Tests:** 44 unit, 34 integration (incl. 12-way concurrent check-out, DB lock enforcement, unauthorized-read auditing), 6 web, smoke script now 16 checks.

**Decisions / assumptions made in this increment**
- Name, type, unit and source live on the master and are not editable yet (rename is Phase 2). Description and attributes are per iteration.
- `created_by` / `state_changed_by` store the login name for display; the audit log keeps the stable IdP subject.
- Manual transitions: In Work ⇄ Under Review. Release only via workflow (1.5); Obsolete only via change notice (1.6). Admins can override with a reason (migration and corrections), audited as `part.set_state`.
- Markings of locked revisions change only through change control (1.6).
- Revising (A → B) is implemented in 1.6 inside an ECN (A8); `RevisionScheme.Next` is ready for it.

**Deferred**
- Revision-level marking overrides (ADR-0002) are not yet stored; markings live on the master.
- Attribute definitions are seeded by migration; the admin UI is Phase 2.

### UI moved to Blazor (2026-10-27, product owner direction)
- ADR-0014 supersedes ADR-0010: Blazor Interactive Server (no prerender) in `Keel.Web`, hosted by `Keel.Api`. React app and Node toolchain removed.
- UI auth: server-side OIDC code + PKCE (+ PAR) as confidential client `keel-web`, HTTP-only `keel.session` cookie (SameSite=Lax, 8 h absolute, revalidated in-circuit every minute). API keeps JWT bearer; policy scheme `keel` routes `/api/*` and bearer requests to JWT.
- Shared services extracted so UI and API can't diverge: `SubjectClaims` (Keel.Security), `AuditLogService` + `TrustedCheckpointKeys` (Keel.Audit), `Policies` (Keel.Core). `/api/v1/config` (SPA bootstrap) removed. Audit queries support newest-first.
- Security fixes found while porting: CSP `form-action` now includes the IdP origin (sign-out redirect), `base-uri 'self'`; logout validates antiforgery explicitly (minimal APIs don't enforce it); forbidden UI navigation lands on `/access-denied` (audited once); dev realm users have fixed IDs.
- Tests: bUnit component tests (banner placement via sections, attribute/markings fields), web service tests, architecture rule "every page uses PageFrame", UI auth integration tests (IdP redirect with PKCE, API 401 not redirect, framework script served, logout CSRF rejected).
- Deferred: idle-session lock (NIST 3.1.10) beyond the absolute lifetime; Playwright E2E (1.7).

### Merge of increments 1.3-1.5 (2026-10-28)
Built in parallel by three agents in separate worktrees, merged into `main`. Reconciliations made by the coordinator:
- `PartService` check-in now does: insert iteration -> copy document links -> `IPartIterationParticipant` hook (BOM, with veto) -> bump iteration/delete check-out.
- Under Review is workflow-only (see A28): tests that need a locked part use `SetStateAsync(admin, ..., adminOverride: true)`.
- Architecture rule for list queries accepts any `*filter.Sql}` interpolation (case-insensitive); `PartWorkflowGateway` is allow-listed (keyed loads; the workflow service evaluates policy).
- BOM virtualization uses a small JS shim because Blazor `Virtualize` writes inline `style` attributes that the CSP blocks; CSP is intentionally not relaxed.
- Dev Docker VM disk was ~99% full at merge time (two orphaned PostgreSQL volumes from other work, ~26 GB). SeaweedFS (vault) cannot create volumes in that state and uploads hang; automated tests use tmpfs and are unaffected. Free Docker disk before using the dev vault.
- Known gaps: API upload does not time out when the vault cannot write (hang instead of a fast 503) - add a vault write timeout; dev CLI password-grant tokens have no `auth_time`, so REST e-signature smoke tests need an authorization-code token.

### Phase 1 completion notes (2026-10-28)
- Final merge state: 233 unit, 177 integration tests, 7 Playwright E2E (KEEL_E2E=1, `scripts/e2e.sh`; not part of `scripts/test.sh`), packaging scripts (`verify-compose-example.sh`, `verify-helm.sh`, `verify-offline.sh`, `backup-restore-test.sh`) run by the packaging agent against throwaway containers.
- NOT verified: the UBI image build itself (Docker VM disk), the FIPS-enabled path on a real FIPS host, Helm install on a cluster, image SBOMs (only .NET SBOMs), the demo script by hand in a headed browser.
- Seeder and tools never default to the dev database (`--use-dev-stack` is explicit) after a scale run once wrote to it.
- Dev vault telemetry disabled (`-master.telemetry=false`), matching the air-gap requirement.
- Open compliance gaps (docs/ops/nist-800-171-mapping.md): idle session lock (3.1.10), malware scanning hook, vault GC, SIEM forwarding.

### Test-suite notes (2026-10-28)
- Integration tests share one SeaweedFS bucket and one Postgres container; keep assertions scoped to your own data. `ScanningTests.Eicar_*` uses a unique payload for that reason; the SIEM metric test polls; the uuid-v7 test compares millisecond stamps only.
- `VaultGcTests.Dry_run_reports_orphans_*` failed once under heavy parallel load in a full run and passed on every rerun (alone and in the full suite). If it recurs, investigate listing/prefix scoping before dismissing it.
- New hash columns must be classified in `Keel.Documents/Vault/VaultReferences.cs` (a schema guard test enforces it).

### State at 2026-10-28 (end of build-out waves)
- Tests on main: 571 unit, 470 integration, 25 Playwright E2E (`KEEL_E2E=1`, `scripts/e2e.sh`). All migrations 0001-0013 apply on a fresh database.
- Operational notes: running several agents' Testcontainers stacks in the 4 GB Docker VM kills the dev Keycloak (exit 137); restart with `docker compose -f deploy/compose/compose.yaml up -d keycloak`. An unreachable IdP makes unauthenticated requests return 500 (OIDC discovery fails), not a redirect. Old session cookies (before the lock feature) are refused once: sign in again.
- Demo environment: `keel_demo` database + throwaway vault on :18334 + launch config `keel-demo` (see .claude/launch.json). Re-seed with `Keel__Database__AppConnectionString=... dotnet run --project src/Keel.Seed -- --scenario demo`.

- Load-sensitive tests (seen failing only when another heavy test run shared the 4 GB Docker VM; all pass alone): `SessionLockTests.The_server_locks_an_idle_session_by_itself_*`, `UiAuthTests.Ui_document_download_*`, `DocumentApiTests.Document_lifecycle_*` (vault-dependent). Run the suites one at a time.
- Test cookies must carry a `keel_session_id` claim (cookies without one are refused as stale).

### State at end of UX-1 (2026-10-29)
- Tests on main: 673 unit, 499 integration, 33 Playwright E2E (`KEEL_E2E=1`), all passing on a quiet machine. Migrations 0001-0014 apply on a fresh database.
- GC tests use their own vault bucket through a test-only admin identity written into the test container's s3.json (a 4-hex key prefix in the shared bucket collided with other tests' objects in a few percent of runs).
- Palette result rows wrap the marking chip under the name instead of squeezing the name.
