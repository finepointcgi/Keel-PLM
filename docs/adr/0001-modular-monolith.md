# ADR-0001: Modular monolith on .NET 10

**Status:** Accepted · 2026-10-27

## Context
Air-gapped sites run with small operations teams. PLM transactions span many concepts at once (an ECN release touches parts, BOMs, lifecycle, workflow, audit and access control) and must be atomic. .NET 8 support ends 2026-11-10 (A17).

## Decision
- One ASP.NET Core host (`Keel.Api`) on **.NET 10 LTS**, composed of modules with strict boundaries: `Keel.Security`, `Keel.Audit`, and later `Parts`, `Documents`, `Bom`, `Change`, `Workflow`, `Admin`.
- Modules depend on `Keel.Core` abstractions, never on each other's internals. A module exposes a public service interface; cross-module calls go through it.
- **One PostgreSQL 17 database**, one schema per module (`audit`, `plm`, …), so a release is one ACID transaction.
- Out-of-process workers only where isolation pays for itself: CAD/neutral-format conversion (Phase 4) and migration importers (Phase 3).
- Data access: Npgsql + EF Core for aggregates; hand-written SQL (Npgsql) for hot paths (BOM explosion, audit append) where we need control of the plan.

## Consequences
- Simple deploy (one image + workers), transactional consistency, easy local dev.
- Module discipline has to be enforced by review and an architecture test (assembly reference rules) rather than network boundaries.
- Horizontal scale is by running N stateless API replicas behind a load balancer; the database is the scaling ceiling, addressed in ADR-0003/0009.

## Alternatives
- Microservices: rejected; distributed transactions across change/BOM/lifecycle and heavy ops cost for air-gapped sites.
- Java/Spring (Windchill's ecosystem): no benefit over the requested stack.
