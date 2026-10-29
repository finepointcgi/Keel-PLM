namespace Keel.Web.Components.Shared;

/// <summary>One tab of a <see cref="TabSet"/>. <paramref name="Id"/> is the anchor (#documents); <paramref name="Count"/> is shown as a badge when set.</summary>
public sealed record TabInfo(string Id, string Title, int? Count = null);
