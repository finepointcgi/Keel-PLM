using Keel.Api.Infrastructure;
using Keel.Audit;
using Keel.Core;
using Keel.Notifications;

namespace Keel.Api.Endpoints;

/// <summary>One report of a bounce or complaint. <c>Kind</c> is hard, soft or complaint; <c>Status</c> is the enhanced status code such as 5.1.1 (optional).</summary>
public sealed record EmailBounceItem(string Address, string Kind, string? Status = null, string? Diagnostic = null);

public sealed record EmailBounceRequest(IReadOnlyList<EmailBounceItem> Reports);

/// <summary>
/// Inbound bounce and complaint reports for the e-mail notification channel (ADR-0055, docs/ops/email-verification-and-bounces.md). Authenticated like the integration pull API:
/// a service account (client credentials, JWT bearer) with the <c>plm-integration</c> role, typically a small script or hook next to the mail relay. The response counts what
/// was recorded and never says whether an address belongs to a user beyond the totals, and an address that is not one of ours is dropped.
/// </summary>
public static class NotificationEmailEndpoints
{
    public static void MapNotificationEmailEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/notifications/email").RequireAuthorization(Policies.Integration).WithTags("E-mail bounces");

        group.MapPost("/bounces", async (EmailBounceRequest request, HttpContext c, EmailBounceService bounces, CancellationToken ct) =>
        {
            var subject = SubjectFactory.Require(c);
            var reports = (request.Reports ?? []).Select(r => new BounceReport(r.Address ?? "", (r.Kind ?? "").ToLowerInvariant(), r.Status, r.Diagnostic)).ToList();
            return (await bounces.RecordAsync(ActorSnapshot.From(subject), reports, BounceSources.Api, CorrelationId.Get(c), ct)).ToHttp();
        }).WithSummary("Reports hard bounces, soft bounces and complaints; an address is suppressed after the configured number of hard bounces or one complaint.");
    }
}
