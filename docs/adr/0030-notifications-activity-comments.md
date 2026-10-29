# ADR-0030: Notifications, watch lists, comments and the activity feed

Status: accepted (UX-4, 2026-10-29). Builds on ADR-0004 (access and markings), ADR-0005 (tamper-evident audit), ADR-0011 and ADR-0025 (outbox), ADR-0018 (workflow),
ADR-0019 (change), ADR-0026 (idle lock) and ADR-0028 (shell and personal workspace data).

## Context
People had to hunt for what changed and what needed them: tasks lived on one page, decisions on another, and nothing told a reviewer that an object they care about
moved. The product holds export-controlled and CUI data, so every new surface that names an object (a bell, a feed, a comment, a suggestion list) is a new place
where data could reach someone who may not read it. The constraints did not change: no outbound network (so no e-mail yet), strict CSP, an idle lock that removes
page data from the DOM, access decided by `AccessPolicy` on the object's markings, and an audit log that never holds marked text.

## Decisions

### 1. Notifications are pointers; the text is rendered after a read check
A row of `ntf.notification` holds the recipient, a kind code, the object's type and number, an optional reference id (a comment id or workflow task id), the actor's
subject, a counter and three timestamps. It holds no title, name, description, markings or sentence, and a test reads the migration to keep it that way. At read time
`NotificationCenter` finds each object through the owning module's `IObjectAccess` (a key lookup returning name, state and markings) and applies `AccessPolicy.Evaluate`
to the VIEWER's live attributes. A viewer who lost access (markings raised, programme left, role removed) gets a neutral row: "A notification you can no longer view",
with no number, name, kind, actor, link, banner or group. Unread counts include such rows once each (a count of rows names nothing); the Mentions, Tasks and Watching tabs
leave them out because a kind tab would reveal the kind. The kind registry (`NotificationKinds`) is data: code, group, label, sentence, whether it can be turned off, whether it
collapses, whether it is a decision, priority. Adding a kind is adding a row and a generation point.

### 2. Generation: in the owning transaction, and from the outbox
- `INotificationSink` (Keel.Core) is called inside the transaction of the step that causes the notification, like `IDomainEventSink`: a rolled-back step leaves nothing.
  Hooks (new `*Awareness` files in each module, plus one-line calls): Workflow (task assigned or review requested, decision for the submitter, auto-watch on sign, reassign),
  Change (change task assigned, decision on a request for the requester), Parts and Documents (auto-watch on create and check-out; watchers told of a new version and of a
  state change). Each call runs under a savepoint: a database error in the notification code is logged and rolled back to the savepoint, so it cannot fail a release.
- Releases, obsolescence, approvals, baselines and manufacturing BOMs reach watchers through the outbox, not through hooks: `FanOutService` (hosted) reads committed
  events in commit order from its own cursor (`ntf.fanout_cursor`, `(created_in_tx, seq)`, with the same visibility rule as the pull API), so an event of a rolled-back change
  never exists and one that commits late is not skipped. Rows and cursor advance in one transaction; `(recipient, kind, event_id)` is unique, so reprocessing is idempotent.
  There is exactly one source per event (hooks cover what is not an outbox event; the outbox covers the rest).
- EVERY recipient is checked by `NotificationWriter` against the markings the event carries, using the attributes `identity.user_directory` holds for them (the snapshot
  the workflow module uses for participant eligibility, A50). A user the directory does not know gets nothing (fail closed); a user who lost the programme since they started
  watching gets nothing. The row is checked again with the live subject when it is shown (decision 1). Both checks are needed: the snapshot can be older than the person's token.
- Nobody is told about their own action unless they set "also my own actions" for that kind (default off). Kinds that need action (tasks, reviews, change tasks, mentions)
  cannot be turned off. Repeats of a collapsible kind (new version, state change) inside five minutes update one unread row's counter; a row the user read is never reopened.
  The newest 500 per user are kept; an optional hosted job purges read notifications older than N days (off by default).

