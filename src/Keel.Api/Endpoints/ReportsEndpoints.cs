using System.Text.Json.Nodes;
using Keel.Api.Infrastructure;
using Keel.Core;
using Keel.Core.Reports;
using Keel.Reports;
using Keel.Reports.Signing;

namespace Keel.Api.Endpoints;

public sealed record GeneratePackageRequest(string Kind, string Key);
public sealed record SigningPolicyRequest(bool Required);
public sealed record DevelopmentIdentityRequest(string? Name);
public sealed record MassPreviewRequest(string ObjectKind, string[] Keys, string Operation, JsonObject? Parameters, string? ChangeNumber = null);
public sealed record ReportRunRequest(string Dataset, ReportSpec Spec);
public sealed record SaveReportRequest(string Name, string Dataset, ReportSpec Spec, bool Shared = false);

/// <summary>REST for release packages, BOM exchange, mass change and the report builder (ADR-0044). Thin: every rule is in Keel.Reports. Documented in /openapi/reports.json.</summary>
public static class ReportsEndpoints
{
    public const string UiPackageRoute = "/ui/output/packages/{number}";
    public const string UiBomExportRoute = "/ui/output/bom-export";
    public const string UiBomTemplateRoute = "/ui/output/bom-template";
    public const string UiMassTemplateRoute = "/ui/output/mass-template";
    public const string UiReportExportRoute = "/ui/output/report-export";
    public const int MaxImportBytes = 5 * 1024 * 1024;

    private static IResult MassTemplate(string kind, string operation) =>
        MassKinds.All.Contains(kind) && MassChangeService.AllOperations.Contains(operation)
            ? Results.File(MassUpload.Template(kind, operation), "text/csv; charset=utf-8", $"mass-change-{kind}-{operation}.csv")
            : Outcome<string>.Invalid(["Choose a kind of object and an operation the page offers."]).ToHttp();

    public static void MapReportsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/output").RequireAuthorization(Policies.AnyPlmRole).WithTags("Output and bulk work");
        static Subject Who(HttpContext c) => SubjectFactory.Require(c);
        static string? Corr(HttpContext c) => CorrelationId.Get(c);

        // ---- release packages
        g.MapGet("/packages", async (string kind, string key, HttpContext c, ReleasePackageService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(Who(c), kind, key, ct)))
            .WithSummary("The release packages of a part, ECN or baseline that you may read completely.");
        g.MapPost("/packages", async (GeneratePackageRequest r, HttpContext c, ReleasePackageService svc, CancellationToken ct) =>
            (await svc.GenerateAsync(Who(c), r.Kind, r.Key, Corr(c), ct)).ToHttp(v => Results.Created($"/api/v1/output/packages/{v.Number}/download", v)))
            .WithSummary("Generates a release package PDF. Refused (403) when any object it needs is unreadable; the package is never redacted.");
        g.MapGet("/packages/{number}/download", PackageDownload).WithSummary("Downloads a package. 404 unless you may read every object it includes.");
        app.MapGet(UiPackageRoute, PackageDownload).RequireAuthorization(Policies.AnyPlmRole).ExcludeFromDescription();

