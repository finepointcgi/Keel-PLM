using Keel.Api.Infrastructure;
using Keel.Core;
using Keel.Records;

namespace Keel.Api.Endpoints;

public sealed record CreateScheduleRequest(string Title, string? Notes = null);
public sealed record UpdateScheduleRequest(string Title, int Revision, string? Notes = null);
public sealed record ProgramEventRequest(string Program, string Event, DateOnly OccurredOn);
public sealed record CreateDispositionRequest(ObjectRef[] Items, string? Note = null);
public sealed record CreateArchiveRequest(string Title, ObjectRef[] Items);

/// <summary>
/// REST for records management (ADR-0041): retention schedules, program events, classification, legal holds, the disposition queue and requests, certificates of
/// destruction and WORM-style archives. Thin: every rule, access decision and audit event lives in the module services. Reviews (publishing a schedule, releasing a hold,
/// approving a disposition) go through the existing workflow API with object types <c>retention_schedule</c>, <c>legal_hold</c> and <c>disposition</c>.
/// </summary>
public static class RecordsEndpoints
{
    public static void MapRecordsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/records").RequireAuthorization(Policies.Records);

        // ---- schedules
        g.MapGet("/schedules", async (HttpContext ctx, ScheduleService svc, CancellationToken ct) => (await svc.ListAsync(SubjectFactory.Require(ctx), CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/schedules", async (CreateScheduleRequest req, HttpContext ctx, ScheduleService svc, CancellationToken ct) =>
            (await svc.CreateDraftAsync(SubjectFactory.Require(ctx), req.Title, req.Notes, CorrelationId.Get(ctx), ct)).ToHttp(v => Results.Created($"/api/v1/records/schedules/{v.Version}", v)));
        g.MapGet("/schedules/published", async (HttpContext ctx, ScheduleService svc, CancellationToken ct) => (await svc.GetPublishedAsync(SubjectFactory.Require(ctx), CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/schedules/{version:int}", async (int version, HttpContext ctx, ScheduleService svc, CancellationToken ct) => (await svc.GetAsync(SubjectFactory.Require(ctx), version, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPut("/schedules/{version:int}", async (int version, UpdateScheduleRequest req, HttpContext ctx, ScheduleService svc, CancellationToken ct) =>
            (await svc.UpdateDraftAsync(SubjectFactory.Require(ctx), version, req.Title, req.Notes, req.Revision, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPut("/schedules/{version:int}/classes", async (int version, ClassInput input, HttpContext ctx, ScheduleService svc, CancellationToken ct) =>
            (await svc.UpsertClassAsync(SubjectFactory.Require(ctx), version, input, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapDelete("/schedules/{version:int}/classes/{code}", async (int version, string code, HttpContext ctx, ScheduleService svc, CancellationToken ct) =>
            (await svc.RemoveClassAsync(SubjectFactory.Require(ctx), version, code, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/schedules/{version:int}/discard", async (int version, HttpContext ctx, ScheduleService svc, CancellationToken ct) =>
            (await svc.DiscardDraftAsync(SubjectFactory.Require(ctx), version, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/program-events", async (HttpContext ctx, ScheduleService svc, CancellationToken ct) => (await svc.ListProgramEventsAsync(SubjectFactory.Require(ctx), CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/program-events", async (ProgramEventRequest req, HttpContext ctx, ScheduleService svc, CancellationToken ct) =>
            (await svc.RecordProgramEventAsync(SubjectFactory.Require(ctx), req.Program, req.Event, req.OccurredOn, CorrelationId.Get(ctx), ct)).ToHttp(v => Results.Created($"/api/v1/records/program-events", v)));
        g.MapPost("/classify", async (HttpContext ctx, RetentionClassificationService svc, CancellationToken ct) => (await svc.ClassifyAsync(SubjectFactory.Require(ctx), CorrelationId.Get(ctx), ct)).ToHttp());

        // ---- legal holds
        g.MapGet("/holds", async (bool? includeReleased, HttpContext ctx, HoldService svc, CancellationToken ct) => (await svc.ListAsync(SubjectFactory.Require(ctx), includeReleased == true, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/holds", async (PlaceHoldCommand req, HttpContext ctx, HoldService svc, CancellationToken ct) =>
            (await svc.PlaceAsync(SubjectFactory.Require(ctx), req, CorrelationId.Get(ctx), ct)).ToHttp(v => Results.Created($"/api/v1/records/holds/{v.Number}", v)));
        g.MapGet("/holds/{number}", async (string number, HttpContext ctx, HoldService svc, CancellationToken ct) => (await svc.GetAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/holds/{number}/members", async (string number, int? limit, int? offset, HttpContext ctx, HoldService svc, CancellationToken ct) =>
            (await svc.MembersAsync(SubjectFactory.Require(ctx), number, limit ?? 100, offset ?? 0, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/holds/{number}/reevaluate", async (string number, HttpContext ctx, HoldService svc, CancellationToken ct) =>
            (await svc.ReevaluateAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp(n => Results.Ok(new { added = n })));
        g.MapGet("/objects/{kind}/{number}/status", async (string kind, string number, HttpContext ctx, HoldService svc, CancellationToken ct) =>
            await svc.StatusOfAsync(SubjectFactory.Require(ctx), kind, number, ct) is { } status ? Results.Ok(status) : Results.NotFound());

        // ---- disposition
        g.MapGet("/eligible", async (string? kind, string? classCode, string? q, int? limit, int? offset, HttpContext ctx, DispositionService svc, CancellationToken ct) =>
            (await svc.EligibleAsync(SubjectFactory.Require(ctx), new EligibleQuery(kind, classCode, q, limit ?? 50, offset ?? 0), CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/dispositions", async (string? status, HttpContext ctx, DispositionService svc, CancellationToken ct) => (await svc.ListAsync(SubjectFactory.Require(ctx), status, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/dispositions", async (CreateDispositionRequest req, HttpContext ctx, DispositionService svc, CancellationToken ct) =>
            (await svc.CreateAsync(SubjectFactory.Require(ctx), new CreateDispositionCommand(req.Items, req.Note), CorrelationId.Get(ctx), ct)).ToHttp(v => Results.Created($"/api/v1/records/dispositions/{v.Number}", v)));
        g.MapGet("/dispositions/{number}", async (string number, HttpContext ctx, DispositionService svc, CancellationToken ct) => (await svc.GetAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/dispositions/{number}/cancel", async (string number, HttpContext ctx, DispositionService svc, CancellationToken ct) => (await svc.CancelAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/dispositions/{number}/execute", async (string number, HttpContext ctx, DispositionService svc, CancellationToken ct) => (await svc.ExecuteAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/certificates", async (HttpContext ctx, DispositionService svc, CancellationToken ct) => (await svc.ListCertificatesAsync(SubjectFactory.Require(ctx), CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/certificates/{number}", async (string number, HttpContext ctx, DispositionService svc, CancellationToken ct) => (await svc.GetCertificateAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/certificates/{number}/confirm", async (string number, HttpContext ctx, DispositionService svc, CancellationToken ct) => (await svc.ConfirmDestructionAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());

        // ---- archives
        g.MapGet("/archives", async (HttpContext ctx, ArchiveService svc, CancellationToken ct) => (await svc.ListAsync(SubjectFactory.Require(ctx), CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/archives", async (CreateArchiveRequest req, HttpContext ctx, ArchiveService svc, CancellationToken ct) =>
            (await svc.CreateAsync(SubjectFactory.Require(ctx), new CreateArchiveCommand(req.Title, req.Items), CorrelationId.Get(ctx), ct)).ToHttp(v => Results.Created($"/api/v1/records/archives/{v.Number}", v)));
        g.MapGet("/archives/{number}", async (string number, HttpContext ctx, ArchiveService svc, CancellationToken ct) => (await svc.GetAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/archives/{number}/contents", async (string number, HttpContext ctx, ArchiveService svc, CancellationToken ct) => (await svc.OpenAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/archives/{number}/verify", async (string number, HttpContext ctx, ArchiveService svc, CancellationToken ct) => (await svc.VerifyAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
    }
}
