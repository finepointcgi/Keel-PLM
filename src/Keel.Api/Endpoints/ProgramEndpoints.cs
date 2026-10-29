using Keel.Api.Infrastructure;
using Keel.Core;
using Keel.Programs;
using Keel.Workflow;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Keel.Api.Endpoints;

public sealed record CreateProgramRequest(string Code, string Name, string? Description, string? CustomerRef, string? ContractRef, string Manager, DateOnly? Start, DateOnly? End, MarkingsDto Markings);
public sealed record UpdateProgramRequest(string Name, string? Description, string? CustomerRef, string? ContractRef, string Status, string Manager, DateOnly? Start, DateOnly? End, DateOnly? ActualEnd);
public sealed record AddItemRequest(string Kind, string? Parent, string Title, string? Description, string? Owner, DateOnly Start, DateOnly End, string? GateType, MarkingsDto? Markings);
public sealed record UpdateItemRequest(string Title, string? Description, string? Owner, DateOnly Start, DateOnly End, string? Reason);
public sealed record ItemProgressRequest(string Status, int Percent, DateOnly? ActualStart, DateOnly? ActualEnd);
public sealed record DependencyRequest(string Predecessor, string Successor, int? LagDays);
public sealed record BaselineRequest(string Name);
public sealed record AddDeliverableRequest(string Title, string ObjectKind, string ObjectKey, DateOnly Due, string? Item, string? Owner, MarkingsDto? Markings);
public sealed record ChecklistAddRequest(string Text, bool? Required);
public sealed record ChecklistStatusRequest(string Status, string? Note);

/// <summary>
/// Programs REST API (ADR-0043): a thin mapping of the program service's outcomes to HTTP. A program, item or gate the caller may not read answers 404, a refused change 403 with
/// reason codes, a rule that blocks the step 409. Documented in /openapi/programs.json.
/// </summary>
public static class ProgramEndpoints
{
    public static void MapProgramEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/programs").RequireAuthorization(Policies.AnyPlmRole).WithTags("Programs");

