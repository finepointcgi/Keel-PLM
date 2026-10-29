using System.Globalization;

namespace Keel.Variants;

public sealed record OptionValueDef(string Key, string Name, bool Retired = false);

/// <summary>An option family: a choice such as Engine with its values. A required family needs a value in every valid configuration.</summary>
public sealed record OptionFamilyDef(string Key, string Name, bool Required, bool Retired, IReadOnlyList<OptionValueDef> Values);

public enum RuleKind { Requires, Excludes, Mutex }

/// <summary>One option (a family key and a value key).</summary>
public sealed record OptionRef(string Family, string Value)
{
    public override string ToString() => $"{Family} = {Value}";
}

/// <summary>
/// A rule over options, as data. Requires: choosing <see cref="Subject"/> needs every option in <see cref="Members"/>. Excludes: choosing the subject rules out every
/// member. Mutex (no subject): at most one of the members can be chosen.
/// </summary>
public sealed record OptionRuleDef(Guid Id, RuleKind Kind, OptionRef? Subject, IReadOnlyList<OptionRef> Members, string? Reason);

public sealed record OptionModelDef(IReadOnlyList<OptionFamilyDef> Families, IReadOnlyList<OptionRuleDef> Rules);

public enum FindingSeverity { Error, Warning, Info }

/// <summary>A result of model validation: what is wrong or worth knowing, in plain words.</summary>
public sealed record ModelFinding(FindingSeverity Severity, string Code, string Message);

public sealed record ConfigurationCheck(bool Valid, IReadOnlyList<string> Problems);

/// <param name="Sample">Up to <see cref="RuleEngine.SampleSize"/> valid configurations, in a stable order.</param>
/// <param name="ValidCount">Number of valid configurations found.</param>
/// <param name="Complete">False when the search stopped at its work limit; counts and dead-option analysis are then lower bounds.</param>
public sealed record ConfigurationSpace(IReadOnlyList<IReadOnlyDictionary<string, string>> Sample, long ValidCount, bool Complete, IReadOnlySet<(string Family, string Value)> UsedOptions);

/// <summary>
/// The small deterministic rule engine of ADR-0039: validates an option model (references, contradictions, options that no valid configuration can use, a model with no valid
/// configuration at all), checks one configuration against the rules, and enumerates valid configurations by backtracking over the families in key order with early pruning.
/// Nothing here reads a database or the clock; the same model and selection always give the same answer.
/// </summary>
public static class RuleEngine
{
    public const int SampleSize = 500;
    public const long MaxNodes = 250_000;

    // ---------------------------------------------------------------- text

    public static string Describe(OptionRuleDef rule, Func<OptionRef, string>? name = null)
    {
        string N(OptionRef o) => name?.Invoke(o) ?? $"{o.Family} is {o.Value}";
        var members = string.Join(rule.Kind == RuleKind.Mutex ? " or " : " and ", rule.Members.Select(N));
        return rule.Kind switch
        {
            RuleKind.Requires => $"Choosing {N(rule.Subject!)} needs {members}.",
            RuleKind.Excludes => $"Choosing {N(rule.Subject!)} rules out {string.Join(" and ", rule.Members.Select(N))}.",
            _ => $"At most one of these can be chosen: {members}.",
        };
    }

    // ---------------------------------------------------------------- model validation

