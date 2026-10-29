using System.Globalization;
using Keel.Api.Infrastructure;
using Keel.Compliance;
using Keel.Core;
using Keel.Web.Services;

namespace Keel.Api.Endpoints;

public sealed record SubstanceRequest(string CasNumber, string Name, string? EcNumber = null, string? Mineral = null, int Revision = 0);
public sealed record MaterialComponentRequest(string CasNumber, decimal FractionPct);
public sealed record MaterialRequest(string Code, string Name, string? Description, MaterialComponentRequest[] Composition, bool Active = true, int Revision = 0);
public sealed record CreateListRequest(string Code, string Name, string Kind, string? Description = null);
public sealed record DraftVersionRequest(DateOnly EffectiveFrom, string? SourceNote = null);
public sealed record RegEntryRequest(string CasNumber, decimal ThresholdPct, string? Note = null);
public sealed record RegExemptionRequest(string Code, string Description, string? CasNumber = null, DateOnly? ExpiresOn = null);
public sealed record SaveVersionRequest(DateOnly EffectiveFrom, int Revision, RegEntryRequest[] Entries, RegExemptionRequest[]? Exemptions = null, string? SourceNote = null);
public sealed record DeclarationLineRequest(string? SubstanceCas, string? MaterialCode, decimal? Percent, decimal? MassMg, string? ExemptionCode = null, bool? ConflictFree = null);
public sealed record DeclarationRequest(
    DateOnly DeclaredOn, DateOnly ExpiresOn, DeclarationLineRequest[] Lines, string Source = DeclarationSources.Internal, string? RevisionScope = null, string? SupplierRef = null,
    string? MpnRef = null, decimal? ItemMassMg = null, string? Note = null)
{
    public DeclarationInput ToInput() => new(RevisionScope, Source, SupplierRef, MpnRef, ItemMassMg, DeclaredOn, ExpiresOn,
        [.. Lines.Select(l => new DeclarationLineInput(l.SubstanceCas, l.MaterialCode, l.Percent, l.MassMg, l.ExemptionCode, l.ConflictFree))], Note);
}
public sealed record CreateDeclarationRequest(string PartNumber, DeclarationRequest Declaration);
public sealed record UpdateDeclarationRequest(int Revision, DeclarationRequest Declaration);
public sealed record WithdrawDeclarationRequest(string Reason);
public sealed record LinkDocumentRequest(string DocumentNumber);

/// <summary>
/// REST for product compliance (ADR-0040): the substance master, materials and regulatory lists, the declarations of parts, the compliance of a part and the roll-up of an
/// assembly with its exports. Thin: every rule lives in the module services; the roll-up composes the BOM read paths in <see cref="ComplianceRollupService"/>.
/// </summary>
public static class ProductComplianceEndpoints
{
    public static void MapProductComplianceEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/compliance").RequireAuthorization(Policies.AnyPlmRole);
        static Subject Who(HttpContext c) => SubjectFactory.Require(c);
        static string? Corr(HttpContext c) => CorrelationId.Get(c);

        // ---- substances and materials
        g.MapGet("/substances", async (string? q, string? mineral, string? sort, string? dir, int? limit, int? offset, HttpContext c, RegulatoryCatalogService svc, CancellationToken ct) =>
            !RegulatoryCatalogService.SubstanceSortKeys.TryResolve(sort, dir, out var spec, out var error) ? (IResult)OutcomeResults.BadSort(error!)
            : (await svc.SearchSubstancesAsync(Who(c), new SubstanceSearch(q, mineral, limit ?? 50, offset ?? 0, spec), Corr(c), ct)).ToHttp());
        g.MapPost("/substances", async (SubstanceRequest r, HttpContext c, RegulatoryCatalogService svc, CancellationToken ct) =>
            (await svc.CreateSubstanceAsync(Who(c), new SubstanceInput(r.CasNumber, r.Name, r.EcNumber, r.Mineral), Corr(c), ct)).ToHttp(s => Results.Created($"/api/v1/compliance/substances/{s.Number}", s)));
        g.MapGet("/substances/{number}", async (string number, HttpContext c, RegulatoryCatalogService svc, CancellationToken ct) =>
            (await svc.GetSubstanceAsync(Who(c), number, Corr(c), ct)).ToHttp());
        g.MapPut("/substances/{number}", async (string number, SubstanceRequest r, HttpContext c, RegulatoryCatalogService svc, CancellationToken ct) =>
            (await svc.UpdateSubstanceAsync(Who(c), number, new SubstanceInput(r.CasNumber, r.Name, r.EcNumber, r.Mineral), r.Revision, Corr(c), ct)).ToHttp());

