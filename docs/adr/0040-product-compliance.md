# ADR-0040: Product compliance data (RoHS, REACH, conflict minerals) and material declarations

Status: accepted (increment 8.5, 2026-10-29). Builds on ADR-0004 (markings and access policy), ADR-0005 (audit), ADR-0017 (BOM structure and redaction), ADR-0018 (review and
e-signature), ADR-0029 (relationship pictures), ADR-0030 (notifications), ADR-0031 (list sorting) and ADR-0037 (sourcing, whose manufacturer parts and suppliers are referenced
here only as opaque text). Assumptions are FEAT-CMP-n in [increments/8.5-compliance.md](../increments/8.5-compliance.md).

## Context
Customers and regulators ask whether a delivered assembly contains restricted substances (RoHS), substances of very high concern (REACH SVHC) or conflict minerals (3TG), and
want the answer as a file (a conflict minerals report, a material declaration). The facts come from suppliers and from engineering, they expire, and the lists they are measured
against change by law. Three constraints shape the design: the lists are regulation, so a change of a threshold must be a reviewed, dated, signed act that never rewrites history;
a part nobody has declared must read as unknown, never as compliant; and the usual guarantees hold (markings on everything, audit in the same transaction, no marked text in
audit or notification payloads, air-gapped, strict CSP).

## Decisions

### 1. One module, one schema, reference data separate from declarations
`Keel.Compliance` (schema `cmp`, migration `0023_compliance.sql`) references only Core, Security and Audit. Reference data is public regulatory data and carries no markings:
the substance master (CAS number with its check digit validated, EC number, the 3TG flag), materials with a typical composition, and regulatory lists. Declarations are about
parts and are marked through the part.

### 2. Lists are versioned data, published by signatures, immutable afterwards
A list (`rohs`, `reach_svhc`, `conflict_minerals`, or one an administrator adds) has a kind (restricted, svhc, conflict) and versions. A version holds entries (substance and a
threshold in mass percent, 0 meaning any amount), exemptions (code, substance, expiry) and an effective date. It is drafted by an administrator, submitted through the workflow
engine (`reg-list-publish`, an ordinary workflow template) and becomes current when the approvers sign: the gateway supersedes the previous version, publishes this one and
re-evaluates every declaration in the same transaction. The signed content is a readable summary plus a digest recomputed from the live rows. Database triggers refuse any
change of the entries of a version that is not a draft and any change of a published version except its supersession, so history cannot be rewritten even by a bug. The
version in force on a date is the newest published version whose effective date has come, so a version published for next month changes nothing until then; the daily job
re-evaluates when the set in force changed. A starter set of three lists is seeded (sample thresholds, not the legal lists).

### 3. A declaration is composition plus claims, evaluated per line
A declaration (per part, optionally for one revision letter) lists substances or materials with an amount as mass percent or as mass (turned into a percent through the mass of the
item), an exemption code and, for 3TG, a conflict-free statement. A material line expands into its substances. Each line is its own homogeneous material: its percent is compared
with the threshold of its substance. Above a restricted or SVHC threshold is a violation unless the line claims an exemption that the list version has, that applies to the
substance and that has not expired; a conflict mineral is judged by its statement (not conflict-free is a violation, not stated leaves it unverified). Evaluation is a pure
function (`ComplianceRules.Evaluate`); the stored status uses one precedence everywhere (withdrawn, expired, non-compliant, verified, declared). Opaque references to a supplier
number and a manufacturer part, a source (internal or supplier) and linked documents (by document number, checked for readability each time) complete the record.

### 4. Access follows the part, and the roll-up is built from what the reader can read
A declaration has no markings; every read joins the part master and applies the read filter (or evaluates the part's markings for a single object), so a change of the part's
markings applies at once and a hidden part's declarations do not exist for the reader. The roll-up of an assembly is composed in `Keel.Web` (`ComplianceRollupService`): the
BOM read paths (which redact) give the structure; the module reads the declarations of exactly the parts the reader may read, again under the filter. A hidden line is not in the
report and cannot change a total, a status or a cause. A purchased part (a leaf) without a current declaration is a gap; a declaration counts only while it has not expired or been
withdrawn and covers the line's revision (an unknown revision matches unscoped declarations only); several current declarations of one part (alternate sources) are judged by the worst.
An assembly is non-compliant if any line is, incomplete if any is missing, expired or unknown, and compliant otherwise. Reports can be pinned to list versions and to a date.

### 5. Exports are files built from the report
A conflict minerals CSV (one row per purchased part, 3TG status, declaration, supplier reference) and a material declaration XML in the spirit of IPC-1752 (items, declarations,
composition per substance, standing per regulation). The XML is not a conformant IPC-1752A or B document; its schema is documented in `docs/compliance/exports.md`. Cells that a
spreadsheet would run as a formula are neutralized; XML text is escaped and stripped of characters XML cannot carry. Every export is audited with counts only.

### 6. Expiry, re-evaluation and notices are pointers
`DeclarationExpiryService` raises one notice per declaration and stage (60, 30 and 7 days before, expired; idempotent through `cmp.expiry_notice`), marks expired declarations, and
re-evaluates when the regulations in force changed. Notifications (declaration became non-compliant, expires soon, expired, supplier declaration recorded) point at the part and
are checked per recipient by the notification module; audit details carry declaration numbers, codes and counts, never names, notes, reasons or reference text.

### 7. Interface
Pages: the Compliance tab of a part, the assembly roll-up (summary per regulation, the causes, a table and a treemap with drill-down), declarations, regulatory lists and their
versions, substances and materials, a dashboard card on Home. REST under `/api/v1/compliance` with its own OpenAPI document (`docs/compliance/openapi.json`, drift-tested).
Substances are found by the global search (`sub:`) and the palette; a part's declarations are on its timeline and in its activity feed.

## Consequences
- A threshold change is a new dated version with signatures; the old version stays for reports pinned to it.
- Evaluation is recomputed on read for views and roll-ups, and stored on write for status, search and the dashboard; the two cannot disagree except between an effective date
  passing and the daily job, which is bounded by its interval.
- A regulation's exemption code means nothing to another regulation: a RoHS exemption does not excuse lead above 0.1 % under REACH SVHC.
- Supplier and manufacturer part references are text; showing a supplier's name next to a declaration needs a resolver the Sourcing module can register later.

## Alternatives considered
- Marking each declaration separately: the part's markings would have to be copied and kept in step, and a change of the part could leave a declaration more open than its part.
- Storing the roll-up: it would go stale at every declaration, list or BOM change and would have to be access-filtered per reader anyway; computing it keeps one definition.
- Treating a missing declaration as compliant until proven otherwise: the opposite of what an audit needs.
