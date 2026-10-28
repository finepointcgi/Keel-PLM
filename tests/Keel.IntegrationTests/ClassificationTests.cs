using System.Text.Json.Nodes;
using Keel.Admin;
using Keel.Audit;
using Keel.Core;
using Keel.Parts;

namespace Keel.IntegrationTests;

/// <summary>The classification tree, inherited attribute sets, safe move and retire, and classification as iteration-owned part data (ADR-0022).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ClassificationTests(PostgresFixture pg)
{
    private static readonly Subject Ada = AdminEnv.Ada;
    private static readonly Subject Alice = AdminEnv.Alice;

    /// <summary>MECH (material grade) > FAST (thread size, diameter) > BOLT (length); MECH > BRACKET; ELEC.</summary>
    private static async Task BuildTreeAsync(AdminEnv env)
    {
        var c = env.Classes;
        AdminEnv.Ok(await c.CreateNodeAsync(Ada, new ClassNodeInput("MECH", "Mechanical", "Mechanical parts", null, 10), null));
        AdminEnv.Ok(await c.CreateNodeAsync(Ada, new ClassNodeInput("FAST", "Fasteners", null, "MECH", 10), null));
        AdminEnv.Ok(await c.CreateNodeAsync(Ada, new ClassNodeInput("BOLT", "Bolts", null, "FAST", 10), null));
        AdminEnv.Ok(await c.CreateNodeAsync(Ada, new ClassNodeInput("BRACKET", "Brackets", null, "MECH", 20), null));
        AdminEnv.Ok(await c.CreateNodeAsync(Ada, new ClassNodeInput("ELEC", "Electrical", null, null, 20), null));
        AdminEnv.Ok(await c.SaveAttributeAsync(Ada, "MECH", AdminEnv.Enum("grade", "Material grade", "304", "316", "6061"), true, null));
        AdminEnv.Ok(await c.SaveAttributeAsync(Ada, "FAST", new AttributeDefinitionInput("thread", "Thread size", "string", Pattern: "M[0-9]{1,2}x[0-9.]+"), true, null));
        AdminEnv.Ok(await c.SaveAttributeAsync(Ada, "FAST", AdminEnv.Number("diameter", "Diameter", "mm", 1, 100), true, null));
        AdminEnv.Ok(await c.SaveAttributeAsync(Ada, "BOLT", AdminEnv.Number("length", "Length", "mm", 1, 500, required: true), true, null));
    }

    [Fact]
    public async Task Children_inherit_the_attributes_of_every_ancestor_and_the_effective_set_lists_ancestors_first()
    {
        var env = await AdminEnv.CreateAsync(pg);
        await BuildTreeAsync(env);
        var bolt = (await env.Classes.GetAsync("BOLT"))!;
        Assert.Equal(["MECH", "FAST"], bolt.Ancestors.Select(a => a.Code));
        Assert.Equal([("grade", "MECH"), ("diameter", "FAST"), ("thread", "FAST"), ("length", "BOLT")], bolt.Effective.Select(e => (e.Definition.Name, e.DefinedAt)));
        Assert.Equal(["length"], bolt.OwnAttributes.Select(a => a.Name));
        Assert.Equal(["grade"], (await env.Classes.GetAsync("BRACKET"))!.Effective.Select(e => e.Definition.Name));
        Assert.Empty((await env.Classes.GetAsync("ELEC"))!.Effective);
        var tree = await env.Classes.TreeAsync();
        Assert.Equal("/MECH/FAST/BOLT/", tree.ByCode("bolt")!.Path); // codes ignore letter case
        Assert.Equal(3, tree.ByCode("BOLT")!.Depth);

        // An attribute name is unique along a path: not on a descendant, not on an ancestor.
        Assert.Equal(OutcomeKind.Conflict, (await env.Classes.SaveAttributeAsync(Ada, "BOLT", AdminEnv.Number("diameter", "Bolt diameter"), true, null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Classes.SaveAttributeAsync(Ada, "MECH", AdminEnv.Number("length", "Length"), true, null)).Kind);
        AdminEnv.Ok(await env.Classes.SaveAttributeAsync(Ada, "ELEC", AdminEnv.Number("length", "Length"), true, null)); // another branch may reuse it
        Assert.Equal(OutcomeKind.Conflict, (await env.Classes.CreateNodeAsync(Ada, new ClassNodeInput("bolt", "Duplicate", null, null), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Classes.CreateNodeAsync(Ada, new ClassNodeInput("bad code", "Bad", null, null), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Classes.CreateNodeAsync(Ada, new ClassNodeInput("X", "X", null, "NOPE"), null)).Kind);

        var audit = (await env.AuditAsync()).Where(e => e.Action == "admin.class_attribute_create").ToList();
        Assert.True(audit.Count >= 5);
        Assert.All(audit, e => Assert.Contains("\"after\"", e.Payload, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_part_is_classified_in_its_working_copy_validated_against_the_effective_set_and_frozen_at_check_in()
    {
        var env = await AdminEnv.CreateAsync(pg);
        await BuildTreeAsync(env);
        var number = await env.NewPartAsync("Hex bolt M8");

        // Not checked out: refused. Checked out: validated against grade (MECH), thread and diameter (FAST) and the required length (BOLT).
        var early = await env.Classes.SetForPartAsync(Alice, number, "BOLT", new JsonObject { ["length"] = 30 }, null);
        Assert.Equal(OutcomeKind.Conflict, early.Kind);
        Assert.True((await env.Parts.CheckOutAsync(Alice, number, null)).IsOk);
        var invalid = await env.Classes.SetForPartAsync(Alice, number, "BOLT", new JsonObject { ["grade"] = "9999", ["thread"] = "M8", ["diameter"] = 200 }, null);
        Assert.Equal(OutcomeKind.Invalid, invalid.Kind);
        var text = AdminEnv.Errors(invalid);
        Assert.Contains("Material grade must be one of", text, StringComparison.Ordinal);
        Assert.Contains("Thread size does not match", text, StringComparison.Ordinal);
        Assert.Contains("Diameter must be at most 100", text, StringComparison.Ordinal);
        Assert.Contains("Length is required", text, StringComparison.Ordinal);

        var set = AdminEnv.Ok(await env.Classes.SetForPartAsync(Alice, number, "BOLT",
            new JsonObject { ["grade"] = "316", ["thread"] = "M8x1.25", ["diameter"] = 8, ["length"] = 30 }, "c-set"));
        Assert.Equal(("BOLT", true, "A.1"), (set.ClassCode, set.IsWorkingCopy, set.IterationLabel));
        Assert.Equal(["Mechanical", "Fasteners", "Bolts"], set.PathNames);

        // Others (and the frozen iteration) see nothing until check-in.
        var bob = AdminEnv.Bob;
        Assert.Null(AdminEnv.Ok(await env.Classes.GetForPartAsync(bob, number, null)).ClassCode);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM cfg.part_classification"));
        Assert.Equal("BOLT", AdminEnv.Ok(await env.Classes.GetForPartAsync(Alice, number, null)).ClassCode); // the owner sees the working copy

        Assert.True((await env.Parts.CheckInAsync(Alice, number, "classified", null)).IsOk);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM cfg.part_checkout_classification"));
        var frozen = AdminEnv.Ok(await env.Classes.GetForPartAsync(bob, number, null));
        Assert.Equal(("BOLT", "A.2", false), (frozen.ClassCode, frozen.IterationLabel, frozen.IsWorkingCopy));
        Assert.Equal(8m, frozen.Attributes["diameter"]!.GetValue<decimal>());
        Assert.Equal(4, await env.ScalarAsync("SELECT count(*) FROM cfg.part_class_value"));
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM cfg.part_class_value WHERE attr = 'diameter' AND num = 8"));
        Assert.Equal("class:BOLT;attributes:{\"grade\": \"316\", \"length\": 30, \"thread\": \"M8x1.25\", \"diameter\": 8}",
            await env.TextAsync($"SELECT 'class:' || pc.class_code || ';attributes:' || pc.attributes::text FROM cfg.part_classification pc JOIN plm.part_iteration i ON i.id = pc.iteration_id WHERE i.iteration = 2"));

        // Each iteration owns its own classification: a later change creates a new one, the old stays.
        Assert.True((await env.Parts.CheckOutAsync(Alice, number, null)).IsOk);
        Assert.Equal("BOLT", AdminEnv.Ok(await env.Classes.GetForPartAsync(Alice, number, null)).ClassCode); // copied into the working copy at check-out
        Assert.Null(AdminEnv.Ok(await env.Classes.SetForPartAsync(Alice, number, null, null, null)).ClassCode);
        Assert.True((await env.Parts.CheckInAsync(Alice, number, "declassified", null)).IsOk);
        Assert.Null(AdminEnv.Ok(await env.Classes.GetForPartAsync(bob, number, null)).ClassCode);
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM cfg.part_classification")); // A.2 still has it

        var audits = (await env.AuditAsync()).Where(e => e.Action == "part.classify" && e.ObjectId == number).ToList();
        Assert.Equal(2, audits.Count(e => e.Outcome == AuditOutcome.Success));
        Assert.Contains("BOLT", audits.First(e => e.CorrelationId == "c-set").Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Undoing_the_check_out_discards_the_working_classification_and_access_rules_apply_to_classification_too()
    {
        var env = await AdminEnv.CreateAsync(pg);
        await BuildTreeAsync(env);
        var number = await env.NewPartAsync("Bracket", AdminEnv.Itar());
        Assert.True((await env.Parts.CheckOutAsync(Alice, number, null)).IsOk);
        AdminEnv.Ok(await env.Classes.SetForPartAsync(Alice, number, "BRACKET", new JsonObject { ["grade"] = "6061" }, null));
        Assert.True((await env.Parts.UndoCheckOutAsync(Alice, number, null)).IsOk);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM cfg.part_checkout_classification"));
        Assert.Null(AdminEnv.Ok(await env.Classes.GetForPartAsync(Alice, number, null)).ClassCode);

        // Eve is not on program ORCA: the part does not exist for her, and the attempt is audited.
        var hidden = await env.Classes.GetForPartAsync(AdminEnv.Eve, number, null);
        Assert.Equal(OutcomeKind.NotFound, hidden.Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Classes.SetForPartAsync(AdminEnv.Eve, number, "BRACKET", null, null)).Kind);
        // A viewer may read but not classify.
        Assert.Equal(OutcomeKind.Forbidden, (await env.Classes.SetForPartAsync(AdminEnv.Mallory, number, "BRACKET", null, null)).Kind);
        var denied = (await env.AuditAsync()).Where(e => e.Outcome == AuditOutcome.Denied && e.ObjectId == number).Select(e => e.Action).ToList();
        Assert.Contains("part.classification_read", denied);
        Assert.Contains("part.classify", denied);
        // Unknown and retired classes are refused.
        Assert.True((await env.Parts.CheckOutAsync(Alice, number, null)).IsOk);
        Assert.Equal(OutcomeKind.Invalid, (await env.Classes.SetForPartAsync(Alice, number, "NOPE", null, null)).Kind);
    }

    [Fact]
    public async Task A_class_retired_during_the_check_out_cannot_be_frozen_into_a_new_iteration()
    {
        var env = await AdminEnv.CreateAsync(pg);
        await BuildTreeAsync(env);
        var number = await env.NewPartAsync();
        Assert.True((await env.Parts.CheckOutAsync(Alice, number, null)).IsOk);
        AdminEnv.Ok(await env.Classes.SetForPartAsync(Alice, number, "ELEC", null, null));
        // The working copy blocks the retirement: classified parts must be moved first.
        var blocked = await env.Classes.SetStatusAsync(Ada, "ELEC", false, "Not used", null);
        Assert.Equal(OutcomeKind.Invalid, blocked.Kind);
        Assert.Contains("1 part(s) are classified in ELEC", AdminEnv.Errors(blocked), StringComparison.Ordinal);
        Assert.Contains(number, AdminEnv.Errors(blocked), StringComparison.Ordinal);
        // Reclassify, check in, and the retirement goes through; the new class cannot be assigned any more.
        AdminEnv.Ok(await env.Classes.SetForPartAsync(Alice, number, null, null, null));
        AdminEnv.Ok(await env.Classes.SetStatusAsync(Ada, "ELEC", false, "Not used", "c-ret"));
        Assert.Equal(OutcomeKind.Invalid, (await env.Classes.SetForPartAsync(Alice, number, "ELEC", null, null)).Kind);
        Assert.True((await env.Parts.CheckInAsync(Alice, number, "x", null)).IsOk);
        Assert.Equal(OutcomeKind.Conflict, (await env.Classes.SetStatusAsync(Ada, "ELEC", false, "again", null)).Kind);
        AdminEnv.Ok(await env.Classes.SetStatusAsync(Ada, "ELEC", true, "Needed again", null));
        var entry = ConfigHistoryService.Parse((await env.AuditAsync()).Single(e => e.CorrelationId == "c-ret"));
        Assert.Contains("\"status\":\"retired\"", entry.After!, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"active\"", entry.Before!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Moving_a_class_checks_cycles_depth_name_clashes_and_the_classified_parts_against_the_new_effective_set()
    {
        var env = await AdminEnv.CreateAsync(pg);
        await BuildTreeAsync(env);
        // Structural refusals.
        Assert.Equal(OutcomeKind.Invalid, (await env.Classes.MoveNodeAsync(Ada, "FAST", "BOLT", "cycle", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Classes.MoveNodeAsync(Ada, "FAST", "FAST", "self", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Classes.MoveNodeAsync(Ada, "FAST", "ELEC", " ", null)).Kind); // reason required
        // A name clash with the new ancestors: ELEC defines length, BOLT (below) defines length too.
        AdminEnv.Ok(await env.Classes.SaveAttributeAsync(Ada, "ELEC", AdminEnv.Number("length", "Cable length"), true, null));
        var clash = await env.Classes.MoveNodeAsync(Ada, "FAST", "ELEC", "regroup", null);
        Assert.Equal(OutcomeKind.Invalid, clash.Kind);
        Assert.Contains("'length'", AdminEnv.Errors(clash), StringComparison.Ordinal);

        // Depth limit: a chain of twelve levels cannot get a thirteenth.
        var parent = (string?)null;
        for (var i = 1; i <= 12; i++)
        {
            AdminEnv.Ok(await env.Classes.CreateNodeAsync(Ada, new ClassNodeInput($"L{i}", $"Level {i}", null, parent), null));
            parent = $"L{i}";
        }
        Assert.Equal(OutcomeKind.Invalid, (await env.Classes.CreateNodeAsync(Ada, new ClassNodeInput("L13", "Level 13", null, "L12"), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Classes.MoveNodeAsync(Ada, "FAST", "L12", "too deep", null)).Kind);
    }

    [Fact]
    public async Task A_move_that_would_strand_classified_parts_is_refused_with_counts_and_only_visible_examples()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var c = env.Classes;
        AdminEnv.Ok(await c.CreateNodeAsync(Ada, new ClassNodeInput("MECH", "Mechanical", null, null), null));
        AdminEnv.Ok(await c.CreateNodeAsync(Ada, new ClassNodeInput("FAST", "Fasteners", null, "MECH"), null));
        AdminEnv.Ok(await c.CreateNodeAsync(Ada, new ClassNodeInput("GEN", "General", null, null), null));
        AdminEnv.Ok(await c.SaveAttributeAsync(Ada, "MECH", AdminEnv.Enum("grade", "Material grade", "304", "316"), true, null));
        AdminEnv.Ok(await c.SaveAttributeAsync(Ada, "GEN", AdminEnv.Text("lot", "Lot code"), true, null));
        var kim = WorkflowEnv.User("kim", [Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]);
        var open = await env.NewPartAsync("Open part", AdminEnv.Open);
        var secret = await env.NewPartAsync("Secret part", AdminEnv.Itar("KRAKEN"), kim);
        await env.ClassifyAsync(open, "FAST", new JsonObject { ["grade"] = "304" });
        await env.ClassifyAsync(secret, "FAST", new JsonObject { ["grade"] = "316" }, kim);

        // GEN has no "grade": the stored values would be unknown attributes of the new effective set.
        var opsNoProgram = WorkflowEnv.User("ops", [Roles.Admin, Roles.Viewer], programs: []);
        var move = await c.MoveNodeAsync(opsNoProgram, "FAST", "GEN", "Regroup under general", null);
        Assert.Equal(OutcomeKind.Invalid, move.Kind);
        var text = AdminEnv.Errors(move);
        Assert.Contains("2 classified part(s)", text, StringComparison.Ordinal);                  // the count is exact
        Assert.Contains("Unknown attribute 'grade'", text, StringComparison.Ordinal);
        Assert.Contains(open, text, StringComparison.Ordinal);                                    // visible example
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);                            // hidden part never named
        Assert.Equal("/MECH/FAST/", await env.TextAsync("SELECT path FROM cfg.class_node WHERE code = 'FAST'"));

        // Moving within the same attribute context is fine; paths of the subtree follow.
        AdminEnv.Ok(await c.CreateNodeAsync(Ada, new ClassNodeInput("MECH2", "Mechanical 2", null, "MECH"), null));
        AdminEnv.Ok(await c.CreateNodeAsync(Ada, new ClassNodeInput("SCREW", "Screws", null, "FAST"), null));
        var moved = AdminEnv.Ok(await c.MoveNodeAsync(Ada, "FAST", "MECH2", "Regroup", "c-move"));
        Assert.Equal(("/MECH/MECH2/FAST/", 3), (moved.Path, moved.Depth));
        Assert.Equal("/MECH/MECH2/FAST/SCREW/", await env.TextAsync("SELECT path FROM cfg.class_node WHERE code = 'SCREW'"));
        var entry = ConfigHistoryService.Parse((await env.AuditAsync()).Single(e => e.CorrelationId == "c-move"));
        Assert.Contains("/MECH/FAST/", entry.Before!, StringComparison.Ordinal);
        Assert.Contains("/MECH/MECH2/FAST/", entry.After!, StringComparison.Ordinal);
        Assert.Equal("Regroup", entry.Reason);
        // The classified parts follow their class: still valid, still found under the new parent.
        Assert.Equal("FAST", AdminEnv.Ok(await c.GetForPartAsync(Alice, open, null)).ClassCode);
    }

    [Fact]
    public async Task Tightening_a_class_attribute_is_refused_while_classified_parts_would_become_invalid()
    {
        var env = await AdminEnv.CreateAsync(pg);
        await BuildTreeAsync(env);
        var number = await env.NewPartAsync("Bolt");
        await env.ClassifyAsync(number, "BOLT", new JsonObject { ["length"] = 450, ["diameter"] = 10 });

        // A new required attribute that the part lacks.
        var required = await env.Classes.SaveAttributeAsync(Ada, "FAST", new AttributeDefinitionInput("coating", "Coating", "string", Required: true), true, null);
        Assert.Equal(OutcomeKind.Invalid, required.Kind);
        Assert.Contains("Coating is required", AdminEnv.Errors(required), StringComparison.Ordinal);
        // A lower maximum than the stored value.
        var tighter = await env.Classes.SaveAttributeAsync(Ada, "BOLT", AdminEnv.Number("length", "Length", "mm", 1, 400, required: true), false, null);
        Assert.Equal(OutcomeKind.Invalid, tighter.Kind);
        // A type change with values stored.
        var typeChange = await env.Classes.SaveAttributeAsync(Ada, "BOLT", AdminEnv.Text("length", "Length"), false, null);
        Assert.Contains("cannot change", AdminEnv.Errors(typeChange), StringComparison.Ordinal);
        // Loosening and optional additions are fine, and retiring an attribute keeps the stored value readable.
        AdminEnv.Ok(await env.Classes.SaveAttributeAsync(Ada, "BOLT", AdminEnv.Number("length", "Length", "mm", 1, 600, required: true), false, null));
        AdminEnv.Ok(await env.Classes.SaveAttributeAsync(Ada, "FAST", new AttributeDefinitionInput("coating", "Coating", "string"), true, null));
        AdminEnv.Ok(await env.Classes.SetAttributeRetiredAsync(Ada, "FAST", "diameter", true, "Replaced", null));
        Assert.Equal(10m, AdminEnv.Ok(await env.Classes.GetForPartAsync(Alice, number, null)).Attributes["diameter"]!.GetValue<decimal>());
    }
}
