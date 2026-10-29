using Keel.Api.Infrastructure;
using Keel.Core;
using Keel.Requirements;

namespace Keel.Api.Endpoints;

public sealed record CreateSpecRequest(string Name, MarkingsDto Markings, string? Product = null, string? Description = null);
public sealed record RequirementInputDto(
    string Title, string Text, string Type, string Priority, string Method, string? Rationale = null, string Status = "draft", string? ParentKey = null, string? BeforeKey = null,
    MarkingsDto? Markings = null, bool ConfirmRaiseMarkings = false);
public sealed record EditOpDto(string Op, string? Key, RequirementInputDto? Input);
public sealed record EditRequest(int BaseIteration, EditOpDto[] Ops, string? Comment = null);
public sealed record SpecMarkingsRequest(MarkingsDto Markings, string Reason, bool ConfirmRaise = false);
public sealed record SpecReasonRequest(string Reason);
public sealed record AddLinkRequest(string LinkType, string? TargetSpec = null, string? TargetKey = null, string? TargetNumber = null);
public sealed record VerificationRequest(string Result, string? Method = null, string? Notes = null, string? WaiverReason = null, string[]? EvidenceDocuments = null, bool Formal = false, string? SignatureMeaning = null);
public sealed record SaveLinkTypeRequest(string Label, string ReverseLabel, string TargetKind, string CoverageRole = "none", bool IsActive = true, int SortOrder = 100, string? Reason = null);

/// <summary>
/// REST for requirement specifications and traceability (ADR-0038), documented in /openapi/requirements.json. Thin: every rule lives in <see cref="RequirementService"/>.
/// Requirement text is marked data: it appears only in responses to callers the access policy lets read it.
/// </summary>
public static class RequirementEndpoints
{
    public const string UiExportRoute = "/ui/requirements/{number}/export";

    private static RequirementInput? ToInput(RequirementInputDto? d, out string? error)
    {
        error = null;
        if (d is null) return null;
        Markings? markings = null;
        if (d.Markings is not null)
        {
            var parsed = d.Markings.ToMarkings();
            if (!parsed.IsSuccess) { error = string.Join(" ", parsed.Errors); return null; }
            markings = parsed.Value;
        }
        return new RequirementInput(d.Title, d.Text, d.Type, d.Priority, d.Method, d.Rationale, d.Status, d.ParentKey, d.BeforeKey, markings, d.ConfirmRaiseMarkings);
    }

