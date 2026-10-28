using Keel.Api.Infrastructure;
using Keel.Core;
using Keel.Core.Viewing;
using Keel.Viewing;
using Keel.Web.Services;
using Microsoft.Net.Http.Headers;

namespace Keel.Api.Endpoints;

/// <summary>
/// The file viewer (increment 6.0, ADR-0027): a standalone page, a metadata route and a content route, all authenticated by the session cookie like
/// the UI download route (no token ever appears in a URL). Viewing is authorized as a download, audited as <c>document.view</c>, never cached, and
/// served under the viewer's own Content-Security-Policy (<see cref="ViewerCsp"/>); every other route keeps the strict global policy.
/// </summary>
public static class ViewerEndpoints
{
    public const string NeutralBanner = "UNCLASSIFIED";

    public static void MapViewerEndpoints(this IEndpointRouteBuilder app)
    {
        var viewer = app.MapGroup("/viewer").RequireAuthorization(Policies.AnyPlmRole);
        viewer.MapGet("/documents/{number}/files/{fileId:guid}", Page);
        viewer.MapGet("/documents/{number}/primary", Primary);
        viewer.MapGet("/meta/documents/{number}/files/{fileId:guid}", Meta);
        viewer.MapGet("/content/documents/{number}/files/{fileId:guid}", Content);
    }

    private static async Task<IResult> Page(string number, Guid fileId, HttpContext ctx, ViewerService viewer, IServiceProvider services, CancellationToken ct)
    {
        var outcome = await viewer.DescribeAsync(SubjectFactory.Require(ctx), number, fileId, CorrelationId.Get(ctx), ct);
        return await RenderAsync(outcome, number, services, ctx);
    }

    private static async Task<IResult> Primary(string number, string? revision, HttpContext ctx, ViewerService viewer, IServiceProvider services, CancellationToken ct)
    {
        var outcome = await viewer.DescribePrimaryAsync(SubjectFactory.Require(ctx), number, string.IsNullOrWhiteSpace(revision) ? null : revision, CorrelationId.Get(ctx), ct);
        return outcome.IsOk
            ? Results.Redirect(ViewerUi.PageUrl(outcome.Value!.File.DocumentNumber, outcome.Value.File.FileId))
            : await RenderAsync(outcome, number, services, ctx);
    }

    private static async Task<IResult> RenderAsync(Outcome<ViewerDescription> outcome, string number, IServiceProvider services, HttpContext ctx)
    {
        ViewerPageModel model;
        var status = StatusCodes.Status200OK;
        if (outcome.IsOk)
        {
            var d = outcome.Value!;
            model = PageModel(d);
        }
        else
        {
            // Same answer as a missing document (ADR-0004): existence is not disclosed, and there are no markings to show, so the neutral banner is used.
            status = outcome.Kind == OutcomeKind.Forbidden ? StatusCodes.Status403Forbidden : StatusCodes.Status404NotFound;
            model = new ViewerPageModel
            {
                Banner = NeutralBanner,
                Title = "File not available",
                Status = "not-found",
                BackUrl = "/documents",
                Message = outcome.Kind == OutcomeKind.Forbidden
                    ? "You may read this document, but you may not view or download its files."
                    : $"Document {number} or its file was not found, or you are not authorized to see it.",
            };
        }
        var html = await ViewerUi.RenderAsync(services, model);
        return Results.Content(html, "text/html; charset=utf-8", System.Text.Encoding.UTF8, status);
    }

    /// <summary>
    /// The page model of a described file. The banner and the notices are the object's own (<c>ViewableFile.Banner</c> is
    /// <c>Markings.BannerText()</c>, produced by the documents module); this is the only place a viewer page gets its banner for a real file.
    /// </summary>
    public static ViewerPageModel PageModel(ViewerDescription d)
    {
        var f = d.File;
        return new ViewerPageModel
        {
            Banner = f.Banner,
            Title = f.FileName,
            Status = d.Status switch { ViewerStatus.Ready => "ready", ViewerStatus.TooLarge => "too-large", ViewerStatus.Blocked => "blocked", _ => "no-preview" },
            Message = d.Message,
            Notices = f.Notices,
            DocumentNumber = f.DocumentNumber,
            DocumentName = f.DocumentName,
            FileName = f.FileName,
            FormatLabel = d.Detection.Format.Label,
            FormatId = d.Detection.Format.Id,
            ViewerType = d.ViewerType,
            SizeBytes = f.SizeBytes,
            StoredMediaType = f.StoredMediaType,
            Sha256 = f.Sha256,
            IterationLabel = f.IterationLabel,
            Warning = d.Detection.Warning,
            BackUrl = "/documents/" + Uri.EscapeDataString(f.DocumentNumber),
            DownloadUrl = "/" + DocumentUi.DownloadUrl(f.DocumentNumber, f.FileId),
            MetaUrl = ViewerUi.MetaUrl(f.DocumentNumber, f.FileId),
            ContentUrl = ViewerUi.ContentUrl(f.DocumentNumber, f.FileId),
            MaxTriangles = d.MaxTriangles,
        };
    }

