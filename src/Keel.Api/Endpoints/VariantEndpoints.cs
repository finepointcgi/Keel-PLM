using System.Text;
using Keel.Api.Infrastructure;
using Keel.Bom;
using Keel.Core;
using Keel.Variants;
using Keel.Web.Services;

namespace Keel.Api.Endpoints;

public sealed record NewFamilyRequest(string Key, string Name, string? Description = null, bool Required = true, int SortOrder = 0);
public sealed record FamilyChangeRequest(string? Name = null, string? Description = null, bool? Required = null, bool? Retired = null, int? SortOrder = null);
public sealed record NewValueRequest(string Key, string Name, string? Description = null, int SortOrder = 0);
public sealed record ValueChangeRequest(string? Name = null, string? Description = null, bool? Retired = null, int? SortOrder = null);
public sealed record OptionRefDto(string Family, string Value);
/// <summary><c>Kind</c> is requires, excludes or mutex. A mutex rule has no subject.</summary>
public sealed record NewRuleRequest(string Kind, OptionRefDto? Subject, OptionRefDto[] Members, string? Reason = null);
public sealed record NewVariantRequest(string Name, Dictionary<string, string> Selection, string? Description = null);
public sealed record VariantChangeRequest(string? Name = null, string? Description = null, Dictionary<string, string>? Selection = null);
public sealed record CheckConfigurationRequest(Dictionary<string, string> Selection);
public sealed record PreviewConditionRequest(string? Product, string? Condition);
public sealed record SetConditionRequest(string? Condition, string? Product = null);

/// <summary>
/// REST for options, rules, named variants, usage conditions and the resolved (100%) structure of a variant (ADR-0039). Thin: every rule is in the module services.
/// A product the caller cannot read answers 404, exactly like one that does not exist. Documented in /openapi/variants.json (committed as docs/variants/openapi.json).
/// </summary>
public static class VariantEndpoints
{
    public static void MapVariantEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/variants").RequireAuthorization(Policies.AnyPlmRole).WithTags("Options and variants");
        static Subject Who(HttpContext c) => SubjectFactory.Require(c);
        static string? Corr(HttpContext c) => CorrelationId.Get(c);
        static string P(string s) => Uri.EscapeDataString(s);

        // ---- products with an option model
        g.MapGet("/products", async (string? search, string? sort, string? direction, int? limit, HttpContext c, VariantService svc, CancellationToken ct) =>
        {
            if (!VariantService.TryResolveSort(sort, direction, out var spec, out var error)) return OutcomeResults.BadSort(error!);
            return Results.Ok(await svc.ListProductsAsync(Who(c), search, null, spec, limit ?? 100, ct));
        }).WithSummary("Products with an option model that you may read.");

        g.MapGet("/products/{number}", async (string number, HttpContext c, VariantService svc, CancellationToken ct) =>
            (await svc.GetModelAsync(Who(c), number, Corr(c), ct)).ToHttp()).WithSummary("Options, rules and variants of a product, with validation findings.");

        g.MapPost("/products/{number}", async (string number, HttpContext c, VariantService svc, CancellationToken ct) =>
            (await svc.CreateModelAsync(Who(c), number, Corr(c), ct)).ToHttp(m => Results.Created($"/api/v1/variants/products/{P(number)}", m))).WithSummary("Creates the option model of an assembly.");

        g.MapPost("/products/{number}/families", async (string number, NewFamilyRequest r, HttpContext c, VariantService svc, CancellationToken ct) =>
            (await svc.AddFamilyAsync(Who(c), number, new NewFamily(r.Key, r.Name, r.Description, r.Required, r.SortOrder), Corr(c), ct)).ToHttp());
        g.MapPut("/products/{number}/families/{family}", async (string number, string family, FamilyChangeRequest r, HttpContext c, VariantService svc, CancellationToken ct) =>
            (await svc.UpdateFamilyAsync(Who(c), number, family, new FamilyChange(r.Name, r.Description, r.Required, r.Retired, r.SortOrder), Corr(c), ct)).ToHttp());
        g.MapDelete("/products/{number}/families/{family}", async (string number, string family, HttpContext c, VariantService svc, CancellationToken ct) =>
            (await svc.DeleteFamilyAsync(Who(c), number, family, Corr(c), ct)).ToHttp());
        g.MapPost("/products/{number}/families/{family}/values", async (string number, string family, NewValueRequest r, HttpContext c, VariantService svc, CancellationToken ct) =>
            (await svc.AddValueAsync(Who(c), number, family, new NewValue(r.Key, r.Name, r.Description, r.SortOrder), Corr(c), ct)).ToHttp());
        g.MapPut("/products/{number}/families/{family}/values/{value}", async (string number, string family, string value, ValueChangeRequest r, HttpContext c, VariantService svc, CancellationToken ct) =>
            (await svc.UpdateValueAsync(Who(c), number, family, value, new ValueChange(r.Name, r.Description, r.Retired, r.SortOrder), Corr(c), ct)).ToHttp());
        g.MapDelete("/products/{number}/families/{family}/values/{value}", async (string number, string family, string value, HttpContext c, VariantService svc, CancellationToken ct) =>
            (await svc.DeleteValueAsync(Who(c), number, family, value, Corr(c), ct)).ToHttp());

