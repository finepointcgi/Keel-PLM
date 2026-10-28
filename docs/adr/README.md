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
