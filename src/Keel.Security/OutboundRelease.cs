using System.Collections.Immutable;
using Keel.Core;

namespace Keel.Security;

/// <summary>Who receives the data. Statements of DoDI 5230.24 limit the categories that may receive them.</summary>
public enum RecipientCategory
{
    /// <summary>Anyone: approved for public release.</summary>
    Public = 0,
    /// <summary>A U.S. Government agency other than DoD.</summary>
    Usg = 1,
    /// <summary>A DoD component.</summary>
    Dod = 2,
    /// <summary>A contractor of a U.S. Government agency other than DoD.</summary>
    UsgContractor = 3,
    /// <summary>A DoD contractor.</summary>
    DodContractor = 4,
    /// <summary>A foreign government or foreign entity.</summary>
    Foreign = 5,
}

/// <summary>
/// What a receiving system or a TDP recipient is cleared for (ADR-0025). Declared by an administrator; the outbound release check
/// evaluates an object's markings against it and nothing else decides whether data leaves the system.
/// </summary>
public sealed record ClearanceProfile(
    string Name,
    RecipientCategory Category,
    /// <summary>Every operator and user of the receiving system is a U.S. person.</summary>
    bool UsPersonsOnly,
    ImmutableSortedSet<string> AllowedJurisdictions,
    /// <summary>Empty = every ECCN. Otherwise exact codes or prefixes ending in '*' (9A*). EAR99 is allowed whenever EAR is.</summary>
    ImmutableSortedSet<string> AllowedEccns,
    /// <summary>Empty = every USML category. Same pattern rules as <see cref="AllowedEccns"/>.</summary>
    ImmutableSortedSet<string> AllowedUsmlCategories,
    ImmutableSortedSet<string> AllowedCuiCategories,
    ImmutableSortedSet<string> AllowedPrograms,
    ImmutableSortedSet<DistributionStatement> AllowedStatements,
    /// <summary>Export authorizations (license or agreement ids) the recipient holds.</summary>
    ImmutableSortedSet<string> ExportAuthorizations)
{
    /// <summary>Problems that make the profile unusable; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (AllowedStatements.Contains(DistributionStatement.F))
            errors.Add("Distribution statement F cannot be listed: it is never released automatically.");
        foreach (var j in AllowedJurisdictions)
            if (j is not ("None" or "Ear" or "Itar")) errors.Add($"Unknown export jurisdiction '{j}'.");
        if (Category == RecipientCategory.Public && (AllowedStatements.Any(s => s != DistributionStatement.A) || AllowedJurisdictions.Any(j => j != "None")))
            errors.Add("A public recipient can only be cleared for statement A and data with no export jurisdiction.");
        if (AllowedJurisdictions.Contains("Itar") && !AllowedCuiCategories.Contains("EXPT"))
            errors.Add("ITAR data carries CUI category EXPT: add EXPT to the allowed CUI categories or remove ITAR.");
        if (AllowedJurisdictions.Contains("Ear") && !AllowedCuiCategories.Contains("EXPT") && AllowedEccns.Any(e => !string.Equals(e, Markings.Ear99, StringComparison.OrdinalIgnoreCase)))
            errors.Add("EAR data with an ECCN other than EAR99 carries CUI category EXPT: add EXPT to the allowed CUI categories.");
        foreach (var p in AllowedEccns.Concat(AllowedUsmlCategories))
            if (p.Length == 0 || p.Contains('*', StringComparison.Ordinal) && p.IndexOf('*', StringComparison.Ordinal) != p.Length - 1)
                errors.Add($"'{p}' is not a valid code pattern: use an exact code or a prefix ending in '*'.");
        return errors;
    }
}

public sealed record OutboundDecision(bool Permitted, ImmutableArray<string> Reasons)
{
    public static readonly OutboundDecision Permit = new(true, []);
}

/// <summary>
/// The outbound release check: a pure function from (recipient profile, object markings) to a decision with stable reason codes. The
/// counterpart of <see cref="AccessPolicy"/> for data leaving the system. It never returns a partial or redacted result: an object is
/// released as it is or not at all.
/// <para>
/// DoDI 5230.24 (ASSUMPTION INT-6): A public; B U.S. Government agencies only; C U.S. Government agencies and their contractors; D DoD and
/// DoD contractors; E DoD components only; F the controlling office decides each release, so it is never released automatically. A foreign
/// recipient is cleared for B-E only through an export authorization that the object itself lists (the controlling office's approval for
/// that agreement) and that the recipient holds.
/// </para>
/// </summary>
public static class OutboundRelease
{
    public const string DistributionFManual = "outbound.distribution_f_manual_release";
    public const string DistributionNotAllowed = "outbound.distribution_not_allowed";
    public const string RecipientCategoryNotAllowed = "outbound.recipient_category_not_allowed";
    public const string JurisdictionNotAllowed = "outbound.jurisdiction_not_allowed";
    public const string EccnNotAllowed = "outbound.eccn_not_allowed";
    public const string UsmlNotAllowed = "outbound.usml_not_allowed";
    public const string UsPersonRequired = "outbound.us_person_or_authorization_required";
    public const string CuiCategoryNotAllowed = "outbound.cui_category_not_allowed";
    public const string ProgramNotAllowed = "outbound.program_not_allowed";

