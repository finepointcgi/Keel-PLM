using Keel.Change;

namespace Keel.UnitTests;

/// <summary>Pure parts of change management: type metadata, process wording, the frozen review snapshot.</summary>
public sealed class ChangeDomainTests
{
    private static SnapshotData Sample(int submission = 1) => new(
        ChangeSnapshot.CurrentFormat, ChangeTypes.Ecn, "ECN-000007", submission, "Revise bracket", "Thicker wall", "High", "II", "Cracks under load", "ECR-000003",
        "CUI//EXPT · ITAR · DISTRIBUTION D",
        [new SnapshotAffected("part", "P-000001", "Bracket", "A", Guid.Parse("00000000-0000-0000-0000-00000000000a"), "revise")],
        [new SnapshotResulting("part", "P-000001", "B", Guid.Parse("00000000-0000-0000-0000-00000000000b"), "B.2", Guid.Parse("00000000-0000-0000-0000-00000000000c"), "ab12")]);

    [Fact]
    public void Types_map_to_lifecycles_object_types_and_labels()
    {
        Assert.Equal(["PR", "ECR", "ECN"], ChangeTypes.All);
        Assert.Equal(("ecn-default", "ecn", "ECN", "Change notice"), (ChangeTypes.LifecycleOf("ECN"), ChangeTypes.ObjectTypeOf("ECN"), ChangeTypes.TypeOfObjectType("ecn"), ChangeTypes.Label("ECN")));
        Assert.Equal("Problem report", ChangeTypes.Label(ChangeTypes.Pr));
        Assert.Equal(["revise", "obsolete", "none"], ChangeActions.All);
        Assert.Equal(["I", "II"], ChangeClasses.All);
    }

    [Theory]
    [InlineData("PR", "Rejected", "pr.reject")]
    [InlineData("PR", "Closed", "pr.close")]
    [InlineData("ECR", "Closed", "ecr.close")]
    [InlineData("ECN", "Cancelled", "ecn.cancel")]
    [InlineData("ECN", "Implemented", null)]
    [InlineData("PR", "Evaluating", null)]
    public void Reason_scopes_follow_the_state_left_to(string type, string toState, string? scope) =>
        Assert.Equal(scope, ChangeService.ReasonScope(type, toState));

    [Fact]
    public void Refusals_say_how_to_reach_states_that_belong_to_the_workflow_or_the_notice()
    {
        Assert.Contains("CCB review", ChangeService.ViaMessage(ChangeTypes.Ecr, "workflow", "Approved"), StringComparison.Ordinal);
        Assert.Contains("release review", ChangeService.ViaMessage(ChangeTypes.Ecn, "workflow", "Released"), StringComparison.Ordinal);
        Assert.Contains("change notice that implements it", ChangeService.ViaMessage(ChangeTypes.Ecr, "change", "Implemented"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_snapshot_round_trips_and_is_deterministic()
    {
        var text = ChangeSnapshot.Serialize(Sample());
        Assert.Equal(text, ChangeSnapshot.Serialize(Sample()));
        Assert.Equal(Sample().Resulting, ChangeSnapshot.Parse(text).Resulting);
        Assert.Equal(Sample().Affected, ChangeSnapshot.Parse(text).Affected);
        Assert.StartsWith("{\"format\":1,\"type\":\"ECN\",\"number\":\"ECN-000007\"", text, StringComparison.Ordinal);
        Assert.NotEqual(text, ChangeSnapshot.Serialize(Sample(2)));
        Assert.Equal(32, ChangeSnapshot.Sha256(text).Length);
        Assert.NotEqual(ChangeSnapshot.Sha256(text), ChangeSnapshot.Sha256(text + " "));
    }

    [Fact]
    public void Submission_ids_are_stable_distinct_and_derived_from_change_and_number()
    {
        var change = Guid.Parse("018f0000-0000-7000-8000-000000000001");
        Assert.Equal(ChangeSnapshot.SubmissionId(change, 1), ChangeSnapshot.SubmissionId(change, 1));
        Assert.NotEqual(ChangeSnapshot.SubmissionId(change, 1), ChangeSnapshot.SubmissionId(change, 2));
        Assert.NotEqual(ChangeSnapshot.SubmissionId(change, 1), ChangeSnapshot.SubmissionId(Guid.NewGuid(), 1));
        Assert.NotEqual(change, ChangeSnapshot.SubmissionId(change, 1));
    }

    [Fact]
    public void The_reviewers_text_and_the_signed_attributes_show_what_will_be_released()
    {
        var text = ChangeSnapshot.Describe(Sample());
        Assert.StartsWith("ECN-000007 (Class II): Revise bracket", text, StringComparison.Ordinal);
        Assert.Contains("Created from ECR-000003", text, StringComparison.Ordinal);
        Assert.Contains("P-000001 revision A: revise", text, StringComparison.Ordinal);
        Assert.Contains("P-000001 revision B (B.2)", text, StringComparison.Ordinal);
        Assert.Contains("CUI//EXPT", text, StringComparison.Ordinal);

        var data = Sample();
        var attributes = ChangeSnapshot.Attributes(data, "aa", new Dictionary<Guid, string?> { [data.Resulting[0].IterationId] = "live123" });
        Assert.Equal(ChangeSnapshot.ContentFormat, attributes["format"]!.GetValue<string>());
        Assert.Equal("P-000001 revision A: revise", attributes["affected 1"]!.GetValue<string>());
        Assert.Contains("content live123", attributes["resulting 1"]!.GetValue<string>(), StringComparison.Ordinal);
        // The live fingerprint (not the stored one) is what is signed: a different live value changes the attributes, a missing one is flagged.
        var changed = ChangeSnapshot.Attributes(data, "aa", new Dictionary<Guid, string?> { [data.Resulting[0].IterationId] = "other" });
        Assert.NotEqual(attributes.ToJsonString(), changed.ToJsonString());
        Assert.Contains("missing", ChangeSnapshot.Attributes(data, "aa", new Dictionary<Guid, string?>())["resulting 1"]!.GetValue<string>(), StringComparison.Ordinal);
    }
}
