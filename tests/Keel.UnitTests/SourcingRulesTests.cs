using System.Collections.Immutable;
using Keel.Core;
using Keel.Security;
using Keel.Sourcing;
using Keel.Web.Services;
using Keel.Workspace;

namespace Keel.UnitTests;

/// <summary>The pure rules of sourcing (ADR-0037): export control, release to suppliers, standing, coverage, validation.</summary>
public sealed class SourcingRulesTests
{
    private static Markings Itar(string[]? auths = null) =>
        Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "r", program: "ORCA", exportAuthorizations: auths).Value;

    private static readonly Markings Open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;

    private static Markings Ear(string eccn) =>
        Markings.Create(ExportJurisdiction.Ear, DistributionStatement.C, "ENG", eccn: eccn, cuiCategories: eccn == "EAR99" ? null : ["EXPT"], controllingOffice: "ACME", distributionReason: "r").Value;

    private static ClearanceProfile Profile(RecipientCategory c, bool us, string[] jur, string[]? cui = null, string[]? programs = null, DistributionStatement[]? statements = null, string[]? auths = null) =>
        new("p", c, us, jur.ToImmutableSortedSet(StringComparer.Ordinal), ImmutableSortedSet<string>.Empty, ImmutableSortedSet<string>.Empty, (cui ?? []).ToImmutableSortedSet(StringComparer.Ordinal),
            (programs ?? []).ToImmutableSortedSet(StringComparer.Ordinal), (statements ?? [DistributionStatement.A]).ToImmutableSortedSet(), (auths ?? []).ToImmutableSortedSet(StringComparer.Ordinal));

    // ---------------------------------------------------------------- export control

    [Fact]
    public void A_foreign_supplier_is_refused_for_ITAR_parts_whatever_else_is_true()
    {
        Assert.Equal(SourcingRules.ForeignSupplierItarPart, SourcingRules.CheckSupplierForPart(true, Itar()));
        Assert.Equal(SourcingRules.ForeignSupplierItarPart, SourcingRules.CheckSupplierForPart(true, Itar(["TAA-0042"]))); // no export authorization exception
        Assert.Null(SourcingRules.CheckSupplierForPart(false, Itar()));
        Assert.Null(SourcingRules.CheckSupplierForPart(true, Open));
        Assert.Null(SourcingRules.CheckSupplierForPart(true, Ear("9A610"))); // EAR parts are the portal profile's business, not this rule's
    }

    [Fact]
    public void Release_to_a_supplier_needs_a_profile_and_agrees_with_the_outbound_check_for_everyone_else()
    {
        var profiles = new[]
        {
            Profile(RecipientCategory.Public, false, ["None"]),
            Profile(RecipientCategory.DodContractor, true, ["None", "Ear", "Itar"], ["EXPT", "CTI"], ["ORCA"], [DistributionStatement.A, DistributionStatement.C, DistributionStatement.D]),
            Profile(RecipientCategory.Foreign, false, ["None", "Ear", "Itar"], ["EXPT"], ["ORCA"], [DistributionStatement.A, DistributionStatement.C, DistributionStatement.D], ["TAA-0042"]),
        };
        var markings = new[] { Open, Itar(), Itar(["TAA-0042"]), Ear("9A610"), Ear("EAR99") };
        Assert.False(SourcingRules.ReleaseToSupplier(false, null, Open).Permitted);
        Assert.Equal([SourcingRules.NoClearanceProfile], SourcingRules.ReleaseToSupplier(false, null, Itar()).Reasons);
        foreach (var profile in profiles)
            foreach (var m in markings)
            {
                var domestic = SourcingRules.ReleaseToSupplier(false, profile, m);
                Assert.Equal(OutboundRelease.Evaluate(profile, m).Permitted, domestic.Permitted);
                var foreign = SourcingRules.ReleaseToSupplier(true, profile, m);
                // A foreign supplier gets exactly what the profile allows, except that ITAR data is never released.
                Assert.Equal(domestic.Permitted && m.ExportJurisdiction != ExportJurisdiction.Itar, foreign.Permitted);
            }
    }

    [Fact]
    public void The_supplier_role_grants_no_policy_action_and_is_not_a_person_role()
    {
        var supplier = new Subject { Id = "s", DisplayName = "S", Roles = [Roles.Supplier], UsPerson = true, Programs = ["ORCA"], CuiCategories = ["EXPT", "CTI"], Org = "ENG" };
        foreach (var action in Enum.GetValues<PlmAction>()) Assert.False(AccessPolicy.HasRoleFor(supplier, action), action.ToString());
        Assert.DoesNotContain(Roles.Supplier, Roles.All);
        foreach (var m in new[] { Open, Itar(), Ear("EAR99") })
        {
            Assert.False(AccessPolicy.Evaluate(supplier, m, PlmAction.Read).Permitted);
            Assert.Equal("FALSE", AccessPolicy.BuildReadFilter(supplier, "m").Sql);
        }
    }

    // ---------------------------------------------------------------- suppliers

    [Theory]
    [InlineData("prospective", "approved", 1, null)]
    [InlineData("prospective", "conditional", 0, null)]
    [InlineData("disapproved", "approved", 2, null)]
    [InlineData("conditional", "approved", 1, null)]
    [InlineData("approved", "conditional", 0, null)]
    public void Valid_proposals(string current, string proposed, int certs, string? expected) => Assert.Equal(expected, SourcingRules.CheckProposal(current, proposed, certs));

    [Theory]
    [InlineData("prospective", "approved", 0)]
    [InlineData("approved", "approved", 3)]
    [InlineData("obsolete", "approved", 3)]
    [InlineData("prospective", "disapproved", 1)]
    [InlineData("prospective", "obsolete", 1)]
    public void Invalid_proposals(string current, string proposed, int certs) => Assert.NotNull(SourcingRules.CheckProposal(current, proposed, certs));

    [Fact]
    public void Direct_status_changes_go_one_way_and_need_a_reason()
    {
        Assert.Null(SourcingRules.CheckDirectStatus("approved", "disapproved", "Failed audit"));
        Assert.Null(SourcingRules.CheckDirectStatus("disapproved", "obsolete", "Closed"));
        Assert.NotNull(SourcingRules.CheckDirectStatus("approved", "disapproved", " "));
        Assert.NotNull(SourcingRules.CheckDirectStatus("approved", "approved", "x"));
        Assert.NotNull(SourcingRules.CheckDirectStatus("approved", "conditional", "x"));
        Assert.NotNull(SourcingRules.CheckDirectStatus("obsolete", "disapproved", "x"));
        Assert.NotNull(SourcingRules.CheckDirectStatus("approved", "obsolete", new string('x', 1001)));
    }

    [Fact]
    public void Supplier_data_is_validated()
    {
        Assert.Empty(SourcingRules.ValidateSupplier("Alpine", "USA", ["vendor"], false, "1A2B3", "123456789"));
        Assert.Empty(SourcingRules.ValidateSupplier("Berlin", "DEU", ["manufacturer"], true, null, null));
        Assert.Contains("foreign", string.Join(" ", SourcingRules.ValidateSupplier("Berlin", "DEU", ["vendor"], false, null, null)), StringComparison.Ordinal);
        Assert.NotEmpty(SourcingRules.ValidateSupplier("", "USA", ["vendor"], false, null, null));
        Assert.NotEmpty(SourcingRules.ValidateSupplier("A", "us", ["vendor"], false, null, null));
        Assert.NotEmpty(SourcingRules.ValidateSupplier("A", "USA", [], false, null, null));
        Assert.NotEmpty(SourcingRules.ValidateSupplier("A", "USA", ["buyer"], false, null, null));
        Assert.NotEmpty(SourcingRules.ValidateSupplier("A", "USA", ["vendor"], false, "ABC", null));
        Assert.NotEmpty(SourcingRules.ValidateSupplier("A", "USA", ["vendor"], false, null, "12345"));
    }

    [Theory]
    [InlineData(2026, 10, 29, 2026, 10, 29, 0, "d7")]
    [InlineData(2026, 11, 5, 2026, 10, 29, 0, "d7")]
    [InlineData(2026, 11, 6, 2026, 10, 29, 0, "d30")]
    [InlineData(2026, 11, 28, 2026, 10, 29, 0, "d30")]
    [InlineData(2026, 11, 29, 2026, 10, 29, 0, "d60")]
    [InlineData(2026, 12, 28, 2026, 10, 29, 0, "d60")]
    [InlineData(2026, 12, 29, 2026, 10, 29, 0, null)]
    [InlineData(2026, 10, 28, 2026, 10, 29, 0, "expired")]
    public void Certification_notice_stages(int ey, int em, int ed, int ty, int tm, int td, int _, string? expected) =>
        Assert.Equal(expected, SourcingRules.NoticeStage(new DateOnly(ey, em, ed), new DateOnly(ty, tm, td)));

    [Fact]
    public void Certification_standing()
    {
        var today = new DateOnly(2026, 10, 29);
        Assert.Equal(CertStandings.Valid, SourcingRules.CertStanding(today.AddDays(61), false, today));
        Assert.Equal(CertStandings.Expiring, SourcingRules.CertStanding(today.AddDays(60), false, today));
        Assert.Equal(CertStandings.Expiring, SourcingRules.CertStanding(today, false, today));
        Assert.Equal(CertStandings.Expired, SourcingRules.CertStanding(today.AddDays(-1), false, today));
        Assert.Equal(CertStandings.Revoked, SourcingRules.CertStanding(today.AddDays(500), true, today));
    }

    [Fact]
    public void Certification_and_contact_data_is_validated()
    {
        var ok = new AddCertificationCommand("AS9100", "C-1", "Registrar", null, new DateOnly(2026, 1, 1), new DateOnly(2029, 1, 1));
        Assert.Empty(SourcingRules.ValidateCertification(ok));
        Assert.NotEmpty(SourcingRules.ValidateCertification(ok with { Type = "as9100!" }));
        Assert.NotEmpty(SourcingRules.ValidateCertification(ok with { ExpiresOn = new DateOnly(2025, 1, 1) }));
        Assert.NotEmpty(SourcingRules.ValidateCertification(ok with { Number = "" }));
        Assert.Empty(SourcingRules.ValidateContact(new ContactInput("Ann", "ann@example.test", "+1 555", "QA", true)));
        Assert.NotEmpty(SourcingRules.ValidateContact(new ContactInput("", null, null, null, false)));
        Assert.NotEmpty(SourcingRules.ValidateContact(new ContactInput("Ann", "not-an-address", null, null, false)));
    }

    // ---------------------------------------------------------------- entries and coverage

    [Fact]
    public void Entries_are_validated_including_price_breaks()
    {
        Assert.Empty(SourcingRules.ValidateEntry("avl", "approved", null, null, "V-1", 14, 100, "USD", [new PriceBreak(10, 2), new PriceBreak(100, 1.5m)], null, "A"));
        Assert.NotEmpty(SourcingRules.ValidateEntry("xx", "approved", null, null, null, null, null, null, null, null, null));
        Assert.NotEmpty(SourcingRules.ValidateEntry("avl", "great", null, null, null, null, null, null, null, null, null));
        Assert.NotEmpty(SourcingRules.ValidateEntry("avl", "approved", new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1), null, null, null, null, null, null, null));
        Assert.NotEmpty(SourcingRules.ValidateEntry("avl", "approved", null, null, null, -1, null, null, null, null, null));
        Assert.NotEmpty(SourcingRules.ValidateEntry("avl", "approved", null, null, null, null, 0, null, null, null, null));
        Assert.NotEmpty(SourcingRules.ValidateEntry("avl", "approved", null, null, null, null, null, "usd", null, null, null));
        Assert.NotEmpty(SourcingRules.ValidateEntry("avl", "approved", null, null, null, null, null, null, [new PriceBreak(1, 1)], null, null)); // breaks need a currency
        Assert.NotEmpty(SourcingRules.ValidateEntry("avl", "approved", null, null, null, null, null, "USD", [new PriceBreak(10, 1), new PriceBreak(10, 1)], null, null));
        Assert.NotEmpty(SourcingRules.ValidateEntry("avl", "approved", null, null, null, null, null, "USD", [new PriceBreak(-1, 1)], null, null));
        Assert.NotEmpty(SourcingRules.ValidateEntry("avl", "approved", null, null, null, null, null, "USD", [new PriceBreak(1, -1)], null, null));
        Assert.NotEmpty(SourcingRules.ValidateEntry("avl", "approved", null, null, null, null, null, null, null, null, "a1"));
    }

    [Fact]
    public void An_entry_counts_only_while_active_and_inside_its_dates()
    {
        var today = new DateOnly(2026, 10, 29);
        Assert.True(SourcingRules.IsEffective("Active", null, null, today));
        Assert.True(SourcingRules.IsEffective("Active", today, today, today));
        Assert.False(SourcingRules.IsEffective("Active", today.AddDays(1), null, today));
        Assert.False(SourcingRules.IsEffective("Active", null, today.AddDays(-1), today));
        foreach (var state in new[] { "Draft", "UnderReview", "Withdrawn" }) Assert.False(SourcingRules.IsEffective(state, null, null, today));
    }

    private static SourcingRules.EntryFacts Facts(string status = "approved", bool effective = true, string supplier = "approved", bool expired = false, bool foreign = false, string? scope = null) =>
        new(status, effective, supplier, expired, foreign, scope);

    [Fact]
    public void Coverage_classification_follows_the_rules()
    {
        Assert.Equal(CoverageBuckets.NotCovered, SourcingRules.Classify([], Open, "A"));
        Assert.Equal(CoverageBuckets.Covered, SourcingRules.Classify([Facts()], Open, "A"));
        Assert.Equal(CoverageBuckets.Covered, SourcingRules.Classify([Facts("preferred")], Open, "A"));
        Assert.Equal(CoverageBuckets.Conditional, SourcingRules.Classify([Facts("alternate")], Open, "A"));
        Assert.Equal(CoverageBuckets.Conditional, SourcingRules.Classify([Facts(supplier: "conditional")], Open, "A"));
        Assert.Equal(CoverageBuckets.Conditional, SourcingRules.Classify([Facts(expired: true)], Open, "A"));
        Assert.Equal(CoverageBuckets.NotCovered, SourcingRules.Classify([Facts("disapproved")], Open, "A"));
        Assert.Equal(CoverageBuckets.NotCovered, SourcingRules.Classify([Facts(effective: false)], Open, "A"));
        Assert.Equal(CoverageBuckets.NotCovered, SourcingRules.Classify([Facts(supplier: "prospective"), Facts(supplier: "obsolete"), Facts(supplier: "disapproved")], Open, "A"));
        Assert.Equal(CoverageBuckets.Covered, SourcingRules.Classify([Facts("alternate"), Facts(), Facts(supplier: "conditional")], Open, "A")); // the best one wins
        // Revision scope.
        Assert.Equal(CoverageBuckets.Covered, SourcingRules.Classify([Facts(scope: "B")], Open, "B"));
        Assert.Equal(CoverageBuckets.NotCovered, SourcingRules.Classify([Facts(scope: "B")], Open, "A"));
        Assert.Equal(CoverageBuckets.NotCovered, SourcingRules.Classify([Facts(scope: "B")], Open, null));
        // A foreign supplier never counts for an ITAR part, even when the data says approved.
        Assert.Equal(CoverageBuckets.NotCovered, SourcingRules.Classify([Facts(foreign: true)], Itar(), "A"));
        Assert.Equal(CoverageBuckets.Covered, SourcingRules.Classify([Facts(foreign: true)], Open, "A"));
    }

    // ---------------------------------------------------------------- requests and responses

    [Fact]
    public void Requests_and_responses_are_validated()
    {
        var ok = new CreateRequestCommand("rfq", "Quote", "d", null, Open, [new RequestPartInput("P-1", 10)], ["SUP-1"]);
        Assert.Empty(SourcingRules.ValidateRequest(ok));
        Assert.NotEmpty(SourcingRules.ValidateRequest(ok with { Kind = "bid" }));
        Assert.NotEmpty(SourcingRules.ValidateRequest(ok with { Title = " " }));
        Assert.NotEmpty(SourcingRules.ValidateRequest(ok with { Parts = [] }));
        Assert.NotEmpty(SourcingRules.ValidateRequest(ok with { Parts = [new("P-1", 1), new("P-1", 2)] }));
        Assert.NotEmpty(SourcingRules.ValidateRequest(ok with { Parts = [new("P-1", 0)] }));
        Assert.NotEmpty(SourcingRules.ValidateRequest(ok with { SupplierNumbers = [] }));
        Assert.NotEmpty(SourcingRules.ValidateRequest(ok with { SupplierNumbers = ["SUP-1", "SUP-1"] }));

        string[] parts = ["P-1", "P-2"];
        Assert.Empty(SourcingRules.ValidateResponse(new PortalResponseInput("hi", false, []), parts));
        Assert.Empty(SourcingRules.ValidateResponse(new PortalResponseInput("", false, [new QuoteLine("P-1", 1m, "USD", 5, 10)]), parts));
        Assert.Empty(SourcingRules.ValidateResponse(new PortalResponseInput("no", true, []), parts));
        Assert.NotEmpty(SourcingRules.ValidateResponse(new PortalResponseInput("", false, []), parts));
        Assert.NotEmpty(SourcingRules.ValidateResponse(new PortalResponseInput("no", true, [new QuoteLine("P-1", 1m, null, null, null)]), parts));
        Assert.NotEmpty(SourcingRules.ValidateResponse(new PortalResponseInput("x", false, [new QuoteLine("P-9", 1m, null, null, null)]), parts)); // not a part it can see
        Assert.NotEmpty(SourcingRules.ValidateResponse(new PortalResponseInput("x", false, [new QuoteLine("P-1", -1m, null, null, null)]), parts));
        Assert.NotEmpty(SourcingRules.ValidateResponse(new PortalResponseInput("x", false, [new QuoteLine("P-1", 1m, "usd", null, null)]), parts));
        Assert.NotEmpty(SourcingRules.ValidateResponse(new PortalResponseInput("x", false, [new QuoteLine("P-1", 1m, null, null, null), new QuoteLine("P-1", 2m, null, null, null)]), parts));
        Assert.NotEmpty(SourcingRules.ValidateResponse(new PortalResponseInput(new string('x', 4001), false, []), parts));
    }

    [Theory]
    [InlineData("quote.pdf", "quote.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\x\\cert.pdf", "cert.pdf")]
    [InlineData("a<b>:c|d?.txt", "abcd.txt")]
    [InlineData("...hidden", "hidden")]
    [InlineData("", "file")]
    [InlineData("../", "file")]
    public void Uploaded_file_names_are_reduced_to_a_safe_leaf(string given, string expected) => Assert.Equal(expected, PortalService.SafeName(given));

    [Fact]
    public void A_very_long_file_name_is_cut_to_200_characters() => Assert.Equal(200, PortalService.SafeName(new string('a', 500) + ".pdf").Length);

    // ---------------------------------------------------------------- navigation and wording

    [Fact]
    public void A_supplier_user_gets_the_portal_navigation_only()
    {
        var groups = NavModel.ForSupplier();
        var hrefs = groups.SelectMany(g => g.Items).Select(i => i.Href).ToList();
        Assert.Equal(["portal", "portal/scorecard"], hrefs); // the requests and the scorecard the buyer shares
        // The internal navigation offers suppliers and requests to people with the internal roles.
        Assert.Contains(NavModel.For([Roles.Viewer]).SelectMany(g => g.Items), i => i.Href == "suppliers");
        Assert.Contains(NavModel.For([Roles.Viewer]).SelectMany(g => g.Items), i => i.Href == "requests");
    }

    [Fact]
    public void Search_prefix_s_finds_suppliers_and_the_default_search_includes_them()
    {
        Assert.Equal([ObjectKinds.Supplier], ParsedQuery.Parse("s: alpine").Kinds);
        Assert.Equal("alpine", ParsedQuery.Parse("s: alpine").Text);
        Assert.Contains(ObjectKinds.Supplier, ParsedQuery.Parse("alpine").Kinds);
        Assert.Equal("suppliers/SUP-000001", ObjectKinds.Path(ObjectKinds.Supplier, "SUP-000001"));
        Assert.Equal("Supplier", AwarenessKinds.Label(AwarenessKinds.Supplier));
        Assert.Equal(AwarenessKinds.Supplier, AwarenessKinds.FromAuditObjectType("supplier"));
    }
}