    public static void MapRequirementEndpoints(this IEndpointRouteBuilder app)
    {
        static Subject Who(HttpContext c) => SubjectFactory.Require(c);
        static string? Corr(HttpContext c) => CorrelationId.Get(c);
        var g = app.MapGroup("/api/v1/requirements").RequireAuthorization(Policies.AnyPlmRole).WithTags("Requirements");

        // ---- lists and administrator data (before the {number} routes)
        g.MapGet("", async (string? q, string? state, string? program, string? sort, string? dir, int? limit, int? offset, HttpContext c, RequirementService svc, CancellationToken ct) =>
            !RequirementService.SortKeys.TryResolve(sort, dir, out var spec, out var error) ? OutcomeResults.BadSort(error!)
            : (await svc.SearchAsync(Who(c), new SpecSearch(q, state, program, limit ?? 50, offset ?? 0, spec), Corr(c), ct)).ToHttp()).WithSummary("Specifications you may read, with the number of requirements you may read.");
        g.MapPost("", async (CreateSpecRequest req, HttpContext c, RequirementService svc, CancellationToken ct) =>
        {
            var m = req.Markings.ToMarkings();
            if (!m.IsSuccess) return Outcome<SpecDetail>.Invalid(m.Errors).ToHttp();
            return (await svc.CreateAsync(Who(c), new CreateSpecCommand(req.Name, req.Product, req.Description, m.Value), Corr(c), ct)).ToHttp(d => Results.Created($"/api/v1/requirements/{d.Summary.Number}", d));
        }).WithSummary("Creates a specification (revision A, In Work).");
        g.MapGet("/link-types", async (bool? all, RequirementService svc, CancellationToken ct) => Results.Ok(await svc.ListLinkTypesAsync(all == true, ct))).WithSummary("Traceability link types.");
        g.MapPut("/link-types/{code}", async (string code, SaveLinkTypeRequest req, HttpContext c, RequirementService svc, CancellationToken ct) =>
            (await svc.SaveLinkTypeAsync(Who(c), new SaveLinkTypeCommand(code, req.Label, req.ReverseLabel, req.TargetKind, req.CoverageRole, req.IsActive, req.SortOrder, req.Reason), Corr(c), ct)).ToHttp())
            .WithSummary("Creates or updates a link type (administrators).");
        g.MapPost("/suspect-links/sweep", async (HttpContext c, RequirementService svc, CancellationToken ct) => (await svc.SweepAsync(Who(c), Corr(c), ct)).ToHttp())
            .WithSummary("Marks links whose part or document has a newer revision as suspect (administrators).");
        g.MapGet("/trace/{kind}/{key}", async (string kind, string key, HttpContext c, RequirementService svc, CancellationToken ct) => (await svc.GetObjectTraceAsync(Who(c), kind, key, Corr(c), ct)).ToHttp())
            .WithSummary("The requirements that trace to a part, document or change (kind is part, document or change), with link and verification state.");

        // ---- one specification
        g.MapGet("/{number}", async (string number, string? revision, HttpContext c, RequirementService svc, CancellationToken ct) => (await svc.GetAsync(Who(c), number, revision, Corr(c), ct)).ToHttp())
            .WithSummary("A specification: revisions, state and permissions.");
        g.MapGet("/{number}/requirements", async (string number, string? revision, HttpContext c, RequirementService svc, CancellationToken ct) => (await svc.GetRequirementsAsync(Who(c), number, revision, Corr(c), ct)).ToHttp())
            .WithSummary("The requirements of a revision in outline order; a requirement you may not read is a placeholder with its place only.");
        g.MapGet("/{number}/requirements/{key}", async (string number, string key, HttpContext c, RequirementService svc, CancellationToken ct) => (await svc.GetRequirementAsync(Who(c), number, key, Corr(c), ct)).ToHttp())
            .WithSummary("One requirement of the latest revision.");
        g.MapPost("/{number}/edit", async (string number, EditRequest req, HttpContext c, RequirementService svc, CancellationToken ct) =>
        {
            var ops = new List<EditOp>();
            foreach (var o in req.Ops ?? [])
            {
                var input = ToInput(o.Input, out var error);
                if (error is not null) return Outcome<EditResult>.Invalid([error]).ToHttp();
                ops.Add(new EditOp(o.Op, o.Key, input));
            }
            return (await svc.EditAsync(Who(c), number, new EditCommand(req.BaseIteration, req.Comment, ops), Corr(c), ct)).ToHttp();
        }).WithSummary("Applies add, update and move changes as one new iteration; all or nothing. Requirements are withdrawn, never deleted.");
        g.MapPut("/{number}/markings", async (string number, SpecMarkingsRequest req, HttpContext c, RequirementService svc, CancellationToken ct) =>
        {
            var m = req.Markings.ToMarkings();
            return m.IsSuccess ? (await svc.SetMarkingsAsync(Who(c), number, m.Value, req.Reason, req.ConfirmRaise, Corr(c), ct)).ToHttp() : Outcome<SpecDetail>.Invalid(m.Errors).ToHttp();
        }).WithSummary("Changes the markings of the specification with a reason.");
        g.MapPost("/{number}/revise", async (string number, SpecReasonRequest req, HttpContext c, RequirementService svc, CancellationToken ct) => (await svc.ReviseAsync(Who(c), number, req.Reason, Corr(c), ct)).ToHttp())
            .WithSummary("Starts the next revision of a Released specification.");
        g.MapPost("/{number}/obsolete", async (string number, SpecReasonRequest req, HttpContext c, RequirementService svc, CancellationToken ct) => (await svc.ObsoleteAsync(Who(c), number, req.Reason, Corr(c), ct)).ToHttp())
            .WithSummary("Makes the specification obsolete.");

        // ---- links
        g.MapGet("/{number}/requirements/{key}/links", async (string number, string key, HttpContext c, RequirementService svc, CancellationToken ct) => (await svc.GetLinksAsync(Who(c), number, key, Corr(c), ct)).ToHttp())
            .WithSummary("Links of a requirement in both directions; an end you may not read is a placeholder.");
        g.MapPost("/{number}/requirements/{key}/links", async (string number, string key, AddLinkRequest req, HttpContext c, RequirementService svc, CancellationToken ct) =>
            (await svc.AddLinkAsync(Who(c), number, key, new AddLinkCommand(req.LinkType, req.TargetSpec, req.TargetKey, req.TargetNumber), Corr(c), ct)).ToHttp(v => Results.Created($"/api/v1/requirements/{number}/requirements/{key}/links", v)))
            .WithSummary("Adds a traceability link from a requirement.");
        g.MapDelete("/{number}/links/{id:guid}", async (string number, Guid id, HttpContext c, RequirementService svc, CancellationToken ct) => (await svc.RemoveLinkAsync(Who(c), number, id, Corr(c), ct)).ToHttp(_ => Results.NoContent()))
            .WithSummary("Removes a link (it stays in the audit log).");
        g.MapPost("/{number}/links/{id:guid}/review", async (string number, Guid id, HttpContext c, RequirementService svc, CancellationToken ct) => (await svc.ReviewLinkAsync(Who(c), number, id, Corr(c), ct)).ToHttp())
            .WithSummary("Reviews a suspect link: it is current again.");

        // ---- verification
        g.MapGet("/{number}/requirements/{key}/verifications", async (string number, string key, HttpContext c, RequirementService svc, CancellationToken ct) => (await svc.GetVerificationsAsync(Who(c), number, key, Corr(c), ct)).ToHttp())
            .WithSummary("Verification records of a requirement, newest first, and the state of its current content.");
        g.MapPost("/{number}/requirements/{key}/verifications", async (string number, string key, VerificationRequest req, HttpContext c, RequirementService svc, CancellationToken ct) =>
            (await svc.RecordVerificationAsync(Who(c), number, key, new RecordVerificationCommand(req.Result, req.Method, req.Notes, req.WaiverReason, req.EvidenceDocuments, req.Formal, req.SignatureMeaning), Corr(c), ct))
            .ToHttp(v => Results.Created($"/api/v1/requirements/{number}/requirements/{key}/verifications", v)))
            .WithSummary("Records a verification; a formal one needs the approver role and a fresh authentication (401 with a step-up challenge otherwise).");

        // ---- coverage, trace and matrix
        g.MapGet("/{number}/coverage", async (string number, string? revision, string? baseline, HttpContext c, RequirementService svc, CancellationToken ct) => (await svc.GetCoverageAsync(Who(c), number, revision, baseline, Corr(c), ct)).ToHttp())
            .WithSummary("Allocation and verification coverage and the gaps, over what you may read; optionally within a baseline.");
        g.MapGet("/{number}/trace", async (string number, string? revision, HttpContext c, RequirementService svc, CancellationToken ct) => (await svc.GetTraceAsync(Who(c), number, revision, Corr(c), ct)).ToHttp())
            .WithSummary("Requirements, links and verification state of a revision (what the matrix and the impact graph are drawn from).");
        g.MapGet("/{number}/matrix", async (string number, string linkType, string? revision, HttpContext c, RequirementService svc, CancellationToken ct) => (await svc.GetMatrixAsync(Who(c), number, linkType, revision, Corr(c), ct)).ToHttp())
            .WithSummary("The traceability matrix of one link type.");

        // ---- import and export
        g.MapPost("/{number}/import", async (string number, string? format, bool? dryRun, string? digest, HttpContext c, RequirementService svc, CancellationToken ct) =>
        {
            using var reader = new StreamReader(c.Request.Body, System.Text.Encoding.UTF8);
            var buffer = new char[RequirementIo.MaxBytes + 1];
            var read = await reader.ReadBlockAsync(buffer, ct);
            if (read > RequirementIo.MaxBytes) return Outcome<ImportPlan>.Invalid(["The file is too large."]).ToHttp();
            return (await svc.ImportAsync(Who(c), number, format ?? ImportFormats.Csv, new string(buffer, 0, read), dryRun != false, digest, Corr(c), ct)).ToHttp();
        }).Accepts<string>("text/plain", "text/csv", "application/xml")
          .WithSummary("Reads a file (format csv, excel or reqif; the body is the file). dryRun defaults to true and reports the plan with its digest; apply with dryRun=false and that digest.");
        g.MapGet("/{number}/export", async (string number, string? format, string? revision, HttpContext c, RequirementService svc, CancellationToken ct) =>
            (await svc.ExportAsync(Who(c), number, revision, format ?? ImportFormats.Csv, Corr(c), ct)).ToHttp(Download))
            .WithSummary("The readable requirements of a revision as csv, excel (spreadsheet CSV) or reqif, with the marking banner of what the file holds.");

        // The UI download goes through the cookie session (the circuit cannot stream a file).
        app.MapGet(UiExportRoute, async (string number, string? format, string? revision, HttpContext c, RequirementService svc, CancellationToken ct) =>
            (await svc.ExportAsync(Who(c), number, revision, format ?? ImportFormats.Csv, Corr(c), ct)).ToHttp(Download)).RequireAuthorization(Policies.AnyPlmRole).ExcludeFromDescription();
    }

    private static IResult Download(ExportFile f)
    {
        var contentType = f.ContentType + "; charset=utf-8";
        return Results.File(f.Content, contentType, f.FileName);
    }
}
