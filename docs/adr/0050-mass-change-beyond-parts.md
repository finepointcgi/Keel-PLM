# ADR-0050: Mass change beyond parts: documents, assembly structure, requirements, change notices and files

Status: Accepted. Migration `0029_mass_change_ext.sql`. Extends ADR-0044.

## Context
ADR-0044 shipped mass change for parts with preview, per-object apply, undo and one audit trail, and said other kinds join by registering another `IMassChangeTarget`.
Engineers also need to change one component on many assemblies (found through where-used), set document and requirement fields in bulk, tie the work to a change notice, and drive it from a spreadsheet.

## Decision
1. **Same safety model.** New targets in `Keel.Api/Infrastructure/Reports`: `MassDocumentTarget`, `MassAssemblyTarget`, `MassRequirementTarget`. Each change is the owning service's own command (document check-out/edit/check-in, `ICadPartPort.ApplyBomAsync`, `RequirementService.EditAsync`), so release locks, check-out ownership, markings authority and audit are the normal ones. Unreadable and missing objects get identical words and no name, banner or state.
2. **Assembly structure.** Operations add, replace and remove one component; the object is the assembly. Where-used (`MassSelectors.WhereUsed`, one level, read-filtered) fills the selection; hidden parents are neither offered nor counted. Undo re-applies the recorded lines.
3. **Change notice.** `MassRequest.ChangeNumber` is checked by `IMassChangeBinder` (ECN, caller may modify, editable). The service alone writes the reserved `_change` parameter (any caller value is removed). Parts and assemblies only. A Released object is revised through the notice before it is edited; others are listed as referenced.
4. **Files.** `MassUpload` reads CSV or the first sheet of an .xlsx (existing reader limits). A fixed column whitelist; unknown columns are refused; one row per object; row values override the shared ones (`rpt.mass_change_item.item_parameters`). The file is not stored; name and SHA-256 are audited (`output.mass.upload`).
5. **Record.** Migration 0029 widens the operation check, adds the kind check, `change_number`, `source`, `source_detail` and per-item parameters. New audit actions `output.mass.select` and `output.mass.upload` are under the hidden `output.` prefix of the activity catalog.
6. **REST.** `GET /mass/select`, `POST /mass/upload`, `GET /mass/template.csv`; `changeNumber` on preview. OpenAPI document regenerated.

## Consequences
Caps unchanged (500 objects, 20 operations per hour; an upload counts as one). Assembly and requirement previews read through audited service calls (one per assembly, one per specification). Documents and requirements cannot be listed on a change notice until the change module gains gateways for them.
