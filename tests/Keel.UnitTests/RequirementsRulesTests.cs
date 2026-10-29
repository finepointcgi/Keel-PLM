using Keel.Core;
using Keel.Requirements;

namespace Keel.UnitTests;

public sealed class RequirementsRulesTests
{
    private static readonly Markings Open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;

    [Fact]
    public void Keys_are_formatted_and_recognised()
    {
        Assert.Equal("R-0007", RequirementRules.FormatKey(7));
        Assert.Equal("R-12345", RequirementRules.FormatKey(12345));
        Assert.True(RequirementRules.IsKey("R-0001"));
        Assert.False(RequirementRules.IsKey("R-00x1"));
        Assert.False(RequirementRules.IsKey("X-0001"));
        Assert.False(RequirementRules.IsKey(null));
    }

    [Fact]
    public void The_content_hash_covers_the_content_and_not_the_place()
    {
        var a = RequirementRules.ContentSha("R-0001", "Title", "Text", "functional", "high", "test", "Why", "approved");
        Assert.Matches("^[0-9a-f]{64}$", a);
        Assert.Equal(a, RequirementRules.ContentSha("R-0001", "Title", "Text", "functional", "high", "test", "Why", "approved"));
        foreach (var changed in new[]
        {
            RequirementRules.ContentSha("R-0002", "Title", "Text", "functional", "high", "test", "Why", "approved"),
            RequirementRules.ContentSha("R-0001", "Title2", "Text", "functional", "high", "test", "Why", "approved"),
            RequirementRules.ContentSha("R-0001", "Title", "Text2", "functional", "high", "test", "Why", "approved"),
            RequirementRules.ContentSha("R-0001", "Title", "Text", "safety", "high", "test", "Why", "approved"),
            RequirementRules.ContentSha("R-0001", "Title", "Text", "functional", "low", "test", "Why", "approved"),
            RequirementRules.ContentSha("R-0001", "Title", "Text", "functional", "high", "analysis", "Why", "approved"),
            RequirementRules.ContentSha("R-0001", "Title", "Text", "functional", "high", "test", "Because", "approved"),
            RequirementRules.ContentSha("R-0001", "Title", "Text", "functional", "high", "test", "Why", "draft"),
        }) Assert.NotEqual(a, changed);
        // Fields are separated, so moving a character across a boundary changes the hash.
        Assert.NotEqual(RequirementRules.ContentSha("R-0001", "ab", "c", "functional", "high", "test", "", "draft"), RequirementRules.ContentSha("R-0001", "a", "bc", "functional", "high", "test", "", "draft"));
    }

    [Fact]
    public void Validation_names_every_problem()
    {
        Assert.Empty(RequirementRules.Validate("T", "X", "safety", "low", "inspection", "", "draft"));
        var errors = RequirementRules.Validate(" ", "", "weird", "urgent", "guess", new string('r', 4001), "done");
        Assert.Equal(7, errors.Count);
        Assert.Single(RequirementRules.Validate(new string('t', 201), "x", "functional", "low", "test", null, "draft"));
    }

    [Fact]
    public void The_outline_numbers_a_hierarchy_depth_first_and_treats_a_missing_parent_as_a_root()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid(), orphan = Guid.NewGuid();
        var rows = new (Guid, Guid?, int, string)[] { (d, b, 1, "R-0004"), (c, null, 2, "R-0003"), (b, a, 1, "R-0002"), (a, null, 1, "R-0001"), (orphan, Guid.NewGuid(), 3, "R-0005") };
        var outline = RequirementRules.Outline(rows);
        Assert.Equal([(a, "1", 0), (b, "1.1", 1), (d, "1.1.1", 2), (c, "2", 0), (orphan, "3", 0)], outline);
        // A loop does not hang and shows each row once.
        var loop = RequirementRules.Outline([(a, b, 1, "a"), (b, a, 1, "b")]);
        Assert.True(loop.Count <= 2);
    }

    [Fact]
    public void Verification_state_follows_the_newest_record_for_the_current_content()
    {
        Assert.Equal(VerificationStates.None, RequirementRules.VerificationState("h1", []));
        Assert.Equal(VerificationStates.Verified, RequirementRules.VerificationState("h1", [("h1", "pass")]));
        Assert.Equal(VerificationStates.Failed, RequirementRules.VerificationState("h1", [("h1", "fail"), ("h1", "pass")]));
        Assert.Equal(VerificationStates.Verified, RequirementRules.VerificationState("h1", [("h1", "pass"), ("h1", "fail")]));
        Assert.Equal(VerificationStates.Waived, RequirementRules.VerificationState("h1", [("h1", "waived")]));
        Assert.Equal(VerificationStates.Stale, RequirementRules.VerificationState("h2", [("h1", "pass")]));
        Assert.Equal(VerificationStates.Verified, RequirementRules.VerificationState("h2", [("h1", "fail"), ("h2", "pass")]));
    }

    [Fact]
    public void Coverage_arithmetic_counts_only_requirements_in_scope()
    {
        CoverageRow Row(string key, string status, int alloc, string state, int suspect = 0) =>
            new(key, "1", "t", status, "test", "high", alloc, 0, state, suspect, ReqStatuses.InScope(status), alloc == 0, state is not (VerificationStates.Verified or VerificationStates.Waived), "UNCLASSIFIED", Open);
        var rows = new[]
        {
            Row("R-1", "approved", 1, VerificationStates.Verified), Row("R-2", "approved", 0, VerificationStates.None, 2), Row("R-3", "withdrawn", 0, VerificationStates.None),
            Row("R-4", "deferred", 0, VerificationStates.None), Row("R-5", "draft", 1, VerificationStates.Failed), Row("R-6", "approved", 1, VerificationStates.Waived), Row("R-7", "approved", 1, VerificationStates.Stale),
        };
        var s = RequirementRules.Summarize(rows);
        Assert.Equal(7, s.Total);
        Assert.Equal(5, s.InScope);
        Assert.Equal(4, s.Allocated);
        Assert.Equal(1, s.Unallocated);
        Assert.Equal((1, 1, 1, 1, 1), (s.Verified, s.Waived, s.Failed, s.Stale, s.NotVerified));
        Assert.Equal(2, s.SuspectLinks);
        Assert.Equal(3, s.Gaps); // R-2 (unallocated), R-5 (failed), R-7 (stale)
    }

    [Fact]
    public void Statuses_and_labels()
    {
        Assert.True(ReqStatuses.InScope("draft"));
        Assert.True(ReqStatuses.InScope("approved"));
        Assert.False(ReqStatuses.InScope("deferred"));
        Assert.False(ReqStatuses.InScope("withdrawn"));
        Assert.All(ReqTypes.All, t => Assert.NotEqual(t, ReqTypes.Label(t)));
        Assert.Equal("Needs re-verification", VerificationStates.Label(VerificationStates.Stale));
    }

    [Fact]
    public void Dominance_demands_a_requirement_at_least_as_restrictive_as_its_specification()
    {
        var itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA", cuiCategories: ["EXPT"]).Value;
        Assert.Empty(RequirementRules.DominanceGaps(itar, Open));
        Assert.NotEmpty(RequirementRules.DominanceGaps(Open, itar));
    }
}
