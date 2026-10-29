using System.Collections.Immutable;

namespace Keel.Workspace;

/// <summary>The kinds of object a personal pointer (recent item, favorite, saved search scope) can name.</summary>
public static class ObjectKinds
{
    public const string Part = "part";
    public const string Document = "document";
    public const string Change = "change";
    public const string Baseline = "baseline";
    public const string Mbom = "mbom";
    public const string Ncr = "ncr";
    public const string Capa = "capa";
    public const string Deviation = "deviation";
    public const string Supplier = "supplier";

    public static readonly ImmutableArray<string> All = [Part, Document, Change, Baseline, Mbom, Supplier, Ncr, Capa, Deviation];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);

    /// <summary>The route of an object's page.</summary>
    public static string Path(string kind, string number) => kind switch
    {
        Part => "parts/" + Uri.EscapeDataString(number),
        Document => "documents/" + Uri.EscapeDataString(number),
        Change => "changes/" + Uri.EscapeDataString(number),
        Baseline => "baselines/" + Uri.EscapeDataString(number),
        Mbom => "mboms/" + Uri.EscapeDataString(number),
        Ncr => "quality/ncrs/" + Uri.EscapeDataString(number),
        Capa => "quality/capas/" + Uri.EscapeDataString(number),
        Deviation => "quality/deviations/" + Uri.EscapeDataString(number),
        Supplier => "suppliers/" + Uri.EscapeDataString(number),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown object kind."),
    };

    public static string Label(string kind) => kind switch
    {
        Part => "Part",
        Document => "Document",
        Change => "Change",
        Baseline => "Baseline",
        Mbom => "Manufacturing BOM",
        Ncr => "NCR",
        Capa => "CAPA",
        Deviation => "Deviation",
        Supplier => "Supplier",
        _ => kind,
    };
}

public static class ThemeNames
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";
    public const string HighContrast = "high-contrast";
    public static readonly ImmutableArray<string> All = [System, Light, Dark, HighContrast];
}

public static class DensityNames
{
    public const string Comfortable = "comfortable";
    public const string Compact = "compact";
    public static readonly ImmutableArray<string> All = [Comfortable, Compact];
}

/// <summary>Display preferences of one user. The default is what a user who never chose anything gets.</summary>
public sealed record UserPreferences(string Theme, string Density, bool NavCollapsed)
{
    public static readonly UserPreferences Default = new(ThemeNames.System, DensityNames.Comfortable, false);

    /// <summary>Unknown values fall back to the defaults, so a damaged row can never break the page shell.</summary>
    public static UserPreferences Normalize(string? theme, string? density, bool navCollapsed) =>
        new(theme is not null && ThemeNames.All.Contains(theme) ? theme : ThemeNames.System,
            density is not null && DensityNames.All.Contains(density) ? density : DensityNames.Comfortable, navCollapsed);
}

/// <summary>Column choices, density and sort of one table (identified by a stable id such as "parts") for one user.</summary>
public sealed record TablePreferences(IReadOnlyList<string> HiddenColumns, string? Density, string? SortColumn, bool SortDescending)
{
    public static readonly TablePreferences Default = new([], null, null, false);
}

/// <summary>A pointer to an object. It carries no name or markings: those are read again, under the viewer's access, whenever it is shown.</summary>
public sealed record ObjectPointer(string Kind, string Number, DateTimeOffset At);

/// <summary>A saved search. The text may itself be sensitive, so it is only ever returned to its owner.</summary>
public sealed record SavedSearch(Guid Id, string Name, string Scope, string Query, string FiltersJson, DateTimeOffset CreatedAt);

public enum SaveSearchResult { Saved, Replaced, LimitReached, Invalid }

/// <summary>The caps that bound personal data per user.</summary>
public static class WorkspaceLimits
{
    public const int RecentItems = 50;
    public const int Favorites = 100;
    public const int SavedSearches = 50;
    public const int TablePreferences = 200;
    public const int HiddenColumns = 40;
    /// <summary>A repeated view of the same object within this window does not write again.</summary>
    public static readonly TimeSpan ViewDebounce = TimeSpan.FromSeconds(60);
}

/// <summary>The part of the workspace service that the shell and the tables use, as an interface so components can be tested without a database.</summary>
public interface IPreferenceStore
{
    Task<UserPreferences> GetPreferencesAsync(Keel.Core.Subject subject, CancellationToken ct = default);
    Task<UserPreferences> SetPreferencesAsync(Keel.Core.Subject subject, UserPreferences preferences, CancellationToken ct = default);
    Task<TablePreferences> GetTablePreferencesAsync(Keel.Core.Subject subject, string tableId, CancellationToken ct = default);
    Task<bool> SetTablePreferencesAsync(Keel.Core.Subject subject, string tableId, TablePreferences prefs, CancellationToken ct = default);
}
