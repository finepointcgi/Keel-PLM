using System.Text.Json.Nodes;
using Bunit;
using Keel.Admin;
using Keel.Core;
using CanonicalJson = Keel.Admin.CanonicalJson;
using Keel.Notifications;
using Keel.Web.Components.Pages.Workflow;
using Keel.Web.Components.Visuals;
using Keel.Workflow;

namespace Keel.UnitTests;

/// <summary>Increment 9.6 (ADR-0056): the steps view of a workflow template, the preview map builder, the map label for a group chosen later, and the phone sign panel.</summary>
public sealed class WorkflowStepsTests : BunitContext
{
    private const string Seeded = """
        {
          "id": "part-release-review", "name": "Part release review", "description": "Serial technical review, then parallel approval, then release.", "objectType": "part",
          "slots": [
            { "name": "reviewer",  "label": "Technical reviewer", "role": "plm-viewer",   "min": 1, "max": 1 },
            { "name": "approvers", "label": "Approvers",          "role": "plm-approver", "min": 1, "max": 10 }
          ],
          "start": "flow",
          "nodes": [
            { "id": "flow", "type": "Serial", "name": "Release review", "steps": ["tech-review", "approval", "release"] },
            { "id": "tech-review", "type": "Task", "name": "Technical review", "kind": "review", "assignment": { "mode": "Slot", "slot": "reviewer" }, "dueDays": 5 },
            { "id": "approval", "type": "Parallel", "name": "Approval", "rule": { "type": "All" }, "fanOut": { "slot": "approvers", "kind": "approval" }, "dueDays": 5 },
            { "id": "release", "type": "End", "name": "Release", "outcome": "Release" }
          ]
        }
        """;

    [Fact]
    public void The_seeded_template_opens_in_the_steps_view_and_writes_back_the_same_definition()
    {
        var (draft, why) = StepsDraft.FromJson(Seeded);
        Assert.Null(why);
        Assert.Equal(["tech-review", "approval"], draft!.Steps.Select(s => s.Id));
        Assert.Equal((StepWho.Person, "Technical reviewer", "plm-viewer", 5), (draft.Steps[0].Who, draft.Steps[0].GroupLabel, draft.Steps[0].Role, draft.Steps[0].DueDays));
        Assert.Equal((StepWho.Group, "All", 1, 10), (draft.Steps[1].Who, draft.Steps[1].Rule, draft.Steps[1].Min, draft.Steps[1].Max));
        Assert.Equal(CanonicalJson.Serialize(JsonNode.Parse(Seeded)), CanonicalJson.Serialize(draft.ToJson()));
    }