    public static IReadOnlyList<ModelFinding> ValidateModel(OptionModelDef model, bool analyze = true)
    {
        var findings = new List<ModelFinding>();
        var families = model.Families.ToDictionary(f => f.Key, StringComparer.Ordinal);
        bool Exists(OptionRef o) => families.TryGetValue(o.Family, out var f) && f.Values.Any(v => v.Key == o.Value);

        foreach (var f in model.Families.Where(f => !f.Retired))
            if (f.Required && f.Values.All(v => v.Retired))
                findings.Add(new(FindingSeverity.Error, "family.no_values", $"The required option '{f.Name}' has no usable values."));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in model.Rules)
        {
            var text = Describe(rule);
            foreach (var o in rule.Members.Concat(rule.Subject is null ? [] : [rule.Subject]).Where(o => !Exists(o)))
                findings.Add(new(FindingSeverity.Error, "rule.unknown_option", $"A rule names the option {o}, which does not exist. {text}"));
            if (rule.Subject is { } s && rule.Members.Any(m => m == s))
                findings.Add(new(FindingSeverity.Error, "rule.self", $"A rule relates an option to itself. {text}"));
            if (rule.Kind == RuleKind.Requires && rule.Subject is { } rs)
                foreach (var m in rule.Members.Where(m => m.Family == rs.Family && m.Value != rs.Value))
                    findings.Add(new(FindingSeverity.Error, "rule.same_family", $"{rs} needs {m}, but one family allows one value only, so {rs} could never be chosen."));
            if (rule.Kind == RuleKind.Mutex)
            {
                foreach (var g in rule.Members.GroupBy(m => m.Family).Where(g => g.Count() > 1))
                    findings.Add(new(FindingSeverity.Info, "rule.redundant_mutex", $"Two values of '{g.Key}' are listed as mutually exclusive; one family already allows one value only."));
                if (rule.Members.Distinct().Count() != rule.Members.Count)
                    findings.Add(new(FindingSeverity.Warning, "rule.duplicate_member", "A mutually exclusive rule lists the same option twice."));
            }
            var key = $"{rule.Kind}|{rule.Subject}|{string.Join(",", rule.Members.Select(m => m.ToString()).Order(StringComparer.Ordinal))}";
            if (!seen.Add(key)) findings.Add(new(FindingSeverity.Warning, "rule.duplicate", $"The same rule is listed twice. {text}"));
        }
        // requires X and excludes X for one subject
        foreach (var sub in model.Rules.Where(r => r.Subject is not null).GroupBy(r => r.Subject!))
        {
            var needs = sub.Where(r => r.Kind == RuleKind.Requires).SelectMany(r => r.Members).ToHashSet();
            var bans = sub.Where(r => r.Kind == RuleKind.Excludes).SelectMany(r => r.Members).ToHashSet();
            foreach (var clash in needs.Intersect(bans))
                findings.Add(new(FindingSeverity.Error, "rule.contradiction", $"{sub.Key} both needs and rules out {clash}, so {sub.Key} could never be chosen."));
        }

