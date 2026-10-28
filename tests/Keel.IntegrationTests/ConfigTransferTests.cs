using System.Text.Json;
using System.Text.Json.Nodes;
using Keel.Admin;
using Keel.Audit;
using Keel.Core;

namespace Keel.IntegrationTests;

/// <summary>Configuration export and import (dev to test to production): canonical, hashed, dry-run first, applied through the audited services.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ConfigTransferTests(PostgresFixture pg)
{
    private static readonly Subject Ada = AdminEnv.Ada;

    /// <summary>A "development" environment with a bit of everything configured.</summary>
    private static async Task ConfigureAsync(AdminEnv env)
    {
        AdminEnv.Ok(await env.Attributes.CreateAsync(Ada, "part", AdminEnv.Number("diameter", "Diameter", "mm", 1, 500), null));
        AdminEnv.Ok(await env.Attributes.CreateAsync(Ada, "part", AdminEnv.Enum("grade", "Grade", "A", "B"), null));
        AdminEnv.Ok(await env.Attributes.SetRetiredAsync(Ada, "part", "finish", true, "Replaced", null));
        AdminEnv.Ok(await env.Lists.SaveAsync(Ada, new RefItemInput(RefLists.Unit, "yd", "Yard", 200), null));
        AdminEnv.Ok(await env.Lists.SaveAsync(Ada, new RefItemInput(RefLists.PartType, "Harness", "Wiring harness", 50), null));
        AdminEnv.Ok(await env.Lists.SaveAsync(Ada, new RefItemInput(RefLists.Program, "KRAKEN", "KRAKEN (synthetic)", 20), null));
        AdminEnv.Ok(await env.Lists.SetActiveAsync(Ada, RefLists.DocumentType, "Other", false, "Not used", null));
        AdminEnv.Ok(await env.Numbering.UpdateAsync(Ada, "part", "PRT-", 7, "Scheme", null));
        AdminEnv.Ok(await env.Reasons.SaveAsync(Ada, new ReasonCodeDto("signature", null, "Verified", "Verified", "approve", false, ["review"], 15, true), null));
        AdminEnv.Ok(await env.Reasons.SaveAsync(Ada, new ReasonCodeDto("change", "pr.reject", "TestEscape", "Test escape", null, null, null, 50, true), null));

        var current = AdminEnv.Ok(await env.Lifecycles.GetAsync(Ada, "part-default", null)).Definition;
        AdminEnv.Ok(await env.Lifecycles.PublishVersionAsync(Ada, "part-default", current with
        {
            States = [.. current.States, new LifecycleStateDef("Qualified", "Qualified", 15, false, false, false)],
            Transitions = [.. current.Transitions, new LifecycleTransitionDef("InWork", "Qualified", "manual"), new LifecycleTransitionDef("Qualified", "InWork", "manual")],
        }, "Qualification", null));
        AdminEnv.Ok(await env.Lifecycles.CreateFamilyAsync(Ada, "part-wiring", "Wiring lifecycle", "part", current with { Name = "Wiring lifecycle" }, null));
        AdminEnv.Ok(await env.Lists.SaveAsync(Ada, new RefItemInput(RefLists.PartType, "Harness", "Wiring harness", 50, "part-wiring"), null));

        var versions = AdminEnv.Ok(await env.Workflows.ListAsync(Ada, false, null));
        var (draft, _) = TemplateDraft.FromJson(versions.Single(t => t.Id == "part-release-review").Definition.ToJsonString());
        draft!.Nodes.Single(n => n.Id == "tech-review").DueDays = 9;
        AdminEnv.Ok(await env.Workflows.PublishAsync(Ada, draft.ToJson().ToJsonString(), null));
        draft.Id = "part-fast-review";
        draft.Name = "Part fast review";
        AdminEnv.Ok(await env.Workflows.PublishAsync(Ada, draft.ToJson().ToJsonString(), null));
        AdminEnv.Ok(await env.Workflows.PinAsync(Ada, "part", "part-fast-review", null));

        var c = env.Classes;
        AdminEnv.Ok(await c.CreateNodeAsync(Ada, new ClassNodeInput("MECH", "Mechanical", "Mechanical parts", null, 10), null));
        AdminEnv.Ok(await c.CreateNodeAsync(Ada, new ClassNodeInput("FAST", "Fasteners", null, "MECH", 10), null));
        AdminEnv.Ok(await c.SaveAttributeAsync(Ada, "MECH", AdminEnv.Enum("material", "Material", "Steel", "Titanium"), true, null));
        AdminEnv.Ok(await c.SaveAttributeAsync(Ada, "FAST", AdminEnv.Number("pitch", "Pitch", "mm", 0.1m, 10), true, null));
    }

    [Fact]
    public async Task The_export_is_canonical_versioned_hashed_and_free_of_secrets_counters_and_object_data()
    {
        var env = await AdminEnv.CreateAsync(pg);
        await ConfigureAsync(env);
        await env.NewPartAsync();
        var a = AdminEnv.Ok(await env.Config.ExportAsync(Ada, "c-export"));
        var b = AdminEnv.Ok(await env.Config.ExportAsync(Ada, null));
        Assert.Equal("keel-config/1", a.Format);
        Assert.Equal(a.Sha256, b.Sha256); // deterministic: nothing about time, ids or counters is in the content
        Assert.Matches("^[0-9a-f]{64}$", a.Sha256);
        Assert.Equal(ConfigDocument.HashOf(a.Content), a.Sha256);

        var json = a.ToJson();
        var text = json.ToLowerInvariant();
        foreach (var forbidden in new[] { "password", "secret", "token", "privatekey", "connectionstring", "accesskey", "sub-alice" })
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        Assert.DoesNotContain("P-000001", json, StringComparison.Ordinal);       // no object data
        Assert.DoesNotContain("nextValue", json, StringComparison.Ordinal);      // no counters
        Assert.Contains("PRT-", json, StringComparison.Ordinal);                  // prefixes are configuration

        var (parsed, error) = ConfigDocument.FromJson(json);
        Assert.Null(error);
        Assert.Equal(a.Sha256, parsed!.Sha256);
        Assert.Contains(parsed.Content.Lifecycles, l => l.Family == "part-wiring");
        Assert.Contains(parsed.Content.Lists["part-type"], i => i is { Code: "Harness", LifecycleFamily: "part-wiring" });
        Assert.Equal("part-fast-review", parsed.Content.TemplatePins.Single().TemplateId);
        Assert.Equal(1, (await env.AuditAsync()).Count(e => e.Action == "admin.config_export" && e.CorrelationId == "c-export" && e.Payload.Contains(a.Sha256, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_file_that_was_changed_after_export_is_rejected()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var doc = AdminEnv.Ok(await env.Config.ExportAsync(Ada, null));
        var node = JsonNode.Parse(doc.ToJson())!;
        node["content"]!["numbering"]![0]!["prefix"] = "HACK-";
        var (parsed, error) = ConfigDocument.FromJson(node.ToJsonString());
        Assert.Null(parsed);
        Assert.Contains("hash does not match", error, StringComparison.Ordinal);
        var other = JsonNode.Parse(doc.ToJson())!;
        other["format"] = "keel-config/2";
        Assert.Contains("Unsupported format", ConfigDocument.FromJson(other.ToJsonString()).Error, StringComparison.Ordinal);
        Assert.NotNull(ConfigDocument.FromJson("not json").Error);
    }

    [Fact]
    public async Task Promoting_from_one_environment_to_another_dry_runs_first_then_applies_and_both_exports_match()
    {
        var dev = await AdminEnv.CreateAsync(pg);
        await ConfigureAsync(dev);
        var exported = AdminEnv.Ok(await dev.Config.ExportAsync(Ada, null));

        var test = await AdminEnv.CreateAsync(pg);
        var before = AdminEnv.Ok(await test.Config.ExportAsync(Ada, null));

        // Dry run: what would be created, changed or retired. Nothing is written.
        var plan = AdminEnv.Ok(await test.Config.DryRunAsync(Ada, exported));
        Assert.True(plan.DryRun);
        Assert.Contains(plan.Changes, c => c is { Section: "attributes/part", Key: "diameter", Kind: "create" });
        Assert.Contains(plan.Changes, c => c is { Section: "attributes/part", Key: "finish", Kind: "retire" });
        Assert.Contains(plan.Changes, c => c is { Section: "lists/unit", Key: "yd", Kind: "create" });
        Assert.Contains(plan.Changes, c => c is { Section: "lists/document-type", Key: "Other", Kind: "retire" });
        Assert.Contains(plan.Changes, c => c is { Section: "lifecycles", Key: "part-default", Kind: "change" });
        Assert.Contains(plan.Changes, c => c is { Section: "lifecycles", Key: "part-wiring", Kind: "create" });
        Assert.Contains(plan.Changes, c => c is { Section: "numbering", Key: "part", Kind: "change" });
        Assert.Contains(plan.Changes, c => c is { Section: "workflowTemplates", Key: "part-fast-review", Kind: "create" });
        Assert.Contains(plan.Changes, c => c is { Section: "workflowTemplates", Key: "part-release-review", Kind: "change" });
        Assert.Contains(plan.Changes, c => c is { Section: "templatePins", Key: "part", Kind: "create" });
        Assert.Contains(plan.Changes, c => c is { Section: "classification", Key: "FAST", Kind: "create" });
        Assert.Contains(plan.Changes, c => c is { Section: "reasonCodes", Key: "signature//Verified", Kind: "create" });
        Assert.Equal(before.Sha256, AdminEnv.Ok(await test.Config.ExportAsync(Ada, null)).Sha256);

        // A reason is required, and only administrators may apply.
        Assert.Equal(OutcomeKind.Invalid, (await test.Config.ApplyAsync(Ada, exported, " ", null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await test.Config.ApplyAsync(AdminEnv.Alice, exported, "x", null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await test.Config.DryRunAsync(AdminEnv.Alice, exported)).Kind);

        var applied = AdminEnv.Ok(await test.Config.ApplyAsync(Ada, exported, "Promote 2026.10 to test", "c-import"));
        Assert.False(applied.DryRun);
        Assert.True(applied.Applied >= plan.Changes.Count);

        // The target now exports exactly what the source exported, and a second dry run has nothing left to do.
        var after = AdminEnv.Ok(await test.Config.ExportAsync(Ada, null));
        Assert.Equal(exported.Sha256, after.Sha256);
        Assert.Empty(AdminEnv.Ok(await test.Config.DryRunAsync(Ada, exported)).Changes);
        Assert.Equal(OutcomeKind.Ok, (await test.Config.ApplyAsync(Ada, exported, "Again", null)).Kind); // idempotent

        // The import went through the audited services plus one summary event.
        var audit = await test.AuditAsync();
        Assert.Contains(audit, e => e.Action == "admin.config_import" && e.CorrelationId == "c-import" && e.Payload.Contains(exported.Sha256, StringComparison.Ordinal));
        Assert.Contains(audit, e => e.Action == "admin.attribute_create" && e.ObjectId == "part/diameter");
        Assert.Contains(audit, e => e.Action == "admin.lifecycle_publish");
        Assert.Contains(audit, e => e.Action == "workflow.template_upload");
        Assert.Contains(audit, e => e.Action == "admin.class_import");

        // And it works: parts in the target use the imported configuration.
        var part = await test.Parts.CreateAsync(AdminEnv.Alice, new Keel.Parts.CreatePartCommand("Main harness", "Harness", "yd", "Make", AdminEnv.Itar(), null,
            new JsonObject { ["diameter"] = 12 }), null);
        Assert.True(part.IsOk, AdminEnv.Errors(part));
        Assert.StartsWith("PRT-", part.Value!.Number, StringComparison.Ordinal);
        Assert.Equal("part-wiring", part.Value.LifecycleId);
    }

    [Fact]
    public async Task A_change_the_import_cannot_make_safely_is_reported_as_blocked_and_nothing_is_applied()
    {
        var dev = await AdminEnv.CreateAsync(pg);
        AdminEnv.Ok(await dev.Classes.CreateNodeAsync(Ada, new ClassNodeInput("A", "A", null, null), null));
        AdminEnv.Ok(await dev.Classes.CreateNodeAsync(Ada, new ClassNodeInput("B", "B", null, "A"), null));
        var exported = AdminEnv.Ok(await dev.Config.ExportAsync(Ada, null));

        var test = await AdminEnv.CreateAsync(pg);
        AdminEnv.Ok(await test.Classes.CreateNodeAsync(Ada, new ClassNodeInput("A", "A", null, null), null));
        AdminEnv.Ok(await test.Classes.CreateNodeAsync(Ada, new ClassNodeInput("B", "B", null, null), null)); // B is a root here but under A in the file
        AdminEnv.Ok(await test.Lists.SaveAsync(Ada, new RefItemInput(RefLists.Unit, "furlong", "Furlong", 999), null));
        var plan = AdminEnv.Ok(await test.Config.DryRunAsync(Ada, exported));
        Assert.Contains(plan.Changes, c => c is { Section: "classification", Key: "B", Kind: "blocked" });
        var apply = await test.Config.ApplyAsync(Ada, exported, "x", null);
        Assert.Equal(OutcomeKind.Invalid, apply.Kind);
        Assert.Contains("use Move", AdminEnv.Errors(apply), StringComparison.Ordinal);
        Assert.Equal(0, await test.ScalarAsync("SELECT count(*) FROM audit.event WHERE action = 'admin.config_import'"));
    }

    [Fact]
    public async Task The_history_page_lists_configuration_changes_with_before_after_diffs_and_denials()
    {
        var env = await AdminEnv.CreateAsync(pg);
        AdminEnv.Ok(await env.Attributes.CreateAsync(Ada, "part", AdminEnv.Number("gauge", "Gauge"), null));
        AdminEnv.Ok(await env.Attributes.UpdateAsync(Ada, "part", AdminEnv.Number("gauge", "Wire gauge", "AWG"), "c-h"));
        await env.Attributes.CreateAsync(AdminEnv.Alice, "part", AdminEnv.Number("nope", "Nope"), null); // denied

        var history = AdminEnv.Ok(await env.History.ListAsync(Ada, 50, null));
        var update = history.Single(h => h.Action == "admin.attribute_update");
        Assert.Equal(("ada", "Success", "part/gauge"), (update.Actor, update.Outcome, update.ObjectId));
        Assert.Contains(update.Diff, d => d.StartsWith("label: \"Gauge\" → \"Wire gauge\"", StringComparison.Ordinal));
        Assert.Contains(update.Diff, d => d.StartsWith("revision:", StringComparison.Ordinal));
        Assert.Contains(history, h => h is { Action: "admin.attribute_create", Outcome: "Denied" });
        Assert.True(history.Select(h => h.Seq).SequenceEqual(history.Select(h => h.Seq).OrderDescending())); // newest first
        Assert.Single(AdminEnv.Ok(await env.History.ListAsync(Ada, 50, "attribute_update")));
        // The history is itself covered by the tamper-evident chain.
        await using var conn = new Npgsql.NpgsqlConnection(env.Db.SuperuserConnectionString);
        await conn.OpenAsync();
        Assert.True((await AuditVerifier.VerifyAsync(conn, [])).Valid);
    }

    [Fact]
    public void Diff_lists_created_changed_and_retired_items_and_ignores_what_the_file_leaves_out()
    {
        static ConfigContent Content(params AttributeDoc[] attrs) => new(
            new Dictionary<string, IReadOnlyList<AttributeDoc>> { ["part"] = attrs }, [], new Dictionary<string, IReadOnlyList<RefItemDoc>>(), [], [], [], [],
            new ClassHierarchyDoc(ClassHierarchyDoc.CurrentFormat, []));
        var have = Content(new AttributeDoc("a", "A", "string"), new AttributeDoc("b", "B", "number", Unit: "kg"), new AttributeDoc("left_out", "Left out", "string"));
        var want = Content(new AttributeDoc("a", "A", "string", Retired: true), new AttributeDoc("b", "B", "number", Unit: "g"), new AttributeDoc("c", "C", "boolean"));
        var (changes, unchanged) = ConfigDiff.Compare(have, want);
        Assert.Equal([("a", "retire"), ("b", "change"), ("c", "create")], changes.Select(c => (c.Key, c.Kind)));
        Assert.Contains("unit: \"kg\" → \"g\"", changes.Single(c => c.Key == "b").Detail, StringComparison.Ordinal);
        Assert.Equal(0, unchanged);
        Assert.Empty(ConfigDiff.Compare(have, have).Changes);
        Assert.Equal(3, ConfigDiff.Compare(have, have).Unchanged);
        Assert.Equal(JsonSerializer.Serialize(have, AdminRunner.Json), JsonSerializer.Serialize(have, AdminRunner.Json));
    }
}
