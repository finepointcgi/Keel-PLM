# Keel PLM demo script (about 25 minutes)

The Phase 1 definition of done, live: log in, create a part, attach a document, build a multi-level BOM, route it through a review with
e-signatures, release it, then change it with an ECN (impact analysis), with export-control markings enforced and every step in the audit log.
All data is **synthetic** (fictional program ORCA); nothing here is real engineering data.

The "Contrast" column is general product knowledge for orientation, not a benchmark. Windchill and Teamcenter vary by version, licensed
modules and local configuration; confirm any statement against the customer's own deployment before using it in a comparison.

## Before the demo
1. Stack up and seeded (see [Reset](#reset) for a clean run): `docker compose -f deploy/compose/compose.yaml up -d`, migrate, `scripts/seed.sh --use-dev-stack`.
2. Start the API (`ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Keel.Api`) and open <http://localhost:5080>.
3. Open **four separate browser profiles or private windows** (they must not share cookies; the identity provider keeps a session per browser):
   alice (author), ada (reviewer and admin), bob (approver), and one window you reuse for lena, farid and audrey in turn (sign out between them).
   Password for every user: `keel-dev-<username>`.
4. Sign **ada and bob in now**. Signing needs a login younger than 5 minutes; by the time you reach step 6 their logins are old, so the
   step-up (a second password prompt at Keycloak) appears naturally. If you reach the signing step within 5 minutes of their login, wait or sign them in earlier.
5. Numbers below are what a fresh seed produces (they are deterministic); the seeder prints them if yours differ.

| Object | Number |
|---|---|
| Top assembly "Unmanned sensor pod, ORCA" (In Work, 5 levels) | P-000001 |
| Pressure housing assembly / Gimbal assembly (Released) | P-000002 / P-000006 |
| Sensor payload assembly (In Work, contains a KRAKEN line) | P-000003 |
| Avionics bay assembly (Under Review, ada's task open) | P-000004 |
| Gimbal bracket (Released A) | P-000015 |
| Sensor mounting plate (Released A, in the Class I ECR) | P-000023 |
| Mounting interface plate (lists TAA-0042) | P-000029 |
| Field spares kit (open assembly with restricted lines) | P-000010 |
| KRAKEN part | P-000039 |
| Gimbal bracket drawing / CAD placeholder / partner drawing | D-000002 / D-000007 / D-000011 |
| ECN draft with revision B in work / under review / rejected | ECN-000001 / ECN-000002 / ECN-000003 |
| ECR awaiting the CCB (Class I) | ECR-000001 |

## Getting around (new in UX-1)
- After sign-in you land on **Home**: Needs my attention, Recently viewed, Favorites, Changes in my programs, Recently released. Each card shows only what the signed-in user may read.
- The left rail groups navigation: Work (Home, My tasks), Product data (Parts, Documents, Manufacturing BOMs, Baselines), Change, Configuration and Administration (admins), Tools (Access check, Technical data packages, Audit log for auditors). Below 768 px it is a menu button.
- **Ctrl+K** (or **/**) opens the command palette: type a number or name, or a prefix (`p:`, `d:`, `ecn:`, `b:`, `mb:`); Enter opens, Ctrl+Enter opens in a new tab. In step 5 show that lena cannot find P-000010 there.
- Object pages are tabbed: Overview, Structure, Documents, Changes, Review & release, Classification, History, Security markings. Check out, check in, undo and state moves are in the sticky bar under the header; the lifecycle stepper shows where the object is.
- The user menu (top right) switches theme (system, light, dark, high contrast) and density; the choice follows the user to the next sign-in.

## Walkthrough

| # | Min | Who | Do | Say and show | Contrast (Windchill / Teamcenter) |
|---|---|---|---|---|---|
| 1 | 2 | alice | Open the site, sign in at Keycloak. Land on **Home**, then open **Parts** in the rail. | Server-side OIDC sign-in; production uses the site IdP with CAC/PIV. Every page has a top and bottom **marking banner**; the list banner is the aggregate of what is on screen, each row has a marking chip. Mixed ITAR, EAR (9A610, 3A611), EAR99 items in one list. | Security labels or classification attributes plus ACL/domain policies, usually set up and customized by administrators; banners and aggregation on every view are typically custom UI work. |
| 2 | 3 | alice | **New part**: name "Demo cable clamp", type Component, material 6061-T6. Leave the ITAR defaults. Create. | Markings are mandatory and validated (ITAR needs a USML category, export-controlled data must be CUI//EXPT, no distribution A). The banner changes to `CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA`. | Export attributes are usually added as custom attributes or classification schemas; the validation rules are customer-built. |
| 3 | 2 | alice | **Documents > New document**, upload any PDF, then open the part, **Check out**, open the **Documents** tab and add the document number under Linked documents (describes), **Check in**. | Files go to the vault (S3 API; SeaweedFS in dev) with SHA-256 and server-side encryption; part to document links belong to the iteration (A.2 now). | Content vault and part-document association are native; the S3 vault and per-iteration link snapshot are specific to Keel. |
| 4 | 3 | alice | Open **P-000001**, **Structure** tab. Choose **Expand all**. Open **Where used** on a fastener. Open **Purchasing summary**. | Five-level BOM (top, housing, gimbal, drive module, motor and gear), about 70 lines, virtualized grid with keyboard navigation. One line in the payload assembly reads **Restricted item**: it is a KRAKEN-program part alice may not see, so only quantity and find number show. Where-used is permission-filtered too. | Structure and where-used are core features of both; per-line redaction for need-to-know programs normally relies on access rules that hide the part, which often removes the line or errors the BOM view. |
| 5 | 3 | lena, then farid | In the shared window: sign in as **lena**, search for P-000015, open `/parts/P-000015`, open **P-000010** (Field spares kit). Sign out; sign in as **farid**; open **P-000029** and **D-000011**, then P-000015. | Lena is a non-U.S. person without authorization: the bracket is not in search, and the direct URL says "not found" exactly as for a missing part (no existence leak). The open spares kit is hers, but three lines are redacted. Farid (GBR, TAA-0042 listed on the object) sees the plate and its drawing, still not the bracket. Denials are audited. | Nationality-based gating needs user attributes in the IdP or directory and policy rules that check them; "same answer as missing" is rarely the default. |
| 6 | 4 | alice, ada, bob | alice: open **P-000003**, open the **Review & release** tab, **Submit for review**: reviewer **ada**, approver **bob**, **Start review**. Note the lock. ada: **My tasks > Technical review**, choose **Reviewed**; Keycloak asks for the password again (**step-up**), then **Reviewed** again. bob: **Approval** task, **Approved**, step-up again. | Separation of duties (the submitter cannot sign; reviewer and approver must be eligible to read the object). The part shows **Locked** while under review. The signature needs a fresh authentication (`auth_time` within 5 minutes), records who, meaning, time and a hash of the exact content, and the release happens in the same transaction. Show "Signed by Bob Approver (bob) ..." and the release history, then **Workflow details > Verify signatures**. | Sign-off tasks exist in both; re-authentication at signing, hash-bound content and atomic release with the signature usually depend on enabling and verifying specific electronic-signature options in the version in use. |
| 7 | 5 | alice, bob | **Changes**. Open **ECN-000001** (Draft, class II). Show affected object (gimbal bracket A) and resulting **revision B in work**. **Run impact analysis**: where-used up the tree (gimbal, housing, top assembly, spares kit), linked documents (drawing, CAD placeholder, vibration report), other open changes. Submit for review (ada, bob), sign both (ada first, then bob; step-up may repeat). Open the bracket: **Rev B (Released)**, **Rev A (Obsolete)**, links and structure carried over. | A released item changes only through a change notice; revision B is created when the part joins the notice and is locked with it. Class I needs a CCB-approved ECR: open **ECR-000001** (Class I, bob's CCB task open), sign as bob, then **Open change notice**. Also show ECN-000002 (approver pending), ECN-000003 (rejected, back in Draft with the reason), the change tasks, and PR-000001/PR-000002. | Change objects (problem report, request, notice, task) are native in both; impact analysis that combines where-used, documents, other open changes and marking-aware redaction is usually assembled from reports or customization. |
| 8 | 3 | audrey | Sign in as **audrey**, open **Audit log**, **Verify chain**. | Newest first: part, document, BOM, workflow, signature and change events, plus the denials from step 5. The chain is hash-linked and signed checkpoints are written periodically; audrey can verify without being able to change anything, and `keel-audit verify` does the same offline. Admin ada can create checkpoints but, like everyone, gets no bypass of markings. | Audit trails exist in both; tamper-evidence (hash chain, signed checkpoints) and an offline verifier are typically provided by database-level controls or add-ons. |

## What's new in the seeded data (Phase 2 and 4 content)
The seed now also goes through the Phase 2 and Phase 4 services, so these pages have content from the first minute. The walkthrough above is unchanged (part and document numbers, ECN-000001 to ECN-000003 and the counts of 63 parts, 12 documents and 8 changes are the same); the seeder prints this list too, and `--manifest` writes it as JSON (`extras`).

| What | Where | Number or name |
|---|---|---|
| Classification tree: Fasteners > Screws > Socket head; Machined parts > Brackets; Electronics > Modules, with class attributes (thread, length, drive size, minimum wall, nominal voltage) | Administration, Classification | FASTENERS, SCREWS, SOCKET-HEAD, MACHINED, BRACKETS, ELECTRONICS, MODULES |
| 13 parts classified with values before their release (five socket head screws, four brackets and plates including the gimbal bracket P-000015, three modules, the forward shell) | the part pages and the classification facet search | for example wall thickness 2.0 mm on P-000015 |
| Design baseline of the Gimbal assembly, established directly (frozen before the yoke change) | Baselines | BL-000001 |
| Release baseline of the pressure housing, established through review with two e-signatures | Baselines | BL-000002 |
| Effectivity context with Block 1 and Block 2 | Administration, Effectivity | ORCA-POD |
| In-work assembly with two alternates by serial for find number 30 (screws M5 for serials 1 to 100, M4 from 101) | the BOM of the partner mounting interface assembly | P-000005 |
| Revision B of the gimbal yoke effective from serial 101 through an ECN released with effectivity; revision A stays Released for serials up to 100 | the yoke page and the ECN | P-000016, ECN-000004 |
| MBOM for PLANT-A of the gimbal drive module, released through review with signatures; MBOM of the pod left in work with a stale reconciliation after an EBOM change | MBOMs | MB-000001 released, MB-000002 stale |
| TDP export record (the real service wrote the signed ZIP to a throwaway stream; only the manifest is kept) for the gimbal bracket to the recipient profile `orca-prime-contractor` | Administration, Integration, and the part's export history | P-000015 |
| Clearance profiles `orca-prime-contractor`, `partner-taa-0042`, `public-release`; the file-drop sink `erp-staging-drop` is **disabled** (a sink pointing at a system that does not exist would only produce dead letters) | Administration, Integration | |
| ERP outbox events written by the releases (`part.released`, `bom.published`, `baseline.established`, `change.released`, `mbom.released`); no dispatcher runs in the seed | Administration, Integration, Outbox | about 70 events |

The demo host needs `Keel:Integration:FileDropRoot` (and a TDP signing key) in its own configuration before the sink can be enabled or a package exported again; the seeded export was signed with a key that existed only during the seed, so verifying that record against your site's keys reports an untrusted signature, by design. The numbers above are what a fresh seed prints.

## What this demo does not show yet
Say this plainly if asked; it is on the roadmap, not hidden.
- **CAD integration** (check-in from CAD tools, viewing, attribute sync): only a CAD placeholder file exists (Phase 4).
- **Redlines** and markup. Effectivity, baselines, alternates and the MBOM exist and are seeded (see above) but are not part of the 25-minute walkthrough.
- **Migration** (`keel-import`) and the **ERP integrations** exist (docs/migration, docs/integration) but are not part of the walkthrough; notifications do not exist.
- **High availability, backup and disaster recovery, SIEM forwarding, idle-session lock, CAC/PIV login**: Phase 5 or site integration (the dev IdP is Keycloak with passwords).
- **Containers, Helm and the offline bundle** are delivered separately in increment 1.7 packaging.
- Workflow template editor, delegation and escalation, documents as affected objects, and a signing path for change tasks are Phase 2.
- The only users who can approve in the dev realm are bob (approver) and ada (admin, reviewer only); there is no second approver account.

## Reset
Return to a clean, seeded state (this deletes dev data; it never touches anything else):
```bash
docker compose -f deploy/compose/compose.yaml down -v     # removes the dev database, Keycloak state and vault objects
docker compose -f deploy/compose/compose.yaml up -d
cd src/Keel.Api && ASPNETCORE_ENVIRONMENT=Development dotnet run -- --migrate && cd ../..
scripts/seed.sh --use-dev-stack
```
`down -v` removes the compose project's volumes only. Without wiping, re-seeding is refused (the tool exits with code 2 when parts exist). For a different database set `Keel__Database__AppConnectionString` and the `Keel__Vault__*` variables first.

## Troubleshooting
| Symptom | Cause and fix |
|---|---|
| The seed hangs or fails at documents | The vault cannot write (Docker disk nearly full leaves SeaweedFS with no volumes). The tool pings the vault first and stops after 20 s; free Docker disk, restart `vault`. |
| "refusing to run" (exit 2) | The database already has parts. Reset, or pass `--allow-non-empty` knowingly. |
| Signing never asks for the password | The login is younger than 5 minutes (step-up only when stale). Sign in earlier, or wait. |
| Sign-out then sign-in shows the same user | The identity provider session is per browser. Use separate profiles or private windows per user. |
| "No eligible users found" in the review dialog | Users appear in the directory after the seed or their first sign-in; reviewer needs a work role and must be allowed to read the object (lena, carmen, farid cannot review ORCA ITAR items); only bob holds the approver role. |
| Part "not found" for a part you just saw | Markings or program of the signed-in user; this is the point of step 5. Check **My access**. |
| Audit page shows "Chain INVALID" | Someone tampered with `audit.event`, or the seed ran with a different checkpoint key. Check the failure text; a first run on a fresh database is valid. |
| Keycloak redirect error | The realm's redirect URI is `http://localhost:5080`; run the API on that port. |
| Console error about inline styles | Known notice from Blazor Virtualize under the strict CSP; the BOM grid ships a shim, see [1.4 notes](increments/1.4-bom.md). |

## Seeing relationships (UX-3)
Sign in as alice. 1) `/changes/ECN-000001`, tab **Impact map**: the notice at the left, P-000015 (A to B), the assemblies that use it, documents; arrow keys follow connections, Enter opens, **Table** shows the same data, **Export SVG** downloads the picture with its banner. 2) `/parts/P-000001`, **Structure**, choose **Treemap**: drill into an assembly, Backspace goes up, colour by markings shows the hatched Restricted block. 3) `/parts/P-000005/compare-units`: ORCA-POD serial 100 against 101, **Side by side**, Next change. 4) `/parts/P-000015` and `/parts/P-000016`, tab **Timeline** (the ECN-000004 effectivity entry). 5) `/changes/ECN-000002`, **Review & release**: the workflow map shows the pending approver. As lena the notice and bracket are "not found" and nothing restricted is named.
Screenshots: docs/ux/screenshots (impact-map, bom-treemap, structure-diff, timeline, workflow-map).
