using Keel.Web.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Keel.Web.Localization;

public static class LocalizationServices
{
    /// <summary>Registers the circuit language and the resource-backed localizers (ADR 0057). No external service: .resx resources and satellite assemblies only.</summary>
    public static IServiceCollection AddKeelLocalization(this IServiceCollection services)
    {
        services.AddScoped<UiLanguage>();
        services.AddScoped(typeof(IStringLocalizer<>), typeof(KeelLocalizer<>));
        return services;
    }

    /// <summary>The resource key of a label that comes from data (navigation, palette): the area plus the English text lower-cased with dashes ("Nav.item.my-tasks").</summary>
    public static string Key(string area, string english) =>
        area + "." + string.Join('-', english.ToLowerInvariant().Split([' ', '/', ',', '.', '’', '\''], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The marker types of the resource files, for tests and tools.</summary>
    public static readonly IReadOnlyList<Type> ResourceTypes = [typeof(ShellStrings), typeof(CommonStrings), typeof(PageStrings)];
}

/// <summary>
/// For the small shared controls that every page and every component test renders (breadcrumbs, pager, skeleton, confirmation dialog, data table): the localizer
/// and language of the circuit when they are registered, otherwise English. A host that has not registered localization therefore still renders these controls,
/// in English, instead of failing; the real host always registers it (<see cref="LocalizationServices.AddKeelLocalization"/>).
/// </summary>
public static class Loc
{
    public static UiLanguage Language(IServiceProvider services) => services.GetService<UiLanguage>() ?? new UiLanguage();

    public static IStringLocalizer<T> Strings<T>(IServiceProvider services) => services.GetService<IStringLocalizer<T>>() ?? new KeelLocalizer<T>(Language(services));
}
