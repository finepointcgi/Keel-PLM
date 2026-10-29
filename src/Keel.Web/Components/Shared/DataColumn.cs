using Microsoft.AspNetCore.Components;

namespace Keel.Web.Components.Shared;

/// <summary>
/// One column of a <see cref="DataTable{TItem}"/>. <see cref="Cell"/> renders the cell (links, chips); <see cref="Text"/> is the plain value. A column with a
/// <see cref="SortValue"/> sorts the rows of the CURRENT page in the browser's circuit; a column with a <see cref="ServerSortKey"/> asks the page to sort on the
/// server (the table raises <c>OnServerSort</c>). A column with neither is not sortable.
/// </summary>
public sealed class DataColumn<TItem>
{
    /// <summary>Stable id (kept in the user's saved layout, so do not rename it).</summary>
    public required string Key { get; init; }
    public required string Title { get; init; }
    public RenderFragment<TItem>? Cell { get; init; }
    public Func<TItem, string?>? Text { get; init; }
    public Func<TItem, IComparable?>? SortValue { get; init; }
    public string? ServerSortKey { get; init; }
    /// <summary>Hidden until the user chooses it in the column chooser.</summary>
    public bool DefaultHidden { get; init; }
    /// <summary>Always shown (the identifying column): the column chooser does not offer it.</summary>
    public bool Required { get; init; }
    public string? CssClass { get; init; }

    public bool Sortable => SortValue is not null || ServerSortKey is not null;
}
