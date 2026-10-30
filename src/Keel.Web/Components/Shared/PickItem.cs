namespace Keel.Web.Components.Shared;

/// <summary>One candidate of an <see cref="ObjectPicker"/>. <paramref name="Note"/> says why a candidate cannot be chosen (shown instead of silently hiding it).</summary>
public sealed record PickItem(string Number, string Name, string Banner, string? Note = null, bool Disabled = false);
