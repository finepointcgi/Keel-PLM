# Performance at scale

Test: `tests/Keel.IntegrationTests/ScalePerformanceTests.cs` (service level) and `tests/Keel.E2E/ScaleTests.cs` (browser). Decision record: [ADR-0034](../adr/0034-accessibility-and-performance-at-scale.md).
Both build their data in a throwaway database inside the run's own Testcontainers PostgreSQL (tmpfs); the dev and demo databases and ports are never touched.
Measured on an Apple-silicon laptop, PostgreSQL 17 in Docker (4 GB VM), warm cache, median of five calls, through the real services and the access filter. Numbers vary by machine;
the test budgets are several times higher on purpose.

## Dataset
| | Default run | `KEEL_SCALE_FACTOR=3` |
|---|---|---|
| Part masters (incl. one 10-level, 50,000-line structure) | 108,150 | 248,150 |
| Documents | 50,000 | 150,000 |
| Changes | 20,000 | 60,000 |
| Audit events | 150,000 | 450,000 |
| Notifications (one user) | 20,000 | 20,000 |

Run: `dotnet test --project tests/Keel.IntegrationTests --filter-class "*ScalePerformanceTests"` (about 40 s; `KEEL_PERF_LOG=<file>` also appends the timings, and `TestResults/scale-performance.txt` receives timings and query plans).

## Measurements (milliseconds, median)
| Operation | 108k parts | 248k parts | Budget (x1) |
|---|---|---|---|
| Part search, first page, no query | 98 | 289 | 3000 |
| Part search, restricted caller | 73 | 120 | 3000 |
| Part search, deep page (offset 60,000) | 107 | 225 | 4000 |
| Part search, words ("gimbal bracket") | 8 | 56 | 3000 |
| Part search, number fragment | 6 | 54 | 3000 |
| Part search, state filter | 44 | 108 | 3000 |
| Part list sorted by number / name / type / state / version (asc, desc) | 79 to 98 | 248 to 288 | 3500 |
| Document list, first page / sorted by name | 46 / 48 | 174 / 178 | 3000 / 3500 |
| Change list, first page / any sort key | 16 to 20 | 44 to 57 | 3000 / 3500 |
| BOM explode, one level (structure tab first paint) | 3 | 3 | 1500 |
| BOM explode, all 10 levels, 50,000 lines | 732 | 785 | 4000 |
| Where-used, one level / all levels (standard part) | 5 / 26 | 5 / 28 | 2000 / 4000 |
| Where-used, all levels, restricted caller | 10 | 10 | 4000 |
| Home: recently viewed, favorites | 1 | 1 | 1500 |
| Home: recently released | 12 | 11 | 2500 |
| Home: changes in my programs | 15 | 41 | 2500 |
| Home: needs my attention | 4 | 6 | 2500 |
| Home: all cards in sequence | 36 | 65 | 6000 |
| Activity feed, object with 5,000 events, first 30 | 1 | 1 | 1500 |
| Notifications: unread count, 20,000 rows | 1 | 1 | 1000 |
| Notifications: first page / deep page / unread only | 20 / 22 / 19 | 20 / 22 / 19 | 2500 / 3000 / 2500 |

(The BOM explode budget of 2 s for 50,000 lines in `BomPerformanceTests` is the older, tighter gate.)

## Query plans and indexes
- Every list query reads the whole access-filtered set once, because `count(*) OVER ()` needs every readable row for the total ("Showing 1-50 of N"). The cost is linear and small
  (a sort of 108,000 rows costs about 60 ms in the executor; plan: hash join, WindowAgg, top-N heapsort). An index on a sort key cannot remove that pass, so **GAP-A-5 is closed
  without a migration**: no sort-key index is added. Re-measure with `KEEL_SCALE_FACTOR` before adding one.
- Recently released parts and documents (home): 4.7 ms and 6.9 ms at 108k rows using `part_revision_state_idx` / `document_revision_state_idx`; a partial index on `state_changed_at`
  would save milliseconds and is not added.
- Activity feed uses `event_object_idx` (also asserted by `ActivityFeedTests` at 100,000 events). Notifications use `notification_user_time`.
- Migration number 0018 stays unused.

