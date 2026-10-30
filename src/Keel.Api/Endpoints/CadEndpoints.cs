using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Keel.Api.Infrastructure;
using Keel.Cad;
using Keel.Cad.Structure;
using Keel.Core;
using Keel.Documents.Vault;
using Microsoft.AspNetCore.Http.Features;

namespace Keel.Api.Endpoints;

public sealed record CadCheckOutRequest(string Top, IReadOnlyList<string>? Roles = null, bool? IncludeDependents = null, bool? AllowPartial = null);
public sealed record CadAssociationRequest(string DocumentNumber, string PartNumber, string Kind);
public sealed record CadDecisionsRequest(IReadOnlyList<ProposalPartDecision>? Decisions, bool? ApplyRemovals = null);
public sealed record CadProposalMetadata(string Title, string Format, MarkingsDto Markings, string? RootNumber = null, bool ApplyRemovals = false);

/// <summary>REST surface of the CAD connector protocol (ADR-0035, docs/integration/cad-connector.md). JWT bearer, like the rest of /api/v1.</summary>
public static partial class CadEndpoints
{
    private static readonly JsonSerializerOptions MetadataJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [GeneratedRegex(@"^(cad\.[a-z_.]+):\s*")]
    private static partial Regex CodePrefix();

    /// <summary>Like <c>ToHttp</c>, and adds the protocol's stable error code (the "cad.…" prefix of the message) as the <c>code</c> member of the problem.</summary>
    private static IResult ToCad<T>(this Outcome<T> o, Func<T, IResult>? ok = null)
    {
        if (o.Kind is OutcomeKind.Conflict or OutcomeKind.Invalid or OutcomeKind.Unprocessable && o.Errors.Count > 0 && CodePrefix().Match(o.Errors[0]) is { Success: true } m)
        {
            var status = o.Kind switch { OutcomeKind.Conflict => 409, OutcomeKind.Unprocessable => 422, _ => 400 };
            return Results.Problem(statusCode: status, title: o.Errors[0][m.Length..], extensions: new Dictionary<string, object?> { ["code"] = m.Groups[1].Value });
        }
        return o.ToHttp(ok);
    }

