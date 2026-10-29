# Hosting the demo on Google Cloud Run

One container: PostgreSQL 17, an S3 vault (SeaweedFS), the Keel host and a demo sign-in page. No Keycloak, no external services. Decisions: [ADR-0045](../adr/0045-cloud-run-demo.md).
Notes and measurements: [increments/9.0-cloud-run-demo.md](../increments/9.0-cloud-run-demo.md).

**Synthetic data only.** Anyone who can open the page can sign in as any demo user, including the administrator. Never put real data in it, and never point the image at a real database
(the host refuses a database with non-demo people, and refuses to start with demo mode outside the Demo environment).

## Risk of a public, password-free demo, and the recommendation
A public URL means anyone on the internet can use it: upload files (malware scanning is off), fill the disk or memory of the instance, and use it as free hosting for content you are
responsible for. The data is synthetic and disposable, but the abuse is not. Recommended, in this order:
1. **Require authentication (IAM)**: leave "Require authentication" selected. Only Google accounts you grant `roles/run.invoker` can reach it. To show it to someone, add their Google
   account, or use `gcloud run services proxy keel-plm-demo --region northamerica-northeast2 --port 8080` on your machine (it adds your identity), or the identity-token header in the smoke test.
2. **Short-lived public demo**: allow unauthenticated access only for the hours of a meeting, then switch back (console: Security tab, or `gcloud run services remove-iam-policy-binding`) or
   delete the service. Set `--max-instances=1` (already part of the design) so cost and abuse are bounded.
3. A permanently public demo is not recommended.

## Cost and sizing (estimates: check the current price list)
- 1 vCPU, 2 GiB, `--min-instances=1`, `--max-instances=1`, **CPU always allocated (`--no-cpu-throttling`)**. PostgreSQL, the vault and Keel's timers (idle lock, checkpoints) run between
  requests; with the default request-based CPU they would be frozen between requests, and a request would pay a wake-up delay and timeouts. Always-on is what makes the demo reliable.
- Rough monthly cost for 24/7: about US$55-75 (vCPU about 2.6 million seconds, 2 GiB memory, instance-based billing, region pricing tier 1 or 2; `northamerica-northeast2` is
  priced at the higher tier). Check the Cloud Run pricing page; free-tier credits cover only a small part of this.
- Cheaper: `--min-instances=0` with the default CPU billing costs almost nothing while idle, but every request after idle meets a cold start (about 10 s measured locally, more on Cloud
  Run for the image pull) and the data is reseeded each time, so a half-finished walk-through is lost. Use it only for a demo you start yourself and warm up.
- Memory measured locally: about 250-310 MiB in use of the 2 GiB limit (the container file system is memory-backed on Cloud Run and counts against it). 1 GiB would likely work but
  leaves little room for uploads; 2 GiB is the documented size.

## Behaviour to know
- `/data` is the instance's own file system: **ephemeral**. Every cold start (new instance, redeploy, crash, scale to zero) gets an empty database that is migrated and reseeded with the ORCA
  demo data (about 10 s measured; no pre-built snapshot is needed). The banner on every page says so. There is no nightly reset: Cloud Run restarts instances when it decides to; to reset
  on demand, deploy a new revision (for example `gcloud run services update keel-plm-demo --update-env-vars=RESET=$(date +%s)`).
- **Exactly one instance** (`--max-instances=1`): the data is per instance, so two instances would show two different demos. Concurrency 80 is fine.
- The idle-session lock is database-backed (migration 0016), so it lives in the instance's PostgreSQL and works as usual (default: lock after inactivity, unlock by confirming it is you).
- Malware scanning is off (acknowledged Disabled mode) and the sign-in page says so. Do not offer uploads to people you do not trust.
- The REST API accepts the demo session cookie (ADR-0045); use a cookie jar as in `deploy/demo/smoke-test.sh`. Bearer tokens are still validated as in production, and there are none to mint.

## Console steps: Deploy from repository
Prerequisites: a Google Cloud project `gen-lang-client-0706696021` with billing on, and the GitHub repository `finepointcgi/Keel-PLM` reachable by Cloud Build.
1. Open <https://console.cloud.google.com/run?project=gen-lang-client-0706696021> and click **Create service**.
2. Choose **Continuously deploy from a repository (source or function)**, then **Set up with Cloud Build**.
3. Repository provider **GitHub**; authenticate and install the Google Cloud Build GitHub app for `finepointcgi/Keel-PLM` if asked; select the repository.
4. Branch `^main$`. Build type **Dockerfile**, source location `/Dockerfile` (the repository root is the build context). Save. (The default build uses the last stage of the Dockerfile,
   which is `demo`; nothing else to set.)
