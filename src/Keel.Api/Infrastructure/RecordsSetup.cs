using Keel.Core;
using Keel.Documents.Vault;
using Keel.Records;
using Microsoft.OpenApi;

namespace Keel.Api.Infrastructure;

/// <summary>Host composition of records management (8.6): the module, the vault's S3 Object Lock feature probe, and the OpenAPI document docs/records/openapi.json.</summary>
public static class RecordsSetup
{
    public const string Document = "records";

    public static IServiceCollection AddKeelRecordsHost(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddKeelRecords(configuration);
        services.AddSingleton<IWormStorage>(sp => new VaultObjectLock(sp.GetRequiredService<ObjectVault>())); // feature-detected: a store without object lock answers "not supported"
        services.AddOpenApi(Document, o =>
        {
            o.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            o.ShouldInclude = d => d.RelativePath is { } p && p.StartsWith("api/v1/records", StringComparison.Ordinal);
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Servers = [new OpenApiServer { Url = "/" }];
                doc.Info = new OpenApiInfo
                {
                    Title = "Keel PLM records management API",
                    Version = "1",
                    Description = "Retention schedules, program events, legal holds, the disposition queue and requests, certificates of destruction and WORM-style archives. Reviews (schedule publication, hold release, disposition approval) use the workflow API with object types retention_schedule, legal_hold and disposition.",
                };
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
