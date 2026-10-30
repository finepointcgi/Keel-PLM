using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

#pragma warning disable ASP0006 // the loop numbers REGIONS, not sequence points: a region restarts the numbering inside it

namespace Keel.Web.Localization;

/// <summary>
/// Renders a translated sentence that has data in it ("This part will be a {0}, {1}, {2}.") with the data emphasized, WITHOUT cutting the sentence into pieces: the whole
/// sentence is one resource string, so a language can put the data wherever its grammar needs it. The data is rendered as text (never as markup).
/// </summary>
public static partial class Rich
{
    [GeneratedRegex(@"\{(\d+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    public static RenderFragment Strong(string format, params string[] args) => Build(format, (builder, i) =>
    {
        builder.OpenElement(0, "strong");
        builder.AddContent(1, args[i]);
        builder.CloseElement();
    });

    /// <summary>Like <see cref="Strong"/>, but each {n} is filled with a fragment (a link, a badge) instead of text.</summary>
    public static RenderFragment With(string format, params RenderFragment[] args) => Build(format, (builder, i) => builder.AddContent(0, args[i]));

    // Each piece of the sentence is its own region, so the render tree stays valid however many placeholders a translation has and wherever it puts them.
    private static RenderFragment Build(string format, Action<Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder, int> placeholder) => builder =>
    {
        var region = 0;
        var pos = 0;
        foreach (Match m in Placeholder().Matches(format))
        {
            if (m.Index > pos) { builder.OpenRegion(region++); builder.AddContent(0, format[pos..m.Index]); builder.CloseRegion(); }
            builder.OpenRegion(region++);
            placeholder(builder, int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            builder.CloseRegion();
            pos = m.Index + m.Length;
        }
        if (pos < format.Length) { builder.OpenRegion(region++); builder.AddContent(0, format[pos..]); builder.CloseRegion(); }
    };
}
