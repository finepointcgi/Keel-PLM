using System.Text.Json.Nodes;
using Keel.Admin;
using Keel.Api.Infrastructure;
using Keel.Core;

namespace Keel.Api.Endpoints;

public sealed record ReasonRequest(string? Reason);
public sealed record RetireRequest(bool Retired, string? Reason);
public sealed record ActiveRequest(bool Active, string? Reason);
public sealed record RefItemRequest(string Label, int SortOrder = 0, string? LifecycleFamily = null);
public sealed record LifecycleDraftRequest(string? Name, IReadOnlyList<LifecycleStateDef> States, IReadOnlyList<LifecycleTransitionDef> Transitions, string? ChangeNote = null);
public sealed record CreateLifecycleRequest(string Family, string Name, string ObjectType, IReadOnlyList<LifecycleStateDef> States, IReadOnlyList<LifecycleTransitionDef> Transitions);
public sealed record MigrateRequest(int FromVersion, int ToVersion, Dictionary<string, string> Mapping, string? Reason);
public sealed record NumberingRequest(string Prefix, int Width, string? Reason);
public sealed record AdvanceRequest(long NextValue, string? Reason);
public sealed record PinRequest(string? TemplateId);
public sealed record ClassUpdateRequest(string Name, string? Description, int SortOrder = 0);
public sealed record MoveRequest(string? NewParentCode, string? Reason);
public sealed record ClassStatusRequest(bool Active, string? Reason);
public sealed record AssignClassRequest(string? ClassCode, JsonObject? Attributes);

