using System.Globalization;

namespace Keel.Web.Services;

/// <summary>UI helpers for documents (increment 1.3).</summary>
public static class DocumentUi
{
    /// <summary>
    /// Cookie-authenticated download route for the UI (mapped by Keel.Api next to the REST download). The browser
    /// follows a plain link with its session cookie, so no token ever appears in a URL, and the response streams with
    /// Content-Disposition and the X-Keel-Markings header.
    /// </summary>
    public const string DownloadRoute = "/downloads/documents/{number}/files/{fileId:guid}";

    public static string DownloadUrl(string number, Guid fileId) => $"downloads/documents/{Uri.EscapeDataString(number)}/files/{fileId:D}";

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " KiB",
        < 1024L * 1024 * 1024 => (bytes / 1024d / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " MiB",
        _ => (bytes / 1024d / 1024 / 1024).ToString("0.##", CultureInfo.InvariantCulture) + " GiB",
    };

    public static string LinkTypeLabel(string linkType) => linkType switch
    {
        "describes" => "Describes",
        "references" => "References",
        _ => linkType,
    };
}
