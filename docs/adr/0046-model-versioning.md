# ADR-0046: Model versioning for CAD and mesh files

Status: accepted (increment 9.1, 2026-10-29). Builds on ADR-0002 (master / revision / iteration), ADR-0004 (markings and access), ADR-0005 (audit), ADR-0015 (vault),
ADR-0026 (scanning and quarantine), ADR-0027 (file viewer) and ADR-0035 (CAD integration). Assumptions are MDL-n in
[increments/9.1-model-versions.md](../increments/9.1-model-versions.md).

## Context
Documents already version as master / revision / iteration, but saving a new model means check-out, upload, check-in as three separate steps, and nothing tells an engineer what
changed between two versions of a model. Teams ask three questions all day: "what is the newest file", "what changed", and "can I get the old one back".

## Decisions
1. **A version of a model IS a document iteration.** No parallel store, no second "model version" table that could drift from the document. Every version therefore has the
   markings, read filter, release lock, audit trail, malware scan, quarantine and SHA-256 vault hashing of any document iteration. The model of an iteration is its primary file.
2. **Upload new version = check-out, replace the primary file, check-in, in ONE database transaction.** It reuses the bodies of the normal check-out and check-in (so iteration
   numbering, the primary-file rule and quarantine checks are the normal ones) inside the document's usual row-locked mutation. A failure rolls the whole step back, so no
   check-out is ever left behind. The bytes go through the normal upload path first (size limit, scanner, SHA-256, vault) and only after access, lock and check-out state have
   been checked. A required change note becomes the check-in comment. A file whose SHA-256 equals the current primary file is refused. `expectedIteration` (the version the person
   saw) makes concurrent uploads safe: the loser is told the document changed and nothing is written.
3. **A released revision needs "Revise".** Documents had no revise action (change management revises parts only). `DocumentService.ReviseAsync` creates the next revision from
   the latest iteration of a Released revision (iteration 1, initial state, same files and description, the old revision untouched). The upload dialog offers it as an explicit tick
   box and performs it in the same transaction. Revising is a direct, audited, reason-bearing action by anyone who may modify the document until documents join change control (MDL-3).
4. **Measured geometry, computed on the server, stored immutably.** A small in-house reader set in `Keel.Core.Geometry` (no third-party code, no recursion, hard limits) reads STL
   (binary and ASCII), OBJ and 3MF and `MeshAnalyzer` computes triangle count, distinct vertices, bounding box and dimensions, surface area, signed volume (only for a closed,
   consistently oriented mesh), watertightness and a geometry fingerprint. Results go to `mdl.iteration_metrics` (migration 0028, one immutable row per iteration; update and delete
   are rejected by trigger, and the app role has no UPDATE or DELETE grant; a trigger checks the measured file belongs to the iteration). Rows are written only by the documents
   module from bytes verified against their SHA-256, when a version is created through the new paths or lazily on the first view (at most eight per view), and reused for identical
   bytes. For STEP the in-house STEP structure reader of `Keel.Cad` supplies a product count through the `IStepProductCounter` port; for glTF/GLB/IGES and when the port is absent
   the history says "not analysed". Nothing is ever shown that was not computed.
5. **Geometry fingerprint.** SHA-256 over the multiset of triangles after snapping each corner to a grid of 1/100,000 of the model's diagonal, sorting the three corners and
   hashing each triangle; the per-triangle hashes are added lane-wise (so triangle order, corner order and winding do not matter) and finished with a tagged hash. A re-export of
   the same triangles with different bytes (binary versus ASCII, another float format, another order) has the same fingerprint and is reported as "same geometry". It is
   triangulation dependent (a quad split along the other diagonal is a different mesh) and a coordinate sitting on a grid line can flip, which yields a false "different", never a
   false "same" apart from a hash collision.
6. **Compare = numbers plus a picture.** The service returns a delta table (sizes, volume, surface area, triangles, vertices, with change and percent) and a plain-language summary
   built by `ModelDeltas` ("Taller by 2.0 mm, volume down 4.1 %"; words follow the format's up axis; noise below 0.05 % is not reported). The visual comparison is a standalone viewer
   page (`/viewer/compare/documents/{n}?a=&b=`, own CSP like the file viewer, both marking banners and the distribution statement from the document's markings) with overlay (A blue,
   B orange, transparency slider), side by side with one shared camera, and a swipe divider. The browser gets plain float32 triangles from `/viewer/mesh/...`, which runs through the
   same `ViewerService` path as file views: Read and Download, the integrity-verified read, size limit, gates, concurrency bound and a `document.view` audit event. Both models are
   shown in the coordinates of their files: **there is no automatic registration** and the page says so.
7. **Restore** creates a NEW iteration whose files and description equal the chosen one, change note "Restored from A.2", through the same check-out and check-in bodies in one
   transaction. History is never rewritten. It is refused for the current content, a locked revision, a checked-out document, a stale view and a quarantined version.
8. **Quarantined versions disappear.** A version whose primary file is quarantined is not listed, not counted, and cannot be compared, restored or downloaded. A document disposed of
   under a retention schedule shows no history. A caller who may not read the document gets the same 404 as for any document and the denial is audited.
9. **Audit.** `document.version_upload`, `document.version_restore`, `document.revise` (state-changing, shown in the activity feed), `document.model_history`,
   `document.model_compare`, `document.versions_download` (views). Payloads carry version labels, checksums and the change note, never geometry values or fingerprints.

## Consequences
- The feature needs no external service: the Cloud Run demo gets it unchanged, and the demo seed creates two synthetic STL models with two versions each, generated in code.
- Metrics from an older analyser stay as they were (the row records its analyser version); a new analyser version measures new or uncached versions again.
- STEP and glTF versions can be listed, downloaded, compared as files and restored, but numeric and visual comparison say plainly that they are not measured.
- The visual compare holds both meshes in the browser (limit one million triangles per side) and shows no burned-in screenshot (not built).

## Alternatives
- A separate model-version store with its own file table: rejected; it would duplicate marking, audit, quarantine and vault rules and drift.
- Snapshot metrics client-side in the browser: rejected; numbers must be computed by the server from verified bytes and be the same for everyone.
- A third-party mesh library (assimp, trimesh via a sidecar, CGAL): rejected for licence and the air-gap requirement; the readers are small and have hostile-input tests.
- Welding vertices by exact coordinates only: rejected; it makes the fingerprint depend on the exporter's last digit.