## Browser at 50,000 lines (`ScaleTests`)
A separate database and host hold a 38,000-part, 50,000-line structure. Asserted: first level appears quickly; "Expand all" loads every line into the circuit and the browser holds at most 120
grid rows and fewer than 4,000 DOM nodes (`aria-rowcount` is about 50,001); scroll to the end and to the middle keeps the DOM small and the last line reachable; the purchasing summary,
where-used and comparison tables are paged at 100 rows; the structure-difference picture draws at most 500 lines (with a notice; the table is complete); the treemap stays under 6,000
elements; banners are present and the accessibility audit passes with the big grid on screen. Time budgets there are generous (expand all under 90 s; others under 30 s).
Timings of the last run are in the fixture log (`TestResults/e2e/`).

## End-to-end load smoke (ADR-0060, measured 2026-10-30)
The numbers above are service-level (no HTTP, no authentication). `scripts/ops/load-smoke.sh` measures what a client sees: the **production image** with the limits of the compose bundle (application 2 CPUs and 2 GiB, PostgreSQL 2 CPUs and 4 GiB, vault 1 CPU and 2 GiB, read-only root, uid 1001), real Keycloak tokens, the real access filter, and the in-repo client `tests/Keel.LoadTool` (closed loop, 20 s per phase, 3 s warm-up; no third-party load tool, k6 is AGPL). Data: a synthetic structure of 50,000 usage lines over 8 levels (36,362 part masters before the run, 61,628 at the end because the run creates parts). Everything ran on one machine (Apple silicon, Docker VM with 8 CPUs and 15 GiB, PostgreSQL and the vault in tmpfs, the client on the host), so latency includes no network and no disk. Full table and conditions: [drill-results/load-smoke.md](drill-results/load-smoke.md).

| Operation (what it is) | 1 worker p50 / p95 | 8 workers p50 / p95 | 32 workers p50 / p95 | Errors |
|---|---|---|---|---|
| list: a page of 50 parts at a random offset (about 25 KB) | 66 / 75 ms | 305 / 392 ms | 1,206 / 2,301 ms | 0 |
| search: free text from names and numbers in the data (small result sets, about 1.4 KB) | 2 / 4 ms | 4 / 53 ms | 20 / 83 ms | 0 |
| create-part: `POST /api/v1/parts` (writes the part and its audit event in one transaction) | 5 / 8 ms | 15 / 27 ms | 54 / 87 ms | 0 |
| BOM expand, one level of the root (about 10 KB; the structure tab's first paint) | 3 / 5 ms | 5 / 47 ms | 23 / 70 ms | 0 |
| BOM expand, every level of the root (46 MB of JSON) | 935 / 981 ms | 4,984 / 5,430 ms | not run | 0 |

How to read it, and its limits:
- **The database is the limit for list pages.** With PostgreSQL held to the bundle's 2 CPUs, a list page costs a pass over the readable part set (the `count(*) OVER ()` described above), PostgreSQL ran at about 205% CPU, and list throughput stopped at about 25 pages per second whatever the concurrency (p95 2.3 s at 32 concurrent clients). An earlier run of the same script on the same 50,000-line data with PostgreSQL unrestricted on the 8-CPU VM (PostgreSQL used about 780% CPU; that run's report was replaced by this one and is not kept) sustained about 97 list pages per second with p95 575 ms at 32 workers, so give PostgreSQL more CPU (`KEEL_POSTGRES_CPUS`) before more application replicas if list and search at this size matter to your users.
- The application container peaked at about 142% CPU and 652 MiB of its 2 CPUs and 2 GiB in this run: it was not the bottleneck.
- Search and one-level BOM expand stay under 100 ms p95 at 32 concurrent clients on this data. The search terms come from names found in the data, so the result sets are small; a query that matches thousands of parts costs like a list page.
- Full BOM expand of a 50,000-line structure is a 46 MB response and takes about one second for one client and five seconds each with eight at once. The UI does not do this (it loads a level at a time); the API allows it. It is capped at 8 workers in the smoke because many 46 MB responses in flight measure the response size, not the service.
- This is a smoke on one machine, with the client on the same host, an empty cache profile (warm-up only), one user (`alice`, a US person who can read most of the data) and no concurrent uploads, workflow, notifications or SIEM forwarding. It is **not a capacity test and not an availability or response-time commitment**. It shows the order of magnitude, finds the first bottleneck, and gives a repeatable measurement: run it against your own sizing, and `--lines`, `--concurrency`, `--duration`, `--app-cpus`, `--pg-cpus`, `--app-memory` and `--pg-memory` change the conditions.

## Operating notes
- Each page of a list costs one pass over the readable rows. If a deployment's lists pass about 1 million rows, measure with `KEEL_SCALE_FACTOR=10` before relying on this table.
- Run `ANALYZE` after bulk loads.
