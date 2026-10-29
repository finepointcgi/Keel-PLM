using System.Text.Json.Nodes;
using Keel.Api.Infrastructure;
using Keel.Core;
using Keel.Parts;
using Npgsql;

namespace Keel.Api.Endpoints;

public sealed record CreatePartRequest(string Name, string PartType, string DefaultUnit, string Source, MarkingsDto Markings,
    string? Description = null, JsonObject? Attributes = null, string? Number = null);
public sealed record WorkingCopyRequest(string? Description, JsonObject? Attributes);
public sealed record CheckInRequest(string? Comment);
public sealed record SetStateRequest(string ToState, string? Reason = null, bool Override = false);
public sealed record ChangeMarkingsRequest(MarkingsDto Markings, string Reason);

public sealed record PartMetadata(IReadOnlyList<string> PartTypes, IReadOnlyList<string> Sources, IReadOnlyList<string> Units,
    IReadOnlyList<AttributeDefinition> Attributes, Lifecycle Lifecycle);

public static class PartEndpoints
{
    public static void MapPartEndpoints(this IEndpointRouteBuilder app)
    {
        var parts = app.MapGroup("/api/v1/parts").RequireAuthorization(Policies.AnyPlmRole);

        parts.MapGet("", async Task<Microsoft.AspNetCore.Http.HttpResults.Results<Microsoft.AspNetCore.Http.HttpResults.Ok<PartSearchResult>, Microsoft.AspNetCore.Http.HttpResults.ValidationProblem>> (string? q, string? state, string? sort, string? dir, int? limit, int? offset, HttpContext ctx, PartService svc, CancellationToken ct) =>
            !PartService.SortKeys.TryResolve(sort, dir, out var spec, out var error) ? OutcomeResults.BadSort(error!)
            : TypedResults.Ok(await svc.SearchAsync(SubjectFactory.Require(ctx), q, state, limit ?? 50, offset ?? 0, spec, CorrelationId.Get(ctx), ct)));

        parts.MapPost("", async (CreatePartRequest req, HttpContext ctx, PartService svc, CancellationToken ct) =>
        {
            var markings = req.Markings.ToMarkings();
            if (!markings.IsSuccess) return Outcome<PartDetail>.Invalid(markings.Errors).ToHttp();
            var cmd = new CreatePartCommand(req.Name, req.PartType, req.DefaultUnit, req.Source, markings.Value, req.Description, req.Attributes, req.Number);
            return (await svc.CreateAsync(SubjectFactory.Require(ctx), cmd, CorrelationId.Get(ctx), ct))
                .ToHttp(d => Results.Created($"/api/v1/parts/{Uri.EscapeDataString(d.Number)}", d));
        });

        parts.MapGet("/{number}", async (string number, string? revision, HttpContext ctx, PartService svc, CancellationToken ct) =>
            (await svc.GetAsync(SubjectFactory.Require(ctx), number, revision, CorrelationId.Get(ctx), ct)).ToHttp());

        parts.MapPost("/{number}/checkout", async (string number, HttpContext ctx, PartService svc, CancellationToken ct) =>
            (await svc.CheckOutAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());

        parts.MapPut("/{number}/working-copy", async (string number, WorkingCopyRequest req, HttpContext ctx, PartService svc, CancellationToken ct) =>
            (await svc.UpdateWorkingCopyAsync(SubjectFactory.Require(ctx), number, req.Description, req.Attributes, CorrelationId.Get(ctx), ct)).ToHttp());

        parts.MapPost("/{number}/checkin", async (string number, CheckInRequest req, HttpContext ctx, PartService svc, CancellationToken ct) =>
            (await svc.CheckInAsync(SubjectFactory.Require(ctx), number, req.Comment, CorrelationId.Get(ctx), ct)).ToHttp());

        parts.MapPost("/{number}/undo-checkout", async (string number, HttpContext ctx, PartService svc, CancellationToken ct) =>
            (await svc.UndoCheckOutAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());

        parts.MapPost("/{number}/state", async (string number, SetStateRequest req, HttpContext ctx, PartService svc, CancellationToken ct) =>
            (await svc.SetStateAsync(SubjectFactory.Require(ctx), number, req.ToState, req.Reason, req.Override, CorrelationId.Get(ctx), ct)).ToHttp());

        parts.MapPut("/{number}/markings", async (string number, ChangeMarkingsRequest req, HttpContext ctx, PartService svc, CancellationToken ct) =>
        {
            var markings = req.Markings.ToMarkings();
            if (!markings.IsSuccess) return Outcome<PartDetail>.Invalid(markings.Errors).ToHttp();
            return (await svc.ChangeMarkingsAsync(SubjectFactory.Require(ctx), number, markings.Value, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp();
        });

        app.MapGet("/api/v1/metadata/parts", async (NpgsqlDataSource db, IReferenceData refs, CancellationToken ct) =>
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            var attrs = await AttributeValidator.LoadAsync(conn, null, PartService.ObjectType, ct);
            var lifecycle = await Lifecycle.LoadAsync(conn, null, await Lifecycle.CurrentIdAsync(conn, null, PartService.DefaultLifecycle, ct), ct);
            // 2.2: the lists are administrator data; retired attributes are hidden from new input.
            return new PartMetadata(await refs.CodesAsync(RefLists.PartType, ct), await refs.CodesAsync(RefLists.PartSource, ct), await refs.CodesAsync(RefLists.Unit, ct),
                [.. attrs.Where(a => !a.Retired)], lifecycle!);
        }).RequireAuthorization(Policies.AnyPlmRole);
    }
}
