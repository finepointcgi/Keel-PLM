# Recorded results of the operations proof

Each file here is the output of one real run of a script, written by the script itself (`--report`), committed as measured, with the date, image, machine and dataset in its header. They are evidence that the procedures work and what the detectors catch, **on small synthetic data on one machine**. They are not capacity, availability, RPO or RTO commitments. Re-run them before a release; the nightly workflow ([ci.md](../ci.md)) runs them on every night's main.

| File | Script | What it proves |
|---|---|---|
| [backup-restore.md](backup-restore.md) | `scripts/ops/backup-restore-drill.sh` | dump, vault copy and manifest restored into a new server and vault; audit chain, row counts, vault integrity and every document file verified; negative controls caught |
| [upgrade-rehearsal.md](upgrade-rehearsal.md) | `scripts/ops/upgrade-rehearsal.sh` | a database at the previous migration set, holding data, upgraded by the shipped migrator; no row lost or changed; edited migration refused; the new application starts on it |
| [load-smoke.md](load-smoke.md) | `scripts/ops/load-smoke.sh` | end-to-end latency (p50, p95, p99) and error counts of list, search, create-part and BOM expand through real tokens against the production image with its shipped limits |

Reproduce (needs Docker, the .NET SDK, the pinned PostgreSQL and vault images and a production image; build one with `docker build --target runtime` using the pins of `deploy/images.env`, or with `scripts/package-offline.sh`):
```bash
until mkdir /tmp/keel-int.lock 2>/dev/null; do sleep 20; done      # one container-heavy job at a time on a shared machine
scripts/ops/backup-restore-drill.sh --image keel/api:TAG --report docs/ops/drill-results/backup-restore.md
scripts/ops/upgrade-rehearsal.sh   --image keel/api:TAG --report docs/ops/drill-results/upgrade-rehearsal.md
scripts/ops/load-smoke.sh          --image keel/api:TAG --report docs/ops/drill-results/load-smoke.md
rmdir /tmp/keel-int.lock
```
All containers, volumes and networks the scripts create are named `keel-drill-<drill>-<id>` and removed by the script on exit (`--keep` leaves them for inspection). They never use an existing container, volume or network, and Docker assigns the host ports.
