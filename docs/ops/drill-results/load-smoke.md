# Load smoke result

Run `keel-drill-load-114113-20397` on 2026-10-30T15:46:33Z. Image `keel/api:prodready` (sha256:7ab7a6f65c9e), application limits cpus 2, memory 2g, read-only root, uid 1001; PostgreSQL cpus 2, memory 4g; vault cpus 1, memory 2g (the defaults of the production compose bundle).
Docker VM: 8 CPUs, 15 GiB memory; host Darwin arm64. PostgreSQL 17 and the vault in tmpfs on the same machine; the load client runs on the host (not in the VM).
Data: 61628 part masters (synthetic structure of 50000 lines over 8 levels, root `LR-000041`), 25266 created by the run, 112519 audit events at the end. Duration per phase 20s, closed loop.

| Operation | Concurrency | Requests | Errors | Requests/s | p50 ms | p95 ms | p99 ms | max ms | Avg response bytes |
|---|---|---|---|---|---|---|---|---|---|
| list | 1 | 311 | 0 | 15.5 | 66 | 75 | 80 | 85 | 25536 |
| list | 8 | 525 | 0 | 26.1 | 305 | 392 | 400 | 492 | 25533 |
| list | 32 | 506 | 0 | 24.5 | 1206 | 2301 | 2694 | 2987 | 25520 |
| search | 1 | 6104 | 0 | 305.2 | 2 | 4 | 45 | 57 | 1475 |
| search | 8 | 17791 | 0 | 886.7 | 4 | 53 | 96 | 178 | 1361 |
| search | 32 | 16747 | 0 | 833.6 | 20 | 83 | 120 | 261 | 1418 |
| create-part | 1 | 3828 | 0 | 191.4 | 5 | 8 | 15 | 54 | 2129 |
| create-part | 8 | 9276 | 0 | 463.5 | 15 | 27 | 35 | 61 | 2130 |
| create-part | 32 | 10825 | 0 | 539.8 | 54 | 87 | 112 | 134 | 2130 |
| bom-expand-1 | 1 | 6004 | 0 | 300.2 | 3 | 5 | 7 | 15 | 10555 |
| bom-expand-1 | 8 | 17724 | 0 | 885.9 | 5 | 47 | 54 | 65 | 10555 |
| bom-expand-1 | 32 | 17626 | 0 | 880.5 | 23 | 70 | 77 | 99 | 10555 |
| bom-expand-all | 1 | 22 | 0 | 1.1 | 935 | 981 | 1017 | 1017 | 46348062 |
| bom-expand-all | 8 | 38 | 0 | 1.7 | 4984 | 5430 | 5468 | 5468 | 46348062 |

bom-expand-1 is the structure tab's first paint (one level); bom-expand-all returns every level of the root and is tens of megabytes, so it runs at 1 and 8 workers only. Latency is the time to the last byte of the body as seen by the client on the host.

Peak while loaded: application 142% CPU (of one core), 652 MiB; PostgreSQL 205% CPU (of one core), 1539 MiB (docker stats every 2 s).
