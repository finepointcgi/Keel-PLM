using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>The MBOM as its own versioned object: creation, working copy, check-in freezing, history, revision, markings and the database guards (ADR-0024).</summary>
[Collection(PostgresCollection.Name)]
public sealed class MbomLifecycleTests(PostgresFixture pg)
{
    [Fact]
    public async Task A_manual_mbom_starts_empty_in_work_with_its_own_number_and_revision_history()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var p = await env.ProductAsync();
        var created = await env.Mbom.CreateAsync(MbomEnv.Alice, new CreateMbomCommand(p.Top, MbomEnv.Plant, null, "Manual MBOM"), "c");
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        var d = created.Value!;
        Assert.Equal("MB-000001", d.Summary.Number);
        Assert.Equal((p.Top, MbomEnv.Plant, "A", 1, "InWork"), (d.Summary.PartNumber, d.Summary.Plant, d.Summary.Revision, d.Summary.Iteration, d.Summary.State));
        Assert.Null(d.Summary.DerivedFrom);
        Assert.Equal("Manual MBOM", d.Description);
        Assert.Single(d.Iterations);
        Assert.Empty((await env.LinesAsync(d.Summary.Number)).Lines);

        // One MBOM per part and plant; another plant is another object.
        var again = await env.Mbom.CreateAsync(MbomEnv.Alice, new CreateMbomCommand(p.Top, MbomEnv.Plant), "c");
        Assert.Equal(OutcomeKind.Conflict, again.Kind);
        Assert.Contains("MB-000001", again.Errors[0], StringComparison.Ordinal);
        var other = await env.Mbom.CreateAsync(MbomEnv.Alice, new CreateMbomCommand(p.Top, "PLANT-B"), "c");
        Assert.True(other.IsOk);
        Assert.Equal("MB-000002", other.Value!.Summary.Number);

        // Only Assembly parts; only active plants.
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.CreateAsync(MbomEnv.Alice, new CreateMbomCommand(p.Screw, MbomEnv.Plant), "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.CreateAsync(MbomEnv.Alice, new CreateMbomCommand(p.Sub, "NOWHERE"), "c")).Kind);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "mbom.create" && e.Outcome == AuditOutcome.Success && e.ObjectId == "MB-000001");
    }

    [Fact]
    public async Task Editing_needs_the_check_out_and_check_in_freezes_a_new_iteration()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var p = await env.ProductAsync();
        var number = (await env.Mbom.CreateAsync(MbomEnv.Alice, new CreateMbomCommand(p.Top, MbomEnv.Plant), "c")).Value!.Summary.Number;

