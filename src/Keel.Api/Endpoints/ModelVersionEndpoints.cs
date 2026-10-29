using System.Globalization;
using Keel.Api.Infrastructure;
using Keel.Core;
using Keel.Core.Geometry;
using Keel.Core.Viewing;
using Keel.Documents;
using Keel.Documents.Marking;
using Keel.Documents.Vault;
using Keel.Viewing;
using Keel.Web.Services;
using Microsoft.Net.Http.Headers;

namespace Keel.Api.Endpoints;

/// <summary>
/// Model versioning (increment 9.1, ADR-0046): the REST API (bearer), the cookie-authenticated ZIP download of selected versions for the UI, and the two viewer
/// routes of the visual comparison (the compare page and the triangle stream). Every route goes through the documents module's own authorization, which
/// evaluates the access policy and audits; nothing here decides access.
/// </summary>
public static class ModelVersionEndpoints
{
    public static void MapModelVersionEndpoints(this IEndpointRouteBuilder app)
    {
        var docs = app.MapGroup("/api/v1/documents").RequireAuthorization(Policies.AnyPlmRole);
        docs.MapGet("/{number}/versions", async (string number, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.GetModelVersionsAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        docs.MapGet("/{number}/versions/compare", async (string number, Guid a, Guid b, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.CompareModelVersionsAsync(SubjectFactory.Require(ctx), number, a, b, CorrelationId.Get(ctx), ct)).ToHttp());
        docs.MapPost("/{number}/versions/{iterationId:guid}/restore", async (string number, Guid iterationId, int? expectedIteration, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.RestoreModelVersionAsync(SubjectFactory.Require(ctx), number, iterationId, expectedIteration, CorrelationId.Get(ctx), ct)).ToHttp());
        docs.MapPost("/{number}/revise", async (string number, ReviseRequest req, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.ReviseAsync(SubjectFactory.Require(ctx), number, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp());
        // multipart: text fields (note, reviseIfReleased, expectedIteration) first, then the file, streamed into the vault
        docs.MapPost("/{number}/versions", async (string number, HttpContext ctx, DocumentService svc, VaultOptions vault, CancellationToken ct) =>
            await DocumentEndpoints.ReadMultipartAsync(ctx, vault, async (fields, file) =>
            {
                if (file is null) return DocumentEndpoints.Invalid("A 'file' field is required.");
                fields.TryGetValue("note", out var note);
                var revise = fields.TryGetValue("reviseIfReleased", out var r) && bool.TryParse(r, out var rb) && rb;
                int? expected = fields.TryGetValue("expectedIteration", out var e) && int.TryParse(e, NumberStyles.None, CultureInfo.InvariantCulture, out var ei) ? ei : null;
                return (await svc.UploadNewVersionAsync(SubjectFactory.Require(ctx), number, file, note, revise, expected, CorrelationId.Get(ctx), ct)).ToHttp();
            }, ct));

        app.MapGet(DocumentUi.VersionsZipRoute, Zip).RequireAuthorization(Policies.AnyPlmRole);

        var viewer = app.MapGroup("/viewer").RequireAuthorization(Policies.AnyPlmRole);
        viewer.MapGet("/mesh/documents/{number}/files/{fileId:guid}", Mesh);
        viewer.MapGet("/compare/documents/{number}", Compare);
    }

    public sealed record ReviseRequest(string? Reason);

    private static async Task<IResult> Zip(string number, string? ids, HttpContext ctx, DocumentService svc, CancellationToken ct)
    {
        var list = new List<Guid>();
        foreach (var part in (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(part, out var id)) return DocumentEndpoints.Invalid("The version list is not valid.");
            list.Add(id);
        }
        Outcome<MarkedDownload> outcome;
        try
        {
            outcome = await svc.DownloadVersionsAsync(SubjectFactory.Require(ctx), number, list, CorrelationId.Get(ctx), ct);
        }
        catch (VaultIntegrityException e)
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Stored file failed integrity verification", detail: e.Message);
        }
        if (!outcome.IsOk) return outcome.ToHttp();
        var download = outcome.Value!;
        ctx.Response.Headers[DocumentEndpoints.MarkingsHeader] = DocumentEndpoints.HeaderValue(download.Banner);
        return Results.Stream(s => download.WriteToAsync(s, ctx.RequestAborted), download.ContentType, download.FileName);
    }

    // ---------------------------------------------------------------- viewer: triangle stream and compare page

    private static async Task<IResult> Mesh(string number, Guid fileId, HttpContext ctx, ViewerService viewer, CancellationToken ct)
    {
        ViewerMeshResult result;
        try
        {
            result = await viewer.OpenMeshAsync(SubjectFactory.Require(ctx), number, fileId, CorrelationId.Get(ctx), ct);
        }
        catch (StoredFileIntegrityException e)
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Stored file failed integrity verification", detail: e.Message);
        }
        switch (result.Status)
        {
            case ViewerContentStatus.Ok: break;
            case ViewerContentStatus.NotFound: return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
            case ViewerContentStatus.Forbidden:
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", extensions: new Dictionary<string, object?> { ["reasons"] = result.Reasons });
            case ViewerContentStatus.TooLarge: return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Too large to compare", detail: result.Message);
            case ViewerContentStatus.NoPreview: return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "No visual comparison for this type", detail: result.Message);
            case ViewerContentStatus.Blocked: return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Preview not available", detail: result.Message);
            case ViewerContentStatus.Unprocessable: return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "The model cannot be read", detail: result.Message);
            case ViewerContentStatus.Busy:
                ctx.Response.Headers.RetryAfter = "5";
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Viewer busy", detail: result.Message);
            default: throw new InvalidOperationException($"Unhandled viewer status {result.Status}");
        }
        var mesh = result.Mesh!.Mesh;
        var h = ctx.Response.Headers;
        h.ContentType = "application/octet-stream";
        h.ContentLength = (long)mesh.Positions.Length * sizeof(float);
        h[HeaderNames.ContentSecurityPolicy] = ViewerCsp.Content;
        h.CacheControl = "no-store, max-age=0";
        h.Pragma = "no-cache";
        h.AcceptRanges = "none";
        h[DocumentEndpoints.MarkingsHeader] = DocumentEndpoints.HeaderValue(result.Mesh.File.Banner);
        h["X-Keel-Triangles"] = mesh.TriangleCount.ToString(CultureInfo.InvariantCulture);
        h["X-Keel-Up"] = mesh.Up;
        h["X-Keel-Unit"] = mesh.Unit;
        var bytes = new byte[mesh.Positions.Length * sizeof(float)];
        Buffer.BlockCopy(mesh.Positions, 0, bytes, 0, bytes.Length);
        await ctx.Response.Body.WriteAsync(bytes, ct);
        return Results.Empty;
    }

