using Microsoft.OpenApi;

namespace Keel.Api.Infrastructure;

/// <summary>
/// The OpenAPI 3.1 document of the options and variants surface (option models, rules, named variants, usage conditions, resolved structures, which variants use a part),
/// published at /openapi/variants.json and committed as docs/variants/openapi.json. A test fails when the committed file differs from the generated one.
/// </summary>
public static class VariantOpenApi
{
    public const string Document = "variants";

    public static IServiceCollection AddKeelVariantsOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(Document, o =>
        {
            o.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            o.ShouldInclude = d => d.RelativePath is { } p && p.StartsWith("api/v1/variants", StringComparison.Ordinal);
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Servers = [new OpenApiServer { Url = "/" }];
                doc.Info = new OpenApiInfo
                {
                    Title = "Keel PLM options and variants API",
                    Version = "1",
                    Description = "Option families and values, option rules, named variants and their release, option usage conditions on BOM lines, and the structure resolved for one variant. "
                        + "A product you may not read answers 404, like one that does not exist. Documented in docs/adr/0039-configuration-and-variants.md.",
                };
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
