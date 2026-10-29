using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Keel.Web.Services;

/// <summary>One side of the visual comparison as the page and its script need it.</summary>
public sealed record ViewerCompareSide(string Label, string FileName, string Sha256, long SizeBytes, string MeshUrl, string Summary);

/// <summary>
/// Everything the standalone compare document shows (increment 9.1, ADR-0046). Built by the host from a model comparison of the documents module, never from user
/// input. <see cref="Banner"/> is the document's own marking banner (<c>Markings.BannerText()</c>).
/// </summary>
public sealed record ViewerCompareModel
{
    public required string Banner { get; init; }
    public required string Title { get; init; }
    /// <summary>"ready" or "not-found".</summary>
    public required string Status { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<string> Notices { get; init; } = [];
    public string? DocumentNumber { get; init; }
    public string? DocumentName { get; init; }
    public ViewerCompareSide? A { get; init; }
    public ViewerCompareSide? B { get; init; }
    /// <summary>The plain-language summary of the numbers ("Taller by 2.0 mm, volume down 4.1 %").</summary>
    public string? Summary { get; init; }
    public string BackUrl { get; init; } = "/documents";
    public int MaxTriangles { get; init; }

    public string? DistributionStatement => Notices.FirstOrDefault(n => n.StartsWith("DISTRIBUTION STATEMENT", StringComparison.Ordinal));
}

public static class ViewerCompareUi
{
    public static string PageUrl(string number, Guid iterationA, Guid iterationB) =>
        $"/viewer/compare/documents/{Uri.EscapeDataString(number)}?a={iterationA:D}&b={iterationB:D}";

    public static string MeshUrl(string number, Guid fileId) => $"/viewer/mesh/documents/{Uri.EscapeDataString(number)}/files/{fileId:D}";

    /// <summary>Renders the standalone compare document to a complete HTML string (static rendering: no circuit, no Blazor script).</summary>
    public static async Task<string> RenderAsync(IServiceProvider services, ViewerCompareModel model)
    {
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<Components.Pages.Viewer.ViewerCompare>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Model"] = model }));
            return output.ToHtmlString();
        });
    }
}
