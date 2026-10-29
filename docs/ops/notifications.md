# Operating notifications, watch lists and comments

Design: [ADR-0030](../adr/0030-notifications-activity-comments.md). In-app only: there is no e-mail channel until Phase 5.

## Configuration (`Keel:Notifications`, environment `Keel__Notifications__...`)
| Setting | Default | Meaning |
|---|---|---|
| `MaxPerUser` | 500 | Newest notifications kept per user; older ones are dropped when a new one is written. |
| `DedupeWindowMinutes` | 5 | Repeated "changed" / "changed state" notifications for one object inside the window update one unread row's counter. 0 turns collapsing off. |
| `RetentionDays` | 0 (off) | Read notifications older than this are purged by the hosted retention job. Unread ones are never purged by age. |
| `PurgeIntervalHours` | 6 | How often the retention job runs (only when `RetentionDays` > 0). |
| `FanOutEnabled` | true | Runs the outbox fan-out in this process. Several nodes may run it: the cursor row is locked, so each event is handled once. |
| `FanOutPollSeconds` / `FanOutBatchSize` | 3 / 200 | Polling interval and batch size of the fan-out. |
| `MaxWatchesPerUser` | 500 | Watch list size. |
| `MaxCommentsPerObject` / `CommentsPerMinutePerUser` | 2000 / 10 | Comment caps (counted in the table, so they hold across nodes). |
| `PublishDelayMilliseconds` | 400 | Delay before the bell is signalled, so the writing transaction has committed. |

## Retention
Notification rows are low-value personal pointers (no names or text). Without `RetentionDays` the per-user cap bounds the table. Comments are never purged (immutable,
audit-relevant); the application role cannot delete them. Back up the `ntf` schema with the database; restore needs no special handling. The fan-out cursor
(`ntf.fanout_cursor`) starts at the end of the outbox when migration 0015 runs, so history is not replayed; after restoring an older database it replays from its own position
(idempotent: a unique key per recipient, kind and event).

## Realtime, polling and several nodes
The bell listens to an in-process hub: a notification written on this node refreshes the badge of the user's circuits within about a second. With several nodes, a
notification written on another node is picked up by the bell's 60-second poll (the poll is always on). Sticky sessions (already required, ADR-0014) keep a circuit on its
node; no broker is needed. Notifications written by the outbox fan-out are signalled on the node that ran the fan-out.

## Monitoring
- `ntf.fanout_cursor.updated_at` should keep moving while events are produced; a stalled cursor means the fan-out is not running or an event keeps failing (see the
  `Notification fan-out failed` warning in the log; it retries from the cursor).
- A `notification could not be written` error in the log means the savepoint around a notification write rolled back; the change it reported was not affected.
- Audit actions: `comment.create`, `comment.delete` (comment id, never text), `activity.read` (auditor views, and denials), `watch.set` and `comment.read` denials.

## Privacy notes for administrators
Notification and watch rows never hold object names or text. Comment bodies are marked data: they live only in `ntf.comment`, follow the object's markings, and are
not in the audit log, exports or technical data packages. An auditor can read removed comments. Database administrators can read comment bodies; treat the table like the part tables.
