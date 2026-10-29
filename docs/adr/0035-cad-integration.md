# ADR-0035: CAD integration without vendor plug-ins

Status: accepted (increment 8.0, 2026-10-29). Builds on ADR-0001 (modular monolith), ADR-0002 (versioned objects), ADR-0004 (markings and the read filter), ADR-0005 (audit),
ADR-0015 (vault), ADR-0017 (BOM structure), ADR-0026 (malware scanning) and ADR-0034 (accessibility audit).

## Context
CAD systems (NX, Creo, SolidWorks, CATIA) hold the engineering model and the drawings; Keel holds the controlled product data. Vendor plug-ins need SDKs and licences we do not have
and cannot ship. What Keel can offer for real is the vendor-neutral half: a protocol that a plug-in or script calls, so that a plug-in author writes a thin client and no policy.
The protocol must not be a way around anything: markings, locks, iteration rules, release locks, malware scanning and audit stay exactly what they are for ordinary documents, parts and BOMs.

## Decisions

### 1. CAD files are ordinary documents; the CAD module adds a graph, not a second vault
`Keel.Cad` (schema `cad`, migration 0018) stores only the CAD role of a document (`model`, `drawing`, `derived-pdf`, `step`), the sets of documents checked out together, the dependency
graph between document iterations, associations with parts, and structure proposals. File bytes, markings, lifecycle, revisions, scanning and download marking are the document
module's. The lock **is** the document check-out, so a CAD set and a person editing in the browser exclude each other through the one mechanism that already exists.

### 2. Modules stay separate: ports in Keel.Core, adapters by the owners
`Keel.Cad` references Core, Security and Audit only. It reaches documents through `ICadDocumentPort` (implemented in `Keel.Documents` over `DocumentService`) and parts and BOM
structure through `ICadPartPort` (implemented in `Keel.Api`, which already references both modules, over `PartService` and `BomService`). The architecture test lists `Keel.Cad`
with those references. Nothing in the CAD module writes to a `plm` table.

### 3. Sets are atomic, in the document module
`DocumentService` gained `CheckOutSetAsync`, `CheckInSetAsync` and `UndoCheckOutSetAsync`: one transaction, row locks taken in number order (concurrent sets cannot deadlock),
the same per-document bodies as the single-document commands (those were extracted, not copied), the same audit events (marked `set=true`), no change when any member refuses.
`CheckInSetAsync` runs a callback in the same transaction, which is where the CAD module writes dependency rows and closes the set, so iterations, dependencies and audit commit
or roll back together. Check-out of a set records the set in the same transaction as the locks.

### 4. Integrity: the client states the SHA-256, the server proves it
An upload carries `X-Content-SHA256` (required). The request stream is hashed while the vault reads it, and a different digest throws before anything is recorded; the vault
object that was written is an unreferenced orphan collected by vault GC. Storage is content-addressed by the vault as always, malware scanning and the size limit apply in the vault,
and the check-in can name the `{fileName, sha256}` pairs the connector expects in each working copy. Connectors receive files as the unchanged-file ZIP with the markings cover sheet
(never a stamped PDF), so the stored bytes come back exactly.

### 5. Dependencies are asserted by the connector, recorded immutably, and compared lazily
Keel does not read native CAD files. The connector declares, at check-in, which documents each member was built against; Keel records the referenced document's revision and iteration
at that moment (the new iteration when it is in the same set). A document is stale when the referenced document's latest iteration differs. Nothing is rewritten when a model changes:
staleness is a comparison at read time, so it is always current. References not declared at a check-in carry forward with their old values (no silent refresh); declaring a stale
reference again is how a connector says the document was rebuilt.

### 6. Structure ingestion is a proposal; applying it uses the normal services
A neutral JSON schema (`keel.cad.structure/v1`) is the one internal form; STEP (AP242/AP214/AP203 product structure), CSV and XML importers produce it, and one validator checks it
(cycles, quantities, find numbers, designators, size limits, no recursion, no DTDs). The STEP reader is a small Part 21 reader written for Keel: no OpenCASCADE, no third-party
parser, no geometry, so there is nothing to licence. A proposal matches parts by number and then by attribute rules, shows the difference with the current EBOM (computed on every
read, so it is never stale), and is applied only by a person with Modify permission. Apply creates missing parts through `PartService`, then edits each parent through part check-out,
BOM working-copy rules and part check-in. A released or foreign-checked-out parent blocks the whole apply before anything changes. Removals are off unless asked for. Nothing is released.

### 7. Proposals are marked data; audit holds numbers only
A proposal names parts, so it carries the marking columns, is listed through `AccessPolicy.BuildReadFilter` and is a 404 for anyone who may not read it. A matched part the viewer
cannot read is shown as "a part you are not authorized to see". Audit entries of the CAD module hold numbers, counts, hashes and the banner, never names, titles, descriptions or
attribute values; a unit test scans the module's audit calls for such keys and an integration test scans the written payloads.

### 8. The API has its own OpenAPI document
`/openapi/cad.json` (3.1), committed as `docs/integration/cad-openapi.json` and compared by a test, kept apart from the ERP integration document so the two evolve independently.
Protocol errors carry a stable `code` member (`cad.locked`, `cad.package_incomplete`, `cad.integrity_mismatch`, `cad.set_closed`, `cad.member_unknown`, `cad.reference_unknown`,
`cad.proposal`).

## Consequences
- A plug-in author implements authentication, hashing, reference declaration and a few HTTP calls; the guide is `docs/integration/cad-connector.md`.
- Nothing here has been exercised against a real CAD system. The reference tool `keel-cad` proves the protocol end to end with plain files.
- Check-in creates one iteration of every member (as specified). `skipUnchanged` is an opt-in to avoid empty iterations.
- Dependency rows are per iteration and immutable, so the graph grows with every check-in; the queries touch only the latest iteration of each document.
- A document set larger than 500 members is refused; larger assemblies are checked out in parts.
