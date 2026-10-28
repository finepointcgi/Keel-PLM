using System.Collections.Immutable;

namespace Keel.Core;

/// <summary>Units of measure accepted for parts and BOM quantities. Codes are case-sensitive (mm vs MM).</summary>
public static class UnitsOfMeasure
{
    public const string Each = "EA";

    // ASSUMPTION: a fixed list until the Phase 2 admin UI makes units configurable.
    public static readonly ImmutableSortedSet<string> All = ImmutableSortedSet.Create(StringComparer.Ordinal,
        "EA", "SET", "mm", "cm", "m", "in", "ft", "g", "kg", "lb", "mL", "L", "gal", "m2", "m3");

    public static bool IsValid(string? unit) => unit is not null && All.Contains(unit);
}
