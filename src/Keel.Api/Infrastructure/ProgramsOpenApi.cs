using Microsoft.OpenApi;

namespace Keel.Api.Infrastructure;

/// <summary>
/// The OpenAPI 3.1 document of the programs surface (programs, schedule, deliverables and gate reviews), published at /openapi/programs.json and committed
/// as docs/programs/openapi.json. A test fails when the committed file differs from the generated one.
/// </summary>
public static class ProgramsOpenApi
{
    public const string Document = "programs";

    public static IServiceCollection AddKeelProgramsOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(Document, o =>
        {
            o.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            o.ShouldInclude = d => d.RelativePath is { } p && (p.StartsWith("api/v1/programs", StringComparison.Ordinal) || p.StartsWith("api/v1/gates", StringComparison.Ordinal));
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Servers = [new OpenApiServer { Url = "/" }];
                doc.Info = new OpenApiInfo
                {
                    Title = "Keel PLM programs API",
                    Version = "1",
                    Description = "Programs with their work breakdown, schedule, critical path, immutable schedule baselines, deliverables that point at Keel objects and gate reviews approved with signatures. Every response is limited to what the caller may read.",
                };
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