    private static async Task<IResult> Compare(string number, Guid a, Guid b, HttpContext ctx, DocumentService docs, IServiceProvider services, CancellationToken ct)
    {
        var outcome = await docs.CompareModelVersionsAsync(SubjectFactory.Require(ctx), number, a, b, CorrelationId.Get(ctx), ct);
        ViewerCompareModel model;
        var status = StatusCodes.Status200OK;
        if (outcome.IsOk)
        {
            var c = outcome.Value!;
            var notices = MarkingStatements.Notices(c.Markings);
            if (c.VisualAvailable)
            {
                model = new ViewerCompareModel
                {
                    Banner = c.Banner, Title = $"Compare {c.A.Label} and {c.B.Label}", Status = "ready", Notices = notices, DocumentNumber = c.Number, DocumentName = c.Name,
                    A = SideModel(c.Number, c.A), B = SideModel(c.Number, c.B), Summary = c.Delta.Summary,
                    BackUrl = "/documents/" + Uri.EscapeDataString(c.Number) + "?tab=versions", MaxTriangles = ViewerService.MaxCompareTriangles,
                };
            }
            else
            {
                status = StatusCodes.Status409Conflict;
                model = new ViewerCompareModel
                {
                    Banner = c.Banner, Title = "Visual comparison not available", Status = "unavailable", Message = c.VisualUnavailableReason, Notices = notices,
                    DocumentNumber = c.Number, DocumentName = c.Name, BackUrl = "/documents/" + Uri.EscapeDataString(c.Number) + "?tab=versions",
                };
            }
        }
        else
        {
            // Same answer as a missing document (ADR-0004): existence is not disclosed, there are no markings to show, so the neutral banner is used.
            status = outcome.Kind switch { OutcomeKind.Invalid => StatusCodes.Status400BadRequest, OutcomeKind.Conflict => StatusCodes.Status409Conflict, _ => StatusCodes.Status404NotFound };
            model = new ViewerCompareModel
            {
                Banner = ViewerEndpoints.NeutralBanner, Title = "Comparison not available", Status = "not-found", BackUrl = "/documents",
                Message = outcome.Kind is OutcomeKind.Invalid or OutcomeKind.Conflict
                    ? string.Join(" ", outcome.Errors)
                    : $"Document {number} or its versions were not found, or you are not authorized to see them.",
            };
        }
        var html = await ViewerCompareUi.RenderAsync(services, model);
        return Results.Content(html, "text/html; charset=utf-8", System.Text.Encoding.UTF8, status);
    }

    private static ViewerCompareSide SideModel(string number, ModelCompareSide s)
    {
        var m = s.Metrics;
        var summary = m.IsAnalysed
            ? $"{m.Triangles:N0} triangles · {Dim(m.SizeX)} × {Dim(m.SizeY)} × {Dim(m.SizeZ)} {(m.Unit == "mm" ? "mm" : "units")}"
            : "not measured";
        return new ViewerCompareSide(s.Label, s.FileName ?? "", s.Sha256 ?? "", s.SizeBytes ?? 0, ViewerCompareUi.MeshUrl(number, s.FileId!.Value), summary);
    }

    private static string Dim(double? v) => (v ?? 0).ToString(Math.Abs(v ?? 0) >= 100 ? "0.0" : "0.##", CultureInfo.InvariantCulture);
}
