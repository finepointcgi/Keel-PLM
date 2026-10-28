using System.Text.RegularExpressions;
using Keel.Core;

namespace Keel.Migration.Mapping;

/// <summary>The outcome of evaluating the markings rules for one object. <see cref="Markings"/> is null exactly when <see cref="Errors"/> is not empty.</summary>
public sealed record MarkingsDecision(Markings? Markings, bool Defaulted, IReadOnlyList<string> MatchedRules, IReadOnlyList<string> Errors);

/// <summary>
/// Evaluates the markings rules (the security-critical part of the mapping, ADR-0023). The rules of this class are:
/// <list type="bullet">
/// <item>Every matching rule contributes; contributions combine restrictively (highest jurisdiction, union of CUI categories, most restrictive
/// distribution statement, intersection of export authorizations). Two contributions that disagree on a scalar (program, owning org, USML
/// category, ECCN, controlling office, reason) are an error: the object stays unimported.</item>
/// <item>Every property context of the object (master, each iteration) is evaluated and the results combine the same way, so a later iteration
/// can only tighten what an earlier one said.</item>
/// <item>Fail closed: when no rule assigns an export jurisdiction the object has no determinable markings and is an error, unless
/// <c>defaultMarkings</c> is configured. The default is added as one more contribution, so it can never loosen what rules said.</item>
/// <item>The result must pass <see cref="Markings.Create"/>.</item>
/// </list>
/// </summary>
public sealed class MarkingsMapper
{
    private readonly MarkingsMapping _config;

    public MarkingsMapper(MarkingsMapping config) => _config = config;

    private sealed record Contribution(string Rule, ExportJurisdiction? Jurisdiction, string? Eccn, string? Usml, IReadOnlyList<string> Cui,
        DistributionStatement? Dist, string? Office, string? Reason, string? Program, string? Org, IReadOnlyList<string>? Auths);

