# ADR-0061: Simplified flows: quick add, one-step edit and add document, Simple view

Status: Accepted

## Context
Users found the application too complicated. Adding a part showed every field at once (name, type, unit, source, description, all attributes, ten markings fields). Updating a part meant Check out, Edit working copy, Save working copy and Check in, with terms nobody outside PLM uses. A document could be linked to a part only from the Documents tab of a part that was already checked out, and the new document form had no link at all. The navigation rail offered a dozen groups to everybody.

## Decision
- **No rule changes.** Markings and access control, release locks, audit entries, iterations and e-signature workflows are untouched. Every simplified action is a chain of the same module calls the advanced controls make, so the audit trail and the refusals are the same. A chain that fails halfway gives back what it took (an undo of the check-out it made), so it never leaves a part locked to somebody who did not ask for it.
- **The chains live in `Keel.Web/Services/SimpleFlows`**, not in a module, because a module may reference only `Keel.Core` (the parts and documents modules cannot call each other). `SimpleFlows` contains no rule: it reads the part, explains in a plain sentence why an action is not possible (`Advise`, `LinkBlocker`) and otherwise lets the module decide and audit. The module's answer is what the person reads.
- **Quick add part**: the name is the only thing asked for. Type, unit and source start from the person's last choice, then the common default, then the first item of the administrator's list. Markings start from the person's own program and the standard settings, shown as one chip with a Change button; they are validated exactly as before. Attributes marked required by the administrator are asked for on the form; the rest sit under "More details". Errors are plain sentences next to the field. "Create and add another" keeps the form open.
- **Edit and Save**: Edit does check-out (or continues an existing own check-out); Save does save working copy and check in with an optional change note; Discard changes is undo check-out, after a confirmation. The advanced step-by-step controls stay under **More**. A released part shows one line saying it needs a change order, with a Start change button that opens a change notice with the part filled in.
- **Add document**: from the part page one dialog attaches an existing document or creates a new one. If the person is not editing the part, the part is checked out, linked and checked in with a note naming the document; if they are editing, the link joins their draft. `DocumentNew` gets an optional "Link to part" picker (parts the person can read; parts they cannot change are listed with the reason and cannot be chosen) and checks the part first so no document is created for a part that would refuse the link.
- **Language**: "working copy", "check out" and "iteration" remain where the architecture fixes them, each with an inline sentence (`GlossaryHint`), never a tooltip. Create actions are named Create part and Create document everywhere (Parts list, Home, command palette).
- **Simple view** (a display preference, `ws.preference.simple_view`): the rail hides Programs, Quality, Sourcing, Output and most of Product data and Tools. Role-gated groups (Records, Configuration, Administration, the audit log) are untouched. Nothing is decided from it: every hidden place stays reachable by search and the command palette, and every page and service still checks the role. Groups were already collapsed except the one holding the current page and Work, so that part needed no change.
- **Data**: migration 0040 adds `ws.preference.simple_view` and `ws.last_choice` (user, key from a closed list, a code from a list). Neither holds object data or markings.

## Consequences
- The step-by-step controls moved under More; E2E tests that click Check out or Check in open the menu first (`BrowserSession.OpenMoreAsync`).
- Part creation defaults the program to the person's own. A person with no program gets a part without a program, never an invented one.
- The one-step add-document flow creates a new version of the part (it is a check-in). That is the intended record; it is the same entry a manual link would have produced.
- Assumptions A73-A75 in `docs/assumptions.md`.
