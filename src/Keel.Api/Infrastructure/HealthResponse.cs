using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Keel.Api.Infrastructure;

public static class HealthResponse
{
    /// <summary>Status and description per check; exception details are never exposed.</summary>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(context.Response.Body, new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => new { status = e.Value.Status.ToString(), description = e.Value.Description }),
        });
    }
}
