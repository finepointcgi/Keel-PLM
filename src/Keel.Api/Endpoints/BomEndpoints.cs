using System.Text;
using Keel.Api.Infrastructure;
using Keel.Bom;
using Keel.Core;

namespace Keel.Api.Endpoints;

public sealed record AddUsageRequest(string ChildNumber, decimal Quantity, string? Unit = null, int? FindNumber = null,
    string[]? ReferenceDesignators = null, string? Notes = null, EffectivityInput? Effectivity = null);
public sealed record UpdateUsageRequest(decimal Quantity, string Unit, int FindNumber, string[]? ReferenceDesignators = null, string? Notes = null,
    EffectivityInput? Effectivity = null, bool ClearEffectivity = false);

/// <summary>
/// The unit and the baseline a structure request may name (2.1): <c>spec=Effectivity</c> with <c>context</c> and one or more of <c>serial</c>, <c>lot</c>,
/// <c>date</c> (yyyy-MM-dd, UTC); <c>spec=Baseline</c> with <c>baseline</c>.
/// </summary>
public sealed record UnitQuery(string? Baseline = null, string? Context = null, long? Serial = null, long? Lot = null, string? Date = null)
{
    public EffectivityRequest? ToRequest()
    {
        if (Serial is null && Lot is null && string.IsNullOrWhiteSpace(Date)) return null;
        long? day = null;
        if (!string.IsNullOrWhiteSpace(Date))
            day = DateOnly.TryParseExact(Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? EffectivityRange.DayNumber(d) : -1;
        return new EffectivityRequest(string.IsNullOrWhiteSpace(Context) ? null : Context.Trim().ToUpperInvariant(), Serial, Lot, day);
    }
}
public sealed record RenumberRequest(int Start = 10, int Step = 10);
public sealed record CopyStructureRequest(string SourceNumber, bool Replace = false);

public static class BomEndpoints
{
    public static void MapBomEndpoints(this IEndpointRouteBuilder app)
    {
        var parts = app.MapGroup("/api/v1/parts").RequireAuthorization(Policies.AnyPlmRole);

        parts.MapGet("/{number}/bom", async (string number, string? revision, int? iteration, string? spec, int? depth, string? parentLine,
            [AsParameters] UnitQuery unit, HttpContext ctx, BomService svc, CancellationToken ct) =>
            await Run(spec, s => svc.ExplodeAsync(SubjectFactory.Require(ctx), new BomQuery(number, revision, iteration, s, depth, parentLine, unit.Baseline, unit.ToRequest()), CorrelationId.Get(ctx), ct)));

        parts.MapGet("/{number}/bom/export.csv", async (string number, string? revision, int? iteration, string? spec, int? depth,
            [AsParameters] UnitQuery unit, HttpContext ctx, BomService svc, CancellationToken ct) =>
            await Run(spec, async s => (await svc.ExportCsvAsync(SubjectFactory.Require(ctx), new BomQuery(number, revision, iteration, s, depth, null, unit.Baseline, unit.ToRequest()), CorrelationId.Get(ctx), ct)),
                csv => Csv(csv)));

        parts.MapGet("/{number}/bom/summary", async (string number, string? revision, int? iteration, string? spec, int? depth, bool? leavesOnly,
            [AsParameters] UnitQuery unit, HttpContext ctx, BomService svc, CancellationToken ct) =>
            await Run(spec, s => svc.SummaryAsync(SubjectFactory.Require(ctx), new BomQuery(number, revision, iteration, s, depth, null, unit.Baseline, unit.ToRequest()), leavesOnly ?? true, CorrelationId.Get(ctx), ct)));

        parts.MapGet("/{number}/bom/compare", async (string number, string from, string to, HttpContext ctx, BomService svc, CancellationToken ct) =>
            (await svc.CompareAsync(SubjectFactory.Require(ctx), number, from, to, CorrelationId.Get(ctx), ct)).ToHttp());

        parts.MapGet("/{number}/where-used", async (string number, int? levels, [AsParameters] UnitQuery unit, HttpContext ctx, BomService svc, CancellationToken ct) =>
            (await svc.WhereUsedAsync(SubjectFactory.Require(ctx), number, levels ?? 1, unit.ToRequest(), CorrelationId.Get(ctx), ct)).ToHttp());

        // Working copy: only the user holding the part's check-out may read or edit it.
        var working = parts.MapGroup("/{number}/working-copy/bom");
        working.MapGet("", async (string number, HttpContext ctx, BomService svc, CancellationToken ct) =>
            (await svc.GetWorkingAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());

        working.MapPost("", async (string number, AddUsageRequest req, HttpContext ctx, BomService svc, CancellationToken ct) =>
            (await svc.AddUsageAsync(SubjectFactory.Require(ctx), number,
                new AddUsageCommand(req.ChildNumber, req.Quantity, req.Unit, req.FindNumber, req.ReferenceDesignators, req.Notes, req.Effectivity), CorrelationId.Get(ctx), ct))
            .ToHttp(l => Results.Created($"/api/v1/parts/{Uri.EscapeDataString(number)}/working-copy/bom/{l.Id}", l)));

        working.MapPut("/{usageId:guid}", async (string number, Guid usageId, UpdateUsageRequest req, HttpContext ctx, BomService svc, CancellationToken ct) =>
            (await svc.UpdateUsageAsync(SubjectFactory.Require(ctx), number, usageId,
                new UpdateUsageCommand(req.Quantity, req.Unit, req.FindNumber, req.ReferenceDesignators, req.Notes, req.Effectivity, req.ClearEffectivity), CorrelationId.Get(ctx), ct)).ToHttp());

        working.MapDelete("/{usageId:guid}", async (string number, Guid usageId, HttpContext ctx, BomService svc, CancellationToken ct) =>
            (await svc.RemoveUsageAsync(SubjectFactory.Require(ctx), number, usageId, CorrelationId.Get(ctx), ct)).ToHttp());

        working.MapPost("/renumber", async (string number, RenumberRequest? req, HttpContext ctx, BomService svc, CancellationToken ct) =>
            (await svc.RenumberAsync(SubjectFactory.Require(ctx), number, req?.Start ?? 10, req?.Step ?? 10, CorrelationId.Get(ctx), ct)).ToHttp());

        working.MapPost("/copy-from", async (string number, CopyStructureRequest req, HttpContext ctx, BomService svc, CancellationToken ct) =>
            (await svc.CopyStructureAsync(SubjectFactory.Require(ctx), number, req.SourceNumber, req.Replace, CorrelationId.Get(ctx), ct)).ToHttp());

        // The Blazor UI authenticates with its session cookie, which the /api/* routes (JWT bearer only) do not accept. A plain
        // link to this route downloads the export without JavaScript and within the CSP (default-src 'self'); same service, filter and audit.
        app.MapGet("/ui/parts/{number}/bom.csv", async (string number, string? revision, int? iteration, string? spec, int? depth,
            [AsParameters] UnitQuery unit, HttpContext ctx, BomService svc, CancellationToken ct) =>
            await Run(spec, async s => (await svc.ExportCsvAsync(SubjectFactory.Require(ctx), new BomQuery(number, revision, iteration, s, depth, null, unit.Baseline, unit.ToRequest()), CorrelationId.Get(ctx), ct)),
                csv => Csv(csv)))
            .RequireAuthorization(Policies.AnyPlmRole);
    }

    private static IResult Csv(BomExport export) =>
        Results.File(Encoding.UTF8.GetBytes(export.Csv), "text/csv; charset=utf-8", export.FileName);

    /// <summary>Parses the config spec and maps the outcome. NotSupportedException (a capability a host did not register) answers 501.</summary>
    private static async Task<IResult> Run<T>(string? spec, Func<BomConfigSpec, Task<Outcome<T>>> call, Func<T, IResult>? ok = null)
    {
        BomConfigSpec parsed = BomConfigSpec.Latest;
        if (!string.IsNullOrWhiteSpace(spec) && !Enum.TryParse(spec, ignoreCase: true, out parsed))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["spec"] = [$"Unknown spec '{spec}'; use Latest, LatestReleased, Effectivity or Baseline."] });
        try
        {
            return (await call(parsed)).ToHttp(ok);
        }
        catch (NotSupportedException e)
        {
            return Results.Problem(statusCode: StatusCodes.Status501NotImplemented, title: "Not implemented", detail: e.Message);
        }
    }
}