        g.MapGet("", async Task<Results<Ok<ProgramSearchResult>, ValidationProblem>> (string? q, string? status, string? sort, string? dir, int? limit, int? offset, HttpContext ctx, ProgramService svc, CancellationToken ct) =>
            !ProgramService.SortKeys.TryResolve(sort, dir, out var spec, out var error) ? OutcomeResults.BadSort(error!)
            : TypedResults.Ok(await svc.SearchAsync(SubjectFactory.Require(ctx), new ProgramSearchQuery(q, status, limit ?? 50, offset ?? 0, spec), CorrelationId.Get(ctx), ct)));
        g.MapGet("/my-work", async (int? limit, HttpContext ctx, ProgramService svc, CancellationToken ct) => await svc.MyWorkAsync(SubjectFactory.Require(ctx), limit ?? 10, CorrelationId.Get(ctx), ct));
        g.MapPost("", async (CreateProgramRequest req, HttpContext ctx, ProgramService svc, CancellationToken ct) =>
        {
            var m = req.Markings.ToMarkings();
            if (!m.IsSuccess) return Outcome<ProgramDetail>.Invalid(m.Errors).ToHttp();
            return (await svc.CreateAsync(SubjectFactory.Require(ctx), new CreateProgramCommand(req.Code, req.Name, req.Description, req.CustomerRef, req.ContractRef, req.Manager, req.Start, req.End, m.Value), CorrelationId.Get(ctx), ct))
                .ToHttp(d => Results.Created($"/api/v1/programs/{d.Code}", d));
        });
        g.MapGet("/{code}", async (string code, HttpContext ctx, ProgramService svc, CancellationToken ct) => (await svc.GetAsync(SubjectFactory.Require(ctx), code, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPut("/{code}", async (string code, UpdateProgramRequest req, HttpContext ctx, ProgramService svc, CancellationToken ct) =>
            (await svc.UpdateAsync(SubjectFactory.Require(ctx), code, new UpdateProgramCommand(req.Name, req.Description, req.CustomerRef, req.ContractRef, req.Status, req.Manager, req.Start, req.End, req.ActualEnd), CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/{code}/dashboard", async (string code, DateOnly? asOf, HttpContext ctx, ProgramService svc, CancellationToken ct) =>
            (await svc.GetDashboardAsync(SubjectFactory.Require(ctx), code, asOf, CorrelationId.Get(ctx), ct)).ToHttp());

        // ---- schedule
        g.MapGet("/{code}/schedule", async (string code, int? baseline, DateOnly? asOf, HttpContext ctx, ProgramService svc, CancellationToken ct) =>
            (await svc.GetScheduleAsync(SubjectFactory.Require(ctx), code, baseline, asOf, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/{code}/items", async (string code, AddItemRequest req, HttpContext ctx, ProgramService svc, CancellationToken ct) =>
        {
            Markings? m = null;
            if (req.Markings is not null)
            {
                var parsed = req.Markings.ToMarkings();
                if (!parsed.IsSuccess) return Outcome<ItemRow>.Invalid(parsed.Errors).ToHttp();
                m = parsed.Value;
            }
            return (await svc.AddItemAsync(SubjectFactory.Require(ctx), code, new AddItemCommand(req.Kind, req.Parent, req.Title, req.Description, req.Owner, req.Start, req.End, req.GateType, m), CorrelationId.Get(ctx), ct))
                .ToHttp(i => Results.Created($"/api/v1/programs/{code}/items/{i.Key}", i));
        });
        g.MapPut("/{code}/items/{key}", async (string code, string key, UpdateItemRequest req, HttpContext ctx, ProgramService svc, CancellationToken ct) =>
            (await svc.UpdateItemAsync(SubjectFactory.Require(ctx), code, key, new UpdateItemCommand(req.Title, req.Description, req.Owner, req.Start, req.End, req.Reason), CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPut("/{code}/items/{key}/progress", async (string code, string key, ItemProgressRequest req, HttpContext ctx, ProgramService svc, CancellationToken ct) =>
            (await svc.SetProgressAsync(SubjectFactory.Require(ctx), code, key, new SetProgressCommand(req.Status, req.Percent, req.ActualStart, req.ActualEnd), CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/{code}/dependencies", async (string code, DependencyRequest req, HttpContext ctx, ProgramService svc, CancellationToken ct) =>
            (await svc.AddDependencyAsync(SubjectFactory.Require(ctx), code, req.Predecessor, req.Successor, req.LagDays ?? 0, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapDelete("/{code}/dependencies", async (string code, string predecessor, string successor, HttpContext ctx, ProgramService svc, CancellationToken ct) =>
            (await svc.RemoveDependencyAsync(SubjectFactory.Require(ctx), code, predecessor, successor, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/{code}/baselines", async (string code, BaselineRequest req, HttpContext ctx, ProgramService svc, CancellationToken ct) =>
            (await svc.CreateBaselineAsync(SubjectFactory.Require(ctx), code, req.Name, CorrelationId.Get(ctx), ct)).ToHttp(b => Results.Created($"/api/v1/programs/{code}/schedule?baseline={b.Seq}", b)));

        // ---- deliverables
        g.MapGet("/{code}/deliverables", async (string code, HttpContext ctx, ProgramService svc, CancellationToken ct) =>
            (await svc.ListDeliverablesAsync(SubjectFactory.Require(ctx), code, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/{code}/deliverables", async (string code, AddDeliverableRequest req, HttpContext ctx, ProgramService svc, CancellationToken ct) =>
        {
            Markings? m = null;
            if (req.Markings is not null)
            {
                var parsed = req.Markings.ToMarkings();
                if (!parsed.IsSuccess) return Outcome<DeliverableRow>.Invalid(parsed.Errors).ToHttp();
                m = parsed.Value;
            }
            return (await svc.AddDeliverableAsync(SubjectFactory.Require(ctx), code, new AddDeliverableCommand(req.Title, req.ObjectKind, req.ObjectKey, req.Due, req.Item, req.Owner, m), CorrelationId.Get(ctx), ct))
                .ToHttp(d => Results.Created($"/api/v1/programs/{code}/deliverables", d));
        });
        g.MapDelete("/{code}/deliverables/{number}", async (string code, string number, HttpContext ctx, ProgramService svc, CancellationToken ct) =>
            (await svc.RemoveDeliverableAsync(SubjectFactory.Require(ctx), code, number, CorrelationId.Get(ctx), ct)).ToHttp());

        // ---- gate reviews
        var gates = app.MapGroup("/api/v1/gates").RequireAuthorization(Policies.AnyPlmRole).WithTags("Programs");
        gates.MapGet("/{number}", async (string number, HttpContext ctx, ProgramService svc, CancellationToken ct) => (await svc.GetGateAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        gates.MapPost("/{number}/checklist", async (string number, ChecklistAddRequest req, HttpContext ctx, ProgramService svc, CancellationToken ct) =>
            (await svc.AddChecklistItemAsync(SubjectFactory.Require(ctx), number, req.Text, req.Required ?? true, CorrelationId.Get(ctx), ct)).ToHttp());
        gates.MapPut("/{number}/checklist/{id:guid}", async (string number, Guid id, ChecklistStatusRequest req, HttpContext ctx, ProgramService svc, CancellationToken ct) =>
            (await svc.SetChecklistStatusAsync(SubjectFactory.Require(ctx), number, id, req.Status, req.Note, CorrelationId.Get(ctx), ct)).ToHttp());
        gates.MapDelete("/{number}/checklist/{id:guid}", async (string number, Guid id, HttpContext ctx, ProgramService svc, CancellationToken ct) =>
            (await svc.RemoveChecklistItemAsync(SubjectFactory.Require(ctx), number, id, CorrelationId.Get(ctx), ct)).ToHttp());
        gates.MapPost("/{number}/review", async (string number, StartReviewRequest req, HttpContext ctx, WorkflowService flow, CancellationToken ct) =>
            (await flow.StartAsync(SubjectFactory.Require(ctx), new StartReviewCommand(AwarenessKinds.Gate, number, req.Participants ?? [], req.TemplateId, req.Comment), CorrelationId.Get(ctx), ct))
                .ToHttp(i => Results.Created($"/api/v1/workflows/{i.Id}", i)));
        gates.MapGet("/{number}/workflow", async (string number, HttpContext ctx, WorkflowService flow, CancellationToken ct) =>
            (await flow.StatusForObjectAsync(SubjectFactory.Require(ctx), AwarenessKinds.Gate, number, CorrelationId.Get(ctx), ct)).ToHttp());
    }
}
