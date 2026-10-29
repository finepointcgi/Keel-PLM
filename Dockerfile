# Keel PLM API + Blazor UI host and the keel-audit verifier (ADR-0012, ADR-0020).
#
#   docker build --target runtime -t keel/api:1.7.0 \
#     --build-arg SDK_IMAGE="$SDK_IMAGE" --build-arg RUNTIME_IMAGE="$RUNTIME_IMAGE" .      # values from deploy/images.env
#
# The LAST stage is `demo` (ADR-0045): one self-contained image with PostgreSQL 17, an S3 store and the guarded demo sign-in, for Google Cloud Run
# ("Deploy from repository" builds the last stage by default). Production builds MUST pass `--target runtime` (scripts/package-offline.sh does).
# This file uses plain Dockerfile syntax only (no BuildKit-only features), because Cloud Build's default builder rejects them.
#
# Entrypoints (same image, one artifact to scan, sign and ship):
#   default                                     the web host (UI + REST API) on :8080
#   --migrate                                   apply db/migrations as keel_migrator, then exit (job)
#   dotnet /app/audit/keel-audit.dll verify ... offline audit-chain verifier
#   dotnet /app/vault/keel-vault.dll gc ...     vault garbage collection (dry run by default)
#
# The build uses the SDK that ships in the UBI image and ignores global.json (which pins the developer SDK feature band);
# CI separately builds the solution with -warnaserror on the pinned SDK before an image is cut.
ARG SDK_IMAGE=registry.access.redhat.com/ubi9/dotnet-100:latest
ARG RUNTIME_IMAGE=registry.access.redhat.com/ubi9/dotnet-100-aspnet:latest
# Demo image only (the `demo` stage at the end of this file). WEED_IMAGE is the same pin as KEEL_VAULT_IMAGE in deploy/images.env (a test keeps them equal).
ARG DEMO_BASE_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-noble
ARG WEED_IMAGE=chrislusf/seaweedfs:4.47@sha256:ce9e796f1fe6f06968f4c04bdaf8f678dad9c8acdfef3d244133d71bfa6bf882

FROM ${SDK_IMAGE} AS publish
USER 0
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src ./src
COPY db/migrations ./db/migrations
# Keel.Cad embeds its JSON schema from docs/ (a build input, not documentation).
COPY docs/integration/cad/structure-v1.schema.json ./docs/integration/cad/structure-v1.schema.json
# The demo sign-in people are read from the dev realm file, embedded in Keel.Web at build time (ADR-0045).
COPY deploy/compose/keycloak/keel-realm.json ./deploy/compose/keycloak/keel-realm.json
# Deterministic, CI-style build; analyzers' warnings-as-errors are enforced by CI on the pinned SDK, not re-evaluated here.
RUN dotnet publish src/Keel.Api/Keel.Api.csproj -c Release -o /out/api --nologo \
        -p:UseAppHost=false -p:ContinuousIntegrationBuild=true -p:Deterministic=true -p:TreatWarningsAsErrors=false \
 && dotnet publish src/Keel.AuditCli/Keel.AuditCli.csproj -c Release -o /out/audit --nologo \
        -p:UseAppHost=false -p:ContinuousIntegrationBuild=true -p:Deterministic=true -p:TreatWarningsAsErrors=false \
 && dotnet publish src/Keel.VaultTool/Keel.VaultTool.csproj -c Release -o /out/vault --nologo \
        -p:UseAppHost=false -p:ContinuousIntegrationBuild=true -p:Deterministic=true -p:TreatWarningsAsErrors=false \
 # Dev-only credentials never ship (secrets come from the environment or mounted files).
 && rm -f /out/api/appsettings.Development.json /out/api/web.config \
 # The framework script and the Keel.Web component assets must be part of the published output (ADR-0014).
 && test -f /out/api/wwwroot/_framework/blazor.web.js \
 && test -d /out/api/wwwroot/_content/Keel.Web \
 && test -f /out/api/migrations/0001_audit.sql

FROM ${RUNTIME_IMAGE} AS runtime
ARG KEEL_VERSION=0.0.0-dev
ARG KEEL_REVISION=unknown
ARG KEEL_CREATED=
LABEL org.opencontainers.image.title="Keel PLM API and UI" \
      org.opencontainers.image.description="Keel PLM: Blazor Server UI and REST API for export-controlled engineering data" \
      org.opencontainers.image.vendor="Keel PLM" \
      org.opencontainers.image.version="${KEEL_VERSION}" \
      org.opencontainers.image.revision="${KEEL_REVISION}" \
      org.opencontainers.image.created="${KEEL_CREATED}" \
      org.opencontainers.image.documentation="docs/ops/configuration.md" \
      org.opencontainers.image.base.name="${RUNTIME_IMAGE}"

USER 0
# Hardening: no package managers or downloaders in the final image, no setuid/setgid binaries. The UBI base keeps bash and the
# coreutils (the entrypoint does not use them). The data directory is writable by group 0 so any numeric UID in group 0 works.
RUN rm -f /usr/bin/microdnf /usr/bin/dnf /usr/bin/yum /usr/bin/curl \
 && find / -xdev -type f \( -perm -4000 -o -perm -2000 \) -exec chmod a-s {} + \
 && mkdir -p /app /var/lib/keel/dataprotection /var/lib/keel/tmp \
 && chown -R 1001:0 /var/lib/keel \
 && chmod -R 0770 /var/lib/keel

