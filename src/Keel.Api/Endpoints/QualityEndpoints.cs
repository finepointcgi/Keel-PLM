using Keel.Api.Infrastructure;
using Keel.Core;
using Keel.Quality;
using Keel.Workflow;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Keel.Api.Endpoints;

public sealed record CreateNcrRequest(string Title, string? Description, string Severity, string Stage, string PartNumber, string? Revision, string? LotNumber, string? SerialNumber,
    string? SupplierRef, decimal Quantity, string? Unit, MarkingsDto Markings);
public sealed record UpdateNcrRequest(string Title, string? Description, string Severity, string Stage, string? LotNumber, string? SerialNumber, string? SupplierRef, decimal Quantity, string? Unit);
public sealed record ProposeDispositionRequest(string Disposition, string Rationale);
public sealed record QualityMarkingsRequest(MarkingsDto Markings, string Reason);
public sealed record QualityTextRequest(string? Text);
public sealed record CreateCapaRequest(string Title, string? Description, string CapaType, string Source, string Priority, string? Owner, DateOnly? TargetDate, MarkingsDto Markings);
public sealed record UpdateCapaRequest(string Title, string? Description, string CapaType, string Source, string Priority, string Owner, DateOnly? TargetDate);
public sealed record SaveAnalysisRequest(string RootCause, IReadOnlyList<WhyInput>? Whys, IReadOnlyList<CauseInput>? Causes);
public sealed record SaveVerificationPlanRequest(string Method, string Criteria, DateOnly? DueDate);
public sealed record RecordVerificationRequest(string Result, bool Effective);
public sealed record CreateActionRequest(string ActionType, string Description, string Owner, DateOnly? DueDate);
public sealed record AddLinkRequest(string ToKind, string ToKey);
public sealed record CreateDeviationRequest(string Kind, string Title, string? Description, string? RequirementRef, bool CustomerApprovalRequired, DateOnly ValidFrom, DateOnly ValidTo, MarkingsDto Markings);
public sealed record UpdateDeviationRequest(string Kind, string Title, string? Description, string? RequirementRef, bool CustomerApprovalRequired, DateOnly ValidFrom, DateOnly ValidTo);
public sealed record AddScopeRequest(string PartNumber, string? Revision, long? SerialFrom, long? SerialTo, string[]? Serials, string[]? Lots, decimal? MaxQuantity);
public sealed record CustomerApprovalRequest(string Reference, DateOnly ApprovedOn);

