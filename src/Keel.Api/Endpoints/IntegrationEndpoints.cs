using System.Text.Json.Nodes;
using Keel.Api.Infrastructure;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Integration;
using Microsoft.AspNetCore.Antiforgery;

namespace Keel.Api.Endpoints;

public sealed record TdpApiRequest(string ObjectType, string Number, string? Revision, string RecipientProfile);
public sealed record RequeueRequest(Guid? DeliveryId, string? Sink);
public sealed record CategoryRuleRequest(string DocumentType, string? LinkType, string Category);

/// <summary>One line of the released-EBOM snapshot of the pull API. A line the caller may not read carries find number, quantity and unit only (A4).</summary>
public sealed record BomSnapshotLine(
    int Level, string Path, int FindNumber, decimal Quantity, decimal ExtendedQuantity, string Unit, bool Restricted, bool Unresolved, string? Number, string? Name,
    string? PartType, string? Revision, string? Iteration, string? State, IReadOnlyList<string> ReferenceDesignators, string? Notes, string? Effectivity, string? Banner);

public sealed record BomSnapshot(string Number, string Name, string Revision, string Iteration, string Spec, string? Configuration, string Banner, int RestrictedLines, int UnresolvedLines,
    IReadOnlyList<BomSnapshotLine> Lines);

/// <summary>One line of the released-MBOM snapshot of the pull API. A line the caller may not read carries find number, quantity and unit only (A4).</summary>
public sealed record MbomSnapshotLine(
    int FindNumber, int Alternate, bool Restricted, decimal Quantity, string Unit, string? Number, string? Name, string? Source, decimal? ScrapFactor, bool Phantom, string? SupplyType,
    string? OperationReference, bool MbomOnly, IReadOnlyList<string> ReferenceDesignators, string? Notes, string? Effectivity, IReadOnlyList<MbomSnapshotLink> EbomLinks, string? ChildMbom, string? ChildMbomRevision);

public sealed record MbomSnapshotLink(int EbomFindNumber, string Status, string? Reason);

public sealed record MbomSnapshot(
    string MbomNumber, string PartNumber, string PartName, string Plant, string Revision, string Iteration, string State, string? DerivedFromEbom, string ContentSha256,
    Guid? ReconciliationRunId, string? ReconciliationStatus, string Banner, int RestrictedLines, IReadOnlyList<MbomSnapshotLine> Lines);

public sealed record ChangeSnapshotAffected(string ObjectType, string? Number, string? Name, string? Revision, string Action, string? ResultingRevision, string? Effectivity, bool Restricted);

public sealed record ChangeSnapshotDto(string Type, string Number, string Title, string State, string? ChangeClass, string Priority, string Reason, string Banner, string? SourceChange,
    IReadOnlyList<ChangeSnapshotAffected> Affected);

/// <summary>
/// The ERP-facing API (ADR-0025): the pull API for reconciliation and backfill (service accounts with plm-integration; their own markings decide what they
/// see), Technical Data Package export, and the administration of sinks and profiles. Documented in docs/integration and /openapi/v1.json.
/// </summary>
public static class IntegrationEndpoints
{
    public const string UiTdpRoute = "/ui/tdp/export";

    public static void MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        static Subject Who(HttpContext c) => SubjectFactory.Require(c);
        static string? Corr(HttpContext c) => CorrelationId.Get(c);

        // ---- pull API for service accounts
        var pull = app.MapGroup("/api/v1/integration").RequireAuthorization(Policies.Integration).WithTags("Integration pull API");

        pull.MapGet("/events", async (string? cursor, int? limit, string? type, string? aggregate, HttpContext c, OutboxQueries q, CancellationToken ct) =>
            (await q.PullAsync(Who(c), cursor, limit ?? 100, type, aggregate, Corr(c), ct)).ToHttp())
            .WithSummary("Events in commit order after a cursor, limited to what the caller may read.");

