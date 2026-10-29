namespace Keel.Web.Components.Shared;

/// <summary>One level of the breadcrumb trail. The last crumb is the current page and is shown without a link.</summary>
public sealed record Crumb(string Label, string? Href = null);
