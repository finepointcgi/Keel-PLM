using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Keel.Admin;
using Keel.Audit;
using Keel.Core;
using Keel.Workflow;

namespace Keel.IntegrationTests;

/// <summary>Increment 9.6 (ADR-0056): workflow drafts, template versions that running reviews keep, the template preview map, the steps editor's output run by the engine, and the phone review summary.</summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkflowAdminMobileTests(PostgresFixture pg)
{
    private static readonly Subject Ada = AdminEnv.Ada;

    private static async Task<string> SeededJsonAsync(AdminEnv env, string id = "part-release-review") =>
        AdminEnv.Ok(await env.Workflows.ListAsync(Ada, false, null)).Single(t => t.Id == id).Definition.ToJsonString();

    // ---------------------------------------------------------------- drafts

    [Fact]
    public async Task A_draft_is_saved_as_written_listed_replaced_and_audited_and_is_never_a_template_version()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var versionsBefore = await env.ScalarAsync("SELECT count(*) FROM wf.template");

        // An unfinished definition (it would not pass the engine's rules) can still be saved: validation happens when it is validated or published.
        const string unfinished = "{\"id\":\"wip-flow\",\"name\":\"Work in progress\",\"objectType\":\"part\",\"slots\":[],\"start\":\"main\",\"nodes\":[]}";
        var saved = AdminEnv.Ok(await env.Workflows.SaveDraftAsync(Ada, "wip-flow", 0, unfinished, "c-draft"));
        Assert.Equal((0, "ada"), (saved.BaseVersion, saved.UpdatedBy));
        Assert.Equal(OutcomeKind.Invalid, (await env.Workflows.ValidateAsync(Ada, unfinished)).Kind);

        var replaced = unfinished.Replace("Work in progress", "Still in progress", StringComparison.Ordinal);
        AdminEnv.Ok(await env.Workflows.SaveDraftAsync(Ada, "wip-flow", 0, replaced, null));
        var list = AdminEnv.Ok(await env.Workflows.ListDraftsAsync(Ada));
        Assert.Equal(replaced, Assert.Single(list).Definition);

        // Nothing was published, nothing can start from it, and the save is in the audit log with the template id.
        Assert.Equal(versionsBefore, await env.ScalarAsync("SELECT count(*) FROM wf.template"));
        Assert.DoesNotContain(AdminEnv.Ok(await env.Workflows.ListAsync(Ada, true, null)), t => t.Id == "wip-flow");
        var saveEvent = (await env.AuditAsync()).First(e => e.Action == "admin.workflow_draft_save" && e.CorrelationId == "c-draft");
        Assert.Equal(("workflow_template", "wip-flow", AuditOutcome.Success), (saveEvent.ObjectType, saveEvent.ObjectId, saveEvent.Outcome));

        Assert.True(AdminEnv.Ok(await env.Workflows.DiscardDraftAsync(Ada, "wip-flow", "c-discard")));
        Assert.Empty(AdminEnv.Ok(await env.Workflows.ListDraftsAsync(Ada)));
        Assert.Equal(OutcomeKind.NotFound, (await env.Workflows.DiscardDraftAsync(Ada, "wip-flow", null)).Kind);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "admin.workflow_draft_discard" && e.CorrelationId == "c-discard");
    }

    [Fact]
    public async Task Drafts_refuse_bad_ids_and_text_that_is_not_json_and_are_for_administrators_with_audited_denials()
    {
        var env = await AdminEnv.CreateAsync(pg);
        Assert.Equal(OutcomeKind.Invalid, (await env.Workflows.SaveDraftAsync(Ada, "Not An Id", 0, "{}", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Workflows.SaveDraftAsync(Ada, "ok-id", 0, "{not json", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Workflows.SaveDraftAsync(Ada, "ok-id", -1, "{}", null)).Kind);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM wf.template_draft"));

        Assert.Equal(OutcomeKind.Forbidden, (await env.Workflows.SaveDraftAsync(AdminEnv.Alice, "ok-id", 0, "{}", null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Workflows.ListDraftsAsync(AdminEnv.Alice)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Workflows.DiscardDraftAsync(AdminEnv.Alice, "ok-id", null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Workflows.ListSignatureMeaningsAsync(AdminEnv.Alice)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Flow.PreviewTemplateMapAsync(AdminEnv.Alice, await SeededJsonAsync(env), null)).Kind);
        var denied = (await env.AuditAsync()).Where(e => e.Outcome == AuditOutcome.Denied).Select(e => e.Action).ToList();
        Assert.Contains("admin.workflow_draft_save", denied);
        Assert.Contains("admin.workflow_drafts_list", denied);
        Assert.Contains("admin.workflow_draft_discard", denied);
        Assert.Contains("admin.workflow_meanings_list", denied);
        Assert.Contains("admin.workflow_preview", denied);
    }

    [Fact]
    public async Task Publishing_removes_the_draft_in_the_same_transaction_and_a_failed_publish_keeps_it()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var seeded = await SeededJsonAsync(env);
        AdminEnv.Ok(await env.Workflows.SaveDraftAsync(Ada, "part-release-review", 1, seeded, null));

        // The engine refuses the definition: the draft stays so the work is not lost.
        var broken = JsonNode.Parse(seeded)!.AsObject();
        broken["start"] = "nowhere";
        Assert.Equal(OutcomeKind.Invalid, (await env.Workflows.PublishAsync(Ada, broken.ToJsonString(), null)).Kind);
        Assert.Single(AdminEnv.Ok(await env.Workflows.ListDraftsAsync(Ada)));

        var published = AdminEnv.Ok(await env.Workflows.PublishAsync(Ada, seeded, "c-pub"));
        Assert.Equal(2, published.Version);
        Assert.Empty(AdminEnv.Ok(await env.Workflows.ListDraftsAsync(Ada)));
        var upload = (await env.AuditAsync()).Single(e => e.Action == "workflow.template_upload" && e.CorrelationId == "c-pub");
        Assert.Contains("draftRemoved", upload.Payload, StringComparison.Ordinal);
        Assert.Contains("True", upload.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signature_meanings_come_from_the_engines_reason_codes_per_task_kind()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var meanings = AdminEnv.Ok(await env.Workflows.ListSignatureMeaningsAsync(Ada));
        Assert.Contains(meanings, m => m.Code == "Approved" && m.AppliesTo.Contains("approval"));
        Assert.Contains(meanings, m => m.Code == "Reviewed" && m.AppliesTo.Contains("review"));
        Assert.DoesNotContain(meanings.Where(m => m.AppliesTo.Contains("review")), m => m.Code == "Approved");
    }

    // ---------------------------------------------------------------- versions

    [Fact]
    public async Task A_running_review_keeps_the_version_it_started_with_and_the_list_counts_running_reviews_per_version()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var number = await env.NewPartAsync();
        var started = await env.Flow.StartAsync(AdminEnv.Alice, WorkflowEnv.Start(number, "rita", "bob", "carol"), null);
        Assert.True(started.IsOk, AdminEnv.Errors(started));
        Assert.Equal(1, started.Value!.TemplateVersion);

        // Version 2 drops the technical review altogether.
        var steps = StepsDraft.FromJson(await SeededJsonAsync(env)).Draft!;
        steps.Steps.RemoveAt(0);
        var v2 = AdminEnv.Ok(await env.Workflows.PublishAsync(Ada, steps.ToJson().ToJsonString(), null));
        Assert.Equal(2, v2.Version);

        var running = AdminEnv.Ok(await env.Workflows.ListAsync(Ada, true, null)).Where(t => t.Id == "part-release-review").ToDictionary(t => t.Version, t => t.RunningReviews);
        Assert.Equal((1, 0), (running[1], running[2]));

        // The running review still has its technical review task, and its map is drawn from version 1.
        var instance = AdminEnv.Ok(await env.Flow.GetInstanceAsync(AdminEnv.Alice, started.Value.Id, null));
        Assert.Equal(1, instance.TemplateVersion);
        Assert.Contains(instance.Tasks, t => t.Name == "Technical review");
        var map = AdminEnv.Ok(await env.Flow.GetMapAsync(AdminEnv.Alice, started.Value.Id, null));
        Assert.Equal(["Technical review", "Approval", "Release"], map.Stages.Select(s => s.Name));

        // A review started later runs version 2.
        Assert.True((await env.Flow.CancelAsync(AdminEnv.Alice, started.Value.Id, "restart", null)).IsOk);
        var again = await env.Flow.StartAsync(AdminEnv.Alice, new StartReviewCommand("part", number, new Dictionary<string, string[]> { ["approvers"] = ["bob", "carol"] }), null);
        Assert.True(again.IsOk, AdminEnv.Errors(again));
        Assert.Equal(2, again.Value!.TemplateVersion);
        Assert.DoesNotContain(again.Value.Tasks, t => t.Name == "Technical review");
    }

    [Fact]
    public async Task Running_review_counts_cover_only_the_reviews_the_administrator_may_read()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var number = await env.NewPartAsync("ORCA bracket"); // ITAR, program ORCA
        Assert.True((await env.Flow.StartAsync(AdminEnv.Alice, WorkflowEnv.Start(number, "rita", "bob"), null)).IsOk);

        int Running(IEnumerable<TemplateVersionInfo> all) => all.Single(t => t.Id == "part-release-review" && t.Version == 1).RunningReviews;
        Assert.Equal(1, Running(AdminEnv.Ok(await env.Workflows.ListAsync(Ada, true, null))));
        // An administrator who is not on program ORCA is not told that an ORCA review exists, not even as a count.
        var outsider = Ada with { Programs = ImmutableSortedSet<string>.Empty };
        Assert.Equal(0, Running(AdminEnv.Ok(await env.Workflows.ListAsync(outsider, true, null))));
    }

    // ---------------------------------------------------------------- preview

    [Fact]
    public async Task The_preview_map_shows_the_steps_the_groups_and_the_vote_rule_without_storing_anything()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var versions = await env.ScalarAsync("SELECT count(*) FROM wf.template");
        var map = AdminEnv.Ok(await env.Flow.PreviewTemplateMapAsync(Ada, await SeededJsonAsync(env), null));
        Assert.Equal(("Part release review", "Preview", false), (map.TemplateName, map.Status, map.Redacted));
        Assert.Equal(["Technical review", "Approval", "Release"], map.Stages.Select(s => s.Name));
        Assert.All(map.Stages, s => Assert.Equal("Waiting", s.State));

        var review = map.Stages[0];
        var task = Assert.Single(review.Tasks);
        Assert.Equal(("Technical reviewer", "Waiting", null), (task.SlotLabel, task.Status, task.Person));
        var approval = map.Stages[1];
        Assert.Equal(("Parallel", "All", 1), (approval.Type, approval.Rule, approval.Total));
        Assert.Equal("Approvers", Assert.Single(approval.Tasks).SlotLabel);
        Assert.Equal(versions, await env.ScalarAsync("SELECT count(*) FROM wf.template"));
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM wf.instance"));
    }

    [Fact]
    public async Task The_preview_reports_the_engines_messages_for_a_definition_it_cannot_draw()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var broken = JsonNode.Parse(await SeededJsonAsync(env))!.AsObject();
        broken["start"] = "nowhere";
        var o = await env.Flow.PreviewTemplateMapAsync(Ada, broken.ToJsonString(), null);
        Assert.Equal(OutcomeKind.Invalid, o.Kind);
        Assert.Contains("nowhere", AdminEnv.Errors(o), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the steps editor's output runs in the engine

    [Fact]
    public async Task A_workflow_built_in_the_steps_editor_is_published_previewed_and_run_to_release_by_the_engine()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var steps = new StepsDraft { Id = "part-three-step", Name = "Three step review", ObjectType = "part" };
        steps.Steps.Add(new StepDraft { Id = steps.NextStepId(), Name = "Peer review", Kind = "review", Who = StepWho.Person, Role = Roles.Viewer, GroupLabel = "Peer", DueDays = 3 });
        steps.Steps.Add(new StepDraft { Id = "step-2", Name = "Board", Kind = "approval", Who = StepWho.Group, Role = Roles.Approver, GroupLabel = "Board", Min = 2, Max = 3, Rule = "NofM", RuleN = 2 });
        steps.Steps.Add(new StepDraft { Id = "step-3", Name = "Final sign-off", Kind = "approval", Who = StepWho.Role, Role = Roles.Approver });
        var json = steps.ToJson().ToJsonString();

        AdminEnv.Ok(await env.Workflows.ValidateAsync(Ada, json));
        var map = AdminEnv.Ok(await env.Flow.PreviewTemplateMapAsync(Ada, json, null));
        Assert.Equal(["Peer review", "Board", "Final sign-off", "Release"], map.Stages.Select(s => s.Name));
        Assert.Equal("2 of 2", map.Stages[1].Rule);
        Assert.Equal("plm-approver", Assert.Single(map.Stages[2].Tasks).PoolRole);

        AdminEnv.Ok(await env.Workflows.PublishAsync(Ada, json, null));
        var number = await env.NewPartAsync();
        var started = await env.Flow.StartAsync(AdminEnv.Alice, new StartReviewCommand("part", number, new Dictionary<string, string[]>
        {
            ["step-1"] = ["rita"], ["step-2"] = ["bob", "carol"],
        }, "part-three-step"), null);
        Assert.True(started.IsOk, AdminEnv.Errors(started));
        var v = started.Value!;
        v = await SignAsync(env, AdminEnv.Rita, v.Tasks.Single(t => t.Status == "Open"), "Reviewed");
        v = await SignAsync(env, AdminEnv.Bob, v.Tasks.Single(t => t.Status == "Open" && t.AssigneeUsername == "bob"), "Approved");
        v = await SignAsync(env, AdminEnv.Carol, v.Tasks.Single(t => t.Status == "Open" && t.AssigneeUsername == "carol"), "Approved");
        var pool = v.Tasks.Single(t => t.Status == "Open");
        Assert.Equal(("Final sign-off", null), (pool.Name, pool.AssigneeUsername));
        v = await SignAsync(env, AdminEnv.Bob, pool, "Approved");
        Assert.Equal("Approved", v.Status);
        Assert.Equal("Released", (await env.PartAsync(number)).Revision.State);
    }

    private static async Task<InstanceView> SignAsync(AdminEnv env, Subject who, TaskItem task, string code)
    {
        var r = await env.Flow.SignAsync(WorkflowEnv.Fresh(who), task.Id, code, null, null);
        Assert.True(r.IsOk, $"{who.Username}: {r.Kind} {AdminEnv.Errors(r)}");
        return r.Value!;
    }

    // ---------------------------------------------------------------- phone review summary

    [Fact]
    public async Task The_review_summary_states_the_content_hash_the_signature_then_records_and_the_submitters_note()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var number = await env.NewPartAsync();
        var started = await env.Flow.StartAsync(AdminEnv.Alice, WorkflowEnv.Start(number, "rita", "bob") with { Comment = "Raised the wall thickness to 2 mm." }, null);
        Assert.True(started.IsOk, AdminEnv.Errors(started));
        var task = started.Value!.Tasks.Single(t => t.Status == "Open");

        var detail = AdminEnv.Ok(await env.Flow.GetTaskAsync(AdminEnv.Rita, task.Id, null));
        var review = detail.Review!;
        Assert.Equal("Raised the wall thickness to 2 mm.", review.SubmitterNote);
        Assert.Equal(("Part release review", 1), (review.TemplateName, review.TemplateVersion));
        Assert.Matches("^[0-9a-f]{64}$", review.ContentHash);
        Assert.Equal(number, detail.Summary.Target!.Key);
        Assert.Equal("A", detail.Summary.Target.Revision);
        Assert.False(string.IsNullOrEmpty(detail.Banner));

        // What the phone shows before signing is what the signature records.
        var signed = await SignAsync(env, AdminEnv.Rita, task, "Reviewed");
        Assert.Equal(review.ContentHash, Assert.Single(signed.Signatures).ContentHash);
    }

    [Fact]
    public async Task A_reader_who_lost_access_gets_no_summary_no_hash_and_nothing_that_names_the_item()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var number = await env.NewPartAsync("Secret bracket, ORCA");
        var started = await env.Flow.StartAsync(AdminEnv.Alice, WorkflowEnv.Start(number, "rita", "bob") with { Comment = "Secret change note" }, null);
        var task = started.Value!.Tasks.Single(t => t.Status == "Open");
        var openHash = AdminEnv.Ok(await env.Flow.GetTaskAsync(AdminEnv.Rita, task.Id, null)).Review!.ContentHash;

        // Rita is taken off program ORCA: the task stays (she is the assignee) but is redacted.
        var ritaNow = AdminEnv.Rita with { Programs = ImmutableSortedSet<string>.Empty };
        var detail = AdminEnv.Ok(await env.Flow.GetTaskAsync(ritaNow, task.Id, null));
        Assert.True(detail.Summary.Redacted);
        Assert.Null(detail.Review);
        Assert.False(detail.CanSign);
        var wire = JsonSerializer.Serialize(detail);
        Assert.DoesNotContain(number, wire, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", wire, StringComparison.Ordinal);
        Assert.DoesNotContain(openHash, wire, StringComparison.Ordinal);
        Assert.DoesNotContain("ITAR", wire, StringComparison.OrdinalIgnoreCase);

        // The list the phone shows redacts it the same way, and a stranger cannot open the task at all.
        var item = Assert.Single((await env.Flow.MyTasksAsync(ritaNow, null)).Items);
        Assert.True(item.Redacted);
        Assert.Null(item.Target);
        Assert.Equal(OutcomeKind.NotFound, (await env.Flow.GetTaskAsync(AdminEnv.Mallory, task.Id, null)).Kind);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "workflow.task_read" && e.Outcome == AuditOutcome.Denied);
    }
}
