using System.Globalization;
using System.Text.RegularExpressions;

namespace Keel.Compliance;

/// <summary>A substance of a list version with its threshold.</summary>
public sealed record ListEntry(Guid SubstanceId, string SubstanceNumber, string Cas, string Name, decimal ThresholdPct, string? Mineral);

/// <summary>An exemption of a list version. <see cref="SubstanceId"/> null means it applies to any substance of the list.</summary>
public sealed record ListExemption(string Code, Guid? SubstanceId, DateOnly? ExpiresOn);

/// <summary>One regulation as evaluated: the list, its kind and the version that applied.</summary>
public sealed record ListVersionData(
    string Code, string Name, string Kind, Guid VersionId, int Version, DateOnly EffectiveFrom, IReadOnlyDictionary<Guid, ListEntry> Entries, IReadOnlyList<ListExemption> Exemptions);

/// <summary>A declared amount of one substance after materials were expanded into their substances.</summary>
public sealed record ExpandedLine(int Ordinal, Guid SubstanceId, decimal Percent, string? ExemptionCode, bool? ConflictFree);

public sealed record RawFinding(int Ordinal, Guid SubstanceId, decimal Percent, decimal ThresholdPct, string Reason, string? ExemptionCode);

public sealed record ListEvaluation(string Result, IReadOnlyList<RawFinding> Findings)
{
    public int Violations => Findings.Count(f => FindingReasons.IsViolation(f.Reason));
}

/// <summary>The part of a declaration the standing of a part needs.</summary>
public sealed record CurrentDeclaration(string Number, string? RevisionScope, DateOnly ExpiresOn, string Status, IReadOnlyDictionary<string, string> ResultByList);

/// <summary>The pure rules of the module (no database): CAS numbers, evaluation against a list version, statuses, standings and expiry stages. Unit-tested on their own.</summary>
public static partial class ComplianceRules
{
    public const int ExpiryWarnDays = 60;
    public const decimal MaxPercent = 100m;

