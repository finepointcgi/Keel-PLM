# ADR-0043: Program and project management: program master, schedule, deliverables and gate reviews

Status: accepted (increment 8.8, 2026-10-29). Builds on ADR-0004 (markings and access policy), ADR-0005 (tamper-evident audit), ADR-0018 (review and e-signature),
ADR-0019 (marking dominance), ADR-0025 (outbox and events), ADR-0029 (relationship pictures), ADR-0030 (notifications, activity and comments) and ADR-0031 (sort whitelists).
Assumptions are FEAT-PRG-n in [increments/8.8-programs.md](../increments/8.8-programs.md). Interface: `docs/programs/openapi.json`.

## Context
Until now a program was only a marking: `markings.program` on an object, and `programs` in the claims of a user. Program managers need the program as a thing with a plan: what is
due when, what depends on what, what has slipped, which Keel objects are the deliverables and whether they are ready, and the design reviews (PDR, CDR and the like) that gate the work.
The danger is the obvious one: a plan that lists names, dates and states of marked objects becomes a second, unmarked copy of the product data, and a program record that can
add members becomes a way around the access policy.

## Decisions

### 1. The program record is marked with its own code and never touches membership
`Keel.Programs` (schema `prg`, migration `0026_programs.sql`) references Core, Security and Audit only. A program row has a `code` that **is** its `markings.program` (a CHECK
constraint: `program = code`), so reading the page is the ordinary read filter: only members of the program see it. Membership stays with the identity provider and the policy
(ADR-0004): the module never writes `identity.user_directory` or any claim, a creator must already be allowed to modify an object with these markings (which needs the program),
and a program manager, an item owner or a deliverable owner must be able to read what they are assigned (a person who could not is refused, nobody is "made a member by being named").
Creating or editing a program, an item or a deliverable never grants or widens access.

### 2. Items carry their own markings, covering the program's
Phases, milestones and tasks are rows with marking columns. Their markings must cover the program's (`MarkingDominance`), so an item is at least as restricted as its program and
can be stricter (a restricted task in an open program). Dependencies, checklist rows, baselines and the slip log carry no markings: they are child rows, read only through readable
parents. A reader sees only readable items; for an item they cannot read they get a **placeholder** with its time span and its position, and nothing else (no key, title, owner,
state, gate or link), the same rule as the timeline of ADR-0029. Placeholders are counted nowhere: dashboard numbers, percentages and the critical path are computed over what the
reader can read (the SQL read filter), and the SQL-oracle tests compare them with the in-memory policy for nine probe users.

### 3. The schedule is pure, deterministic arithmetic
Finish-to-start dependencies (with a lag in days, loops refused by the service and by a database trigger) over tasks and milestones. `ScheduleMath.Compute` runs the critical path
method with a status date as an input (no clock inside): a completed item is fixed at its actual dates, a started one cannot finish before the status date, one not started cannot start
before it, and the planned start is a "no earlier than" constraint. It returns forecast dates, float, the critical items and the forecast finish; ties break on (planned start, key),
so the same input gives the same answer (tested, including input order). Percent complete rolls up by duration. Baselines are immutable snapshots of planned dates and ids only (no
text copied); slip is the distance of the planned (or forecast) end to the baseline end, and moving an end later without a reason is refused and logged (`prg.schedule_change`,
immutable).

### 4. Deliverables are typed pointers; the state is read live
A deliverable stores an object kind (part, document, change, baseline, requirement specification, NCR, CAPA, deviation, MBOM), the object's business number and a due date.
Its readiness is derived at read time from the lifecycle state of the object, through the owning module's key lookup (`IObjectAccess`, Keel.Core) and the policy on the object's
markings for **the reader**: released or approved means ready, review states mean in review, obsolete or cancelled means ended, anything else is in work, and an object the
reader cannot read (or that does not exist) is "not visible to you" with nothing else shown. Nothing of the object is stored, so the program can never be a second copy.

### 5. Gate reviews are milestones approved through the workflow engine
A milestone may be a gate review (SRR, PDR, CDR, TRR, FRR, PRR or other) with a number `GR-nnnnnn` and a checklist. Submitting is a workflow review (object type `gate`, template
`gate-review-approval`, all approvers sign with a fresh sign-in): submitting freezes a text snapshot (the gate, its checklist and the readiness of its deliverables as the submitter
saw them) whose hash is part of every signature. A database trigger refuses `Approved` without an approving signature in `wf.signature`; approval completes the milestone and writes the
additive outbox event `program.milestone_reached` in the same transaction (markings of the milestone). A completed milestone completed by hand writes the same event once.

### 6. Awareness, search and integration reuse what exists
`program` and `gate` are awareness kinds (watch, comments, activity); notifications for a milestone that is due soon or overdue are system notices raised by a maintenance job once
per milestone, to its owner, pointer-only (kind, program code, an opaque id). The program has an `ITimelineSource`, a search prefix (`pg:`), palette and navigation entries, sort
whitelists, a REST API with its own OpenAPI document, and audit events that carry keys, states and counts, never titles or descriptions (an architecture test checks the statements).
The visuals are server-computed SVG in the shared frame (keyboard: arrows between rows and along dependencies, a table with the same facts, a key of shapes and patterns).

## Consequences
- A plan that names restricted work shows placeholders to people who cannot read it; the shape of the schedule (dates) is visible, which is the accepted trade-off of the timeline (FEAT-PRG-4).
- Critical path and slip are the reader's view. A manager with narrower access sees a different path than one with wider access; a system-wide path would reveal hidden dependencies.
- Readiness labels are the default lifecycle labels (FEAT-PRG-6); administrators who rename states change what "ready" means for that kind.
- Not built: resource loading, cost, calendars (all days count), other dependency types, a real-time Gantt editor.
