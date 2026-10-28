using Keel.Core.Viewing;
using Keel.Documents;
using Keel.Viewing;

namespace Keel.Api.Infrastructure;

/// <summary>Composition of the file viewer (increment 6.0), kept out of Program.cs.</summary>
public static class ViewerSetup
{
    /// <summary>The documents module is the viewer's file source: access control, integrity, PDF marking and audit stay in its code.</summary>
    public static IServiceCollection AddKeelViewerHost(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IViewableFileSource>(sp => sp.GetRequiredService<DocumentService>());
        return services.AddKeelViewer(configuration);
    }

    /// <summary>
    /// Replaces the global Content-Security-Policy with the viewer's own on viewer routes and assets only (see <see cref="ViewerCsp"/>). Runs after
    /// <c>UseSecurityHeaders</c>, which has set the strict global policy; every other path keeps it untouched.
    /// </summary>
    public static IApplicationBuilder UseKeelViewerHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (ViewerCsp.ForPath(context.Request.Path.Value ?? "") is { } policy)
            context.Response.Headers.ContentSecurityPolicy = policy;
        await next(context);
    });
}
