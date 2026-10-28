using System.Collections.Immutable;
using Keel.Core;

namespace Keel.Security;

public enum PlmAction
{
    Read,
    Download,
    Modify,
    Sign,
    Administer,
}

public sealed record AccessDecision(bool Permitted, ImmutableArray<string> Reasons)
{
    public static readonly AccessDecision Permit = new(true, []);
}

/// <summary>A SQL predicate plus its parameters. Parameter names are unique per table alias.</summary>
public sealed record SqlFilter(string Sql, IReadOnlyList<KeyValuePair<string, object>> Parameters);

/// <summary>
/// The single access policy of ADR-0004. <see cref="Evaluate"/> and <see cref="BuildReadFilter"/> must
/// always agree on read access; the integration test suite proves it against PostgreSQL.
/// </summary>
public static class AccessPolicy
{
    // Reason codes are stable identifiers: they are written to the audit log and shown in the UI.
    public const string RoleMissing = "role.missing";
    public const string ExportRequiresUsPerson = "export.us_person_or_authorization_required";
    public const string ProgramNotMember = "program.not_member";
    public const string CuiCategoryMissing = "cui.category_missing";
    public const string OrgMismatch = "org.not_owner";

    private static readonly ImmutableDictionary<PlmAction, ImmutableArray<string>> RolesForAction =
        new Dictionary<PlmAction, ImmutableArray<string>>
        {
            // plm-integration may read (for the pull API, under its own markings) but not download, modify or sign.
            [PlmAction.Read] = [Roles.Viewer, Roles.Author, Roles.Approver, Roles.Admin, Roles.Auditor, Roles.Integration],
            [PlmAction.Download] = [Roles.Viewer, Roles.Author, Roles.Approver, Roles.Admin, Roles.Auditor],
            [PlmAction.Modify] = [Roles.Author, Roles.Admin],
            [PlmAction.Sign] = [Roles.Approver],
            [PlmAction.Administer] = [Roles.Admin],
        }.ToImmutableDictionary();

    public static bool HasRoleFor(Subject subject, PlmAction action) =>
        RolesForAction[action].Any(subject.Roles.Contains);

    public static AccessDecision Evaluate(Subject subject, Markings markings, PlmAction action)
    {
        var reasons = ImmutableArray.CreateBuilder<string>();

        if (!HasRoleFor(subject, action)) reasons.Add(RoleMissing);

        // Clause 1: export control. Administrators get no exemption.
        if (markings.IsExportControlled && !subject.UsPerson && !subject.ExportAuthorizations.Overlaps(markings.ExportAuthorizations))
            reasons.Add(ExportRequiresUsPerson);

        // Clause 2: program need-to-know.
        if (markings.Program is not null && !subject.Programs.Contains(markings.Program))
            reasons.Add(ProgramNotMember);

        // Clause 3: every CUI category on the object must be held by the subject.
        foreach (var category in markings.CuiCategories)
            if (!subject.CuiCategories.Contains(category))
                reasons.Add($"{CuiCategoryMissing}:{category}");

        if (action == PlmAction.Modify &&
            !string.Equals(subject.Org, markings.OwningOrg, StringComparison.Ordinal) &&
            !(markings.Program is not null && subject.Programs.Contains(markings.Program)))
            reasons.Add(OrgMismatch);

        return reasons.Count == 0 ? AccessDecision.Permit : new AccessDecision(false, reasons.ToImmutable());
    }

    /// <summary>
    /// SQL predicate granting read access over the marking columns of <paramref name="alias"/>
    /// (see db/migrations for the column contract: export_jurisdiction, eccn, program, cui_categories, export_authorizations).
    /// </summary>
    public static SqlFilter BuildReadFilter(Subject subject, string alias)
    {
        if (!IsIdentifier(alias)) throw new ArgumentException("Alias must be a simple SQL identifier.", nameof(alias));
        if (!HasRoleFor(subject, PlmAction.Read)) return new SqlFilter("FALSE", []);

        var p = $"acl_{alias}_";
        var sql =
            $"((({alias}.export_jurisdiction = 'None' OR ({alias}.export_jurisdiction = 'Ear' AND {alias}.eccn = '{Markings.Ear99}')) " +
            $"OR @{p}us OR {alias}.export_authorizations && @{p}auths) " +
            $"AND ({alias}.program IS NULL OR {alias}.program = ANY(@{p}programs)) " +
            $"AND {alias}.cui_categories <@ @{p}cui)";

        return new SqlFilter(sql,
        [
            new($"{p}us", subject.UsPerson),
            new($"{p}auths", subject.ExportAuthorizations.ToArray()),
            new($"{p}programs", subject.Programs.ToArray()),
            new($"{p}cui", subject.CuiCategories.ToArray()),
        ]);
    }

    private static bool IsIdentifier(string s) =>
        s.Length is > 0 and <= 32 && (char.IsAsciiLetterLower(s[0]) || s[0] == '_') && s.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');
}