    [Fact]
    public void Everything_the_steps_view_writes_is_accepted_by_the_engines_parser()
    {
        var draft = new StepsDraft { Id = "demo-flow", Name = "Demo", ObjectType = "part" };
        draft.Steps.Add(new StepDraft { Id = draft.NextStepId(), Name = "Peer", Kind = "review", Who = StepWho.Person, Role = Roles.Viewer, GroupLabel = "Peer" });
        draft.Steps.Add(new StepDraft { Id = draft.NextStepId(), Name = "Board", Kind = "approval", Who = StepWho.Group, Role = Roles.Approver, Min = 3, Max = 5, Rule = "NofM", RuleN = 2, DueDays = 7 });
        draft.Steps.Add(new StepDraft { Id = draft.NextStepId(), Name = "Anyone", Kind = "approval", Who = StepWho.Role, Role = Roles.Admin });
        Assert.Equal(["step-1", "step-2", "step-3"], draft.Steps.Select(s => s.Id));

        var parsed = TemplateParser.Parse(draft.ToJson().ToJsonString());
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Errors));
        var template = parsed.Value;
        Assert.Equal(["step-1", "step-2", "step-3", "release"], ((SerialNode)template.Node(template.Start)).Steps);
        var board = Assert.IsType<ParallelNode>(template.Node("step-2"));
        Assert.Equal((VoteKind.NofM, 2, 7), (board.Rule.Kind, board.Rule.N, board.DueDays));
        Assert.Equal(("step-1", AssignmentMode.Slot), (((TaskNode)template.Node("step-1")).Assignment.Slot, ((TaskNode)template.Node("step-1")).Assignment.Mode));
        Assert.Equal(AssignmentMode.Role, ((TaskNode)template.Node("step-3")).Assignment.Mode);
        Assert.Equal(["step-1", "step-2"], template.Slots.Select(s => s.Name));

        // The same draft read back is the same draft (the template round-trips through the steps view).
        var again = StepsDraft.FromJson(draft.ToJson().ToJsonString()).Draft!;
        Assert.Equal(CanonicalJson.Serialize(draft.ToJson()), CanonicalJson.Serialize(again.ToJson()));
    }

    [Fact]
    public void A_new_step_id_never_collides_with_an_existing_id_or_group_name()
    {
        var draft = new StepsDraft();
        draft.Steps.Add(new StepDraft { Id = "step-1" });
        draft.Steps.Add(new StepDraft { Id = "review", Slot = "step-2" });
        Assert.Equal("step-3", draft.NextStepId());
    }

    [Theory]
    [InlineData("""{"id":"a","name":"A","objectType":"part","slots":[],"start":"p","nodes":[{"id":"p","type":"Parallel","name":"P","rule":{"type":"Any"},"branches":["t"]},{"id":"t","type":"Task","name":"T","kind":"review","assignment":{"mode":"Role","role":"plm-approver"}}]}""", "advanced")]
    [InlineData("""{"id":"a","name":"A","objectType":"part","slots":[{"name":"s","label":"S","role":"plm-approver","min":1,"max":1}],"start":"m","nodes":[{"id":"m","type":"Serial","name":"M","steps":["a","b","e"]},{"id":"a","type":"Task","name":"A","kind":"review","assignment":{"mode":"Slot","slot":"s"}},{"id":"b","type":"Task","name":"B","kind":"review","assignment":{"mode":"Slot","slot":"s"}},{"id":"e","type":"End","name":"E","outcome":"Release"}]}""", "shared")]
    [InlineData("""{"id":"a","name":"A","objectType":"part","slots":[],"start":"m","nodes":[{"id":"m","type":"Serial","name":"M","steps":["e","t"]},{"id":"e","type":"End","name":"E","outcome":"Release"},{"id":"t","type":"Task","name":"T","kind":"review","assignment":{"mode":"Role","role":"plm-approver"}}]}""", "release")]
    [InlineData("""{"id":"a","name":"A","objectType":"part","slots":[],"start":"m","nodes":[{"id":"m","type":"Serial","name":"M","steps":["t","e"]},{"id":"t","type":"Task","name":"T","kind":"review","assignment":{"mode":"Role","role":"plm-approver"}},{"id":"e","type":"End","name":"E","outcome":"Release"},{"id":"x","type":"Serial","name":"X","steps":[]}]}""", "outside")]
    [InlineData("not json", "shape")]
    public void A_definition_that_is_not_a_plain_list_of_steps_is_left_to_the_advanced_views_with_a_reason(string json, string _)
    {
        var (draft, why) = StepsDraft.FromJson(json);
        Assert.Null(draft);
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    // ---------------------------------------------------------------- preview map

    [Fact]
    public void The_preview_builder_labels_groups_and_sizes_a_fan_out_by_the_groups_minimum()
    {
        var template = TemplateParser.Parse(Seeded.Replace("\"min\": 1, \"max\": 10", "\"min\": 2, \"max\": 10", StringComparison.Ordinal)).Value;
        var stages = WorkflowMapBuilder.Build(template, new ExecutionState(), new Dictionary<Guid, SignatureRow>(), new Dictionary<string, IReadOnlyList<Participant>>(),
            "Preview", DateTimeOffset.UtcNow, redacted: false, preview: true);
        Assert.Equal(["Technical review", "Approval", "Release"], stages.Select(s => s.Name));
        Assert.Equal("Technical reviewer", stages[0].Tasks.Single().SlotLabel);
        Assert.Equal((2, 2, "All"), (stages[1].Tasks.Count, stages[1].Total, stages[1].Rule));
        Assert.All(stages[1].Tasks, t => Assert.Equal("Approvers", t.SlotLabel));
        Assert.Equal("Waiting", stages[2].State);
    }

    [Fact]
    public void A_live_map_has_no_slot_labels_so_nothing_changes_for_running_reviews()
    {
        var template = TemplateParser.Parse(Seeded).Value;
        var stages = WorkflowMapBuilder.Build(template, new ExecutionState(), new Dictionary<Guid, SignatureRow>(), new Dictionary<string, IReadOnlyList<Participant>>(),
            InstanceStatus.Active, DateTimeOffset.UtcNow, redacted: false);
        Assert.All(stages.SelectMany(s => s.Tasks), t => Assert.Null(t.SlotLabel));
    }

    [Fact]
    public void The_map_view_says_that_a_person_is_chosen_when_the_review_starts()
    {
        var map = new WorkflowMap(Guid.Empty, "Demo", "Preview", false,
            [new MapStage("a", "Review", "Task", null, 1, 1, "Waiting", [new MapTask(null, "Review", "review", "Waiting", null, null, null, null, null, false, "Technical reviewer")])],
            "UNCLASSIFIED");
        var cut = Render<WorkflowMapView>(p => p.Add(x => x.Map, map));
        Assert.Contains("Chosen when the review starts: Technical reviewer", cut.Markup, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- phone sign panel

    [Fact]
    public void The_phone_sign_panel_uses_the_phone_layout_and_keeps_every_meaning_as_a_button()
    {
        var codes = new ReasonCodeView[] { new("Approved", "Approved", "approve", false), new("Rejected", "Rejected", "reject", true) };
        var cut = Render<SignPanel>(p => p.Add(x => x.ReasonCodes, codes).Add(x => x.CanSign, true).Add(x => x.Phone, true));
        Assert.Contains("sign-phone", cut.Find("section.sign-panel").ClassName, StringComparison.Ordinal);
        Assert.Equal(["Approved", "Rejected"], cut.FindAll(".decision-buttons button").Select(b => b.TextContent));
        Assert.Equal("Your decision", cut.Find("#sign-heading").TextContent);
        var desktop = Render<SignPanel>(p => p.Add(x => x.ReasonCodes, codes).Add(x => x.CanSign, true));
        Assert.DoesNotContain("sign-phone", desktop.Find("section.sign-panel").ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void Signed_messages_name_the_item_in_words()
    {
        Assert.Equal("MBOM", WorkflowText.Noun("mbom"));
        Assert.Equal("part", WorkflowText.Noun("part"));
        Assert.Equal("change", WorkflowText.Noun("ecn"));
    }

    [Theory]
    [InlineData("admin.workflow_draft_save")]
    [InlineData("admin.workflow_draft_discard")]
    [InlineData("admin.workflow_drafts_list")]
    [InlineData("admin.workflow_meanings_list")]
    [InlineData("admin.workflow_preview")]
    [InlineData("workflow.template_upload")]
    public void Every_audit_action_of_this_increment_is_classified_in_the_activity_catalog(string action) => Assert.True(ActivityCatalog.IsKnown(action));
}
