# ADR-0004: Attribute-based access control and export-control markings

**Status:** Accepted · 2026-10-27

## Context
Every object carries export-control and CUI markings. Access must be decided from the user's attributes (A2) against those markings everywhere data can surface: object pages, search, BOM explosion, where-used, downloads, exports, notifications. A check that runs only on "open object" leaks through lists.

## Decision

### Markings (value object, stored as columns on every master/revision)
| Field | Values | Notes |
|---|---|---|
| `export_jurisdiction` | `None`, `EAR`, `ITAR` | |
| `eccn` | e.g. `EAR99`, `9E610.a` | required when EAR |
| `usml_category` | e.g. `XI(d)` | required when ITAR |
| `cui_categories` | set, e.g. `CTI`, `EXPT`, `PROPIN` | empty = not CUI |
| `distribution_statement` | `A`–`F` per DoDI 5230.24, plus controlling office and reason text | |
| `program` | program/contract code or null | |
| `owning_org` | org code | |
| `export_authorizations` | set of license/agreement IDs (TAA, MLA, DSP-5…) that permit foreign-person access to this object | A3 |

Invariants are enforced by the value object (ITAR needs a USML category; EAR needs an ECCN; CUI-marked export data must carry `EXPT`).

### Read rule (all clauses must hold; missing attributes fail closed)
1. **Export:** jurisdiction `None`, or EAR with ECCN `EAR99`, or `subject.us_person`, or `subject.export_authorizations ∩ object.export_authorizations ≠ ∅`.
2. **Program need-to-know:** `object.program` is null or in `subject.programs`.
3. **CUI:** `object.cui_categories ⊆ subject.cui_categories`.

Distribution statements are **rendered and stamped** on every view and file, and enforced on **outbound release** (TDP export and integrations, Phase 4). Assumption: internal users are employees of the contractor and so eligible under statements B–E for internal use. Statement F can only be applied to an object that has a program, so the program need-to-know clause always governs it.

### Actions and roles
Roles come from the IdP `roles` claim: `plm-viewer`, `plm-author`, `plm-approver`, `plm-admin`, `plm-auditor`. An action is permitted only if **the role allows the action AND the marking read rule passes**. **Administrators do not bypass markings.** An admin without U.S.-person status cannot read ITAR data. Modify also requires `subject.org = object.owning_org` or membership of the object's program.

### One policy, two evaluators
`AccessPolicy` is implemented once in C# and exposes:
- `Evaluate(subject, markings, action) → Decision { Permit | Deny, reasons[] }` for single objects; the reasons are written to the audit log.
- `BuildReadFilter(subject) → (sql, parameters)`: a SQL predicate over the marking columns, used by every list, search, BOM and where-used query.

A property-based test generates random subjects and markings and asserts that both evaluators agree, against a real PostgreSQL instance. That test is a release gate.

### Redaction
Rows that fail the filter in structural queries (BOM, where-used) come back as redacted placeholders (A4). Rows that fail in search are simply absent; we don't confirm that they exist.

## Consequences
- Policy changes are code changes reviewed by security, not ad-hoc ACLs. Admin-configurable rules can come later on the same evaluator pair.
- All read paths must use the filter builder; an architecture test flags raw queries on marked tables that don't use it.

## Alternatives
- OPA/Rego sidecar: air-gap-friendly, but partial evaluation to SQL is immature and it adds a hop on every list query.
- Per-object ACLs (Windchill-style): cannot express nationality rules and drift badly at 5 M objects.
