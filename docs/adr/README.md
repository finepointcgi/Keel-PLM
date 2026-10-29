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
| [0039](0039-configuration-and-variants.md) | Configuration and variants: options, rules, 150% BOM conditions | Accepted |
| [0041](0041-records-management.md) | Records management: retention, legal hold, disposition, WORM-style archive | Accepted |
