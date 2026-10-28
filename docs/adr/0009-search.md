# ADR-0009: Search on PostgreSQL first

**Status:** Accepted · 2026-10-27

## Decision
PostgreSQL full-text search (`tsvector` with weighted number/name/description) plus `pg_trgm` for part-number fragments, and attribute filters on typed columns and `jsonb` with GIN indexes. Every search query includes the ADR-0004 read filter in SQL, so results and counts never include inaccessible objects.

## Consequences
No extra cluster to run or secure. Revisit OpenSearch via a new ADR only if measured latency at A11 scale misses targets. The indexer would then have to replicate marking columns and apply the same filter builder.
