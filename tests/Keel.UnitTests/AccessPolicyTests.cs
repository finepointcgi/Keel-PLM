using System.Collections.Immutable;
using Keel.Core;
using Keel.Security;

namespace Keel.UnitTests;

public sealed class AccessPolicyTests
{
    private static readonly Markings ItarProgram = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG",
        usmlCategory: "XI(d)", cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology",
        program: "ORCA", exportAuthorizations: ["TAA-0042"]).Value;

    private static readonly Markings Public = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;

    private static Subject User(bool usPerson = true, string[]? programs = null, string[]? cui = null, string[]? auths = null,
        string[]? roles = null, string org = "ENG") => new()
    {
        Id = "u1",
        DisplayName = "Test User",
        UsPerson = usPerson,
        Org = org,
        Programs = (programs ?? ["ORCA"]).ToImmutableSortedSet(StringComparer.Ordinal),
        CuiCategories = (cui ?? ["EXPT"]).ToImmutableSortedSet(StringComparer.Ordinal),
        ExportAuthorizations = (auths ?? []).ToImmutableSortedSet(StringComparer.Ordinal),
        Roles = (roles ?? [Roles.Viewer]).ToImmutableSortedSet(StringComparer.Ordinal),
    };

    [Fact]
    public void Us_person_on_program_with_expt_can_read_itar()
        => Assert.True(AccessPolicy.Evaluate(User(), ItarProgram, PlmAction.Read).Permitted);

    [Fact]
    public void Foreign_person_without_authorization_is_denied()
    {
        var d = AccessPolicy.Evaluate(User(usPerson: false), ItarProgram, PlmAction.Read);
        Assert.False(d.Permitted);
        Assert.Equal([AccessPolicy.ExportRequiresUsPerson], d.Reasons);
    }

    [Fact]
    public void Foreign_person_with_listed_authorization_is_permitted()
        => Assert.True(AccessPolicy.Evaluate(User(usPerson: false, auths: ["TAA-0042"]), ItarProgram, PlmAction.Read).Permitted);

    [Fact]
    public void Foreign_person_with_unrelated_authorization_is_denied()
        => Assert.False(AccessPolicy.Evaluate(User(usPerson: false, auths: ["TAA-9999"]), ItarProgram, PlmAction.Read).Permitted);

    [Fact]
    public void Administrators_do_not_bypass_export_control()
    {
        var admin = User(usPerson: false, roles: [Roles.Admin]);
        var d = AccessPolicy.Evaluate(admin, ItarProgram, PlmAction.Administer);
        Assert.False(d.Permitted);
        Assert.Contains(AccessPolicy.ExportRequiresUsPerson, d.Reasons);
    }

    [Fact]
    public void Missing_program_and_cui_category_report_every_reason()
    {
        var d = AccessPolicy.Evaluate(User(programs: [], cui: []), ItarProgram, PlmAction.Read);
        Assert.Equal([AccessPolicy.ProgramNotMember, $"{AccessPolicy.CuiCategoryMissing}:EXPT"], d.Reasons);
    }

    [Fact]
    public void Role_governs_action()
    {
        Assert.Equal([AccessPolicy.RoleMissing], AccessPolicy.Evaluate(User(), Public, PlmAction.Modify).Reasons);
        Assert.True(AccessPolicy.Evaluate(User(roles: [Roles.Author]), Public, PlmAction.Modify).Permitted);
        Assert.False(AccessPolicy.Evaluate(User(roles: [Roles.Author]), Public, PlmAction.Sign).Permitted);
        Assert.True(AccessPolicy.Evaluate(User(roles: [Roles.Approver]), Public, PlmAction.Sign).Permitted);
    }

    [Fact]
    public void Modify_requires_owning_org_or_program_membership()
    {
        var other = User(roles: [Roles.Author], org: "MFG", programs: []);
        Assert.Contains(AccessPolicy.OrgMismatch, AccessPolicy.Evaluate(other, Public, PlmAction.Modify).Reasons);
        var onProgram = User(roles: [Roles.Author], org: "MFG");
        Assert.True(AccessPolicy.Evaluate(onProgram, ItarProgram, PlmAction.Modify).Permitted);
    }

    [Fact]
    public void Read_filter_without_role_is_false()
        => Assert.Equal("FALSE", AccessPolicy.BuildReadFilter(User(roles: []), "p").Sql);

    [Theory]
    [InlineData("p; DROP")]
    [InlineData("P")]
    [InlineData("")]
    public void Read_filter_rejects_unsafe_alias(string alias)
        => Assert.Throws<ArgumentException>(() => AccessPolicy.BuildReadFilter(User(), alias));
}
