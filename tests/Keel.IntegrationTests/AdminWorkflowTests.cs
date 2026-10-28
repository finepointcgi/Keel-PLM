using System.Text.Json.Nodes;
using Keel.Admin;
using Keel.Audit;
using Keel.Core;

namespace Keel.IntegrationTests;

/// <summary>Workflow template management through the Keel.Core interface: structured drafts, validation, publishing, diffs and pinning.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AdminWorkflowTests(PostgresFixture pg)
{
    private static readonly Subject Ada = AdminEnv.Ada;

    private static async Task<TemplateDraft> SeededDraftAsync(AdminEnv env, string id = "part-release-review")
    {
        var versions = AdminEnv.Ok(await env.Workflows.ListAsync(Ada, false, null));
        var (draft, error) = TemplateDraft.FromJson(versions.Single(t => t.Id == id).Definition.ToJsonString());
        Assert.Null(error);
        return draft!;
    }

    [Fact]
    public async Task The_structured_draft_round_trips_the_seeded_template_and_publishes_as_the_next_version()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var draft = await SeededDraftAsync(env);
        Assert.Equal(["flow", "tech-review", "approval", "release"], draft.Nodes.Select(n => n.Id));
        Assert.Equal("Slot", draft.Nodes.Single(n => n.Id == "tech-review").AssignmentMode);
        Assert.Equal("All", draft.Nodes.Single(n => n.Id == "approval").Rule);

        // The draft re-serializes to the stored definition.
        var stored = AdminEnv.Ok(await env.Workflows.ListAsync(Ada, false, null)).Single(t => t.Id == "part-release-review").Definition;
        Assert.Equal(CanonicalJson.Serialize(stored), CanonicalJson.Serialize(draft.ToJson()));

        // Edit: a majority vote of two approvers and a longer review deadline.
        var approval = draft.Nodes.Single(n => n.Id == "approval");
        approval.Rule = "NofM";
        approval.RuleN = 2;
        draft.Nodes.Single(n => n.Id == "tech-review").DueDays = 10;
        draft.Slots.Single(s => s.Name == "approvers").Min = 2;
        var v2 = AdminEnv.Ok(await env.Workflows.PublishAsync(Ada, draft.ToJson().ToJsonString(), "c-wf"));
        Assert.Equal((2, true), (v2.Version, v2.IsActive));

        var all = AdminEnv.Ok(await env.Workflows.ListAsync(Ada, true, null));
        Assert.Equal([2, 1], all.Where(t => t.Id == "part-release-review").Select(t => t.Version));
        Assert.Single(AdminEnv.Ok(await env.Workflows.ListAsync(Ada, false, null)), t => t.Id == "part-release-review");

        var diff = AdminEnv.Ok(await env.Workflows.DiffAsync(Ada, "part-release-review", 1, 2));
        Assert.Contains(diff, d => d.Contains("node 'approval', rule", StringComparison.Ordinal) && d.Contains("NofM", StringComparison.Ordinal));
        Assert.Contains(diff, d => d.Contains("node 'tech-review', dueDays: 5 → 10", StringComparison.Ordinal));
        Assert.Contains(diff, d => d.Contains("slot 'approvers', min", StringComparison.Ordinal));

        // The upload is audited by the workflow module, and the history page lists it.
        Assert.Contains(await env.AuditAsync(), e => e.Action == "workflow.template_upload" && e.CorrelationId == "c-wf" && e.Outcome == AuditOutcome.Success);
        var history = AdminEnv.Ok(await env.History.ListAsync(Ada, 50, "template"));
        Assert.Contains(history, h => h.Action == "workflow.template_upload");
    }

    [Fact]
    public async Task Validation_errors_come_from_the_engines_own_parser_and_nothing_is_stored()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var draft = await SeededDraftAsync(env);
        draft.Nodes.Single(n => n.Id == "tech-review").Slot = "nobody";        // undeclared slot
        draft.Nodes.Single(n => n.Id == "flow").Steps.Add("missing-node");     // dangling step
        draft.ObjectType = "widget";                                           // no workflow runs on it

        var validation = await env.Workflows.ValidateAsync(Ada, draft.ToJson().ToJsonString());
        Assert.Equal(OutcomeKind.Invalid, validation.Kind);
        var text = AdminEnv.Errors(validation);
        Assert.Contains("slot 'nobody' is not declared", text, StringComparison.Ordinal);
        Assert.Contains("missing-node", text, StringComparison.Ordinal);

        var publish = await env.Workflows.PublishAsync(Ada, draft.ToJson().ToJsonString(), null);
        Assert.Equal(OutcomeKind.Invalid, publish.Kind);
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM wf.template WHERE id = 'part-release-review'"));

        // A valid draft of an unknown object type is refused with its own message.
        var ok = await SeededDraftAsync(env);
        ok.ObjectType = "widget";
        Assert.Contains("No workflow can run on objects of type 'widget'", AdminEnv.Errors(await env.Workflows.ValidateAsync(Ada, ok.ToJson().ToJsonString())), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pinning_chooses_the_template_that_Submit_for_review_uses_and_is_audited_with_before_and_after()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var number = await env.NewPartAsync();
        var fast = await SeededDraftAsync(env);
        fast.Id = "part-fast-review";
        fast.Name = "Part fast review";
        AdminEnv.Ok(await env.Workflows.PublishAsync(Ada, fast.ToJson().ToJsonString(), null));

        // Two active templates for parts and no pin: the engine asks the user to choose.
        var ambiguous = await env.Flow.StartAsync(AdminEnv.Alice, WorkflowEnv.Start(number, "rita", "bob"), null);
        Assert.Equal(OutcomeKind.Invalid, ambiguous.Kind);
        Assert.Contains("Choose a workflow template", AdminEnv.Errors(ambiguous), StringComparison.Ordinal);

        var pin = AdminEnv.Ok(await env.Workflows.PinAsync(Ada, "part", "part-fast-review", "c-pin"))!;
        Assert.Equal(("part", "part-fast-review"), (pin.ObjectType, pin.TemplateId));
        var started = await env.Flow.StartAsync(AdminEnv.Alice, WorkflowEnv.Start(number, "rita", "bob"), null);
        Assert.True(started.IsOk, AdminEnv.Errors(started));
        Assert.Equal("part-fast-review", started.Value!.TemplateId);

        // An explicit choice still wins over the pin.
        Assert.True((await env.Flow.CancelAsync(AdminEnv.Alice, started.Value.Id, "restart", null)).IsOk);
        var explicitChoice = await env.Flow.StartAsync(AdminEnv.Alice, WorkflowEnv.Start(number, "rita", "bob") with { TemplateId = "part-release-review" }, null);
        Assert.True(explicitChoice.IsOk, AdminEnv.Errors(explicitChoice));
        Assert.Equal("part-release-review", explicitChoice.Value!.TemplateId);

        var event1 = (await env.AuditAsync()).Single(e => e.Action == "admin.workflow_pin" && e.CorrelationId == "c-pin");
        var entry = ConfigHistoryService.Parse(event1);
        Assert.Equal((null, "part-fast-review"), (entry.Before, entry.After));
        Assert.Equal("part", event1.ObjectId);

        Assert.Single(AdminEnv.Ok(await env.Workflows.ListPinsAsync(Ada)));
        Assert.Null(AdminEnv.Ok(await env.Workflows.PinAsync(Ada, "part", null, null)));
        Assert.Empty(AdminEnv.Ok(await env.Workflows.ListPinsAsync(Ada)));
        Assert.Equal(OutcomeKind.Invalid, (await env.Workflows.PinAsync(Ada, "part", "ecn-release-review", null)).Kind); // wrong object type
        Assert.Equal(OutcomeKind.Invalid, (await env.Workflows.PinAsync(Ada, "part", "no-such", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Workflows.PinAsync(Ada, "widget", "part-fast-review", null)).Kind);
    }

    [Fact]
    public async Task Template_administration_is_for_administrators_and_denials_are_audited()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var draft = await SeededDraftAsync(env);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Workflows.PublishAsync(AdminEnv.Alice, draft.ToJson().ToJsonString(), null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Workflows.PinAsync(AdminEnv.Alice, "part", "part-release-review", null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Workflows.ValidateAsync(AdminEnv.Alice, draft.ToJson().ToJsonString())).Kind);
        var denied = (await env.AuditAsync()).Where(e => e.Outcome == AuditOutcome.Denied).Select(e => e.Action).ToList();
        Assert.Contains("workflow.template_upload", denied);
        Assert.Contains("admin.workflow_pin", denied);
        Assert.Contains("admin.workflow_template_validate", denied);
        Assert.Equal(["ecn", "ecr", "part"], env.Workflows.ReviewableObjectTypes);
    }

    [Fact]
    public async Task The_json_view_accepts_what_the_structured_editor_cannot_show()
    {
        var (draft, error) = TemplateDraft.FromJson("""{"id":"x","name":"X","objectType":"part","start":"a","slots":[],"nodes":[{"id":"a","type":"Condition","name":"A"}]}""");
        Assert.Null(draft);
        Assert.Contains("JSON view", error, StringComparison.Ordinal);
        Assert.Null(TemplateDraft.FromJson("[]").Draft);
        Assert.NotNull(JsonNode.Parse(new TemplateDraft { Id = "t", Name = "T", Start = "a" }.ToJson().ToJsonString()));
    }
}