5. Service name `keel-plm-demo`. Region `northamerica-northeast2` (Toronto).
6. **Authentication**: **Require authentication** (recommended). Choose **Allow public access** only for a short-lived public demo (see the risk section).
7. **Billing**: **Instance-based** (CPU always allocated).
8. **Service scaling**: minimum instances `1`, maximum instances `1`.
9. Ingress: all. Under **Container(s), Volumes, Networking, Security**:
   - Container port `8080`.
   - Resources: **Memory 2 GiB, CPU 1** (CPU is always allocated with instance-based billing).
   - Request timeout 300 s. Execution environment: **Second generation**.
   - Startup CPU boost: on (faster first start).
   - Startup probe: the default TCP probe on 8080 is enough; or an HTTP probe on `/health/live` with a period of 5 s and failure threshold 60.
   - Variables and secrets: none are required. The image sets `ASPNETCORE_ENVIRONMENT=Demo` and `Keel__Demo__Enabled=true` itself, generates random database and vault credentials on each start,
     and talks only to its own PostgreSQL and vault on 127.0.0.1. Do NOT set `ASPNETCORE_ENVIRONMENT` to anything else.
10. **Create**. The first build takes several minutes (the .NET SDK image is large; PostgreSQL is installed from apt.postgresql.org during the build). Later pushes to `main` redeploy automatically.
11. Open the service URL (or use the proxy command above when authentication is required). You should see the sign-in page with the amber demo banner. Run the smoke test:
    `AUTH_HEADER="Authorization: Bearer $(gcloud auth print-identity-token)" deploy/demo/smoke-test.sh https://<service-url>` (omit `AUTH_HEADER` for a public service).

## Equivalent gcloud commands
```bash
PROJECT=gen-lang-client-0706696021; REGION=northamerica-northeast2
gcloud config set project $PROJECT
gcloud services enable run.googleapis.com cloudbuild.googleapis.com artifactregistry.googleapis.com

# One-off deploy from the local checkout (builds the Dockerfile in Cloud Build, last stage = demo):
gcloud run deploy keel-plm-demo --source . --region $REGION \
  --memory 2Gi --cpu 1 --port 8080 --no-cpu-throttling --cpu-boost \
  --min-instances 1 --max-instances 1 --timeout 300 --execution-environment gen2 \
  --no-allow-unauthenticated            # recommended; use --allow-unauthenticated only for a short-lived public demo

# Let one person in (when authentication is required):
gcloud run services add-iam-policy-binding keel-plm-demo --region $REGION \
  --member user:someone@example.com --role roles/run.invoker

# Continuous deployment from GitHub (same as the console steps; connect the repository once in the console first):
gcloud run deploy keel-plm-demo --source . --region $REGION ...   # re-run after each change, or use the console trigger

# Look at it, test it, stop paying for it:
gcloud run services describe keel-plm-demo --region $REGION --format 'value(status.url)'
gcloud run services proxy keel-plm-demo --region $REGION --port 8080 &
deploy/demo/smoke-test.sh http://127.0.0.1:8080
gcloud run services delete keel-plm-demo --region $REGION
```

## Run it locally (no Cloud Run)
```bash
docker build -t keel-demo .                       # the last stage is the demo; plain syntax, so DOCKER_BUILDKIT=0 also works
docker run --rm --memory 2g -p 18555:8080 keel-demo
deploy/demo/smoke-test.sh http://127.0.0.1:18555
```
The production image is `docker build --target runtime ...` (see the Dockerfile header and [offline-install.md](offline-install.md)).

## Other hosts (render.yaml-style note)
Any host that runs a container with 2 GiB and one always-on instance works the same way: build the repository root Dockerfile with no target, expose `$PORT` (the entrypoint listens on it,
default 8080), set nothing else. A `render.yaml` would be a `web` service with `runtime: docker`, `plan` of at least 2 GB, one instance and no disk (data is disposable).

## Troubleshooting
- Container fails to become ready: open the revision's logs; the entrypoint prints `[keel-demo +N s]` milestones (postgres, vault, migrate, seed, host).
- "refusing to start" messages are the guards of ADR-0045: demo mode was requested outside the Demo environment, or the database holds non-demo data.
- Cloud Build cannot pull a base image: the build needs `mcr.microsoft.com`, `registry.access.redhat.com`, Docker Hub (`chrislusf/seaweedfs`) and `apt.postgresql.org` at build time only.
