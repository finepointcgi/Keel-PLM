namespace Keel.Web.Components.Visuals;

/// <summary>One choice of a <see cref="ViewSwitch"/>.</summary>
public sealed record ViewOption(string Id, string Title);

/// <summary>A legend entry: <see cref="Path"/> is an outline in a 34 by 22 box, <see cref="Class"/> the colour class, <see cref="Pattern"/> an optional hatching class.</summary>
public sealed record LegendItem(string Text, string Class, string Path, string? Pattern = null)
{
    public const string Rounded = "M8 2 H26 Q32 2 32 8 V14 Q32 20 26 20 H8 Q2 20 2 14 V8 Q2 2 8 2 Z";
    public const string Rect = "M2 2 H32 V20 H2 Z";
    public const string Hexagon = "M8 2 H26 L32 11 L26 20 H8 L2 11 Z";
    public const string Octagon = "M8 2 H26 L32 8 V14 L26 20 H8 L2 14 V8 Z";
    public const string Document = "M2 2 H24 L32 8 V20 H2 Z";
    public const string Pill = "M11 2 H23 A9 9 0 0 1 23 20 H11 A9 9 0 0 1 11 2 Z";
}