        if (analyze && !findings.Any(f => f.Severity == FindingSeverity.Error && f.Code is "rule.unknown_option" or "family.no_values"))
        {
            var space = Enumerate(model);
            if (space.ValidCount == 0 && space.Complete) findings.Add(new(FindingSeverity.Error, "model.no_valid_configuration", "No valid configuration exists: the rules together rule out every combination."));
            else if (space.Complete)
                foreach (var f in model.Families.Where(f => !f.Retired))
                    foreach (var v in f.Values.Where(v => !v.Retired && !space.UsedOptions.Contains((f.Key, v.Key))))
                        findings.Add(new(FindingSeverity.Warning, "option.unusable", $"The option {f.Name} = {v.Name} cannot appear in any valid configuration under the current rules."));
            else findings.Add(new(FindingSeverity.Info, "model.analysis_limited", $"The model has too many combinations to check them all; unusable options are not reported."));
        }
        return findings;
    }

    // ---------------------------------------------------------------- one configuration

    /// <summary>Checks a selection (family key to value key) against the families and the rules.</summary>
    public static ConfigurationCheck Check(OptionModelDef model, IReadOnlyDictionary<string, string> selection)
    {
        var problems = new List<string>();
        var families = model.Families.ToDictionary(f => f.Key, StringComparer.Ordinal);
        foreach (var (fk, vk) in selection.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!families.TryGetValue(fk, out var f)) { problems.Add($"The option '{fk}' does not exist."); continue; }
            var v = f.Values.FirstOrDefault(x => x.Key == vk);
            if (v is null) problems.Add($"'{vk}' is not a value of '{f.Name}'.");
            else if (v.Retired || f.Retired) problems.Add($"{f.Name} = {v.Name} is retired and cannot be used.");
        }
        foreach (var f in model.Families.Where(f => f.Required && !f.Retired && !selection.ContainsKey(f.Key)))
            problems.Add($"Choose a value for '{f.Name}'.");
        foreach (var rule in model.Rules)
        {
            bool On(OptionRef o) => selection.TryGetValue(o.Family, out var v) && v == o.Value;
            var broken = rule.Kind switch
            {
                RuleKind.Requires => On(rule.Subject!) && rule.Members.Any(m => !On(m)),
                RuleKind.Excludes => On(rule.Subject!) && rule.Members.Any(On),
                _ => rule.Members.Distinct().Count(On) > 1,
            };
            if (broken) problems.Add(rule.Reason is { Length: > 0 } r ? $"{Describe(rule)} {r}" : Describe(rule));
        }
        return new ConfigurationCheck(problems.Count == 0, problems);
    }

    // ---------------------------------------------------------------- all valid configurations

    public static ConfigurationSpace Enumerate(OptionModelDef model, long maxNodes = MaxNodes)
    {
        var fams = model.Families.Where(f => !f.Retired).OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < fams.Count; i++) index[fams[i].Key] = i;
        var choices = fams.Select(f => f.Values.Where(v => !v.Retired).Select(v => v.Key).ToList()).ToList();
        var assign = new string?[fams.Count];
        var rules = model.Rules.Where(r => r.Members.Concat(r.Subject is null ? [] : [r.Subject]).All(o => index.ContainsKey(o.Family))).ToList();
        var sample = new List<IReadOnlyDictionary<string, string>>();
        var used = new HashSet<(string, string)>();
        long count = 0, nodes = 0;
        var complete = true;

        bool Decided(OptionRef o, int upTo) => index[o.Family] < upTo;
        bool On(OptionRef o) => assign[index[o.Family]] == o.Value;

        // true when a rule is already broken given the families decided so far (indices below upTo)
        bool Broken(int upTo)
        {
            foreach (var r in rules)
            {
                switch (r.Kind)
                {
                    case RuleKind.Requires:
                        if (Decided(r.Subject!, upTo) && On(r.Subject!) && r.Members.Any(m => Decided(m, upTo) && !On(m))) return true;
                        break;
                    case RuleKind.Excludes:
                        if (Decided(r.Subject!, upTo) && On(r.Subject!) && r.Members.Any(m => Decided(m, upTo) && On(m))) return true;
                        break;
                    default:
                        if (r.Members.Where(m => Decided(m, upTo)).Distinct().Count(On) > 1) return true;
                        break;
                }
            }
            return false;
        }

        void Walk(int i)
        {
            if (!complete) return;
            if (++nodes > maxNodes) { complete = false; return; }
            if (i == fams.Count)
            {
                count++;
                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var k = 0; k < fams.Count; k++)
                    if (assign[k] is { } v) { dict[fams[k].Key] = v; used.Add((fams[k].Key, v)); }
                if (sample.Count < SampleSize) sample.Add(dict);
                return;
            }
            foreach (var v in choices[i])
            {
                assign[i] = v;
                if (!Broken(i + 1)) Walk(i + 1);
                if (!complete) return;
            }
            if (!fams[i].Required)
            {
                assign[i] = null;
                if (!Broken(i + 1)) Walk(i + 1);
            }
            assign[i] = null;
        }
        // A required family with no usable values makes the space empty (Walk finds nothing); an empty model has exactly one (empty) configuration.
        Walk(0);
        return new ConfigurationSpace(sample, count, complete, used);
    }

    /// <summary>The size of the unconstrained space (product of the family sizes, +1 for optional families), saturating.</summary>
    public static long RawSpace(OptionModelDef model)
    {
        long total = 1;
        foreach (var f in model.Families.Where(f => !f.Retired))
        {
            var n = f.Values.Count(v => !v.Retired) + (f.Required ? 0 : 1);
            total = n == 0 ? 0 : total > long.MaxValue / Math.Max(1, n) ? long.MaxValue : total * n;
        }
        return total;
    }

    public static string Format(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