        g.MapGet("/materials", async (string? q, HttpContext c, RegulatoryCatalogService svc, CancellationToken ct) => (await svc.ListMaterialsAsync(Who(c), q, Corr(c), ct)).ToHttp());
        g.MapGet("/materials/{code}", async (string code, HttpContext c, RegulatoryCatalogService svc, CancellationToken ct) => (await svc.GetMaterialAsync(Who(c), code, Corr(c), ct)).ToHttp());
        g.MapPost("/materials", async (MaterialRequest r, HttpContext c, RegulatoryCatalogService svc, CancellationToken ct) =>
            (await svc.CreateMaterialAsync(Who(c), new MaterialInput(r.Code, r.Name, r.Description, [.. r.Composition.Select(x => new MaterialComponentInput(x.CasNumber, x.FractionPct))], r.Active), Corr(c), ct))
                .ToHttp(m => Results.Created($"/api/v1/compliance/materials/{m.Code}", m)));
        g.MapPut("/materials/{code}", async (string code, MaterialRequest r, HttpContext c, RegulatoryCatalogService svc, CancellationToken ct) =>
            (await svc.UpdateMaterialAsync(Who(c), code, new MaterialInput(r.Code, r.Name, r.Description, [.. r.Composition.Select(x => new MaterialComponentInput(x.CasNumber, x.FractionPct))], r.Active), r.Revision, Corr(c), ct)).ToHttp());

        // ---- regulatory lists: versions are drafted by administrators and published through the review (signatures) of the workflow API
        g.MapGet("/lists", async (HttpContext c, RegulatoryCatalogService svc, CancellationToken ct) => (await svc.ListsAsync(Who(c), Corr(c), ct)).ToHttp());
        g.MapPost("/lists", async (CreateListRequest r, HttpContext c, RegulatoryCatalogService svc, CancellationToken ct) =>
            (await svc.CreateListAsync(Who(c), new CreateListCommand(r.Code, r.Name, r.Kind, r.Description), Corr(c), ct)).ToHttp(l => Results.Created($"/api/v1/compliance/lists/{l.Code}", l)));
        g.MapGet("/lists/{code}", async (string code, HttpContext c, RegulatoryCatalogService svc, CancellationToken ct) => (await svc.GetListAsync(Who(c), code, Corr(c), ct)).ToHttp());
        g.MapGet("/lists/{code}/versions/{version:int}", async (string code, int version, HttpContext c, RegulatoryCatalogService svc, CancellationToken ct) =>
            (await svc.GetVersionAsync(Who(c), code, version, Corr(c), ct)).ToHttp());
        g.MapPost("/lists/{code}/versions", async (string code, DraftVersionRequest r, HttpContext c, RegulatoryCatalogService svc, CancellationToken ct) =>
            (await svc.CreateDraftAsync(Who(c), code, r.EffectiveFrom, r.SourceNote, Corr(c), ct)).ToHttp(v => Results.Created($"/api/v1/compliance/lists/{code}/versions/{v.Version}", v)));
        g.MapPut("/lists/{code}/versions/{version:int}", async (string code, int version, SaveVersionRequest r, HttpContext c, RegulatoryCatalogService svc, CancellationToken ct) =>
            (await svc.SaveDraftAsync(Who(c), code, version, r.EffectiveFrom, r.SourceNote, [.. r.Entries.Select(e => new RegEntryInput(e.CasNumber, e.ThresholdPct, e.Note))],
                [.. (r.Exemptions ?? []).Select(x => new RegExemptionInput(x.Code, x.Description, x.CasNumber, x.ExpiresOn))], r.Revision, Corr(c), ct)).ToHttp());
        g.MapDelete("/lists/{code}/versions/{version:int}", async (string code, int version, HttpContext c, RegulatoryCatalogService svc, CancellationToken ct) =>
            (await svc.DiscardDraftAsync(Who(c), code, version, Corr(c), ct)).ToHttp(_ => Results.NoContent()));
        g.MapGet("/regulations", async (DateOnly? asOf, HttpContext c, RegulatoryCatalogService svc, CancellationToken ct) => (await svc.ActiveRegulationsAsync(Who(c), asOf, Corr(c), ct)).ToHttp());

