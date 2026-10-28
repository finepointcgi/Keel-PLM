using Keel.Core;

namespace Keel.Web.Services;

/// <summary>Editable form state for markings; list fields are comma-separated text.</summary>
public sealed class MarkingsInput
{
    public ExportJurisdiction ExportJurisdiction { get; set; } = ExportJurisdiction.Itar;
    public string Eccn { get; set; } = "";
    public string UsmlCategory { get; set; } = "XI(d)";
    public string CuiCategories { get; set; } = "EXPT";
    public DistributionStatement DistributionStatement { get; set; } = DistributionStatement.D;
    public string ControllingOffice { get; set; } = "[Company] Engineering";
    public string DistributionReason { get; set; } = "Critical technology";
    public string Program { get; set; } = "ORCA";
    public string OwningOrg { get; set; } = "ENG";
    public string ExportAuthorizations { get; set; } = "";

    public Result<Markings> ToMarkings() => Markings.Create(ExportJurisdiction, DistributionStatement, OwningOrg,
        Eccn, UsmlCategory, Split(CuiCategories), ControllingOffice, DistributionReason, Program, Split(ExportAuthorizations));

    public static MarkingsInput From(Markings m) => new()
    {
        ExportJurisdiction = m.ExportJurisdiction,
        Eccn = m.Eccn ?? "",
        UsmlCategory = m.UsmlCategory ?? "",
        CuiCategories = string.Join(", ", m.CuiCategories),
        DistributionStatement = m.DistributionStatement,
        ControllingOffice = m.ControllingOffice ?? "",
        DistributionReason = m.DistributionReason ?? "",
        Program = m.Program ?? "",
        OwningOrg = m.OwningOrg,
        ExportAuthorizations = string.Join(", ", m.ExportAuthorizations),
    };

    /// <summary>
    /// Fields that don't apply to the chosen jurisdiction are ignored rather than rejected, so switching
    /// jurisdiction in the form doesn't leave stale values behind.
    /// </summary>
    public MarkingsInput Effective() => new()
    {
        ExportJurisdiction = ExportJurisdiction,
        Eccn = ExportJurisdiction == ExportJurisdiction.Ear ? Eccn : "",
        UsmlCategory = ExportJurisdiction == ExportJurisdiction.Itar ? UsmlCategory : "",
        CuiCategories = CuiCategories,
        DistributionStatement = DistributionStatement,
        ControllingOffice = ControllingOffice,
        DistributionReason = DistributionReason,
        Program = Program,
        OwningOrg = OwningOrg,
        ExportAuthorizations = ExportJurisdiction == ExportJurisdiction.None ? "" : ExportAuthorizations,
    };

    internal static string[] Split(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