    public static OutboundDecision Evaluate(ClearanceProfile profile, Markings markings)
    {
        var reasons = ImmutableArray.CreateBuilder<string>();
        var matchingAuthorization = profile.ExportAuthorizations.Overlaps(markings.ExportAuthorizations);

        // Distribution statement.
        if (markings.DistributionStatement == DistributionStatement.F)
            reasons.Add(DistributionFManual);
        else
        {
            if (!profile.AllowedStatements.Contains(markings.DistributionStatement))
                reasons.Add($"{DistributionNotAllowed}:{markings.DistributionStatement}");
            if (!StatementAllowsCategory(markings.DistributionStatement, profile.Category, matchingAuthorization))
                reasons.Add($"{RecipientCategoryNotAllowed}:{profile.Category}");
        }

        // Export control: nationality of the recipient, or an authorization the object lists and the recipient holds (same shape as AccessPolicy).
        if (markings.IsExportControlled && !profile.UsPersonsOnly && !matchingAuthorization)
            reasons.Add(UsPersonRequired);

        var jurisdiction = markings.ExportJurisdiction.ToString();
        if (!profile.AllowedJurisdictions.Contains(jurisdiction))
            reasons.Add($"{JurisdictionNotAllowed}:{jurisdiction}");
        else
        {
            if (markings.ExportJurisdiction == ExportJurisdiction.Ear && !string.Equals(markings.Eccn, Markings.Ear99, StringComparison.Ordinal) &&
                profile.AllowedEccns.Count > 0 && !Matches(profile.AllowedEccns, markings.Eccn!))
                reasons.Add(EccnNotAllowed);
            if (markings.ExportJurisdiction == ExportJurisdiction.Itar && profile.AllowedUsmlCategories.Count > 0 && !Matches(profile.AllowedUsmlCategories, markings.UsmlCategory!))
                reasons.Add(UsmlNotAllowed);
        }

        foreach (var category in markings.CuiCategories)
            if (!profile.AllowedCuiCategories.Contains(category))
                reasons.Add($"{CuiCategoryNotAllowed}:{category}");

        if (markings.Program is not null && !profile.AllowedPrograms.Contains(markings.Program))
            reasons.Add(ProgramNotAllowed);

        return reasons.Count == 0 ? OutboundDecision.Permit : new OutboundDecision(false, reasons.ToImmutable());
    }

    /// <summary>The DoDI 5230.24 table: which recipient categories each statement allows.</summary>
    public static bool StatementAllowsCategory(DistributionStatement statement, RecipientCategory category, bool recipientHoldsListedAuthorization = false) => statement switch
    {
        DistributionStatement.A => true,
        DistributionStatement.B => category is RecipientCategory.Usg or RecipientCategory.Dod || ForeignWithAuthorization(category, recipientHoldsListedAuthorization),
        DistributionStatement.C => category is RecipientCategory.Usg or RecipientCategory.Dod or RecipientCategory.UsgContractor or RecipientCategory.DodContractor
            || ForeignWithAuthorization(category, recipientHoldsListedAuthorization),
        DistributionStatement.D => category is RecipientCategory.Dod or RecipientCategory.DodContractor || ForeignWithAuthorization(category, recipientHoldsListedAuthorization),
        DistributionStatement.E => category is RecipientCategory.Dod || ForeignWithAuthorization(category, recipientHoldsListedAuthorization),
        _ => false, // F: never automatic
    };

    private static bool ForeignWithAuthorization(RecipientCategory category, bool held) => category == RecipientCategory.Foreign && held;

    /// <summary>Exact (case-insensitive) match or a prefix pattern ending in '*'.</summary>
    public static bool Matches(IEnumerable<string> patterns, string value) => patterns.Any(p =>
        p.EndsWith('*') ? value.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase) : string.Equals(p, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Human-readable text for a reason code, free of anything about the object beyond its own markings.</summary>
    public static string Describe(string reason)
    {
        var (code, arg) = reason.Contains(':', StringComparison.Ordinal) ? (reason[..reason.IndexOf(':', StringComparison.Ordinal)], reason[(reason.IndexOf(':', StringComparison.Ordinal) + 1)..]) : (reason, null);
        return code switch
        {
            DistributionFManual => "Distribution statement F: the controlling office decides each release; it is never released automatically.",
            DistributionNotAllowed => $"The recipient is not cleared for distribution statement {arg}.",
            RecipientCategoryNotAllowed => $"Distribution statement of the data does not allow a recipient of category {arg}.",
            JurisdictionNotAllowed => $"The recipient is not cleared for export jurisdiction {arg}.",
            EccnNotAllowed => "The ECCN of the data is not in the recipient's allowed ECCNs.",
            UsmlNotAllowed => "The USML category of the data is not in the recipient's allowed categories.",
            UsPersonRequired => "Export-controlled data needs a recipient operated by U.S. persons or an export authorization listed on the data.",
            CuiCategoryNotAllowed => $"The recipient is not cleared for CUI category {arg}.",
            ProgramNotAllowed => "The data belongs to a program the recipient is not cleared for.",
            _ => reason,
        };
    }
}
