# ADR-0033: Document review through the workflow engine, and licence and source completeness of the offline bundle (gap wave C)

Status: accepted (2026-10-29). Builds on ADR-0018 (review workflow and e-signature), ADR-0016 (documents), ADR-0025 (outbox), ADR-0027 (file viewer, LGPL reading),
ADR-0012 and ADR-0020 (packaging, offline bundle). Notes and assumptions GAP-C-1 to GAP-C-14: [7.6-doc-review-licenses.md](../increments/7.6-doc-review-licenses.md).
Migration `0017_document_review.sql`. **Legal review of the licence arrangement is pending** ([third-party/legal-review.md](../third-party/legal-review.md)).

## Context
Documents had the same lifecycle as parts, but Release was reachable only through an administrator override because no workflow gateway existed ("Documents gateway:
needs only a new `IWorkflowObjectGateway`", 1.5 notes). Separately, the viewer ships occt-import-js (LGPL-2.1) and the offline bundle carried neither its corresponding
source nor a notices file, and the SBOM covered .NET only. For an air-gapped site a link to source is not a usable offer.

## Decisions

### 1. `DocumentWorkflowGateway`, the part pattern
`Keel.Documents.DocumentWorkflowGateway` implements `IWorkflowObjectGateway` with plain keyed SQL (allow-listed in the architecture rule like `PartWorkflowGateway`; the engine
evaluates `AccessPolicy` on the markings it returns). Nothing changes in the engine: start, tasks, e-signature meanings and step-up, cancel, reassign, the workflow map, the
timeline source, notifications (`WorkflowAwareness`) and the audit actions are generic and already keyed by object type. The gateway:
- refuses to submit without a primary file (A24), when checked out, when not the latest revision, and when a file of the iteration is quarantined (ADR-0026);
- moves In Work to Under Review to Released/In Work only through lifecycle transitions tagged `workflow`, in the engine's transaction;
- re-checks quarantine at release (the scanner can quarantine during a review);
- emits `document.released` with `via = workflow` inside the release transaction (the schema's `via` is now `admin | workflow`);
- tells watchers about entering and leaving review through the optional `INotificationSink` (as `DocumentAwareness` did for the manual transition);
- supplies the signed content: description, document type and every file (role, name, media type, size, SHA-256) of the bound iteration, so altering a file row after
  signing fails verification (GAP-C-2).

### 2. Migration 0017 (unavoidable)
The review transitions of `document-default` were `manual`. Without changing that data a document could still enter review without the engine, and the engine could not enter it.
The migration sets In Work to Under Review and back to `workflow` in every version of the document lifecycle family (the migrator is exempt from the published-version guard, as
0006 did for parts) and seeds template `document-release-review` v1 (technical reviewer, parallel approval, All rule, release; the part template's shape). No table, column or
privilege changes. Administrators keep the audited "set state" override; a workflow orphaned by it cannot be signed and is closed with Cancel.

### 3. UI and API
The document page gets a "Review & release" tab (`DocumentReviewPanel`, same behaviour as the part's: status, progress map, submit dialog, cancel, release history). The manual
"Move to Under Review" button is disabled with an explanation. `POST /api/v1/documents/{n}/review` and `GET /api/v1/documents/{n}/workflow` mirror the part routes. Task and
workflow pages link back to the document and say "document" in their messages.

### 4. Offline bundle: sources, SBOM, notices
`deploy/offline/third-party-sources.json` pins, by SHA-256, the corresponding source of every copyleft component (occt-import-js 0.0.23, OpenCascade at the commit its submodule
pins, and the npm package the shipped bytes came from) and lists components compiled into shipped files that no lock file names. `scripts/offline-compliance.sh` (called by
`package-offline.sh`) fetches missing archives once into a cache, verifies them, writes `sources/` and `SOURCES.txt`, runs `keel-sbom` (`src/Keel.SbomTool`, which reads only
repository files: NuGet restore results with their nuspec and licence files, the npm lock file, the vendored viewer files recomputed against `manifest.json`, fonts, image pins)
to write `sbom/keel-plm.cdx.json` (CycloneDX 1.5) and `THIRD-PARTY-NOTICES.md`, and runs the same bundle check the verifier uses. `--strict` fails on any gap. The files are inside
the signed `SHA256SUMS`. `scripts/verify-offline.sh` adds 21 checks (including every way of breaking the offer). The application image carries the viewer licence texts.

### 5. What is not decided here
Whether this arrangement satisfies the LGPL for this product, the offer wording and period, the supplier contact, and the GPL/LGPL operating system packages of the images (source only
if `--os-sources-dir` is given; otherwise the bundle says `NOT INCLUDED`). All are recorded as pending in `docs/third-party/legal-review.md`. Nothing claims sign-off.

## Consequences
- A document released before this migration keeps its state; new releases need a review with signatures. Sites that released documents by override can continue to use it.
- The document lifecycle can no longer be moved to review by the manual button; custom lifecycle versions that tag those transitions `manual` fail the gateway with a clear message.
- The release build needs `dotnet` and, on first use, the network (or a pre-filled source cache). The bundle build fails rather than ships without the source of an LGPL component.
