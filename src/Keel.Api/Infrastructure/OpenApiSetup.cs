using Microsoft.OpenApi;

namespace Keel.Api.Infrastructure;

/// <summary>
/// The OpenAPI 3.1 document of the integration surface (pull API, Technical Data Packages, integration administration), published at
/// /openapi/v1.json and committed as docs/integration/openapi.json. A test fails when the committed file differs from the generated one.
/// </summary>
public static class OpenApiSetup
{
    public const string Version = "v1";

    public static IServiceCollection AddKeelIntegrationOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(Version, o =>
        {
            o.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            o.ShouldInclude = d => d.RelativePath is { } p &&
                (p.StartsWith("api/v1/integration", StringComparison.Ordinal) || p.StartsWith("api/v1/tdp", StringComparison.Ordinal) || p.StartsWith("api/v1/admin/integration", StringComparison.Ordinal));
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Servers = [new OpenApiServer { Url = "/" }]; // the document must not depend on the host that generated it
                doc.Info = new OpenApiInfo
                {
                    Title = "Keel PLM integration API",
                    Version = "1",
                    Description = "Pull API for ERP reconciliation, Technical Data Package export and integration administration. Events are delivered to sinks as signed envelopes; see docs/integration/erp-contract.md.",
                };
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
