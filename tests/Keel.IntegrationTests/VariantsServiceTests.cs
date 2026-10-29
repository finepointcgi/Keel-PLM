using System.Text.Json;
using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.Core.Variants;
using Keel.Variants;
using Npgsql;
using static Keel.IntegrationTests.VarEnv;
using Keel.Security;

namespace Keel.IntegrationTests;

/// <summary>Option models, rules, variants and their locks (ADR-0039) over a real database.</summary>
[Collection(PostgresCollection.Name)]
public sealed class VariantsServiceTests(PostgresFixture pg)
{
    [Fact]
    public async Task A_model_is_built_validated_and_audited()
    {
        var env = await VarEnv.CreateAsync(pg);
        var top = await env.C.NewPartAsync("Rocket", "Assembly");
        await env.EngineAndPayloadAsync(top);

        var model = await env.ModelAsync(Alice, top);
        Assert.Equal(["Engine", "Payload"], model.Families.Select(f => f.Key));
        Assert.Equal(["A", "B"], model.Families[0].Values.Select(v => v.Key));
        Assert.Equal(4, model.ValidCombinations);
        Assert.Equal(4, model.RawCombinations);
        Assert.True(model.AnalysisComplete);
        Assert.Empty(model.Findings);

        // key and name validation, duplicates, limits of shape
        Assert.Equal(OutcomeKind.Invalid, (await env.Var.AddFamilyAsync(Alice, top, new NewFamily("has space", "x"), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Var.AddFamilyAsync(Alice, top, new NewFamily("OR", "x"), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Var.AddFamilyAsync(Alice, top, new NewFamily("Color", " "), null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Var.AddFamilyAsync(Alice, top, new NewFamily("Engine", "Again"), null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Var.AddValueAsync(Alice, top, "Engine", new NewValue("A", "Again"), null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Var.AddValueAsync(Alice, top, "Ghost", new NewValue("A", "Again"), null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Var.CreateModelAsync(Alice, top, null)).Kind);

        // only assemblies have options
        var screw = await env.C.NewPartAsync("Screw");
        var notAssembly = await env.Var.CreateModelAsync(Alice, screw, null);
        Assert.Equal(OutcomeKind.Conflict, notAssembly.Kind);
        Assert.Contains("Assembly", string.Join(" ", notAssembly.Errors), StringComparison.Ordinal);

        // a rule must name existing options, and may not make the model inconsistent
        Assert.Equal(OutcomeKind.Invalid, (await env.Var.AddRuleAsync(Alice, top, new NewRule(RuleKind.Requires, new("Engine", "A"), [new("Cooling", "Air")]), null)).Kind);
        var ok = Ok(await env.Var.AddRuleAsync(Alice, top, new NewRule(RuleKind.Excludes, new("Engine", "B"), [new("Payload", "X")], "Electric drives cannot carry the camera"), null));
        Assert.Equal(3, ok.ValidCombinations);
        Assert.Contains("Choosing Engine is Electric rules out Payload is Camera.", ok.Rules.Single().Text, StringComparison.Ordinal);
        var inconsistent = await env.Var.AddRuleAsync(Alice, top, new NewRule(RuleKind.Excludes, new("Engine", "B"), [new("Payload", "Y")]), null);
        Assert.Equal(OutcomeKind.Conflict, inconsistent.Kind);
        Assert.Contains("inconsistent", string.Join(" ", inconsistent.Errors), StringComparison.Ordinal);
        Assert.Single((await env.ModelAsync(Alice, top)).Rules); // the refused rule was not stored

        // checking a configuration
        var bad = Ok(await env.Var.CheckConfigurationAsync(Alice, top, new Dictionary<string, string> { ["Engine"] = "B", ["Payload"] = "X" }, null));
        Assert.False(bad.Valid);
        Assert.Contains("Electric", string.Join(" ", bad.Problems), StringComparison.Ordinal);
        Assert.True(Ok(await env.Var.CheckConfigurationAsync(Alice, top, new Dictionary<string, string> { ["Engine"] = "B", ["Payload"] = "Y" }, null)).Valid);
        Assert.False(Ok(await env.Var.CheckConfigurationAsync(Alice, top, new Dictionary<string, string> { ["Engine"] = "B" }, null)).Valid); // Payload is required

        var audit = await env.C.AuditAsync();
        foreach (var action in new[] { "options.model_create", "options.family_add", "options.value_add", "options.rule_add", "variant.view", "variant.check" })
            Assert.Contains(audit, a => a.Action == action && a.Outcome == AuditOutcome.Success && a.ObjectType == "part" && a.ObjectId == top);
        Assert.Contains(audit, a => a.Action == "options.rule_add" && a.Outcome == AuditOutcome.Failure && a.ObjectId == top);
    }

    [Fact]
    public async Task Variants_are_validated_released_frozen_and_reported_to_the_outbox()
    {
        var env = await VarEnv.CreateAsync(pg);
        var top = await env.C.NewPartAsync("Rocket", "Assembly");
        await env.EngineAndPayloadAsync(top);
        Ok(await env.Var.AddRuleAsync(Alice, top, new NewRule(RuleKind.Excludes, new("Engine", "B"), [new("Payload", "X")]), null));

        Assert.Equal(OutcomeKind.Invalid, (await env.Var.CreateVariantAsync(Alice, top, new NewVariant("Ghost", new Dictionary<string, string> { ["Engine"] = "Z" }), null)).Kind);
        await env.VariantAsync(top, "Alpha", "A", "X");
        Assert.Equal(OutcomeKind.Conflict, (await env.Var.CreateVariantAsync(Alice, top, new NewVariant("alpha", new Dictionary<string, string> { ["Engine"] = "A" }), null)).Kind); // names are unique ignoring case

        // an invalid draft can be saved but not released
        var draft = Ok(await env.Var.CreateVariantAsync(Alice, top, new NewVariant("Bad", new Dictionary<string, string> { ["Engine"] = "B", ["Payload"] = "X" }), null));
        Assert.False(draft.Variants.Single(v => v.Name == "Bad").Valid);
        var refused = await env.Var.ReleaseVariantAsync(Alice, top, "Bad", null);
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        Assert.Empty(env.Sink.Events);

        // fix it and release: the event carries the choices and the product's markings
        Ok(await env.Var.UpdateVariantAsync(Alice, top, "Bad", new VariantChange("Fixed", null, new Dictionary<string, string> { ["Engine"] = "B", ["Payload"] = "Y" }), null));
        var released = Ok(await env.Var.ReleaseVariantAsync(Alice, top, "Fixed", null));
        Assert.Equal("Released", released.Variants.Single(v => v.Name == "Fixed").State);
        var ev = Assert.Single(env.Sink.Events);
        Assert.Equal(EventTypes.VariantReleased, ev.EventType);
        Assert.Equal(top, ev.AggregateNumber);
        Assert.Equal("Fixed", ev.Payload["variant"]!["name"]!.GetValue<string>());
        Assert.Equal(["Engine", "Payload"], ev.Payload["choices"]!.AsArray().Select(c => c!["family"]!.GetValue<string>()));
        Assert.Equal("Electric", ev.Payload["choices"]![0]!["valueName"]!.GetValue<string>());
        var schemaFile = Path.Combine(AppContext.BaseDirectory, "../../../../../docs/integration/schemas/variant.released.v1.json");
        var schema = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(schemaFile))!.AsObject();
        Assert.Empty(Keel.TestSupport.JsonSchemaLite.Validate(System.Text.Json.Nodes.JsonNode.Parse(ev.Payload.ToJsonString())!, schema));

        // released means frozen: no edit, no delete, no rename, and the database agrees
        Assert.Equal(OutcomeKind.Conflict, (await env.Var.UpdateVariantAsync(Alice, top, "Fixed", new VariantChange(Description: "x"), null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Var.DeleteVariantAsync(Alice, top, "Fixed", null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Var.ReleaseVariantAsync(Alice, top, "Fixed", null)).Kind);
        await using (var conn = new NpgsqlConnection(env.C.Db.AppConnectionString))
        {
            await conn.OpenAsync();
            await using var upd = new NpgsqlCommand("UPDATE var.variant_selection SET value_id = value_id WHERE variant_id = (SELECT id FROM var.variant WHERE name = 'Fixed')", conn);
            var e1 = await Assert.ThrowsAsync<PostgresException>(() => upd.ExecuteNonQueryAsync());
            Assert.Contains("released variant", e1.MessageText, StringComparison.Ordinal);
            await using var ren = new NpgsqlCommand("UPDATE var.variant SET name = 'Renamed' WHERE name = 'Fixed'", conn);
            var e2 = await Assert.ThrowsAsync<PostgresException>(() => ren.ExecuteNonQueryAsync());
            Assert.Contains("renamed", e2.MessageText, StringComparison.Ordinal);
            await using var del = new NpgsqlCommand("DELETE FROM var.variant WHERE name = 'Fixed'", conn);
            await Assert.ThrowsAsync<PostgresException>(() => del.ExecuteNonQueryAsync());
        }

        // a change to the model that would break a released variant is refused; unused options can still change
        Assert.Equal(OutcomeKind.Conflict, (await env.Var.UpdateValueAsync(Alice, top, "Engine", "B", new ValueChange(Retired: true), null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Var.DeleteValueAsync(Alice, top, "Payload", "Y", null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Var.DeleteFamilyAsync(Alice, top, "Engine", null)).Kind);
        var addRequired = await env.Var.AddFamilyAsync(Alice, top, new NewFamily("Color", "Color", Required: true), null);
        Assert.Equal(OutcomeKind.Conflict, addRequired.Kind);
        Assert.Contains("released variant", string.Join(" ", addRequired.Errors), StringComparison.Ordinal);
        Ok(await env.Var.AddFamilyAsync(Alice, top, new NewFamily("Color", "Color", Required: false), null));
        var blockingRule = await env.Var.AddRuleAsync(Alice, top, new NewRule(RuleKind.Excludes, new("Engine", "B"), [new("Payload", "Y")]), null);
        Assert.Equal(OutcomeKind.Conflict, blockingRule.Kind);
        Ok(await env.Var.UpdateValueAsync(Alice, top, "Engine", "A", new ValueChange("Turbofan"), null)); // a rename of an unused-by-released value is fine

        // retire, then the model may move on
        Ok(await env.Var.RetireVariantAsync(Alice, top, "Fixed", null));
        Ok(await env.Var.UpdateValueAsync(Alice, top, "Engine", "B", new ValueChange(Retired: true), null));
        Ok(await env.Var.DeleteVariantAsync(Alice, top, "Alpha", null));

        var audit = await env.C.AuditAsync();
        Assert.Contains(audit, a => a.Action == "variant.release" && a.Outcome == AuditOutcome.Success);
        Assert.Contains(audit, a => a.Action == "variant.release" && a.Outcome == AuditOutcome.Failure);
        Assert.Contains(audit, a => a.Action == "options.value_update" && a.Outcome == AuditOutcome.Failure);
    }

    [Fact]
    public async Task Option_changes_need_modify_rights_and_hide_products_the_caller_cannot_read()
    {
        var env = await VarEnv.CreateAsync(pg);
        var open = await env.C.NewPartAsync("Open rocket", "Assembly");
        var secret = await env.C.NewPartAsync("Secret rocket", "Assembly", ConfigEnv.Itar());
        await env.EngineAndPayloadAsync(open);
        await env.EngineAndPayloadAsync(secret);
        await env.VariantAsync(secret, "Covert", "A", "X");

        // a viewer reads but does not change
        var viewer = WorkflowEnv.Mallory;
        Assert.True((await env.Var.GetModelAsync(viewer, open, null)).IsOk);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Var.AddFamilyAsync(viewer, open, new NewFamily("Color", "Color"), null)).Kind);

        // someone without the program gets the answer for a missing product, for every kind of call
        string[] hidden = [secret, "P-999999"];
        var answers = new List<OutcomeKind>();
        foreach (var number in hidden)
        {
            answers.Add((await env.Var.GetModelAsync(Eve, number, null)).Kind);
            answers.Add((await env.Var.AddFamilyAsync(Eve, number, new NewFamily("Color", "Color"), null)).Kind);
            answers.Add((await env.Var.CreateVariantAsync(Eve, number, new NewVariant("x", new Dictionary<string, string>()), null)).Kind);
            answers.Add((await env.Var.ResolveAsync(Eve, number, "Covert", null)).Kind);
            answers.Add((await env.Var.CheckConfigurationAsync(Eve, number, new Dictionary<string, string>(), null)).Kind);
            Assert.False((await env.Var.PreviewConditionAsync(Eve, number, "Engine = A")).Valid);
        }
        Assert.All(answers, k => Assert.Equal(OutcomeKind.NotFound, k));
        var errors = (await env.Var.PreviewConditionAsync(Eve, secret, "Engine = A")).Errors;
        var missing = (await env.Var.PreviewConditionAsync(Eve, "P-999999", "Engine = A")).Errors;
        Assert.Equal(missing.Select(e => e.Replace("P-999999", "X", StringComparison.Ordinal)), errors.Select(e => e.Replace(secret, "X", StringComparison.Ordinal)));

        // the denials are audited, once per call, and name no option
        var audit = await env.C.AuditAsync();
        Assert.Contains(audit, a => a.Outcome == AuditOutcome.Denied && a.Action == "variant.view" && a.ObjectId == secret);
        Assert.Contains(audit, a => a.Outcome == AuditOutcome.Denied && a.Action == "options.family_add" && a.ObjectId == secret);
        Assert.DoesNotContain("Covert", JsonSerializer.Serialize(audit.Where(a => a.Outcome == AuditOutcome.Denied)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_product_list_equals_the_access_policy_for_every_subject_including_counts()
    {
        var env = await VarEnv.CreateAsync(pg);
        var open = await env.C.NewPartAsync("Open rocket", "Assembly");
        var itar = await env.C.NewPartAsync("Itar rocket", "Assembly", ConfigEnv.Itar());
        var ear = await env.C.NewPartAsync("Ear rocket", "Assembly", BomTestKit.Ear());
        var opal = WorkflowEnv.User("opal", [Roles.Viewer, Roles.Author], programs: ["OTHER"]);
        var third = await env.C.NewPartAsync("Third rocket", "Assembly", ConfigEnv.Itar("OTHER"), opal);
        var nothing = await env.C.NewPartAsync("No model", "Assembly");
        var all = new Dictionary<string, Markings>();
        foreach (var (n, m) in new[] { (open, ConfigEnv.Open), (itar, ConfigEnv.Itar()), (ear, BomTestKit.Ear()), (third, ConfigEnv.Itar("OTHER")) })
        {
            var creator = n == third ? opal : Alice;
            await env.EngineAndPayloadAsync(n, creator);
            var variants = n == open ? 3 : 1;
            for (var i = 0; i < variants; i++) await env.VariantAsync(n, $"V{i}", "A", i % 2 == 0 ? "X" : "Y", release: i == 0, by: creator);
            all[n] = m;
        }
        _ = nothing;
        foreach (var who in new[] { Alice, Eve, WorkflowEnv.Lena, WorkflowEnv.Farid, Ada, WorkflowEnv.Audrey, WorkflowEnv.Mallory })
        {
            var list = await env.Var.ListProductsAsync(who, null, null, null, 100);
            var expected = all.Where(kv => Keel.Security.AccessPolicy.Evaluate(who, kv.Value, Keel.Security.PlmAction.Read).Permitted).Select(kv => kv.Key).Order().ToList();
            Assert.Equal(expected, list.Rows.Select(r => r.Number).Order().ToList());
            foreach (var row in list.Rows) Assert.Equal(row.Number == open ? 3 : 1, row.Variants); // counts only of readable products
            var json = JsonSerializer.Serialize(list);
            foreach (var hiddenNumber in all.Keys.Except(expected)) Assert.DoesNotContain(hiddenNumber, json, StringComparison.Ordinal);
        }
        // limiting to numbers still goes through the filter
        var viaNumbers = await env.Var.ListProductsAsync(Eve, null, [itar, open], null, 100);
        Assert.Equal([open], viaNumbers.Rows.Select(r => r.Number));
    }
}
