# keel-import runbook

`keel-import` is an offline command-line tool (no network calls except to the database and the vault you name). Run it from a host that can reach both. It writes data that **cannot be deleted**, so there is no default target.

```
keel-import inspect <package> [--format F]
keel-import map-template <package> [--out mapping.json]
keel-import validate <package> --mapping M [--offline]
keel-import dry-run <package> --mapping M [--quarantine]
keel-import load <package> --mapping M --dry-run-report R --yes [--quarantine]
keel-import reconcile <package> --mapping M [--batch ID] [--deep]
keel-import report <batch-id|latest>
```

## Target and credentials
Required for everything except `inspect`, `map-template` and `validate --offline`:
* `--connection "<Npgsql connection string>"` (the application role `keel_app`; never a superuser), or `--settings file.json` (`{"Keel":{"Database":{"AppConnectionString":"..."},"Vault":{"ServiceUrl":"...","AccessKey":"...","SecretKey":"...","Bucket":"keel-vault"}}}`), or environment `KEEL_IMPORT_CONNECTION` / `Keel__Database__AppConnectionString` and `Keel__Vault__*`.
* Vault (required by `load`; optional for `dry-run` and `reconcile`, where it adds a presence check): `--vault-url --vault-access-key --vault-secret-key [--vault-bucket --vault-region]`.
* `--use-dev-stack` reads `src/Keel.Api/appsettings.Development.json` (the shared dev database). Explicit on purpose; never use it for a rehearsal you want to keep separate.
* The tool prints `host:port/database as user` (never the password) before it does anything.

## Options
`--format plmxml|windchill-json|windchill-csv|canonical` (detected from the package: `package.json`, `parts.json`, `parts.csv`, `*.plmxml`/`*.xml`), `--out DIR` (report directory, default `./keel-import-reports`), `--max-errors N` (print at most N problems; the report files always list all), `--operator NAME`, `--batch-name NAME`, `--chunk-size N` (objects per transaction, default 100), `--quarantine`, `--yes`, `--deep`, `--offline`, `--batch ID`.

## Exit codes
| Code | Meaning | What to do |
|---|---|---|
| 0 | success | continue with the next step |
| 1 | usage or I/O error (bad arguments, unreadable file, `load` without `--yes` or report) | read the message |
| 2 | invalid: validation or dry-run found blocking errors, or the mapping file is invalid | fix the source or mapping using the codes and hints, rerun `validate` |
| 3 | the load finished (or stopped) with issues: objects quarantined, conflicts, or an interrupted run | read `load-report.md`; for an interrupted run rerun the same command, it completes the rest |
| 4 | reconcile found differences | read `reconcile-report.md`; do not cut over until explained |
| 5 | refused or target unavailable: no database target, connection failure, or the dry-run report does not authorize this load | see the message (report modified, package or mapping changed, quarantine setting differs, database changed) and rerun `dry-run` |
| 6 | not supported yet (for example `classification.load: true`) | the message names the phase |
| 130 | cancelled | |

## Typical session
```
keel-import inspect  export/ 
keel-import map-template export/ --out mapping.json     # fill every empty value
keel-import validate export/ --mapping mapping.json --offline
keel-import dry-run  export/ --mapping mapping.json --connection "..." --out reports/
keel-import load     export/ --mapping mapping.json --dry-run-report reports/dry-run-report.json --yes \
                     --connection "..." --vault-url ... --vault-access-key ... --vault-secret-key ...
keel-import reconcile export/ --mapping mapping.json --connection "..." --vault-url ... --deep
```
Rehearse against a **throwaway copy of the database** first (restore a backup into a scratch instance); the production load is the second run.

## Reading the dry-run report
Order of attention: *Blocking*, *Problems* (errors first; each has a code and a fix hint), *Defaulted markings* (every one needs a human decision), *Quarantined*, *Unmapped properties* (what lands in `legacy_properties`), *Users replaced by the fallback user*, *Revision label mapping*, *Mapping decisions* (promotions, assigned find numbers, defaulted states ...), *Unknown source elements*, *Files* (missing, hash mismatches, hashes computed because the source gave none), *Estimate*. The report hash on the last line gates the load.

## Troubleshooting
| Symptom | Cause and fix |
|---|---|
| `MARKINGS-UNDETERMINED` for many objects | the rules do not cover a property value (for example the flag is blank). Add a rule, or set `defaultMarkings` and review the defaulted list. |
| `REV-ORDER-AMBIGUOUS` | labels mix letters and numbers and dates are missing: `revisions.explicit` for those items. |
| `FILE-SHA-MISMATCH` | the file changed after export or was corrupted in transfer; re-export it. Check the transfer (compare the package SHA-256 at both ends). |
| `LOAD-CONFLICT` | an object that was imported before has different source data or mapping now. Released data is immutable: nothing was overwritten. Decide whether the change belongs in a Keel change notice. |
| `NUMBER-EXISTS` | a native Keel object already uses the number. Renumber in the source or retire the native object. |
| `DEPENDENCY-FAILED` | the object uses a child or document that has errors. Fix that one; this one imports on the next run. |
| `SRC-DANGLING-REF`, `SRC-PARSE` | the export is inconsistent or malformed; quarantine does not help. Re-export (see the spec for the supported subset). |
| `permission denied` from the database | connect as `keel_app` after migration `0010` has been applied by the migrator. |
| Load stopped (exit 3, `Failed` batch) | fix the cause and rerun the same command; the id map makes it resume. |
| Slow load | the vault dominates when many large files are new; rows load at roughly 3,000 per second on the reference machine. |

## After the load
`reconcile` (exit code 0 required), then open `/admin/migration` (administrators) to see the batch, its load report and reconciliations. Keep the package, mapping, reports and the batch id together (they are CUI-handling artifacts). The audit log has the batch start/end events with the package manifest hash.
