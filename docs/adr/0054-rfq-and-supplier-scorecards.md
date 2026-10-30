# ADR-0054: RFQs beyond the portal and supplier quality scorecards

Status: accepted (increment 9.6, 2026-10-30). Builds on ADR-0004 (markings and access), ADR-0005 (audit), ADR-0025 (outbound release check and clearance profiles),
ADR-0029 (visuals kit), ADR-0036 (quality records), ADR-0037 (suppliers, sourcing, the portal) and ADR-0046 (visual language). Assumptions are A76 to A78 in
[assumptions.md](../assumptions.md); the details are in [increments/9.6-rfq-scorecards.md](../increments/9.6-rfq-scorecards.md).

## Context
ADR-0037 shipped a light request ("RFQ-lite"): parts and quantities out, a free-form response back. A buyer who really sources a part needs more: the date each line is
needed, the released drawings the supplier must quote against, quotes with price breaks, lead time and exceptions, a side-by-side comparison, an award that leaves a
trace in the approved vendor list, and some measure of how a supplier performs over time. All of it concerns controlled data that leaves the building (documents) or comes
from controlled records (nonconformances), which is where a convenient feature leaks. The users also find the application too complicated, so every new flow had to be small.

## Decisions
1. **One request model.** An RFQ is the existing `src.request` of kind `rfq`; nothing is duplicated. Lines gain a need-by date (`src.request_part.need_by`), a quote gains a
   validity date (`src.response.valid_until`), and the quote JSON of a line gains `tiers` (price breaks), `note` and `exception`. Responses stay immutable: a correction is a new
   response, the latest one counts, and a later decline withdraws the earlier quote. After the due date the portal takes no more responses (a buyer can move the date; an issued
   request needs a date that has not passed).
2. **Documents are pinned, released and whole.** A request can carry released documents (`src.request_document`, migration 0033): the pin is one released iteration of one
   document (a trigger refuses an unreleased version, a request that is not a draft, and an iteration of another revision). The request's markings must cover every document as they
   must cover every part (a reader of the request may read what it lists); the reader's own right is checked again at read time. "TDP" in this increment means the released
   documents linked to the parts (a checkbox adds them); Keel still stores no package bytes (ADR-0025).
3. **Refusal, not redaction.** Whether a supplier receives a document is decided at read time by the outbound release check (`SourcingRules.ReleaseToSupplier`: clearance profile,
   distribution statement, jurisdiction and, hard, a foreign supplier never receives ITAR data) against the document's OWN markings. A document that fails it is not listed for that
   supplier (no placeholder, no count), and its file answers with the same "not found" as a file that does not exist; the refusal is audited. A permitted file is the stored bytes,
   verified against their hash, never a stamped or blacked-out rendition. The buyer sees, per document, which suppliers do not receive it.
4. **Comparison.** `RequestService.CompareAsync` builds, from what the reader may read, one cell per line and supplier: the unit price at the line's quantity (the tier with the
   largest quantity not above it; a quantity below the smallest tier has no price), the extended price, lead time, minimum order, note, exception, "lowest price" (only within one
   currency: there is no exchange rate in an air-gapped system, so quotes in different currencies are not ranked), "shortest lead time" and whether an order placed today meets the
   need-by date. Totals are per supplier and currency over the priced lines and say when lines are missing. The comparison is pure (`BuildComparison`) and recomputed on every view.
5. **Awards record the sourcing link.** An award is one active winner per line, full (every line, which needs a priced quote for each) or per line. It rests on the supplier's
   latest quote, which must not have expired, at the line's quantity. Each award is tied to an approved vendor list entry (AVL) of the part and the supplier: an open entry
   that exists is linked unchanged, otherwise a DRAFT entry is created carrying the quoted lead time, minimum order and price breaks, which still goes through the entry's
   approval review (an award does not make a source; a signed review does). The export-control rule is checked again (a foreign supplier is never awarded an ITAR part;
   a trigger agrees). When every line has an award the request closes. An award is withdrawn with a reason (immutable otherwise, trigger-enforced); the entry it created goes
   with it while still a draft. A supplier sees only the lines awarded to it.
6. **Scorecards are views, formula version 1.** `ScorecardRules` is pure: Nonconformances 40 (severity points over the last 12 months), Corrective actions 10 (open ones),
   Responsiveness to requests 20 (answered on or before the due date), Certifications 20 (valid on the date), On-time delivery 10 (not measured: Keel records no deliveries).
   The score is the weighted average of the measured components; a component without a basis is shown as not measured and its weight is left out (never counted as perfect). The
   trend is the same formula at 12 month ends, so it is derived and needs no stored history. The formula is written on the page in plain words.
7. **Quality facts through a port.** `ISupplierQualityFacts` (Keel.Core) is implemented by Keel.Quality over `AccessPolicy.BuildReadFilter`: only records the viewer may read are
   returned (cancelled ones flagged), so a hidden record is not counted and leaves no sign. Sourcing never reads Quality tables. The supplier page already shows linked records,
   which is why the number of linked-but-uncounted records may be shown to a reader of the supplier.
8. **Publishing is the only way to the supplier.** `src.scorecard_publication` holds a frozen payload (score, components, trend, banner; counts and rates, never a record number or
   title). Publishing needs permission to modify the supplier and is refused when a linked record is hidden from the publisher (the card would understate it). The published copy counts
   only records and requests that may be released to THAT supplier (the same outbound check). A later publication replaces it for the supplier; earlier ones stay; withdrawing
   ends the supplier's access at once. The portal reads only the caller's own supplier's latest publication.
9. **Visuals kit.** `TrendChartView` (a line chart with labelled reference lines, a text description and an accessible table behind a `ViewSwitch`) lives with the other pictures
   (tokens only, no inline style, no script). The same `ScorecardCard` serves the buyer's Scorecard tab and the portal.
10. **Keep it simple.** A request starts from a part ("Request a quote for this part"), from an assembly ("Request quotes for these parts" on the coverage report, which adds the
    purchased parts with their extended quantities), or from a blank form with three lines of text per part ("P-000001 100 2026-12-01"); everything else has a default.

## Consequences
- No new project and no new route family: the existing request pages and portal pages carry the new fields; the portal gains `/portal/scorecard`.
- A quote's price at a quantity is a rule, not a convention: a supplier cannot be awarded a quantity its tiers do not cover.
- Currency conversion, split awards (one line shared by two suppliers), purchase orders and a delivery measure are not built; the manual says so.
- Every new audit action is classified in `ActivityCatalog`, and the catalog test now scans the `rfq`, `portal`, `scorecard`, `sourcing` and `supplier` prefixes as well.
