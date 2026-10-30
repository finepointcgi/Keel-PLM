namespace Keel.Web.Services;

/// <summary>Addresses of the redline pages and the cookie-authenticated report download (increment 9.5).</summary>
public static class RedlineUi
{
    public const string ReportRoute = "/downloads/redline";

    public static string Query(string left, Guid? leftVersion, string right, Guid? rightVersion)
    {
        var q = $"left={Uri.EscapeDataString(left)}&right={Uri.EscapeDataString(right)}";
        if (leftVersion is { } l) q += $"&leftVersion={l:D}";
        if (rightVersion is { } r) q += $"&rightVersion={r:D}";
        return q;
    }

    public static string ReportUrl(string left, Guid? leftVersion, string right, Guid? rightVersion, string format) =>
        "downloads/redline?" + Query(left, leftVersion, right, rightVersion) + "&format=" + format;

    public static string PageUrl(string left, Guid? leftVersion, string right, Guid? rightVersion) => "compare?" + Query(left, leftVersion, right, rightVersion);
}
