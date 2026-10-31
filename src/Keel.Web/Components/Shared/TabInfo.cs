namespace Keel.Web.Components.Shared;

/// <summary>One tab of a <see cref="TabSet"/>. <paramref name="Id"/> is the anchor (#documents); <paramref name="Count"/> is shown as a badge when set. Tabs that share a <paramref name="Group"/> (its shown title) appear as one tab with a second row of the members; a group with one visible member is an ordinary tab. Ids, deep links and the address-bar fragment are unchanged.</summary>
public sealed record TabInfo(string Id, string Title, int? Count = null, string? Group = null);