### 3. The bell and realtime
The bell lives only in the unlocked branch of the layout, so a locked session has no bell and no notification text in the DOM; a call that still arrives is refused by
`CurrentUser`, and the bell clears. It subscribes to an in-process per-user hub (`NotificationHub`, singleton, content-free): after a notification is written the hub signals
the user's circuits, which re-query after a 250 ms debounce. A 60-second poll is the fallback. With several nodes a circuit only hears about notifications written on its own
node, so sticky sessions plus the poll keep the badge correct within a minute (docs/ops/notifications.md). The badge is decoration; the button's accessible name carries the count
and a visually hidden polite live region announces an increase without moving the focus.

### 4. Watch lists
`ntf.watch` is a pointer with a level (`all` or `decisions`). Setting a watch needs read access (a refusal is audited and looks like "not found"). Objects the user creates,
checks out, signs or comments on are watched automatically (preference, default on). The watch list is re-authorized on every read; an object the user can no longer read is a
row with no name that can still be removed or re-levelled through an opaque row id.

### 5. The activity feed comes from the audit log
`ActivityService` first resolves the object through `IObjectAccess` and evaluates the viewer's read access (a refusal is audited as `activity.read` Denied and looks like not found),
then reads only the audit rows of that object (`object_type`, `object_id`, through the existing `event_object_idx`) whose action is in the catalog and whose outcome is Success.
`ActivityCatalog` is a data table: action to sentence, safe detail fields, category; views and downloads are listed separately and shown only to an auditor who asks (that is
itself audited); everything else is listed as hidden with a reason. A unit test scans the sources for audit actions and fails when one is in none of the three lists. A detail that
names another object is a link only if the viewer can read that object. Markings changes show the object's own banner before and after, never the field description. No new index
was added (EXPLAIN on 100,000 events: index scan on `event_object_idx`, 0.04 ms). Viewing the feed is not audited for ordinary viewers (viewing the object already is).

### 6. Comments inherit the object's markings dynamically
`ntf.comment` has no marking columns. Every read and write starts with a read check of the object under the caller's live attributes, so the comments follow the object when its
markings change, and a missing and an unreadable object look the same. Writing also needs a role of viewer or higher (auditors and service accounts read only). Bodies are plain
text (at most 4,000 characters, no control characters), stored exactly as written and rendered only as encoded text (segments of text, `@login` and object numbers; no markup
anywhere). An object number becomes a chip only when the viewer can read that object. Comments are immutable: a database trigger refuses every UPDATE except a one-time deletion
marker and refuses DELETE and TRUNCATE for every role. The author or an administrator can remove a comment with a reason; the body stays in the table, the UI shows a tombstone,
an auditor can read the text. The audit events `comment.create` and `comment.delete` (same transaction) carry the comment id and the object pointer, never the text or the reason.
Limits: 2,000 comments per object, 10 per user per minute (counted in the table, so it holds across nodes), one level of replies (also enforced by a trigger).

### 7. Mentions
`@` offers directory users whose stored attributes would let them read THIS object (role and policy), so the author is never told who is outside the audience. A mention
notifies only if the person is still eligible when the comment is written. Mention outranks every other kind for the same person and comment; replying also tells the author of the
thread; watchers at level "all" hear plain comments. The suggestion list follows the trailing `@token` of the text (no caret tracking).

### 8. Out of scope
E-mail and other channels: delivered by ADR-0032 (pointer-only e-mail over an on-premises relay; the Phase-5 stub is gone). Push to other nodes (a broker) is not built; the poll covers it.
A notification digest, per-object mute and edit of comments are not built.

## Consequences
- A new module `Keel.Notifications` (references Core, Security, Audit only) and migration `0015_awareness.sql` (schema `ntf`). Owning modules gained optional constructor parameters
  and small `*Awareness` helpers; none references the new module. `Keel.Web` composes.
- The audit log gained the actions `comment.create`, `comment.delete`, `activity.read` and `watch.set`/`comment.read` denials, and the catalog test keeps future actions honest.
- Counts of unread notifications can include notifications whose object the user can no longer read (once each). That reveals only that something once reached them.
- The fan-out is a second reader of the outbox besides the dispatcher; it has no end user and checks every recipient, which an architecture test pins.
