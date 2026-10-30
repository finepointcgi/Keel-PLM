# ADR-0052: Assembly placements from STEP and the Viewer tab

Status: accepted (increment 9.5, 2026-10-30). Builds on ADR-0004 (markings and access), ADR-0005 (audit), ADR-0014 (Blazor Server, strict CSP), ADR-0027 (file viewer),
ADR-0035 (CAD integration), ADR-0046 (model versioning) and ADR-0047 (open-source CAD connector, real STEP fixtures). Assumptions A67 to A69 in [../assumptions.md](../assumptions.md);
notes in [../increments/9.5-3d-viewer.md](../increments/9.5-3d-viewer.md). Migration 0031.

## Context
Keel read the *structure* of a STEP assembly (which product contains which, how many) but not *where* anything sits, and the 3D viewer lived only on a standalone page
(ADR-0027). Engineers want to look at an assembly from the document or the part they are on, see how it is built, and tell scripts and the CAD connector where each occurrence is.
The product is air-gapped, the markings must hold, and the application's strict Content-Security-Policy must not be loosened for a convenience.

## Decisions

### 1. Placements are read from the file, never guessed (A67)
`StepStructureReader` already walks `NEXT_ASSEMBLY_USAGE_OCCURRENCE`. In the same pass it now collects the entities that give an occurrence its position:
`PRODUCT_DEFINITION_SHAPE` (of the usage) ← `CONTEXT_DEPENDENT_SHAPE_REPRESENTATION` → `REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION` (simple instance, or the complex
instance `REPRESENTATION_RELATIONSHIP + REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION + SHAPE_REPRESENTATION_RELATIONSHIP`) → `ITEM_DEFINED_TRANSFORMATION(item_1, item_2)` →
`AXIS2_PLACEMENT_3D`. An axis becomes a 4x4 matrix as ISO 10303-42 says (z = axis, x = the reference direction made orthogonal to z, y = z × x; defaults per `first_proj_axis`).
The transformation maps the frame `item_1` onto `item_2`. Which of the two representations is the child's is **not** assumed: `SHAPE_DEFINITION_REPRESENTATION` ties each
`SHAPE_REPRESENTATION` to a product definition, and the relationship's `rep_1` and `rep_2` are matched against the parent and child definitions of the usage. Open CASCADE (so FreeCAD
and CadQuery) writes the child as `rep_1`; the opposite order is handled too (tested with a synthetic file). The result is the child-to-parent matrix `M(parent frame) · M(child frame)⁻¹`,
row-major, column vectors (`p_parent = M · p_child`), in the file's length unit. When the representations cannot be matched to the usage, or an axis is missing or degenerate, the
occurrence has **no placement** (`transform = null`): the viewer draws it at its parent's origin and says so, and the CLI prints "no placement". Mapped items
(`MAPPED_ITEM` + `REPRESENTATION_MAP`, which some exporters use instead of occurrence transforms) are not read; that is a documented limit.
The length unit is the file's `LENGTH_UNIT` (SI prefix or inch/foot); two different units in one file leave the unit unknown and nothing is converted.

### 2. Bounding boxes where derivable, and honest about what they are (A68)
For each product with its own shape representation the reader derives an axis-aligned box in the part's own coordinates from the B-rep topology (iterative walk, each entity once):
vertex points, the **exact** extent of a circular arc between its two vertices (including which way round it runs: `EDGE_CURVE.same_sense`), a whole ellipse, and the control
points of B-spline curves (an upper bound). Parts that are only assemblies have no own box; an assembly's box is the union of what it holds, computed when instances are expanded
(`AssemblyLayouts.Expand`: depth-first, no recursion, at most 5,000 instances and 30 levels). A file with more than three million geometry entities gets no boxes (and says so by
having none). The box is a viewing aid, **not a measurement**: surfaces that bulge beyond their edges (a sphere without circular edges) are not represented, and control points can
overstate a curve. On the real fixtures (bolt, nut, bracket, plate) the boxes equal the dimensions of the generating scripts.

### 3. Stored with the CAD structure; exposed through the CAD API and the CLI
Migration 0031 (forward-only) adds `cad.bom_proposal_occurrence` (proposal, sequence, parent and child part, occurrence id, name, `transform double precision[16]`),
`cad.bom_proposal_part.bbox` (6 numbers) and `cad.bom_proposal.length_unit(_mm)`. They belong to the proposal and inherit its markings (a caller who may not read the proposal gets
404 for everything); the existing `cad.guard_proposal` trigger freezes them with the proposal; the application role has no UPDATE or DELETE grant on the new table.
`GET /api/v1/cad/bom-proposals/{number}/placements` returns parts with boxes, occurrences with local matrices, and the expanded instances with world matrices and boxes
(audit action `cad.bom_proposal.placements`, a view). `GET /api/v1/cad/documents/{number}/placements` returns the same shape for the STEP file of a document, derived on request from the
verified stored bytes. The proposal detail gains `occurrences`, `placedOccurrences`, `lengthUnit` and per-part `box`. `keel-cad bom placements <proposal>` and
`keel-cad placements <document>` print a table of instances (or `--json`). The committed `cad-openapi.json` is regenerated.