/// <summary>
/// Quality REST API (ADR-0036): a thin mapping of the quality services' outcomes to HTTP. A record the caller may not read answers 404, a refused change 403 with reason
/// codes, a rule that blocks the step 409. Documented in /openapi/quality.json.
/// </summary>
public static class QualityEndpoints
{
    public static void MapQualityEndpoints(this IEndpointRouteBuilder app)
    {
        var qg = app.MapGroup("/api/v1/quality").RequireAuthorization(Policies.AnyPlmRole).WithTags("Quality");

        qg.MapGet("/dashboard", async (HttpContext ctx, QualityQueries svc, CancellationToken ct) => await svc.DashboardAsync(SubjectFactory.Require(ctx), CorrelationId.Get(ctx), ct));
        qg.MapGet("/my-actions", async (HttpContext ctx, QualityActions svc, CancellationToken ct) => await svc.MyActionsAsync(SubjectFactory.Require(ctx), CorrelationId.Get(ctx), ct));
        qg.MapGet("/owners", async (string? q, QualityActions svc, CancellationToken ct) => await svc.SearchUsersAsync(q ?? "", 15, ct));
        qg.MapGet("/parts/{number}", async (string number, bool? all, HttpContext ctx, QualityQueries svc, CancellationToken ct) =>
            (await svc.ForPartAsync(SubjectFactory.Require(ctx), number, all == true, CorrelationId.Get(ctx), ct)).ToHttp());
        qg.MapGet("/parts/{number}/applicable", async (string number, string? serial, string? lot, DateOnly? asOf, HttpContext ctx, DeviationService svc, CancellationToken ct) =>
            (await svc.ForPartAsync(SubjectFactory.Require(ctx), number, false, serial, lot, asOf, CorrelationId.Get(ctx), ct)).ToHttp());

        MapCommon(qg, "ncrs", QualityKinds.Ncr);
        MapCommon(qg, "capas", QualityKinds.Capa);
        MapCommon(qg, "deviations", QualityKinds.Deviation);
        MapActions(qg, "ncrs", QualityKinds.Ncr);
        MapActions(qg, "capas", QualityKinds.Capa);

        // ---- nonconformance reports
        var n = qg.MapGroup("/ncrs");
        n.MapPost("", async (CreateNcrRequest req, HttpContext ctx, NcrService svc, CancellationToken ct) =>
        {
            var m = req.Markings.ToMarkings();
            if (!m.IsSuccess) return Outcome<NcrDetail>.Invalid(m.Errors).ToHttp();
            return (await svc.CreateAsync(SubjectFactory.Require(ctx), new CreateNcrCommand(req.Title, req.Description, req.Severity, req.Stage, req.PartNumber, req.Revision, req.LotNumber, req.SerialNumber,
                req.SupplierRef, req.Quantity, req.Unit, m.Value), CorrelationId.Get(ctx), ct)).ToHttp(d => Results.Created($"/api/v1/quality/ncrs/{d.Number}", d));
        });
        n.MapPut("/{number}", async (string number, UpdateNcrRequest req, HttpContext ctx, NcrService svc, CancellationToken ct) =>
            (await svc.UpdateAsync(SubjectFactory.Require(ctx), number, new UpdateNcrCommand(req.Title, req.Description, req.Severity, req.Stage, req.LotNumber, req.SerialNumber, req.SupplierRef, req.Quantity, req.Unit), CorrelationId.Get(ctx), ct)).ToHttp());
        n.MapPut("/{number}/disposition", async (string number, ProposeDispositionRequest req, HttpContext ctx, NcrService svc, CancellationToken ct) =>
            (await svc.ProposeDispositionAsync(SubjectFactory.Require(ctx), number, new ProposeDispositionCommand(req.Disposition, req.Rationale), CorrelationId.Get(ctx), ct)).ToHttp());
        n.MapPut("/{number}/markings", async (string number, QualityMarkingsRequest req, HttpContext ctx, NcrService svc, CancellationToken ct) =>
        {
            var m = req.Markings.ToMarkings();
            return m.IsSuccess ? (await svc.ChangeMarkingsAsync(SubjectFactory.Require(ctx), number, m.Value, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp() : Outcome<NcrDetail>.Invalid(m.Errors).ToHttp();
        });
        n.MapPost("/{number}/close", async (string number, QualityTextRequest req, HttpContext ctx, NcrService svc, CancellationToken ct) =>
            (await svc.CloseAsync(SubjectFactory.Require(ctx), number, req.Text ?? "", CorrelationId.Get(ctx), ct)).ToHttp());
        n.MapPost("/{number}/cancel", async (string number, QualityTextRequest req, HttpContext ctx, NcrService svc, CancellationToken ct) =>
            (await svc.CancelAsync(SubjectFactory.Require(ctx), number, req.Text ?? "", CorrelationId.Get(ctx), ct)).ToHttp());

        // ---- corrective and preventive actions
        var c = qg.MapGroup("/capas");
        c.MapPost("", async (CreateCapaRequest req, HttpContext ctx, CapaService svc, CancellationToken ct) =>
        {
            var m = req.Markings.ToMarkings();
            if (!m.IsSuccess) return Outcome<CapaDetail>.Invalid(m.Errors).ToHttp();
            return (await svc.CreateAsync(SubjectFactory.Require(ctx), new CreateCapaCommand(req.Title, req.Description, req.CapaType, req.Source, req.Priority, req.Owner, req.TargetDate, m.Value), CorrelationId.Get(ctx), ct))
                .ToHttp(d => Results.Created($"/api/v1/quality/capas/{d.Number}", d));
        });
        c.MapPut("/{number}", async (string number, UpdateCapaRequest req, HttpContext ctx, CapaService svc, CancellationToken ct) =>
            (await svc.UpdateAsync(SubjectFactory.Require(ctx), number, new UpdateCapaCommand(req.Title, req.Description, req.CapaType, req.Source, req.Priority, req.Owner, req.TargetDate), CorrelationId.Get(ctx), ct)).ToHttp());
        c.MapPut("/{number}/markings", async (string number, QualityMarkingsRequest req, HttpContext ctx, CapaService svc, CancellationToken ct) =>
        {
            var m = req.Markings.ToMarkings();
            return m.IsSuccess ? (await svc.ChangeMarkingsAsync(SubjectFactory.Require(ctx), number, m.Value, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp() : Outcome<CapaDetail>.Invalid(m.Errors).ToHttp();
        });
        c.MapPut("/{number}/analysis", async (string number, SaveAnalysisRequest req, HttpContext ctx, CapaService svc, CancellationToken ct) =>
            (await svc.SaveAnalysisAsync(SubjectFactory.Require(ctx), number, new SaveAnalysisCommand(req.RootCause, req.Whys ?? [], req.Causes ?? []), CorrelationId.Get(ctx), ct)).ToHttp());
        c.MapPut("/{number}/verification-plan", async (string number, SaveVerificationPlanRequest req, HttpContext ctx, CapaService svc, CancellationToken ct) =>
            (await svc.SaveVerificationPlanAsync(SubjectFactory.Require(ctx), number, new SaveVerificationPlanCommand(req.Method, req.Criteria, req.DueDate), CorrelationId.Get(ctx), ct)).ToHttp());
        c.MapPost("/{number}/verification", async (string number, RecordVerificationRequest req, HttpContext ctx, CapaService svc, CancellationToken ct) =>
            (await svc.RecordVerificationAsync(SubjectFactory.Require(ctx), number, new RecordVerificationCommand(req.Result, req.Effective), CorrelationId.Get(ctx), ct)).ToHttp());
        c.MapPost("/{number}/start", async (string number, HttpContext ctx, CapaService svc, CancellationToken ct) =>
            (await svc.StartAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        c.MapPost("/{number}/cancel", async (string number, QualityTextRequest req, HttpContext ctx, CapaService svc, CancellationToken ct) =>
            (await svc.CancelAsync(SubjectFactory.Require(ctx), number, req.Text ?? "", CorrelationId.Get(ctx), ct)).ToHttp());

        // ---- deviations and waivers
        var d = qg.MapGroup("/deviations");
        d.MapPost("", async (CreateDeviationRequest req, HttpContext ctx, DeviationService svc, CancellationToken ct) =>
        {
            var m = req.Markings.ToMarkings();
            if (!m.IsSuccess) return Outcome<DeviationDetail>.Invalid(m.Errors).ToHttp();
            return (await svc.CreateAsync(SubjectFactory.Require(ctx), new CreateDeviationCommand(req.Kind, req.Title, req.Description, req.RequirementRef, req.CustomerApprovalRequired, req.ValidFrom, req.ValidTo, m.Value), CorrelationId.Get(ctx), ct))
                .ToHttp(x => Results.Created($"/api/v1/quality/deviations/{x.Number}", x));
        });
        d.MapPut("/{number}", async (string number, UpdateDeviationRequest req, HttpContext ctx, DeviationService svc, CancellationToken ct) =>
            (await svc.UpdateAsync(SubjectFactory.Require(ctx), number, new UpdateDeviationCommand(req.Kind, req.Title, req.Description, req.RequirementRef, req.CustomerApprovalRequired, req.ValidFrom, req.ValidTo), CorrelationId.Get(ctx), ct)).ToHttp());
        d.MapPut("/{number}/markings", async (string number, QualityMarkingsRequest req, HttpContext ctx, DeviationService svc, CancellationToken ct) =>
        {
            var m = req.Markings.ToMarkings();
            return m.IsSuccess ? (await svc.ChangeMarkingsAsync(SubjectFactory.Require(ctx), number, m.Value, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp() : Outcome<DeviationDetail>.Invalid(m.Errors).ToHttp();
        });
        d.MapPost("/{number}/scope", async (string number, AddScopeRequest req, HttpContext ctx, DeviationService svc, CancellationToken ct) =>
            (await svc.AddScopeAsync(SubjectFactory.Require(ctx), number, new ScopeInput(req.PartNumber, req.Revision, req.SerialFrom, req.SerialTo, req.Serials, req.Lots, req.MaxQuantity), CorrelationId.Get(ctx), ct)).ToHttp());
        d.MapDelete("/{number}/scope/{id:guid}", async (string number, Guid id, HttpContext ctx, DeviationService svc, CancellationToken ct) =>
            (await svc.RemoveScopeAsync(SubjectFactory.Require(ctx), number, id, CorrelationId.Get(ctx), ct)).ToHttp());
        d.MapPost("/{number}/withdraw", async (string number, QualityTextRequest req, HttpContext ctx, DeviationService svc, CancellationToken ct) =>
            (await svc.WithdrawAsync(SubjectFactory.Require(ctx), number, req.Text ?? "", CorrelationId.Get(ctx), ct)).ToHttp());
        d.MapPost("/{number}/revoke", async (string number, QualityTextRequest req, HttpContext ctx, DeviationService svc, CancellationToken ct) =>
            (await svc.RevokeAsync(SubjectFactory.Require(ctx), number, req.Text ?? "", CorrelationId.Get(ctx), ct)).ToHttp());
        d.MapPost("/{number}/customer-approval", async (string number, CustomerApprovalRequest req, HttpContext ctx, DeviationService svc, CancellationToken ct) =>
            (await svc.RecordCustomerApprovalAsync(SubjectFactory.Require(ctx), number, new CustomerApprovalCommand(req.Reference, req.ApprovedOn), CorrelationId.Get(ctx), ct)).ToHttp());
        d.MapPost("/{number}/customer-reject", async (string number, QualityTextRequest req, HttpContext ctx, DeviationService svc, CancellationToken ct) =>
            (await svc.RejectByCustomerAsync(SubjectFactory.Require(ctx), number, req.Text ?? "", CorrelationId.Get(ctx), ct)).ToHttp());
    }

    /// <summary>List, read, review and links: the same for the three kinds.</summary>
    private static void MapCommon(RouteGroupBuilder root, string segment, string kind)
    {
        var g = root.MapGroup("/" + segment);
        g.MapGet("", async Task<Results<Ok<QualitySearchResult>, ValidationProblem>> (string? state, string? q, bool? open, string? part, string? sort, string? dir, int? limit, int? offset, HttpContext ctx, QualityQueries svc, CancellationToken ct) =>
            !QualityQueries.SortKeysFor(kind).TryResolve(sort, dir, out var spec, out var error) ? OutcomeResults.BadSort(error!)
            : TypedResults.Ok(await svc.SearchAsync(SubjectFactory.Require(ctx), kind, new QualitySearchQuery(q, state, open == true, limit ?? 50, offset ?? 0, spec, part), CorrelationId.Get(ctx), ct)));

        g.MapGet("/{number}", async (string number, HttpContext ctx, NcrService ncr, CapaService capa, DeviationService dev, CancellationToken ct) =>
        {
            var s = SubjectFactory.Require(ctx);
            var corr = CorrelationId.Get(ctx);
            return kind switch
            {
                QualityKinds.Ncr => (await ncr.GetAsync(s, number, corr, ct)).ToHttp(),
                QualityKinds.Capa => (await capa.GetAsync(s, number, corr, ct)).ToHttp(),
                _ => (await dev.GetAsync(s, number, corr, ct)).ToHttp(),
            };
        });

        g.MapPost("/{number}/review", async (string number, StartReviewRequest req, HttpContext ctx, WorkflowService flow, CancellationToken ct) =>
            (await flow.StartAsync(SubjectFactory.Require(ctx), new StartReviewCommand(kind, number, req.Participants ?? [], req.TemplateId, req.Comment), CorrelationId.Get(ctx), ct))
                .ToHttp(i => Results.Created($"/api/v1/workflows/{i.Id}", i)));
        g.MapGet("/{number}/workflow", async (string number, HttpContext ctx, WorkflowService flow, CancellationToken ct) =>
            (await flow.StatusForObjectAsync(SubjectFactory.Require(ctx), kind, number, CorrelationId.Get(ctx), ct)).ToHttp());

        g.MapPost("/{number}/links", async (string number, AddLinkRequest req, HttpContext ctx, QualityActions svc, CancellationToken ct) =>
            (await svc.AddLinkAsync(SubjectFactory.Require(ctx), kind, number, req.ToKind, req.ToKey, CorrelationId.Get(ctx), ct)).ToHttp(l => Results.Created($"/api/v1/quality/{segment}/{number}", l)));
        g.MapDelete("/{number}/links/{id:guid}", async (string number, Guid id, HttpContext ctx, QualityActions svc, CancellationToken ct) =>
            (await svc.RemoveLinkAsync(SubjectFactory.Require(ctx), kind, number, id, CorrelationId.Get(ctx), ct)).ToHttp());
    }

    private static void MapActions(RouteGroupBuilder root, string segment, string kind)
    {
        var g = root.MapGroup("/" + segment);
        g.MapPost("/{number}/actions", async (string number, CreateActionRequest req, HttpContext ctx, QualityActions svc, CancellationToken ct) =>
            (await svc.AddAsync(SubjectFactory.Require(ctx), kind, number, new CreateActionCommand(req.ActionType, req.Description, req.Owner, req.DueDate), CorrelationId.Get(ctx), ct))
                .ToHttp(a => Results.Created($"/api/v1/quality/{segment}/{number}", a)));
        g.MapPost("/{number}/actions/{action}/start", async (string number, string action, HttpContext ctx, QualityActions svc, CancellationToken ct) =>
            (await svc.StartAsync(SubjectFactory.Require(ctx), kind, number, action, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/{number}/actions/{action}/complete", async (string number, string action, QualityTextRequest req, HttpContext ctx, QualityActions svc, CancellationToken ct) =>
            (await svc.CompleteAsync(SubjectFactory.Require(ctx), kind, number, action, req.Text ?? "", CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/{number}/actions/{action}/cancel", async (string number, string action, QualityTextRequest req, HttpContext ctx, QualityActions svc, CancellationToken ct) =>
            (await svc.CancelAsync(SubjectFactory.Require(ctx), kind, number, action, req.Text ?? "", CorrelationId.Get(ctx), ct)).ToHttp());
    }
}
