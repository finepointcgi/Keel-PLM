using System.Text;
using Keel.Api.Infrastructure;
using Keel.Bom;
using Keel.Core;
using Keel.Change;
using Keel.Workflow;

namespace Keel.Api.Endpoints;

public sealed record CreateBaselineRequest(
    string RootNumber, string Name, string Type, BomConfigSpec? Spec = null, bool ReleasedOnly = false, bool IncludeDocuments = false,
    string? Purpose = null, string? Milestone = null, string? RootRevision = null, EffectivityInput? Unit = null, string? UnitContext = null,
    long? Serial = null, long? Lot = null, string? Date = null, MarkingsDto? Markings = null, bool ConfirmRaiseMarkings = false, bool? RequireSignoff = null);
public sealed record UpdateBaselineRequest(string Name, string? Purpose = null, string? Milestone = null);
public sealed record BaselineReasonRequest(string Reason);
public sealed record SupersedeBaselineRequest(string SupersededBy, string Reason);
public sealed record CreateContextRequest(string Id, string Name, string? Description = null, string? Program = null, EffectivityBlock[]? Blocks = null);
public sealed record SetEffectivityRequest(EffectivityInput? Effectivity);

/// <summary>REST for configuration management (ADR-0021): baselines, effectivity contexts and revision effectivity.</summary>
public static class ConfigurationEndpoints
{
    public static void MapConfigurationEndpoints(this IEndpointRouteBuilder app)
    {
        var baselines = app.MapGroup("/api/v1/baselines").RequireAuthorization(Policies.AnyPlmRole);

        baselines.MapGet("", async (string? q, string? state, string? type, string? root, string? contains, string? sort, string? dir, int? limit, int? offset,
            HttpContext ctx, BaselineService svc, CancellationToken ct) =>
            !BaselineService.SortKeys.TryResolve(sort, dir, out var spec, out var error) ? (IResult)OutcomeResults.BadSort(error!)
            : (await svc.SearchAsync(SubjectFactory.Require(ctx), new BaselineSearch(q, state, type, root, contains, limit ?? 50, offset ?? 0, spec), CorrelationId.Get(ctx), ct)).ToHttp());

        baselines.MapGet("/types", async (BaselineService svc, CancellationToken ct) => Results.Ok(await svc.ListTypesAsync(ct)));

        baselines.MapPost("", async (CreateBaselineRequest req, HttpContext ctx, BaselineService svc, CancellationToken ct) =>
        {
            Markings? markings = null;
            if (req.Markings is not null)
            {
                var parsed = req.Markings.ToMarkings();
                if (!parsed.IsSuccess) return Outcome<BaselineDetail>.Invalid(parsed.Errors).ToHttp();
                markings = parsed.Value;
            }
            var unit = new UnitQuery(null, req.UnitContext, req.Serial, req.Lot, req.Date).ToRequest();
            var spec = req.Spec ?? (unit is null ? BomConfigSpec.LatestReleased : BomConfigSpec.Effectivity);
            return (await svc.CreateAsync(SubjectFactory.Require(ctx), new CreateBaselineCommand(req.RootNumber, req.Name, req.Type, spec, req.ReleasedOnly, req.IncludeDocuments,
                req.Purpose ?? "", req.Milestone, req.RootRevision, unit, markings, req.ConfirmRaiseMarkings, req.RequireSignoff), CorrelationId.Get(ctx), ct))
                .ToHttp(b => Results.Created($"/api/v1/baselines/{b.Summary.Number}", b));
        });

        // Comparisons come before the {number} routes so "compare" is never read as a baseline number.
        baselines.MapGet("/compare", async (string from, string to, HttpContext ctx, BomService svc, CancellationToken ct) =>
            (await svc.CompareBaselinesAsync(SubjectFactory.Require(ctx), from, to, CorrelationId.Get(ctx), ct)).ToHttp());

        baselines.MapGet("/compare.csv", async (string from, string to, HttpContext ctx, BomService svc, CancellationToken ct) =>
            (await svc.CompareBaselinesAsync(SubjectFactory.Require(ctx), from, to, CorrelationId.Get(ctx), ct)).ToHttp(c => Csv(BomService.ExportComparison(c, $"{from}_{to}"))));

        baselines.MapGet("/{number}", async (string number, HttpContext ctx, BaselineService svc, CancellationToken ct) =>
            (await svc.GetAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());

        baselines.MapPut("/{number}", async (string number, UpdateBaselineRequest req, HttpContext ctx, BaselineService svc, CancellationToken ct) =>
            (await svc.UpdateAsync(SubjectFactory.Require(ctx), number, req.Name, req.Purpose ?? "", req.Milestone, CorrelationId.Get(ctx), ct)).ToHttp());

        baselines.MapGet("/{number}/items", async (string number, int? depth, string? parentLine, HttpContext ctx, BomService svc, CancellationToken ct) =>
            (await svc.ExplodeAsync(SubjectFactory.Require(ctx), new BomQuery("", null, null, BomConfigSpec.Baseline, depth, parentLine, number), CorrelationId.Get(ctx), ct)).ToHttp());

        baselines.MapGet("/{number}/items/export.csv", async (string number, int? depth, HttpContext ctx, BomService svc, CancellationToken ct) =>
            (await svc.ExportCsvAsync(SubjectFactory.Require(ctx), new BomQuery("", null, null, BomConfigSpec.Baseline, depth, null, number), CorrelationId.Get(ctx), ct)).ToHttp(Csv));

        baselines.MapGet("/{number}/summary", async (string number, bool? leavesOnly, HttpContext ctx, BomService svc, CancellationToken ct) =>
            (await svc.SummaryAsync(SubjectFactory.Require(ctx), new BomQuery("", null, null, BomConfigSpec.Baseline, null, null, number), leavesOnly ?? true, CorrelationId.Get(ctx), ct)).ToHttp());

        baselines.MapGet("/{number}/documents", async (string number, HttpContext ctx, BaselineService svc, CancellationToken ct) =>
            (await svc.GetDocumentsAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());

        baselines.MapGet("/{number}/verify", async (string number, HttpContext ctx, BaselineService svc, CancellationToken ct) =>
            (await svc.VerifyAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());

        baselines.MapGet("/{number}/compare-current", async (string number, string? spec, HttpContext ctx, BomService svc, CancellationToken ct) =>
            ParseCurrentSpec(spec, out var s) is { } bad ? bad : (await svc.CompareBaselineToCurrentAsync(SubjectFactory.Require(ctx), number, s, CorrelationId.Get(ctx), ct)).ToHttp());

        baselines.MapGet("/{number}/compare-current.csv", async (string number, string? spec, HttpContext ctx, BomService svc, CancellationToken ct) =>
            ParseCurrentSpec(spec, out var s) is { } bad ? bad
                : (await svc.CompareBaselineToCurrentAsync(SubjectFactory.Require(ctx), number, s, CorrelationId.Get(ctx), ct)).ToHttp(c => Csv(BomService.ExportComparison(c, $"{number}_{s}"))));

        baselines.MapPost("/{number}/establish", async (string number, BaselineReasonRequest req, HttpContext ctx, BaselineService svc, CancellationToken ct) =>
            (await svc.EstablishAsync(SubjectFactory.Require(ctx), number, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp());

        baselines.MapPost("/{number}/supersede", async (string number, SupersedeBaselineRequest req, HttpContext ctx, BaselineService svc, CancellationToken ct) =>
            (await svc.SupersedeAsync(SubjectFactory.Require(ctx), number, req.SupersededBy, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp());

        baselines.MapPost("/{number}/archive", async (string number, BaselineReasonRequest req, HttpContext ctx, BaselineService svc, CancellationToken ct) =>
            (await svc.ArchiveAsync(SubjectFactory.Require(ctx), number, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp());

        // Establishment with e-signatures is the workflow engine's, bound to object type "baseline" (step-up applies to every signature).
        baselines.MapPost("/{number}/review", async (string number, StartReviewRequest req, HttpContext ctx, WorkflowService flow, CancellationToken ct) =>
            (await flow.StartAsync(SubjectFactory.Require(ctx), new StartReviewCommand(BaselineService.ObjectType, number, req.Participants ?? [], req.TemplateId, req.Comment), CorrelationId.Get(ctx), ct))
                .ToHttp(i => Results.Created($"/api/v1/workflows/{i.Id}", i)));

        baselines.MapGet("/{number}/workflow", async (string number, HttpContext ctx, WorkflowService flow, CancellationToken ct) =>
            (await flow.StatusForObjectAsync(SubjectFactory.Require(ctx), BaselineService.ObjectType, number, CorrelationId.Get(ctx), ct)).ToHttp());

        // ---- effectivity
        var eff = app.MapGroup("/api/v1/effectivity").RequireAuthorization(Policies.AnyPlmRole);
        eff.MapGet("/contexts", async (HttpContext ctx, EffectivityService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListContextsAsync(SubjectFactory.Require(ctx), CorrelationId.Get(ctx), ct)));
        eff.MapGet("/contexts/{id}", async (string id, HttpContext ctx, EffectivityService svc, CancellationToken ct) =>
            (await svc.GetContextAsync(SubjectFactory.Require(ctx), id, CorrelationId.Get(ctx), ct)).ToHttp());
        eff.MapPost("/contexts", async (CreateContextRequest req, HttpContext ctx, EffectivityService svc, CancellationToken ct) =>
            (await svc.CreateContextAsync(SubjectFactory.Require(ctx), new CreateContextCommand(req.Id, req.Name, req.Description ?? "", req.Program, req.Blocks), CorrelationId.Get(ctx), ct))
                .ToHttp(c => Results.Created($"/api/v1/effectivity/contexts/{c.Id}", c)));

        app.MapGet("/api/v1/parts/{number}/effectivity", async (string number, HttpContext ctx, EffectivityService svc, CancellationToken ct) =>
            (await svc.GetPartAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp()).RequireAuthorization(Policies.AnyPlmRole);

        // Compare what differs between two units of one part: ?context=ORCA-POD&serial=100&serialTo=101 (or lot/date, or a different dimension per side).
        app.MapGet("/api/v1/parts/{number}/bom/compare-units", async (string number, string? context, long? serial, long? lot, string? date,
            long? serialTo, long? lotTo, string? dateTo, string? contextTo, int? depth, HttpContext ctx, BomService svc, CancellationToken ct) =>
            (await svc.CompareUnitsAsync(SubjectFactory.Require(ctx), number, new UnitQuery(null, context, serial, lot, date).ToRequest() ?? new EffectivityRequest(null),
                new UnitQuery(null, contextTo ?? context, serialTo, lotTo, dateTo).ToRequest() ?? new EffectivityRequest(null), depth, CorrelationId.Get(ctx), ct)).ToHttp())
            .RequireAuthorization(Policies.AnyPlmRole);

        app.MapGet("/api/v1/parts/{number}/baselines", async (string number, HttpContext ctx, BaselineService svc, CancellationToken ct) =>
            (await svc.SearchAsync(SubjectFactory.Require(ctx), new BaselineSearch(RootNumber: number), CorrelationId.Get(ctx), ct)).ToHttp()).RequireAuthorization(Policies.AnyPlmRole);

        app.MapPut("/api/v1/changes/{number}/affected/{id:guid}/effectivity", async (string number, Guid id, SetEffectivityRequest req, HttpContext ctx, ChangeService svc, CancellationToken ct) =>
            (await svc.SetAffectedEffectivityAsync(SubjectFactory.Require(ctx), number, id, req.Effectivity, CorrelationId.Get(ctx), ct)).ToHttp()).RequireAuthorization(Policies.AnyPlmRole);

        // Cookie-authenticated downloads for the Blazor UI (the /api routes accept bearer tokens only; same services, filter and audit).
        app.MapGet("/ui/baselines/{number}/items.csv", async (string number, int? depth, HttpContext ctx, BomService svc, CancellationToken ct) =>
            (await svc.ExportCsvAsync(SubjectFactory.Require(ctx), new BomQuery("", null, null, BomConfigSpec.Baseline, depth, null, number), CorrelationId.Get(ctx), ct)).ToHttp(Csv))
            .RequireAuthorization(Policies.AnyPlmRole);
        app.MapGet("/ui/baselines/compare.csv", async (string from, string? to, string? spec, HttpContext ctx, BomService svc, CancellationToken ct) =>
        {
            var subject = SubjectFactory.Require(ctx);
            if (!string.IsNullOrWhiteSpace(to))
                return (await svc.CompareBaselinesAsync(subject, from, to, CorrelationId.Get(ctx), ct)).ToHttp(c => Csv(BomService.ExportComparison(c, $"{from}_{to}")));
            return ParseCurrentSpec(spec, out var s) is { } bad ? bad
                : (await svc.CompareBaselineToCurrentAsync(subject, from, s, CorrelationId.Get(ctx), ct)).ToHttp(c => Csv(BomService.ExportComparison(c, $"{from}_{s}")));
        }).RequireAuthorization(Policies.AnyPlmRole);
        app.MapGet("/ui/parts/{number}/compare-units.csv", async (string number, string? context, long? serial, long? lot, string? date, string? contextTo, long? serialTo, long? lotTo, string? dateTo,
            HttpContext ctx, BomService svc, CancellationToken ct) =>
            (await svc.CompareUnitsAsync(SubjectFactory.Require(ctx), number, new UnitQuery(null, context, serial, lot, date).ToRequest() ?? new EffectivityRequest(null),
                new UnitQuery(null, string.IsNullOrWhiteSpace(contextTo) ? context : contextTo, serialTo, lotTo, dateTo).ToRequest() ?? new EffectivityRequest(null), null, CorrelationId.Get(ctx), ct))
                .ToHttp(c => Csv(BomService.ExportComparison(c, $"{number}_units"))))
            .RequireAuthorization(Policies.AnyPlmRole);
    }

    private static IResult Csv(BomExport export) => Results.File(Encoding.UTF8.GetBytes(export.Csv), "text/csv; charset=utf-8", export.FileName);

    private static IResult Csv(DeepComparisonExport export) => Results.File(Encoding.UTF8.GetBytes(export.Csv), "text/csv; charset=utf-8", export.FileName);

    private static IResult? ParseCurrentSpec(string? spec, out BomConfigSpec parsed)
    {
        parsed = BomConfigSpec.LatestReleased;
        if (string.IsNullOrWhiteSpace(spec)) return null;
        return Enum.TryParse(spec, ignoreCase: true, out parsed) && parsed is BomConfigSpec.Latest or BomConfigSpec.LatestReleased
            ? null
            : Results.ValidationProblem(new Dictionary<string, string[]> { ["spec"] = ["Compare with Latest or LatestReleased."] });
    }
}