### 4. Tessellation: the file viewer's path is kept, the tab does not tessellate STEP
STEP surface meshes still come only from the client-side OpenCascade WebAssembly importer of ADR-0027 (occt-import-js, LGPL-2.1, two unmodified files in a worker, replaceable, with
notices and the source-offer item that release engineering still owes). Nothing in this increment adds a second tessellator. A server-side converter was considered again and is
still rejected: every permissive-licence option (a pure .NET reader) does not exist for B-rep tessellation; every real option (OpenCascade, FreeCAD) is the same LGPL stack plus a
native runtime on a FIPS-mode host and a heavy request per view. So **the Viewer tab shows a STEP assembly as placed bounding boxes** with the structure list, and offers "Open in the
full file viewer" (a link) for the surface. This limitation is shown on the tab itself. STL, OBJ and 3MF get a real triangle view in the tab, from the existing access-checked
`/viewer/mesh/...` route (Keel's own readers with hard limits). glTF, GLB and IGES get the facts and the link.

### 5. Renderer and assets
three.js 0.186.1 (MIT) from the **same vendored bundle** the file viewer uses (`wwwroot/viewer/vendor/three-bundle.js`, hash in `viewer/manifest.json`). No new third-party asset
enters the repository; the tab's own code (`ModelViewerPanel.razor.js`, `model-viewer.css`) is ours. Nothing is fetched from a CDN. If the bundle is rebuilt, the tab follows.

### 6. Content-Security-Policy: nothing is widened
The tab is part of an ordinary Blazor page and runs under the **global** policy (`default-src 'self'`, no `script-src` widening). Its script is a same-origin ES module imported
through the normal collocated-JavaScript mechanism, uses no `eval`, no `new Function`, no WebAssembly, no worker and no inline style or script (every style is set through the CSSOM
or a stylesheet class). The viewer routes keep their own tested policies (ADR-0027); `ViewerCsp` is untouched. An E2E test asserts there are no console or page errors, which is where a CSP
violation would show.

### 7. Markings and audit
The Viewer tab reads a file only through `IViewableFileSource` (the documents module): Read **and** Download on the markings, SHA-256 verified read, quarantine and other view gates,
and a `document.view` audit event written before anything is returned (`viewerType model3d`, `handling assembly`, `STEP`), a denial audited as a denial. A caller who may not view the
document gets the same "not found" as for a missing one and **nothing** derived from the file, not even a part name; the service returns no bytes at all, only numbers and names
derived from the file. The page banner is the document's banner (on a part page, the union of the models listed and shown, merged like every other panel's banner). On a part page a model
the caller may not read is a placeholder that says only how many such models exist (the association graph already redacts them). The oracle test evaluates the system's own SQL read filter
for four subjects against proposals and documents with open and ITAR markings and requires the routes to agree with it, with no secret string in any refusal.

### 8. Accessibility and themes
The picture has a text alternative that is complete, not a summary: the structure list lists every instance with its position, and selecting one shows its placement, box and matrix.
The picture container is a focusable `role="application"` named "3D view" with the instructions in `aria-describedby`; arrow keys orbit, Shift + arrow keys pan, `+`/`-` zoom, `0` or `Home`
resets; the same actions are buttons with names. Colours come from the theme tokens (read from the computed style and re-read when the theme changes) and, under forced colours,
from the system palette; selection is shown by the list (`aria-pressed`) and the facts panel as well as by colour. axe runs on the tab in the light, dark and high-contrast themes.

## Consequences
- The CAD connector and scripts can read where every occurrence sits and how big each part is, from the proposal they already create, with no new write path.
- The tab costs one read of the STEP file per view (bounded by 2 concurrent reads, 64 MiB and the viewer's gates). Nothing derived is cached; the proposal store is the only persistence.
- A STEP file with several top-level products is refused by the tab with the reader's message (the proposal path takes a root number; the tab has no such input yet).
- Placement correctness is verified against Open CASCADE files (AP203, AP214, AP242) and synthetic variants; files from other exporters may use mapped items and then show "no placement".

## Alternatives
- Server-side STEP tessellation (OpenCascade, FreeCAD, assimp): rejected, see 4.
- A new WebAssembly module or worker for the tab: rejected; it would need the viewer's looser policy on a page that must keep the strict one.
- Putting the viewer in an iframe of the standalone page: rejected; `frame-ancestors 'none'` and `frame-src 'none'` are deliberate.
- Storing placements as JSON in the proposal row: rejected; one row per occurrence keeps them queryable, bounded and individually constrained.
- Treating `item_1` as always the child's frame: rejected; real files from other writers put the parent first and would be placed wrongly.

## Not in this increment
Mapped-item placements, surface meshes for STEP inside the tab, colours and PMI, per-instance transforms for JSON/CSV/XML structure files, a root-number input on the tab, picking by box in
the list for assemblies of more than 5,000 instances, a stored copy of derived placements for documents (the proposal store holds them for structures that were sent as proposals).