    public static void MapCadEndpoints(this IEndpointRouteBuilder app)
    {
        var cad = app.MapGroup("/api/v1/cad").RequireAuthorization(Policies.AnyPlmRole).WithTags("CAD");

        cad.MapGet("/schemas/structure-v1", () => Results.Text(CadModule.StructureSchema(), "application/schema+json")).WithName("CadStructureSchema");

        // ---- packages: check-out and check-in as a set
        cad.MapPost("/packages/checkout", async (CadCheckOutRequest req, HttpContext ctx, CadPackageService svc, CancellationToken ct) =>
            (await svc.CheckOutAsync(SubjectFactory.Require(ctx), req.Top, new PackageOptions(req.Roles, req.IncludeDependents ?? true, req.AllowPartial ?? false), CorrelationId.Get(ctx), ct))
                .ToCad(m => Results.Created($"/api/v1/cad/checkouts/{m.Id}", m))).WithName("CadCheckOutPackage");

        cad.MapGet("/checkouts", async (HttpContext ctx, CadPackageService svc, CancellationToken ct) =>
            Results.Ok((await svc.ListOpenAsync(SubjectFactory.Require(ctx), ct)).Select(s => new { id = s.Id, top = s.Top, createdAt = s.CreatedAt }))).WithName("CadListCheckouts");

        cad.MapGet("/checkouts/{id:guid}", async (Guid id, HttpContext ctx, CadPackageService svc, CancellationToken ct) =>
            (await svc.GetAsync(SubjectFactory.Require(ctx), id, CorrelationId.Get(ctx), ct)).ToCad()).WithName("CadGetCheckout");

        cad.MapPost("/checkouts/{id:guid}/members", async (Guid id, AddMemberRequest req, HttpContext ctx, CadPackageService svc, CancellationToken ct) =>
            (await svc.AddMemberAsync(SubjectFactory.Require(ctx), id, req, CorrelationId.Get(ctx), ct)).ToCad(m => Results.Created($"/api/v1/cad/checkouts/{id}", m))).WithName("CadAddMember");

        // Raw body, streamed: query fileName (required), role (primary or attachment), header X-Content-SHA256 (required).
        cad.MapPut("/checkouts/{id:guid}/members/{number}/files", async (Guid id, string number, string fileName, string? role, HttpContext ctx, CadPackageService svc, VaultOptions vault, CancellationToken ct) =>
        {
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = vault.MaxUploadBytes + 1;
            var sha = ctx.Request.Headers["X-Content-SHA256"].ToString();
            return (await svc.UploadAsync(SubjectFactory.Require(ctx), id, number, role ?? "primary", fileName, ctx.Request.ContentType, sha, ctx.Request.Body, CorrelationId.Get(ctx), ct)).ToCad();
        }).WithName("CadUploadFile");

        cad.MapGet("/checkouts/{id:guid}/members/{number}/files/{sha256}", async (Guid id, string number, string sha256, HttpContext ctx, CadPackageService svc, CancellationToken ct) =>
        {
            Outcome<CadDownload> outcome;
            try { outcome = await svc.DownloadAsync(SubjectFactory.Require(ctx), id, number, sha256, CorrelationId.Get(ctx), ct); }
            catch (VaultIntegrityException e) { return Results.Problem(statusCode: 500, title: "Stored file failed integrity verification", detail: e.Message); }
            if (!outcome.IsOk) return outcome.ToCad();
            var d = outcome.Value!;
            ctx.Response.Headers[DocumentEndpoints.MarkingsHeader] = DocumentEndpoints.HeaderValue(d.Banner);
            return Results.Stream(s => d.WriteToAsync(s, ctx.RequestAborted), "application/zip", d.FileName + ".zip");
        }).WithName("CadDownloadFile");

        cad.MapPost("/checkouts/{id:guid}/checkin", async (Guid id, PackageCheckInRequest req, HttpContext ctx, CadPackageService svc, CancellationToken ct) =>
            (await svc.CheckInAsync(SubjectFactory.Require(ctx), id, req, CorrelationId.Get(ctx), ct)).ToCad()).WithName("CadCheckIn");

        cad.MapPost("/checkouts/{id:guid}/undo", async (Guid id, HttpContext ctx, CadPackageService svc, CancellationToken ct) =>
            (await svc.UndoAsync(SubjectFactory.Require(ctx), id, CorrelationId.Get(ctx), ct)).ToCad()).WithName("CadUndoCheckOut");

        // ---- graph, associativity, staleness
        cad.MapPut("/documents/{number}/role", async (string number, RegisterRoleRequest req, HttpContext ctx, CadGraphService svc, CancellationToken ct) =>
            (await svc.RegisterRoleAsync(SubjectFactory.Require(ctx), number, req.Role, req.CadSystem, CorrelationId.Get(ctx), ct)).ToCad()).WithName("CadRegisterRole");

        cad.MapGet("/documents/{number}/package", async (string number, int? levels, HttpContext ctx, CadGraphService svc, CancellationToken ct) =>
            (await svc.GetPackageAsync(SubjectFactory.Require(ctx), number, levels ?? 5, CorrelationId.Get(ctx), ct)).ToCad()).WithName("CadGetPackage");

        cad.MapGet("/documents/{number}/where-used", async (string number, int? levels, HttpContext ctx, CadGraphService svc, CancellationToken ct) =>
            (await svc.WhereUsedAsync(SubjectFactory.Require(ctx), number, levels ?? 5, CorrelationId.Get(ctx), ct)).ToCad()).WithName("CadWhereUsed");

        cad.MapGet("/stale", async (int? limit, HttpContext ctx, CadGraphService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListStaleAsync(SubjectFactory.Require(ctx), limit ?? 100, CorrelationId.Get(ctx), ct))).WithName("CadListStale");

        cad.MapPost("/associations", async (CadAssociationRequest req, HttpContext ctx, CadGraphService svc, CancellationToken ct) =>
            (await svc.AddAssociationAsync(SubjectFactory.Require(ctx), req.DocumentNumber, req.PartNumber, req.Kind, CorrelationId.Get(ctx), ct)).ToCad(a => Results.Created($"/api/v1/cad/associations/{a.Id}", a))).WithName("CadAddAssociation");

        cad.MapDelete("/associations/{id:guid}", async (Guid id, HttpContext ctx, CadGraphService svc, CancellationToken ct) =>
            (await svc.RemoveAssociationAsync(SubjectFactory.Require(ctx), id, CorrelationId.Get(ctx), ct)).ToCad(_ => Results.NoContent())).WithName("CadRemoveAssociation");

        cad.MapGet("/parts/{number}/documents", async (string number, HttpContext ctx, CadGraphService svc, CancellationToken ct) =>
            (await svc.ListForPartAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToCad()).WithName("CadDocumentsOfPart");

        // ---- structure proposals (multipart: field "metadata" JSON, then field "file")
        cad.MapPost("/bom-proposals", async (HttpContext ctx, CadBomProposalService svc, VaultOptions vault, CancellationToken ct) =>
            await DocumentEndpoints.ReadMultipartAsync(ctx, vault, async (fields, file) =>
            {
                if (!fields.TryGetValue("metadata", out var json)) return DocumentEndpoints.Invalid("Field 'metadata' (JSON) is required before 'file'.");
                if (file is null) return DocumentEndpoints.Invalid("Field 'file' is required.");
                CadProposalMetadata? meta;
                try { meta = JsonSerializer.Deserialize<CadProposalMetadata>(json, MetadataJson); }
                catch (JsonException e) { return DocumentEndpoints.Invalid("Field 'metadata' is not valid JSON: " + e.Message); }
                if (meta?.Markings is null) return DocumentEndpoints.Invalid("Field 'metadata' must contain title, format and markings.");
                var markings = meta.Markings.ToMarkings();
                if (!markings.IsSuccess) return Outcome<ProposalDetail>.Invalid(markings.Errors).ToHttp();
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int n;
                while ((n = await file.Content.ReadAsync(chunk, ct)) > 0)
                {
                    if (buffer.Length + n > StructureLimits.MaxBytes) return DocumentEndpoints.Invalid($"The file is larger than {StructureLimits.MaxBytes / (1024 * 1024)} MiB.");
                    buffer.Write(chunk, 0, n);
                }
                return (await svc.CreateAsync(SubjectFactory.Require(ctx), new CreateProposalRequest(meta.Title, meta.Format, markings.Value, meta.RootNumber, meta.ApplyRemovals),
                    buffer.ToArray(), CorrelationId.Get(ctx), ct)).ToCad(p => Results.Created($"/api/v1/cad/bom-proposals/{p.Number}", p));
            }, ct)).WithName("CadCreateProposal");

        cad.MapGet("/bom-proposals", async (string? status, int? limit, int? offset, HttpContext ctx, CadBomProposalService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(SubjectFactory.Require(ctx), status, limit ?? 50, offset ?? 0, CorrelationId.Get(ctx), ct))).WithName("CadListProposals");

        cad.MapGet("/bom-proposals/{number}", async (string number, HttpContext ctx, CadBomProposalService svc, CancellationToken ct) =>
            (await svc.GetAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToCad()).WithName("CadGetProposal");

        cad.MapGet("/bom-proposals/{number}/placements", async (string number, HttpContext ctx, CadBomProposalService svc, CancellationToken ct) =>
            (await svc.GetPlacementsAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToCad()).WithName("CadGetProposalPlacements");

        // The placed structure of the STEP file of a document, derived from the verified stored bytes. Needs Read and Download, like the file viewer; audited as document.view.
        cad.MapGet("/documents/{number}/placements", async (string number, Guid? fileId, string? revision, HttpContext ctx, AssemblyViewService svc, CancellationToken ct) =>
        {
            var result = await svc.OpenAsync(SubjectFactory.Require(ctx), number, fileId, revision, CorrelationId.Get(ctx), ct);
            return result.Status switch
            {
                AssemblyViewStatus.Ok when result.Kind == AssemblyViewKinds.Assembly => Results.Ok(DocumentPlacements.From(result)),
                AssemblyViewStatus.Ok => Results.Problem(statusCode: 422, title: "This model carries no placed structure (only STEP files are read for placements).", extensions: new Dictionary<string, object?> { ["code"] = "cad.no_placements" }),
                AssemblyViewStatus.NotFound => Outcome<object>.NotFound().ToHttp(),
                AssemblyViewStatus.TooLarge => Results.Problem(statusCode: 413, title: result.Message),
                AssemblyViewStatus.Blocked => Results.Problem(statusCode: 409, title: result.Message),
                AssemblyViewStatus.Busy => Results.Problem(statusCode: 503, title: result.Message),
                _ => Results.Problem(statusCode: 422, title: result.Message, extensions: new Dictionary<string, object?> { ["code"] = CadErrors.Proposal }),
            };
        }).WithName("CadGetDocumentPlacements");

        cad.MapPut("/bom-proposals/{number}/decisions", async (string number, CadDecisionsRequest req, HttpContext ctx, CadBomProposalService svc, CancellationToken ct) =>
            (await svc.DecideAsync(SubjectFactory.Require(ctx), number, req.Decisions ?? [], req.ApplyRemovals, CorrelationId.Get(ctx), ct)).ToCad()).WithName("CadDecideProposal");

        cad.MapPost("/bom-proposals/{number}/apply", async (string number, HttpContext ctx, CadBomProposalService svc, CancellationToken ct) =>
            (await svc.ApplyAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToCad()).WithName("CadApplyProposal");

        cad.MapPost("/bom-proposals/{number}/discard", async (string number, HttpContext ctx, CadBomProposalService svc, CancellationToken ct) =>
            (await svc.DiscardAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToCad()).WithName("CadDiscardProposal");
    }
}
