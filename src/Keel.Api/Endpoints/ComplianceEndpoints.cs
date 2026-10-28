using Keel.Api.Infrastructure;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Vault;

namespace Keel.Api.Endpoints;

public sealed record ReleaseQuarantineRequest(string DocumentNumber, string Sha256, string Reason);

/// <summary>
/// Compliance operations (increment 5.0, ADR-0026): malware scan coverage, rescan and quarantine release, vault garbage collection reports and dry runs.
/// Garbage collection can only be reported and previewed here; deleting is the <c>keel-vault</c> tool or the scheduled service.
/// </summary>
public static class ComplianceEndpoints
{
    public static void MapComplianceEndpoints(this IEndpointRouteBuilder app)
    {
        var vault = app.MapGroup("/api/v1/admin/vault").RequireAuthorization(Policies.AnyPlmRole);
        static Subject Who(HttpContext c) => SubjectFactory.Require(c);
        static string? Corr(HttpContext c) => CorrelationId.Get(c);

        // Administrators and auditors (the service enforces the roles and audits denials).
        vault.MapGet("/scan-coverage", async (HttpContext c, DocumentService svc, CancellationToken ct) =>
            (await svc.GetScanCoverageAsync(Who(c), Corr(c), ct)).ToHttp());

        vault.MapPost("/rescan", async (RescanRequest? body, HttpContext c, DocumentService svc, CancellationToken ct) =>
            (await svc.RescanAsync(Who(c), body ?? new RescanRequest(), Corr(c), ct)).ToHttp());

        vault.MapPost("/quarantine/release", async (ReleaseQuarantineRequest body, HttpContext c, DocumentService svc, CancellationToken ct) =>
            (await svc.ReleaseQuarantineAsync(Who(c), body.DocumentNumber, body.Sha256, body.Reason, Corr(c), ct)).ToHttp());

        vault.MapGet("/gc/runs", async (int? limit, HttpContext c, VaultGcAdmin svc, CancellationToken ct) =>
            (await svc.RecentRunsAsync(Who(c), limit ?? 10, Corr(c), ct)).ToHttp());

        vault.MapPost("/gc/dry-run", async (HttpContext c, VaultGcAdmin svc, CancellationToken ct) =>
            (await svc.DryRunAsync(Who(c), Corr(c), ct)).ToHttp());
    }
}
