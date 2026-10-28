using System.Text.RegularExpressions;

namespace Keel.Admin;

public sealed record LifecycleStateDef(string State, string Label, int SortOrder, bool IsInitial, bool IsLocked, bool IsFinal);
public sealed record LifecycleTransitionDef(string From, string To, string Via);

/// <summary>One lifecycle version: a list of states and the transitions between them.</summary>
public sealed record LifecycleDefinition(
    string Family, string Name, string ObjectType, int Version, string Status, IReadOnlyList<LifecycleStateDef> States, IReadOnlyList<LifecycleTransitionDef> Transitions,
    string? ChangeNote = null);

public sealed record LifecycleVersionInfo(int Version, string Status, DateTimeOffset CreatedAt, string CreatedBy, string? ChangeNote, bool IsCurrent);

public sealed record LifecycleFamilyInfo(string Family, string Name, string ObjectType, int CurrentVersion, bool IsSystem, bool Editable, IReadOnlyList<LifecycleVersionInfo> Versions);

/// <summary>Revisions per state of one lifecycle version, so the editor can say "N objects use this state".</summary>
public sealed record StateUsage(string State, long Revisions, long CheckedOut);

public sealed record LifecycleVersionView(LifecycleDefinition Definition, IReadOnlyList<StateUsage> Usage, long TotalRevisions, bool IsCurrent, bool Editable);

/// <summary>Result of validating a draft: blocking errors, warnings and a plain-language description of what publishing would do.</summary>
public sealed record LifecycleValidation(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, IReadOnlyList<string> Effects);

public sealed record MigrationPlan(string Family, int FromVersion, int ToVersion, IReadOnlyList<StateUsage> StatesInUse, IReadOnlyDictionary<string, string> SuggestedMapping,
    long Revisions, long ActiveReviews);

public sealed record MigrationResult(string Family, int FromVersion, int ToVersion, long RevisionsMoved, long ObjectsOnNewVersion);

/// <summary>Pure lifecycle validation (ADR-0022). The database keeps its own constraints as the safety net.</summary>
public static partial class LifecycleRules
{
    public static readonly IReadOnlyList<string> Vias = ["manual", "workflow", "change"];
    public static readonly IReadOnlyList<string> VersionedObjectTypes = ["part", "document"];

    /// <summary>States the engines refer to by name (review gateways, change gateway, services). They may be relabelled and reordered, never removed.</summary>
    public static IReadOnlyList<(string State, bool? Locked, bool? Initial)> ProtectedStates(string objectType) => objectType switch
    {
        "part" or "document" => [("InWork", false, true), ("UnderReview", true, false), ("Released", true, false), ("Obsolete", true, false)],
        _ => [],
    };

    /// <summary>Transitions that code relies on, with the tag it requires (the part review and change gateways load them by from/to/via).</summary>
    public static IReadOnlyList<LifecycleTransitionDef> ProtectedTransitions(string objectType) => objectType switch
    {
        "part" =>
        [
            new("InWork", "UnderReview", "workflow"), new("UnderReview", "InWork", "workflow"), new("UnderReview", "Released", "workflow"),
            new("Released", "Obsolete", "change"), new("InWork", "Obsolete", "change"),
        ],
        _ => [],
    };

    /// <param name="current">The version this one replaces: workflow-tagged transitions it already has are kept without a template check (the seeded document lifecycle has one).</param>
    public static LifecycleValidation Validate(LifecycleDefinition d, bool workflowBound, LifecycleDefinition? current = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(d.Name) || d.Name.Trim().Length > 120) errors.Add("The lifecycle name is required (at most 120 characters).");
        if (d.States.Count == 0) errors.Add("A lifecycle needs at least one state.");
        if (d.States.Count > 40) errors.Add("A lifecycle has at most 40 states.");

