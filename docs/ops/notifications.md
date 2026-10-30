# Operating notifications, watch lists and comments

Design: [ADR-0030](../adr/0030-notifications-activity-comments.md) and, for e-mail, [ADR-0032](../adr/0032-platform-hardening.md). In-app by default; an optional e-mail channel over an on-premises SMTP relay is described below.

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

## E-mail channel (`Keel:Notifications:Email`)
Off by default. A mail says only "You have a notification in Keel PLM" and links to `/notifications`; it never names an object, kind, person or marking. Users opt in on
`/notifications/preferences` (their own address, a switch, and a per-kind column). Nothing is sent to anyone who did not, **and nothing is sent to an address the user has not confirmed** with a one-time link ([email-verification-and-bounces.md](email-verification-and-bounces.md), which also covers bounce handling and the suppression list).
| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | false | Turns the channel on. Then `Host`, `FromAddress`, `BaseUrl` and `AllowedRecipientDomains` are required, or startup fails. |
| `Host` / `Port` | empty / 587 | The on-premises relay. Only loopback, private (RFC 1918, unique-local, link-local) addresses are used, unless the name is in `AllowedHosts` or the address in `AllowedNetworks`. |
| `Security` | `StartTls` | `StartTls`, `ImplicitTls` (port 465) or `None`. `None` is refused unless `AcknowledgeInsecureTransport=true`. A relay that does not offer STARTTLS is refused, never used in clear text. |
| `Username` / `Password` | empty | Optional AUTH PLAIN or LOGIN, sent only after TLS. Provide the password from the secrets directory (`Keel__Notifications__Email__Password`). |
| `CaBundlePath` | empty (system store) | PEM file with the CA that signed the relay's certificate. Mount it read-only. TLS 1.2 or 1.3 only; no revocation lookups (air gap). |
| `FromAddress` / `BaseUrl` | empty | Sender, and the address users reach Keel at (for example `https://keel.corp.example`). |
| `AllowedRecipientDomains` | empty (required) | Domains users may receive mail at: exact or `*.suffix`. Users type their own address; the user must then confirm it by link before any mail goes to it. |
| `MinIntervalMinutes` | 10 | At most one mail per user in this time, however many notifications arrive. |
| `MaxAttempts` / `TimeoutSeconds` / `PollSeconds` | 6 / 20 / 10 | Retries (backoff 1 min doubling to 1 h), connection and answer timeout, queue polling. |

Operation: mails wait in `ntf.email_queue` (one pending row per user; any node may send; rows older than 30 days that are finished are removed). Audit actions: `notification.email.sent`,
`notification.email.failed` (user id and attempts only) and `notification.email.preference` (the switch, never the address); verification and bounce actions are listed in [email-verification-and-bounces.md](email-verification-and-bounces.md). Logs name the relay's answer with addresses removed.
A stuck queue shows as pending rows with a growing `attempts` and a `last_error` such as "does not offer STARTTLS" or "certificate". Typical fixes: the CA bundle, the relay name in the
certificate, `AllowedHosts` for a relay outside the private ranges.