        pull.MapGet("/boms/{number}", async (string number, string? revision, string? spec, string? baseline, int? depth, HttpContext c, BomService bom, CancellationToken ct) =>
        {
            var parsed = BomConfigSpec.LatestReleased;
            if (!string.IsNullOrWhiteSpace(spec) && !Enum.TryParse(spec, ignoreCase: true, out parsed) || parsed == BomConfigSpec.Effectivity)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["spec"] = ["Use LatestReleased (default), Latest or Baseline."] });
            var o = await bom.ExplodeAsync(Who(c), new BomQuery(number, revision, null, parsed, depth ?? 1, null, baseline), Corr(c), ct);
            return o.ToHttp(e => Results.Ok(new BomSnapshot(e.RootNumber, e.RootName, e.RootRevision, $"{e.RootRevision}.{e.RootIteration}", e.Spec.ToString(), e.Configuration, e.Banner,
                e.RedactedCount, e.UnresolvedCount,
                [.. e.Lines.Select(l => new BomSnapshotLine(l.Level, l.Path, l.FindNumber, l.Quantity, l.ExtendedQuantity, l.Unit, l.Redacted, l.Unresolved, l.Number, l.Name, l.PartType, l.Revision,
                    l.Revision is null ? null : $"{l.Revision}.{l.Iteration}", l.StateLabel, l.ReferenceDesignators, l.Notes, l.EffectivityText, l.Banner))])));
        }).WithSummary("Released EBOM snapshot of an assembly revision (children resolved at the requested configuration spec).");

        pull.MapGet("/mboms/{number}", async (string number, string? plant, string? revision, HttpContext c, IManufacturingBomSource mboms, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(plant))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["plant"] = ["Name the plant: an MBOM exists per part and plant."] });
            var o = await mboms.GetReleasedAsync(Who(c), number, plant, string.IsNullOrWhiteSpace(revision) ? null : revision, Corr(c), ct);
            return o.ToHttp(m => Results.Ok(new MbomSnapshot(m.MbomNumber, m.PartNumber, m.PartName, m.Plant, m.Revision, $"{m.Revision}.{m.Iteration}", m.State, m.DerivedFromEbom, m.ContentSha256,
                m.ReconciliationRunId, m.ReconciliationStatus, m.Banner, m.Lines.Count(l => l.Redacted),
                [.. m.Lines.Select(l => new MbomSnapshotLine(l.FindNumber, l.Alt, l.Redacted, l.Quantity, l.Unit, l.ChildNumber, l.ChildName, l.ChildSource, l.ScrapFactor, l.Phantom, l.SupplyType,
                    l.OperationReference, l.MbomOnly, l.ReferenceDesignators, l.Notes, l.Effectivity?.Describe(), [.. l.EbomLinks.Select(k => new MbomSnapshotLink(k.EbomFindNumber, k.Status, k.Reason))],
                    l.ChildMbomNumber, l.ChildMbomRevision))])));
        }).WithSummary("Latest released MBOM of a part in a plant (or the named revision), children resolved to the plant's released MBOMs. Lines you may not read are placeholders.");

        pull.MapGet("/changes/{number}", async (string number, HttpContext c, ChangeService changes, CancellationToken ct) =>
            (await changes.GetAsync(Who(c), number, Corr(c), ct)).ToHttp(d => Results.Ok(new ChangeSnapshotDto(d.Type, d.Number, d.Title, d.StateLabel, d.ChangeClass, d.Priority, d.Reason, d.Banner, d.Source?.Number,
                [.. d.Affected.Select(a => new ChangeSnapshotAffected(a.ObjectType, a.Key, a.Name, a.Revision, a.Action, a.ResultingRevision, a.Effectivity, a.Redacted))]))))
            .WithSummary("A change request or notice with its affected and resulting revisions and effectivity.");

        // ---- Technical Data Packages
        var tdp = app.MapGroup("/api/v1/tdp").RequireAuthorization(Policies.AnyPlmRole).WithTags("Technical Data Package");

        tdp.MapPost("/preview", async (TdpApiRequest req, HttpContext c, TdpService svc, CancellationToken ct) =>
            (await svc.PlanAsync(Who(c), new TdpRequest(req.ObjectType, req.Number, req.Revision, req.RecipientProfile), Corr(c), ct)).ToHttp())
            .WithSummary("What a package would contain, and why it would be refused (counts only).");

        tdp.MapPost("", async (TdpApiRequest req, HttpContext c, TdpService svc, CancellationToken ct) =>
            await ExportAsync(c, svc, new TdpRequest(req.ObjectType, req.Number, req.Revision, req.RecipientProfile), ct))
            .WithSummary("Exports the package as a signed ZIP stream.");

        tdp.MapGet("", async (string? number, int? limit, HttpContext c, TdpService svc, CancellationToken ct) =>
            (await svc.ListAsync(Who(c), number, limit ?? 50, ct)).ToHttp()).WithSummary("Export history readable by the caller.");

        tdp.MapGet("/{id:guid}", async (Guid id, HttpContext c, TdpService svc, CancellationToken ct) => (await svc.GetAsync(Who(c), id, Corr(c), ct)).ToHttp())
            .WithSummary("One export record with its manifest.");

        tdp.MapGet("/{id:guid}/verify", async (Guid id, HttpContext c, TdpService svc, CancellationToken ct) => (await svc.VerifyRecordAsync(Who(c), id, Corr(c), ct)).ToHttp())
            .WithSummary("Checks the stored manifest against its recorded hash and signature.");

        tdp.MapPost("/{id:guid}/verify", async (Guid id, HttpContext c, TdpService svc, CancellationToken ct) =>
        {
            var result = await svc.VerifyPackageAsync(Who(c), c.Request.Body, Corr(c), ct);
            if (result.IsOk && result.Value!.ExportId is { } found && found != id)
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The package belongs to a different export.");
            return result.ToHttp();
        }).Accepts<Stream>("application/zip").WithSummary("Verifies an uploaded package against the record of this export.");

        // The same export for the Blazor UI: a form POST with the session cookie and an antiforgery token (no token in a URL).
        app.MapPost(UiTdpRoute, async (HttpContext c, IAntiforgery antiforgery, TdpService svc, CancellationToken ct) =>
        {
            if (!await antiforgery.IsRequestValidAsync(c)) return Results.BadRequest();
            var form = await c.Request.ReadFormAsync(ct);
            return await ExportAsync(c, svc, new TdpRequest(form["objectType"].ToString(), form["number"].ToString(), string.IsNullOrWhiteSpace(form["revision"]) ? null : form["revision"].ToString(), form["recipient"].ToString()), ct);
        }).RequireAuthorization(Policies.AnyPlmRole).DisableAntiforgery().ExcludeFromDescription(); // validated explicitly above

        // ---- administration
        var admin = app.MapGroup("/api/v1/admin/integration").RequireAuthorization(Policies.Admin).WithTags("Integration administration");

        admin.MapGet("/profiles", async (HttpContext c, IntegrationAdminService s, CancellationToken ct) => (await s.ListProfilesAsync(Who(c), ct)).ToHttp());
        admin.MapPut("/profiles", async (ProfileInput body, HttpContext c, IntegrationAdminService s, CancellationToken ct) => (await s.SaveProfileAsync(Who(c), body, Corr(c), ct)).ToHttp());
        admin.MapGet("/sinks", async (HttpContext c, IntegrationAdminService s, CancellationToken ct) => (await s.ListSinksAsync(Who(c), ct)).ToHttp());
        admin.MapPut("/sinks", async (SinkInput body, HttpContext c, IntegrationAdminService s, CancellationToken ct) =>
        {
            try { return (await s.SaveSinkAsync(Who(c), body, Corr(c), ct)).ToHttp(); }
            catch (NotSupportedException e) { return Results.Problem(statusCode: StatusCodes.Status501NotImplemented, title: "Not implemented", detail: e.Message); }
        });
        admin.MapGet("/deliveries", async (string? status, string? sink, int? limit, HttpContext c, IntegrationAdminService s, CancellationToken ct) =>
            (await s.ListDeliveriesAsync(Who(c), status, sink, limit ?? 100, ct)).ToHttp());
        admin.MapPost("/deliveries/requeue", async (RequeueRequest body, HttpContext c, IntegrationAdminService s, CancellationToken ct) =>
            (await s.RequeueDeadAsync(Who(c), body.DeliveryId, body.Sink, Corr(c), ct)).ToHttp());
        admin.MapPost("/deliveries/reevaluate", async (RequeueRequest body, HttpContext c, IntegrationAdminService s, CancellationToken ct) =>
            (await s.ReevaluateWithheldAsync(Who(c), body.DeliveryId, body.Sink, Corr(c), ct)).ToHttp());
        admin.MapGet("/outbox", async (string? type, string? aggregate, long? before, int? limit, HttpContext c, OutboxQueries q, CancellationToken ct) =>
            (await q.ListForAdminAsync(Who(c), type, aggregate, before, limit ?? 50, Corr(c), ct)).ToHttp());
        admin.MapGet("/tdp-categories", async (HttpContext c, IntegrationAdminService s, CancellationToken ct) => (await s.ListCategoryRulesAsync(Who(c), ct)).ToHttp());
        admin.MapPut("/tdp-categories", async (CategoryRuleRequest body, HttpContext c, IntegrationAdminService s, CancellationToken ct) =>
            (await s.SaveCategoryRuleAsync(Who(c), body.DocumentType, body.LinkType, body.Category, Corr(c), ct)).ToHttp());
        admin.MapDelete("/tdp-categories", async (string documentType, string? linkType, HttpContext c, IntegrationAdminService s, CancellationToken ct) =>
            (await s.DeleteCategoryRuleAsync(Who(c), documentType, string.IsNullOrWhiteSpace(linkType) ? "*" : linkType, Corr(c), ct)).ToHttp());
    }

    private static async Task<IResult> ExportAsync(HttpContext c, TdpService svc, TdpRequest request, CancellationToken ct)
    {
        if (!svc.CanExport)
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "TDP export is not configured",
                detail: "No signing key is configured (Keel:Integration:TdpSigningKeyPath); packages are never exported unsigned.");
        var outcome = await svc.PrepareAsync(SubjectFactory.Require(c), request, CorrelationId.Get(c), ct);
        if (!outcome.IsOk) return outcome.ToHttp();
        var download = outcome.Value!;
        c.Response.Headers[DocumentEndpoints.MarkingsHeader] = DocumentEndpoints.HeaderValue(download.Banner);
        c.Response.Headers["X-Keel-Tdp-Id"] = download.ExportId.ToString("D");
        return Results.Stream(async s => await download.WriteToAsync(s, c.RequestAborted), download.ContentType, download.FileName);
    }
}
