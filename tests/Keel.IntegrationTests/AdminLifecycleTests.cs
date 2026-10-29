using Keel.Admin;
using Keel.Audit;
using Keel.Core;
using Keel.Parts;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Versioned lifecycles (ADR-0022): new objects use the new version, old ones keep theirs until an audited, mapped migration.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AdminLifecycleTests(PostgresFixture pg)
{
    private static readonly Subject Ada = AdminEnv.Ada;

    /// <summary>The current part lifecycle plus a "Qualified" state reachable from In Work, and Obsolete relabelled.</summary>
    private static LifecycleDefinition WithQualified(LifecycleDefinition current)
    {
        var states = current.States.Select(s => s.State == "Obsolete" ? s with { Label = "Retired" } : s).ToList();
        states.Add(new LifecycleStateDef("Qualified", "Qualified", 15, false, false, false));
        var transitions = current.Transitions.ToList();
        transitions.Add(new LifecycleTransitionDef("InWork", "Qualified", "manual"));
        transitions.Add(new LifecycleTransitionDef("Qualified", "InWork", "manual"));
        return current with { States = states, Transitions = transitions };
    }

    private static async Task<LifecycleDefinition> CurrentAsync(AdminEnv env, string family = "part-default") =>
        AdminEnv.Ok(await env.Lifecycles.GetAsync(Ada, family, null)).Definition;

    [Fact]
    public async Task The_seeded_lifecycles_are_version_one_and_keep_working_unchanged()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var families = AdminEnv.Ok(await env.Lifecycles.ListFamiliesAsync(Ada));
        var part = families.Single(f => f.Family == "part-default");
        Assert.Equal((1, "part", true, true), (part.CurrentVersion, part.ObjectType, part.IsSystem, part.Editable));
        Assert.Equal(new[] { "baseline-default", "capa-default", "ct-default", "deviation-default", "document-default", "ecn-default", "ecr-default", "mbom-default", "ncr-default", "part-default", "pr-default", "reqspec-default" }.Order(), families.Select(f => f.Family).Order());
        Assert.False(families.Single(f => f.Family == "ecn-default").Editable);
        // The baseline lifecycle (2.1) is enforced by cfg.baseline triggers, so it is a view-only system family.
        Assert.Equal((false, true), (families.Single(f => f.Family == "baseline-default").Editable, families.Single(f => f.Family == "baseline-default").IsSystem));

        var number = await env.NewPartAsync();
        Assert.Equal("part-default", await env.TextAsync($"SELECT lifecycle_id FROM plm.part_master WHERE number = '{number}'"));
        var view = AdminEnv.Ok(await env.Lifecycles.GetAsync(Ada, "part-default", null));
        Assert.Equal(1, view.Usage.Single(u => u.State == "InWork").Revisions);
        Assert.Equal(1, view.TotalRevisions);
        Assert.Equal(["InWork", "UnderReview", "Released", "Obsolete"], view.Definition.States.OrderBy(s => s.SortOrder).Select(s => s.State));
    }

    [Fact]
    public async Task Publishing_a_new_version_affects_new_parts_only_and_the_audit_event_carries_before_and_after()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var oldPart = await env.NewPartAsync("Old part");
        var draft = WithQualified(await CurrentAsync(env));

        var validation = AdminEnv.Ok(await env.Lifecycles.ValidateAsync(Ada, "part-default", draft));
        Assert.Empty(validation.Errors);
        Assert.Contains(validation.Effects, e => e.Contains("Added state Qualified", StringComparison.Ordinal));
        Assert.Contains(validation.Effects, e => e.Contains("1 existing revision(s) stay on version 1", StringComparison.Ordinal));

        var published = AdminEnv.Ok(await env.Lifecycles.PublishVersionAsync(Ada, "part-default", draft, "Add the qualification step", "c-lc"));
        Assert.Equal((2, "published", true), (published.Definition.Version, published.Definition.Status, published.IsCurrent));

        var newPart = await env.NewPartAsync("New part");
        Assert.Equal("part-default~v2", await env.TextAsync($"SELECT lifecycle_id FROM plm.part_master WHERE number = '{newPart}'"));
        Assert.Equal("part-default", await env.TextAsync($"SELECT lifecycle_id FROM plm.part_master WHERE number = '{oldPart}'"));
        Assert.Contains((await env.PartAsync(newPart)).Transitions, t => t.To == "Qualified");
        Assert.DoesNotContain((await env.PartAsync(oldPart)).Transitions, t => t.To == "Qualified");
        // The new part can use the new state through the ordinary manual transition.
        var moved = await env.Parts.SetStateAsync(AdminEnv.Alice, newPart, "Qualified", null, false, null);
        Assert.True(moved.IsOk, AdminEnv.Errors(moved));
        Assert.Equal("Qualified", moved.Value!.Revision.State);
        // The old part's lifecycle is exactly what it was.
        Assert.Equal(OutcomeKind.Invalid, (await env.Parts.SetStateAsync(AdminEnv.Alice, oldPart, "Qualified", null, false, null)).Kind);

        var audit = (await env.AuditAsync()).Single(e => e.Action == "admin.lifecycle_publish");
        Assert.Equal(("part-default", AuditOutcome.Success, "c-lc"), (audit.ObjectId, audit.Outcome, audit.CorrelationId));
        var entry = ConfigHistoryService.Parse(audit);
        Assert.Equal("Add the qualification step", entry.Details["changeNote"]);
        Assert.Contains("\"version\":1", entry.Before!, StringComparison.Ordinal);
        Assert.Contains("\"version\":2", entry.After!, StringComparison.Ordinal);
        Assert.Contains("Qualified", entry.After!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_lifecycles_are_refused_with_messages_and_nothing_is_published()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var current = await CurrentAsync(env);

        var twoInitial = current with { States = [.. current.States.Select(s => s.State == "Released" ? s with { IsInitial = true } : s)] };
        var unreachable = current with { States = [.. current.States, new LifecycleStateDef("Orphan", "Orphan", 50, false, false, false)] };
        var dangling = current with { Transitions = [.. current.Transitions, new LifecycleTransitionDef("InWork", "Nowhere", "manual")] };
        var noLocked = current with { States = [.. current.States.Select(s => s with { IsLocked = false })] };
        var missingProtected = current with
        {
            States = [.. current.States.Where(s => s.State != "Released")],
            Transitions = [.. current.Transitions.Where(t => t.From != "Released" && t.To != "Released")],
        };
        var noTemplate = current with { Transitions = [.. current.Transitions.Where(t => t.From != "UnderReview" || t.To != "Released"), new LifecycleTransitionDef("UnderReview", "Released", "manual")] };

        foreach (var (draft, expected) in new[]
        {
            (twoInitial, "Exactly one initial state"), (unreachable, "not reachable"), (dangling, "does not exist"), (noLocked, "needs at least one locked state"),
            (missingProtected, "cannot be removed"), (noTemplate, "required by the review and change processes"),
        })
        {
            var r = await env.Lifecycles.PublishVersionAsync(Ada, "part-default", draft, "x", null);
            Assert.Equal(OutcomeKind.Invalid, r.Kind);
            Assert.Contains(expected, AdminEnv.Errors(r), StringComparison.Ordinal);
        }
        Assert.Equal(OutcomeKind.Invalid, (await env.Lifecycles.PublishVersionAsync(Ada, "part-default", current, "same", null)).Kind); // nothing changed
        Assert.Equal(OutcomeKind.Invalid, (await env.Lifecycles.PublishVersionAsync(Ada, "part-default", WithQualified(current), " ", null)).Kind); // note required
        Assert.Equal(1, await env.ScalarAsync("SELECT current_version FROM plm.lifecycle_family WHERE family = 'part-default'"));
    }

    [Fact]
    public async Task A_workflow_transition_needs_a_bound_workflow_template_and_change_lifecycles_are_view_only()
    {
        var env = await AdminEnv.CreateAsync(pg);
        // Documents have a review workflow since 0017 (template document-release-review): a NEW workflow-tagged transition is accepted because a template is active.
        var doc = await CurrentAsync(env, "document-default");
        var withNew = doc with
        {
            States = [.. doc.States, new LifecycleStateDef("Qualified", "Qualified", 15, false, false, false)],
            Transitions = [.. doc.Transitions, new LifecycleTransitionDef("InWork", "Qualified", "workflow"), new LifecycleTransitionDef("Qualified", "InWork", "manual")],
        };
        AdminEnv.Ok(await env.Lifecycles.PublishVersionAsync(Ada, "document-default", withNew, "Qualification", null));
        // The seeded review transitions stay workflow-tagged in version 1 (migration 0017).
        Assert.Contains(doc.Transitions, t => t.From == "InWork" && t.To == "UnderReview" && t.Via == "workflow");

        var ecn = await CurrentAsync(env, "ecn-default");
        var e = await Assert.ThrowsAsync<NotSupportedException>(() => env.Lifecycles.PublishVersionAsync(Ada, "ecn-default", ecn with { States = [.. ecn.States] }, "x", null));
        Assert.Contains("Phase 5", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migrating_objects_needs_a_complete_mapping_and_never_unlocks_released_data()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var inWork = await env.NewPartAsync("In work");
        var released = await env.NewPartAsync("Released");
        await env.ReleaseAsync(released);
        AdminEnv.Ok(await env.Lifecycles.PublishVersionAsync(Ada, "part-default", WithQualified(await CurrentAsync(env)), "v2", null));

        var plan = AdminEnv.Ok(await env.Lifecycles.PreviewMigrationAsync(Ada, "part-default", 1, 2));
        Assert.Equal(2, plan.Revisions);
        Assert.Equal(["InWork", "Released"], plan.SuggestedMapping.Keys.Order());

        // Mapping is required for every state in use.
        var missing = await env.Lifecycles.MigrateObjectsAsync(Ada, "part-default", 1, 2, new Dictionary<string, string> { ["InWork"] = "InWork" }, "Roll out", null);
        Assert.Equal(OutcomeKind.Invalid, missing.Kind);
        Assert.Contains("Released is in use by 1 revision(s) and needs a target state", AdminEnv.Errors(missing), StringComparison.Ordinal);
        // A locked state cannot map to an unlocked one.
        var unlock = await env.Lifecycles.MigrateObjectsAsync(Ada, "part-default", 1, 2, new Dictionary<string, string> { ["InWork"] = "InWork", ["Released"] = "Qualified" }, "Roll out", null);
        Assert.Equal(OutcomeKind.Invalid, unlock.Kind);
        Assert.Contains("would unlock released data", AdminEnv.Errors(unlock), StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await env.Lifecycles.MigrateObjectsAsync(Ada, "part-default", 1, 2, plan.SuggestedMapping, " ", null)).Kind);
        Assert.Equal(2, await env.ScalarAsync("SELECT count(*) FROM plm.part_master WHERE lifecycle_id = 'part-default'"));

        // A checked-out part moves state too: the mapping applies to every revision.
        var result = AdminEnv.Ok(await env.Lifecycles.MigrateObjectsAsync(Ada, "part-default", 1, 2, new Dictionary<string, string> { ["InWork"] = "Qualified", ["Released"] = "Released" }, "Roll out v2", "c-mig"));
        Assert.Equal((1L, 2L), (result.RevisionsMoved, result.ObjectsOnNewVersion));
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM plm.part_master WHERE lifecycle_id = 'part-default'"));
        var moved = await env.PartAsync(inWork);
        Assert.Equal(("Qualified", "part-default~v2"), (moved.Revision.State, moved.LifecycleId));
        Assert.Equal("Released", (await env.PartAsync(released)).Revision.State);
        Assert.True((await env.PartAsync(released)).Revision.IsLocked); // still locked

        var audit = (await env.AuditAsync()).Single(e => e.CorrelationId == "c-mig");
        var entry = ConfigHistoryService.Parse(audit);
        Assert.Equal("Roll out v2", entry.Reason);
        Assert.Contains("\"mapping\"", entry.After!, StringComparison.Ordinal);
        Assert.Contains("\"objects\":2", entry.Before!, StringComparison.Ordinal);
        // Only forward.
        Assert.Equal(OutcomeKind.Invalid, (await env.Lifecycles.MigrateObjectsAsync(Ada, "part-default", 2, 1, new Dictionary<string, string>(), "back", null)).Kind);
    }

    [Fact]
    public async Task Objects_under_an_active_review_or_checked_out_into_a_locked_state_block_the_migration()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var number = await env.NewPartAsync("Under review");
        var started = await env.Flow.StartAsync(AdminEnv.Alice, WorkflowEnv.Start(number, "rita", "bob", "carol"), null);
        Assert.True(started.IsOk);
        AdminEnv.Ok(await env.Lifecycles.PublishVersionAsync(Ada, "part-default", WithQualified(await CurrentAsync(env)), "v2", null));
        var blocked = await env.Lifecycles.MigrateObjectsAsync(Ada, "part-default", 1, 2,
            new Dictionary<string, string> { ["UnderReview"] = "UnderReview" }, "Roll out", null);
        Assert.Equal(OutcomeKind.Conflict, blocked.Kind);
        Assert.Contains("active review", AdminEnv.Errors(blocked), StringComparison.Ordinal);

        // A checked-out In Work part may not be mapped into a locked state.
        var env2 = await AdminEnv.CreateAsync(pg);
        var p = await env2.NewPartAsync();
        Assert.True((await env2.Parts.CheckOutAsync(AdminEnv.Alice, p, null)).IsOk);
        AdminEnv.Ok(await env2.Lifecycles.PublishVersionAsync(Ada, "part-default", WithQualified(await CurrentAsync(env2)), "v2", null));
        var toLocked = await env2.Lifecycles.MigrateObjectsAsync(Ada, "part-default", 1, 2, new Dictionary<string, string> { ["InWork"] = "UnderReview" }, "x", null);
        Assert.Equal(OutcomeKind.Invalid, toLocked.Kind);
        Assert.Contains("checked out", AdminEnv.Errors(toLocked), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_lifecycle_can_be_bound_to_a_part_type_and_the_database_refuses_edits_to_published_versions()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var def = await CurrentAsync(env);
        var created = AdminEnv.Ok(await env.Lifecycles.CreateFamilyAsync(Ada, "part-fastener", "Fastener lifecycle", "part", WithQualified(def), null));
        Assert.Equal(1, created.Definition.Version);
        AdminEnv.Ok(await env.Lists.SaveAsync(Ada, new RefItemInput(RefLists.PartType, "Standard", "Standard", 30, "part-fastener"), null));
        var standard = await env.Parts.CreateAsync(AdminEnv.Alice, new CreatePartCommand("Hex bolt", "Standard", "EA", "Buy", AdminEnv.Itar()), null);
        Assert.True(standard.IsOk, AdminEnv.Errors(standard));
        Assert.Equal("part-fastener", standard.Value!.LifecycleId);
        var component = await env.Parts.CreateAsync(AdminEnv.Alice, new CreatePartCommand("Bracket", "Component", "EA", "Make", AdminEnv.Itar()), null);
        Assert.Equal("part-default", component.Value!.LifecycleId);

        // An unknown or mismatched binding is refused.
        Assert.Equal(OutcomeKind.Invalid, (await env.Lists.SaveAsync(Ada, new RefItemInput(RefLists.PartType, "Standard", "Standard", 30, "document-default"), null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Lifecycles.CreateFamilyAsync(Ada, "part-fastener", "Again", "part", WithQualified(def), null)).Kind);

        // Safety net: the application role cannot touch a published version.
        await using var conn = await env.Ds.OpenConnectionAsync();
        foreach (var sql in new[]
        {
            "UPDATE plm.lifecycle_state SET label = 'Hacked' WHERE lifecycle_id = 'part-default' AND state = 'Released'",
            "INSERT INTO plm.lifecycle_state (lifecycle_id, state, label, sort_order) VALUES ('part-default', 'Sneaky', 'Sneaky', 99)",
            "UPDATE plm.lifecycle_transition SET via = 'manual' WHERE lifecycle_id = 'part-default' AND to_state = 'Released'",
            "UPDATE plm.lifecycle SET name = 'x' WHERE id = 'part-default'",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Contains("immutable", ex.MessageText, StringComparison.Ordinal);
        }
        await using var back = new NpgsqlCommand("UPDATE plm.lifecycle_family SET current_version = 1 WHERE family = 'part-fastener'", conn);
        Assert.Equal(1, await back.ExecuteNonQueryAsync()); // equal is a no-op, not a regression
    }
}
