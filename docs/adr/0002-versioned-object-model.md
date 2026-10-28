# ADR-0002: Master / revision / iteration object model

**Status:** Accepted · 2026-10-27

## Context
Engineers know Windchill/Teamcenter semantics: a *master* holds identity (number, name), a *revision* (A, B…) is a formal, change-controlled version, an *iteration* (A.1, A.2) is a working save inside a revision.

## Decision
- Tables per versioned type family: `*_master`, `*_revision`, `*_iteration`. All carry `uuid` primary keys (v7, time-ordered).
- Revision labels follow ASME Y14.35 (A7) and are produced by one `RevisionScheme` service, configurable per type later.
- **Iterations are immutable** once superseded. Check-out creates a private working copy owned by one user; check-in makes it iteration n+1. Undo-checkout discards it. One check-out per revision at a time (row lock + unique partial index).
- Lifecycle state lives on the **revision**. Released revisions reject edits by domain rule **and** a database trigger (defence in depth).
- Attributes: typed core columns for universal fields; admin-defined attributes in a `jsonb` column validated against the type's attribute set (Phase 2 adds the admin UI; Phase 1 seeds definitions).
- Every versioned object carries a `markings` value object (ADR-0004) on the **master**, with an optional override on a revision (markings can tighten on a later revision, never silently loosen without an audited change).

## Consequences
- Matches incumbent mental models, eases migration (PLMXML / Windchill exports map 1:1).
- Queries need "latest iteration of revision" views; we maintain `is_latest` flags transactionally rather than computing them.

## Alternatives
- Single table with version columns: simpler but makes "latest" queries and BOM resolution slower and murkier.
- Event sourcing the whole model: attractive for history but heavy for search/BOM queries; the audit log (ADR-0005) gives us the history guarantees we need.
