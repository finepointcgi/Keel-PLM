# Packet 2: export-control system description (for an empowered official and counsel)

Status: **pending review. For counsel and the empowered official to decide: every classification, jurisdiction, authorization and compliance question.** This packet states what the software does, with file references. It contains no determination that anything is or is not export-controlled, licensed, authorized or compliant. Language is neutral on purpose. See [README.md](README.md).

Marks: **verified** = read in this repository (path given); **unverified** = not checkable here.

> **Repository visibility (checked by the coordinator, 2026-10-30):** the GitHub repository `finepointcgi/Keel-PLM` is **private**. This is a point-in-time fact; visibility can change, and counsel should decide whether and when any publication affects the export questions below.

## 1. What the product is

- Keel PLM (working name; the organisation name is the placeholder `[Company]`, assumption A21) is a product lifecycle management system: parts, documents and a file vault, bills of materials, change management, workflow with electronic signatures, baselines, configuration, CAD file management, quality, sourcing, requirements, records, programs, reports. It is a web application (ASP.NET Core host with a Blazor Server UI and REST API) over PostgreSQL and an S3-compatible object store (`README.md`, `docs/CLAUDE.md`, ADR-0001, ADR-0012).
- Intended use: on-premises, air-gapped deployment holding export-controlled and CUI engineering data for a customer (`README.md`; assumption A5: the system holds unclassified CUI and export-controlled data only, never classified).
- It is software, not a data set. The product's source repository does not contain real controlled data: tests and seed data are synthetic (`docs/CLAUDE.md` conventions, ADR-0045).
- Its job with respect to export control is to record markings on data (jurisdiction, ECCN or USML category, CUI categories, distribution statement, program, authorizations) and to enforce them when people and systems read or receive that data (ADR-0004, ADR-0025).

## 2. Architecture and data flows

