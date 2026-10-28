using System.Text.Json.Nodes;
using Keel.Admin;
using Keel.Audit;
using Keel.Core;
using Keel.Parts;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Attribute definitions, reference lists, numbering and reason codes (ADR-0022): validated, audited with before and after, administrators only.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AdminConfigTests(PostgresFixture pg)
{
    private static readonly Subject Ada = AdminEnv.Ada;

    // ---------------------------------------------------------------- attributes

    [Fact]
    public async Task A_new_attribute_with_range_and_format_validations_is_enforced_by_the_part_service_and_audited()
    {
        var env = await AdminEnv.CreateAsync(pg);
        AdminEnv.Ok(await env.Attributes.CreateAsync(Ada, "part", AdminEnv.Number("diameter", "Diameter", "mm", min: 1, max: 500), "c1"));
        AdminEnv.Ok(await env.Attributes.CreateAsync(Ada, "part", AdminEnv.Text("drawing_ref", "Drawing reference", pattern: "DRW-[0-9]{4}", help: "DRW-nnnn"), "c2"));

        var low = await env.Parts.CreateAsync(AdminEnv.Alice, new CreatePartCommand("Pin", "Component", "EA", "Make", AdminEnv.Itar(), null, new JsonObject { ["diameter"] = 0.5 }), null);
        Assert.Equal(OutcomeKind.Invalid, low.Kind);
        Assert.Contains("Diameter must be at least 1", AdminEnv.Errors(low));
        var badRef = await env.Parts.CreateAsync(AdminEnv.Alice, new CreatePartCommand("Pin", "Component", "EA", "Make", AdminEnv.Itar(), null, new JsonObject { ["drawing_ref"] = "X-1" }), null);
        Assert.Contains("does not match the required format (DRW-nnnn)", AdminEnv.Errors(badRef));
        var ok = await env.Parts.CreateAsync(AdminEnv.Alice, new CreatePartCommand("Pin", "Component", "EA", "Make", AdminEnv.Itar(), null,
            new JsonObject { ["diameter"] = 12.5, ["drawing_ref"] = "DRW-0042" }), null);
        Assert.True(ok.IsOk, AdminEnv.Errors(ok));

        var created = (await env.AuditAsync()).Single(e => e.Action == "admin.attribute_create" && e.ObjectId == "part/diameter");
        Assert.Equal(AuditOutcome.Success, created.Outcome);
        Assert.Contains("\"after\":\"{", created.Payload, StringComparison.Ordinal);
        Assert.Contains("diameter", created.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editing_an_attribute_records_before_and_after_and_the_data_type_of_an_attribute_with_values_cannot_change()
    {
        var env = await AdminEnv.CreateAsync(pg);
        AdminEnv.Ok(await env.Attributes.CreateAsync(Ada, "part", AdminEnv.Number("gauge", "Gauge"), null));
        await env.NewPartAsync(attributes: new JsonObject { ["gauge"] = 18 });

        var relabelled = AdminEnv.Ok(await env.Attributes.UpdateAsync(Ada, "part", AdminEnv.Number("gauge", "Wire gauge", "AWG", 10, 30), "c-edit"));
        Assert.Equal(("Wire gauge", "AWG", 2, 1L), (relabelled.Definition.Label, relabelled.Definition.Unit, relabelled.Definition.Revision, relabelled.Definition.ValuesInUse));

        var typeChange = await env.Attributes.UpdateAsync(Ada, "part", AdminEnv.Text("gauge", "Wire gauge"), null);
        Assert.Equal(OutcomeKind.Invalid, typeChange.Kind);
        Assert.Contains("cannot change", AdminEnv.Errors(typeChange));
        Assert.Contains("Create a new attribute", AdminEnv.Errors(typeChange));

        // Without stored values the type may change.
        AdminEnv.Ok(await env.Attributes.CreateAsync(Ada, "part", AdminEnv.Number("spare", "Spare"), null));
        AdminEnv.Ok(await env.Attributes.UpdateAsync(Ada, "part", AdminEnv.Text("spare", "Spare"), null));

        var edit = (await env.AuditAsync()).Single(e => e.Action == "admin.attribute_update" && e.CorrelationId == "c-edit");
        Assert.Contains("\"before\"", edit.Payload, StringComparison.Ordinal);
        Assert.Contains("\"after\"", edit.Payload, StringComparison.Ordinal);
        var entry = ConfigHistoryService.Parse(edit);
        Assert.Contains(entry.Diff, d => d.StartsWith("label:", StringComparison.Ordinal) && d.Contains("Wire gauge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enum_values_in_use_cannot_be_removed_but_new_ones_can_be_added()
    {
        var env = await AdminEnv.CreateAsync(pg);
        AdminEnv.Ok(await env.Attributes.CreateAsync(Ada, "part", AdminEnv.Enum("grade", "Grade", "A", "B"), null));
        await env.NewPartAsync(attributes: new JsonObject { ["grade"] = "B" });
        AdminEnv.Ok(await env.Attributes.UpdateAsync(Ada, "part", AdminEnv.Enum("grade", "Grade", "A", "B", "C"), null));
        var removeUsed = await env.Attributes.UpdateAsync(Ada, "part", AdminEnv.Enum("grade", "Grade", "A", "C"), null);
        Assert.Equal(OutcomeKind.Invalid, removeUsed.Kind);
        Assert.Contains("B", AdminEnv.Errors(removeUsed));
        AdminEnv.Ok(await env.Attributes.UpdateAsync(Ada, "part", AdminEnv.Enum("grade", "Grade", "B", "C"), null)); // unused A may go
    }

    [Fact]
    public async Task A_retired_attribute_disappears_from_new_input_but_its_values_stay_readable_and_are_carried_forward()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var number = await env.NewPartAsync(attributes: new JsonObject { ["material"] = "6061-T6", ["finish"] = "Anodized" });
        var retired = AdminEnv.Ok(await env.Attributes.SetRetiredAsync(Ada, "part", "finish", true, "Replaced by coating", null));
        Assert.True(retired.Definition.Retired);
        Assert.Contains("remain readable", string.Join(" ", retired.Warnings), StringComparison.Ordinal);

        // The old iteration still shows the value.
        Assert.Equal("Anodized", (await env.PartAsync(number)).Iteration.Attributes["finish"]!.GetValue<string>());
        // A working copy carries it forward unchanged; a retired attribute is never required or re-validated.
        Assert.True((await env.Parts.CheckOutAsync(AdminEnv.Alice, number, null)).IsOk);
        var detail = await env.PartAsync(number);
        var save = await env.Parts.UpdateWorkingCopyAsync(AdminEnv.Alice, number, "edited", detail.Checkout!.Attributes, null);
        Assert.True(save.IsOk, AdminEnv.Errors(save));
        Assert.True((await env.Parts.CheckInAsync(AdminEnv.Alice, number, "x", null)).IsOk);
        Assert.Equal("Anodized", (await env.PartAsync(number)).Iteration.Attributes["finish"]!.GetValue<string>());
        // The metadata API (retired hidden) and the admin list (retired shown on request).
        Assert.DoesNotContain(AdminEnv.Ok(await env.Attributes.ListAsync(Ada, "part", false)), d => d.Name == "finish");
        Assert.Contains(AdminEnv.Ok(await env.Attributes.ListAsync(Ada, "part", true)), d => d.Name == "finish" && d.Retired);
        AdminEnv.Ok(await env.Attributes.SetRetiredAsync(Ada, "part", "finish", false, "Needed again", null));
    }

    [Fact]
    public async Task Attributes_of_documents_and_change_objects_are_not_stored_yet_and_say_which_phase_delivers_them()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var e = await Assert.ThrowsAsync<NotSupportedException>(() => env.Attributes.ListAsync(Ada, "document", false));
        Assert.Contains("Phase 2c", e.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<NotSupportedException>(() => env.Attributes.CreateAsync(Ada, "ecn", AdminEnv.Text("x", "X"), null));
    }

    [Fact]
    public async Task Invalid_definitions_are_rejected_with_messages()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var bad = await env.Attributes.CreateAsync(Ada, "part", new AttributeDefinitionInput("Bad Name", "", "string", Pattern: "(", Min: 1), null);
        Assert.Equal(OutcomeKind.Invalid, bad.Kind);
        var text = AdminEnv.Errors(bad);
        Assert.Contains("name must start", text, StringComparison.Ordinal);
        Assert.Contains("label is required", text, StringComparison.Ordinal);
        Assert.Contains("not a valid regular expression", text, StringComparison.Ordinal);
        Assert.Contains("range applies to number", text, StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Conflict, (await env.Attributes.CreateAsync(Ada, "part", AdminEnv.Text("material", "Material"), null)).Kind);
    }

    // ---------------------------------------------------------------- role gating

    [Fact]
    public async Task Non_administrators_are_refused_everywhere_and_each_denial_is_audited()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var alice = AdminEnv.Alice;
        Assert.Equal(OutcomeKind.Forbidden, (await env.Attributes.CreateAsync(alice, "part", AdminEnv.Text("x", "X"), null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Lifecycles.ListFamiliesAsync(alice)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Lists.SaveAsync(alice, new RefItemInput(RefLists.Unit, "yd", "Yard"), null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Numbering.UpdateAsync(alice, "part", "Q-", 6, "x", null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Classes.CreateNodeAsync(alice, new ClassNodeInput("X", "X", null, null), null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Config.ExportAsync(alice, null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Workflows.ListAsync(alice, true, null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.History.ListAsync(alice, 10, null)).Kind);

        var denials = (await env.AuditAsync()).Where(e => e.Outcome == AuditOutcome.Denied && e.Actor == "sub-alice").Select(e => e.Action).ToList();
        foreach (var action in new[] { "admin.attribute_create", "admin.lifecycle_list", "admin.ref_save", "admin.numbering_update", "admin.class_create", "admin.config_export", "admin.workflow_templates_list", "admin.history_read" })
            Assert.Contains(action, denials);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM cfg.class_node"));
    }

    // ---------------------------------------------------------------- reference lists

    [Fact]
    public async Task A_new_part_type_and_unit_become_available_and_retired_ones_are_refused_for_new_parts_only()
    {
        var env = await AdminEnv.CreateAsync(pg);
        AdminEnv.Ok(await env.Lists.SaveAsync(Ada, new RefItemInput(RefLists.PartType, "Harness", "Wiring harness", 50), null));
        AdminEnv.Ok(await env.Lists.SaveAsync(Ada, new RefItemInput(RefLists.Unit, "yd", "Yard", 200), null));
        env.Refs.Invalidate();
        Assert.Contains("Harness", await env.RefData.CodesAsync(RefLists.PartType));
        var part = await env.Parts.CreateAsync(AdminEnv.Alice, new CreatePartCommand("Main harness", "Harness", "yd", "Make", AdminEnv.Itar()), null);
        Assert.True(part.IsOk, AdminEnv.Errors(part));

        AdminEnv.Ok(await env.Lists.SetActiveAsync(Ada, RefLists.PartType, "Harness", false, "Obsolete type", null));
        var refused = await env.Parts.CreateAsync(AdminEnv.Alice, new CreatePartCommand("Another harness", "Harness", "EA", "Make", AdminEnv.Itar()), null);
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        Assert.Contains("Part type must be one of", AdminEnv.Errors(refused));
        // The existing part keeps its (now retired) type and can still be edited.
        Assert.Equal("Harness", (await env.PartAsync(part.Value!.Number)).PartType);
        Assert.True((await env.Parts.CheckOutAsync(AdminEnv.Alice, part.Value.Number, null)).IsOk);
        env.Refs.Invalidate();
        Assert.DoesNotContain("Harness", await env.RefData.CodesAsync(RefLists.PartType));
        Assert.Contains("Harness", (await env.Refs.ListAsync(RefLists.PartType, includeRetired: true)).Select(i => i.Code));
    }

    [Fact]
    public async Task The_database_refuses_a_retired_type_even_when_the_service_is_bypassed_and_protected_items_cannot_be_retired()
    {
        var env = await AdminEnv.CreateAsync(pg);
        AdminEnv.Ok(await env.Lists.SetActiveAsync(Ada, RefLists.DocumentType, "Report", false, "Not used", null));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => env.Db.ExecuteAsSuperuserAsync(
            "INSERT INTO plm.document_master (id, number, name, document_type, lifecycle_id, export_jurisdiction, cui_categories, distribution_statement, owning_org, export_authorizations, created_at, created_by) " +
            "VALUES (gen_random_uuid(), 'D-X', 'x', 'Report', 'document-default', 'None', '{}', 'A', 'ENG', '{}', now(), 'x')"));
        Assert.Contains("not an active document-type", ex.MessageText, StringComparison.Ordinal);

        Assert.Equal(OutcomeKind.Invalid, (await env.Lists.SetActiveAsync(Ada, RefLists.Unit, "EA", false, "x", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Lists.SetActiveAsync(Ada, RefLists.ChangeClass, "I", false, "x", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Lists.SaveAsync(Ada, new RefItemInput(RefLists.ChangeClass, "III", "Class III"), null)).Kind);
    }

    [Fact]
    public async Task Change_priorities_and_bom_units_follow_the_lists()
    {
        var env = await AdminEnv.CreateAsync(pg);
        AdminEnv.Ok(await env.Lists.SaveAsync(Ada, new RefItemInput(RefLists.ChangePriority, "Urgent", "Urgent", 45), null));
        var ecn = await env.Change.CreateAsync(AdminEnv.Alice, new Keel.Change.CreateChangeCommand("ECN", "Urgent fix", null, "Urgent", "x", AdminEnv.Itar(), "II"), null);
        Assert.True(ecn.IsOk, AdminEnv.Errors(ecn));
        AdminEnv.Ok(await env.Lists.SetActiveAsync(Ada, RefLists.ChangePriority, "Urgent", false, "Too many urgent", null));
        var refused = await env.Change.CreateAsync(AdminEnv.Alice, new Keel.Change.CreateChangeCommand("ECN", "Another", null, "Urgent", "x", AdminEnv.Itar(), "II"), null);
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        // An unchanged retired priority stays valid when the existing change is edited.
        var edited = await env.Change.UpdateAsync(AdminEnv.Alice, ecn.Value!.Number, new Keel.Change.UpdateChangeCommand("Urgent fix 2", null, "Urgent", "x"), null);
        Assert.True(edited.IsOk, AdminEnv.Errors(edited));
    }

    // ---------------------------------------------------------------- numbering

    [Fact]
    public async Task Numbering_prefix_changes_apply_to_new_numbers_only_and_the_counter_never_goes_back()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var first = await env.NewPartAsync();
        Assert.Equal("P-000001", first);

        var list = AdminEnv.Ok(await env.Numbering.ListAsync(Ada));
        Assert.Equal(("P-", 6, 2L, "P-000002"), (list.Single(n => n.ObjectType == "part").Prefix, 6, list.Single(n => n.ObjectType == "part").NextValue, list.Single(n => n.ObjectType == "part").NextNumber));

        var changed = AdminEnv.Ok(await env.Numbering.UpdateAsync(Ada, "part", "PRT-", 7, "New numbering scheme", "c-num"));
        Assert.Equal("PRT-0000002", changed.NextNumber);
        Assert.Equal("PRT-0000002", await env.NewPartAsync());
        Assert.Equal("P-000001", (await env.PartAsync(first)).Number); // existing numbers untouched

        // Back to the old prefix: the sequence moved on, so P-000001 is never issued twice.
        AdminEnv.Ok(await env.Numbering.UpdateAsync(Ada, "part", "P-", 6, "Revert", null));
        Assert.Equal("P-000003", await env.NewPartAsync());

        // Only forward.
        Assert.Equal(OutcomeKind.Invalid, (await env.Numbering.AdvanceAsync(Ada, "part", 2, "back", null)).Kind);
        var advanced = AdminEnv.Ok(await env.Numbering.AdvanceAsync(Ada, "part", 500, "Imported numbers up to 499", null));
        Assert.Equal("P-000500", advanced.NextNumber);
        Assert.Equal("P-000500", await env.NewPartAsync());

        var audit = (await env.AuditAsync()).Single(e => e.CorrelationId == "c-num");
        Assert.Contains("PRT-", audit.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Change_prefixes_are_fixed_and_a_prefix_that_would_collide_with_an_explicit_number_is_refused()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var fixedPrefix = await env.Numbering.UpdateAsync(Ada, "ecn", "CHG-", 6, "x", null);
        Assert.Equal(OutcomeKind.Invalid, fixedPrefix.Kind);
        Assert.Contains("fixed", AdminEnv.Errors(fixedPrefix), StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await env.Numbering.UpdateAsync(Ada, "part", "p-", 6, "x", null)).Kind); // capitals only
        Assert.Equal(OutcomeKind.Invalid, (await env.Numbering.UpdateAsync(Ada, "part", "P-", 3, "x", null)).Kind);

        // An explicit number equal to the next generated one blocks the change until the counter moves past it.
        Assert.True((await env.Parts.CreateAsync(Ada, new CreatePartCommand("Imported", "Component", "EA", "Make", AdminEnv.Open, Number: "X-000001"), null)).IsOk);
        var clash = await env.Numbering.UpdateAsync(Ada, "part", "X-", 6, "x", null);
        Assert.Equal(OutcomeKind.Conflict, clash.Kind);
        AdminEnv.Ok(await env.Numbering.AdvanceAsync(Ada, "part", 5, "skip", null));
        AdminEnv.Ok(await env.Numbering.UpdateAsync(Ada, "part", "X-", 6, "x", null));
    }

    // ---------------------------------------------------------------- reason codes and vocabularies

    [Fact]
    public async Task Signature_meanings_and_change_reasons_are_editable_but_signing_must_stay_possible()
    {
        var env = await AdminEnv.CreateAsync(pg);
        AdminEnv.Ok(await env.Reasons.SaveAsync(Ada, new ReasonCodeDto("signature", null, "Verified", "Verified against drawing", "approve", false, ["review"], 15, true), null));
        AdminEnv.Ok(await env.Reasons.SaveAsync(Ada, new ReasonCodeDto("change", "pr.reject", "TestEscape", "Test escape", null, null, null, 50, true), null));
        // The decision of an existing meaning cannot change (signatures carry it); a rejection always needs a comment.
        Assert.Equal(OutcomeKind.Invalid, (await env.Reasons.SaveAsync(Ada, new ReasonCodeDto("signature", null, "Approved", "Approved", "reject", true, ["approval"], 20, true), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Reasons.SaveAsync(Ada, new ReasonCodeDto("signature", null, "Nope", "Nope", "reject", false, ["review"], 5, true), null)).Kind);
        // Retiring the only approving meaning for approvals is refused; retiring one of two is fine.
        AdminEnv.Ok(await env.Reasons.SetActiveAsync(Ada, "signature", null, "ApprovedWithComments", false, "Not wanted", null));
        var last = await env.Reasons.SetActiveAsync(Ada, "signature", null, "Approved", false, "x", null);
        Assert.Equal(OutcomeKind.Invalid, last.Kind);
        Assert.Contains("approves", AdminEnv.Errors(last), StringComparison.Ordinal);
        // The workflow engine reads the data: the new meaning is offered.
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM wf.reason_code WHERE code = 'Verified' AND is_active"));
    }

    [Fact]
    public async Task Picker_vocabularies_hold_offices_and_programs_without_touching_security_attributes()
    {
        var env = await AdminEnv.CreateAsync(pg);
        AdminEnv.Ok(await env.Lists.SaveAsync(Ada, new RefItemInput(RefLists.Program, "KRAKEN", "KRAKEN (synthetic)", 20), null));
        AdminEnv.Ok(await env.Lists.SaveAsync(Ada, new RefItemInput(RefLists.Office, "BETA", "Beta Systems (synthetic)", 20), null));
        env.Refs.Invalidate();
        Assert.Equal(["ORCA", "KRAKEN"], await env.RefData.CodesAsync(RefLists.Program));
        // A program on the list does not grant anyone access to it: access still comes from the subject's own programs.
        var restricted = await env.Parts.CreateAsync(AdminEnv.Alice, new CreatePartCommand("Kraken part", "Component", "EA", "Make", AdminEnv.Itar("KRAKEN")), null);
        Assert.Equal(OutcomeKind.Forbidden, restricted.Kind);
    }
}
