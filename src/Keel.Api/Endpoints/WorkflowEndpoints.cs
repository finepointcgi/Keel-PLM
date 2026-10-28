using System.Text.Json.Nodes;
using Keel.Api.Infrastructure;
using Keel.Core;
using Keel.Workflow;

namespace Keel.Api.Endpoints;

public sealed record StartReviewRequest(Dictionary<string, string[]>? Participants, string? TemplateId = null, string? Comment = null);
public sealed record SignRequest(string ReasonCode, string? Comment = null);
public sealed record CancelWorkflowRequest(string? Reason);
public sealed record ReassignRequest(string Assignee, string? Reason);

public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        var any = app.MapGroup("/api/v1").RequireAuthorization(Policies.AnyPlmRole);

        // ---- templates: read-only listing for everyone, upload for administrators (the admin UI is Phase 2)
        any.MapGet("/workflow/templates", async (bool? all, HttpContext ctx, WorkflowService svc, CancellationToken ct) =>
            await svc.ListTemplatesAsync(SubjectFactory.Require(ctx), activeOnly: all != true, CorrelationId.Get(ctx), ct));

        app.MapPost("/api/v1/workflow/templates", async (JsonObject body, HttpContext ctx, WorkflowService svc, CancellationToken ct) =>
            (await svc.UploadTemplateAsync(SubjectFactory.Require(ctx), body.ToJsonString(), CorrelationId.Get(ctx), ct))
                .ToHttp(t => Results.Created($"/api/v1/workflow/templates?all=true#{t.Id}-v{t.Version}", t)))
            .RequireAuthorization(Policies.Admin);

        // ---- start
        any.MapPost("/parts/{number}/review", async (string number, StartReviewRequest req, HttpContext ctx, WorkflowService svc, CancellationToken ct) =>
            (await svc.StartAsync(SubjectFactory.Require(ctx),
                new StartReviewCommand("part", number, req.Participants ?? [], req.TemplateId, req.Comment), CorrelationId.Get(ctx), ct))
                .ToHttp(i => Results.Created($"/api/v1/workflows/{i.Id}", i)));

        any.MapGet("/parts/{number}/workflow", async (string number, HttpContext ctx, WorkflowService svc, CancellationToken ct) =>
            (await svc.StatusForObjectAsync(SubjectFactory.Require(ctx), "part", number, CorrelationId.Get(ctx), ct)).ToHttp());

        // ---- tasks
        any.MapGet("/tasks", async (HttpContext ctx, WorkflowService svc, CancellationToken ct) =>
            await svc.MyTasksAsync(SubjectFactory.Require(ctx), CorrelationId.Get(ctx), ct));

        any.MapGet("/tasks/{id:guid}", async (Guid id, HttpContext ctx, WorkflowService svc, CancellationToken ct) =>
            (await svc.GetTaskAsync(SubjectFactory.Require(ctx), id, CorrelationId.Get(ctx), ct)).ToHttp());

        any.MapPost("/tasks/{id:guid}/sign", async (Guid id, SignRequest req, HttpContext ctx, WorkflowService svc, CancellationToken ct) =>
            (await svc.SignAsync(SubjectFactory.Require(ctx), id, req.ReasonCode, req.Comment, CorrelationId.Get(ctx), ct)).ToHttp());

        app.MapPost("/api/v1/tasks/{id:guid}/reassign", async (Guid id, ReassignRequest req, HttpContext ctx, WorkflowService svc, CancellationToken ct) =>
            (await svc.ReassignTaskAsync(SubjectFactory.Require(ctx), id, req.Assignee, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp())
            .RequireAuthorization(Policies.Admin);

        // ---- instances
        app.MapGet("/api/v1/workflows", async (string? status, int? limit, int? offset, HttpContext ctx, WorkflowService svc, CancellationToken ct) =>
            (await svc.ListInstancesAsync(SubjectFactory.Require(ctx), status, limit ?? 50, offset ?? 0, CorrelationId.Get(ctx), ct)).ToHttp())
            .RequireAuthorization(Policies.Admin);

        any.MapGet("/workflows/{id:guid}", async (Guid id, HttpContext ctx, WorkflowService svc, CancellationToken ct) =>
            (await svc.GetInstanceAsync(SubjectFactory.Require(ctx), id, CorrelationId.Get(ctx), ct)).ToHttp());

        any.MapPost("/workflows/{id:guid}/cancel", async (Guid id, CancelWorkflowRequest req, HttpContext ctx, WorkflowService svc, CancellationToken ct) =>
            (await svc.CancelAsync(SubjectFactory.Require(ctx), id, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp());

        any.MapGet("/workflows/{id:guid}/signatures/verify", async (Guid id, HttpContext ctx, WorkflowService svc, CancellationToken ct) =>
            (await svc.VerifyInstanceAsync(SubjectFactory.Require(ctx), id, CorrelationId.Get(ctx), ct)).ToHttp());

        // ---- participant picker support
        any.MapGet("/users", async (string? q, string? role, string? objectType, string? objectKey, int? limit, HttpContext ctx, WorkflowService svc, CancellationToken ct) =>
            (await svc.SearchUsersAsync(SubjectFactory.Require(ctx), q, role, objectType, objectKey, limit ?? 25, CorrelationId.Get(ctx), ct)).ToHttp());
    }
}