        var early = await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(p.Screw, 4), "c");
        Assert.Equal(OutcomeKind.Conflict, early.Kind);
        Assert.Equal("Check the MBOM out before editing its lines.", early.Errors[0]);

        await env.CheckOutAsync(number);
        var added = await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(p.Screw, 4, ScrapFactor: 0.05m, OperationReference: "OP-10", ReferenceDesignators: ["S1", "S2"]), "c");
        Assert.True(added.IsOk, string.Join("; ", added.Errors));
        var line = added.Value!;
        Assert.Equal((10, "Buy", 0.05m, "OP-10", false), (line.FindNumber, line.SupplyType, line.ScrapFactor, line.OperationReference, line.Phantom));
        Assert.Equal("EA", line.Unit);

        // The working copy is private; the checked-in iteration is still empty.
        Assert.Equal(OutcomeKind.Conflict, (await env.Mbom.GetWorkingAsync(MbomEnv.Eve, number, null)).Kind);
        Assert.Empty((await env.LinesAsync(number)).Lines);
        Assert.Equal(OutcomeKind.Conflict, (await env.Mbom.CheckInAsync(ConfigEnv.Rita, number, null, "c")).Kind);

        await env.CheckInAsync(number, comment: "first lines");
        var d = await env.GetAsync(number);
        Assert.Equal(("A", 2), (d.Summary.Revision, d.Summary.Iteration));
        Assert.Equal([2, 1], d.Iterations.Select(i => i.Iteration).ToArray());
        Assert.Equal("first lines", d.Iterations[0].Comment);
        Assert.Equal((1, 0), (d.Iterations[0].LineCount, d.Iterations[0].LinkCount));
        Assert.Single((await env.LinesAsync(number)).Lines);
        Assert.Empty((await env.LinesAsync(number, iteration: 1)).Lines);
        Assert.NotEqual(d.Iterations[0].ContentSha256, d.Iterations[1].ContentSha256);

        // A second check-out starts from the latest iteration; undo discards the working copy.
        await env.CheckOutAsync(number);
        Assert.Single((await env.Mbom.GetWorkingAsync(MbomEnv.Alice, number, null)).Value!.Lines);
        var removed = await env.Mbom.RemoveLineAsync(MbomEnv.Alice, number, (await env.WorkingLineAsync(number, 10)).Id, "c");
        Assert.Equal(0, removed.Value!.Remaining);
        Assert.True((await env.Mbom.UndoCheckOutAsync(MbomEnv.Alice, number, "c")).IsOk);
        Assert.Single((await env.LinesAsync(number)).Lines);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM mbom.working_usage"));
    }

    [Fact]
    public async Task Line_edits_are_validated_and_alternates_need_effectivity()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var p = await env.ProductAsync();
        await env.C.NewContextAsync("MBOM-CTX");
        var number = (await env.Mbom.CreateAsync(MbomEnv.Alice, new CreateMbomCommand(p.Top, MbomEnv.Plant), "c")).Value!.Summary.Number;
        await env.CheckOutAsync(number);
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(p.Screw, 4, ScrapFactor: -1m), "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(p.Screw, 4, SupplyType: "Scavenge"), "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(p.Top, 1), "c")).Kind); // its own part
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand("P-999999", 1), "c")).Kind);

        Assert.True((await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(p.Screw, 4, FindNumber: 10, Effectivity: ConfigEnv.Serial("MBOM-CTX", 1, 50)), "c")).IsOk);
        var overlap = await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(p.Plate, 1, FindNumber: 10, Effectivity: ConfigEnv.Serial("MBOM-CTX", 40, 80)), "c");
        Assert.Equal(OutcomeKind.Conflict, overlap.Kind);
        var alt = await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(p.Plate, 1, FindNumber: 10, Effectivity: ConfigEnv.Serial("MBOM-CTX", 51, null)), "c");
        Assert.True(alt.IsOk, string.Join("; ", alt.Errors));
        await env.CheckInAsync(number);
        var lines = (await env.LinesAsync(number)).Lines;
        Assert.Equal([(10, 0), (10, 1)], lines.Select(l => (l.FindNumber, l.Alt)).ToArray());
        Assert.Equal("serial 51 and up (MBOM-CTX)", lines[1].EffectivityText);
    }

    [Fact]
    public async Task A_released_mbom_is_revised_independently_of_the_design_revision()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (p, number) = await env.ReconciledProductAsync();
        Assert.Equal(OutcomeKind.Conflict, (await env.Mbom.ReviseAsync(MbomEnv.Alice, number, "too early", "c")).Kind);
        await env.ReleaseAsync(number);
        var released = await env.GetAsync(number);
        Assert.Equal(("A", "Released"), (released.Summary.Revision, released.Summary.State));
        Assert.Equal(OutcomeKind.Conflict, (await env.Mbom.CheckOutAsync(MbomEnv.Alice, number, "c")).Kind); // locked
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.ReviseAsync(MbomEnv.Alice, number, " ", "c")).Kind);

        var revised = await env.Mbom.ReviseAsync(MbomEnv.Alice, number, "Add a drying step", "c");
        Assert.True(revised.IsOk, string.Join("; ", revised.Errors));
        Assert.Equal(("B", 1, "InWork"), (revised.Value!.Summary.Revision, revised.Value.Summary.Iteration, revised.Value.Summary.State));
        Assert.Equal(["B", "A"], revised.Value.Revisions.Select(r => r.Revision).ToArray());
        // The part itself did not change: revision A of the part is still the latest.
        Assert.Equal("A", (await env.C.PartAsync(p.Top)).Revision.Revision);
        var oldLines = await env.LinesAsync(number, revision: "A");
        var newLines = await env.LinesAsync(number, revision: "B");
        Assert.Equal(oldLines.Lines.Count, newLines.Lines.Count);
        Assert.All(newLines.Lines, l => Assert.NotEmpty(l.Links));
        Assert.Equal(OutcomeKind.Conflict, (await env.Mbom.ReviseAsync(MbomEnv.Alice, number, "again", "c")).Kind); // B is In Work

        // Releasing B supersedes A.
        await env.ReconcileAsync(number);
        await env.ReleaseAsync(number);
        var states = (await env.GetAsync(number)).Revisions.ToDictionary(r => r.Revision, r => r.State);
        Assert.Equal(("Released", "Obsolete"), (states["B"], states["A"]));
    }

    [Fact]
    public async Task Manual_transitions_obsolete_an_mbom_with_a_reason_and_review_states_are_workflow_only()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var p = await env.ProductAsync();
        var number = (await env.Mbom.CreateAsync(MbomEnv.Alice, new CreateMbomCommand(p.Top, MbomEnv.Plant), "c")).Value!.Summary.Number;
        var viaReview = await env.Mbom.TransitionAsync(MbomEnv.Alice, number, "UnderReview", null, "c");
        Assert.Equal(OutcomeKind.Conflict, viaReview.Kind);
        Assert.Contains("submitting the MBOM for review", viaReview.Errors[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.TransitionAsync(MbomEnv.Alice, number, "Obsolete", null, "c")).Kind);
        var detail = (await env.Mbom.TransitionAsync(MbomEnv.Alice, number, "Obsolete", "Replaced by a new plant layout", "c")).Value!;
        Assert.Equal("Obsolete", detail.Summary.State);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "mbom.transition" && e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task Markings_must_dominate_the_part_and_are_redacted_for_readers_without_access()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var itar = ConfigEnv.Itar();
        var p = await env.ProductAsync(itar);

        // Asking for open markings on an ITAR part is refused until the minimal cover is confirmed.
        var open = await env.Mbom.CreateAsync(MbomEnv.Alice, new CreateMbomCommand(p.Top, MbomEnv.Plant, ConfigEnv.Open), "c");
        Assert.Equal(OutcomeKind.Invalid, open.Kind);
        Assert.Contains("at least as restrictive", open.Errors[0], StringComparison.Ordinal);
        var made = await env.Mbom.GenerateAsync(MbomEnv.Alice, new GenerateMbomCommand(p.Top, MbomEnv.Plant, EbomSpec.Released, false, ConfigEnv.Open, ConfirmRaiseMarkings: true), "c");
        Assert.True(made.IsOk, string.Join("; ", made.Errors));
        var number = made.Value!.Items[0].MbomNumber;
        var detail = await env.GetAsync(number);
        Assert.Equal(itar.BannerText(), detail.Summary.Banner);

        // Eve is not on program ORCA: the MBOM is invisible (404, denial audited), absent from search, and where-used shows a placeholder only.
        Assert.Equal(OutcomeKind.NotFound, (await env.Mbom.GetAsync(MbomEnv.Eve, number, null, "c")).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Mbom.GetLinesAsync(MbomEnv.Eve, number, null, null, "c")).Kind);
        Assert.Empty((await env.Mbom.SearchAsync(MbomEnv.Eve, new MbomSearch(), "c")).Value!.Items);
        Assert.Single((await env.Mbom.SearchAsync(MbomEnv.Alice, new MbomSearch(), "c")).Value!.Items);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "mbom.read" && e.Outcome == AuditOutcome.Denied && e.ObjectId == number);

        // Changing markings: a reason is needed, and they must still cover the part.
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.ChangeMarkingsAsync(MbomEnv.Alice, number, ConfigEnv.Open, "x", "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.ChangeMarkingsAsync(MbomEnv.Alice, number, itar, " ", "c")).Kind);
        var raised = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.F, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;
        var changed = await env.Mbom.ChangeMarkingsAsync(MbomEnv.Alice, number, raised, "Program direction", "c");
        Assert.True(changed.IsOk, string.Join("; ", changed.Errors));
        Assert.Contains("DISTRIBUTION F", changed.Value!.Summary.Banner, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restricted_parts_in_the_lines_are_placeholders_and_the_banner_never_hints_at_them()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var secret = await env.C.NewPartAsync("Stealth rotor", "Component", ConfigEnv.Itar());
        var plain = await env.BuyPartAsync("Washer");
        var top = await env.C.NewPartAsync("Mixed assembly", "Assembly");
        var number = (await env.Mbom.CreateAsync(MbomEnv.Alice, new CreateMbomCommand(top, MbomEnv.Plant), "c")).Value!.Summary.Number;
        await env.CheckOutAsync(number);
        Assert.True((await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(plain, 2), "c")).IsOk);
        Assert.True((await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(secret, 3, Notes: "rotor assembly note"), "c")).IsOk);
        await env.CheckInAsync(number);

        var asEve = (await env.Mbom.GetLinesAsync(MbomEnv.Eve, number, null, null, "c")).Value!;
        Assert.Equal(2, asEve.Lines.Count);
        var hidden = asEve.Lines.Single(l => l.Redacted);
        Assert.Equal((20, 3m, "EA"), (hidden.FindNumber, hidden.Quantity, hidden.Unit));
        Assert.All([hidden.ChildNumber, hidden.ChildName, hidden.Notes, hidden.Banner, hidden.SupplyType, hidden.OperationReference], Assert.Null);
        Assert.Empty(hidden.ReferenceDesignators);
        Assert.DoesNotContain("ITAR", asEve.Banner, StringComparison.Ordinal);
        var asAlice = (await env.Mbom.GetLinesAsync(MbomEnv.Alice, number, null, null, "c")).Value!;
        Assert.Contains("ITAR", asAlice.Banner, StringComparison.Ordinal);
        Assert.Equal(secret, asAlice.Lines.Single(l => l.FindNumber == 20).ChildNumber);
    }

    [Fact]
    public async Task Stored_rows_are_immutable_and_the_database_guards_hold_even_for_a_superuser()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (_, number) = await env.ReconciledProductAsync();
        await env.ReleaseAsync(number);
        async Task<string> Rejected(string sql)
        {
            try { await env.Db.ExecuteAsSuperuserAsync(sql); }
            catch (PostgresException e) { return e.MessageText; }
            Assert.Fail($"Not rejected: {sql}");
            return "";
        }
        Assert.Contains("immutable", await Rejected("UPDATE mbom.usage SET quantity = 99"), StringComparison.Ordinal);
        Assert.Contains("immutable", await Rejected("DELETE FROM mbom.usage"), StringComparison.Ordinal);
        Assert.Contains("immutable", await Rejected("UPDATE mbom.iteration SET description = 'x'"), StringComparison.Ordinal);
        Assert.Contains("immutable", await Rejected("UPDATE mbom.usage_link SET status = 'substituted', reason = 'x'"), StringComparison.Ordinal);
        Assert.Contains("immutable", await Rejected("UPDATE mbom.reconciliation_run SET status = 'Reconciled'"), StringComparison.Ordinal);
        Assert.Contains("immutable", await Rejected("DELETE FROM mbom.reconciliation_run"), StringComparison.Ordinal);
        Assert.Contains("immutable", await Rejected("TRUNCATE mbom.reconciliation_run CASCADE"), StringComparison.Ordinal);
        Assert.Contains("immutable", await Rejected("DELETE FROM mbom.mbom"), StringComparison.Ordinal);
        Assert.Contains("identity is immutable", await Rejected("UPDATE mbom.mbom SET plant = 'PLANT-B'"), StringComparison.Ordinal);
        // The released revision keeps its content pointers and accepts no new lines.
        Assert.Contains("locked", await Rejected("UPDATE mbom.revision SET latest_iteration = 9 WHERE lifecycle_state = 'Released'"), StringComparison.Ordinal);
        Assert.Contains("locked", await Rejected(
            "INSERT INTO mbom.usage (id, iteration_id, child_master_id, quantity, unit, find_number, supply_type, created_at, created_by) " +
            "SELECT gen_random_uuid(), i.id, u.child_master_id, 1, 'EA', 990, 'Make', now(), 'x' FROM mbom.usage u JOIN mbom.iteration i ON i.id = u.iteration_id LIMIT 1"), StringComparison.Ordinal);
        // The app role cannot even try: it holds no UPDATE or DELETE on the immutable tables.
        await using var app = new NpgsqlConnection(env.Db.AppConnectionString);
        await app.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE mbom.usage SET quantity = 99", app);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("42501", denied.SqlState);
        Assert.NotEmpty(number);
    }

    [Fact]
    public async Task Plants_and_supply_types_are_administrator_data()
    {
        var env = await MbomEnv.CreateAsync(pg);
        Assert.Equal(["PLANT-A", "PLANT-B"], (await env.Mbom.ListPlantsAsync()).Select(p => p.Code).ToArray());
        Assert.Equal(["Make", "Buy", "Consume"], (await env.Mbom.ListSupplyTypesAsync()).Select(s => s.Code).ToArray());
        var denied = await env.Mbom.SavePlantAsync(MbomEnv.Alice, new SavePlantCommand("PLANT-C", "Third plant"), "c");
        Assert.Equal(OutcomeKind.Forbidden, denied.Kind);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "mbom.plant_save" && e.Outcome == AuditOutcome.Denied);
        var saved = await env.Mbom.SavePlantAsync(MbomEnv.Ada, new SavePlantCommand("plant-c", "Third plant (synthetic)", true, false, 30), "c");
        Assert.True(saved.IsOk, string.Join("; ", saved.Errors));
        Assert.Equal(("PLANT-C", false), (saved.Value!.Code, saved.Value.ReleaseAllowsWarnings));
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.SavePlantAsync(MbomEnv.Ada, new SavePlantCommand("x", "Bad code"), "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.SaveSupplyTypeAsync(MbomEnv.Ada, new SaveSupplyTypeCommand("Make", "Make", IsActive: false), "c")).Kind);
        Assert.True((await env.Mbom.SaveSupplyTypeAsync(MbomEnv.Ada, new SaveSupplyTypeCommand("Subcontract", "Subcontracted", true, 40), "c")).IsOk);
        // A Consumable part type is seeded for MBOM-only items.
        var consumable = await env.C.Parts.CreateAsync(MbomEnv.Alice, new Keel.Parts.CreatePartCommand("Adhesive", "Consumable", "mL", "Buy", ConfigEnv.Open), null);
        Assert.True(consumable.IsOk, string.Join("; ", consumable.Errors));
    }
}
