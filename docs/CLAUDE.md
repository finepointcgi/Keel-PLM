# Keel PLM: working notes for future sessions

Keel is an on-prem, air-gapped PLM platform replacing Teamcenter/Windchill for `[Company]`.
Read this file, then [assumptions.md](assumptions.md) and [adr/README.md](adr/README.md), before changing code.

## Status
- **Phase 1, increment 1.1 (Foundations):** done 2026-10-27. Next: **1.2 Parts**.
- Phase plan: 1.1 Foundations → 1.2 Parts → 1.3 Documents and vault → 1.4 BOM → 1.5 Review and release → 1.6 Change → 1.7 Demo-ready.
  Later phases: 2 Configuration management, 3 Migration, 4 Integrations, 5 Production operations.

## Repo layout
```
src/Keel.Core        domain primitives shared by all modules (Subject, Markings, Result types)
src/Keel.Security    ABAC policy: in-memory evaluator + SQL filter builder (ADR-0004)
src/Keel.Audit       hash-chained audit log, checkpoints, verifier (ADR-0005)
src/Keel.Api         ASP.NET Core host, auth, health, metrics, endpoints
src/Keel.AuditCli    `keel-audit verify` command-line verifier
db/migrations        plain SQL migrations, applied in order by the migrator (Keel.Api --migrate)
scripts/             test.sh (CI entry point), smoke.sh (against running dev stack)
web/                 React + TypeScript SPA
deploy/compose       Docker Compose dev stack (Postgres, Keycloak with synthetic realm)
tests/*              xUnit unit + integration (Testcontainers) tests
docs/                ADRs, assumptions, setup, this file
```

## Conventions
- .NET 10, C# latest, nullable enabled, warnings as errors. File-scoped namespaces. `sealed` by default.
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