        g.MapPost("/products/{number}/rules", async (string number, NewRuleRequest r, HttpContext c, VariantService svc, CancellationToken ct) =>
        {
            if (!Enum.TryParse<RuleKind>(r.Kind, ignoreCase: true, out var kind))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Use requires, excludes or mutex."] });
            return (await svc.AddRuleAsync(Who(c), number, new NewRule(kind, r.Subject is null ? null : new OptionRef(r.Subject.Family, r.Subject.Value),
                [.. (r.Members ?? []).Select(m => new OptionRef(m.Family, m.Value))], r.Reason), Corr(c), ct)).ToHttp();
        }).WithSummary("Adds an option rule. A rule that makes the model inconsistent, or an option unusable, is refused.");
        g.MapDelete("/products/{number}/rules/{id:guid}", async (string number, Guid id, HttpContext c, VariantService svc, CancellationToken ct) =>
            (await svc.DeleteRuleAsync(Who(c), number, id, Corr(c), ct)).ToHttp());

        // ---- named variants
        g.MapPost("/products/{number}/variants", async (string number, NewVariantRequest r, HttpContext c, VariantService svc, CancellationToken ct) =>
            (await svc.CreateVariantAsync(Who(c), number, new NewVariant(r.Name, r.Selection ?? [], r.Description), Corr(c), ct)).ToHttp());
        g.MapPut("/products/{number}/variants/{name}", async (string number, string name, VariantChangeRequest r, HttpContext c, VariantService svc, CancellationToken ct) =>
            (await svc.UpdateVariantAsync(Who(c), number, name, new VariantChange(r.Name, r.Description, r.Selection), Corr(c), ct)).ToHttp());
        g.MapDelete("/products/{number}/variants/{name}", async (string number, string name, HttpContext c, VariantService svc, CancellationToken ct) =>
            (await svc.DeleteVariantAsync(Who(c), number, name, Corr(c), ct)).ToHttp()).WithSummary("Deletes a draft variant. A released variant is retired, never deleted.");
        g.MapPost("/products/{number}/variants/{name}/release", async (string number, string name, HttpContext c, VariantService svc, CancellationToken ct) =>
            (await svc.ReleaseVariantAsync(Who(c), number, name, Corr(c), ct)).ToHttp()).WithSummary("Releases a valid draft variant and reports variant.released to the integration outbox.");
        g.MapPost("/products/{number}/variants/{name}/retire", async (string number, string name, HttpContext c, VariantService svc, CancellationToken ct) =>
            (await svc.RetireVariantAsync(Who(c), number, name, Corr(c), ct)).ToHttp());
        g.MapPost("/products/{number}/check", async (string number, CheckConfigurationRequest r, HttpContext c, VariantService svc, CancellationToken ct) =>
            (await svc.CheckConfigurationAsync(Who(c), number, r.Selection ?? [], Corr(c), ct)).ToHttp()).WithSummary("Checks a combination of options against the rules.");

