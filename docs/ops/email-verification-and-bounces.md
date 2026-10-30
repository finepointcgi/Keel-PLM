# E-mail address verification and bounce handling

Design: [ADR-0055](../adr/0055-email-verification-bounces-and-broker-sink.md). Applies when the e-mail channel is on (`Keel:Notifications:Email:Enabled`, see [notifications.md](notifications.md)).

## What users see
1. On **Notifications > Preferences** a user saves an address and switches e-mail on. The page sends a confirmation link to that address at once and says "We sent a link".
2. The mail contains one link, `https://<your Keel>/notifications/verify-email/<token>`. Opening it asks the user to sign in and press **Confirm my e-mail address**.
3. Until then **no notification mail is sent**. After confirming, the usual one-line "You have a notification" mail (unchanged, pointer only) is sent.
4. Changing the address un-confirms it. A link works once, for the user it was made for, for 24 hours, and stops working when a newer link is requested or the address changes.
Every failure of a link (wrong, used, expired, someone else's, replaced) shows the same message, so a link tells an outsider nothing.

After upgrading to migration 0034, users who had e-mail on **must confirm once**; until they do they are mailed nothing. Tell them (or ask them to open their preferences).

## Settings (`Keel:Notifications:Email:Verification`, environment `Keel__Notifications__Email__Verification__...`)
| Setting | Default | Meaning |
|---|---|---|
| `LinkMinutes` | 1440 | How long a link works (10 to 10080). |
| `MinIntervalSeconds` | 60 | Shortest time between two links for one user. |
| `MaxPerUserPerDay` | 5 | Links one user may request in 24 hours. |
| `MaxPerAddressPerDay` | 3 | Links that may be sent to one address in 24 hours, whoever asks. Over the limit the caller still sees "a link is on its way" and nothing is sent. |

## Bounce handling (`Keel:Notifications:Email:Bounces`)
| Setting | Default | Meaning |
|---|---|---|
| `HardBounceThreshold` | 2 | Hard bounces after which an address is suppressed (1 to 10). A complaint suppresses at once. Soft bounces and "mailbox full" (5.2.2) never suppress. |
| `MailboxDirectory` | empty (off) | Directory where the relay delivers bounce mail for Keel's sender address (a Maildir; both the directory and its `new/` are read). Needs `TagSecret`. |
| `MailboxPollSeconds` / `MailboxSettleSeconds` | 30 / 5 | Polling interval; a file is read only after it has not changed for this long (use 0 with an atomic Maildir). |
| `TagSecret` | empty | At least 32 characters, from the environment or the secrets directory (`Keel__Notifications__Email__Bounces__TagSecret`); never stored in the database. When set, every mail Keel sends carries `X-Keel-Bounce-Tag`, an HMAC of its recipient. |

A suppressed address gets nothing from Keel, including confirmation links. Only addresses that belong to a user (or have a recent confirmation link) are recorded; reports about any other
address are dropped, so these tables cannot be filled from outside. Old reports are purged after 90 days and confirmation rows after 7 days; suppressions are kept.

### Reporting a bounce: the REST endpoint
`POST /api/v1/notifications/email/bounces` with a bearer token of a **service account that has the role `plm-integration`** (the same account type as the ERP pull API; client credentials
against the identity provider). Maximum 100 reports per request.
```
curl -sS -X POST https://keel.corp.example/api/v1/notifications/email/bounces \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"reports":[{"address":"jo@corp.example","kind":"hard","status":"5.1.1","diagnostic":"user unknown"}]}'
```
| Field | Meaning |
|---|---|
| `address` | The recipient that bounced. |
| `kind` | `hard` (permanent failure), `soft` (temporary failure or delay) or `complaint` (the recipient reported abuse). |
| `status` | Optional enhanced status code, for example `5.1.1`; stored for the administrator. |
| `diagnostic` | Optional text from the relay. Anything that looks like an address is replaced by `[address]` and the text is cut to 200 characters. |
Answers: `200 {"accepted": n, "ignored": n, "suppressed": n}` (`ignored` = not an address of this system); `400` for a malformed request (bad kind, no reports, more than 100);
`401` without a token; `403` for any other role. The endpoint is not part of the published OpenAPI document of the integration API; this page is its contract. Every call is audited
(`notification.email.bounce_report`, counts only) and every new suppression too (`notification.email.suppressed`, a hash of the address and the reason).

A typical hook: a script on the mail relay that reads the relay's bounce log or hands each delivery status notification to this endpoint.

### Reporting a bounce: the mailbox reader
Set `MailboxDirectory` and `TagSecret`, and route the relay's bounce mail for Keel's sender address (the envelope sender) to that directory (for example a Postfix `virtual` or
`local` delivery to a Maildir). Keel reads delivery status notifications (RFC 3464, `multipart/report; report-type=delivery-status`) and abuse reports (RFC 5965). A report counts only when
the returned original headers carry the right `X-Keel-Bounce-Tag` for the address it names; the relay must therefore return headers (Postfix does by default;
`bounce_size_limit` and `bounce_template_file` can change that). Files that cannot be used (not a report, no or wrong tag, over 256 KB) go to `rejected/` and are removed after 14 days;
used files are deleted. Mail that is not a bounce should not be routed here.

## Administration
**Administration > E-mail delivery** (`/admin/email`, administrators only) lists suppressed addresses (reason, hard bounces, date, number of users) and the 100 latest reports.
**Lift suppression** resets the counter and clears the confirmation of every user of that address: each must confirm again (the **re-verify flow**), and mail resumes only then. Lifting is
audited (`notification.email.suppression_cleared`). A user whose own confirmed address is suppressed is told on the preferences page; a user who types an address never learns whether it
is suppressed.

## Audit and privacy
Actions `notification.email.verification_sent`, `verification_throttled`, `verified`, `verify_failed`, `bounce_report`, `suppressed`, `suppression_cleared` and `health_list` (denials). They never contain an address or a token; an address is identified by a
12-character hash. The addresses in `ntf.preference`, `ntf.email_address_health` and `ntf.email_bounce_event` are personal data: back them up with the database and treat them like the user directory.
