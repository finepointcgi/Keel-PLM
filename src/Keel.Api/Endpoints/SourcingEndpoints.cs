using System.Text.Json;
using Keel.Api.Infrastructure;
using Keel.Core;
using Keel.Sourcing;
using Keel.Web.Services;

namespace Keel.Api.Endpoints;

public sealed record CreateSupplierRequest(string Name, string Country, string[] Roles, bool IsForeign, MarkingsDto Markings, string? CageCode = null, string? DunsId = null, string? ClearanceProfile = null);
public sealed record UpdateSupplierRequest(string Name, string Country, string[] Roles, bool IsForeign, int Revision, string? CageCode = null, string? DunsId = null, string? ClearanceProfile = null);
public sealed record SupplierMarkingsRequest(MarkingsDto Markings, string Reason);
public sealed record ProposeStatusRequest(string Status);
public sealed record SupplierStatusRequest(string Status, string Reason);
public sealed record CertificationRequest(string Type, string Number, string Issuer, DateOnly IssuedOn, DateOnly ExpiresOn, string? Scope = null);
public sealed record SourcingReasonRequest(string Reason);
public sealed record ContactRequest(string Name, string? Email = null, string? Phone = null, string? Title = null, bool IsPrimary = false);
public sealed record QualityRefRequest(string Kind, string Id);
public sealed record PortalUserRequest(string User);
public sealed record PriceBreakRequest(decimal Quantity, decimal UnitPrice);
public sealed record CreateEntryRequest(
    string Kind, string PartNumber, string SupplierNumber, string Status, string? RevisionScope = null, string? Mpn = null, string? ManufacturerNumber = null, DateOnly? EffectiveFrom = null,
    DateOnly? EffectiveTo = null, string? VendorPartNumber = null, int? LeadTimeDays = null, decimal? Moq = null, string? Currency = null, PriceBreakRequest[]? PriceBreaks = null, string? Note = null);
public sealed record UpdateEntryRequest(
    string Status, int Revision, DateOnly? EffectiveFrom = null, DateOnly? EffectiveTo = null, string? VendorPartNumber = null, int? LeadTimeDays = null, decimal? Moq = null, string? Currency = null,
    PriceBreakRequest[]? PriceBreaks = null, string? Note = null);
public sealed record EndEntryRequest(DateOnly EndsOn, string Reason);
public sealed record RelationRequest(string RelatedPart, string Relation, string? Note = null);
public sealed record RequestPartRequest(string PartNumber, decimal Quantity, string? Unit = null, string? Note = null);
public sealed record CreateSourcingRequest(string Kind, string Title, string? Description, DateOnly? DueOn, MarkingsDto Markings, RequestPartRequest[] Parts, string[] Suppliers);

/// <summary>
/// REST for suppliers, approved lists and sourcing views (internal roles) and for the supplier portal (the supplier role, scoped to the caller's own supplier by the
/// portal-user link and by nothing the request says). Thin: every rule lives in the module services (ADR-0037).
/// </summary>
public static class SourcingEndpoints
{
    public static void MapSourcingEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/sourcing").RequireAuthorization(Policies.AnyPlmRole);

