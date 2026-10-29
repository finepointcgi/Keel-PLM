using Keel.Api.Infrastructure;
using Keel.Change;
using Keel.Core;
using Keel.Workflow;

namespace Keel.Api.Endpoints;

public sealed record CreateChangeRequest(string Type, string Title, string? Description, string Priority, string? Reason, MarkingsDto Markings,
    string? ChangeClass = null, string? SourceNumber = null);
public sealed record CreateChangeFromRequest(string? Title = null, string? Description = null, string? Priority = null, string? Reason = null,
    string? ChangeClass = null, MarkingsDto? Markings = null);
public sealed record UpdateChangeRequest(string Title, string? Description, string Priority, string? Reason);
public sealed record ClassifyChangeRequest(string ChangeClass);
public sealed record ChangeObjectMarkingsRequest(MarkingsDto Markings, string Reason);
public sealed record AddAffectedRequest(string ObjectType, string Key, string Action, string? Revision = null, bool RaiseMarkings = false, EffectivityInput? Effectivity = null);
public sealed record ChangeTransitionRequest(string ToState, string? ReasonCode = null, string? Reason = null);
public sealed record CreateChangeTaskRequest(string Title, string? Description, string Assignee, DateOnly? DueDate = null);
public sealed record TaskTextRequest(string? Text);

