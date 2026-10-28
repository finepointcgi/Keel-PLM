using Keel.Core.Viewing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Viewing;

public static class ViewerModule
{
    /// <summary>
    /// Registers the viewer service and its options (<c>Keel:Viewer</c>). The file source is the documents module's <see cref="IViewableFileSource"/>,
    /// registered by the host; <see cref="IFileViewGate"/> implementations from other modules are picked up automatically.
    /// </summary>
    public static IServiceCollection AddKeelViewer(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ViewerOptions.Section).Get<ViewerOptions>() ?? new ViewerOptions();
        var errors = options.Validate();
        if (errors.Count > 0) throw new InvalidOperationException("Invalid viewer configuration: " + string.Join(" ", errors));
        services.AddSingleton(options);
        services.AddSingleton<ViewerService>();
        return services;
    }
}
