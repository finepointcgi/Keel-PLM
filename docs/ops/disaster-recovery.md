# Disaster recovery

**Proposals for the product owner to confirm** (no availability or recovery target has been agreed; the 99.9% availability target
is the working assumption used here). 99.9% allows about 8.8 hours of unplanned downtime per year, or 43 minutes per month.

## Targets (proposed)
| Scenario | RPO | RTO | Mechanism |
|---|---|---|---|
| Application pod/container failure | 0 | under 1 min | restart policy; 2+ replicas behind the ingress (Helm) |
| Node or host failure, Kubernetes | 0 | under 5 min | replicas on other nodes (anti-affinity), PDB |
| Database primary failure (HA cluster) | 0 to 5 s (sync or near-sync replica) | under 5 min | operator-managed failover (CloudNativePG, Patroni) |
| Database loss, single-host compose site | up to `archive_timeout` (60 s) | 1 to 2 h | restore base backup + WAL (backup-restore.md) |
| Vault loss | 0 with replication, else last copy | 2 to 8 h (size dependent) | replica bucket promoted or restored; 50 TB moves slowly |
| Site loss | 15 min (WAL shipping to second site) | 4 h | warm standby at a second accredited site |
| Corruption or malicious change | to chosen point in time | 4 h | PITR to before the event, `keel-audit verify` to find it |

The RTO for the vault is bounded by network bandwidth: 50 TB over 10 Gb/s is about 11 hours, so a second site needs a replicated
bucket kept current, not a restore from tape.

## Options
1. **Single site, HA inside** (target 99.9% only with this or better): 3 Kubernetes nodes, 2+ Keel replicas, PostgreSQL operator with
   one synchronous replica, replicated S3 store, backups to a separate failure domain. Maintenance is rolling.
2. **Warm standby second site:** continuous WAL shipping and bucket replication to a second accredited site; the application is
   installed there from the same signed bundle and kept scaled to zero or minimal. Failover is promote database, point vault URL,
   scale up, repoint DNS: rehearse it twice a year. The IdP must be reachable from the second site. Both sites need the same
   audit checkpoint **public** keys and the private key (custodian-controlled).
3. **Cold restore only** (small sites): backups to offline media at another location; RTO is hours to a day.

## Order of recovery
IdP reachable, then PostgreSQL, then vault, then Keel (the migration job is a no-op when the schema is current), then verify
(`/health/ready`, `keel-audit verify`, download a file). Never run two application instances against two divergent copies of the
database: the audit chain would fork and verification fails.

## Gaps to close in Phase 5
Failover automation and its tests, a bulk vault verifier, load-tested RTO figures, and a written site runbook with named custodians.
