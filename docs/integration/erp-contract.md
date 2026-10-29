# ERP contract

Keel PLM publishes engineering changes to connected systems as **events**, delivered at least once to configured **sinks**, and offers a **pull API** for
reconciliation. Nothing leaves Keel toward a system that is not cleared for it (see [Clearance profiles](#clearance-profiles)).

## Envelope

Every event is a JSON document (UTF-8). The bytes of one event are identical for every attempt and every sink.

```json
{
  "id": "0198e1c2-6d3b-7c1e-9a52-3f0b6c2d1a77",
  "type": "part.released",
  "version": 1,
  "occurredAt": "2026-10-28T14:03:11.482913Z",
  "source": "keel-plm",
  "aggregate": { "type": "part", "number": "P-000123", "revision": "B" },
  "correlationId": "5c9f0f8e-0d0e-4c5d-8e55-1f6a6d1f7c10",
  "actor": "alice",
  "markings": {
    "banner": "CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA",
    "exportJurisdiction": "Itar", "eccn": null, "usmlCategory": "XI(d)", "cuiCategories": ["EXPT"],
    "distributionStatement": "D", "controllingOffice": "ACME", "program": "ORCA", "owningOrg": "ENG", "exportAuthorizations": []
  },
  "data": { }
}
```

`id` is a UUID v7 and the key to deduplicate on. `data` is described by `schemas/{type}.v{version}.json`; the envelope by `schemas/envelope.v1.json`.
The `markings` are the markings of the event: handle the data as marked.

## Event catalogue

| Type | Emitted when | Payload (v1) |
|---|---|---|
| `part.released` | A part revision is released (review workflow, change notice, or administrator override: `via`) | `partId, revisionId, number, name, partType, source, defaultUnit, revision, iteration, state, description, attributes, via, stateChangedBy` |
| `part.obsoleted` | A released part revision becomes obsolete | same as `part.released` |
| `document.released` / `document.obsoleted` | A document revision is released or obsoleted (administrator override today) | `documentId, revisionId, number, name, documentType, revision, iteration, state, via, files[role,fileName,mediaType,sizeBytes,sha256]` |
| `change.approved` | An engineering change request is approved by the board | `changeId, number, type, title, description, changeClass, priority, reason, sourceChange, submission, affected[], approvedAt` |
| `change.released` | An engineering change notice is released | as above plus `resulting[objectType, number, revision, iteration]`, `obsoleted[]`, `retainedByEffectivity[]`, `releasedAt`; each `affected` item has `action` and `effectivity` |
| `baseline.established` | A baseline is established | `baselineId, number, name, type, state, milestone, root{number,revision,iteration}, configuration, itemCount, documentCount, contentSha256, establishedAt, establishReason` |
| `bom.published` | An assembly revision is released: its EBOM, one level, children at their latest released revision | `assembly{...}, spec, levels, lineCount, restrictedLineCount, lines[]` |
| `mbom.released` | A manufacturing BOM revision of a part in a plant is released through the review workflow (not on check-in, submission or reconciliation) | `mbom{mbomId,number,plant,revision,iteration,state}, part{partId,number,name}, derivedFrom{ebomIteration,spec}, contentSha256, reconciliation{runId,status,sha256}, releasedAt, lineCount, restrictedLineCount, lines[]` |
| `bom.structure.published` | Same, all levels, only for sinks that opted in (limited to a configured number of lines; a larger structure is skipped and audited, never truncated) | as above with `level, path, extendedQuantity` per line |

`bom.published` example (`data`):

```json
{
  "assembly": { "partId": "…", "revisionId": "…", "number": "P-000200", "name": "Pod", "revision": "A", "iteration": "A.2" },
  "spec": "LatestReleased", "levels": "one", "lineCount": 2, "restrictedLineCount": 1,
  "lines": [
    { "findNumber": 10, "alternate": 0, "quantity": 4, "unit": "EA", "restricted": false,
      "referenceDesignators": ["R1","R2","R3","R4"], "notes": null, "effectivity": null,
      "child": { "partId": "…", "number": "P-000101", "name": "Bolt", "partType": "Component", "revision": "A", "iteration": 1, "state": "Released", "unresolved": false } },
    { "findNumber": 20, "alternate": 0, "quantity": 3, "unit": "EA", "restricted": true }
  ]
}
```

A line with `restricted: true` is a **placeholder**: the markings of the assembly do not cover that child, so only find number, quantity and unit are given.
Count them with `restrictedLineCount` and obtain the line with your own clearance through the pull API (`/api/v1/integration/boms/{number}`), which applies the
caller's markings. A child with `unresolved: true` has no released revision yet. A later release of a child does not re-publish its parents: reconcile with the pull API.

`mbom.released` example (`data`):

```json
{
  "mbom": { "mbomId": "…", "number": "MBOM-000007", "plant": "PLANT-A", "revision": "A", "iteration": "A.2", "state": "Released" },
  "part": { "partId": "…", "number": "P-000200", "name": "Pod" },
  "derivedFrom": { "ebomIteration": "A.2", "spec": "Latest released revision (P-000200)" },
  "contentSha256": "9c1f…", "reconciliation": { "runId": "…", "status": "Reconciled", "sha256": "41ab…" },
  "releasedAt": "2026-10-28T14:03:11Z", "lineCount": 3, "restrictedLineCount": 1,
  "lines": [
    { "findNumber": 10, "alternate": 0, "quantity": 4.2, "unit": "EA", "restricted": false,
      "child": { "partId": "…", "number": "P-000101", "name": "Bolt", "partType": "Component", "source": "Buy" },
      "scrapFactor": 0.05, "phantom": false, "supplyType": "Buy", "operationReference": "OP-20", "mbomOnly": false,
      "referenceDesignators": [], "notes": null, "effectivity": null,
      "ebomLinks": [ { "ebomFindNumber": 10, "status": "quantity_adjusted", "reason": "Scrap allowance" } ], "childMbom": null },
    { "findNumber": 30, "alternate": 0, "quantity": 2, "unit": "EA", "restricted": false,
      "child": { "partId": "…", "number": "P-000150", "name": "Bracket assembly", "partType": "Assembly", "source": "Make" },
      "scrapFactor": null, "phantom": false, "supplyType": "Make", "operationReference": null, "mbomOnly": false,
      "referenceDesignators": [], "notes": null, "effectivity": "serial 101 and up (ORCA-POD)",
      "ebomLinks": [ { "ebomFindNumber": 30, "status": "matched", "reason": null } ], "childMbom": { "number": "MBOM-000006", "revision": "A" } },
    { "findNumber": 40, "alternate": 0, "quantity": 1, "unit": "EA", "restricted": true }
  ]
}
```

`mbom.released` follows the same release rules as `bom.published`: the event carries the markings of the MBOM, a sink whose profile does not clear them
never receives it (it is withheld and audited), and a line whose child part the MBOM's markings do not cover is an explicit **placeholder** (`restricted: true`,
only find number, alternate, quantity and unit; its EBOM links and child MBOM are not named). `ebomLinks[].status` is the declared relationship to the EBOM line
(`matched`, `quantity_adjusted`, `substituted`); `childMbom` names the plant's released MBOM a Make sub-assembly resolves to. An ERP consumes the event as an upsert
keyed by `mbom.number` and `mbom.revision`; the owner of routings and operations stays the ERP/MES (A18, `operationReference` is a free reference only).

`change.released` example (`data`, abbreviated):

```json
{ "number": "ECN-000012", "type": "ecn", "title": "Revise bracket", "changeClass": "II", "priority": "Medium", "sourceChange": null, "submission": 1,
  "affected": [ { "objectType": "part", "number": "P-000123", "name": "Bracket", "revision": "A", "action": "revise", "effectivity": "serial 101 and up (ORCA-POD)" } ],
  "resulting": [ { "objectType": "part", "number": "P-000123", "revision": "B", "iteration": "B.1" } ],
  "obsoleted": [], "retainedByEffectivity": ["P-000123 A (serial up to 100 (ORCA-POD))"], "releasedAt": "2026-10-28T14:03:11Z" }
```

## Delivery

### Webhook
`POST` to the sink's https URL with `Content-Type: application/json`:

| Header | Value |
|---|---|
| `X-Keel-Event-Id` | the event id |
| `X-Keel-Event-Type` | e.g. `part.released` |
| `X-Keel-Timestamp` | Unix seconds when this attempt was signed |
| `X-Keel-Signature` | `v1=<hex>`; during a secret rotation `v1=<hex>,v1=<hex>` (one per active secret) |
| `X-Keel-Delivery-Attempt` | 1, 2, 3... |

Signature: lower-case hex of HMAC-SHA-256 with the shared secret (UTF-8) over the ASCII timestamp, a dot, and the raw body bytes. Verify the signature **before**
parsing, with a constant-time comparison, accept any `v1` value that matches any secret you hold, and reject timestamps more than five minutes away from your clock.

Response handling by Keel:

| Response | Result |
|---|---|
| 2xx | delivered |
| 408, 429, 5xx, connection error, timeout | retried with exponential backoff and jitter (5 s base, capped at 1 h, 8 attempts by default), then a dead letter |
| 3xx | permanent: redirects are never followed |
| any other 4xx, including 401 and 410 | permanent: dead letter at once; an administrator fixes the endpoint and requeues |

Keel verifies your server certificate against the CA bundle configured for the sink (or the system store) and does not use a proxy. mTLS is optional: Keel
presents the client certificate configured for the sink.

### File drop
The directory is a durable spool: after processing, delete the data file and its manifest (acknowledgement). Keel pauses when too many files wait (ops: integration.md).
Keel writes each file under a temporary name and renames it, then writes `{file}.manifest.json` (`file`, `sha256`, `sizeBytes`, `writtenAt`, `events[]`). **Process
only files that have a manifest, and verify the SHA-256.** Formats: one JSON file per event (`keel-{seq}-{eventId}.json`) or one NDJSON file per batch.

### Guarantees
- **At least once.** The same event can arrive more than once (a crash after your response, an administrator's requeue). Deduplicate on `id`; store processed ids.
- **Ordering per aggregate within a sink.** Events about the same object (same `aggregate.type` and `number`) arrive in the order they were committed; a failing
  event holds back later events of that object until it succeeds or becomes a dead letter. There is **no global ordering**, and a requeued dead letter can arrive after
  newer events: compare `occurredAt` and the revision before overwriting newer data.
- **Idempotent handlers.** Treat every event as an upsert keyed by the object and revision.
- **Not delivered means withheld, not lost.** An event the sink is not cleared for is withheld and visible to administrators; it is sent only after an administrator
  changes the profile and explicitly re-evaluates it.

## Pull API (reconciliation and backfill)
Authenticate with a token of a service account with role `plm-integration` and the account's security attributes (`us_person`, `programs`, `cui_categories`,
`export_authorizations`...). The account sees exactly what those attributes permit; no sink profile applies to a pull.

| Request | Returns |
|---|---|
| `GET /api/v1/integration/events?cursor=&limit=&type=&aggregate=` | `{events:[{cursor, envelope}], nextCursor}` in commit order. Resume with `nextCursor`; an empty page means you are current. |
| `GET /api/v1/integration/boms/{number}?revision=&spec=&depth=&baseline=` | Resolved children (revision, iteration, quantity, unit, find number, designators, effectivity). `spec`: `LatestReleased` (default), `Latest`, `Baseline`. Lines you may not read are placeholders. |
| `GET /api/v1/integration/mboms/{number}?plant=&revision=` | The latest Released MBOM of the part `{number}` in the plant (or the named revision) at its latest iteration: lines with child, quantity, unit, scrap, phantom, supply type, operation reference, effectivity, EBOM links and the child MBOM of Make sub-assemblies, plus the reconciliation run and digest. `plant` is required. Lines you may not read are placeholders; an MBOM you may not read is `404`. |
| `GET /api/v1/integration/changes/{number}` | A change with its affected and resulting revisions and effectivity. |

Events appear in the pull only once no older transaction is still open, so a cursor never skips an event. The OpenAPI 3.1 document is at `/openapi/v1.json`
(committed: [openapi.json](openapi.json)).

## Verifying a signature

C#:
```csharp
static bool Verify(string[] secrets, string header, string timestamp, byte[] body)
{
    if (!long.TryParse(timestamp, out var ts) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > 300) return false;
    var message = Encoding.ASCII.GetBytes(timestamp + ".").Concat(body).ToArray();
    var offered = header.Split(',').Select(p => p.Trim()).Where(p => p.StartsWith("v1=")).Select(p => p[3..]).ToList();
    return secrets.Any(secret =>
    {
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));
        return offered.Any(o => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(o), Encoding.ASCII.GetBytes(expected)));
    });
}
```

Python:
```python
import hmac, hashlib, time

def verify(secrets, header, timestamp, body: bytes) -> bool:
    if abs(time.time() - int(timestamp)) > 300:
        return False
    message = timestamp.encode() + b"." + body
    offered = [p.strip()[3:] for p in header.split(",") if p.strip().startswith("v1=")]
    for secret in secrets:
        expected = hmac.new(secret.encode(), message, hashlib.sha256).hexdigest()
        if any(hmac.compare_digest(o, expected) for o in offered):
            return True
    return False
```

Shell (openssl), for checking a captured request:
```sh
printf '%s.' "$TIMESTAMP" | cat - body.json | openssl dgst -sha256 -hmac "$SECRET" -hex | sed 's/^.* //'
```

## Versioning and deprecation
- Each type has an integer `version`. Adding an optional field, or a new event type, does **not** change the version: ignore fields and types you do not know.
- A change that removes or re-types a field publishes a new version under the same type; both schemas stay in `schemas/`. The previous version is delivered in
  parallel for at least two release cycles, announced in the release notes, before it is retired.
- Event types you do not handle: acknowledge with 2xx (Keel only sends what a sink subscribes to, but a new type can be added to "all").

## Errors
Return 2xx only when the event is durably recorded. Return 5xx (or 429 with a delay) for conditions that may pass; return 4xx only for events that will never be
acceptable. A response body is ignored; log the event id on your side for support.

## Security
- TLS 1.2 or 1.3 only, certificate verified against the configured CA bundle; mTLS optional.
- HMAC-SHA-256 shared secret per sink, provisioned by name on the Keel side (never stored in the database). **Rotation:** provision the new secret, set it as the
  sink's second secret, deploy it to your receiver (which accepts both), make it the primary, remove the old one on both sides.
- The sink URL must be https and match the site's allow-list of hosts or networks; loopback, link-local and metadata addresses are always refused.
- Events are marked data (CUI, possibly ITAR). Store and log them under your own handling rules.

## Clearance profiles
Each sink has a clearance profile: recipient category (public, USG, DoD, USG contractor, DoD contractor, foreign), whether the system is operated by U.S. persons only,
allowed jurisdictions and ECCN/USML patterns, CUI categories, programs, distribution statements (A to E) and the export authorizations the recipient holds. Statement
F is never released automatically; B is for U.S. Government agencies, C adds their contractors, D is DoD and DoD contractors, E is DoD components. A foreign recipient
can receive B to E data only through an export authorization listed on the data. ITAR and non-EAR99 EAR data need a U.S.-persons-only recipient or a matching
authorization. A profile is declared by your Keel administrator with your security office.

## Onboarding checklist
1. Security office approves the recipient and defines its clearance profile (category, U.S.-person operation, programs, CUI, statements, authorizations).
2. Agree the endpoint: https URL on an allow-listed host or network, CA bundle, optional client certificate. Keel admin adds the host to `Keel:Integration:AllowedHosts`.
3. Generate a shared secret (at least 32 random bytes), give it to the Keel operator as `Keel__Integration__Secrets__{name}` in the secrets directory; keep your copy.
4. Implement the receiver: verify signature and timestamp, deduplicate on `id`, handle events as upserts, answer 2xx after durable storage.
5. Create the sink in Administration, Integration, Sinks (disabled), review the validation messages, send a test event with the reference receiver, enable it.
6. Backfill with the pull API (`/events` from the empty cursor, `/boms/{number}` for assemblies), then switch to events.
7. Watch Administration, Integration, Deliveries and `/health/ready` (integration check) for retries, dead letters and withheld events.

## Mapping to ERP objects
| Keel | Typical ERP object | Owner of the data |
|---|---|---|
| Part revision (`part.released`) | Material master / item revision | Keel owns number, name, description, type, unit, revision and engineering attributes; the ERP owns procurement, costing, planning and plant data |
| Released MBOM (`mbom.released`) | Manufacturing bill of material per plant | Keel owns the MBOM structure, net quantities, scrap factors, supply type and the EBOM links; the ERP owns routings, operations, costing and planning (A18) |
| Released EBOM (`bom.published`) | Bill of material (engineering) | Keel owns the structure, quantities, find numbers, designators and effectivity; the ERP owns the manufacturing BOM, routings and operations (A18) |
| Change notice (`change.released`) | Engineering change number / change master | Keel owns the change content, the affected and resulting revisions and effectivity; the ERP owns the order-level execution |
| Document revision (`document.released`) | Document info record | Keel owns the file and its revision; send the files through the TDP export or a controlled transfer: the event carries names and SHA-256, not bytes |
| Baseline (`baseline.established`) | Configuration / as-designed snapshot reference | Keel owns the frozen content; use the pull API for its items |

## Technical Data Package
`POST /api/v1/tdp` (or Export on a part or baseline page) streams a signed ZIP aligned with MIL-STD-31000 for a recipient profile. See ADR-0025 for the manifest,
signature and verification (`POST /api/v1/tdp/{id}/verify` with the ZIP as body). A package is complete or refused; a refusal states counts only.
