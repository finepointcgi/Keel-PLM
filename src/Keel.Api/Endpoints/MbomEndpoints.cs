using System.Text;
using Keel.Api.Infrastructure;
using Keel.Bom;
using Keel.Core;
using Keel.Workflow;

namespace Keel.Api.Endpoints;

public sealed record CreateMbomRequest(string PartNumber, string Plant, MarkingsDto? Markings = null, string? Description = null, bool ConfirmRaiseMarkings = false);

/// <summary>The EBOM an MBOM is generated from or reconciled against: <c>spec</c> is Latest, LatestReleased, Effectivity (with context and serial, lot or date), Baseline (with <c>baseline</c>) or Iteration (<c>iteration</c> like B.1).</summary>
public sealed record EbomSpecQuery(string? Spec = null, string? Baseline = null, string? Iteration = null, string? Context = null, long? Serial = null, long? Lot = null, string? Date = null)
{
    public (EbomSpec? Spec, string? Error) ToSpec()
    {
        if (string.IsNullOrWhiteSpace(Spec)) return (null, null);
        if (!Enum.TryParse<EbomSpecKind>(Spec, true, out var kind)) return (null, $"Unknown EBOM selection '{Spec}'; use Latest, LatestReleased, Effectivity, Baseline or Iteration.");
        return (new EbomSpec(kind, new UnitQuery(null, Context, Serial, Lot, Date).ToRequest(), Baseline, Iteration), null);
    }
}

public sealed record GenerateMbomRequest(string PartNumber, string Plant, EbomSpecQuery? Ebom = null, bool MultiLevel = false, MarkingsDto? Markings = null, bool ConfirmRaiseMarkings = false);
public sealed record ApplyRegenerationRequest(EbomSpecQuery? Ebom = null, string[]? Accept = null);
public sealed record MbomReasonRequest(string Reason);
public sealed record MbomCheckInRequest(string? Comment);
public sealed record MbomTransitionRequest(string To, string? Reason);
public sealed record MbomMarkingsRequest(MarkingsDto Markings, string Reason);
public sealed record MbomLineRequest(
    string ChildNumber, decimal Quantity, string? Unit = null, int? FindNumber = null, string[]? ReferenceDesignators = null, decimal? ScrapFactor = null, bool Phantom = false,
    string? SupplyType = null, string? OperationReference = null, bool MbomOnly = false, string? Notes = null, EffectivityInput? Effectivity = null);
public sealed record MbomLineUpdateRequest(
    decimal Quantity, string Unit, int FindNumber, string[]? ReferenceDesignators = null, decimal? ScrapFactor = null, bool Phantom = false, string? SupplyType = null,
    string? OperationReference = null, bool MbomOnly = false, string? Notes = null, EffectivityInput? Effectivity = null, bool ClearEffectivity = false);
public sealed record MbomLinksRequest(LinkInput[] Links);
public sealed record SavePlantRequest(string Name, bool IsActive = true, bool ReleaseAllowsWarnings = true, int SortOrder = 100);
public sealed record SaveSupplyTypeRequest(string Label, bool IsActive = true, int SortOrder = 100, string? Reason = null);

/// <summary>REST for manufacturing BOMs (ADR-0024). Thin: every rule lives in <see cref="MbomService"/>.</summary>
public static class MbomEndpoints
{
    public static void MapMbomEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/mboms").RequireAuthorization(Policies.AnyPlmRole);

