using System.Globalization;
using Keel.Core;

namespace Keel.Records;

/// <summary>A record class as the matcher needs it (pure data; the matcher touches no database).</summary>
public sealed record ClassRule(
    string Code, string Name, int Priority, string[] Kinds, string[] Types, string[] Programs, string[] Jurisdictions, string[] Cui, string[] Statements,
    int RetentionMonths, string Trigger, string Disposition)
{
    public static ClassRule From(ClassInput c) =>
        new(c.Code, c.Name, c.Priority, c.Kinds, c.Types, c.Programs, c.Jurisdictions, c.Cui, c.Statements, c.RetentionMonths, c.TriggerEvent, c.Disposition);
}

/// <summary>What the rules need to know about one object (FEAT-REC-5: lifecycle state names are the stored keys Released / Obsolete).</summary>
public sealed record ObjectFacts(
    string Kind, Guid Id, string Number, string Name, string? Type, Markings Markings, DateTimeOffset? ReleasedAt, DateTimeOffset? ObsoleteAt, bool OpenWork, DateOnly? DeferredUntil);

public sealed record ClassAssignment(string Code, string Name, string Trigger, DateOnly TriggerDate, DateOnly EligibleOn, string Disposition);

/// <summary>
/// The retention rules as pure functions, so they can be proven without a database. The first matching class (lowest priority number, then code) decides an
/// object's class; an object that matches but has no trigger date yet (not released, program not closed) or still has work in progress is simply not due.
/// </summary>
public static class RetentionRules
{
    public static bool Matches(ClassRule rule, ObjectFacts f)
    {
        if (rule.Kinds.Length > 0 && !rule.Kinds.Contains(f.Kind, StringComparer.Ordinal)) return false;
        if (rule.Types.Length > 0 && (f.Type is null || !rule.Types.Contains(f.Type, StringComparer.OrdinalIgnoreCase))) return false;
        if (rule.Programs.Length > 0 && (f.Markings.Program is null || !rule.Programs.Contains(f.Markings.Program, StringComparer.Ordinal))) return false;
        if (rule.Jurisdictions.Length > 0 && !rule.Jurisdictions.Contains(f.Markings.ExportJurisdiction.ToString(), StringComparer.Ordinal)) return false;
        if (rule.Cui.Length > 0 && !rule.Cui.Any(f.Markings.CuiCategories.Contains)) return false;
        if (rule.Statements.Length > 0 && !rule.Statements.Contains(f.Markings.DistributionStatement.ToString(), StringComparer.Ordinal)) return false;
        return true;
    }

    /// <summary>The date the retention period runs from, or null when the trigger has not happened (or cannot be known) for this object.</summary>
    public static DateOnly? TriggerDate(string trigger, ObjectFacts f, Func<string, string, DateOnly?> programEvent) => trigger switch
    {
        TriggerEvents.Release => f.ReleasedAt is { } r ? DateOnly.FromDateTime(r.UtcDateTime) : null,
        TriggerEvents.Obsolescence => f.ObsoleteAt is { } o ? DateOnly.FromDateTime(o.UtcDateTime) : null,
        TriggerEvents.ProgramClose or TriggerEvents.ContractEnd => f.Markings.Program is { } p ? programEvent(p, trigger) : null,
        _ => null,
    };

    /// <summary>The object's class and eligibility date, or null when no class matches or the object is not yet on a retention clock.</summary>
    public static ClassAssignment? Assign(IEnumerable<ClassRule> rules, ObjectFacts f, Func<string, string, DateOnly?> programEvent)
    {
        var rule = rules.OrderBy(r => r.Priority).ThenBy(r => r.Code, StringComparer.Ordinal).FirstOrDefault(r => Matches(r, f));
        if (rule is null || f.OpenWork) return null;
        if (TriggerDate(rule.Trigger, f, programEvent) is not { } start) return null;
        var due = start.AddMonths(rule.RetentionMonths);
        if (f.DeferredUntil is { } until && until > due) due = until;
        return new ClassAssignment(rule.Code, rule.Name, rule.Trigger, start, due, rule.Disposition);
    }

    /// <summary>Validation of a class as an administrator enters it. Returns user-facing messages; empty when valid.</summary>
    public static IReadOnlyList<string> Validate(ClassInput c)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(c.Code) || !System.Text.RegularExpressions.Regex.IsMatch(c.Code, "^[A-Z0-9][A-Z0-9._-]{1,29}$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)))
            errors.Add("The class code must be 2 to 30 characters: capital letters, digits, '.', '_' or '-'.");
        if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 200) errors.Add("The class needs a name of at most 200 characters.");
        if (c.Priority is < 1 or > 10000) errors.Add("The priority must be between 1 and 10000.");
        if (c.RetentionMonths is < 0 or > 1200) errors.Add("The retention period must be between 0 and 1200 months.");
        if (!TriggerEvents.All.Contains(c.TriggerEvent)) errors.Add("Choose a trigger event.");
        if (!DispositionActions.All.Contains(c.Disposition)) errors.Add("Choose a disposition action.");
        if (c.Kinds.Any(k => !RecordKinds.IsKnown(k))) errors.Add("Object kinds must be document or part.");
        if (c.Jurisdictions.Any(j => j is not ("None" or "Ear" or "Itar"))) errors.Add("Export jurisdictions must be None, Ear or Itar.");
        if (c.Statements.Any(s => s is not ("A" or "B" or "C" or "D" or "E" or "F"))) errors.Add("Distribution statements must be A to F.");
        if (c.TriggerEvent is TriggerEvents.ProgramClose or TriggerEvents.ContractEnd && c.Programs.Length == 0)
            errors.Add("A program-based trigger needs at least one program to match: the event is recorded per program.");
        if ((c.Citation?.Length ?? 0) > 500) errors.Add("The citation must be at most 500 characters.");
        if ((c.Notes?.Length ?? 0) > 2000 || (c.Description?.Length ?? 0) > 2000) errors.Add("Notes and description must be at most 2000 characters.");
        return errors;
    }

    public static string Describe(ClassInput c) => string.Create(CultureInfo.InvariantCulture,
        $"{c.Code} {c.Name}: {c.RetentionMonths} months after {TriggerEvents.Label(c.TriggerEvent).ToLowerInvariant()}, then {DispositionActions.Label(c.Disposition).ToLowerInvariant()}");
}
