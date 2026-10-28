using System.Globalization;
using Keel.Core;

namespace Keel.Bom;

/// <summary>A unit to resolve a structure for: serial and lot inside a context, and/or a UTC date (A19). At least one value is required.</summary>
public sealed record EffectivityRequest(string? Context, long? Serial = null, long? Lot = null, long? Day = null)
{
    public static EffectivityRequest ForSerial(string context, long serial) => new(context, Serial: serial);
    public static EffectivityRequest ForLot(string context, long lot) => new(context, Lot: lot);
    public static EffectivityRequest ForDate(DateOnly date) => new(null, Day: EffectivityRange.DayNumber(date));

    public string Describe()
    {
        var parts = new List<string>();
        if (Serial is { } s) parts.Add($"serial {s.ToString(CultureInfo.InvariantCulture)}");
        if (Lot is { } l) parts.Add($"lot {l.ToString(CultureInfo.InvariantCulture)}");
        if (Day is { } d) parts.Add($"date {EffectivityRange.DayOf(d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        return string.Join(", ", parts) + (Context is null ? "" : $" ({Context})");
    }

    /// <summary>Validation shared by the service and the UI; returns user-facing errors.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Serial is null && Lot is null && Day is null) errors.Add("Give a serial number, a lot or a date.");
        if ((Serial is not null || Lot is not null) && string.IsNullOrWhiteSpace(Context)) errors.Add("Serial numbers and lots need an end-item context.");
        if (Serial is < 1 || Lot is < 1) errors.Add("Serial numbers and lots start at 1.");
        if (Day is { } d && (d < EffectivityRules.MinDay || d > EffectivityRules.MaxDay)) errors.Add("The date must be between 1970-01-01 and 2200-12-31.");
        return errors;
    }
}

/// <summary>
/// Pure rules of effectivity (ADR-0021): parsing, range conflicts for alternatives of one find number, and the supersession rule applied
/// when a change notice releases a revision that is effective from a unit onwards.
/// </summary>
public static class EffectivityRules
{
    public const long MaxSerial = 9_999_999_999L;
    public const long MinDay = 0;
    public static readonly long MaxDay = EffectivityRange.DayNumber(new DateOnly(2200, 12, 31));

    /// <summary>
    /// Parses user input into a range. <paramref name="blocks"/> maps the context's block names to lot numbers (null for none known).
    /// Both ends are optional but not both missing; serials and lots are positive integers, lots may be a block name; dates are yyyy-MM-dd.
    /// </summary>
    public static (EffectivityRange? Range, string? Error) Parse(EffectivityInput input, IReadOnlyDictionary<string, int>? blocks = null)
    {
        var kind = input.Kind?.Trim().ToLowerInvariant();
        if (!EffectivityKinds.IsValid(kind)) return (null, "Effectivity type must be serial, lot or date.");
        var context = string.IsNullOrWhiteSpace(input.Context) ? null : input.Context.Trim().ToUpperInvariant();
        if (kind == EffectivityKinds.Date && context is not null) return (null, "Date effectivity has no end-item context.");
        if (kind != EffectivityKinds.Date && context is null) return (null, $"{Capitalize(kind!)} effectivity needs an end-item context such as ORCA-POD.");

        var (from, e1) = ParseEnd(kind!, input.From, "From", blocks);
        var (to, e2) = ParseEnd(kind!, input.To, "To", blocks);
        if (e1 is not null) return (null, e1);
        if (e2 is not null) return (null, e2);
        if (from is null && to is null) return (null, "Give a From, a To or both; an effectivity open on both ends is no effectivity.");
        if (from is { } f && to is { } t && f > t) return (null, "From must not be after To.");
        return (new EffectivityRange(kind!, context, from, to), null);
    }