        // ---- lists and search (before the {number} routes)
        g.MapGet("", async (string? q, string? plant, string? state, string? part, string? sort, string? dir, int? limit, int? offset, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            !MbomService.SortKeys.TryResolve(sort, dir, out var spec, out var error) ? (IResult)OutcomeResults.BadSort(error!)
            : (await svc.SearchAsync(SubjectFactory.Require(ctx), new MbomSearch(q, plant, state, part, limit ?? 50, offset ?? 0, spec), CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/plants", async (bool? all, MbomService svc, CancellationToken ct) => Results.Ok(await svc.ListPlantsAsync(all == true, ct)));
        g.MapGet("/supply-types", async (bool? all, MbomService svc, CancellationToken ct) => Results.Ok(await svc.ListSupplyTypesAsync(all == true, ct)));
        g.MapPut("/plants/{code}", async (string code, SavePlantRequest req, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.SavePlantAsync(SubjectFactory.Require(ctx), new SavePlantCommand(code, req.Name, req.IsActive, req.ReleaseAllowsWarnings, req.SortOrder), CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPut("/supply-types/{code}", async (string code, SaveSupplyTypeRequest req, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.SaveSupplyTypeAsync(SubjectFactory.Require(ctx), new SaveSupplyTypeCommand(code, req.Label, req.IsActive, req.SortOrder, req.Reason), CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/where-used/{partNumber}", async (string partNumber, string? plant, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.WhereUsedAsync(SubjectFactory.Require(ctx), partNumber, plant, CorrelationId.Get(ctx), ct)).ToHttp());

        // ---- create and generate
        g.MapPost("", async (CreateMbomRequest req, HttpContext ctx, MbomService svc, CancellationToken ct) =>
        {
            Markings? markings = null;
            if (req.Markings is not null)
            {
                var parsed = req.Markings.ToMarkings();
                if (!parsed.IsSuccess) return Outcome<MbomDetail>.Invalid(parsed.Errors).ToHttp();
                markings = parsed.Value;
            }
            return (await svc.CreateAsync(SubjectFactory.Require(ctx), new CreateMbomCommand(req.PartNumber, req.Plant, markings, req.Description, req.ConfirmRaiseMarkings), CorrelationId.Get(ctx), ct))
                .ToHttp(d => Results.Created($"/api/v1/mboms/{d.Summary.Number}", d));
        });
        g.MapPost("/generate", async (GenerateMbomRequest req, HttpContext ctx, MbomService svc, CancellationToken ct) =>
        {
            Markings? markings = null;
            if (req.Markings is not null)
            {
                var parsed = req.Markings.ToMarkings();
                if (!parsed.IsSuccess) return Outcome<GenerateResult>.Invalid(parsed.Errors).ToHttp();
                markings = parsed.Value;
            }
            var (spec, error) = (req.Ebom ?? new EbomSpecQuery()).ToSpec();
            if (error is not null) return Outcome<GenerateResult>.Invalid([error]).ToHttp();
            return (await svc.GenerateAsync(SubjectFactory.Require(ctx), new GenerateMbomCommand(req.PartNumber, req.Plant, spec, req.MultiLevel, markings, req.ConfirmRaiseMarkings), CorrelationId.Get(ctx), ct))
                .ToHttp(r => Results.Created($"/api/v1/mboms/{r.Items[0].MbomNumber}", r));
        });

        // ---- one MBOM
        g.MapGet("/{number}", async (string number, string? revision, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.GetAsync(SubjectFactory.Require(ctx), number, revision, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/{number}/lines", async (string number, string? revision, int? iteration, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.GetLinesAsync(SubjectFactory.Require(ctx), number, revision, iteration, CorrelationId.Get(ctx), ct)).ToHttp());

        g.MapPost("/{number}/checkout", async (string number, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.CheckOutAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/{number}/checkin", async (string number, MbomCheckInRequest? req, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.CheckInAsync(SubjectFactory.Require(ctx), number, req?.Comment, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/{number}/undo-checkout", async (string number, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.UndoCheckOutAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/{number}/transition", async (string number, MbomTransitionRequest req, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.TransitionAsync(SubjectFactory.Require(ctx), number, req.To, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/{number}/revise", async (string number, MbomReasonRequest req, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.ReviseAsync(SubjectFactory.Require(ctx), number, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPut("/{number}/markings", async (string number, MbomMarkingsRequest req, HttpContext ctx, MbomService svc, CancellationToken ct) =>
        {
            var markings = req.Markings.ToMarkings();
            if (!markings.IsSuccess) return Outcome<MbomDetail>.Invalid(markings.Errors).ToHttp();
            return (await svc.ChangeMarkingsAsync(SubjectFactory.Require(ctx), number, markings.Value, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp();
        });

        // ---- working copy
        var working = g.MapGroup("/{number}/working-copy");
        working.MapGet("", async (string number, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.GetWorkingAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        working.MapPost("/lines", async (string number, MbomLineRequest req, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.AddLineAsync(SubjectFactory.Require(ctx), number, new AddMbomLineCommand(req.ChildNumber, req.Quantity, req.Unit, req.FindNumber, req.ReferenceDesignators, req.ScrapFactor,
                req.Phantom, req.SupplyType, req.OperationReference, req.MbomOnly, req.Notes, req.Effectivity), CorrelationId.Get(ctx), ct))
                .ToHttp(l => Results.Created($"/api/v1/mboms/{Uri.EscapeDataString(number)}/working-copy/lines/{l.Id}", l)));
        working.MapPut("/lines/{lineId:guid}", async (string number, Guid lineId, MbomLineUpdateRequest req, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.UpdateLineAsync(SubjectFactory.Require(ctx), number, lineId, new UpdateMbomLineCommand(req.Quantity, req.Unit, req.FindNumber, req.ReferenceDesignators, req.ScrapFactor, req.Phantom,
                req.SupplyType, req.OperationReference, req.MbomOnly, req.Notes, req.Effectivity, req.ClearEffectivity), CorrelationId.Get(ctx), ct)).ToHttp());
        working.MapDelete("/lines/{lineId:guid}", async (string number, Guid lineId, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.RemoveLineAsync(SubjectFactory.Require(ctx), number, lineId, CorrelationId.Get(ctx), ct)).ToHttp());
        working.MapPut("/lines/{lineId:guid}/links", async (string number, Guid lineId, MbomLinksRequest req, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.SetLinksAsync(SubjectFactory.Require(ctx), number, lineId, req.Links ?? [], CorrelationId.Get(ctx), ct)).ToHttp());
        working.MapPut("/derivation", async (string number, EbomSpecQuery req, HttpContext ctx, MbomService svc, CancellationToken ct) =>
        {
            var (spec, error) = req.ToSpec();
            if (spec is null) return Outcome<MbomLineView?>.Invalid([error ?? "Choose the EBOM to derive from."]).ToHttp();
            return (await svc.SetDerivationAsync(SubjectFactory.Require(ctx), number, spec, CorrelationId.Get(ctx), ct)).ToHttp(_ => Results.NoContent());
        });
        g.MapGet("/{number}/ebom-lines", async (string number, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.GetEbomLineChoicesAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());

        // ---- regeneration from a changed EBOM
        g.MapGet("/{number}/regenerate", async (string number, [AsParameters] EbomSpecQuery ebom, HttpContext ctx, MbomService svc, CancellationToken ct) =>
        {
            var (spec, error) = ebom.ToSpec();
            return error is not null ? Outcome<RegenerationProposal>.Invalid([error]).ToHttp()
                : (await svc.ProposeRegenerationAsync(SubjectFactory.Require(ctx), number, spec, CorrelationId.Get(ctx), ct)).ToHttp();
        });
        g.MapPost("/{number}/regenerate", async (string number, ApplyRegenerationRequest req, HttpContext ctx, MbomService svc, CancellationToken ct) =>
        {
            var (spec, error) = (req.Ebom ?? new EbomSpecQuery()).ToSpec();
            return error is not null ? Outcome<RegenerationResult>.Invalid([error]).ToHttp()
                : (await svc.ApplyRegenerationAsync(SubjectFactory.Require(ctx), number, spec, req.Accept is null ? null : req.Accept.ToHashSet(StringComparer.Ordinal), CorrelationId.Get(ctx), ct)).ToHttp();
        });

        // ---- reconciliation
        g.MapPost("/{number}/reconcile", async (string number, EbomSpecQuery? ebom, HttpContext ctx, MbomService svc, CancellationToken ct) =>
        {
            var (spec, error) = (ebom ?? new EbomSpecQuery()).ToSpec();
            return error is not null ? Outcome<ReconciliationReport>.Invalid([error]).ToHttp()
                : (await svc.ReconcileAsync(SubjectFactory.Require(ctx), number, spec, CorrelationId.Get(ctx), ct)).ToHttp(r => Results.Created($"/api/v1/mboms/{Uri.EscapeDataString(number)}/reconciliations/{r.Run.Id}", r));
        });
        g.MapGet("/{number}/reconciliations", async (string number, int? limit, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.GetReconciliationHistoryAsync(SubjectFactory.Require(ctx), number, limit ?? 50, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/{number}/reconciliations/latest", async (string number, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.GetReconciliationAsync(SubjectFactory.Require(ctx), number, null, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/{number}/reconciliations/{runId:guid}", async (string number, Guid runId, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.GetReconciliationAsync(SubjectFactory.Require(ctx), number, runId, CorrelationId.Get(ctx), ct)).ToHttp());

        // ---- structure queries
        g.MapGet("/{number}/explosion", async (string number, string? revision, int? iteration, bool? latestChildren, int? depth, string? parentLine, [AsParameters] UnitQuery unit,
            HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.ExplodeAsync(SubjectFactory.Require(ctx), new MbomQuery(number, revision, iteration, latestChildren == true, depth, parentLine, unit.ToRequest()), CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/{number}/explosion.csv", async (string number, string? revision, int? iteration, bool? latestChildren, int? depth, [AsParameters] UnitQuery unit,
            HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.ExportCsvAsync(SubjectFactory.Require(ctx), new MbomQuery(number, revision, iteration, latestChildren == true, depth, null, unit.ToRequest()), CorrelationId.Get(ctx), ct)).ToHttp(Csv));
        g.MapGet("/{number}/summary", async (string number, string? revision, int? iteration, bool? latestChildren, bool? leavesOnly, [AsParameters] UnitQuery unit,
            HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.SummaryAsync(SubjectFactory.Require(ctx), new MbomQuery(number, revision, iteration, latestChildren == true, null, null, unit.ToRequest()), leavesOnly ?? true, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/{number}/compare", async (string number, string from, string to, HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.CompareAsync(SubjectFactory.Require(ctx), number, from, to, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/{number}/side-by-side", async (string number, [AsParameters] EbomSpecQuery ebom, HttpContext ctx, MbomService svc, CancellationToken ct) =>
        {
            var (spec, error) = ebom.ToSpec();
            return error is not null ? Outcome<SideBySide>.Invalid([error]).ToHttp()
                : (await svc.SideBySideAsync(SubjectFactory.Require(ctx), number, spec, CorrelationId.Get(ctx), ct)).ToHttp();
        });

        // ---- review through the workflow service (object type "mbom")
        g.MapPost("/{number}/review", async (string number, StartReviewRequest req, HttpContext ctx, WorkflowService svc, CancellationToken ct) =>
            (await svc.StartAsync(SubjectFactory.Require(ctx), new StartReviewCommand(MbomService.ObjectType, number, req.Participants ?? [], req.TemplateId, req.Comment), CorrelationId.Get(ctx), ct))
                .ToHttp(i => Results.Created($"/api/v1/workflows/{i.Id}", i)));
        g.MapGet("/{number}/workflow", async (string number, HttpContext ctx, WorkflowService svc, CancellationToken ct) =>
            (await svc.StatusForObjectAsync(SubjectFactory.Require(ctx), MbomService.ObjectType, number, CorrelationId.Get(ctx), ct)).ToHttp());

        // The Blazor UI authenticates with its session cookie; a plain link to this route downloads the export within the CSP (like the EBOM export).
        app.MapGet("/ui/mboms/{number}/explosion.csv", async (string number, string? revision, int? iteration, bool? latestChildren, int? depth, [AsParameters] UnitQuery unit,
            HttpContext ctx, MbomService svc, CancellationToken ct) =>
            (await svc.ExportCsvAsync(SubjectFactory.Require(ctx), new MbomQuery(number, revision, iteration, latestChildren == true, depth, null, unit.ToRequest()), CorrelationId.Get(ctx), ct)).ToHttp(Csv))
            .RequireAuthorization(Policies.AnyPlmRole);
    }

    private static IResult Csv(MbomExport export) => Results.File(Encoding.UTF8.GetBytes(export.Csv), "text/csv; charset=utf-8", export.FileName);
}
