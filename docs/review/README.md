# Review packets for outside counsel and the export-control official

These packets let outside counsel and an empowered export-control official review Keel PLM quickly. They are written by the engineering side. They state facts with file references, mark what could not be verified, and list the questions that need professional judgment.

**Every conclusion in these packets is for counsel (or the empowered official) to decide.** Nothing here is legal advice, a sign-off, or a statement that anything is compliant, legal, classified in a particular way, or authorized.

## Packets

| Packet | Audience | Contents |
|---|---|---|
| [legal-open-source.md](legal-open-source.md) | outside counsel (licences) | inventory of every third-party component built from the repository: version, SPDX licence, where it ships, how it is linked or loaded, what the licence requires; copyleft highlighted; questions for counsel; evidence locations |
| [export-control.md](export-control.md) | empowered export-control official and counsel | factual system description: architecture, data flows, where controlled data lives, marking enforcement, foreign-person and supplier rules, every outbound channel, encryption, audit, administrators, hosting, development process; questions table |
| [data-handling-and-security-facts.md](data-handling-and-security-facts.md) | counsel and assessors (optional appendix) | control-to-evidence map for NIST SP 800-171 and CMMC-style questions, with unverified items called out |

Existing related material: [../third-party/legal-review.md](../third-party/legal-review.md) (licence review status, pending), [../ops/nist-800-171-mapping.md](../ops/nist-800-171-mapping.md), [../assumptions.md](../assumptions.md) (A34 on INT-6 and INT-12).

## How to use the packets

1. Start with this page, then the packet for your role. Each ends with a questions table and a list of unverified items.
2. Every statement names a file. Check it there. "Verified" in a packet means read in the repository or produced by a tool run on it; "unverified" means it was not checked or cannot be checked from the repository.
3. Record your outcome in the status table below (reviewer, date in `YYYY-MM-DD`, outcome). A row stays `pending` until a reviewer name and a date are given; a unit test (`ReviewPacketTests`) fails if a row claims anything else without both.
4. Send questions or corrections back to engineering; engineering changes the packets, not the outcome column.

## What the packets do not do

- They do not give legal advice and do not conclude that any licence obligation is met.
- They do not classify the software or any data (no ITAR/EAR jurisdiction, USML category or ECCN is asserted for the product) and do not say any authorization or license exception is needed or not needed.
- They do not certify NIST SP 800-171 or CMMC compliance, and they are not a System Security Plan.
- They cite no regulation section numbers. Public regulation names are used only as labels; anything more specific is to be confirmed by counsel.
- They do not describe the organisation, its people or its contracts beyond what the repository records. Developer nationality and location are not in the repository and are not guessed.

## Status

| Packet | Reviewer | Date | Outcome |
|---|---|---|---|
| Open-source and third-party licences (legal-open-source.md) | - | - | pending |
| Export-control system description (export-control.md) | - | - | pending |
| Data handling and security facts (data-handling-and-security-facts.md) | - | - | pending |

## Glossary (as used in the product)

| Term | Meaning in Keel |
|---|---|
| ITAR | International Traffic in Arms Regulations. A marking value `ITAR` on an object (with a USML category such as `XI(d)`) means the owner has declared the item or data as ITAR-controlled; Keel records and enforces it, it does not decide it |
| EAR | Export Administration Regulations. Marking value `EAR` with an ECCN (for example `9E610.a`) or `EAR99` |
| USML / ECCN | U.S. Munitions List category / Export Control Classification Number, as typed in the markings |
| CUI | Controlled Unclassified Information; categories such as `CTI`, `EXPT`, `PROPIN` on an object. ITAR and non-EAR99 EAR data must carry `EXPT` in Keel |
| Distribution statement | A to F as used in DoDI 5230.24 ("Distribution Statements on Technical Documents"); stamped on output and used by the outbound release check. F is never released automatically |
| DoDI 5230.24 | the DoD instruction the distribution statement table in `OutboundRelease.cs` is modelled on, as understood by the team (assumption INT-6; confirmation pending) |
| U.S. person | a Keel `Subject` flag `us_person` from the identity provider; Keel does not determine it |
| Export authorization | a license or agreement id (for example a TAA or DSP-5 number) listed on an object and held by a person or recipient; the only exception to the nationality rule |
| TAA | Technical Assistance Agreement, as an example of an authorization id |
| TDP | Technical Data Package, exported as a signed ZIP; categories follow MIL-STD-31000 as understood by the team (INT-12; review pending) |
| Clearance profile | declared description of an outbound recipient (category, U.S.-persons-only, allowed jurisdictions, statements, programs, authorizations) |
| ABAC | attribute-based access control: `AccessPolicy` (ADR-0004) |
| Program | a program or contract code used for need-to-know |
| SBOM | software bill of materials (CycloneDX 1.5) |
| LGPL, MPL, GPL | licence families discussed in the licence packet |
| FIPS | U.S. cryptographic module validation standard family; Keel relies on the host platform for it |
| NIST SP 800-171 / CMMC | control catalogue and certification model referenced in `docs/ops/nist-800-171-mapping.md` |
| Empowered official | the person with authority to make export-control determinations for the organisation |
