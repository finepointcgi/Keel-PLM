using Keel.Api.Infrastructure;
using Keel.Core;
using Keel.Process;
using Keel.Workflow;

namespace Keel.Api.Endpoints;

public sealed record CreatePlanRequest(string Mbom, string? Description = null, MarkingsDto? Markings = null, bool ConfirmRaiseMarkings = false);
public sealed record CheckInPlanRequest(string? Comment = null);
public sealed record CreateToolRequest(string Name, string Kind, string Plant, MarkingsDto Markings, string? Description = null, bool RequiresCalibration = false, int? IntervalDays = null, DateOnly? LastCalibratedOn = null);
public sealed record UpdateToolRequest(string Name, bool RequiresCalibration, int? IntervalDays, int ExpectedRevision, string? Description = null);
public sealed record CalibrationRequest(DateOnly CalibratedOn, string? Reference = null, string? Note = null);
public sealed record ToolStatusRequest(string Reason);
public sealed record WorkCenterRequest(string Plant, string Code, string Name, string ResourceKind = "work_center", bool IsActive = true);

/// <summary>
/// REST for process plans, work instructions, tools and work centers (ADR-0042). Thin: every rule is in the module services. A plan or tool the caller cannot read answers 404, exactly like
/// one that does not exist. Documented in /openapi/process.json (committed as docs/process/openapi.json).
/// </summary>
public static class ProcessEndpoints
{
    public static void MapProcessEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/process").RequireAuthorization(Policies.AnyPlmRole).WithTags("Manufacturing process data");
        static Subject Who(HttpContext c) => SubjectFactory.Require(c);
        static string? Corr(HttpContext c) => CorrelationId.Get(c);

        // ---- plans
        g.MapGet("/plans", async (string? q, string? plant, string? state, bool? stale, string? sort, string? dir, int? limit, int? offset, HttpContext c, ProcessPlanService svc, CancellationToken ct) =>
            !ProcessPlanService.SortKeys.TryResolve(sort, dir, out var spec, out var error) ? (IResult)OutcomeResults.BadSort(error!)
            : (await svc.SearchAsync(Who(c), new PlanSearch(q, plant, state, stale == true, limit ?? 50, offset ?? 0, spec), Corr(c), ct)).ToHttp()).WithSummary("Process plans you may read (plan, MBOM and part).");

        g.MapPost("/plans", async (CreatePlanRequest r, HttpContext c, ProcessPlanService svc, CancellationToken ct) =>
        {
            Markings? m = null;
            if (r.Markings is not null)
            {
                var parsed = r.Markings.ToMarkings();
                if (!parsed.IsSuccess) return Outcome<PlanDetail>.Invalid(parsed.Errors).ToHttp();
                m = parsed.Value;
            }
            return (await svc.CreateAsync(Who(c), new CreatePlanCommand(r.Mbom, r.Description, m, r.ConfirmRaiseMarkings), Corr(c), ct)).ToHttp(d => Results.Created($"/api/v1/process/plans/{d.Summary.Number}", d));
        }).WithSummary("Creates the process plan of an MBOM that has a released revision, bound to that revision.");

        g.MapGet("/plans/{number}", async (string number, string? revision, HttpContext c, ProcessPlanService svc, CancellationToken ct) =>
            (await svc.GetAsync(Who(c), number, revision, Corr(c), ct)).ToHttp()).WithSummary("A plan with routings, operations, work instructions, findings and staleness.");

        g.MapGet("/plans/{number}/matrix", async (string number, string? revision, string? routing, HttpContext c, ProcessPlanService svc, CancellationToken ct) =>
        {
            var detail = await svc.GetAsync(Who(c), number, revision, Corr(c), ct);
            if (!detail.IsOk) return detail.ToHttp();
            var chosen = detail.Value!.Routings.FirstOrDefault(r => routing is null ? r.IsPrimary : r.Code == routing);
            return chosen is null ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found") : Results.Ok(PlanMatrix.Build(chosen, detail.Value.Lines));
        }).WithSummary("The operation-by-component matrix of a routing.");

        g.MapGet("/plans/{number}/shop-floor", async (string number, string? revision, string? routing, HttpContext c, ProcessPlanService svc, CancellationToken ct) =>
            (await svc.GetShopFloorAsync(Who(c), number, revision, routing, Corr(c), ct)).ToHttp()).WithSummary("The read-only shop floor view of a released revision (one routing).");

        g.MapGet("/mboms/{mbom}/plan", async (string mbom, HttpContext c, ProcessPlanService svc, CancellationToken ct) =>
            (await svc.ForMbomAsync(Who(c), mbom, Corr(c), ct)).ToHttp()).WithSummary("The process plan of an MBOM, when there is one you may read.");

