using System.Data.Common;
using Keel.Core;

namespace Keel.Security;

/// <summary>
/// The marking column contract every controlled table implements (ADR-0004), with helpers to read and write it.
/// Keep in sync with <see cref="AccessPolicy.BuildReadFilter"/>.
/// </summary>
public static class MarkingColumns
{
    public const int Count = 10;

    public static string Select(string alias) =>
        $"{alias}.export_jurisdiction, {alias}.eccn, {alias}.usml_category, {alias}.cui_categories, {alias}.distribution_statement, " +
        $"{alias}.controlling_office, {alias}.distribution_reason, {alias}.program, {alias}.owning_org, {alias}.export_authorizations";

    public const string InsertColumns =
        "export_jurisdiction, eccn, usml_category, cui_categories, distribution_statement, controlling_office, distribution_reason, program, owning_org, export_authorizations";

    public const string InsertValues = "@mk_j, @mk_eccn, @mk_usml, @mk_cui, @mk_dist, @mk_office, @mk_reason, @mk_program, @mk_org, @mk_auths";

    public const string UpdateSet =
        "export_jurisdiction = @mk_j, eccn = @mk_eccn, usml_category = @mk_usml, cui_categories = @mk_cui, distribution_statement = @mk_dist, " +
        "controlling_office = @mk_office, distribution_reason = @mk_reason, program = @mk_program, owning_org = @mk_org, export_authorizations = @mk_auths";

    public static IReadOnlyList<KeyValuePair<string, object>> Parameters(Markings m) =>
    [
        new("mk_j", m.ExportJurisdiction.ToString()),
        new("mk_eccn", (object?)m.Eccn ?? DBNull.Value),
        new("mk_usml", (object?)m.UsmlCategory ?? DBNull.Value),
        new("mk_cui", m.CuiCategories.ToArray()),
        new("mk_dist", m.DistributionStatement.ToString()),
        new("mk_office", (object?)m.ControllingOffice ?? DBNull.Value),
        new("mk_reason", (object?)m.DistributionReason ?? DBNull.Value),
        new("mk_program", (object?)m.Program ?? DBNull.Value),
        new("mk_org", m.OwningOrg),
        new("mk_auths", m.ExportAuthorizations.ToArray()),
    ];

    /// <summary>Reads <see cref="Count"/> columns starting at <paramref name="start"/> in <see cref="Select"/> order.</summary>
    public static Markings Read(DbDataReader r, int start)
    {
        string? S(int i) => r.IsDBNull(start + i) ? null : r.GetString(start + i);
        var result = Markings.Create(
            Enum.Parse<ExportJurisdiction>(r.GetString(start)),
            Enum.Parse<DistributionStatement>(r.GetString(start + 4)),
            r.GetString(start + 8),
            eccn: S(1),
            usmlCategory: S(2),
            cuiCategories: (string[])r.GetValue(start + 3),
            controllingOffice: S(5),
            distributionReason: S(6),
            program: S(7),
            exportAuthorizations: (string[])r.GetValue(start + 9));
        // Stored markings were validated on write; failing here means the database was edited outside the application.
        return result.IsSuccess ? result.Value : throw new InvalidOperationException("Stored markings are invalid: " + string.Join("; ", result.Errors));
    }
}
