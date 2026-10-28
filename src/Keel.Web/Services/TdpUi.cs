namespace Keel.Web.Services;

/// <summary>Routes the Technical Data Package UI shares with the API host.</summary>
public static class TdpUi
{
    /// <summary>Form POST (session cookie plus antiforgery token) that streams the signed package; no token appears in a URL.</summary>
    public const string ExportRoute = "ui/tdp/export";
}
