using Microsoft.OpenApi;

namespace Keel.Api.Infrastructure;

/// <summary>
/// The OpenAPI 3.1 document of the product compliance surface (substances, materials, regulatory lists, declarations, assembly roll-up and exports), published at
/// /openapi/compliance.json and committed as docs/compliance/openapi.json. A test fails when the committed file differs from the generated one.
/// </summary>
public static class ComplianceOpenApi
{
    public const string Document = "compliance";

    public static IServiceCollection AddKeelComplianceOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(Document, o =>
        {
            o.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            o.ShouldInclude = d => d.RelativePath is { } p && p.StartsWith("api/v1/compliance", StringComparison.Ordinal);
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Servers = [new OpenApiServer { Url = "/" }];
                doc.Info = new OpenApiInfo
                {
                    Title = "Keel PLM product compliance API",
                    Version = "1",
                    Description = "Substances and materials, regulatory lists in versions, compliance declarations of parts, and the compliance roll-up of an assembly with CSV and XML exports. Declarations are readable only through readable parts; a part the caller may not read is reported as not found and never counted.",
                };
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
