using Keel.Api.Infrastructure;
using Keel.Api.Infrastructure.Programs;
using Keel.Core;
using Keel.Programs;

namespace Keel.Api.Endpoints;

public sealed record AddPoolRequest(string Name, string? Description, MarkingsDto? Markings);
public sealed record AddRoleRequest(string Pool, string Name, decimal? HourlyRate, DateOnly? RateFrom, MarkingsDto? Markings);
public sealed record SetRateRequest(decimal HourlyRate, DateOnly EffectiveFrom);
public sealed record AddResourceRequest(string Role, string Name, decimal? CapacityHoursPerDay, DateOnly? AvailableFrom, DateOnly? AvailableTo, MarkingsDto? Markings);
public sealed record UpdateResourceRequest(string Name, string Role, decimal CapacityHoursPerDay, DateOnly? AvailableFrom, DateOnly? AvailableTo, bool Active);
public sealed record AddAssignmentRequest(string Item, string Resource, DateOnly Start, DateOnly End, decimal HoursPerDay);
public sealed record UpdateAssignmentRequest(DateOnly Start, DateOnly End, decimal HoursPerDay);
public sealed record SetBudgetRequest(string CostType, DateOnly Period, decimal Amount);
public sealed record AddActualRequest(string CostType, DateOnly IncurredOn, decimal? Amount, decimal? Hours, string? Resource, string? Item, string? Note, string? Reverses);
public sealed record AddMaterialRequest(string Part, decimal Quantity, DateOnly NeededOn, string? Note);

/// <summary>
/// Resources and cost of a program (ADR-0049): pools, roles, resources, assignments, load and over-allocation (hours; any reader of the program), and rates, budget, actual costs,
/// material lines and the cost roll-up (the program cost permission as well). A program the caller cannot read answers 404; a missing permission 403 with the reason code
/// <c>cost.role_required</c>. The export is built from the same reads, so it holds nothing the caller may not see.
/// </summary>
public static class ProgramResourceEndpoints
{
    public const string UiExportRoute = "/ui/programs/{code}/resources-export";

    public static void MapProgramResourceEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/programs").RequireAuthorization(Policies.AnyPlmRole).WithTags("Programs");
        static Subject Who(HttpContext c) => SubjectFactory.Require(c);
        static string? Corr(HttpContext c) => CorrelationId.Get(c);
        static Markings? Marks(MarkingsDto? dto, out IResult? error)
        {
            error = null;
            if (dto is null) return null;
            var m = dto.ToMarkings();
            if (m.IsSuccess) return m.Value;
            error = Outcome<object>.Invalid(m.Errors).ToHttp();
            return null;
        }

        g.MapGet("/{code}/resources", async (string code, HttpContext c, ProgramService svc, CancellationToken ct) => (await svc.GetResourcesAsync(Who(c), code, Corr(c), ct)).ToHttp())
            .WithSummary("Pools, roles, resources and assignments you may read. Rates only with the program cost permission.");
        g.MapPost("/{code}/resource-pools", async (string code, AddPoolRequest r, HttpContext c, ProgramService svc, CancellationToken ct) =>
        {
            var m = Marks(r.Markings, out var bad);
            return bad ?? (await svc.AddPoolAsync(Who(c), code, new AddPoolCommand(r.Name, r.Description, m), Corr(c), ct)).ToHttp(p => Results.Created($"/api/v1/programs/{code}/resources", p));
        });
        g.MapPost("/{code}/resource-roles", async (string code, AddRoleRequest r, HttpContext c, ProgramService svc, CancellationToken ct) =>
        {
            var m = Marks(r.Markings, out var bad);
            return bad ?? (await svc.AddRoleAsync(Who(c), code, new AddRoleCommand(r.Pool, r.Name, r.HourlyRate, r.RateFrom, m), Corr(c), ct)).ToHttp(x => Results.Created($"/api/v1/programs/{code}/resources", x));
        });
        g.MapPut("/{code}/resource-roles/{role}/rate", async (string code, string role, SetRateRequest r, HttpContext c, ProgramService svc, CancellationToken ct) =>
            (await svc.SetRoleRateAsync(Who(c), code, role, new SetRoleRateCommand(r.HourlyRate, r.EffectiveFrom), Corr(c), ct)).ToHttp())
            .WithSummary("Sets the hourly rate of a role from a day on. Needs the program cost permission. Earlier rates stay.");
        g.MapPost("/{code}/resources", async (string code, AddResourceRequest r, HttpContext c, ProgramService svc, CancellationToken ct) =>
        {
            var m = Marks(r.Markings, out var bad);
            return bad ?? (await svc.AddResourceAsync(Who(c), code, new AddResourceCommand(r.Role, r.Name, r.CapacityHoursPerDay ?? 8m, r.AvailableFrom, r.AvailableTo, m), Corr(c), ct)).ToHttp(x => Results.Created($"/api/v1/programs/{code}/resources", x));
        });
        g.MapPut("/{code}/resources/{key}", async (string code, string key, UpdateResourceRequest r, HttpContext c, ProgramService svc, CancellationToken ct) =>
            (await svc.UpdateResourceAsync(Who(c), code, key, new UpdateResourceCommand(r.Name, r.Role, r.CapacityHoursPerDay, r.AvailableFrom, r.AvailableTo, r.Active), Corr(c), ct)).ToHttp());
        g.MapPost("/{code}/assignments", async (string code, AddAssignmentRequest r, HttpContext c, ProgramService svc, CancellationToken ct) =>
            (await svc.AddAssignmentAsync(Who(c), code, new AddAssignmentCommand(r.Item, r.Resource, r.Start, r.End, r.HoursPerDay), Corr(c), ct)).ToHttp(x => Results.Created($"/api/v1/programs/{code}/resources", x)));
        g.MapPut("/{code}/assignments/{key}", async (string code, string key, UpdateAssignmentRequest r, HttpContext c, ProgramService svc, CancellationToken ct) =>
            (await svc.UpdateAssignmentAsync(Who(c), code, key, new UpdateAssignmentCommand(r.Start, r.End, r.HoursPerDay), Corr(c), ct)).ToHttp());
        g.MapDelete("/{code}/assignments/{key}", async (string code, string key, HttpContext c, ProgramService svc, CancellationToken ct) => (await svc.RemoveAssignmentAsync(Who(c), code, key, Corr(c), ct)).ToHttp());
        g.MapGet("/{code}/load", async (string code, DateOnly? from, DateOnly? to, HttpContext c, ProgramService svc, CancellationToken ct) => (await svc.GetLoadAsync(Who(c), code, from, to, Corr(c), ct)).ToHttp())
            .WithSummary("Allocated hours against capacity by resource and week, and the over-allocation periods (hours only).");

