using Keel.Core;
using Microsoft.OpenApi;

namespace Keel.Api.Infrastructure;

/// <summary>Composition of CAD integration (ADR-0035) and its own OpenAPI document, published at /openapi/cad.json and committed as docs/integration/cad-openapi.json.</summary>
public static class CadSetup
{
    public const string OpenApiDocument = "cad";

    public static IServiceCollection AddKeelCadHost(this IServiceCollection services)
    {
        services.AddSingleton<ICadDocumentPort, Keel.Documents.CadDocumentPort>();
        services.AddSingleton<ICadPartPort, CadPartPort>();
        Keel.Cad.CadModule.AddKeelCad(services);
        services.AddOpenApi(OpenApiDocument, o =>
        {
            o.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            o.ShouldInclude = d => d.RelativePath is { } p && p.StartsWith("api/v1/cad", StringComparison.Ordinal);
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Servers = [new OpenApiServer { Url = "/" }];
                doc.Info = new OpenApiInfo
                {
                    Title = "Keel PLM CAD connector API",
                    Version = "1",
                    Description = "Check-out and check-in of CAD packages, dependency graph and stale detection, associations with parts, and CAD structure proposals. Protocol: docs/integration/cad-connector.md.",
                };
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