    [GeneratedRegex(@"^[0-9]{2,7}-[0-9]{2}-[0-9]$")]
    private static partial Regex CasPattern();

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9._-]{1,39}$")]
    private static partial Regex MaterialCodePattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9().,/ -]{0,29}$")]
    private static partial Regex ExemptionPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_]{1,30}$")]
    private static partial Regex ListCodePattern();

    [GeneratedRegex(@"^[0-9]{3}-[0-9]{3}-[0-9]$")]
    private static partial Regex EcPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._ -]{0,59}$")]
    private static partial Regex SupplierRefPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/ -]{0,79}$")]
    private static partial Regex MpnRefPattern();

    public static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>A CAS Registry Number: digits in the 2-7, 2, 1 pattern whose check digit is right.</summary>
    public static bool IsValidCas(string? cas)
    {
        if (cas is null || !CasPattern().IsMatch(cas)) return false;
        var parts = cas.Split('-');
        var digits = (parts[0] + parts[1]).Reverse().Select(c => c - '0').ToArray();
        var sum = 0;
        for (var i = 0; i < digits.Length; i++) sum += (i + 1) * digits[i];
        return sum % 10 == parts[2][0] - '0';
    }

    public static bool IsValidEc(string? ec) => ec is null || EcPattern().IsMatch(ec);
    public static bool IsValidMaterialCode(string? code) => code is not null && MaterialCodePattern().IsMatch(code);
    public static bool IsValidExemptionCode(string? code) => code is not null && ExemptionPattern().IsMatch(code);
    public static bool IsValidListCode(string? code) => code is not null && ListCodePattern().IsMatch(code);

    public static IReadOnlyList<string> ValidateSubstance(string? cas, string? name, string? ec, string? mineral)
    {
        var errors = new List<string>();
        if (!IsValidCas(cas)) errors.Add("The CAS number is not valid (for example 7439-92-1; the check digit must be right).");
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) errors.Add("A name of 1 to 200 characters is required.");
        if (!IsValidEc(ec)) errors.Add("The EC number must look like 231-100-4.");
        if (mineral is not null && !Minerals.All.Contains(mineral)) errors.Add("The mineral must be tin, tantalum, tungsten or gold.");
        return errors;
    }

    public static IReadOnlyList<string> ValidateMaterial(string? code, string? name, IReadOnlyList<(string Cas, decimal Fraction)> composition)
    {
        var errors = new List<string>();
        if (!IsValidMaterialCode(code)) errors.Add("The material code uses capital letters, digits, '.', '_' or '-' (2 to 40 characters).");
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) errors.Add("A name of 1 to 200 characters is required.");
        foreach (var c in composition)
            if (c.Fraction is <= 0 or > MaxPercent) errors.Add($"The share of {c.Cas} must be above 0 and at most 100 percent.");
        if (composition.Select(c => c.Cas).Distinct(StringComparer.Ordinal).Count() != composition.Count) errors.Add("A substance appears twice in the composition.");
        if (composition.Sum(c => c.Fraction) > MaxPercent) errors.Add("The shares add up to more than 100 percent.");
        return errors;
    }

    public static IReadOnlyList<string> ValidateListVersion(IReadOnlyList<RegEntryInput> entries, IReadOnlyList<RegExemptionInput> exemptions, string kind)
    {
        var errors = new List<string>();
        foreach (var e in entries)
        {
            if (!IsValidCas(e.CasNumber)) errors.Add($"'{e.CasNumber}' is not a valid CAS number.");
            if (e.ThresholdPct < 0 || e.ThresholdPct > MaxPercent) errors.Add($"The threshold of {e.CasNumber} must be between 0 and 100 percent.");
            if (e.Note is { Length: > 300 }) errors.Add($"The note of {e.CasNumber} is longer than 300 characters.");
        }
        if (entries.Select(e => e.CasNumber).Distinct(StringComparer.Ordinal).Count() != entries.Count) errors.Add("A substance appears twice in the list.");
        foreach (var x in exemptions)
        {
            if (!IsValidExemptionCode(x.Code)) errors.Add($"'{x.Code}' is not a valid exemption code.");
            if (string.IsNullOrWhiteSpace(x.Description) || x.Description.Length > 300) errors.Add($"The exemption {x.Code} needs a description of up to 300 characters.");
            if (x.CasNumber is not null && !IsValidCas(x.CasNumber)) errors.Add($"'{x.CasNumber}' is not a valid CAS number.");
            if (x.CasNumber is not null && entries.All(e => e.CasNumber != x.CasNumber)) errors.Add($"The exemption {x.Code} names {x.CasNumber}, which is not on the list.");
        }
        if (exemptions.Select(x => x.Code.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count() != exemptions.Count) errors.Add("An exemption code appears twice.");
        if (kind == ListKinds.Conflict && exemptions.Count > 0) errors.Add("Conflict mineral lists have no exemptions.");
        return errors;
    }

    /// <summary>The mass percent a line states: the percent itself, or the mass over the mass of the item.</summary>
    public static decimal? LinePercent(decimal? percent, decimal? massMg, decimal? itemMassMg)
    {
        if (percent is not null) return percent;
        if (massMg is not null && itemMassMg is > 0) return massMg / itemMassMg * 100m;
        return null;
    }

    public static IReadOnlyList<string> ValidateDeclaration(DeclarationInput input, DateOnly today)
    {
        var errors = new List<string>();
        if (input.Source is not (DeclarationSources.Internal or DeclarationSources.Supplier)) errors.Add("The source is internal or supplier.");
        if (input.RevisionScope is not null && !Regex.IsMatch(input.RevisionScope, "^[A-Z]{1,3}$")) errors.Add("The revision scope is a revision letter such as A.");
        if (input.SupplierRef is not null && !SupplierRefPattern().IsMatch(input.SupplierRef)) errors.Add("The supplier reference has characters that are not allowed.");
        if (input.MpnRef is not null && !MpnRefPattern().IsMatch(input.MpnRef)) errors.Add("The manufacturer part reference has characters that are not allowed.");
        if (input.Source == DeclarationSources.Supplier && input.SupplierRef is null) errors.Add("A supplier declaration names its supplier (the supplier number).");
        if (input.ItemMassMg is <= 0) errors.Add("The mass of the item must be above 0.");
        if (input.ExpiresOn < input.DeclaredOn) errors.Add("The declaration cannot expire before it was made.");
        if (input.DeclaredOn > today.AddDays(1)) errors.Add("The declaration date is in the future.");
        if (input.Note is { Length: > 500 }) errors.Add("The note is longer than 500 characters.");
        if (input.Lines.Count == 0) errors.Add("A declaration needs at least one line of composition.");
        if (input.Lines.Count > 500) errors.Add("A declaration has at most 500 lines.");
        var i = 0;
        foreach (var l in input.Lines)
        {
            i++;
            if ((l.SubstanceCas is null) == (l.MaterialCode is null)) errors.Add($"Line {i}: give a substance or a material, not both.");
            if (l.SubstanceCas is not null && !IsValidCas(l.SubstanceCas)) errors.Add($"Line {i}: '{l.SubstanceCas}' is not a valid CAS number.");
            if (l.MaterialCode is not null && !IsValidMaterialCode(l.MaterialCode)) errors.Add($"Line {i}: '{l.MaterialCode}' is not a valid material code.");
            if (l.Percent is null && l.MassMg is null) errors.Add($"Line {i}: give a percent or a mass.");
            if (l.Percent is < 0 or > MaxPercent) errors.Add($"Line {i}: the percent must be between 0 and 100.");
            if (l.MassMg is < 0) errors.Add($"Line {i}: the mass cannot be negative.");
            if (l.Percent is null && l.MassMg is not null && input.ItemMassMg is null) errors.Add($"Line {i}: a mass needs the mass of the item to be turned into a percent.");
            if (LinePercent(l.Percent, l.MassMg, input.ItemMassMg) is > MaxPercent) errors.Add($"Line {i}: the mass is more than the mass of the item.");
            if (l.ExemptionCode is not null && !IsValidExemptionCode(l.ExemptionCode)) errors.Add($"Line {i}: '{l.ExemptionCode}' is not a valid exemption code.");
        }
        return errors;
    }

    /// <summary>
    /// Evaluates the lines of a declaration against one version of one list. Each line is its own homogeneous material: its percent is compared with the threshold of its
    /// substance. A restricted or SVHC substance above the threshold is a violation unless the line claims an exemption that this version has, that applies to the substance
    /// and that has not expired. A conflict mineral is reported with its conflict-free status: not conflict-free is a violation, unknown leaves the result unverified.
    /// </summary>
    public static ListEvaluation Evaluate(ListVersionData list, IReadOnlyList<ExpandedLine> lines, DateOnly today)
    {
        var findings = new List<RawFinding>();
        foreach (var line in lines)
        {
            if (!list.Entries.TryGetValue(line.SubstanceId, out var entry)) continue;
            if (line.Percent <= entry.ThresholdPct) continue;
            if (list.Kind == ListKinds.Conflict)
            {
                var reason = line.ConflictFree switch { true => FindingReasons.ConflictFree, false => FindingReasons.NotConflictFree, null => FindingReasons.ConflictUnknown };
                findings.Add(new RawFinding(line.Ordinal, line.SubstanceId, line.Percent, entry.ThresholdPct, reason, null));
                continue;
            }
            if (line.ExemptionCode is null)
            {
                findings.Add(new RawFinding(line.Ordinal, line.SubstanceId, line.Percent, entry.ThresholdPct, FindingReasons.AboveThreshold, null));
                continue;
            }
            var ex = list.Exemptions.FirstOrDefault(x => string.Equals(x.Code, line.ExemptionCode, StringComparison.OrdinalIgnoreCase));
            var why = ex is null ? FindingReasons.ExemptionUnknown
                : ex.SubstanceId is { } sid && sid != line.SubstanceId ? FindingReasons.ExemptionNotApplicable
                : ex.ExpiresOn is { } until && until < today ? FindingReasons.ExemptionExpired
                : FindingReasons.Exempt;
            findings.Add(new RawFinding(line.Ordinal, line.SubstanceId, line.Percent, entry.ThresholdPct, why, line.ExemptionCode));
        }
        var result = findings.Any(f => FindingReasons.IsViolation(f.Reason)) ? EvaluationResults.NonCompliant
            : findings.Any(f => f.Reason == FindingReasons.ConflictUnknown) ? EvaluationResults.Unverified
            : findings.Any(f => f.Reason == FindingReasons.Exempt) ? EvaluationResults.Exempt
            : EvaluationResults.Compliant;
        return new ListEvaluation(result, findings);
    }

    /// <summary>
    /// The stored status of a declaration: withdrawn is final; then expiry (a declaration that ran out says nothing any more), then a violation, then verification.
    /// The same function is used on every write, by the expiry job and by the re-evaluation after a list is published, so the status is always explainable.
    /// </summary>
    public static string DeclarationStatus(bool withdrawn, bool verified, DateOnly expiresOn, DateOnly today, bool violates) =>
        withdrawn ? DeclarationStatuses.Withdrawn
        : expiresOn < today ? DeclarationStatuses.Expired
        : violates ? DeclarationStatuses.NonCompliant
        : verified ? DeclarationStatuses.Verified
        : DeclarationStatuses.Declared;

    /// <summary>The expiry notice due for a declaration: 60, 30 and 7 days before and on expiry; the strongest stage that has been reached.</summary>
    public static string? NoticeStage(DateOnly expires, DateOnly today)
    {
        var days = expires.DayNumber - today.DayNumber;
        return days < 0 ? "expired" : days <= 7 ? "d7" : days <= 30 ? "d30" : days <= ExpiryWarnDays ? "d60" : null;
    }

    /// <summary>A declaration counts for a line when it is not withdrawn and its revision scope covers the line's revision (an unknown revision matches unscoped declarations only).</summary>
    public static bool Covers(string? scope, string? lineRevision) => scope is null || (lineRevision is not null && string.Equals(scope, lineRevision, StringComparison.Ordinal));

    /// <summary>
    /// What a part stands at for one regulation: from its current (unexpired, not withdrawn, covering) declarations, the worst result; no current declaration is a gap, or
    /// expired when declarations exist but have all run out.
    /// </summary>
    public static string PartStanding(IReadOnlyList<CurrentDeclaration> declarations, string listCode, string? revision, DateOnly today)
    {
        var covering = declarations.Where(d => d.Status != DeclarationStatuses.Withdrawn && Covers(d.RevisionScope, revision)).ToList();
        var current = covering.Where(d => d.ExpiresOn >= today).ToList();
        if (current.Count == 0) return covering.Count > 0 ? Standings.Expired : Standings.Gap;
        var results = current.Select(d => d.ResultByList.GetValueOrDefault(listCode)).ToList();
        if (results.Any(r => r == EvaluationResults.NonCompliant)) return Standings.NonCompliant;
        if (results.Any(r => r is null)) return Standings.Gap; // a declaration not evaluated against this regulation yet says nothing about it
        if (results.Any(r => r == EvaluationResults.Unverified)) return Standings.Unverified;
        if (results.Any(r => r == EvaluationResults.Exempt)) return Standings.Exempt;
        return Standings.Compliant;
    }

    /// <summary>The standing of an assembly from its required lines: any non-compliant line decides; otherwise any gap, expiry or unknown makes it incomplete.</summary>
    public static string AssemblyStanding(IEnumerable<string> requiredLineStandings)
    {
        var list = requiredLineStandings.ToList();
        if (list.Count == 0) return Standings.AssemblyNone;
        if (list.Contains(Standings.NonCompliant)) return Standings.AssemblyNonCompliant;
        if (list.Any(s => s is Standings.Gap or Standings.Expired or Standings.Unverified)) return Standings.AssemblyIncomplete;
        return list.Contains(Standings.Exempt) ? Standings.AssemblyExempt : Standings.AssemblyCompliant;
    }

    public static decimal RoundPercent(decimal d) => Math.Round(d, 6, MidpointRounding.AwayFromZero);

    public static string Num(decimal d) => d.ToString("0.########", CultureInfo.InvariantCulture);
}
