# Upgrade rehearsal result

Run `keel-drill-upgrade-115038-28393` on 2026-10-30T15:51:13Z, production image `keel/api:prodready` (sha256:7ab7a6f65c9e), PostgreSQL `postgres:17-alpine`. Docker VM 8 CPUs, 15 GiB; host Darwin arm64.

From: 26 migrations (through `0027_reports.sql`, read from git revision 5b9e750). To: 33 migrations shipped by this image (7 forward migrations: 0026_programs 0028_model_versions 0029_mass_change_ext 0030_pdf_signing 0031_cad_placements 0032_redline 0040_simplify_flows). Of these, 0026_programs has a lower number than the newest migration the old database already had (branches merged out of order); the migrator applies by file name, not by position, so it runs after the newer ones, and the check below shows the data survives that too.  Data before the upgrade: 40 parts through PartService, each with its audit event, and a signed checkpoint (documents are covered by the backup and restore drill); 185 tables, 505 rows, 40 audit events. After: 202 tables. Tables that gained rows: public.schema_migrations +7, wf.template +1. The forward migrations took 0.6 s in total, including the container start; the data volume is small, so this shows the migrations are correct on existing rows, not how long a large database takes (run the same script against a restored copy of the real database for that).

| Check | Result | Detail |
|---|---|---|
| the previous set ends at migration 0027 | pass | 0027 |
| no previously shipped migration file was edited (forward-only) | pass | 0 |
| the previous migrator state: 26 migrations applied | pass | 26 |
| before: audit chain verifies | pass | VALID |
| all 33 migrations are applied after the upgrade (7 new) | pass | 33 |
| no row of any of the 185 existing tables was lost or changed (per-row md5 over the old columns) | pass | 0 |
| tables that gained rows were seeded by an INSERT in a newer migration (listed in the report) | pass |  |
| after: audit chain verifies | pass | VALID |
| after: same audit event count (40) | pass | 40 |
| after: same signed checkpoint count (1) | pass | 1 |
| the recorded migration hashes equal the shipped files | pass | identical (33 lines compared) |
| a second run of the migrator changes nothing | pass | 33 |
| the migrator refuses a previously applied file that was edited (exit non-zero) | pass | 1 |
| and says which one | pass | 1 |
| the new application's database check is Healthy on the upgraded schema (it is Unhealthy while shipped migrations are pending) | pass | Healthy |

Passed: 15, failed: 0.
