# ADR-0011: Integration through REST plus transactional outbox

**Status:** Accepted · 2026-10-27

## Decision
- A versioned public REST API (`/api/v1`, OpenAPI document checked into the repo) for reads and commands.
- Domain events (`PartReleased`, `ChangeNoticeReleased`, `BomPublished`…) are written to `integration.outbox` in the same transaction as the change. A dispatcher delivers them at-least-once to configured sinks: webhook to an on-prem endpoint, file drop, or a message broker (Phase 4). Consumers dedupe by event ID.
- Outbound payloads pass the ADR-0004 outbound-release check (distribution statement and export rules for the receiving system's declared clearance).
- No runtime calls to external/internet services, ever. The CI check `no-egress` scans config and dependencies.
