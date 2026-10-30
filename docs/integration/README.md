# Keel PLM integration interface

How an ERP (or any on-premises system) receives data from Keel PLM and reconciles with it. Start with [erp-contract.md](erp-contract.md).

| File | Content |
|---|---|
| [erp-contract.md](erp-contract.md) | Event catalogue, envelope, signing, delivery guarantees, retries, versioning, security, onboarding checklist, mapping guide |
| [schemas/](schemas/) | JSON Schema (2020-12) of the envelope and of every event payload, one file per type and version |
| [openapi.json](openapi.json) | OpenAPI 3.1 of the pull API, TDP export and integration administration (also served at `/openapi/v1.json`) |

CAD connectors: [cad-connector.md](cad-connector.md) (protocol), [freecad-addon.md](freecad-addon.md), [watch-folder-recipes.md](watch-folder-recipes.md), [cad-plugins.md](cad-plugins.md) (commercial vendors, nothing built).

Design background: [ADR-0025](../adr/0025-integration-outbox-erp-tdp.md). Operations: [../ops/integration.md](../ops/integration.md).
