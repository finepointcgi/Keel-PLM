using Microsoft.OpenApi;

namespace Keel.Api.Infrastructure;

/// <summary>
/// The OpenAPI 3.1 document of the requirements surface (specifications, requirements, traceability links, verification, coverage, import and export), published at
/// /openapi/requirements.json and committed as docs/requirements/openapi.json. A test fails when the committed file differs from the generated one.
/// </summary>
public static class RequirementsOpenApi
{
    public const string Document = "requirements";

    public static IServiceCollection AddKeelRequirementsOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(Document, o =>
        {
            o.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            o.ShouldInclude = d => d.RelativePath is { } p && p.StartsWith("api/v1/requirements", StringComparison.Ordinal);
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Servers = [new OpenApiServer { Url = "/" }];
                doc.Info = new OpenApiInfo
                {
                    Title = "Keel PLM requirements API",
                    Version = "1",
                    Description = "Requirement specifications (revisions, release by review), hierarchical requirements with their own markings, traceability links, verification records, coverage, and import and export. A requirement or link you may not read is never returned; where it has a place in a structure it is a placeholder with no detail.",
                };
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