/// <summary>Change management REST API (increment 1.6): thin mapping of <see cref="ChangeService"/> outcomes to HTTP.</summary>
public static class ChangeEndpoints
{
    public static void MapChangeEndpoints(this IEndpointRouteBuilder app)
    {
        var changes = app.MapGroup("/api/v1/changes").RequireAuthorization(Policies.AnyPlmRole);

        // ---- search and create
        changes.MapGet("", async Task<Microsoft.AspNetCore.Http.HttpResults.Results<Microsoft.AspNetCore.Http.HttpResults.Ok<ChangeSearchResult>, Microsoft.AspNetCore.Http.HttpResults.ValidationProblem>> (string? type, string? state, string? q, bool? open, string? sort, string? dir, int? limit, int? offset, HttpContext ctx, ChangeService svc, CancellationToken ct) =>
            !ChangeService.SortKeys.TryResolve(sort, dir, out var spec, out var error) ? OutcomeResults.BadSort(error!)
            : TypedResults.Ok(await svc.SearchAsync(SubjectFactory.Require(ctx), new ChangeSearchQuery(type?.ToUpperInvariant(), state, q, open == true, limit ?? 50, offset ?? 0, spec), CorrelationId.Get(ctx), ct)));

        changes.MapPost("", async (CreateChangeRequest req, HttpContext ctx, ChangeService svc, CancellationToken ct) =>
        {
            var markings = req.Markings.ToMarkings();
            if (!markings.IsSuccess) return Outcome<ChangeDetail>.Invalid(markings.Errors).ToHttp();
            var cmd = new CreateChangeCommand(req.Type.ToUpperInvariant(), req.Title, req.Description, req.Priority, req.Reason, markings.Value, req.ChangeClass, req.SourceNumber);
            return (await svc.CreateAsync(SubjectFactory.Require(ctx), cmd, CorrelationId.Get(ctx), ct)).ToHttp(d => Results.Created($"/api/v1/changes/{d.Number}", d));
        });

        changes.MapGet("/reason-codes", async (string type, string verb, ChangeService svc, CancellationToken ct) =>
            await svc.ReasonCodesAsync(type.ToUpperInvariant(), verb, ct));

        // ---- the caller's change tasks (before the {number} routes so "tasks" is never read as a number)
        changes.MapGet("/tasks/mine", async (HttpContext ctx, ChangeService svc, CancellationToken ct) =>
            await svc.MyTasksAsync(SubjectFactory.Require(ctx), CorrelationId.Get(ctx), ct));

        changes.MapPost("/tasks/{task}/start", async (string task, HttpContext ctx, ChangeService svc, CancellationToken ct) =>
            (await svc.StartTaskAsync(SubjectFactory.Require(ctx), task, CorrelationId.Get(ctx), ct)).ToHttp());

        changes.MapPost("/tasks/{task}/complete", async (string task, TaskTextRequest req, HttpContext ctx, ChangeService svc, CancellationToken ct) =>
            (await svc.CompleteTaskAsync(SubjectFactory.Require(ctx), task, req.Text ?? "", CorrelationId.Get(ctx), ct)).ToHttp());

        changes.MapPost("/tasks/{task}/cancel", async (string task, TaskTextRequest req, HttpContext ctx, ChangeService svc, CancellationToken ct) =>
            (await svc.CancelTaskAsync(SubjectFactory.Require(ctx), task, req.Text ?? "", CorrelationId.Get(ctx), ct)).ToHttp());

        changes.MapGet("/affecting/{objectType}/{key}", async (string objectType, string key, string? revision, HttpContext ctx, ChangeService svc, CancellationToken ct) =>
            (await svc.ChangesForObjectAsync(SubjectFactory.Require(ctx), objectType, key, revision, CorrelationId.Get(ctx), ct)).ToHttp());

        // ---- one change object
        changes.MapGet("/{number}", async (string number, HttpContext ctx, ChangeService svc, CancellationToken ct) =>
            (await svc.GetAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());

        changes.MapGet("/{number}/history", async (string number, HttpContext ctx, ChangeService svc, CancellationToken ct) =>
            (await svc.GetAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).Map(d => d.History).ToHttp());

        changes.MapPut("/{number}", async (string number, UpdateChangeRequest req, HttpContext ctx, ChangeService svc, CancellationToken ct) =>
            (await svc.UpdateAsync(SubjectFactory.Require(ctx), number, new UpdateChangeCommand(req.Title, req.Description, req.Priority, req.Reason), CorrelationId.Get(ctx), ct)).ToHttp());

        changes.MapPut("/{number}/class", async (string number, ClassifyChangeRequest req, HttpContext ctx, ChangeService svc, CancellationToken ct) =>
            (await svc.ClassifyAsync(SubjectFactory.Require(ctx), number, req.ChangeClass, CorrelationId.Get(ctx), ct)).ToHttp());

        changes.MapPut("/{number}/markings", async (string number, ChangeObjectMarkingsRequest req, HttpContext ctx, ChangeService svc, CancellationToken ct) =>
        {
            var markings = req.Markings.ToMarkings();
            if (!markings.IsSuccess) return Outcome<ChangeDetail>.Invalid(markings.Errors).ToHttp();
            return (await svc.ChangeMarkingsAsync(SubjectFactory.Require(ctx), number, markings.Value, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp();
        });

        // ---- conversions: PR -> ECR, ECR -> ECN
        changes.MapPost("/{number}/ecr", async (string number, CreateChangeFromRequest req, HttpContext ctx, ChangeService svc, CancellationToken ct) =>
            await ConvertAsync(number, ChangeTypes.Ecr, req, ctx, svc, ct));

        changes.MapPost("/{number}/ecn", async (string number, CreateChangeFromRequest req, HttpContext ctx, ChangeService svc, CancellationToken ct) =>
            await ConvertAsync(number, ChangeTypes.Ecn, req, ctx, svc, ct));

        // ---- affected and resulting objects
        changes.MapPost("/{number}/affected", async (string number, AddAffectedRequest req, HttpContext ctx, ChangeService svc, CancellationToken ct) =>
            (await svc.AddAffectedAsync(SubjectFactory.Require(ctx), number, new AddAffectedCommand(req.ObjectType, req.Key, req.Action, req.Revision, req.RaiseMarkings, req.Effectivity), CorrelationId.Get(ctx), ct)).ToHttp());

        changes.MapDelete("/{number}/affected/{id:guid}", async (string number, Guid id, HttpContext ctx, ChangeService svc, CancellationToken ct) =>
            (await svc.RemoveAffectedAsync(SubjectFactory.Require(ctx), number, id, CorrelationId.Get(ctx), ct)).ToHttp());

        changes.MapGet("/{number}/impact", async (string number, int? levels, HttpContext ctx, ImpactAnalysisService svc, CancellationToken ct) =>
            (await svc.AnalyzeAsync(SubjectFactory.Require(ctx), number, levels ?? ImpactAnalysisService.DefaultLevels, CorrelationId.Get(ctx), ct)).ToHttp());

        // ---- lifecycle and review
        changes.MapPost("/{number}/state", async (string number, ChangeTransitionRequest req, HttpContext ctx, ChangeService svc, CancellationToken ct) =>
            (await svc.TransitionAsync(SubjectFactory.Require(ctx), number, req.ToState, req.ReasonCode, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp());

        // The review itself is the workflow engine's: an ECR goes to the CCB, an ECN to its release review (ECR/ECN only).
        changes.MapPost("/{number}/review", async (string number, StartReviewRequest req, HttpContext ctx, WorkflowService flow, CancellationToken ct) =>
        {
            var objectType = ReviewObjectType(number);
            if (objectType is null) return Outcome<InstanceView>.Invalid(["Only a change request or a change notice is submitted for review."]).ToHttp();
            return (await flow.StartAsync(SubjectFactory.Require(ctx), new StartReviewCommand(objectType, number, req.Participants ?? [], req.TemplateId, req.Comment), CorrelationId.Get(ctx), ct))
                .ToHttp(i => Results.Created($"/api/v1/workflows/{i.Id}", i));
        });

        changes.MapGet("/{number}/workflow", async (string number, HttpContext ctx, WorkflowService flow, CancellationToken ct) =>
        {
            var objectType = ReviewObjectType(number);
            return objectType is null
                ? Outcome<ObjectWorkflowStatus>.Invalid(["Only a change request or a change notice is reviewed."]).ToHttp()
                : (await flow.StatusForObjectAsync(SubjectFactory.Require(ctx), objectType, number, CorrelationId.Get(ctx), ct)).ToHttp();
        });

        // ---- tasks
        changes.MapPost("/{number}/tasks", async (string number, CreateChangeTaskRequest req, HttpContext ctx, ChangeService svc, CancellationToken ct) =>
            (await svc.CreateTaskAsync(SubjectFactory.Require(ctx), number, new CreateTaskCommand(req.Title, req.Description, req.Assignee, req.DueDate), CorrelationId.Get(ctx), ct)).ToHttp());
    }

    private static string? ReviewObjectType(string number) =>
        number.StartsWith("ECN-", StringComparison.Ordinal) ? "ecn" : number.StartsWith("ECR-", StringComparison.Ordinal) ? "ecr" : null;

    private static async Task<IResult> ConvertAsync(string number, string targetType, CreateChangeFromRequest req, HttpContext ctx, ChangeService svc, CancellationToken ct)
    {
        Markings? markings = null;
        if (req.Markings is not null)
        {
            var parsed = req.Markings.ToMarkings();
            if (!parsed.IsSuccess) return Outcome<ChangeDetail>.Invalid(parsed.Errors).ToHttp();
            markings = parsed.Value;
        }
        var cmd = new CreateFromCommand(req.Title, req.Description, req.Priority, req.Reason, req.ChangeClass, markings);
        return (await svc.CreateFromAsync(SubjectFactory.Require(ctx), number, targetType, cmd, CorrelationId.Get(ctx), ct)).ToHttp(d => Results.Created($"/api/v1/changes/{d.Number}", d));
    }

    private static Outcome<TOut> Map<TIn, TOut>(this Outcome<TIn> o, Func<TIn, TOut> map) =>
        o.IsOk ? Outcome<TOut>.Ok(map(o.Value!)) : new Outcome<TOut>(o.Kind, default, o.Errors);
}
