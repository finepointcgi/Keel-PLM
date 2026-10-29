using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keel.Api.Infrastructure;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Vault;
using Keel.Web.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Npgsql;

namespace Keel.Api.Endpoints;

public sealed record CreateDocumentRequest(string Name, string DocumentType, MarkingsDto Markings, string? Description = null, string? Number = null);
public sealed record DocumentWorkingCopyRequest(string? Description);
public sealed record AddDocumentLinkRequest(string DocumentNumber, string LinkType, string? DocumentRevision = null);

public sealed record DocumentMetadata(IReadOnlyList<string> DocumentTypes, IReadOnlyList<string> FileRoles, IReadOnlyList<string> LinkTypes,
    long MaxUploadBytes, Lifecycle Lifecycle);

/// <summary>
/// Documents and vault endpoints (increment 1.3). Uploads are multipart/form-data read section by section and streamed
/// straight into the vault: text fields (metadata, role) must precede the <c>file</c> field, and nothing is buffered to
/// memory or temporary files. These are JWT-bearer endpoints without cookie authentication, so antiforgery does not apply.
/// </summary>
public static class DocumentEndpoints
{
    public const string MarkingsHeader = "X-Keel-Markings";
    private const int MaxFieldChars = 64 * 1024;
    private static readonly JsonSerializerOptions MetadataJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    // Room for multipart boundaries, headers and the metadata field on top of the file itself.
    private const long MultipartOverheadBytes = 1024 * 1024;

    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var docs = app.MapGroup("/api/v1/documents").RequireAuthorization(Policies.AnyPlmRole);

