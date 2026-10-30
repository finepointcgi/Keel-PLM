# ADR-0056: Workflow designer in administration and phone approvals

Status: Accepted

## Context
Workflow templates have been versioned data since ADR-0006, and an administrator could already list, validate, publish and pin them (increment 2.2). Editing meant node ids typed as comma-separated text and a JSON view. There was no way to see what a template would do before publishing it, and no place to keep a half-finished definition. Approvers could only decide a task on the desktop task page, a table-and-history page that is hard to use on a phone, and nothing on it stated in one place exactly what a signature binds to.

## Decision
**No new engine features.** The engine's node types stay Task, Serial, Parallel (with a vote rule) and End; assignment stays "the user picked for a group when the review starts" or "anyone holding a role". Conditions (`Condition`, `SetState`, `Notify` nodes) remain rejected by the parser as "not supported yet", and a fixed named user in a template is not an engine concept. The designer exposes exactly what the engine runs (assumption A82).

**Designer**
- The workflow templates page lists every workflow with its running reviews per version, saved drafts, and the pins. Its default editor is a **Steps** view: an ordered list of steps, then the release. A step is *one person* (a one-person group filled in when the review starts), *several people* (a group with a minimum and maximum, deciding by All, Any or N of them) or *anyone holding a role* (a pool task), each with a name, review or approval, an optional due time in days, and a read-only line naming the signature meanings that kind of task signs with (read from `wf.reason_code`, not typed). Steps move up and down with buttons, so the editor is keyboard operable without drag and drop.
- `Keel.Admin.StepsDraft` converts steps to and from the engine's JSON. A definition that is not a plain list of steps (nested blocks, fixed branches, a group shared by two steps) opens in the existing Advanced nodes editor or the JSON view with the reason; nothing is lost either way, and the three views convert through the same JSON.
- **Validate** is the engine's own parser and its messages; **Publish** is the audited upload that already existed (`workflow.template_upload`).
- **Preview** draws the definition with the existing workflow map (`WorkflowMapView`): `WorkflowService.PreviewTemplateMapAsync` builds the same stages from the parsed template with every step Waiting, each group labelled "Chosen when the review starts: <group>", and a fan-out shown with as many placeholders as the group's minimum. It stores nothing and is for administrators only.
- **Drafts** (migration 0035, `wf.template_draft`): at most one saved, possibly invalid definition per template id, with the published version it started from. A draft is never a version and never starts a review. Saving and discarding are audited (`admin.workflow_draft_save`, `admin.workflow_draft_discard`); publishing deletes the template's draft in the same transaction as the new version, and a refused publish keeps it.
- **Versions** need no new mechanism: an instance copies its definition and version at start (migration 0006), so a running review keeps the version it started with. The list now shows how many reviews still run on each version, and the publish confirmation says how many will keep the current one (A83).

**Phone approvals**
- `/approve` lists the caller's open tasks as large cards (the same `MyTasksAsync` call, redaction and audit as My tasks); `/approve/{id}` is the review summary and the decision. My tasks links to it ("Phone view").
- The review summary states, before any button: the kind of item, number and name, revision and version, state, the markings chip (and the marking banners, as on every page), who started it and when it is due, the submitter's note, the description and attributes, and the **content hash** the signature will record. The hash is computed by `SignatureHasher.ContentHash` over the same stored iteration and live markings the signing step uses, so what is shown is what is signed; a test signs and compares the stored hash with the one shown.
- Approve, reject and approve-with-comments are the existing reason codes of the task, shown as full-width buttons at least 56 px high at the bottom of the page, under a comment box. The same `WorkflowService.SignAsync` signs: step-up, separation of duties, role and marking checks and audit are unchanged. After step-up the person returns to `/approve/{id}` with the comment kept in server memory (never in the URL).
- A reader who lost access to the item gets the redacted card and no summary, no hash and nothing that names the item (A84).
- No sticky or floating bar: a fixed bar would cover the bottom marking banner and the content at 320 px reflow. Both screens pass axe at 375 px and do not scroll sideways.

## Consequences
- `IWorkflowTemplateAdmin` gained draft and signature-meaning methods; `TemplateVersionInfo` gained `RunningReviews`; `MapTask` gained an optional `SlotLabel`; `TaskDetail` gained `Review`. All additions are optional or additive.
- Admin audit actions use the `admin.` prefix and are hidden from the activity feed by the catalog's prefix rule.
- Assumptions A82-A84 in `docs/assumptions.md`.