COPY --from=publish --chown=0:0 /out/api /app
COPY --from=publish --chown=0:0 /out/audit /app/audit
COPY --from=publish --chown=0:0 /out/vault /app/vault
# Licence texts of the vendored viewer files travel in the image (LGPL/MIT/Apache notices).
COPY --chown=0:0 docs/third-party/viewer-licenses /app/licenses
# Application files are read-only for the runtime user (owned by root, world-readable).
RUN chmod -R a=rX /app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    TMPDIR=/tmp \
    Keel__DataProtection__KeyDirectory=/var/lib/keel/dataprotection

WORKDIR /app
# Numeric, non-root. Writable paths at runtime: /tmp (upload buffering; mount a tmpfs/emptyDir) and /var/lib/keel (mount a volume).
USER 1001:0
VOLUME ["/var/lib/keel/dataprotection"]
EXPOSE 8080
# No curl in the image: the host binary answers its own probe.
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 \
    CMD ["dotnet", "/app/Keel.Api.dll", "--healthcheck", "http://127.0.0.1:8080/health/live"]
ENTRYPOINT ["dotnet", "/app/Keel.Api.dll"]

# ---------------------------------------------------------------------------------------------------------------------------------------
# DEMO (ADR-0045, docs/ops/cloud-run-demo.md). Everything below this line is for the Cloud Run demo and is not part of the production image.
# The seeder (keel-seed) is published here, on the same SDK as the host; it needs the shared test generator project it references.
FROM publish AS demo-publish
COPY tests/Keel.TestSupport ./tests/Keel.TestSupport
RUN dotnet publish src/Keel.Seed/Keel.Seed.csproj -c Release -o /out/seed --nologo \
        -p:UseAppHost=false -p:ContinuousIntegrationBuild=true -p:Deterministic=true -p:TreatWarningsAsErrors=false \
 && test -f /out/seed/keel-seed.dll

# The vault of the demo is SeaweedFS (Apache-2.0), the same store the dev stack uses: only its `weed` binary is taken from the pinned image.
FROM ${WEED_IMAGE} AS weed

# One container, three processes: PostgreSQL 17 (PGDG packages), SeaweedFS S3 on loopback, and the Keel host on $PORT. Debian-family base because the
# UBI runtime image has no PostgreSQL 17 package source that is usable without a subscription. Packages are fetched at BUILD time only; nothing is
# downloaded at runtime. The entrypoint starts as root (Cloud Run does too), prepares /data and drops each process to its own non-root user.
FROM ${DEMO_BASE_IMAGE} AS demo
ARG KEEL_VERSION=0.0.0-dev
ARG KEEL_REVISION=unknown
LABEL org.opencontainers.image.title="Keel PLM demo" \
      org.opencontainers.image.description="Keel PLM demonstration: synthetic data, demo sign-in, in-container PostgreSQL and S3 vault. Never for real data." \
      org.opencontainers.image.version="${KEEL_VERSION}" \
      org.opencontainers.image.revision="${KEEL_REVISION}"
USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl openssl util-linux postgresql-common \
 && install -d /usr/share/postgresql-common/pgdg \
 && curl -fsSL -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc https://www.postgresql.org/media/keys/ACCC4CF8.asc \
 && . /etc/os-release \
 && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt ${VERSION_CODENAME}-pgdg main" > /etc/apt/sources.list.d/pgdg.list \
 && sed -i 's/^#\?create_main_cluster.*/create_main_cluster = false/' /etc/postgresql-common/createcluster.conf \
 && apt-get update \
 && apt-get install -y --no-install-recommends postgresql-17 \
 && rm -rf /var/lib/apt/lists/* \
 && /usr/lib/postgresql/17/bin/postgres --version
COPY --from=weed /usr/bin/weed /usr/local/bin/weed
WORKDIR /app
COPY --from=publish --chown=0:0 /out/api /app
COPY --from=demo-publish --chown=0:0 /out/seed /app/seed
COPY --chown=0:0 deploy/demo/entrypoint.sh /usr/local/bin/keel-demo
RUN rm -f /app/appsettings.Development.json /app/web.config \
 && chmod -R a=rX /app \
 && chmod 0755 /usr/local/bin/keel-demo /usr/local/bin/weed \
 && mkdir -p /data && chmod 0755 /data
# Demo mode takes both: the Demo environment and Keel:Demo:Enabled (the host refuses to start with only the second).
ENV ASPNETCORE_ENVIRONMENT=Demo \
    Keel__Demo__Enabled=true \
    PORT=8080 \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_gcServer=0 \
    DOTNET_TieredPGO=0
EXPOSE 8080
VOLUME /data
HEALTHCHECK --interval=30s --timeout=5s --start-period=180s --retries=3 \
    CMD curl -fsS "http://127.0.0.1:${PORT:-8080}/health/live" || exit 1
ENTRYPOINT ["/usr/local/bin/keel-demo"]
