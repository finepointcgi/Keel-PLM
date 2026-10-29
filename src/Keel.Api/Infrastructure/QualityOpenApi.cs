using Microsoft.OpenApi;

namespace Keel.Api.Infrastructure;

/// <summary>
/// The OpenAPI 3.1 document of the quality surface (nonconformance reports, corrective actions, deviations and waivers), published at /openapi/quality.json and committed
/// as docs/quality/openapi.json. A test fails when the committed file differs from the generated one.
/// </summary>
public static class QualityOpenApi
{
    public const string Document = "quality";

    public static IServiceCollection AddKeelQualityOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(Document, o =>
        {
            o.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            o.ShouldInclude = d => d.RelativePath is { } p && p.StartsWith("api/v1/quality", StringComparison.Ordinal);
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Servers = [new OpenApiServer { Url = "/" }];
                doc.Info = new OpenApiInfo
                {
                    Title = "Keel PLM quality API",
                    Version = "1",
                    Description = "Nonconformance reports with a signed material review board disposition, corrective and preventive actions with signed verification, and deviations and waivers with scope, window and automatic expiry. Every list and count is limited to records the caller may read; a record the caller may not read answers 404.",
                };
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
