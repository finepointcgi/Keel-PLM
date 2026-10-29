using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Documents;
using Keel.Parts;
using Keel.Workflow;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Web.Services.Visuals;

public static class VisualsRegistration
{
    /// <summary>
    /// Registers the relationship pictures (UX-3, ADR-0029): the browser module, the timeline merge and one <see cref="ITimelineSource"/> from each owning module.
    /// Each source applies the caller's access inside its own module; Keel.Web only composes them.
    /// </summary>
    public static IServiceCollection AddKeelVisuals(this IServiceCollection services)
    {
        services.AddScoped<VisualsJs>();
        services.AddSingleton<TimelineService>();
        services.AddSingleton<ITimelineSource, PartTimelineSource>();
        services.AddSingleton<ITimelineSource, DocumentTimelineSource>();
        services.AddSingleton<ITimelineSource, WorkflowTimelineSource>();
        services.AddSingleton<ITimelineSource, ChangeTimelineSource>();
        services.AddSingleton<ITimelineSource, BomTimelineSource>();
        return services;
    }
}
