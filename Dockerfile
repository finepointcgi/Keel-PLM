# syntax=docker/dockerfile:1.7
# Keel PLM API + Blazor UI host and the keel-audit verifier (ADR-0012, ADR-0020).
#
#   docker build -t keel/api:1.7.0 \
#     --build-arg SDK_IMAGE="$SDK_IMAGE" --build-arg RUNTIME_IMAGE="$RUNTIME_IMAGE" .      # values from deploy/images.env
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

FROM ${SDK_IMAGE} AS publish
USER 0
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src ./src
COPY db/migrations ./db/migrations
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