        // ---- package signing (9.5, ADR-0051)
        g.MapPost("/packages/verify", async (HttpContext c, PackageSigningService svc, CancellationToken ct) =>
        {
            using var ms = new MemoryStream();
            var buffer = new byte[81920]; int n;
            while ((n = await c.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (ms.Length + n > PdfSignatureVerifier.MaxBytes) return Outcome<VerifyResult>.Invalid(["The file is larger than 64 MB."]).ToHttp();
                ms.Write(buffer, 0, n);
            }
            return Results.Ok(await svc.VerifyAsync(Who(c), ms.ToArray(), Corr(c), ct));
        }).Accepts<byte[]>("application/pdf", "application/octet-stream")
          .WithSummary("Verifies the digital signature of a PDF you upload (the body is the file): byte ranges, integrity, signer, signing time, chain to a configured trust anchor. Reports tampering; names the Keel package only when you may read all of it.");
        var sg = g.MapGroup("/signing").RequireAuthorization(Policies.Admin);
        sg.MapGet("/identities", async (HttpContext c, PackageSigningService svc, CancellationToken ct) => (await svc.ListIdentitiesAsync(Who(c), ct)).ToHttp())
            .WithSummary("Administrator: the signing identities (certificates and key references; never a private key or password).");
        sg.MapPost("/identities", async (AddSigningIdentity r, HttpContext c, PackageSigningService svc, CancellationToken ct) =>
            (await svc.AddIdentityAsync(Who(c), r, Corr(c), ct)).ToHttp(v => Results.Created($"/api/v1/output/signing/identities/{v.Id}", v)))
            .WithSummary("Administrator: registers a certificate bundle (signing certificate first, root last) and where its private key is. The key is loaded and proven to match before anything is stored.");
        sg.MapPost("/identities/development", async (DevelopmentIdentityRequest r, HttpContext c, PackageSigningService svc, CancellationToken ct) =>
            (await svc.GenerateDevelopmentIdentityAsync(Who(c), r.Name, Corr(c), ct)).ToHttp(v => Results.Created($"/api/v1/output/signing/identities/{v.Id}", v)))
            .WithSummary("DEVELOPMENT ONLY, administrator: generates a throw-away certificate chain and registers it. 403 on hosts that are not development or demo hosts.");
        sg.MapPost("/identities/{id:guid}/activate", async (Guid id, HttpContext c, PackageSigningService svc, CancellationToken ct) => (await svc.ActivateAsync(Who(c), id, Corr(c), ct)).ToHttp())
            .WithSummary("Administrator: makes this the identity that signs new packages (the previous one becomes standby).");
        sg.MapPost("/identities/{id:guid}/retire", async (Guid id, HttpContext c, PackageSigningService svc, CancellationToken ct) => (await svc.RetireAsync(Who(c), id, Corr(c), ct)).ToHttp())
            .WithSummary("Administrator: retires an identity for good. Packages it signed still verify.");
        sg.MapGet("/policy", async (PackageSigningService svc, CancellationToken ct) => Results.Ok(await svc.GetPolicyAsync(ct))).WithSummary("Administrator: whether signing is required and whether an identity is active.");
        sg.MapPut("/policy", async (SigningPolicyRequest r, HttpContext c, PackageSigningService svc, CancellationToken ct) => (await svc.SetRequiredAsync(Who(c), r.Required, Corr(c), ct)).ToHttp())
            .WithSummary("Administrator: require signing. While required and no identity is active, packages are refused.");

        // ---- BOM exchange
        g.MapGet("/bom/export", BomExport).WithSummary("Exports the resolved EBOM or MBOM as .xlsx or CSV (kind, number, variant, spec, depth, context, serial, lot, date, format).");
        app.MapGet(UiBomExportRoute, BomExport).RequireAuthorization(Policies.AnyPlmRole).ExcludeFromDescription();
        g.MapGet("/bom/template.xlsx", () => Results.File(BomExchangeService.Template(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "bom-changes-template.xlsx")).WithSummary("The workbook template for BOM changes.");
        app.MapGet(UiBomTemplateRoute, () => Results.File(BomExchangeService.Template(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "bom-changes-template.xlsx")).RequireAuthorization(Policies.AnyPlmRole).ExcludeFromDescription();
        g.MapPost("/bom/imports", async (string? fileName, HttpContext c, BomExchangeService svc, CancellationToken ct) =>
        {
            using var ms = new MemoryStream();
            var buffer = new byte[81920]; int n;
            while ((n = await c.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (ms.Length + n > MaxImportBytes) return Outcome<ImportProposal>.Invalid(["The file is larger than 5 MB."]).ToHttp();
                ms.Write(buffer, 0, n);
            }
            return (await svc.CreateProposalAsync(Who(c), fileName ?? "bom-changes.xlsx", ms.ToArray(), Corr(c), ct)).ToHttp(p => Results.Created($"/api/v1/output/bom/imports/{p.Number}", p));
        }).Accepts<byte[]>("application/octet-stream", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "text/csv")
          .WithSummary("Reads a workbook or CSV (the body is the file) into a reviewable proposal with row-level errors. Nothing is changed.");
        g.MapGet("/bom/imports", async (HttpContext c, BomExchangeService svc, CancellationToken ct) => Results.Ok(await svc.ListProposalsAsync(Who(c), ct))).WithSummary("Your import proposals.");
        g.MapGet("/bom/imports/{number}", async (string number, HttpContext c, BomExchangeService svc, CancellationToken ct) => (await svc.GetProposalAsync(Who(c), number, ct)).ToHttp());
        g.MapPost("/bom/imports/{number}/apply", async (string number, HttpContext c, BomExchangeService svc, CancellationToken ct) => (await svc.ApplyAsync(Who(c), number, Corr(c), ct)).ToHttp())
            .WithSummary("Applies a clean proposal through the part and BOM services (check-out, edit, check-in). Repeating it skips assemblies already changed.");
        g.MapPost("/bom/imports/{number}/discard", async (string number, HttpContext c, BomExchangeService svc, CancellationToken ct) => (await svc.DiscardAsync(Who(c), number, Corr(c), ct)).ToHttp());

        // ---- mass change
        g.MapPost("/mass/preview", async (MassPreviewRequest r, HttpContext c, MassChangeService svc, CancellationToken ct) =>
            (await svc.PreviewAsync(Who(c), new MassRequest(r.ObjectKind, r.Keys ?? [], r.Operation, r.Parameters, r.ChangeNumber), Corr(c), ct)).ToHttp(v => Results.Created($"/api/v1/output/mass/{v.Number}", v)))
            .WithSummary("Shows which objects an operation would change and which are refused, and why. Changes nothing.");
        g.MapGet("/mass/select", async (string kind, string selector, string value, HttpContext c, MassChangeService svc, CancellationToken ct) =>
            (await svc.SelectAsync(Who(c), kind, selector, value, Corr(c), ct)).ToHttp())
            .WithSummary("Fills a selection: the assemblies that directly use a component (kind assembly, selector where_used), those you may read. Changes nothing.");
        g.MapPost("/mass/upload", async (string? fileName, string? kind, string? operation, string? changeNumber, HttpContext c, MassChangeService svc, CancellationToken ct) =>
        {
            using var ms = new MemoryStream();
            var buffer = new byte[81920]; int n;
            while ((n = await c.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (ms.Length + n > MaxImportBytes) return Outcome<MassPreview>.Invalid(["The file is larger than 5 MB."]).ToHttp();
                ms.Write(buffer, 0, n);
            }
            return (await svc.PreviewUploadAsync(Who(c), new MassUploadRequest(fileName ?? "mass-change.csv", ms.ToArray(), kind, operation, null, changeNumber), Corr(c), ct)).ToHttp(v => Results.Created($"/api/v1/output/mass/{v.Number}", v));
        }).Accepts<byte[]>("application/octet-stream", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "text/csv")
          .WithSummary("Reads a CSV or workbook (the body is the file) into a mass change preview: the file names the objects and, per row, their values. Changes nothing until you apply.");
        g.MapGet("/mass/template.csv", (string kind, string operation) => MassTemplate(kind, operation)).WithSummary("An empty CSV template (header row only) for a kind of object and an operation.");
        app.MapGet(UiMassTemplateRoute, (string kind, string operation) => MassTemplate(kind, operation)).RequireAuthorization(Policies.AnyPlmRole).ExcludeFromDescription();
        g.MapGet("/mass", async (HttpContext c, MassChangeService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(Who(c), ct))).WithSummary("Your mass change numbers, newest first.");
        g.MapGet("/mass/{number}", async (string number, HttpContext c, MassChangeService svc, CancellationToken ct) => (await svc.GetAsync(Who(c), number, ct)).ToHttp());
        g.MapPost("/mass/{number}/apply", async (string number, HttpContext c, MassChangeService svc, CancellationToken ct) => (await svc.ApplyAsync(Who(c), number, Corr(c), ct)).ToHttp())
            .WithSummary("Applies a previewed operation object by object through the normal services. 409 reports partial failure; apply again to retry only the failed objects.");
        g.MapPost("/mass/{number}/undo", async (string number, HttpContext c, MassChangeService svc, CancellationToken ct) => (await svc.UndoAsync(Who(c), number, Corr(c), ct)).ToHttp())
            .WithSummary("Undoes a fully applied reversible operation.");

        // ---- report builder
        g.MapGet("/reports/datasets", (ReportBuilderService svc) => Results.Ok(svc.Catalog())).WithSummary("The datasets a report can be built on, with their columns.");
        g.MapGet("/reports/definitions", async (HttpContext c, ReportBuilderService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(Who(c), ct))).WithSummary("Your saved reports and those shared with everyone.");
        g.MapPost("/reports/definitions", async (SaveReportRequest r, HttpContext c, ReportBuilderService svc, CancellationToken ct) =>
            (await svc.SaveAsync(Who(c), null, r.Name, r.Dataset, r.Spec, r.Shared, Corr(c), ct)).ToHttp(v => Results.Created($"/api/v1/output/reports/definitions/{v.Id}", v)));
        g.MapPut("/reports/definitions/{id:guid}", async (Guid id, SaveReportRequest r, HttpContext c, ReportBuilderService svc, CancellationToken ct) =>
            (await svc.SaveAsync(Who(c), id, r.Name, r.Dataset, r.Spec, r.Shared, Corr(c), ct)).ToHttp());
        g.MapDelete("/reports/definitions/{id:guid}", async (Guid id, HttpContext c, ReportBuilderService svc, CancellationToken ct) => (await svc.DeleteAsync(Who(c), id, Corr(c), ct)).ToHttp());
        g.MapPost("/reports/run", async (ReportRunRequest r, HttpContext c, ReportBuilderService svc, CancellationToken ct) => (await svc.RunAsync(Who(c), r.Dataset, r.Spec, Corr(c), ct)).ToHttp())
            .WithSummary("Runs a report as you: only rows you may read, the aggregate banner of the rows shown, counts per group.");
        g.MapPost("/reports/export", async (string? format, ReportRunRequest r, HttpContext c, ReportBuilderService svc, CancellationToken ct) =>
            (await svc.ExportAsync(Who(c), r.Dataset, r.Spec, format ?? "csv", Corr(c), ct)).ToHttp(Download)).WithSummary("Runs a report and returns it as csv, xlsx or pdf, with the banner on the file.");
        app.MapGet(UiReportExportRoute, async (string dataset, string? format, string? columns, string? text, string? param, string? sort, string? dir, string? group, string[]? f, HttpContext c, ReportBuilderService svc, CancellationToken ct) =>
        {
            var filters = (f ?? []).Select(x => x.Split('|', 3)).Where(p => p.Length == 3).Select(p => new DatasetFilter(p[0], p[1], p[2])).ToList();
            var spec = new ReportSpec([.. (columns ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)], filters, text, param, sort, dir == "desc", group);
            return (await svc.ExportAsync(Who(c), dataset, spec, format ?? "csv", Corr(c), ct)).ToHttp(Download);
        }).RequireAuthorization(Policies.AnyPlmRole).ExcludeFromDescription();
    }

    private static IResult Download(ExportFile f) => Results.File(f.Content, f.ContentType, f.FileName);

    private static async Task<IResult> PackageDownload(string number, HttpContext c, ReleasePackageService svc, CancellationToken ct)
    {
        var r = await svc.DownloadAsync(SubjectFactory.Require(c), number, CorrelationId.Get(c), ct);
        if (!r.IsOk) return r.ToHttp();
        c.Response.Headers["X-Keel-Markings"] = Uri.EscapeDataString(r.Value!.Banner);
        c.Response.Headers["X-Keel-Sha256"] = r.Value.Sha256;
        return Results.File(r.Value.Content, "application/pdf", r.Value.FileName);
    }

    private static async Task<IResult> BomExport(string kind, string number, string? variant, string? spec, int? depth, string? context, long? serial, long? lot, DateOnly? date, string? format, HttpContext c, BomExchangeService svc, CancellationToken ct)
    {
        var r = await svc.ExportAsync(SubjectFactory.Require(c), new BomExchangeRequest(kind, number, variant, spec ?? "released", depth, context, serial, lot, date), format ?? "xlsx", CorrelationId.Get(c), ct);
        if (r.IsOk) c.Response.Headers["X-Keel-Markings"] = Uri.EscapeDataString(r.Value!.Banner);
        return r.ToHttp(Download);
    }
}
