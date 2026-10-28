using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Keel.Web.Services;

/// <summary>Everything the standalone viewer document shows (increment 6.0, ADR-0027). Built by the host from a viewer description, never from user input.</summary>
public sealed record ViewerPageModel
{
    /// <summary>The marking banner of the OBJECT being viewed (<c>Markings.BannerText()</c>); drawn at the top and the bottom of the document.</summary>
    public required string Banner { get; init; }
    public required string Title { get; init; }
    /// <summary>"ready", "too-large", "no-preview", "blocked" or "not-found".</summary>
    public required string Status { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<string> Notices { get; init; } = [];
    public string? DocumentNumber { get; init; }
    public string? DocumentName { get; init; }
    public string? FileName { get; init; }
    public string FormatLabel { get; init; } = "";
    public string FormatId { get; init; } = "";
    /// <summary>"pdf", "model3d", "image" or "none".</summary>
    public string ViewerType { get; init; } = "none";
    public long SizeBytes { get; init; }
    public string? StoredMediaType { get; init; }
    public string? Sha256 { get; init; }
    public string? IterationLabel { get; init; }
    public string? Warning { get; init; }
    public string BackUrl { get; init; } = "/documents";
    public string? DownloadUrl { get; init; }
    public string? MetaUrl { get; init; }
    public string? ContentUrl { get; init; }
    public int MaxTriangles { get; init; }

    public string Size => ViewerUi.Size(SizeBytes);

    /// <summary>The distribution statement among the notices (it must always be visible), if present.</summary>
    public string? DistributionStatement => Notices.FirstOrDefault(n => n.StartsWith("DISTRIBUTION STATEMENT", StringComparison.Ordinal));
}

public static class ViewerUi
{
    /// <summary>Static viewer assets of this library, served under the viewer's own CSP.</summary>
    public const string AssetBase = "/_content/Keel.Web/viewer/";

    public static string PageUrl(string number, Guid fileId) => $"/viewer/documents/{Uri.EscapeDataString(number)}/files/{fileId:D}";
    public static string PrimaryUrl(string number, string? revision = null) =>
        $"/viewer/documents/{Uri.EscapeDataString(number)}/primary" + (revision is null ? "" : "?revision=" + Uri.EscapeDataString(revision));
    public static string MetaUrl(string number, Guid fileId) => $"/viewer/meta/documents/{Uri.EscapeDataString(number)}/files/{fileId:D}";
    public static string ContentUrl(string number, Guid fileId) => $"/viewer/content/documents/{Uri.EscapeDataString(number)}/files/{fileId:D}";

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " KiB",
        < 1024L * 1024 * 1024 => (bytes / 1024d / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " MiB",
        _ => (bytes / 1024d / 1024 / 1024).ToString("0.##", CultureInfo.InvariantCulture) + " GiB",
    };

    /// <summary>Renders the standalone viewer document to a complete HTML string (static rendering: no circuit, no Blazor script).</summary>
    public static async Task<string> RenderAsync(IServiceProvider services, ViewerPageModel model)
    {
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<Components.Pages.Viewer.ViewerDocument>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Model"] = model }));
            return output.ToHtmlString();
        });
    }
}