        foreach (var s in d.States)
        {
            if (!StateKey().IsMatch(s.State ?? "")) errors.Add($"State key '{s.State}' must start with a letter and use only letters and digits (at most 40 characters).");
            if (string.IsNullOrWhiteSpace(s.Label) || s.Label.Trim().Length > 60) errors.Add($"State '{s.State}' needs a label (at most 60 characters).");
        }
        foreach (var dup in d.States.GroupBy(s => s.State, StringComparer.Ordinal).Where(g => g.Count() > 1))
            errors.Add($"State key '{dup.Key}' is used more than once.");
        foreach (var dup in d.States.GroupBy(s => s.Label?.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"The label '{dup.Key}' is used for more than one state.");

        var initial = d.States.Where(s => s.IsInitial).ToList();
        if (initial.Count != 1) errors.Add($"Exactly one initial state is required (found {initial.Count}).");
        else if (initial[0].IsLocked) errors.Add("The initial state cannot be locked: new objects must be editable.");

        var keys = d.States.Select(s => s.State).ToHashSet(StringComparer.Ordinal);
        foreach (var t in d.Transitions)
        {
            if (!keys.Contains(t.From)) errors.Add($"Transition {t.From} → {t.To} starts at a state that does not exist.");
            if (!keys.Contains(t.To)) errors.Add($"Transition {t.From} → {t.To} ends at a state that does not exist.");
            if (t.From == t.To) errors.Add($"Transition {t.From} → {t.To} loops onto itself.");
            if (!Vias.Contains(t.Via)) errors.Add($"Transition {t.From} → {t.To} has an unknown tag '{t.Via}' (use manual, workflow or change).");
        }
        foreach (var dup in d.Transitions.GroupBy(t => (t.From, t.To)).Where(g => g.Count() > 1))
            errors.Add($"Transition {dup.Key.From} → {dup.Key.To} is defined more than once.");

        var inherited = current?.Transitions.Where(t => t.Via == "workflow").Select(t => (t.From, t.To)).ToHashSet() ?? [];
        if (!workflowBound && d.Transitions.Any(t => t.Via == "workflow" && !inherited.Contains((t.From, t.To))))
            errors.Add($"A transition is tagged 'workflow' but no workflow template is active for {d.ObjectType} objects. Publish and pin a template first (Administration, Workflow templates).");

        if (initial.Count == 1 && d.Transitions.All(t => keys.Contains(t.From) && keys.Contains(t.To)))
        {
            var reached = new HashSet<string>(StringComparer.Ordinal) { initial[0].State };
            var queue = new Queue<string>([initial[0].State]);
            while (queue.Count > 0)
            {
                var from = queue.Dequeue();
                foreach (var t in d.Transitions.Where(t => t.From == from && reached.Add(t.To))) queue.Enqueue(t.To);
            }
            var unreachable = d.States.Where(s => !reached.Contains(s.State)).Select(s => s.State).ToList();
            if (unreachable.Count > 0) errors.Add($"State(s) not reachable from the initial state: {string.Join(", ", unreachable)}.");
            var deadEnds = d.States.Where(s => !s.IsFinal && s.State != initial[0].State && !d.Transitions.Any(t => t.From == s.State)).Select(s => s.State).ToList();
            if (deadEnds.Count > 0) warnings.Add($"State(s) with no way out that are not marked final: {string.Join(", ", deadEnds)}.");
        }

        if (VersionedObjectTypes.Contains(d.ObjectType) && d.States.All(s => !s.IsLocked))
            errors.Add("A release-style lifecycle needs at least one locked state: released data must be frozen.");

        foreach (var (state, locked, init) in ProtectedStates(d.ObjectType))
        {
            var s = d.States.FirstOrDefault(x => x.State == state);
            if (s is null) { errors.Add($"State '{state}' cannot be removed: review, release and change processing refer to it by name."); continue; }
            if (locked is { } l && s.IsLocked != l) errors.Add($"State '{state}' must stay {(l ? "locked" : "unlocked")}.");
            if (init is { } i && s.IsInitial != i) errors.Add(i ? $"State '{state}' must stay the initial state." : $"State '{state}' cannot be the initial state.");
        }
        foreach (var req in ProtectedTransitions(d.ObjectType))
            if (!d.Transitions.Any(t => t.From == req.From && t.To == req.To && t.Via == req.Via))
                errors.Add($"The {req.Via} transition {req.From} → {req.To} is required by the review and change processes.");

        return new LifecycleValidation(errors, warnings, []);
    }