        docs.MapGet("", async Task<Microsoft.AspNetCore.Http.HttpResults.Results<Microsoft.AspNetCore.Http.HttpResults.Ok<DocumentSearchResult>, Microsoft.AspNetCore.Http.HttpResults.ValidationProblem>> (string? q, string? type, string? state, string? sort, string? dir, int? limit, int? offset, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            !DocumentService.SortKeys.TryResolve(sort, dir, out var spec, out var error) ? OutcomeResults.BadSort(error!)
            : TypedResults.Ok(await svc.SearchAsync(SubjectFactory.Require(ctx), q, type, state, limit ?? 50, offset ?? 0, spec, CorrelationId.Get(ctx), ct)));

        // multipart/form-data: field "metadata" (JSON CreateDocumentRequest), then optional field "file".
        docs.MapPost("", async (HttpContext ctx, DocumentService svc, VaultOptions vault, CancellationToken ct) =>
            await ReadMultipartAsync(ctx, vault, async (fields, file) =>
            {
                if (!fields.TryGetValue("metadata", out var json)) return Invalid("Field 'metadata' (JSON) is required before 'file'.");
                CreateDocumentRequest? req;
                try { req = JsonSerializer.Deserialize<CreateDocumentRequest>(json, MetadataJson); }
                catch (JsonException e) { return Invalid("Field 'metadata' is not valid JSON: " + e.Message); }
                if (req?.Markings is null) return Invalid("Field 'metadata' must contain name, documentType and markings.");
                var markings = req.Markings.ToMarkings();
                if (!markings.IsSuccess) return Outcome<DocumentDetail>.Invalid(markings.Errors).ToHttp();
                var cmd = new CreateDocumentCommand(req.Name, req.DocumentType, markings.Value, req.Description, req.Number);
                return (await svc.CreateAsync(SubjectFactory.Require(ctx), cmd, file, CorrelationId.Get(ctx), ct))
                    .ToHttp(d => Results.Created($"/api/v1/documents/{Uri.EscapeDataString(d.Number)}", d));
            }, ct));

        docs.MapGet("/{number}", async (string number, string? revision, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.GetAsync(SubjectFactory.Require(ctx), number, revision, CorrelationId.Get(ctx), ct)).ToHttp());

        docs.MapPost("/{number}/checkout", async (string number, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.CheckOutAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());

        docs.MapPut("/{number}/working-copy", async (string number, DocumentWorkingCopyRequest req, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.UpdateWorkingCopyAsync(SubjectFactory.Require(ctx), number, req.Description, CorrelationId.Get(ctx), ct)).ToHttp());

        // multipart/form-data: field "role" ('primary' or 'attachment'), then field "file".
        docs.MapPost("/{number}/working-copy/files", async (string number, HttpContext ctx, DocumentService svc, VaultOptions vault, CancellationToken ct) =>
            await ReadMultipartAsync(ctx, vault, async (fields, file) =>
            {
                if (file is null) return Invalid("Field 'file' is required.");
                var role = fields.GetValueOrDefault("role") ?? FileRoles.Attachment;
                return (await svc.UploadFileAsync(SubjectFactory.Require(ctx), number, role, file, CorrelationId.Get(ctx), ct)).ToHttp();
            }, ct));

        docs.MapDelete("/{number}/working-copy/files/{fileId:guid}", async (string number, Guid fileId, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.RemoveFileAsync(SubjectFactory.Require(ctx), number, fileId, CorrelationId.Get(ctx), ct)).ToHttp());

        docs.MapPost("/{number}/checkin", async (string number, CheckInRequest req, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.CheckInAsync(SubjectFactory.Require(ctx), number, req.Comment, CorrelationId.Get(ctx), ct)).ToHttp());

        docs.MapPost("/{number}/undo-checkout", async (string number, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.UndoCheckOutAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());

        docs.MapPost("/{number}/state", async (string number, SetStateRequest req, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.SetStateAsync(SubjectFactory.Require(ctx), number, req.ToState, req.Reason, req.Override, CorrelationId.Get(ctx), ct)).ToHttp());

        docs.MapPut("/{number}/markings", async (string number, ChangeMarkingsRequest req, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
        {
            var markings = req.Markings.ToMarkings();
            if (!markings.IsSuccess) return Outcome<DocumentDetail>.Invalid(markings.Errors).ToHttp();
            return (await svc.ChangeMarkingsAsync(SubjectFactory.Require(ctx), number, markings.Value, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp();
        });

        docs.MapGet("/{number}/files/{fileId:guid}/download", Download);

        // The same marked download for the Blazor UI, authenticated by the session cookie (the "keel" policy scheme routes
        // non-/api paths to it), so pages use plain links and no token appears in a URL.
        app.MapGet(DocumentUi.DownloadRoute, Download).RequireAuthorization(Policies.AnyPlmRole);

        docs.MapGet("/{number}/parts", async (string number, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.GetDocumentPartsAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());

        var parts = app.MapGroup("/api/v1/parts").RequireAuthorization(Policies.AnyPlmRole);

        parts.MapGet("/{number}/documents", async (string number, string? revision, int? iteration, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.GetPartLinksAsync(SubjectFactory.Require(ctx), number, revision, iteration, CorrelationId.Get(ctx), ct)).ToHttp());

        parts.MapPost("/{number}/working-copy/documents", async (string number, AddDocumentLinkRequest req, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.AddLinkToWorkingCopyAsync(SubjectFactory.Require(ctx), number, req.DocumentNumber, req.LinkType, req.DocumentRevision, CorrelationId.Get(ctx), ct)).ToHttp());

        parts.MapDelete("/{number}/working-copy/documents/{linkId:guid}", async (string number, Guid linkId, HttpContext ctx, DocumentService svc, CancellationToken ct) =>
            (await svc.RemoveLinkFromWorkingCopyAsync(SubjectFactory.Require(ctx), number, linkId, CorrelationId.Get(ctx), ct)).ToHttp());

        app.MapGet("/api/v1/metadata/documents", async (NpgsqlDataSource db, VaultOptions vault, IReferenceData refs, CancellationToken ct) =>
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            var lifecycle = await Lifecycle.LoadAsync(conn, null, await Lifecycle.CurrentIdAsync(conn, null, DocumentService.DefaultLifecycle, ct), ct);
            return new DocumentMetadata(await refs.CodesAsync(RefLists.DocumentType, ct), FileRoles.All, LinkTypes.All, vault.MaxUploadBytes, lifecycle!);
        }).RequireAuthorization(Policies.AnyPlmRole);
    }

    private static async Task<IResult> Download(string number, Guid fileId, HttpContext ctx, DocumentService svc, CancellationToken ct)
    {
        Outcome<MarkedDownload> outcome;
        try
        {
            outcome = await svc.DownloadAsync(SubjectFactory.Require(ctx), number, fileId, null, null, CorrelationId.Get(ctx), ct);
        }
        catch (VaultIntegrityException e)
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Stored file failed integrity verification", detail: e.Message);
        }
        if (!outcome.IsOk) return outcome.ToHttp();
        var download = outcome.Value!;
        ctx.Response.Headers[MarkingsHeader] = HeaderValue(download.Banner);
        return Results.Stream(s => download.WriteToAsync(s, ctx.RequestAborted), download.ContentType, download.FileName);
    }

    /// <summary>
    /// The banner as an HTTP header value. Header values are ASCII, so non-ASCII characters (the banner separator "·") and
    /// '%' are percent-encoded as UTF-8: "CUI//EXPT %C2%B7 ITAR USML XI(d)". Decoding gives the exact banner text.
    /// </summary>
    public static string HeaderValue(string banner)
    {
        var sb = new StringBuilder();
        foreach (var ch in banner)
        {
            if (ch is >= ' ' and <= '~' and not '%') { sb.Append(ch); continue; }
            foreach (var b in Encoding.UTF8.GetBytes(ch.ToString())) sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    internal static IResult Invalid(string message) => Outcome<DocumentDetail>.Invalid([message]).ToHttp();

    /// <summary>
    /// Reads text fields until the <c>file</c> field, then hands its body (a forward-only stream) to <paramref name="handle"/>.
    /// Sections after the file are not read.
    /// </summary>
    internal static async Task<IResult> ReadMultipartAsync(HttpContext ctx, VaultOptions vault,
        Func<IReadOnlyDictionary<string, string>, FileUpload?, Task<IResult>> handle, CancellationToken ct)
    {
        // Raise the server's request size limit for this request only (Kestrel's default is 30 MB).
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = vault.MaxUploadBytes + MultipartOverheadBytes;

        if (!MediaTypeHeaderValue.TryParse(ctx.Request.ContentType, out var contentType) ||
            !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase) ||
            HeaderUtilities.RemoveQuotes(contentType.Boundary).Value is not { Length: > 0 and <= 200 } boundary)
            return Invalid("Expected a multipart/form-data request.");

        var reader = new MultipartReader(boundary, ctx.Request.Body) { HeadersLengthLimit = 16 * 1024, BodyLengthLimit = vault.MaxUploadBytes + 1 };
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            for (var section = await reader.ReadNextSectionAsync(ct); section is not null; section = await reader.ReadNextSectionAsync(ct))
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd) ||
                    !cd.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase))
                    return Invalid("Every part needs a Content-Disposition: form-data header.");
                var name = HeaderUtilities.RemoveQuotes(cd.Name).Value ?? "";
                if (cd.IsFileDisposition())
                {
                    if (name != "file") return Invalid($"Unexpected file field '{name}'; use 'file'.");
                    var fileName = cd.FileNameStar.HasValue ? cd.FileNameStar.Value! : HeaderUtilities.RemoveQuotes(cd.FileName).Value ?? "";
                    return await handle(fields, new FileUpload(fileName, section.ContentType, section.Body));
                }
                using var text = new StreamReader(section.Body, Encoding.UTF8);
                var buffer = new char[MaxFieldChars + 1];
                var read = await text.ReadBlockAsync(buffer, ct);
                if (read > MaxFieldChars) return Invalid($"Field '{name}' is longer than {MaxFieldChars} characters.");
                fields[name] = new string(buffer, 0, read);
            }
            return await handle(fields, null);
        }
        catch (InvalidDataException e)
        {
            // Raised by MultipartReader for malformed bodies and for sections over the size limit.
            return Results.Problem(statusCode: e.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase) ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest,
                title: "The upload could not be read: " + e.Message);
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: $"The upload exceeds the maximum size of {vault.MaxUploadBytes} bytes.");
        }
    }
}
