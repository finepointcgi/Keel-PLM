# Keel PLM user manual

Keel is an on-premises, air-gapped product lifecycle management system for controlled engineering data: parts, bills of materials, documents and CAD, change control, quality, sourcing, manufacturing data, requirements, compliance, records and programs, with export-control markings on everything.

This manual is written from the user's point of view, with task-oriented steps. The screenshots are real: they were captured from the running application with the synthetic demo data (fictional program ORCA), at 1440 by 900 in the light theme, with a few dark, high-contrast, compact and 375 px examples. Nothing here is real engineering data.

> **Where the manual and the product differ.** The product is the authority. Behavior that is not shipped is said so in the chapter where you would look for it ([What is not shipped](#what-is-not-shipped)).

## Contents

| # | Chapter | You will learn |
|---|---|---|
| 1 | [Getting started](01-getting-started.md) | Signing in, the development accounts, My access, Home |
| 2 | [Navigating Keel](02-navigating.md) | The rail, the command palette, search, themes, density, phones, session lock |
| 3 | [Parts](03-parts.md) | Find, create, check out and in, lifecycle moves, markings |
| 4 | [BOMs, variants and manufacturing BOMs](04-boms-variants-mbom.md) | Structure, where-used, structure diff, 150% BOM and variants, MBOM and reconciliation |
| 5 | [Documents and the vault](05-documents-and-vault.md) | Documents, viewer, marked downloads, links, model versions, compare and redline |
| 6 | [Lifecycles, workflow and e-signatures](06-lifecycles-workflow-signatures.md) | Submitting for review, tasks, signing with step-up |
| 7 | [Change management](07-change-management.md) | PR, ECR, ECN, impact analysis, release |
| 8 | [Baselines and effectivity](08-baselines-effectivity.md) | Freezing a configuration, serial and lot applicability |
| 9 | [Markings, export control and the access check](09-markings-and-access.md) | What you may see and why |
| 10 | [Quality](10-quality.md) | NCRs, CAPAs, deviations and waivers |
| 11 | [CAD connector and the FreeCAD add-on](11-cad-connector-and-freecad.md) | Package check-out and check-in, structure proposals |
| 12 | [Suppliers, sourcing and the portal](12-sourcing-and-portal.md) | Suppliers, AML/AVL, requests, supplier portal |
| 13 | [Manufacturing process data](13-manufacturing-process.md) | Process plans, tools and gauges, shop-floor view |
| 14 | [Requirements and traceability](14-requirements.md) | Specifications, links, suspect links, verification |
| 15 | [Product compliance](15-compliance.md) | Substances, lists, declarations, roll-up |
| 16 | [Records management](16-records.md) | Retention, legal holds, disposition, archives |
| 17 | [Programs](17-programs.md) | Schedules, dependencies, deliverables, gates |
| 18 | [Reports and output](18-reports-and-output.md) | Report builder, release package PDFs, Excel BOM, mass change |
| 19 | [Notifications and activity](19-notifications-and-activity.md) | The bell, watching, comments, activity |
| 20 | [Audit and integrity verification](20-audit-and-integrity.md) | Reading and verifying the audit log |
| 21 | [Integration, ERP events and TDP](21-integration-and-tdp.md) | Outbox, sinks, pull API, technical data packages |
| 22 | [Migration](22-migration.md) | Importing from Teamcenter and Windchill |
| 23 | [Administration](23-administration.md) | Configuring Keel in the browser |
| 24 | [Administrator and operator guide](24-administrator-and-operator-guide.md) | Install, configuration, backup, air-gap and offline packaging |

## Quick start (15 minutes)

You need the demo or a development system and the accounts in [chapter 1](01-getting-started.md). Password for every account: `keel-dev-<username>`.

1. **Sign in as alice** and look at Home. Note the banners at the top and bottom of every page.
2. **Parts**: open P-000015 *Gimbal bracket*. Read the Overview, History and Security markings tabs. ([Chapter 3](03-parts.md))
3. **Structure**: open P-000001 *Unmanned sensor pod, ORCA*, tab Structure, **Expand all**. Find the **Restricted item** line in the payload assembly P-000003. ([Chapter 4](04-boms-variants-mbom.md))
4. **Search**: press `Ctrl+K`, type `bracket`. ([Chapter 2](02-navigating.md))
5. **Change**: open ECN-000001, tab **Impact analysis**, **Run impact analysis**. ([Chapter 7](07-change-management.md))
6. **Review and sign**: on P-000003 open **Review & release**, **Submit for review** with reviewer ada and approver bob; then sign as ada and bob in separate browser profiles. ([Chapter 6](06-lifecycles-workflow-signatures.md))
7. **Export control**: sign in as lena and open `/parts/P-000015`: "not found". ([Chapter 9](09-markings-and-access.md))
8. **Audit**: sign in as audrey, **Audit log**, **Verify chain**. ([Chapter 20](20-audit-and-integrity.md))

The same walkthrough as a 25-minute demo with talking points: [demo-script.md](../demo-script.md).

## Role guide

What each kind of user needs from this manual. Roles come from your identity provider; the dev accounts are in [chapter 1](01-getting-started.md).

| If you are... | Roles | Read |
|---|---|---|
| **An engineer or designer** (alice, carmen, farid) | viewer, author | 1, 2, 3, 4, 5, 7, 9, 11, 14, 19; 10, 13, 15, 17, 18 as needed |
| **An approver or board member** (bob) | viewer, approver | 1, 2, 6, 7, 8, 10 |
| **A read-only viewer or contractor** (lena) | viewer | 1, 2, 9, 19 |
| **A manufacturing engineer** | author | 4 (MBOM), 13, 12, 18 |
| **A quality engineer** | author | 10, 12, 6 |
| **A buyer or sourcing engineer** | author | 12, 4 (coverage), 15 |
| **A program manager** | author | 17, 18, 7, 8 |
| **A records manager** (rex) | records | 16, 20 |
| **An auditor** (audrey) | auditor | 20, 9, 19 (activity), 21 (TDP history) |
| **A KEEL administrator** (ada) | admin | 23, 6 (workflows), 21, 22, 15 (list versions) |
| **An operator** (install, backup, upgrade) | none in Keel | 24, then the [operations documents](../ops/) |
| **A supplier** (sup-alpha, sup-bravo) | supplier | 12 (The supplier portal) |
| **An ERP or integration developer** | integration | 21, [integration docs](../integration/README.md) |
| **A CAD plug-in author or CAD user** | author | 11, [CAD connector guide](../integration/cad-connector.md) |

## Conventions

- **Bold** is a button, tab or menu entry exactly as it appears on the screen. `Code` is something you type, an address or a number.
- Numbers like P-000015 (part), D-000002 (document), ECN-000001 (change notice), MB-000001 (manufacturing BOM), BL-000001 (baseline) are what a fresh demo seed produces. The seeder prints them if yours differ.
- Screenshots are taken as the user named in the caption or chapter. Images are in [images/](images/).

## What is not shipped

Stated in the chapter where it matters; collected here.

- No plug-ins for commercial CAD systems; Keel does not read native CAD files ([chapter 11](11-cad-connector-and-freecad.md)).
- No manufacturing execution, work orders or machine integration ([chapter 13](13-manufacturing-process.md)).
- No renaming of a part; no attributes on documents or changes ([chapters 3, 23](03-parts.md)).
- No conditions on MBOM lines of their own, nor in baselines ([chapter 4](04-boms-variants-mbom.md)).
- No message-broker sink for events ([chapter 21](21-integration-and-tdp.md)).
- Migration has not been rehearsed against a real export ([chapter 22](22-migration.md)).
- No high-availability automation; disaster-recovery targets are proposals ([chapter 24](24-administrator-and-operator-guide.md)).
- The legal and export-control review packets in [docs/review](../review/README.md) are pending sign-off and are not legal advice.

## Glossary

| Term | Meaning |
|---|---|
| **150% BOM** | One structure holding every line of every option, with option conditions; a variant picks the lines that apply |
| **Affected / resulting objects** | The versions an ECN changes / the new versions it creates |
| **Alternate** | Another line for the same find number that applies to a different range of units |
| **AML / AVL** | Approved manufacturer list / approved vendor list (sourcing list entries) |
| **Audit chain** | The hash-linked, signed log of every action |
| **Banner** | The marking text at the top and bottom of every page: the aggregate of what is shown |
| **Baseline** | A frozen set of specific versions and pinned documents for audits and milestones |
| **BOM / EBOM / MBOM** | Bill of materials; engineering BOM; manufacturing BOM (per plant) |
| **CAPA** | Corrective and preventive action |
| **CCB** | Change control board; approves Class I ECRs |
| **Check-out / check-in** | Exclusive lock creating a working copy / saving it as the next iteration |
| **Clearance profile** | What an outbound recipient may receive |
| **CUI** | Controlled unclassified information |
| **Deviation / waiver** | Approved departure from a requirement for a scope and a period |
| **ECN / ECR / PR / CT** | Engineering change notice / request; problem report; change task |
| **Effectivity** | Applicability by serial number, lot or block, or date |
| **Find number** | The position number of a BOM line |
| **Hold** | A legal hold freezing objects against disposition |
| **Iteration** | A working save inside a revision (A.1, A.2) |
| **Markings** | Export jurisdiction, CUI categories, distribution statement, program, owning organization, export authorizations |
| **Master** | The identity of an object (number and name), shared by all versions |
| **MRB** | Material review board; signs NCR dispositions |
| **NCR** | Nonconformance report |
| **Outbox** | Events written with the business change and delivered to ERP sinks |
| **Palette** | The `Ctrl+K` search and command box |
| **Redline** | A word-by-word comparison of two document versions with insertions and deletions marked, and review comments on each change |
| **Reconciliation** | Comparing an MBOM with its EBOM |
| **Redacted placeholder** | A BOM or where-used line you may not read: shows only quantity and find number |
| **Revision** | A formal, change-controlled version (A, B...) that carries the lifecycle state |
| **Step-up** | Signing in again to sign: needed when your login is older than the allowed age (default 5 minutes) |
| **Subject** | A signed-in user with their security attributes |
| **Suspect link** | A requirement link whose requirement changed after the link was reviewed |
| **TDP** | Technical data package (MIL-STD-31000 style signed ZIP) |
| **Usage link** | A BOM line: parent iteration to child part, with quantity, unit and find number |
| **Vault** | The object storage that holds file contents, keyed by SHA-256 |
| **Variant** | A named, valid selection of options |
| **Watch** | Subscribing to changes of an object |

## About these screenshots

Captured by `tests/Keel.E2E/ManualScreenshotsTests.cs` through the end-to-end harness, against throwaway containers; the dev and demo systems are never touched. To regenerate them:

```
KEEL_E2E=1 KEEL_MANUAL_SCREENSHOTS=1 dotnet test --project tests/Keel.E2E --filter-class "*ManualScreenshotsTests"
git checkout -- docs/ux/screenshots   # if another E2E test rewrote them
```

The run needs Docker and writes the images to `docs/manual/images/`. A shot that cannot be taken is logged to `TestResults/e2e/manual-shots.log`.

Not captured: the FreeCAD add-on's own windows (they are FreeCAD's); the Keycloak step-up flow beyond its sign-in screen; real e-mail notifications; production sign-in with CAC/PIV.