        // ---- suppliers
        g.MapGet("/suppliers", async (string? q, string? status, string? role, string? country, bool? foreign, string? sort, int? limit, int? offset, string? dir, HttpContext ctx, SupplierService svc, CancellationToken ct) =>
            !SupplierService.SortKeys.TryResolve(sort, dir, out var spec, out var error) ? (IResult)OutcomeResults.BadSort(error!)
            : (await svc.SearchAsync(SubjectFactory.Require(ctx), new SupplierSearch(q, status, role, country, foreign == true, limit ?? 50, offset ?? 0, spec), CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/suppliers", async (CreateSupplierRequest req, HttpContext ctx, SupplierService svc, CancellationToken ct) =>
        {
            var m = req.Markings.ToMarkings();
            if (!m.IsSuccess) return Outcome<SupplierDetail>.Invalid(m.Errors).ToHttp();
            return (await svc.CreateAsync(SubjectFactory.Require(ctx), new CreateSupplierCommand(req.Name, req.Country, req.Roles, req.IsForeign, m.Value, req.CageCode, req.DunsId, req.ClearanceProfile), CorrelationId.Get(ctx), ct))
                .ToHttp(d => Results.Created($"/api/v1/sourcing/suppliers/{d.Summary.Number}", d));
        });
        g.MapGet("/suppliers/{number}", async (string number, HttpContext ctx, SupplierService svc, CancellationToken ct) =>
            (await svc.GetAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPut("/suppliers/{number}", async (string number, UpdateSupplierRequest req, HttpContext ctx, SupplierService svc, CancellationToken ct) =>
            (await svc.UpdateAsync(SubjectFactory.Require(ctx), number, new UpdateSupplierCommand(req.Name, req.Country, req.Roles, req.IsForeign, req.CageCode, req.DunsId, req.ClearanceProfile), req.Revision, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/suppliers/{number}/markings", async (string number, SupplierMarkingsRequest req, HttpContext ctx, SupplierService svc, CancellationToken ct) =>
        {
            var m = req.Markings.ToMarkings();
            return !m.IsSuccess ? Outcome<SupplierDetail>.Invalid(m.Errors).ToHttp()
                : (await svc.ChangeMarkingsAsync(SubjectFactory.Require(ctx), number, m.Value, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp();
        });
        g.MapPost("/suppliers/{number}/propose", async (string number, ProposeStatusRequest req, HttpContext ctx, SupplierService svc, CancellationToken ct) =>
            (await svc.ProposeStatusAsync(SubjectFactory.Require(ctx), number, req.Status, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/suppliers/{number}/status", async (string number, SupplierStatusRequest req, HttpContext ctx, SupplierService svc, CancellationToken ct) =>
            (await svc.SetStatusAsync(SubjectFactory.Require(ctx), number, req.Status, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/suppliers/{number}/certifications", async (string number, CertificationRequest req, HttpContext ctx, SupplierService svc, CancellationToken ct) =>
            (await svc.AddCertificationAsync(SubjectFactory.Require(ctx), number, new AddCertificationCommand(req.Type, req.Number, req.Issuer, req.Scope, req.IssuedOn, req.ExpiresOn), CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/suppliers/{number}/certifications/{id:guid}/revoke", async (string number, Guid id, SourcingReasonRequest req, HttpContext ctx, SupplierService svc, CancellationToken ct) =>
            (await svc.RevokeCertificationAsync(SubjectFactory.Require(ctx), number, id, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/suppliers/{number}/contacts", async (string number, ContactRequest req, HttpContext ctx, SupplierService svc, CancellationToken ct) =>
            (await svc.AddContactAsync(SubjectFactory.Require(ctx), number, new ContactInput(req.Name, req.Email, req.Phone, req.Title, req.IsPrimary), CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapDelete("/suppliers/{number}/contacts/{id:guid}", async (string number, Guid id, HttpContext ctx, SupplierService svc, CancellationToken ct) =>
            (await svc.RemoveContactAsync(SubjectFactory.Require(ctx), number, id, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/suppliers/{number}/quality-refs", async (string number, QualityRefRequest req, HttpContext ctx, SupplierService svc, CancellationToken ct) =>
            (await svc.AddQualityRefAsync(SubjectFactory.Require(ctx), number, req.Kind, req.Id, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapDelete("/suppliers/{number}/quality-refs/{id:guid}", async (string number, Guid id, HttpContext ctx, SupplierService svc, CancellationToken ct) =>
            (await svc.RemoveQualityRefAsync(SubjectFactory.Require(ctx), number, id, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/suppliers/{number}/portal-users", async (string number, PortalUserRequest req, HttpContext ctx, SupplierService svc, CancellationToken ct) =>
            (await svc.LinkPortalUserAsync(SubjectFactory.Require(ctx), number, req.User, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapDelete("/suppliers/{number}/portal-users/{sub}", async (string number, string sub, HttpContext ctx, SupplierService svc, CancellationToken ct) =>
            (await svc.UnlinkPortalUserAsync(SubjectFactory.Require(ctx), number, sub, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/clearance-profiles", async (SupplierService svc, CancellationToken ct) => Results.Ok(await svc.ClearanceProfilesAsync(ct)));

        // ---- a part's lists
        g.MapGet("/parts/{partNumber}", async (string partNumber, HttpContext ctx, SourcingService svc, CancellationToken ct) =>
            (await svc.GetPartSourcingAsync(SubjectFactory.Require(ctx), partNumber, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/parts/{partNumber}/relations", async (string partNumber, RelationRequest req, HttpContext ctx, SourcingService svc, CancellationToken ct) =>
            (await svc.AddRelationAsync(SubjectFactory.Require(ctx), partNumber, req.RelatedPart, req.Relation, req.Note, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapDelete("/parts/{partNumber}/relations/{id:guid}", async (string partNumber, Guid id, HttpContext ctx, SourcingService svc, CancellationToken ct) =>
            (await svc.RemoveRelationAsync(SubjectFactory.Require(ctx), partNumber, id, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/entries", async (CreateEntryRequest req, HttpContext ctx, SourcingService svc, CancellationToken ct) =>
            (await svc.CreateEntryAsync(SubjectFactory.Require(ctx), new CreateEntryCommand(req.Kind, req.PartNumber, req.RevisionScope, req.SupplierNumber, req.Mpn, req.ManufacturerNumber, req.Status,
                req.EffectiveFrom, req.EffectiveTo, req.VendorPartNumber, req.LeadTimeDays, req.Moq, req.Currency, req.PriceBreaks?.Select(b => new PriceBreak(b.Quantity, b.UnitPrice)).ToList(), req.Note),
                CorrelationId.Get(ctx), ct)).ToHttp(e => Results.Created($"/api/v1/sourcing/entries/{e.Number}", e)));
        g.MapGet("/entries/{number}", async (string number, HttpContext ctx, SourcingService svc, CancellationToken ct) =>
            (await svc.GetEntryAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPut("/entries/{number}", async (string number, UpdateEntryRequest req, HttpContext ctx, SourcingService svc, CancellationToken ct) =>
            (await svc.UpdateEntryAsync(SubjectFactory.Require(ctx), number, new UpdateEntryCommand(req.Status, req.EffectiveFrom, req.EffectiveTo, req.VendorPartNumber, req.LeadTimeDays, req.Moq, req.Currency,
                req.PriceBreaks?.Select(b => new PriceBreak(b.Quantity, b.UnitPrice)).ToList(), req.Note), req.Revision, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/entries/{number}/withdraw", async (string number, SourcingReasonRequest req, HttpContext ctx, SourcingService svc, CancellationToken ct) =>
            (await svc.WithdrawEntryAsync(SubjectFactory.Require(ctx), number, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/entries/{number}/end", async (string number, EndEntryRequest req, HttpContext ctx, SourcingService svc, CancellationToken ct) =>
            (await svc.EndEntryAsync(SubjectFactory.Require(ctx), number, req.EndsOn, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp());

        // ---- queries and the coverage report
        g.MapGet("/where-sourced", async (string? supplier, string? mpn, int? limit, int? offset, HttpContext ctx, SourcingService svc, CancellationToken ct) =>
            (await svc.WhereSourcedAsync(SubjectFactory.Require(ctx), new WhereSourcedQuery(supplier, mpn, null, limit ?? 100, offset ?? 0), CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/coverage", async (string mode, string number, HttpContext ctx, SourcingCoverageService svc, CancellationToken ct) =>
            (await svc.ReportAsync(SubjectFactory.Require(ctx), mode, number, CorrelationId.Get(ctx), ct)).ToHttp());

        // ---- requests (internal side)
        g.MapGet("/requests", async (string? q, string? state, string? sort, string? dir, int? limit, int? offset, HttpContext ctx, RequestService svc, CancellationToken ct) =>
            !RequestService.SortKeys.TryResolve(sort, dir, out var spec, out var error) ? (IResult)OutcomeResults.BadSort(error!)
            : (await svc.SearchAsync(SubjectFactory.Require(ctx), new RequestSearch(q, state, limit ?? 50, offset ?? 0, spec), CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/requests", async (CreateSourcingRequest req, HttpContext ctx, RequestService svc, CancellationToken ct) =>
        {
            var m = req.Markings.ToMarkings();
            if (!m.IsSuccess) return Outcome<RequestDetail>.Invalid(m.Errors).ToHttp();
            return (await svc.CreateAsync(SubjectFactory.Require(ctx), new CreateRequestCommand(req.Kind, req.Title, req.Description ?? "", req.DueOn, m.Value,
                [.. req.Parts.Select(p => new RequestPartInput(p.PartNumber, p.Quantity, p.Unit, p.Note))], req.Suppliers), CorrelationId.Get(ctx), ct))
                .ToHttp(d => Results.Created($"/api/v1/sourcing/requests/{d.Summary.Number}", d));
        });
        g.MapGet("/requests/{number}", async (string number, HttpContext ctx, RequestService svc, CancellationToken ct) =>
            (await svc.GetAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/requests/{number}/issue", async (string number, HttpContext ctx, RequestService svc, CancellationToken ct) =>
            (await svc.IssueAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/requests/{number}/close", async (string number, HttpContext ctx, RequestService svc, CancellationToken ct) =>
            (await svc.CloseAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapPost("/requests/{number}/cancel", async (string number, SourcingReasonRequest req, HttpContext ctx, RequestService svc, CancellationToken ct) =>
            (await svc.CancelAsync(SubjectFactory.Require(ctx), number, req.Reason, CorrelationId.Get(ctx), ct)).ToHttp());
        g.MapGet("/requests/{number}/files/{fileId:guid}", async (string number, Guid fileId, HttpContext ctx, RequestService svc, CancellationToken ct) =>
            await FileResult((await svc.OpenResponseFileAsync(SubjectFactory.Require(ctx), number, fileId, CorrelationId.Get(ctx), ct))));

        // ---- the supplier portal: only the supplier role, only the caller's own supplier
        var p = app.MapGroup("/api/v1/portal").RequireAuthorization(Policies.Supplier);
        p.MapGet("/me", async (HttpContext ctx, PortalService svc, CancellationToken ct) => (await svc.ProfileAsync(SubjectFactory.Require(ctx), CorrelationId.Get(ctx), ct)).ToHttp());
        p.MapGet("/requests", async (HttpContext ctx, PortalService svc, CancellationToken ct) => (await svc.ListRequestsAsync(SubjectFactory.Require(ctx), CorrelationId.Get(ctx), ct)).ToHttp());
        p.MapGet("/requests/{number}", async (string number, HttpContext ctx, PortalService svc, CancellationToken ct) =>
            (await svc.GetRequestAsync(SubjectFactory.Require(ctx), number, CorrelationId.Get(ctx), ct)).ToHttp());
        p.MapPost("/requests/{number}/responses", async (string number, HttpContext ctx, PortalService svc, CancellationToken ct) =>
        {
            // multipart/form-data: message, declined, quote (JSON array of {part, unitPrice, currency, leadTimeDays, moq}), and files (field name "response" or "certificate").
            if (!ctx.Request.HasFormContentType) return Outcome<PortalResponse>.Invalid(["Send multipart/form-data."]).ToHttp();
            var form = await ctx.Request.ReadFormAsync(ct);
            IReadOnlyList<QuoteLine> quote = [];
            if (form["quote"].ToString() is { Length: > 0 } json)
            {
                try { quote = RequestService.ParseQuote(json); }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return Outcome<PortalResponse>.Invalid(["The quote is not valid."]).ToHttp(); }
            }
            var files = new List<UploadedFile>();
            foreach (var f in form.Files) files.Add(new UploadedFile(f.FileName, f.Name == "certificate" ? "certificate" : "response", f.OpenReadStream()));
            try
            {
                return (await svc.RespondAsync(SubjectFactory.Require(ctx), number, new PortalResponseInput(form["message"].ToString(), form["declined"].ToString() == "true", quote), files, CorrelationId.Get(ctx), ct))
                    .ToHttp(r => Results.Created($"/api/v1/portal/requests/{number}", r));
            }
            finally { foreach (var f in files) await f.Content.DisposeAsync(); }
        });
        p.MapGet("/requests/{number}/files/{fileId:guid}", async (string number, Guid fileId, HttpContext ctx, PortalService svc, CancellationToken ct) =>
            await FileResult(await svc.OpenMyFileAsync(SubjectFactory.Require(ctx), number, fileId, CorrelationId.Get(ctx), ct)));

        // The same downloads for the Blazor UI, authenticated by the session cookie, so pages use plain links and no token appears in a URL.
        app.MapGet("/downloads/requests/{number}/files/{fileId:guid}", async (string number, Guid fileId, HttpContext ctx, RequestService svc, CancellationToken ct) =>
            await FileResult(await svc.OpenResponseFileAsync(SubjectFactory.Require(ctx), number, fileId, CorrelationId.Get(ctx), ct))).RequireAuthorization(Policies.AnyPlmRole);
        app.MapGet("/downloads/portal/requests/{number}/files/{fileId:guid}", async (string number, Guid fileId, HttpContext ctx, PortalService svc, CancellationToken ct) =>
            await FileResult(await svc.OpenMyFileAsync(SubjectFactory.Require(ctx), number, fileId, CorrelationId.Get(ctx), ct))).RequireAuthorization(Policies.Supplier);
    }

    /// <summary>A supplier's file is delivered as an attachment of unknown type (never rendered in the application's origin).</summary>
    private static Task<IResult> FileResult(Outcome<(Stream Content, string FileName, long Size)> o) =>
        Task.FromResult(o.ToHttp(v => Results.File(v.Content, "application/octet-stream", v.FileName)));
}