    /// <param name="contexts">One property bag per context (master overlaid with revision and iteration properties).</param>
    /// <param name="appliesTo">"part", "document" or "change".</param>
    public MarkingsDecision Decide(IEnumerable<IReadOnlyDictionary<string, string?>> contexts, string appliesTo)
    {
        var contributions = new List<Contribution>();
        var matched = new SortedSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var ctx in contexts)
        {
            foreach (var rule in _config.Rules)
            {
                if (rule.AppliesTo.Count > 0 && !rule.AppliesTo.Contains(appliesTo, StringComparer.OrdinalIgnoreCase)) continue;
                if (!Matches(rule.When, ctx)) continue;
                matched.Add(rule.Name);
                var c = ToContribution(rule.Name, rule.Set, ctx, errors);
                if (c is not null) contributions.Add(c);
            }
        }
        var defaulted = false;
        if (errors.Count == 0 && !contributions.Any(c => c.Jurisdiction is not null))
        {
            if (_config.DefaultMarkings is null)
                return new MarkingsDecision(null, false, [.. matched], ["No rule assigns an export jurisdiction and no defaultMarkings is configured: the markings cannot be determined."]);
            var d = ToContribution("defaultMarkings", _config.DefaultMarkings, new Dictionary<string, string?>(), errors);
            if (d is not null) contributions.Add(d);
            defaulted = true;
        }
        if (errors.Count > 0) return new MarkingsDecision(null, defaulted, [.. matched], errors);
        var result = Combine(contributions, errors);
        return result is null
            ? new MarkingsDecision(null, defaulted, [.. matched], errors)
            : new MarkingsDecision(result, defaulted, [.. matched], []);
    }

    private static Markings? Combine(List<Contribution> cs, List<string> errors)
    {
        static int Rank(ExportJurisdiction j) => j switch { ExportJurisdiction.Itar => 2, ExportJurisdiction.Ear => 1, _ => 0 };
        var jurisdiction = cs.Where(c => c.Jurisdiction is not null).Select(c => c.Jurisdiction!.Value).DefaultIfEmpty(ExportJurisdiction.None).MaxBy(Rank);
        string? one(IEnumerable<string?> values, string what)
        {
            var distinct = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count > 1) errors.Add($"Rules disagree on {what}: {string.Join(" / ", distinct)}.");
            return distinct.Count == 1 ? distinct[0] : null;
        }
        var atJurisdiction = cs.Where(c => c.Jurisdiction == jurisdiction).ToList();
        string? usml = null, eccn = null;
        if (jurisdiction == ExportJurisdiction.Itar) usml = one(atJurisdiction.Select(c => c.Usml), "the USML category");
        if (jurisdiction == ExportJurisdiction.Ear)
        {
            // EAR99 is the weakest EAR classification: a rule with a real ECCN wins over EAR99, two different real ECCNs disagree.
            var eccns = atJurisdiction.Select(c => c.Eccn?.ToUpperInvariant()).Where(e => e is not null && e != Markings.Ear99);
            eccn = one(eccns, "the ECCN") ?? (atJurisdiction.Any(c => c.Eccn is not null) ? Markings.Ear99 : null);
        }
        var cui = cs.SelectMany(c => c.Cui).ToList();
        var dist = cs.Where(c => c.Dist is not null).Select(c => c.Dist!.Value).DefaultIfEmpty(DistributionStatement.A).Max();
        var atDist = cs.Where(c => c.Dist == dist).ToList();
        var office = one(atDist.Select(c => c.Office), "the controlling office");
        var reason = one(atDist.Select(c => c.Reason), "the distribution reason");
        var program = one(cs.Select(c => c.Program), "the program");
        var org = one(cs.Select(c => c.Org), "the owning organization");
        var auths = cs.Where(c => c.Auths is not null).Select(c => c.Auths!).ToList();
        IEnumerable<string> authorizations = auths.Count == 0 ? [] : auths.Skip(1).Aggregate(auths[0].AsEnumerable(), (a, b) => a.Intersect(b, StringComparer.OrdinalIgnoreCase));
        if (errors.Count > 0) return null;
        if (org is null) { errors.Add("No rule assigns an owning organization."); return null; }
        var made = Markings.Create(jurisdiction, dist, org, eccn, usml, cui, office, reason, program, authorizations);
        if (!made.IsSuccess) { errors.AddRange(made.Errors); return null; }
        return made.Value;
    }

    private static Contribution? ToContribution(string rule, MarkingSpec s, IReadOnlyDictionary<string, string?> ctx, List<string> errors)
    {
        ExportJurisdiction? j = null;
        if (s.ExportJurisdiction is not null)
        {
            if (Enum.TryParse<ExportJurisdiction>(s.ExportJurisdiction, true, out var jj) && Enum.IsDefined(jj)) j = jj;
            else { errors.Add($"Rule '{rule}': unknown exportJurisdiction '{s.ExportJurisdiction}'."); return null; }
        }
        DistributionStatement? d = null;
        if (s.DistributionStatement is not null)
        {
            if (Enum.TryParse<DistributionStatement>(s.DistributionStatement, true, out var dd) && Enum.IsDefined(dd)) d = dd;
            else { errors.Add($"Rule '{rule}': unknown distributionStatement '{s.DistributionStatement}'."); return null; }
        }
        var program = s.Program;
        if (s.ProgramFromProperty is not null)
        {
            ctx.TryGetValue(s.ProgramFromProperty, out var pv);
            if (string.IsNullOrWhiteSpace(pv)) { errors.Add($"Rule '{rule}': property '{s.ProgramFromProperty}' (program) is empty on this object."); return null; }
            program = pv;
        }
        return new Contribution(rule, j, s.Eccn, s.UsmlCategory, s.Cui ?? [], d, s.ControllingOffice, s.DistributionReason, program, s.OwningOrg, s.ExportAuthorizations);
    }

    private static bool Matches(MarkingCondition c, IReadOnlyDictionary<string, string?> ctx)
    {
        if (c.Property is not null)
        {
            var found = TryGet(ctx, c.Property, out var v);
            if (c.Exists is { } ex && ex != (found && !string.IsNullOrWhiteSpace(v))) return false;
            if (c.EqualTo is not null && !(found && string.Equals(v?.Trim(), c.EqualTo, StringComparison.OrdinalIgnoreCase))) return false;
            if (c.In is not null && !(found && c.In.Contains(v?.Trim() ?? "", StringComparer.OrdinalIgnoreCase))) return false;
            if (c.Matches is not null && !(found && v is not null && Regex.IsMatch(v, c.Matches, RegexOptions.None, TimeSpan.FromSeconds(1)))) return false;
            if (c.Exists is null && c.EqualTo is null && c.In is null && c.Matches is null && c.All is null) return found && !string.IsNullOrWhiteSpace(v);
        }
        else if (c.All is null) return false; // a condition without property and without all[] never matches (fail closed in the sense of "no contribution")
        return c.All is null || c.All.All(x => Matches(x, ctx));
    }

    private static bool TryGet(IReadOnlyDictionary<string, string?> ctx, string name, out string? value)
    {
        if (ctx.TryGetValue(name, out value)) return true;
        foreach (var kv in ctx)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
        value = null;
        return false;
    }
}