/// <summary>
/// Administration API (increment 2.2). Everything under <c>/api/v1/admin</c> needs the plm-admin role (the policy denies and audits; the services check the role
/// again). The classification tree, a part's classification, the faceted search and the reference lists are readable by every PLM role.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin").RequireAuthorization(Policies.Admin);
        static Subject Who(HttpContext c) => SubjectFactory.Require(c);
        static string? Corr(HttpContext c) => CorrelationId.Get(c);

        // ---- attributes
        admin.MapGet("/attributes/{objectType}", (string objectType, bool? retired, HttpContext c, AttributeAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.ListAsync(Who(c), objectType, retired == true, ct)).ToHttp()));
        admin.MapPost("/attributes/{objectType}", (string objectType, AttributeDefinitionInput body, HttpContext c, AttributeAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.CreateAsync(Who(c), objectType, body, Corr(c), ct)).ToHttp(v => Results.Created($"/api/v1/admin/attributes/{objectType}", v))));
        admin.MapPut("/attributes/{objectType}/{name}", (string objectType, string name, AttributeDefinitionInput body, HttpContext c, AttributeAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.UpdateAsync(Who(c), objectType, body with { Name = name }, Corr(c), ct)).ToHttp()));
        admin.MapPost("/attributes/{objectType}/{name}/retire", (string objectType, string name, RetireRequest body, HttpContext c, AttributeAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.SetRetiredAsync(Who(c), objectType, name, body.Retired, body.Reason, Corr(c), ct)).ToHttp()));

        // ---- lifecycles
        admin.MapGet("/lifecycles", (HttpContext c, LifecycleAdminService svc, CancellationToken ct) => Guard(async () => (await svc.ListFamiliesAsync(Who(c), ct)).ToHttp()));
        admin.MapGet("/lifecycles/{family}", (string family, int? version, HttpContext c, LifecycleAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.GetAsync(Who(c), family, version, ct)).ToHttp()));
        admin.MapPost("/lifecycles", (CreateLifecycleRequest body, HttpContext c, LifecycleAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.CreateFamilyAsync(Who(c), body.Family, body.Name, body.ObjectType,
                new LifecycleDefinition(body.Family, body.Name, body.ObjectType, 1, "draft", body.States, body.Transitions), Corr(c), ct))
                .ToHttp(v => Results.Created($"/api/v1/admin/lifecycles/{body.Family}", v))));
        admin.MapPost("/lifecycles/{family}/validate", (string family, LifecycleDraftRequest body, HttpContext c, LifecycleAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.ValidateAsync(Who(c), family, new LifecycleDefinition(family, body.Name ?? "", "", 0, "draft", body.States, body.Transitions), ct)).ToHttp()));
        admin.MapPost("/lifecycles/{family}/versions", (string family, LifecycleDraftRequest body, HttpContext c, LifecycleAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.PublishVersionAsync(Who(c), family, new LifecycleDefinition(family, body.Name ?? "", "", 0, "draft", body.States, body.Transitions), body.ChangeNote, Corr(c), ct)).ToHttp()));
        admin.MapGet("/lifecycles/{family}/migration", (string family, int from, int to, HttpContext c, LifecycleAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.PreviewMigrationAsync(Who(c), family, from, to, ct)).ToHttp()));
        admin.MapPost("/lifecycles/{family}/migrate", (string family, MigrateRequest body, HttpContext c, LifecycleAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.MigrateObjectsAsync(Who(c), family, body.FromVersion, body.ToVersion, body.Mapping, body.Reason, Corr(c), ct)).ToHttp()));

        // ---- reference lists, numbering, reason codes
        admin.MapGet("/lists/{list}", (string list, HttpContext c, ReferenceAdminService svc, CancellationToken ct) => Guard(async () => (await svc.ListAsync(Who(c), list, ct)).ToHttp()));
        admin.MapPut("/lists/{list}/{code}", (string list, string code, RefItemRequest body, HttpContext c, ReferenceAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.SaveAsync(Who(c), new RefItemInput(list, code, body.Label, body.SortOrder, body.LifecycleFamily), Corr(c), ct)).ToHttp()));
        admin.MapPost("/lists/{list}/{code}/active", (string list, string code, ActiveRequest body, HttpContext c, ReferenceAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.SetActiveAsync(Who(c), list, code, body.Active, body.Reason, Corr(c), ct)).ToHttp()));
        admin.MapGet("/numbering", (HttpContext c, NumberingAdminService svc, CancellationToken ct) => Guard(async () => (await svc.ListAsync(Who(c), ct)).ToHttp()));
        admin.MapPut("/numbering/{objectType}", (string objectType, NumberingRequest body, HttpContext c, NumberingAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.UpdateAsync(Who(c), objectType, body.Prefix, body.Width, body.Reason, Corr(c), ct)).ToHttp()));
        admin.MapPost("/numbering/{objectType}/advance", (string objectType, AdvanceRequest body, HttpContext c, NumberingAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.AdvanceAsync(Who(c), objectType, body.NextValue, body.Reason, Corr(c), ct)).ToHttp()));
        admin.MapGet("/reason-codes", (HttpContext c, ReasonCodeAdminService svc, CancellationToken ct) => Guard(async () => (await svc.ListAsync(Who(c), ct)).ToHttp()));
        admin.MapPut("/reason-codes", (ReasonCodeDto body, HttpContext c, ReasonCodeAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.SaveAsync(Who(c), body, Corr(c), ct)).ToHttp()));
        admin.MapPost("/reason-codes/active", (ReasonCodeActiveRequest body, HttpContext c, ReasonCodeAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.SetActiveAsync(Who(c), body.Kind, body.Scope, body.Code, body.Active, body.Reason, Corr(c), ct)).ToHttp()));

        // ---- workflow templates
        admin.MapGet("/workflow/templates", (bool? all, HttpContext c, WorkflowAdminService svc, CancellationToken ct) => Guard(async () => (await svc.ListAsync(Who(c), all == true, Corr(c), ct)).ToHttp()));
        admin.MapPost("/workflow/templates/validate", (JsonObject body, HttpContext c, WorkflowAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.ValidateAsync(Who(c), body.ToJsonString(), ct)).ToHttp()));
        admin.MapPost("/workflow/templates", (JsonObject body, HttpContext c, WorkflowAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.PublishAsync(Who(c), body.ToJsonString(), Corr(c), ct)).ToHttp(v => Results.Created($"/api/v1/admin/workflow/templates?all=true#{v.Id}-v{v.Version}", v))));
        admin.MapGet("/workflow/templates/{id}/diff", (string id, int from, int to, HttpContext c, WorkflowAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.DiffAsync(Who(c), id, from, to, ct)).ToHttp()));
        admin.MapGet("/workflow/pins", (HttpContext c, WorkflowAdminService svc, CancellationToken ct) => Guard(async () => (await svc.ListPinsAsync(Who(c), ct)).ToHttp()));
        admin.MapPut("/workflow/pins/{objectType}", (string objectType, PinRequest body, HttpContext c, WorkflowAdminService svc, CancellationToken ct) =>
            Guard(async () => (await svc.PinAsync(Who(c), objectType, body.TemplateId, Corr(c), ct)).ToHttp()));

        // ---- classification administration
        admin.MapPost("/classification/classes", (ClassNodeInput body, HttpContext c, ClassificationService svc, CancellationToken ct) =>
            Guard(async () => (await svc.CreateNodeAsync(Who(c), body, Corr(c), ct)).ToHttp(v => Results.Created($"/api/v1/classification/classes/{v.Code}", v))));
        admin.MapPut("/classification/classes/{code}", (string code, ClassUpdateRequest body, HttpContext c, ClassificationService svc, CancellationToken ct) =>
            Guard(async () => (await svc.UpdateNodeAsync(Who(c), code, body.Name, body.Description, body.SortOrder, Corr(c), ct)).ToHttp()));
        admin.MapPost("/classification/classes/{code}/move", (string code, MoveRequest body, HttpContext c, ClassificationService svc, CancellationToken ct) =>
            Guard(async () => (await svc.MoveNodeAsync(Who(c), code, body.NewParentCode, body.Reason, Corr(c), ct)).ToHttp()));
        admin.MapPost("/classification/classes/{code}/status", (string code, ClassStatusRequest body, HttpContext c, ClassificationService svc, CancellationToken ct) =>
            Guard(async () => (await svc.SetStatusAsync(Who(c), code, body.Active, body.Reason, Corr(c), ct)).ToHttp()));
        admin.MapPost("/classification/classes/{code}/attributes", (string code, AttributeDefinitionInput body, HttpContext c, ClassificationService svc, CancellationToken ct) =>
            Guard(async () => (await svc.SaveAttributeAsync(Who(c), code, body, true, Corr(c), ct)).ToHttp(v => Results.Created($"/api/v1/classification/classes/{code}", v))));
        admin.MapPut("/classification/classes/{code}/attributes/{name}", (string code, string name, AttributeDefinitionInput body, HttpContext c, ClassificationService svc, CancellationToken ct) =>
            Guard(async () => (await svc.SaveAttributeAsync(Who(c), code, body with { Name = name }, false, Corr(c), ct)).ToHttp()));
        admin.MapPost("/classification/classes/{code}/attributes/{name}/retire", (string code, string name, RetireRequest body, HttpContext c, ClassificationService svc, CancellationToken ct) =>
            Guard(async () => (await svc.SetAttributeRetiredAsync(Who(c), code, name, body.Retired, body.Reason, Corr(c), ct)).ToHttp()));
        admin.MapGet("/classification/export", (string? format, HttpContext c, ClassificationService svc, CancellationToken ct) => Guard(async () =>
        {
            var o = await svc.ExportHierarchyAsync(Who(c), ct);
            if (!o.IsOk) return o.ToHttp();
            return string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)
                ? Results.Text(ClassificationIO.ToCsv(o.Value!), "text/csv") : Results.Text(ClassificationIO.ToJson(o.Value!), "application/json");
        }));
        admin.MapPost("/classification/import", (string? format, bool? dryRun, string? reason, HttpContext c, ClassificationService svc, CancellationToken ct) => Guard(async () =>
        {
            using var reader = new StreamReader(c.Request.Body);
            var text = await reader.ReadToEndAsync(ct);
            var (doc, error) = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase) ? ClassificationIO.FromCsv(text) : ClassificationIO.FromJson(text);
            if (doc is null) return Outcome<ClassImportReport>.Invalid([error!]).ToHttp();
            return (await svc.ImportHierarchyAsync(Who(c), doc, dryRun != false, reason, Corr(c), ct)).ToHttp();
        }));

        // ---- configuration export, import, history
        admin.MapGet("/config/export", (HttpContext c, ConfigTransferService svc, CancellationToken ct) => Guard(async () => (await svc.ExportAsync(Who(c), Corr(c), ct)).ToHttp()));
        admin.MapPost("/config/import", (bool? dryRun, string? reason, HttpContext c, ConfigTransferService svc, CancellationToken ct) => Guard(async () =>
        {
            using var reader = new StreamReader(c.Request.Body);
            var (doc, error) = ConfigDocument.FromJson(await reader.ReadToEndAsync(ct));
            if (doc is null) return Outcome<ConfigImportReport>.Invalid([error!]).ToHttp();
            return (dryRun != false ? await svc.DryRunAsync(Who(c), doc, ct) : await svc.ApplyAsync(Who(c), doc, reason, Corr(c), ct)).ToHttp();
        }));
        admin.MapGet("/config/history", (int? limit, string? action, HttpContext c, ConfigHistoryService svc, CancellationToken ct) =>
            Guard(async () => (await svc.ListAsync(Who(c), limit ?? 100, action, ct)).ToHttp()));

        // ---- file downloads for the admin UI (cookie session, so outside /api): the same documents as the API exports
        app.MapGet("/admin-files/config", async (HttpContext c, ConfigTransferService svc, CancellationToken ct) =>
        {
            var o = await svc.ExportAsync(Who(c), Corr(c), ct);
            return o.IsOk ? Results.File(System.Text.Encoding.UTF8.GetBytes(o.Value!.ToJson()), "application/json", "keel-config.json") : o.ToHttp();
        }).RequireAuthorization(Policies.Admin);
        app.MapGet("/admin-files/classification", async (string? format, HttpContext c, ClassificationService svc, CancellationToken ct) =>
        {
            var o = await svc.ExportHierarchyAsync(Who(c), ct);
            if (!o.IsOk) return o.ToHttp();
            var csv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv ? ClassificationIO.ToCsv(o.Value!) : ClassificationIO.ToJson(o.Value!)),
                csv ? "text/csv" : "application/json", csv ? "keel-classification.csv" : "keel-classification.json");
        }).RequireAuthorization(Policies.Admin);

        // ---- open to every PLM role: lists, the class tree, a part's class, the faceted search
        var any = app.MapGroup("/api/v1").RequireAuthorization(Policies.AnyPlmRole);
        any.MapGet("/metadata/lists/{list}", async (string list, IReferenceData refs, CancellationToken ct) =>
            RefLists.All.Contains(list) ? Results.Ok(await refs.ListAsync(list, false, ct)) : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown list"));
        any.MapGet("/classification/tree", async (ClassificationService svc, CancellationToken ct) =>
        {
            var tree = await svc.TreeAsync(ct);
            return Results.Ok(tree.Nodes.OrderBy(n => n.Path, StringComparer.Ordinal));
        });
        any.MapGet("/classification/classes/{code}", async (string code, ClassificationService svc, CancellationToken ct) =>
            await svc.GetAsync(code, ct) is { } detail ? Results.Ok(detail) : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found"));
        any.MapPost("/classification/search", (ClassSearchQuery body, HttpContext c, ClassificationService svc, CancellationToken ct) =>
            Guard(async () => (await svc.SearchAsync(Who(c), body, Corr(c), ct)).ToHttp()));
        any.MapGet("/parts/{number}/classification", (string number, HttpContext c, ClassificationService svc, CancellationToken ct) =>
            Guard(async () => (await svc.GetForPartAsync(Who(c), number, Corr(c), ct)).ToHttp()));
        any.MapPut("/parts/{number}/classification", (string number, AssignClassRequest body, HttpContext c, ClassificationService svc, CancellationToken ct) =>
            Guard(async () => (await svc.SetForPartAsync(Who(c), number, body.ClassCode, body.Attributes, Corr(c), ct)).ToHttp()));
    }

    /// <summary>Out-of-scope behaviour throws <see cref="NotSupportedException"/> naming the phase; the API answers 501 with that message.</summary>
    private static async Task<IResult> Guard(Func<Task<IResult>> body)
    {
        try { return await body(); }
        catch (NotSupportedException e) { return Results.Problem(statusCode: StatusCodes.Status501NotImplemented, title: e.Message); }
    }
}

public sealed record ReasonCodeActiveRequest(string Kind, string? Scope, string Code, bool Active, string? Reason);