    private static async Task<IResult> Meta(string number, Guid fileId, HttpContext ctx, ViewerService viewer, CancellationToken ct)
    {
        var outcome = await viewer.DescribeAsync(SubjectFactory.Require(ctx), number, fileId, CorrelationId.Get(ctx), ct);
        if (!outcome.IsOk) return outcome.ToHttp();
        var d = outcome.Value!;
        var f = d.File;
        ctx.Response.Headers[DocumentEndpoints.MarkingsHeader] = DocumentEndpoints.HeaderValue(f.Banner);
        return Results.Json(new
        {
            status = d.Status.ToString().ToLowerInvariant(),
            viewer = d.ViewerType,
            format = d.Detection.Format.Id,
            formatLabel = d.Detection.Format.Label,
            claimedFormat = d.Detection.Claimed.Id,
            detectedBy = d.Detection.Basis,
            warning = d.Detection.Warning,
            message = d.Message,
            fileName = f.FileName,
            sizeBytes = f.SizeBytes,
            sha256 = f.Sha256,
            iteration = f.IterationLabel,
            documentNumber = f.DocumentNumber,
            banner = f.Banner,
            notices = f.Notices,
            limitBytes = d.LimitBytes,
            maxTriangles = d.MaxTriangles,
            contentUrl = ViewerUi.ContentUrl(f.DocumentNumber, f.FileId),
            downloadUrl = "/" + DocumentUi.DownloadUrl(f.DocumentNumber, f.FileId),
        });
    }

    private static async Task<IResult> Content(string number, Guid fileId, HttpContext ctx, ViewerService viewer, CancellationToken ct)
    {
        ViewerContentResult result;
        try
        {
            result = await viewer.OpenAsync(SubjectFactory.Require(ctx), number, fileId, CorrelationId.Get(ctx), ct);
        }
        catch (StoredFileIntegrityException e)
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Stored file failed integrity verification", detail: e.Message);
        }
        switch (result.Status)
        {
            case ViewerContentStatus.Ok:
                break;
            case ViewerContentStatus.NotFound:
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
            case ViewerContentStatus.Forbidden:
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", extensions: new Dictionary<string, object?> { ["reasons"] = result.Reasons });
            case ViewerContentStatus.TooLarge:
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Too large to preview", detail: result.Message);
            case ViewerContentStatus.NoPreview:
                return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "No preview for this type", detail: result.Message);
            case ViewerContentStatus.Blocked:
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Preview not available", detail: result.Message);
            case ViewerContentStatus.Unprocessable:
                return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "The file cannot be shown with its markings", detail: result.Message);
            case ViewerContentStatus.Busy:
                ctx.Response.Headers.RetryAfter = "5";
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Viewer busy", detail: result.Message);
            default:
                throw new InvalidOperationException($"Unhandled viewer status {result.Status}");
        }

        var content = result.Content!;
        var h = ctx.Response.Headers;
        // The media type is the SERVER's, from the detected format; the stored (client-supplied) one is never echoed. The response is sandboxed so that even a
        // direct navigation to an SVG cannot run script, it is never cached, and ranges are refused: the whole file is verified (and PDFs stamped) before sending.
        h.ContentType = content.Format.MediaType;
        h.ContentLength = content.Bytes.Length;
        h[HeaderNames.ContentSecurityPolicy] = ViewerCsp.Content;
        h.CacheControl = "no-store, max-age=0";
        h.Pragma = "no-cache";
        h.AcceptRanges = "none";
        h[DocumentEndpoints.MarkingsHeader] = DocumentEndpoints.HeaderValue(content.File.Banner);
        h["X-Keel-Handling"] = content.Marked ? "pdf-stamped" : "overlay";
        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(content.File.FileName);
        h.ContentDisposition = disposition.ToString();
        await ctx.Response.Body.WriteAsync(content.Bytes, ct);
        return Results.Empty;
    }
}