        // ---- the resolved (100%) structure of a variant
        g.MapGet("/products/{number}/variants/{name}/bom", async (string number, string name, string? spec, int? depth, string? parentLine, [AsParameters] UnitQuery unit, HttpContext c, VariantService svc, BomService bom, CancellationToken ct) =>
            await Resolved(c, svc, number, name, spec, unit, async (sel, s, eff) => (await bom.ExplodeAsync(Who(c), new BomQuery(number, Spec: s, Depth: depth, ParentLine: parentLine, Effectivity: eff, Variant: sel), Corr(c), ct)), ct))
            .WithSummary("The structure with only the lines the variant uses (conditions and effectivity filtered inside the same read-filtered query).");
        g.MapGet("/products/{number}/variants/{name}/bom/export.csv", async (string number, string name, string? spec, int? depth, [AsParameters] UnitQuery unit, HttpContext c, VariantService svc, BomService bom, CancellationToken ct) =>
            await Resolved(c, svc, number, name, spec, unit, async (sel, s, eff) => (await bom.ExportCsvAsync(Who(c), new BomQuery(number, Spec: s, Depth: depth, Effectivity: eff, Variant: sel), Corr(c), ct)), ct,
                e => Results.File(Encoding.UTF8.GetBytes(e.Csv), "text/csv; charset=utf-8", e.FileName)));
        g.MapGet("/products/{number}/variants/{name}/bom/summary", async (string number, string name, string? spec, int? depth, bool? leavesOnly, [AsParameters] UnitQuery unit, HttpContext c, VariantService svc, BomService bom, CancellationToken ct) =>
            await Resolved(c, svc, number, name, spec, unit, async (sel, s, eff) => (await bom.SummaryAsync(Who(c), new BomQuery(number, Spec: s, Depth: depth, Effectivity: eff, Variant: sel), leavesOnly ?? true, Corr(c), ct)), ct));
        g.MapGet("/products/{number}/variants/{name}/mboms/{mbom}/explosion", async (string number, string name, string mbom, int? depth, HttpContext c, VariantService svc, MbomService mboms, CancellationToken ct) =>
        {
            var sel = await svc.ResolveAsync(Who(c), number, name, Corr(c), ct);
            if (!sel.IsOk) return sel.ToHttp();
            return (await mboms.ExplodeAsync(Who(c), new MbomQuery(mbom, Depth: depth, Variant: sel.Value!.Selection), Corr(c), ct)).ToHttp();
        }).WithSummary("An MBOM resolved for a variant: each line takes the conditions of the EBOM lines it implements.");
        g.MapGet("/products/{number}/compare", async (string number, string left, string right, string? spec, int? depth, HttpContext c, VariantService svc, BomService bom, CancellationToken ct) =>
        {
            var parsed = BomConfigSpec.Latest;
            if (!string.IsNullOrWhiteSpace(spec) && !Enum.TryParse(spec, ignoreCase: true, out parsed))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["spec"] = ["Use Latest or LatestReleased."] });
            var a = await svc.ResolveAsync(Who(c), number, left, Corr(c), ct);
            if (!a.IsOk) return a.ToHttp();
            var b = await svc.ResolveAsync(Who(c), number, right, Corr(c), ct);
            if (!b.IsOk) return b.ToHttp();
            return (await bom.CompareVariantsAsync(Who(c), number, a.Value!.Selection, b.Value!.Selection, parsed, depth, Corr(c), ct)).ToHttp();
        }).WithSummary("What differs between two variants of a product.");

        // ---- which variants use a part
        g.MapGet("/parts/{number}/used-by", async (string number, string? spec, HttpContext c, VariantWhereUsedService svc, CancellationToken ct) =>
        {
            var parsed = BomConfigSpec.Latest;
            if (!string.IsNullOrWhiteSpace(spec) && !Enum.TryParse(spec, ignoreCase: true, out parsed))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["spec"] = ["Use Latest or LatestReleased."] });
            return (await svc.ForPartAsync(Who(c), number, parsed, Corr(c), ct)).ToHttp();
        }).WithSummary("The products that contain a part and the variants of each that use it.");

        // ---- conditions on lines
        g.MapPost("/conditions/preview", async (PreviewConditionRequest r, HttpContext c, VariantService svc, CancellationToken ct) =>
            Results.Ok(await svc.PreviewConditionAsync(Who(c), r.Product, r.Condition, ct))).WithSummary("Parses a usage condition and says it in plain words; checks the option names when a product is named.");
        g.MapPut("/lines/{number}/{usageId:guid}/condition", async (string number, Guid usageId, SetConditionRequest r, HttpContext c, VariantService svc, BomService bom, CancellationToken ct) =>
        {
            var who = Who(c);
            ConditionValidator? validator = string.IsNullOrWhiteSpace(r.Product) ? null : (e, token) => svc.ConditionValidatorFor(who, r.Product!)(e, token);
            return (await bom.SetLineConditionAsync(who, number, usageId, r.Condition, validator, Corr(c), ct)).ToHttp();
        }).WithSummary("Sets or clears the option condition of a line of your working copy. Released iterations change only by revising the part.");

        // The UI downloads through the session cookie; a plain link works within the CSP.
        app.MapGet("/ui/variants/{number}/{name}/bom.csv", async (string number, string name, string? spec, int? depth, [AsParameters] UnitQuery unit, HttpContext c, VariantService svc, BomService bom, CancellationToken ct) =>
            await Resolved(c, svc, number, name, spec, unit, async (sel, s, eff) => (await bom.ExportCsvAsync(Who(c), new BomQuery(number, Spec: s, Depth: depth, Effectivity: eff, Variant: sel), Corr(c), ct)), ct,
                e => Results.File(Encoding.UTF8.GetBytes(e.Csv), "text/csv; charset=utf-8", e.FileName))).RequireAuthorization(Policies.AnyPlmRole);
    }

    /// <summary>Resolves the variant, then runs the call. Naming a unit (serial, lot or date, with its context) resolves for that unit as well: options and effectivity both apply.</summary>
    private static async Task<IResult> Resolved<T>(HttpContext c, VariantService svc, string number, string name, string? spec, UnitQuery unit,
        Func<Keel.Core.Variants.VariantSelection, BomConfigSpec, EffectivityRequest?, Task<Outcome<T>>> call, CancellationToken ct, Func<T, IResult>? ok = null)
    {
        var parsed = BomConfigSpec.Latest;
        if (!string.IsNullOrWhiteSpace(spec) && (!Enum.TryParse(spec, ignoreCase: true, out parsed) || parsed is BomConfigSpec.Baseline or BomConfigSpec.Effectivity))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["spec"] = ["Use Latest or LatestReleased, or name a unit with context and serial, lot or date."] });
        var eff = unit.ToRequest();
        if (eff is not null) parsed = BomConfigSpec.Effectivity;
        var sel = await svc.ResolveAsync(SubjectFactory.Require(c), number, name, CorrelationId.Get(c), ct);
        if (!sel.IsOk) return sel.ToHttp();
        return (await call(sel.Value!.Selection, parsed, eff)).ToHttp(ok);
    }
}