        g.MapPost("/plans/{number}/checkout", async (string number, HttpContext c, ProcessPlanService svc, CancellationToken ct) => (await svc.CheckOutAsync(Who(c), number, Corr(c), ct)).ToHttp());
        g.MapGet("/plans/{number}/working-copy", async (string number, HttpContext c, ProcessPlanService svc, CancellationToken ct) => (await svc.GetWorkingCopyAsync(Who(c), number, Corr(c), ct)).ToHttp())
            .WithSummary("Your working copy; attachments you may not view are returned without a document number and kept on save.");
        g.MapPut("/plans/{number}/working-copy", async (string number, PlanContent content, HttpContext c, ProcessPlanService svc, CancellationToken ct) => (await svc.SaveWorkingCopyAsync(Who(c), number, content, Corr(c), ct)).ToHttp())
            .WithSummary("Replaces the content of your working copy after validating structure, work centers, tools, attachments and assignments.");
        g.MapPost("/plans/{number}/checkin", async (string number, CheckInPlanRequest r, HttpContext c, ProcessPlanService svc, CancellationToken ct) => (await svc.CheckInAsync(Who(c), number, r.Comment, Corr(c), ct)).ToHttp());
        g.MapPost("/plans/{number}/undo-checkout", async (string number, HttpContext c, ProcessPlanService svc, CancellationToken ct) => (await svc.UndoCheckOutAsync(Who(c), number, Corr(c), ct)).ToHttp());
        g.MapPost("/plans/{number}/revise", async (string number, HttpContext c, ProcessPlanService svc, CancellationToken ct) => (await svc.ReviseAsync(Who(c), number, Corr(c), ct)).ToHttp());
        g.MapPost("/plans/{number}/rebind", async (string number, HttpContext c, ProcessPlanService svc, CancellationToken ct) => (await svc.RebindAsync(Who(c), number, Corr(c), ct)).ToHttp())
            .WithSummary("Binds the working revision to the MBOM revision released now.");

        // ---- review through the workflow service (object type "process_plan")
        g.MapPost("/plans/{number}/review", async (string number, StartReviewRequest req, HttpContext c, WorkflowService svc, CancellationToken ct) =>
            (await svc.StartAsync(Who(c), new StartReviewCommand(ProcessPlanService.ObjectType, number, req.Participants ?? [], req.TemplateId, req.Comment), Corr(c), ct))
                .ToHttp(i => Results.Created($"/api/v1/workflows/{i.Id}", i)));
        g.MapGet("/plans/{number}/workflow", async (string number, HttpContext c, WorkflowService svc, CancellationToken ct) =>
            (await svc.StatusForObjectAsync(Who(c), ProcessPlanService.ObjectType, number, Corr(c), ct)).ToHttp());

        // ---- tools, fixtures, gauges
        g.MapGet("/tools", async (string? q, string? plant, string? kind, bool? due, bool? retired, string? sort, string? dir, int? limit, int? offset, HttpContext c, ToolingService svc, CancellationToken ct) =>
            !ToolingService.SortKeys.TryResolve(sort, dir, out var spec, out var error) ? (IResult)OutcomeResults.BadSort(error!)
            : (await svc.SearchAsync(Who(c), new ToolSearch(q, plant, kind, due == true, retired == true, limit ?? 50, offset ?? 0, spec), Corr(c), ct)).ToHttp());
        g.MapPost("/tools", async (CreateToolRequest r, HttpContext c, ToolingService svc, CancellationToken ct) =>
        {
            var m = r.Markings.ToMarkings();
            if (!m.IsSuccess) return Outcome<ToolDetail>.Invalid(m.Errors).ToHttp();
            return (await svc.CreateAsync(Who(c), new CreateToolCommand(r.Name, r.Kind, r.Plant, r.Description, r.RequiresCalibration, r.IntervalDays, r.LastCalibratedOn, m.Value), Corr(c), ct))
                .ToHttp(d => Results.Created($"/api/v1/process/tools/{d.Summary.Number}", d));
        });
        g.MapGet("/tools/{number}", async (string number, HttpContext c, ToolingService svc, CancellationToken ct) => (await svc.GetAsync(Who(c), number, Corr(c), ct)).ToHttp());
        g.MapPut("/tools/{number}", async (string number, UpdateToolRequest r, HttpContext c, ToolingService svc, CancellationToken ct) =>
            (await svc.UpdateAsync(Who(c), number, new UpdateToolCommand(r.Name, r.Description, r.RequiresCalibration, r.IntervalDays), r.ExpectedRevision, Corr(c), ct)).ToHttp());
        g.MapPost("/tools/{number}/calibrations", async (string number, CalibrationRequest r, HttpContext c, ToolingService svc, CancellationToken ct) =>
            (await svc.RecordCalibrationAsync(Who(c), number, r.CalibratedOn, r.Reference, r.Note, Corr(c), ct)).ToHttp()).WithSummary("Records a calibration; the next due date is the calibration date plus the interval.");
        g.MapPost("/tools/{number}/retire", async (string number, ToolStatusRequest r, HttpContext c, ToolingService svc, CancellationToken ct) => (await svc.SetStatusAsync(Who(c), number, true, r.Reason, Corr(c), ct)).ToHttp());
        g.MapPost("/tools/{number}/restore", async (string number, ToolStatusRequest r, HttpContext c, ToolingService svc, CancellationToken ct) => (await svc.SetStatusAsync(Who(c), number, false, r.Reason, Corr(c), ct)).ToHttp());

        // ---- work centers and resources (administrators change them)
        g.MapGet("/work-centers", async (string? plant, bool? retired, ToolingService svc, CancellationToken ct) => Results.Ok(await svc.ListWorkCentersAsync(plant, retired == true, ct)));
        g.MapPut("/work-centers", async (WorkCenterRequest r, HttpContext c, ToolingService svc, CancellationToken ct) =>
            (await svc.SaveWorkCenterAsync(Who(c), new SaveWorkCenterCommand(r.Plant, r.Code, r.Name, r.ResourceKind, r.IsActive), Corr(c), ct)).ToHttp());
    }
}
