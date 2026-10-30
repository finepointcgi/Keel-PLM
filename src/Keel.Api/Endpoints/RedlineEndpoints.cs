using Keel.Api.Infrastructure;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Compare;
using Keel.Web.Services;

namespace Keel.Api.Endpoints;

/// <summary>
/// Cross-document compare and redline review (increment 9.5, ADR-0053): the REST API (bearer) and the cookie-authenticated report download for the UI. Every route goes
/// through the documents module's own authorization (read and download of BOTH documents, audit, markings); nothing here decides access.
/// </summary>
public static class RedlineEndpoints
{
    public static void MapRedlineEndpoints(this IEndpointRouteBuilder app)
    {
        var docs = app.MapGroup("/api/v1/documents").RequireAuthorization(Policies.AnyPlmRole);
        docs.MapGet("/compare", async (string left, Guid? leftVersion, string right, Guid? rightVersion, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.CompareDocumentsAsync(SubjectFactory.Require(ctx), new CompareRef(left, leftVersion), new CompareRef(right, rightVersion), CorrelationId.Get(ctx), ct)).ToHttp());
        docs.MapPost("/compare/comments", async (RedlineCommentRequest req, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.AddRedlineCommentAsync(SubjectFactory.Require(ctx), new CompareRef(req.Left ?? "", req.LeftVersion), new CompareRef(req.Right ?? "", req.RightVersion), req.Change, req.Body ?? "", req.ReplyTo,
                CorrelationId.Get(ctx), ct)).ToHttp());
        docs.MapPost("/compare/threads/{threadId:guid}/resolve", async (Guid threadId, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.SetRedlineThreadResolvedAsync(SubjectFactory.Require(ctx), threadId, true, CorrelationId.Get(ctx), ct)).ToHttp());
        docs.MapPost("/compare/threads/{threadId:guid}/reopen", async (Guid threadId, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.SetRedlineThreadResolvedAsync(SubjectFactory.Require(ctx), threadId, false, CorrelationId.Get(ctx), ct)).ToHttp());
        docs.MapGet("/compare/report", Report);
        docs.MapGet("/{number}/redlines", async (string number, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListRedlineReviewsAsync(SubjectFactory.Require(ctx), number, ct)));

        app.MapGet(RedlineUi.ReportRoute, Report).RequireAuthorization(Policies.AnyPlmRole);
    }

    public sealed record RedlineCommentRequest(string? Left, Guid? LeftVersion, string? Right, Guid? RightVersion, int Change, string? Body, Guid? ReplyTo);

    private static async Task<IResult> Report(string left, Guid? leftVersion, string right, Guid? rightVersion, string? format, HttpContext ctx, DocumentService svc, CancellationToken ct)
    {
        var outcome = await svc.ExportRedlineAsync(SubjectFactory.Require(ctx), new CompareRef(left, leftVersion), new CompareRef(right, rightVersion), format ?? "pdf", CorrelationId.Get(ctx), ct);
        if (!outcome.IsOk) return outcome.ToHttp();
        var report = outcome.Value!;
        ctx.Response.Headers[DocumentEndpoints.MarkingsHeader] = DocumentEndpoints.HeaderValue(report.Banner);
        ctx.Response.Headers.CacheControl = "no-store, max-age=0";
        return Results.File(report.Content, report.ContentType, report.FileName);
    }
}