        // ---- declarations and a part's compliance
        g.MapGet("/parts/{partNumber}", async (string partNumber, HttpContext c, DeclarationService svc, CancellationToken ct) => (await svc.GetForPartAsync(Who(c), partNumber, Corr(c), ct)).ToHttp());
        g.MapGet("/declarations", async (string? part, string? status, string? source, string? supplier, string? sort, string? dir, int? limit, int? offset, HttpContext c, DeclarationService svc, CancellationToken ct) =>
            !DeclarationService.SortKeys.TryResolve(sort, dir, out var spec, out var error) ? (IResult)OutcomeResults.BadSort(error!)
            : (await svc.SearchAsync(Who(c), new DeclarationSearch(part, status, source, supplier, limit ?? 50, offset ?? 0, spec), Corr(c), ct)).ToHttp());
        g.MapPost("/declarations", async (CreateDeclarationRequest r, HttpContext c, DeclarationService svc, CancellationToken ct) =>
            (await svc.CreateAsync(Who(c), new CreateDeclarationCommand(r.PartNumber, r.Declaration.ToInput()), Corr(c), ct)).ToHttp(d => Results.Created($"/api/v1/compliance/declarations/{d.Summary.Number}", d)));
        g.MapGet("/declarations/{number}", async (string number, HttpContext c, DeclarationService svc, CancellationToken ct) => (await svc.GetAsync(Who(c), number, Corr(c), ct)).ToHttp());
        g.MapPut("/declarations/{number}", async (string number, UpdateDeclarationRequest r, HttpContext c, DeclarationService svc, CancellationToken ct) =>
            (await svc.UpdateAsync(Who(c), number, r.Declaration.ToInput(), r.Revision, Corr(c), ct)).ToHttp());
        g.MapPost("/declarations/{number}/verify", async (string number, HttpContext c, DeclarationService svc, CancellationToken ct) => (await svc.VerifyAsync(Who(c), number, Corr(c), ct)).ToHttp());
        g.MapPost("/declarations/{number}/withdraw", async (string number, WithdrawDeclarationRequest r, HttpContext c, DeclarationService svc, CancellationToken ct) =>
            (await svc.WithdrawAsync(Who(c), number, r.Reason, Corr(c), ct)).ToHttp());
        g.MapPost("/declarations/{number}/documents", async (string number, LinkDocumentRequest r, HttpContext c, DeclarationService svc, CancellationToken ct) =>
            (await svc.LinkDocumentAsync(Who(c), number, r.DocumentNumber, Corr(c), ct)).ToHttp());
        g.MapDelete("/declarations/{number}/documents/{documentNumber}", async (string number, string documentNumber, HttpContext c, DeclarationService svc, CancellationToken ct) =>
            (await svc.UnlinkDocumentAsync(Who(c), number, documentNumber, Corr(c), ct)).ToHttp());
        g.MapGet("/summary", async (HttpContext c, DeclarationService svc, CancellationToken ct) =>
            AccessPolicyHasRead(Who(c)) ? Results.Ok(await svc.CountsAsync(Who(c), ct)) : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden"));

        // ---- the roll-up of an assembly and its exports
        g.MapGet("/rollup", async (string mode, string number, DateOnly? asOf, string? versions, HttpContext c, ComplianceRollupService svc, CancellationToken ct) =>
            !TryVersions(versions, out var pins) ? Outcome<ComplianceReport>.Invalid(["versions is a comma-separated list of code:version, for example rohs:2."]).ToHttp()
            : (await svc.ReportAsync(Who(c), mode, number, pins, asOf, Corr(c), ct)).ToHttp());
        g.MapGet("/rollup/export", async (string mode, string number, string format, DateOnly? asOf, string? versions, HttpContext c, ComplianceRollupService svc, CancellationToken ct) =>
            !TryVersions(versions, out var pins) ? Outcome<ExportFile>.Invalid(["versions is a comma-separated list of code:version, for example rohs:2."]).ToHttp()
            : (await svc.ExportAsync(Who(c), mode, number, format, pins, asOf, Corr(c), ct)).ToHttp(f => Results.File(System.Text.Encoding.UTF8.GetBytes(f.Content), f.ContentType, f.FileName)));

        // The same download for the Blazor UI, authenticated by the session cookie, so a page uses a plain link and no token appears in a URL.
        app.MapGet("/downloads/compliance/export", async (string mode, string number, string format, HttpContext c, ComplianceRollupService svc, CancellationToken ct) =>
            (await svc.ExportAsync(Who(c), mode, number, format, null, null, Corr(c), ct)).ToHttp(f => Results.File(System.Text.Encoding.UTF8.GetBytes(f.Content), f.ContentType, f.FileName)))
            .RequireAuthorization(Policies.AnyPlmRole);
    }

    private static bool AccessPolicyHasRead(Subject s) => Keel.Security.AccessPolicy.HasRoleFor(s, Keel.Security.PlmAction.Read);

    /// <summary>"rohs:2,reach_svhc:1" into code and version; false when it is not that shape.</summary>
    internal static bool TryVersions(string? text, out IReadOnlyDictionary<string, int>? pins)
    {
        pins = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(':');
            if (bits.Length != 2 || !ComplianceRules.IsValidListCode(bits[0]) || !int.TryParse(bits[1], NumberStyles.None, CultureInfo.InvariantCulture, out var v) || v < 1) return false;
            map[bits[0]] = v;
        }
        pins = map;
        return true;
    }
}
