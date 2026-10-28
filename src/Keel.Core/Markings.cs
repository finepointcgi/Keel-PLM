using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Keel.Core;

/// <summary>Export-control jurisdiction of technical data (ADR-0004).</summary>
public enum ExportJurisdiction
{
    None = 0,
    Ear = 1,
    Itar = 2,
}

/// <summary>DoDI 5230.24 distribution statements. Statement X was cancelled and is not supported.</summary>
public enum DistributionStatement
{
    A = 1,
    B = 2,
    C = 3,
    D = 4,
    E = 5,
    F = 6,
}

/// <summary>
/// Security markings carried by every controlled object. Instances are always valid;
/// construct through <see cref="Create"/> which enforces the ADR-0004 invariants.
/// </summary>
public sealed partial record Markings
{
    public const string Ear99 = "EAR99";

    public ExportJurisdiction ExportJurisdiction { get; }
    public string? Eccn { get; }
    public string? UsmlCategory { get; }
    public ImmutableSortedSet<string> CuiCategories { get; }
    public DistributionStatement DistributionStatement { get; }
    public string? ControllingOffice { get; }
    public string? DistributionReason { get; }
    public string? Program { get; }
    public string OwningOrg { get; }
    public ImmutableSortedSet<string> ExportAuthorizations { get; }

    private Markings(
        ExportJurisdiction exportJurisdiction,
        string? eccn,
        string? usmlCategory,
        ImmutableSortedSet<string> cuiCategories,
        DistributionStatement distributionStatement,
        string? controllingOffice,
        string? distributionReason,
        string? program,
        string owningOrg,
        ImmutableSortedSet<string> exportAuthorizations)
    {
        ExportJurisdiction = exportJurisdiction;
        Eccn = eccn;
        UsmlCategory = usmlCategory;
        CuiCategories = cuiCategories;
        DistributionStatement = distributionStatement;
        ControllingOffice = controllingOffice;
        DistributionReason = distributionReason;
        Program = program;
        OwningOrg = owningOrg;
        ExportAuthorizations = exportAuthorizations;
    }

    /// <summary>True when nationality or an export authorization gates access.</summary>
    public bool IsExportControlled =>
        ExportJurisdiction == ExportJurisdiction.Itar ||
        (ExportJurisdiction == ExportJurisdiction.Ear && !string.Equals(Eccn, Ear99, StringComparison.Ordinal));

    public bool IsCui => !CuiCategories.IsEmpty;

    public static Result<Markings> Create(
        ExportJurisdiction exportJurisdiction,
        DistributionStatement distributionStatement,
        string owningOrg,
        string? eccn = null,
        string? usmlCategory = null,
        IEnumerable<string>? cuiCategories = null,
        string? controllingOffice = null,
        string? distributionReason = null,
        string? program = null,
        IEnumerable<string>? exportAuthorizations = null)
    {
        var errors = new List<string>();
        eccn = Normalize(eccn)?.ToUpperInvariant();
        usmlCategory = Normalize(usmlCategory);
        program = Normalize(program);
        owningOrg = Normalize(owningOrg) ?? string.Empty;
        controllingOffice = Normalize(controllingOffice);
        distributionReason = Normalize(distributionReason);

        var cui = Codes(cuiCategories, "CUI category", errors);
        var auths = Codes(exportAuthorizations, "export authorization", errors);

        if (!Enum.IsDefined(exportJurisdiction)) errors.Add("Unknown export jurisdiction.");
        if (!Enum.IsDefined(distributionStatement)) errors.Add("Unknown distribution statement.");
        if (owningOrg.Length == 0) errors.Add("Owning organization is required.");
        if (program is not null && !CodePattern().IsMatch(program)) errors.Add("Program code may contain only letters, digits, '-', '_' and '.'.");

        switch (exportJurisdiction)
        {
            case ExportJurisdiction.Itar:
                if (usmlCategory is null) errors.Add("ITAR data requires a USML category.");
                if (eccn is not null) errors.Add("ITAR data must not carry an ECCN.");
                break;
            case ExportJurisdiction.Ear:
                if (eccn is null) errors.Add("EAR data requires an ECCN (use EAR99 if not otherwise classified).");
                else if (eccn != Ear99 && !EccnPattern().IsMatch(eccn)) errors.Add($"'{eccn}' is not a valid ECCN.");
                if (usmlCategory is not null) errors.Add("EAR data must not carry a USML category.");
                break;
            case ExportJurisdiction.None:
                if (eccn is not null || usmlCategory is not null) errors.Add("Data with no export jurisdiction must not carry an ECCN or USML category.");
                if (!auths.IsEmpty) errors.Add("Export authorizations only apply to export-controlled data.");
                break;
        }

        // ASSUMPTION(A3): statement F ("further dissemination only as directed") is tied to a program so that
        // the program need-to-know clause of ADR-0004 governs it.
        if (distributionStatement == DistributionStatement.F && program is null)
            errors.Add("Distribution statement F requires a program.");

        if (distributionStatement != DistributionStatement.A && (controllingOffice is null || distributionReason is null))
            errors.Add("Distribution statements B-F require a controlling office and a reason.");

        var controlled = exportJurisdiction == ExportJurisdiction.Itar ||
                         (exportJurisdiction == ExportJurisdiction.Ear && eccn != Ear99);
        // ASSUMPTION(A3): export-controlled technical data is CUI with category EXPT (CUI Registry, "Export Controlled").
        if (controlled && !cui.Contains("EXPT")) errors.Add("Export-controlled data must carry CUI category EXPT.");
        // Export-controlled technical data cannot be approved for public release.
        if (controlled && distributionStatement == DistributionStatement.A) errors.Add("Export-controlled data cannot carry distribution statement A.");

        return errors.Count > 0
            ? Result<Markings>.Fail(errors)
            : Result<Markings>.Ok(new Markings(exportJurisdiction, eccn, usmlCategory, cui, distributionStatement,
                controllingOffice, distributionReason, program, owningOrg, auths));
    }

    /// <summary>Banner text rendered on screen and stamped on files, e.g. "CUI//EXPT · ITAR · DISTRIBUTION D".</summary>
    public string BannerText()
    {
        var parts = new List<string>();
        parts.Add(IsCui ? "CUI//" + string.Join('/', CuiCategories) : "UNCLASSIFIED");
        switch (ExportJurisdiction)
        {
            case ExportJurisdiction.Itar: parts.Add($"ITAR USML {UsmlCategory}"); break;
            case ExportJurisdiction.Ear: parts.Add($"EAR {Eccn}"); break;
        }
        parts.Add($"DISTRIBUTION {DistributionStatement}");
        if (Program is not null) parts.Add($"PROGRAM {Program}");
        return string.Join(" · ", parts);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ImmutableSortedSet<string> Codes(IEnumerable<string>? values, string label, List<string> errors)
    {
        var set = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var raw in values ?? [])
        {
            var v = Normalize(raw)?.ToUpperInvariant();
            if (v is null) continue;
            if (!CodePattern().IsMatch(v)) { errors.Add($"Invalid {label} '{raw}'."); continue; }
            set.Add(v);
        }
        return set.ToImmutable();
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,63}$")]
    private static partial Regex CodePattern();

    // Commerce Control List format: category digit, product group letter, three digits, optional paragraph suffix.
    [GeneratedRegex(@"^[0-9][A-E][0-9]{3}([.][A-Za-z0-9.]+)?$")]
    private static partial Regex EccnPattern();
}
