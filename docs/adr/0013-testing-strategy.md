# ADR-0013: Testing strategy

**Status:** Accepted · 2026-10-27

## Decision
- **Unit** (xUnit): domain rules, revision scheme, markings invariants, policy evaluator, canonical audit serialization.
- **Integration** (xUnit + Testcontainers): a real PostgreSQL 17 container per test collection. No in-memory database substitutes, no mocked persistence. Covers the SQL access filter, audit chain under concurrency, DB-level protections and API endpoints through `WebApplicationFactory` with locally signed test tokens.
- **Property-based** policy-agreement test (ADR-0004).
- **E2E** (Playwright, from increment 1.7): the full demo script against the Compose stack.
- **Performance:** the BOM explosion benchmark (from 1.4) asserts the < 2 s budget on CI hardware; results are logged per run.
- Test data is synthetic only. Seed data models a multi-level assembly with changes in progress (increment 1.7).
