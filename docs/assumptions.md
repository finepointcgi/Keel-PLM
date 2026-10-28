# Working assumptions

The clarifying questions from the kickoff were answered with "use your defaults" (2026-10-27).
Every answer below is an **assumption**, not a confirmed requirement. Each one names the
ADR or code that depends on it, so it can be revisited cheaply. Change this file when an
assumption is confirmed or overturned.

| # | Topic | Assumption | Depends on it |
|---|---|---|---|
| A1 | Identity | OIDC (authorization code + PKCE for the SPA, JWT bearer for the API). CAC/PIV is performed by the IdP; the app never sees certificates. Keycloak is bundled **for dev/demo only**. | ADR-0007 |
| A2 | User attributes | The IdP releases these claims: `us_person` (bool), `citizenship` (ISO 3166 alpha-3), `org`, `programs` (multi), `export_authorizations` (multi: license/agreement IDs such as TAA or DSP-5 numbers), `cui_categories` (multi). The IdP (fed by the security office) is authoritative. | ADR-0004 |
| A3 | ITAR access | ITAR data requires `us_person=true` **or** an export authorization ID that the object explicitly lists. EAR data with an ECCN other than EAR99 follows the same rule; EAR99 does not restrict on nationality. | ADR-0004 |
| A4 | Hidden BOM children | Shown as a **redacted placeholder line**: quantity, find number and unit of measure are visible; number, name, markings and files are not. Rollups stay correct. | ADR-0003, ADR-0004 |
| A5 | Classification level | System holds **CUI and export-controlled unclassified data only**. Never classified. | ADR-0004 |
| A6 | Numbering | System-generated numbers per object type with a type prefix (e.g. `P-000123` part, `D-000045` document, `ECN-000012`). Explicit numbers are accepted for migration. | Phase 1.2 |
| A7 | Revisions | ASME Y14.35 alpha sequence: A…Y skipping I, O, Q, S, X, Z, then AA, AB… No pre-release revisions; iterations (A.1, A.2) carry in-work history. First revision is A. | Phase 1.2 |
| A8 | Change process | PR → ECR → ECN → Change Task supported. Class I changes need an ECR approved by a CCB before an ECN; Class II changes may open an ECN directly. Released objects can only be revised inside an ECN. | ADR-0006, Phase 1.6 |
| A9 | E-signature | Re-authentication at signing via IdP step-up (`prompt=login`, `max_age` ≤ 300 s, verified via `auth_time`). Default reason codes: Approved, Rejected, Reviewed, Approved with comments. No 21 CFR Part 11 claim. Signature stamping on PDFs is Phase 2. | ADR-0007 |
| A10 | Marking non-PDF files | PDFs are stamped in place. Every other format is delivered inside a ZIP with a marking cover sheet (PDF) and a `MARKINGS.txt` manifest. | Phase 1.3 |
| A11 | Scale (sizing target) | 500 concurrent users, 5 M parts, 50 TB vault, single site. 10-level/50k-line explosion < 2 s at p95. | ADR-0003, ADR-0009 |
| A12 | Deployment | Docker Compose for dev and small sites; Helm on CNCF-conformant Kubernetes (RKE2 is the reference). Images on Red Hat UBI 9 bases; FIPS-mode RHEL hosts in production. | ADR-0008, ADR-0012 |
| A13 | Object storage | Code depends on the S3 API only. No product dependency on MinIO given its 2025 licensing/distribution changes. Dev store chosen in increment 1.3. | ADR-0012 |
| A14 | CAD / ERP | First CAD target and ERP product unknown. Phase 4 starts with neutral formats and a generic event/REST contract. | ADR-0011 |
| A15 | Migration timing | Migration is Phase 3, after configuration management (Phase 2). | Phase plan |
| A16 | UI libraries | Open-source only (MIT/Apache/BSD). No commercial grid. | ADR-0010 |
| A17 | Runtime | .NET 10 LTS (.NET 8 support ends 2026-11-10). PostgreSQL 17. | ADR-0001 |
| A18 | MBOM ownership | Routings/operations are owned by ERP/MES; this system owns MBOM structure only. | Phase 2 |
| A19 | Effectivity | Serial-number effectivity is primary, then lot/block, then date. Effectivity is carried on BOM usage links and on change-driven revision supersession. | Phase 2 |
| A20 | Audit retention | 7 years online-verifiable, with optional syslog (RFC 5424 over TLS) forwarding to a SIEM. | ADR-0005 |
| A21 | Company name | `[Company]` placeholder retained; product name **"Keel PLM"** (working name), namespace `Keel`. | Everywhere |
