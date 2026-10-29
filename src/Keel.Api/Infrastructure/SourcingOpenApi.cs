using Microsoft.OpenApi;

namespace Keel.Api.Infrastructure;

/// <summary>
/// The OpenAPI 3.1 document of the sourcing surface (suppliers, approved lists, sourcing queries, requests, supplier portal), published at /openapi/sourcing.json and
/// committed as docs/sourcing/openapi.json. A test fails when the committed file differs from the generated one.
/// </summary>
public static class SourcingOpenApi
{
    public const string Document = "sourcing";

    public static IServiceCollection AddKeelSourcingOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(Document, o =>
        {
            o.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            o.ShouldInclude = d => d.RelativePath is { } p && (p.StartsWith("api/v1/sourcing", StringComparison.Ordinal) || p.StartsWith("api/v1/portal", StringComparison.Ordinal));
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Servers = [new OpenApiServer { Url = "/" }];
                doc.Info = new OpenApiInfo
                {
                    Title = "Keel PLM sourcing API",
                    Version = "1",
                    Description = "Suppliers, approved manufacturer and vendor lists, sourcing queries and the lightweight supplier portal. Portal routes need the supplier role and show a supplier only its own requests.",
                };
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