        g.MapGet("/{code}/cost", async (string code, DateOnly? asOf, HttpContext c, ProgramService svc, CancellationToken ct) => (await svc.GetCostAsync(Who(c), code, asOf, Corr(c), ct)).ToHttp())
            .WithSummary("Budget against actual against estimate to complete, by month and cost type, with the material estimate. Needs the program cost permission.");
        g.MapPut("/{code}/budget", async (string code, SetBudgetRequest r, HttpContext c, ProgramService svc, CancellationToken ct) =>
            (await svc.SetBudgetAsync(Who(c), code, new SetBudgetCommand(r.CostType, r.Period, r.Amount), Corr(c), ct)).ToHttp())
            .WithSummary("Sets the budget of a cost type for the month of the given day. Needs the program cost permission. Changes are logged.");
        g.MapPost("/{code}/actuals", async (string code, AddActualRequest r, HttpContext c, ProgramService svc, CancellationToken ct) =>
            (await svc.AddActualAsync(Who(c), code, new AddActualCommand(r.CostType, r.IncurredOn, r.Amount, r.Hours, r.Resource, r.Item, r.Note, r.Reverses), Corr(c), ct)).ToHttp(x => Results.Created($"/api/v1/programs/{code}/cost", x)))
            .WithSummary("Records an actual cost (labor from hours and a resource, other costs by amount, or a reversal). Recorded costs are never changed. Needs the program cost permission.");
        g.MapPost("/{code}/material", async (string code, AddMaterialRequest r, HttpContext c, ProgramService svc, CancellationToken ct) =>
            (await svc.AddMaterialLineAsync(Who(c), code, new AddMaterialLineCommand(r.Part, r.Quantity, r.NeededOn, r.Note), Corr(c), ct)).ToHttp(x => Results.Created($"/api/v1/programs/{code}/cost", x)));
        g.MapDelete("/{code}/material/{key}", async (string code, string key, HttpContext c, ProgramService svc, CancellationToken ct) => (await svc.RemoveMaterialLineAsync(Who(c), code, key, Corr(c), ct)).ToHttp());

        g.MapGet("/{code}/resources-export", Export).WithSummary("Excel (all tables) or CSV (one table) of the resources, load and, with the cost permission, cost of a program. The banner is on the file.");
        app.MapGet(UiExportRoute, Export).RequireAuthorization(Policies.AnyPlmRole).ExcludeFromDescription();
    }

    private static async Task<IResult> Export(string code, string? format, string? table, DateOnly? asOf, HttpContext c, ProgramService svc, CancellationToken ct)
    {
        var subject = SubjectFactory.Require(c);
        var corr = CorrelationId.Get(c);
        format = (format ?? "xlsx").ToLowerInvariant();
        if (format is not ("xlsx" or "csv")) return Outcome<object>.Invalid(["The format must be xlsx or csv."]).ToHttp();
        if (table is not null && !ProgramCostExport.Tables.Contains(table)) return Outcome<object>.Invalid([$"The table must be one of: {string.Join(", ", ProgramCostExport.Tables)}."]).ToHttp();
        var catalog = await svc.GetResourcesAsync(subject, code, corr, ct);
        if (!catalog.IsOk) return catalog.ToHttp();
        var load = await svc.GetLoadAsync(subject, code, null, null, corr, ct);
        if (!load.IsOk) return load.ToHttp();
        CostView? cost = null;
        if (ProgramService.CanViewCost(subject))
        {
            var o = await svc.GetCostAsync(subject, code, asOf, corr, ct);
            if (!o.IsOk) return o.ToHttp();
            cost = o.Value;
        }
        else if (table is "summary" or "months" or "roles" or "budget" or "actuals" or "material") return Outcome<object>.Forbidden([ProgramService.CostRoleMissing]).ToHttp();
        var built = ProgramCostExport.Build(catalog.Value!.Code, catalog.Value, load.Value!, cost, format, table, asOf ?? cost?.AsOf ?? DateOnly.FromDateTime(DateTime.UtcNow));
        await svc.RecordCostExportAsync(subject, catalog.Value.Code, format, built.Rows, corr, ct);
        c.Response.Headers["X-Keel-Markings"] = Uri.EscapeDataString(cost?.Banner ?? catalog.Value.Banner);
        return Results.File(built.Content, built.ContentType, built.FileName);
    }
}