    private static (long? Value, string? Error) ParseEnd(string kind, string? raw, string label, IReadOnlyDictionary<string, int>? blocks)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return (null, null);
        switch (kind)
        {
            case EffectivityKinds.Date:
                if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return (null, $"{label}: '{text}' is not a date; use yyyy-MM-dd (UTC).");
                var day = EffectivityRange.DayNumber(date);
                return day < MinDay || day > MaxDay ? (null, $"{label}: the date must be between 1970-01-01 and 2200-12-31.") : (day, null);
            case EffectivityKinds.Lot when blocks is not null && blocks.TryGetValue(text, out var block):
                return (block, null);
            default:
                if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > MaxSerial)
                    return (null, kind == EffectivityKinds.Lot
                        ? $"{label}: '{text}' is not a lot number or a block name of this context."
                        : $"{label}: '{text}' is not a serial number; use a whole number from 1 to {MaxSerial.ToString("N0", CultureInfo.InvariantCulture)}.");
                return (n, null);
        }
    }

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>
    /// Two lines under one find number are alternatives and must be told apart by effectivity. They conflict when either has none (it always
    /// applies), when they use different dimensions or contexts (it cannot be decided which applies), or when the ranges share a value.
    /// </summary>
    public static bool Conflict(EffectivityRange? a, EffectivityRange? b) => a is null || b is null || a.Kind != b.Kind || !string.Equals(a.Context, b.Context, StringComparison.Ordinal) || a.Overlaps(b);

    /// <summary>Why two alternatives conflict, in words for the editor.</summary>
    public static string ConflictMessage(int find, EffectivityRange? existing, EffectivityRange? candidate)
    {
        if (existing is null && candidate is null) return $"Find number {find} is already used on this parent.";
        if (existing is null) return $"Find number {find} is already used by a line that applies to every unit. Give that line an effectivity first, or choose another find number.";
        if (candidate is null) return $"Find number {find} already has a line with effectivity ({existing.Describe()}); this line needs an effectivity that does not overlap it.";
        if (existing.Kind != candidate.Kind || !string.Equals(existing.Context, candidate.Context, StringComparison.Ordinal))
            return $"Find number {find} has a line effective by {existing.Describe()}; alternatives of one find number must use the same type and context ({candidate.Describe()} given).";
        return $"Find number {find} already has a line effective for {existing.Describe()}, which overlaps {candidate.Describe()}.";
    }

    /// <summary>One Released revision of a master as the supersession rule sees it.</summary>
    public sealed record ReleasedRevision(Guid Id, string Revision, EffectivityRange? Range);

    /// <summary>
    /// The supersession rule (CFG-9, ADR-0021). A new revision becomes Released effective from unit N onward (<paramref name="range"/>). For every
    /// other Released revision: with no range given the revision is superseded completely (Obsolete, the classic behaviour). Otherwise its served
    /// range is cut back to units below N: a revision with no stored range serves "everything" and is closed to N-1; one whose served range
    /// starts at or above N (or becomes empty) is Obsolete; the rest stay Released with the closed range. A serial or lot below 1 cannot be served.
    /// </summary>
    public static SupersessionPlan Supersede(IReadOnlyList<ReleasedRevision> others, EffectivityRange? range)
    {
        if (range is null)
            return new SupersessionPlan([.. others.Select(o => o.Id)], [], others.Count == 0 ? [] : ["Every other Released revision is superseded (no effectivity was given)."]);

        if (range.From is not { } n || range.To is not null)
            return new SupersessionPlan([], [], [], "A revision's effectivity must start at a unit and stay open at the top (from N onward).");
        var obsolete = new List<Guid>();
        var narrowed = new List<RevisionEffectivityChange>();
        var notes = new List<string>();
        foreach (var o in others)
        {
            if (o.Range is { } r && (r.Kind != range.Kind || !string.Equals(r.Context, range.Context, StringComparison.Ordinal)))
                return new SupersessionPlan([], [], [], $"Revision {o.Revision} is effective by {r.Describe()}; the new revision's effectivity ({range.Describe()}) must use the same type and context.");

            var start = o.Range?.From;
            var floor = range.Kind == EffectivityKinds.Date ? long.MinValue : 1;
            // Nothing left below N: the revision starts at or above N, or N is the first unit there is.
            if ((start is { } s && s >= n) || (start is null && n <= floor))
            {
                obsolete.Add(o.Id);
                notes.Add($"Revision {o.Revision} serves nothing below {range.Describe()} and becomes Obsolete.");
                continue;
            }
            var currentTo = o.Range?.To;
            if (currentTo is { } t && t <= n - 1) continue; // already closed below N
            var after = new EffectivityRange(range.Kind, range.Context, start, n - 1);
            narrowed.Add(new RevisionEffectivityChange(o.Id, o.Revision, o.Range, after));
            notes.Add($"Revision {o.Revision} stays Released for {after.Describe()}.");
        }
        return new SupersessionPlan(obsolete, narrowed, notes);
    }
}
