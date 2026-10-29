# ADR-0037: Suppliers, approved lists and the supplier portal

Status: accepted (increment 8.2, 2026-10-29). Builds on ADR-0004 (markings and access policy), ADR-0005 (audit), ADR-0018 (review and e-signature), ADR-0024 (EBOM and MBOM,
supply types), ADR-0025 (outbound clearance profiles), ADR-0026 (malware scanning and the vault), ADR-0030 (notifications) and ADR-0031 (list sorting).
Assumptions are FEAT-SRC-n in [increments/8.2-sourcing.md](../increments/8.2-sourcing.md).

## Context
Parts are bought as well as made. Engineering needs the vendors and manufacturers it may buy from (with their quality approvals), the manufacturer part numbers that satisfy
a part, which vendor supplies what at which lead time and price, and a way to ask suppliers for information or a quotation without sending them a login to the whole system.
Three constraints shape the design: export control (a foreign supplier must never be selected for, or told about, ITAR data), isolation (an external user must never see
another supplier or any internal object), and the existing guarantees (markings on everything, audit in the same transaction, no marked text in notifications or audit
payloads, air-gapped, strict CSP).

## Decisions

### 1. One module, one schema, no new access model
`Keel.Sourcing` (schema `src`, migration `0020_sourcing.sql`) references only Core, Security and Audit. It reads parts through their marking columns (key lookups and the
read filter), reaches the review engine through `IWorkflowObjectGateway`, files through `IContentStore`/`IContentReader` (new, Core) and clearance profiles by reading the
integration module's table. The BOM-level coverage report composes BOM, MBOM and sourcing in `Keel.Web` (`SourcingCoverageService`), because the modules do not reference each other.

### 2. What is marked, and how the rest is read
Suppliers and requests carry the ten marking columns and are read with `AccessPolicy.BuildReadFilter`. Manufacturer parts, list entries and part relations carry **no markings
of their own**: they are read only through a readable part (its markings, joined at query time, so a change of the part's markings applies at once) and, for entries, a readable
supplier. An entry whose supplier the reader cannot read is invisible and does not count, so every list, count and report is a function of what the reader can read.

### 3. Export control is a rule with three layers
A supplier carries an explicit `is_foreign` flag (a supplier outside the USA must be flagged). `SourcingRules.CheckSupplierForPart` refuses a foreign supplier for an ITAR-marked
part with no override and no export-authorization exception. It is applied (a) by the services when entries and request recipients are made, (b) by database triggers on
`source_entry`, `request_recipient` and `request_part` (defense in depth), and (c) at read time, so a part whose markings were raised later stops counting a foreign source
(coverage) and stops being released to a foreign recipient (portal). Flagging a supplier foreign is refused while readable ITAR entries exist.

### 4. Approval is a review with signatures; withdrawing is not
A supplier moves prospective to approved or conditional only by a review (`supplier-approval`, an ordinary workflow template). The proposed status is stored on the record, the
review engine freezes it, and the signed content is the record plus every certification with a digest recomputed from the live rows. Approved needs a valid certification;
conditional does not. Disapproving or making obsolete (the direction that removes standing) needs a reason and no signature. List entries use the same engine
(`source-entry-approval`), with the part's markings deciding who may review and sign; an active entry is immutable except for its end date and its withdrawal.

### 5. Certification expiry is data, a job and a notice
`CertificationExpiryService` raises one notice per certification and stage (60, 30 and 7 days before, and expired), idempotent through `src.certification_notice`, as a system
audit event and a pointer-only notification to the watchers of the supplier. The status does not change by itself (that would bypass the signatures); an expired certification
demotes the supplier to a conditional source in coverage.

### 6. The portal is scoped by one link and by nothing else
A portal user is an IdP identity with the new role `plm-supplier`, linked by an administrator to exactly one supplier in `src.portal_user`. The role is not in `Roles.All` and
grants no `PlmAction`, so every internal read fails closed. `PortalService` resolves the caller's supplier first in every method; no parameter, claim or header names a
supplier, and every statement carries it (an architecture test checks this). A supplier sees a request only when it is addressed to it, issued, and releasable: the supplier's
clearance profile (the integration module's profile concept) must allow the request's markings and each listed part (distribution statement, jurisdiction, CUI, program), a
supplier without a profile receives nothing, and a foreign supplier never receives ITAR data whatever its profile says. Anything else is "not found", indistinguishable from an
unknown request. Responses are immutable rows; files go through the vault, which scans them, and one rejected file rejects the response.

### 7. UI
Internal pages live under the existing authorization (suppliers, supplier requests, a Sourcing tab on the part page, where-sourced and coverage pages, the review panel). Portal
pages live in their own folder with their own policy and inject only `PortalService`; the shell shows a supplier user the portal navigation only, with no search, palette or
notification bell. Every page uses `PageFrame` with the banner of what it shows.

### 8. API
`/api/v1/sourcing/*` (internal roles) and `/api/v1/portal/*` (supplier role), published as a separate OpenAPI 3.1 document (`/openapi/sourcing.json`, committed as
`docs/sourcing/openapi.json`, drift-tested). The integration document is unchanged.

## Consequences
- A supplier's name and contacts are marked or personal data: audit details of this module carry numbers, codes and counts only (a test scans the code).
- The `ws` and `ntf` object-type constraints were relaxed by pattern (migration 0020) so `supplier` can be a followed, commented and recent object; other modules adding kinds
  should do the same rather than list kinds.
- Requests fix their parts and recipients at creation; changing them means a new request.
- Not delivered: supplier self-registration, supplier-side certificate management outside a response, price-break comparison across suppliers, PDF stamping of supplier files,
  notification of the buyer when a response arrives.
