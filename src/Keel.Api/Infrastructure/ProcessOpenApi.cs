using Microsoft.OpenApi;

namespace Keel.Api.Infrastructure;

/// <summary>
/// The OpenAPI 3.1 document of the manufacturing process surface (plans, work instructions, shop floor view, tools, work centers), published at /openapi/process.json and committed as
/// docs/process/openapi.json. A test fails when the committed file differs from the generated one.
/// </summary>
public static class ProcessOpenApi
{
    public const string Document = "process";

    public static IServiceCollection AddKeelProcessOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(Document, o =>
        {
            o.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            o.ShouldInclude = d => d.RelativePath is { } p && p.StartsWith("api/v1/process", StringComparison.Ordinal);
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Servers = [new OpenApiServer { Url = "/" }];
                doc.Info = new OpenApiInfo
                {
                    Title = "Keel PLM manufacturing process API",
                    Version = "1",
                    Description = "Process plans per MBOM (routings, operations, component assignments, work instructions), their revisions and release review, the shop floor view, tools, fixtures and gauges with calibration, "
                        + "and work centers per plant. A plan or tool you may not read answers 404, like one that does not exist. Documented in docs/adr/0042-manufacturing-process-data.md.",
                };
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
