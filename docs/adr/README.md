# Architecture Decision Records

Format: Context / Decision / Consequences / Alternatives. Status is one of Proposed, Accepted, Superseded.
Assumptions referenced as `A<n>` live in [../assumptions.md](../assumptions.md).

| ADR | Title | Status |
|---|---|---|
| [0001](0001-modular-monolith.md) | Modular monolith on .NET 10 | Accepted |
| [0002](0002-versioned-object-model.md) | Master / revision / iteration object model | Accepted |
| [0003](0003-bom-usage-links-and-config-specs.md) | BOM usage links and configuration specifications | Accepted |
| [0004](0004-abac-and-markings.md) | Attribute-based access control and export-control markings | Accepted |
| [0005](0005-tamper-evident-audit.md) | Tamper-evident audit log | Accepted |
| [0006](0006-workflow-engine.md) | Data-defined workflow engine | Accepted |
| [0007](0007-authentication-and-esignature.md) | OIDC authentication and e-signature step-up | Accepted |
| [0008](0008-cryptography-fips.md) | Cryptography and FIPS 140-3 posture | Accepted |
| [0009](0009-search.md) | Search on PostgreSQL first | Accepted |
| [0010](0010-frontend.md) | React SPA and large-tree rendering | Superseded by 0014 |
| [0011](0011-integration-outbox.md) | Integration through REST plus transactional outbox | Accepted |
| [0012](0012-packaging-and-deployment.md) | Packaging, deployment and offline install | Accepted |
| [0013](0013-testing-strategy.md) | Testing strategy | Accepted |
| [0014](0014-blazor-server-ui.md) | Blazor (Interactive Server) user interface | Accepted |
| [0015](0015-vault-object-storage.md) | Vault object storage (S3 API, content-addressed) | Accepted |
| [0016](0016-part-document-links.md) | Part-document link semantics (iteration-owned) | Accepted |
| [0017](0017-bom-structure-and-explosion.md) | BOM structure, explosion and redaction | Accepted |
| [0018](0018-workflow-and-esignature.md) | Workflow engine and e-signature | Accepted |
| [0019](0019-change-management.md) | Change management (PR/ECR/ECN/CT, revise-in-change) | Accepted |
| [0020](0020-container-images-and-offline-bundle.md) | Container images and offline bundle | Accepted |
| [0023](0023-migration-importers.md) | Migration importers (PLMXML, Windchill extract, canonical package) | Accepted |
| [0021](0021-baselines-and-effectivity.md) | Baselines and effectivity | Accepted |
| [0022](0022-admin-configuration-and-classification.md) | Admin configuration and classification | Accepted |
| [0024](0024-ebom-mbom.md) | EBOM, MBOM and reconciliation | Accepted |
| [0025](0025-integration-outbox-erp-tdp.md) | Integration outbox, ERP events and TDP export | Accepted |
| [0026](0026-session-lock-scanning-gc-siem.md) | Session lock, upload scanning, vault GC, SIEM forwarding | Accepted |
| [0027](0027-file-viewer.md) | In-browser file viewer (3D, PDF, images) | Accepted |
| [0028](0028-ui-shell-and-workspace.md) | UI shell, design system and personal workspace | Accepted |
| [0029](0029-visual-relationships.md) | Relationship pictures: impact map, treemap, structure diff, timeline, workflow map (server-computed SVG, table equivalents) | Accepted |
| [0030](0030-notifications-activity-comments.md) | Notifications, watch, activity feed and comments (pointer-only, re-authorized on render) | Accepted |
| [0031](0031-list-sorting-and-admin-completeness.md) | Server-side list sorting (whitelisted keys) and admin completeness | Accepted |
| [0032](0032-platform-hardening.md) | Platform hardening: database-backed session lock, outbox retention, e-mail channel, broker sink deferred | Accepted |
| [0033](0033-document-review-and-license-compliance.md) | Document review and release through the workflow engine | Accepted |
| [0034](0034-accessibility-and-performance-at-scale.md) | Accessibility audit (axe-core, test-only) and performance budgets at scale | Accepted |
| [0035](0035-cad-integration.md) | CAD integration: vendor-neutral connector protocol, package check-in/out, CAD BOM proposals | Accepted |
| [0037](0037-suppliers-and-sourcing.md) | Suppliers, approved lists and supplier portal | Accepted |
| [0036](0036-quality-processes.md) | Quality processes: NCR, CAPA, deviations and waivers | Accepted |
| [0038](0038-requirements-and-traceability.md) | Requirements and traceability: specifications, trace links, verification and coverage | Accepted |
| [0039](0039-configuration-and-variants.md) | Configuration and variants: options, rules, 150% BOM conditions | Accepted |
| [0040](0040-product-compliance.md) | Product compliance: substances, regulatory lists, declarations and roll-up | Accepted |
| [0041](0041-records-management.md) | Records management: retention, legal hold, disposition, WORM-style archive | Accepted |
| [0042](0042-manufacturing-process-data.md) | Manufacturing process data: plans, routings, work instructions, tooling | Accepted |
| [0044](0044-output-and-bulk-work.md) | Output and bulk work: release packages, Excel BOM exchange, mass change, report builder | Accepted |
| [0043](0043-programs-and-schedules.md) | Programs and schedules: milestones, deliverables, gate reviews | Accepted |
| [0045](0045-cloud-run-demo.md) | Guarded self-contained demo on Google Cloud Run: demo sign-in, in-container PostgreSQL and S3 vault | Accepted |
| [0046](0046-visual-language.md) | Visual language: tokens, accent, elevation, motion and the style guide | Accepted |
| [0047](0047-open-source-cad-connector.md) | Open-source CAD connector: FreeCAD add-on, watch-folder recipes, real STEP fixtures | Accepted |
| [0046](0046-model-versioning.md) | Model versioning for CAD and mesh files: versions are document iterations, one-step upload, measured geometry, compare, restore | Accepted |
| [0048](0048-mudblazor-navigation.md) | MudBlazor navigation rail with collapsible groups (no theme provider, strict CSP kept) | Accepted |
| [0050](0050-mass-change-beyond-parts.md) | Mass change beyond parts: documents, assembly structure with where-used, requirements, change notices and CSV/Excel upload | Accepted |
| [0051](0051-signed-release-packages.md) | Digitally signed release packages: PAdES-style CMS signature, operator PKI key references, offline verification and tamper detection | Accepted |
| [0053](0053-document-compare-and-redline.md) | Cross-document compare and redline review: word diff, honest non-text compare, anchored review threads, marked HTML/PDF export | Accepted |
| [0052](0052-3d-viewer-and-assembly-placements.md) | Assembly placements from STEP and the Viewer tab: placed bounding boxes, stored and exposed through the CAD API and CLI, strict CSP kept | Accepted |
| [0061](0061-simplified-flows.md) | Simplified flows: quick add part, one-step edit/save and add document, plain-language help, Simple view navigation | Accepted |
| [0056](0056-workflow-admin-and-phone-approvals.md) | Workflow designer in administration (steps editor, drafts, preview as the workflow map) and phone approvals with a review summary of exactly what is signed | Accepted |
