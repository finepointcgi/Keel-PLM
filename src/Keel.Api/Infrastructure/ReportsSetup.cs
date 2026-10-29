using Keel.Api.Infrastructure.Reports;
using Keel.Core;
using Keel.Core.Reports;
using Keel.Documents.Marking;
using Microsoft.OpenApi;

namespace Keel.Api.Infrastructure;

/// <summary>Composition of the output module (ADR-0044) and its OpenAPI 3.1 document, published at /openapi/reports.json and committed as docs/reports/openapi.json.</summary>
public static class ReportsSetup
{
    public const string OpenApiDocument = "reports";

    public static IServiceCollection AddKeelReportsHost(this IServiceCollection services)
    {
        services.AddSingleton<IReleasePackageSource, ReleasePackageSource>();
        services.AddSingleton<IBomExchangeSource, BomExchangeSource>();
        services.AddSingleton<IMassChangeTarget, MassPartTarget>();
        services.AddSingleton<IReportDataset, PartsDataset>();
        services.AddSingleton<IReportDataset, ChangesDataset>();
        services.AddSingleton<IReportDataset, BaselinesDataset>();
        services.AddSingleton<IReportDataset, QualityDataset>();
        services.AddSingleton<IReportDataset, RequirementsDataset>();
        services.AddSingleton<IReportDataset, DeclarationsDataset>();
        services.AddSingleton<IReportDataset, SchedulesDataset>();
        services.AddSingleton<IReportDataset, BomDataset>();
        Keel.Reports.ReportsModule.AddKeelReports(services, MarkingStatements.Notices, EmbeddedFontResolver.EnsureInstalled);
        services.AddOpenApi(OpenApiDocument, o =>
        {
            o.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            o.ShouldInclude = d => d.RelativePath is { } p && p.StartsWith("api/v1/output", StringComparison.Ordinal);
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Servers = [new OpenApiServer { Url = "/" }];
                doc.Info = new OpenApiInfo
                {
                    Title = "Keel PLM output and bulk work API",
                    Version = "1",
                    Description = "Release package PDFs (refused when any included object is unreadable, aggregate marking on every page), Excel and CSV BOM export and import proposals, mass change with preview, apply and undo, and the report builder over a fixed catalog of datasets. Every result is read-filtered for the caller and carries its marking banner.",
                };
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