    /// <summary>What publishing <paramref name="next"/> after <paramref name="current"/> changes, in plain words.</summary>
    public static List<string> Diff(LifecycleDefinition current, LifecycleDefinition next)
    {
        var effects = new List<string>();
        var before = current.States.ToDictionary(s => s.State, StringComparer.Ordinal);
        var after = next.States.ToDictionary(s => s.State, StringComparer.Ordinal);
        foreach (var s in next.States.Where(s => !before.ContainsKey(s.State))) effects.Add($"Added state {s.Label} ({s.State}).");
        foreach (var s in current.States.Where(s => !after.ContainsKey(s.State))) effects.Add($"Removed state {s.Label} ({s.State}); objects in it stay on the old version until migrated.");
        foreach (var s in next.States.Where(s => before.ContainsKey(s.State)))
        {
            var o = before[s.State];
            if (o.Label != s.Label) effects.Add($"Renamed {o.Label} to {s.Label}.");
            if (o.IsLocked != s.IsLocked) effects.Add($"{s.Label} is now {(s.IsLocked ? "locked" : "unlocked")}.");
            if (o.IsFinal != s.IsFinal) effects.Add($"{s.Label} is now {(s.IsFinal ? "" : "not ")}final.");
            if (o.IsInitial != s.IsInitial) effects.Add($"{s.Label} is {(s.IsInitial ? "now" : "no longer")} the initial state.");
        }
        var bt = current.Transitions.ToDictionary(t => (t.From, t.To));
        var at = next.Transitions.ToDictionary(t => (t.From, t.To));
        foreach (var t in next.Transitions.Where(t => !bt.ContainsKey((t.From, t.To)))) effects.Add($"Added transition {t.From} → {t.To} ({t.Via}).");
        foreach (var t in current.Transitions.Where(t => !at.ContainsKey((t.From, t.To)))) effects.Add($"Removed transition {t.From} → {t.To}.");
        foreach (var t in next.Transitions.Where(t => bt.TryGetValue((t.From, t.To), out var o) && o.Via != t.Via)) effects.Add($"Transition {t.From} → {t.To} is now {t.Via} (was {bt[(t.From, t.To)].Via}).");
        return effects;
    }

    /// <summary>
    /// Validates a state mapping for moving objects from one version to another. Every state in use must map to a state of the target; a locked
    /// state may not be mapped to an unlocked one (that would silently unlock released data); a state with checked-out objects may not map to a locked one.
    /// </summary>
    public static List<string> ValidateMapping(LifecycleDefinition from, LifecycleDefinition to, IReadOnlyDictionary<string, string> mapping, IReadOnlyList<StateUsage> inUse)
    {
        var errors = new List<string>();
        var target = to.States.ToDictionary(s => s.State, StringComparer.Ordinal);
        var source = from.States.ToDictionary(s => s.State, StringComparer.Ordinal);
        foreach (var u in inUse.Where(u => u.Revisions > 0))
        {
            if (!mapping.TryGetValue(u.State, out var t) || string.IsNullOrEmpty(t)) { errors.Add($"State {u.State} is in use by {u.Revisions} revision(s) and needs a target state."); continue; }
            if (!target.TryGetValue(t, out var to2)) { errors.Add($"Target state '{t}' does not exist in version {to.Version}."); continue; }
            if (source.TryGetValue(u.State, out var from2))
            {
                if (from2.IsLocked && !to2.IsLocked) errors.Add($"{u.State} is locked but {t} is not: mapping would unlock released data. Map it to a locked state.");
                if (!from2.IsLocked && to2.IsLocked && u.CheckedOut > 0) errors.Add($"{u.CheckedOut} revision(s) in {u.State} are checked out; check them in before mapping to the locked state {t}.");
            }
        }
        foreach (var k in mapping.Keys.Where(k => !source.ContainsKey(k))) errors.Add($"Mapped state '{k}' does not exist in version {from.Version}.");
        return errors;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,39}$")]
    private static partial Regex StateKey();
}