| Component | Role | Where defined |
|---|---|---|
| Host `Keel.Api` (+ `Keel.Web`) | UI, REST API, background jobs; all business rules; the only component that applies access policy | ADR-0001, ADR-0014, `src/Keel.Api` |
| PostgreSQL 17 | all records, marking columns, audit log, outbox | `db/migrations/0001..0028` |
| Object store (SeaweedFS in the shipped examples) | file contents, content addressed by SHA-256, server-side encryption setting `ServerSideEncryption` | ADR-0015, `Keel.Documents/Vault` |
| Identity provider (customer's; Keycloak with synthetic users in dev only) | authenticates people; releases the attribute claims | ADR-0007, assumption A1, A2 |
| Reverse proxy (Caddy or nginx example) | TLS to users | `deploy/compose`, `docs/ops/hardening.md` |
| Optional clamd, SIEM, SMTP relay, ERP receiver | customer-operated peers | sections 5 and 6 |

Flow in one line: user or service authenticates at the IdP, the host maps token claims to a `Subject` (missing attributes fail closed, `src/Keel.Security/SubjectClaims.cs`, ADR-0007), each request is authorized by `AccessPolicy` against the marking columns of the data, and every read, download, change, signature and denial is written to a hash-chained audit log in the same transaction (ADR-0005; `docs/CLAUDE.md` conventions).

Source-code level facts: modules do not read each other's tables except through documented, tested exceptions (architecture tests: `tests/Keel.UnitTests/ArchitectureTests.cs` and per-module `*ArchitectureTests.cs`).

## 3. Where controlled data lives

| Location | What | Notes |
|---|---|---|
| PostgreSQL, schemas `plm`, `doc`-related, `src`, `rpt`, `rec`, etc. | object records, names, descriptions, attributes, marking columns (`export_jurisdiction`, `eccn`, `usml_category`, `cui_categories`, `distribution_statement`, `program`, `owning_org`, `export_authorizations`, ADR-0004) | encryption at rest is the operator's (LUKS), `docs/ops/hardening.md` |
| Object store | file contents | SSE-S3 (`AES256`) requested when `ServerSideEncryption=true` (default in `appsettings.json` and the compose example; code default is false, `VaultOptions.cs`) |
| `ntf.comment` | comment bodies (marked data; not in audit, exports or TDPs) | `docs/ops/notifications.md` |
| Audit log `audit.event` | identifiers, action, outcome, reason codes, actor attribute snapshot (see 8) | no object names or marked text by design; SIEM forwarding drops details |
| Backups | operator's; documented as CUI | `docs/ops/backup-restore.md` |
| Demo (Cloud Run) | synthetic data only | section 7.12 |

## 4. How markings are enforced

### 4.1 Marking model (verified, ADR-0004, `src/Keel.Core/Markings.cs`)
Jurisdiction `None`, `EAR`, `ITAR`; `eccn` required for EAR, `usml_category` required for ITAR; CUI categories (ITAR/export data must carry `EXPT`); distribution statement A to F with controlling office and reason; program; owning organisation; list of export authorization ids (for example TAA or DSP-5 ids as assumed in A2).

### 4.2 The access policy (verified, `src/Keel.Security/AccessPolicy.cs`)
All clauses must hold, missing attributes fail closed:
1. Export: object not export controlled, or EAR with ECCN `EAR99`, or `subject.UsPerson`, or the subject's export authorizations overlap those listed on the object.
2. Program need-to-know: the object's program is null or among the subject's programs.
3. CUI: every CUI category on the object is held by the subject.
4. For Modify: the subject's org equals the owning org, or program membership.
Plus a role check per action (Read, Download, Modify, Sign, Administer). Reason codes are stable and audited (`export.us_person_or_authorization_required`, `program.not_member`, `cui.category_missing:<cat>`, `org.not_owner`, `role.missing`).

How `UsPerson`, citizenship and authorizations arise: they are claims from the customer's IdP, "fed by the security office" (A2). Keel does not determine whether a person is a U.S. person. It records the claim and the audit log stores it as part of the actor snapshot (`src/Keel.Audit/AuditChain.cs`: `usPerson`, `citizenship`). **For the official to decide:** whether the IdP-asserted attribute is a sufficient control, and who maintains it.

### 4.3 One policy, two evaluators; read filters
`Evaluate` (single object) and `BuildReadFilter` (SQL predicate for every list, search, BOM, where-used and report query) are one policy; a test compares them against PostgreSQL over generated subjects and markings (`tests/Keel.IntegrationTests/AccessFilterAgreementTests.cs`; described in ADR-0004 as a release gate). Architecture tests flag queries over marked tables that do not use the filter (ADR-0004 consequences; `docs/CLAUDE.md` conventions). Unit tests of the policy: `tests/Keel.UnitTests/AccessPolicyTests.cs` (foreign person without authorization denied; with a listed authorization permitted; unrelated authorization denied; administrator denied).

### 4.4 Redaction and absence
Structural queries (BOM, where-used) show a redacted placeholder (quantity, find number, unit only) for lines the reader cannot see (A4). Search results the reader cannot read are absent, not flagged (ADR-0004). Unreadable single objects answer "not found" and the denial is audited.

### 4.5 Banners, stamped output and marked downloads
- Every page renders inside `PageFrame` with the aggregate marking banner of what is shown, top and bottom (architecture test; `docs/CLAUDE.md`).
- PDF downloads are stamped on each page with banner, notices (including distribution statement text per DoDI 5230.24), CUI designation and a traceability line (`src/Keel.Documents/Marking/PdfMarker.cs`, `MarkingStatements.cs`). Other file types are delivered in a ZIP with a PDF cover sheet and `MARKINGS.txt` (A10, `MarkedZip.cs`). Responses carry an `X-Keel-Markings` header (ADR-0035 listing). A PDF that cannot be stamped is refused or sent in a marked ZIP, never shown unmarked (ADR-0027 section 5).
- The DoDI 5230.24 "date of determination" is not captured, so stamped statements omit it (A22). Flag for the official.
- In the 3D and image viewer the bytes are unstamped with an on-screen overlay that is a handling aid, not DRM (VWR-4, VWR-6 in `docs/increments/6.0-viewer.md`).

### 4.6 Administrators
Administrators do not bypass markings: a policy clause 1 applies to every role (`AccessPolicy.cs` comment; ADR-0004 "Administrators do not bypass markings"; test `Administrators_do_not_bypass_export_control` in `AccessPolicyTests.cs`). Administrators can change configuration, markings (with a reason, and for locked revisions only through change control), clearance profiles and sinks, all audited. A DBA with database access is outside the application policy: superusers can read tables and can disable triggers (`docs/ops/hardening.md`: "superusers can disable the audit triggers"; pgAudit recommended). Records execution is a separate role and a two-person rule applies for marked disposal requests (FEAT-REC-2, FEAT-REC-9).

### 4.7 Audit
Hash chain `hash = SHA-256(prev_hash || seq || payload)`, insert-only role, immutability triggers, signed ECDSA P-256 checkpoints, offline verifier `keel-audit verify` (ADR-0005). Payload content: identifiers, action, outcome, correlation id, reason codes and the actor snapshot. Several modules state that audit details carry numbers, codes and counts only (ADR-0037 consequences, ADR-0044 decision 7, `docs/ops/notifications.md`). A scan test exists for sourcing (ADR-0037); a repository-wide proof that no marked text is ever put in an audit payload is **unverified**. Denials are audited. 401 responses are logged, not chained (ADR-0005).

## 5. Foreign-person and supplier rules

| Rule | Where | Verified |
|---|---|---|
| Foreign person (not `us_person`, no matching listed authorization) cannot read ITAR data or EAR data with an ECCN other than EAR99 | `AccessPolicy.cs`, A3 | yes |
| Authorization on the object plus held by the person is the only exception for people | clause 1 | yes |
| Outbound to a foreign recipient: cleared for B to E only through an authorization listed on the object and held by the recipient; F never automatic | `OutboundRelease.cs`, INT-6 | yes (assumption pending confirmation) |
| Supplier has an explicit `is_foreign` flag; a country other than USA forces it | `db/migrations/0020_sourcing.sql` (`supplier_foreign_country` check) | yes |
| A foreign supplier is never selected for, or sent, an ITAR-marked part; no override, not even by an export authorization on the part | service `SourcingRules.CheckSupplierForPart`; DB triggers `entry_foreign_itar`, `recipient_foreign_itar`, `request_part_foreign_itar` in migration 0020; read-time check; FEAT-SRC-2 | yes (trigger text read) |
| Flagging a supplier foreign is refused while readable ITAR entries exist | ADR-0037 section 3 | claim read, code not traced |
| EAR-marked parts and foreign suppliers: governed by the clearance profile only | FEAT-SRC-2 | yes |
| Supplier portal users are linked to exactly one supplier; the supplier's clearance profile must allow the request's markings and each part; no profile means nothing is received; a foreign supplier never receives ITAR whatever its profile | ADR-0037 section 6; `tests/Keel.IntegrationTests/PortalTests.cs`, `SourcingOracleTests.cs` | design and tests named; tests **not run in this packet** |
| `plm-supplier` role grants no PlmAction, so internal reads fail closed | `src/Keel.Core/Subject.cs` | yes |
| The `is_foreign` flag is "foreign ownership, control or foreign-person staff with access", set by people | migration comment | yes; correctness of the flag is a data-quality question |

Increment 8.2 notes (`docs/CLAUDE.md` status) say sourcing was merged but not verified end to end because Docker was down at that time. **Unverified** here: current end-to-end state of sourcing and portal.

## 6. All outbound channels

Principle in the design: data leaves only through paths that apply either the user's read policy (a person is the recipient) or the outbound release check against a declared recipient profile, and nothing is partial: an object is released whole or refused (`OutboundRelease.cs`, ADR-0025).

| # | Channel | What leaves | Control applied | Reference |
|---|---|---|---|---|
| 1 | Integration outbox to webhook sinks | event JSON (e.g. `part.released`, `bom.published`, `change.released`) with markings | `OutboundRelease.Evaluate(profile, markings)` at dispatch; withheld events send nothing; HTTPS only, host allow-list default deny, SSRF guard, no redirects, no proxy, HMAC-SHA-256 signature; re-evaluate is an audited admin action | ADR-0025, `docs/ops/integration.md`, `docs/integration/erp-contract.md` |
| 2 | File-drop sink | atomic files plus manifest with SHA-256 in a configured directory | same outbound check; the receiver is whoever reads the directory (operator-controlled) | `docs/ops/integration.md` |
| 3 | Clearance profiles | declare recipient category (public, USG, DoD, USG contractor, DoD contractor, foreign), `UsPersonsOnly`, allowed jurisdictions, ECCN and USML patterns, CUI, programs, statements, held authorizations | validated (`ClearanceProfile.Validate`); statement F cannot be listed | `src/Keel.Security/OutboundRelease.cs` |
| 4 | Pull API `/api/v1/integration/*` | events, BOMs, changes | service account role `plm-integration`; Read only, filtered by the account's own markings (ABAC) | ADR-0025, INT-17 |
| 5 | Technical Data Package export | ZIP: cover sheet, part JSON, BOM CSV/JSON, document files through the marked-download pipeline, manifest with SHA-256, ECDSA P-256 signature | needs Read on the part and Download on every document; every object must be releasable to the declared recipient profile; refused with counts only otherwise; package markings are the minimal cover; two programs refused; record and audit event commit before release; the record stores the manifest, not the package | ADR-0025, INT-12 to INT-16, `tests/Keel.IntegrationTests/TdpTests.cs` |
| 6 | Release package PDFs | PDF with cover, BOM, document list, signatures, change summary | refuse (never redact) if any included object is unreadable; download re-checks read of every object; aggregate banner and notices on every page; immutable record; the PDF is not digitally signed | ADR-0044 decision 3, A53 |
| 7 | Excel and CSV BOM export | resolved EBOM/MBOM with markings column, banner rows | caller's read filter, restricted placeholders, formula neutralization, cap 100,000 lines | ADR-0044 decision 4 |
| 8 | Report builder export (CSV, xlsx, PDF) | rows of eight datasets | each dataset uses the owning module's access-filtered search; banner repeated on the file; 5,000 row cap | ADR-0044 decision 6 |
| 9 | Product compliance exports (conflict minerals CSV, material declaration XML) | part-level declaration data | roll-up only of what the caller may read | `docs/compliance/exports.md` |
| 10 | Marked document download and viewer | files | Read and Download checks, stamping or marked ZIP, `document.download` / `document.view` audit | sections 4.5, ADR-0027 |
| 11 | CAD connector downloads (`keel-cad`, `/api/v1/cad`) | package ZIPs of unchanged files with cover sheet and `MARKINGS.txt` | same document authorization and audit; bytes are deliberately unstamped so they round-trip (FEAT-CAD-4); the connector runs on the user's machine | `docs/integration/cad-connector.md`, `docs/increments/8.0-cad.md` |
| 12 | Supplier portal | request content, files to a supplier user; responses and files from the supplier | scoped by one link, supplier clearance profile, foreign-supplier ITAR rule | ADR-0037 |
| 13 | SIEM forwarding | RFC 5424 over TLS or NDJSON file: sequence, hashes, actor id, action, outcome, object reference, correlation id | never forwarded: object names, descriptions, markings, event details, actor attributes, file names | `docs/ops/siem-onboarding.md`. Object references are identifiers (numbers); that identifiers contain no controlled information is a customer naming matter |
| 14 | Notifications and e-mail | in-app pointers; optional mail with constant subject and body, no object, kind, person or marking | opt-in; recipient domain allow-list mandatory; STARTTLS or TLS enforced; user types own address (not verified) | `docs/ops/notifications.md`, A42 |
| 15 | Records archives and disposition certificates | signed ZIP of released and obsolete revisions, files, signatures, audit extract | one program per archive; two-person rule for marked requests; read access re-checked when shown | ADR-0041 section 6, FEAT-REC-9 |
| 16 | Config export, migration tools, `keel-audit`, `keel-vault` tools | configuration, verification output | operator tools run by administrators on the host; they bypass the UI policy by nature (database-level) | `docs/CLAUDE.md`, ADR-0005 |
| 17 | Backups | database and vault copies | operator (documented as CUI) | `docs/ops/backup-restore.md` |
| 18 | Logs and metrics | JSON logs with correlation ids; Prometheus metrics | metrics endpoint to be reachable from the monitoring network only; content of logs: whether marked text can reach logs is **unverified** | `docs/ops/hardening.md` |

Message broker sink (ADR-0055): it is an ordinary sink, so the dispatcher evaluates the clearance profile before it is called and a withheld event is never published (tested against a real RabbitMQ with a broker-side message count); the broker host follows the webhook on-premises rules. Not present (verified): AMQP 1.0 and Kafka sinks (refused by name); no automatic reporting to third parties; no telemetry from the product (the dev vault runs with telemetry off, `-master.telemetry=false`; .NET CLI telemetry opt-out is set in the images, `Dockerfile`). Whether any of the third-party containers phone home in a customer deployment is **unverified**.

## 7. Facts relevant to classification, hosting and development

### 7.1 Encryption in use
- In transit: TLS 1.2 or 1.3 from users to the proxy (Caddyfile and nginx examples), PostgreSQL `SSL Mode=VerifyFull`, vault `https://`, IdP `https://`, webhooks, SMTP (STARTTLS or TLS), SIEM (TLS, mTLS optional). The hop proxy to app is plain HTTP inside a private network by default (`docs/ops/hardening.md`). clamd INSTREAM is unencrypted on TCP.
- Algorithms in Keel code (grep of `src`): SHA-256 content hashes, HMAC-SHA-256 webhook signatures, ECDSA P-256 signatures (audit checkpoints, TDP manifests, records archives). No `Aes`/`AesGcm`/`RSA.Create` call was found in `src` (grep on 2026-10-29). Session cookies use ASP.NET Core Data Protection (framework; key ring volume must be protected). Object encryption is requested from the store (SSE-S3 AES256), performed by the store.
- TLS and general cryptography come from the platform's OpenSSL, not from code shipped in Keel (ADR-0008). The Caddy example uses Go's own TLS stack (`docs/ops/compose-example.md`).
- FIPS posture: design relies on RHEL 9 FIPS mode; `Keel:Security:RequireFips=true` (default in `appsettings.json`) refuses to start unless `/proc/sys/crypto/fips_enabled` is `1`; readiness reports it. The check proves the kernel flag, not that a container's OpenSSL runs the FIPS provider. **Unverified:** behaviour on a real FIPS host, the container OpenSSL provider, and that any validated module is in use. The demo sets `RequireFips=false` (`deploy/demo/entrypoint.sh`).
- Encryption registration or classification of this functionality is a question for the official (Q7).

### 7.2 Identity and sessions
OIDC code flow with PKCE, JWT for API, HTTP-only cookie session, idle lock 15 minutes default, absolute 8 hours, e-signature step-up within 300 seconds (ADR-0007, ADR-0014, ADR-0026). Passwords and lockout are the IdP's.

### 7.3 Logging and audit content
See 4.7. The audit payload contains the actor's `usPerson` flag and citizenship code (verified `AuditChain.cs`); this is personal and attribute data inside the database, not forwarded to SIEM.

### 7.4 Administrator access
See 4.6. There is no break-glass bypass in the policy code read (`AccessPolicy.cs`). Administrators can assign roles only at the IdP, not in Keel (A1, ADR-0007), so the IdP administrators are the party that can grant `us_person` and authorizations. **For the official to decide:** separation between IdP administration and Keel administration.

### 7.5 Data residency and hosting for customer deployments
- Production design: on-premises, air-gapped, offline signed bundle; no call to anything outside the site at run time (ADR-0011, ADR-0020, ADR-0027 "nothing is loaded from a CDN"; the viewer's CSP `connect-src` names no network origin, `ViewerCsp.cs`).
- Kubernetes (Helm, RKE2 reference) or Docker Compose (A12). Where the customer runs it is the customer's choice; nothing in the repository ties data to a region. `Keel__Vault__Region` is a signing region label only (`docs/ops/configuration.md`).
- Hosting of customer technical data in any commercial cloud is not a documented or tested configuration. **Unverified** and a question (Q5).

### 7.6 Hosted Cloud Run demo
- One container (PostgreSQL 17, SeaweedFS, Keel host, seeder) on Google Cloud Run, service `keel-plm-demo`, region `northamerica-northeast2` (Toronto), built from the GitHub repository by Cloud Build on pushes to `main` (`docs/ops/cloud-run-demo.md`, `docs/CLAUDE.md` status). The Google Cloud project is named in those documents.
- Synthetic data only. Demo mode requires both `ASPNETCORE_ENVIRONMENT=Demo` and `Keel:Demo:Enabled=true`; with only the second the host refuses to start; the host refuses a database holding non-demo identities; the entrypoint refuses to run without both switches (ADR-0045 decision 3; `tests/Keel.UnitTests/DemoModeTests.cs`).
- Anyone who reaches the sign-in page can sign in as any demo user, including the administrator, with no password (ADR-0045 decision 4, consequences). Recommended: require Google IAM authentication; public access only for hours (`docs/ops/cloud-run-demo.md`). **Whether the deployed service currently requires authentication is unverified** (not recorded in the repository).
- Data is ephemeral and re-seeded on cold start; uploads are not malware scanned (acknowledged); SSE and FIPS are off.
- The demo's synthetic data includes records marked ITAR, EAR and CUI so that the controls can be shown (`docs/demo-script.md`; `docs/increments/1.7-demo.md`). Their content is synthetic; whether synthetic technical content could still be technical data is for the official (Q3).

### 7.7 Source repository
The repository is hosted at `https://github.com/finepointcgi/Keel-PLM` (`git remote`). Whether it is public or private is **unverified**. The repository contains source, tests, documents including ADRs, seed data (synthetic), a realm file with synthetic people, and the offline-bundle tooling. It contains no real customer data by convention (`docs/CLAUDE.md`).

### 7.8 Development process
- Much of the code and documents were produced with AI assistance. Evidence: git commit trailers `Co-Authored-By: Claude ...` on 145 of 210 commits at the time of this packet (git log, counted 2026-10-29); `docs/CLAUDE.md` says increments were "built in parallel by three agents in separate worktrees". Human authorship: two author identities appear in git history; no further identity or location data is recorded.
- Nationality, location and employer of the developers and operators of the project are **not recorded in the repository**. This packet does not guess them.
- Third-party components: suppliers named in the repository are Open Cascade SAS (OCCT), the occt-import-js authors, Deque Systems (axe-core), Cisco Talos (ClamAV, per a compose comment), Red Hat (UBI), Microsoft (.NET, many packages), Amazon (AWS SDK), and others in the SBOM. The repository does not record countries of origin. Country of origin of each is **unverified**; see [legal-open-source.md](legal-open-source.md) for the inventory.
- No code in the product is known to be written in or fetched from a place at run time: the application is built from vendored and restored packages; the demo build fetches PGDG packages and a key at build time (`Dockerfile` demo stage).
- The build and test tooling runs on developer machines and Cloud Build; where source and builds are processed (GitHub, Google Cloud, developer laptops) is a hosting fact for the official.

## 8. Mapping of INT-6 and INT-12 and related assumptions (as written in the repository)

- **INT-6**: "DoDI 5230.24 table as in the ADR; a foreign recipient is cleared for B to E only through an authorization listed on the object and held by the recipient. Needs confirmation by the export compliance office." (`docs/increments/4.0-integration.md`; `assumptions.md` A34.)
- **INT-12**: "TDP element categories follow MIL-STD-31000 as understood by the team; the standard text was not available offline. The mapping is admin-editable and should be reviewed against the standard."
- **INT-13/14/23**: TDP contents, completeness rule, two programs refused.
- A2/A3 attribute assumptions; A22 missing date of determination; FEAT-SRC-2 supplier rule.

## 9. Questions for the empowered official and counsel

All are **for counsel and the empowered official to decide**. Specific regulation sections are deliberately not cited; any citation is to be confirmed by counsel.

| # | Question | Why it arises | References |
|---|---|---|---|
| Q1 | Does the software itself need a jurisdiction and classification determination (for example ITAR or EAR, and if EAR an ECCN or EAR99), and who makes it (the supplier's empowered official, the customer, or a government determination request)? The software manages controlled data but is not itself a data set. | Product description in section 1 and architecture in section 2; encryption features in 7.1; cybersecurity-related functions (audit, access control) | `README.md`, ADR-0004, ADR-0008 |
| Q2 | Do the publication of the source on GitHub (visibility unverified) and its public availability to others affect that determination or any authorization question? Does the repository carry anything other than synthetic data? | Section 7.7 | `git remote`, `docs/CLAUDE.md` |
| Q3 | Does the public Cloud Run demo (synthetic ITAR, EAR and CUI-marked sample records; no-password sign-in; hosted by a US cloud provider in a Canadian region) raise any question about the sample content, the hosting location, or access by foreign persons? | Section 7.6 | ADR-0045, `docs/ops/cloud-run-demo.md`, `docs/demo-script.md` |
| Q4 | Do the technical-data-package categories (INT-12) and foreign-recipient exports (INT-6) need authorizations or license exceptions when a customer configures a foreign recipient profile, and is the product's INT-6 table (statement B to E through an authorization listed on the object and held by the recipient) an acceptable rule? Should the product refuse to create a profile of category foreign for ITAR altogether? | Channels 1, 2, 5 | `OutboundRelease.cs`, ADR-0025, `docs/increments/4.0-integration.md` |
| Q5 | Is hosting customer technical data in any commercial cloud, including a Cloud Run or Kubernetes service run by the supplier on the customer's behalf, within scope of any authorization the customer or supplier holds? The product is designed for the customer's own on-premises hosting. | Sections 7.5, 7.6 | ADR-0012, ADR-0045 |
| Q6 | Is the access model's reliance on IdP-asserted `us_person`, `citizenship` and `export_authorizations` claims sufficient, and who attests them? Is storing citizenship in the audit actor snapshot acceptable under the customer's privacy and personnel rules? | Section 4.2, 7.3 | A2, `AuditChain.cs` |
| Q7 | Are there encryption-related classification, registration or reporting questions for the supplier because of the cryptographic functions (TLS via platform libraries, ECDSA signatures, HMAC, SHA-256, Data Protection, SSE requests)? Is the FIPS posture description acceptable, given that FIPS-host behaviour is unverified? | Section 7.1 | ADR-0008, `docs/ops/hardening.md` |
| Q8 | Technology transfer: does access by foreign-person developers, contractors, cloud operators, or support staff of the supplier (to source, to the demo, or to a customer's deployment during support) raise licensing questions? The repository does not record who they are. The AI tooling used in development: do prompts, outputs or logs from development sessions need handling rules? | Section 7.8 | `git log`, `docs/CLAUDE.md` |
| Q9 | Foreign components: should the supplier record the country of origin and ownership of third-party components (OCCT supplier, others), and does it matter for the classification or customer procurement rules? | Section 7.8 | [legal-open-source.md](legal-open-source.md) |
| Q10 | Supplier rules: is the supplier `is_foreign` flag (human-set) and the ITAR trigger a sufficient control, including for EAR-controlled items where only the clearance profile governs? Should foreign-supplier flagging be tied to a stronger verification? | Section 5 | ADR-0037, migration 0020, FEAT-SRC-2 |
| Q11 | Distribution statements: the "date of determination" is not captured on stamped statements (A22); statement F is never released automatically. Is that adequate for customers' marking rules? Are the DoDI 5230.24 statement-to-recipient mappings in `StatementAllowsCategory` right? | Section 4.5, channel 3 | `OutboundRelease.cs`, A22 |
| Q12 | Unstamped channels: CAD connector packages and the 3D or image viewer deliver unstamped bytes (cover sheet or overlay instead). Is that acceptable for the customer's marking rules? | Channels 10, 11 | FEAT-CAD-4, VWR-4 |
| Q13 | Administrators and DBAs: administrators do not bypass the policy, but database superusers and operator tools can read everything. Does the customer need further controls (pgAudit, separation of duties) before the official is satisfied? | Section 4.6 | `docs/ops/hardening.md` |
| Q14 | Support and telemetry: does anything in a customer deployment send data to the supplier or a third party? (Product design: no.) What is the supplier's support access model, given it would be a transfer channel? | Section 6 | ADR-0011, `docs/ops/offline-install.md` |

## 10. Unverified list (collected)

1. Behaviour on a real FIPS host; container OpenSSL provider; any validated module.
2. Whether the GitHub repository is public; whether the deployed Cloud Run service requires authentication.
3. Developer and operator nationality, location, employer.
4. Country of origin of third-party components.
5. Repository-wide absence of marked text in audit payloads and logs.
6. End-to-end state of sourcing and supplier portal (increment notes say not verified when merged); integration, portal and TDP tests were not run for this packet.
7. Third-party containers' network behaviour in customer deployments.
