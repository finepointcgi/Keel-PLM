using Keel.Bom;
using Keel.Core;
using Npgsql;
using static Keel.IntegrationTests.BomTestKit;

namespace Keel.IntegrationTests;

/// <summary>Validation, configuration specs, multi-level explosion, lazy expansion, summary, where-used, compare and edit helpers.</summary>
[Collection(PostgresCollection.Name)]
public sealed class BomStructureTests(PostgresFixture pg)
{
    private static readonly Subject Ada = User("ada", roles: [Roles.Admin, Roles.Viewer]);

    internal static async Task<T?> Scalar<T>(TestDatabase db, string sql, params (string, object)[] ps)
    {
        await using var conn = new NpgsqlConnection(db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    /// <summary>Forces a revision into the Released state (what the 1.5 workflow will do), bypassing the lifecycle on purpose.</summary>
    private static async Task Release(Env env, string number, string revision = "A") =>
        await env.Db.ExecuteAsSuperuserAsync(
            $"UPDATE plm.part_revision SET lifecycle_state = 'Released' WHERE revision = '{revision}' AND master_id = (SELECT id FROM plm.part_master WHERE number = '{number}')");

    /// <summary>Adds an InWork revision B as the latest (revisions are created by change control in 1.6).</summary>
    private static async Task AddRevisionB(Env env, string number) =>
        await env.Db.ExecuteAsSuperuserAsync(
            $"""
            UPDATE plm.part_revision SET is_latest = false WHERE revision = 'A' AND master_id = (SELECT id FROM plm.part_master WHERE number = '{number}');
            INSERT INTO plm.part_revision (id, master_id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by)
                SELECT gen_random_uuid(), id, 'B', 'InWork', true, 1, now(), 'sql', now(), 'sql' FROM plm.part_master WHERE number = '{number}';
            INSERT INTO plm.part_iteration (id, revision_id, iteration, description, attributes, comment, created_at, created_by)
                SELECT gen_random_uuid(), r.id, 1, 'B', jsonb_build_object(), 'Revision B', now(), 'sql'
                FROM plm.part_revision r JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = '{number}' AND r.revision = 'B';
            """);

    // ---------------------------------------------------------------- line validation

    [Fact]
    public async Task Add_enforces_units_EA_whole_numbers_and_designator_rules()
    {
        var env = await Setup(pg);
        var asm = await env.CreatePart(Alice, "Assembly", "Assembly");
        var child = await env.CreatePart(Alice, "Child");
        await env.Checkout(Alice, asm);
        Task<Outcome<WorkingBomLine>> TryAdd(decimal q, string? unit = null, string[]? refs = null, int? find = null) =>
            env.Bom.AddUsageAsync(Alice, asm, new AddUsageCommand(child, q, unit, find, refs), null);

        Assert.Contains((await TryAdd(1, "KG")).Errors, e => e.Contains("is not valid", StringComparison.Ordinal));
        Assert.Contains("whole number", (await TryAdd(2.5m, "EA")).Errors[0], StringComparison.Ordinal);
        Assert.Contains("counts must match", (await TryAdd(3, "EA", ["R1", "R2"])).Errors[0], StringComparison.Ordinal);
        Assert.Contains("unique within the line", (await TryAdd(2, "EA", ["R1", "r1"])).Errors[0], StringComparison.Ordinal);
        Assert.All(new[] { await TryAdd(0), await TryAdd(1, find: 0) }, r => Assert.Equal(OutcomeKind.Invalid, r.Kind));

        var first = (await TryAdd(2, "EA", ["R1", "R2"])).Value!;
        Assert.Equal((10, "EA", 2m), (first.FindNumber, first.Unit, first.Quantity));
        Assert.Equal(["R1", "R2"], first.ReferenceDesignators);
        var clash = await TryAdd(1, "EA", ["R2"]);
        Assert.Equal(OutcomeKind.Invalid, clash.Kind);
        Assert.Contains("already used elsewhere", clash.Errors[0], StringComparison.Ordinal);

        var second = (await TryAdd(1.5m, "m")).Value!; // fractional lengths are fine for non-EA units
        Assert.Equal(20, second.FindNumber);
        var dup = await TryAdd(1, find: 20);
        Assert.Equal((OutcomeKind.Conflict, "Find number 20 is already used on this parent."), (dup.Kind, dup.Errors[0]));
        Assert.Equal(30, (await TryAdd(1, find: 30)).Value!.FindNumber);
        Assert.Equal(40, (await TryAdd(1)).Value!.FindNumber); // next multiple of ten above the highest
    }

    [Fact]
    public async Task Only_assemblies_own_children_and_children_must_be_readable()
    {
        var env = await Setup(pg);
        var component = await env.CreatePart(Alice, "Plain component");
        var secret = await env.CreatePart(Alice, "Stealth rotor", markings: Itar());
        var asm = await env.CreatePart(Lena, "Open assembly", "Assembly");

        await env.Checkout(Alice, component);
        var notAssembly = await env.Bom.AddUsageAsync(Alice, component, new AddUsageCommand(secret, 1), null);
        Assert.Equal(OutcomeKind.Conflict, notAssembly.Kind);
        Assert.Contains("only Assembly parts can own BOM lines", notAssembly.Errors[0], StringComparison.Ordinal);

        await env.Checkout(Lena, asm);
        var unreadable = await env.Bom.AddUsageAsync(Lena, asm, new AddUsageCommand(secret, 1), null);
        Assert.Equal(OutcomeKind.Invalid, unreadable.Kind);
        Assert.DoesNotContain("Stealth", unreadable.Errors[0], StringComparison.Ordinal);
        var missing = await env.Bom.AddUsageAsync(Lena, asm, new AddUsageCommand("NOPE-1", 1), null);
        Assert.Equal(unreadable.Errors[0].Replace(secret, "X", StringComparison.Ordinal), missing.Errors[0].Replace("NOPE-1", "X", StringComparison.Ordinal)); // indistinguishable
        Assert.Contains(await env.Audit(), e => e.Action == "bom.add_usage" && e.Outcome == Keel.Audit.AuditOutcome.Denied && e.Payload.Contains("child.not_readable", StringComparison.Ordinal));

        // The database refuses it as well, whatever the service does.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => env.Db.ExecuteAsSuperuserAsync(
            $"""
            INSERT INTO plm.bom_usage (id, parent_iteration_id, child_master_id, quantity, unit, find_number, created_at, created_by)
            SELECT gen_random_uuid(), i.id, (SELECT id FROM plm.part_master WHERE number = '{secret}'), 1, 'EA', 10, now(), 'sql'
            FROM plm.part_iteration i JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = '{component}'
            """));
        Assert.Contains("only Assembly parts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cycles_are_rejected_with_the_path_named()
    {
        var env = await Setup(pg);
        var a = await env.CreatePart(Alice, "Top", "Assembly");
        var b = await env.CreatePart(Alice, "Middle", "Assembly", Itar());
        var c = await env.CreatePart(Alice, "Bottom", "Assembly");

        await env.Checkout(Alice, b); await env.Add(Alice, b, c); await env.CheckIn(Alice, b);
        await env.Checkout(Alice, a); await env.Add(Alice, a, b); await env.CheckIn(Alice, a);

        await env.Checkout(Alice, c);
        var self = await env.Bom.AddUsageAsync(Alice, c, new AddUsageCommand(c, 1), null);
        Assert.Equal((OutcomeKind.Conflict, $"{c} cannot contain itself."), (self.Kind, self.Errors[0]));

        var loop = await env.Bom.AddUsageAsync(Alice, c, new AddUsageCommand(a, 1), null);
        Assert.Equal(OutcomeKind.Conflict, loop.Kind);
        Assert.Equal($"Adding {a} under {c} would create a circular structure: {a} -> {b} -> {c} -> {a}.", loop.Errors[0]);

        // A user who cannot read the middle assembly gets the same error with that part named as restricted.
        await env.Parts.UndoCheckOutAsync(Alice, c, null);
        await env.Checkout(Lena, c);
        var masked = await env.Bom.AddUsageAsync(Lena, c, new AddUsageCommand(a, 1), null);
        Assert.Equal($"Adding {a} under {c} would create a circular structure: {a} -> (restricted item) -> {c} -> {a}.", masked.Errors[0]);
        Assert.Equal(0, await Scalar<long>(env.Db, "SELECT count(*) FROM plm.bom_working_usage WHERE child_master_id = (SELECT id FROM plm.part_master WHERE number = @n)", ("n", a)));
    }

    [Fact]
    public async Task A_parent_holds_at_most_five_thousand_direct_children()
    {
        var env = await Setup(pg);
        var asm = await env.CreatePart(Alice, "Huge", "Assembly");
        var child = await env.CreatePart(Alice, "Screw");
        await env.Checkout(Alice, asm);
        await env.Db.ExecuteAsSuperuserAsync(
            $"""
            INSERT INTO plm.bom_working_usage (id, checkout_revision_id, child_master_id, quantity, unit, find_number, created_at, created_by)
            SELECT gen_random_uuid(), c.revision_id, (SELECT id FROM plm.part_master WHERE number = '{child}'), 1, 'EA', g * 10, now(), 'sql'
            FROM generate_series(1, {BomLimits.MaxChildrenPerParent}) g, plm.part_checkout c
            """);
        var over = await env.Bom.AddUsageAsync(Alice, asm, new AddUsageCommand(child, 1), null);
        Assert.Equal(OutcomeKind.Conflict, over.Kind);
        Assert.Contains("at most 5000 direct children", over.Errors[0], StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- explosion

    [Fact]
    public async Task Explosion_rolls_up_extended_quantities_and_reports_paths_levels_and_children()
    {
        var env = await Setup(pg);
        var leaf = await env.CreatePart(Alice, "Fastener");
        var rod = await env.CreatePart(Alice, "Rod", "RawMaterial", unit: "m");
        var sub = await env.Assembly(Alice, "Sub", null, (leaf, 3));
        await env.Checkout(Alice, sub);
        await env.Add(Alice, sub, rod, 0.25m, "m");
        await env.CheckIn(Alice, sub);
        var top = await env.Assembly(Alice, "Top", null, (sub, 2), (leaf, 1));

        var all = (await env.Bom.ExplodeAsync(Alice, new BomQuery(top), "corr-x")).Value!;
        Assert.Equal(["10", "10.10", "10.20", "20"], all.Lines.Select(l => l.Path));
        Assert.Equal([1, 2, 2, 1], all.Lines.Select(l => l.Level));
        Assert.Equal([2m, 6m, 0.5m, 1m], all.Lines.Select(l => l.ExtendedQuantity));
        Assert.Equal([sub, leaf, rod, leaf], all.Lines.Select(l => l.Number));
        Assert.Equal([true, false, false, false], all.Lines.Select(l => l.HasChildren));
        Assert.Equal(all.Lines[0].LineId, all.Lines[1].ParentLineId);
        Assert.Null(all.Lines[0].ParentLineId);
        Assert.Equal("A", all.Lines[0].Revision);
        Assert.Equal("In Work", all.Lines[0].StateLabel);
        Assert.Equal("UNCLASSIFIED · DISTRIBUTION A", all.Banner);

        var one = (await env.Bom.ExplodeAsync(Alice, new BomQuery(top, Depth: 1), null)).Value!;
        Assert.Equal(["10", "20"], one.Lines.Select(l => l.Path));
        Assert.True(one.Lines[0].HasChildren);

        // Lazy expansion of one line keeps absolute levels, paths and the rolled-up quantity.
        var lazy = (await env.Bom.ExplodeAsync(Alice, new BomQuery(top, Depth: 1, ParentLine: "10"), null)).Value!;
        Assert.Equal(["10.10", "10.20"], lazy.Lines.Select(l => l.Path));
        Assert.Equal([2, 2], lazy.Lines.Select(l => l.Level));
        Assert.Equal([6m, 0.5m], lazy.Lines.Select(l => l.ExtendedQuantity));
        Assert.Equal(OutcomeKind.NotFound, (await env.Bom.ExplodeAsync(Alice, new BomQuery(top, ParentLine: "99"), null)).Kind);

        Assert.Equal(OutcomeKind.Invalid, (await env.Bom.ExplodeAsync(Alice, new BomQuery(top, Depth: 0), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Bom.ExplodeAsync(Alice, new BomQuery(top, Depth: 11), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Bom.ExplodeAsync(Alice, new BomQuery(top, ParentLine: "10..5"), null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Bom.ExplodeAsync(Alice, new BomQuery(top, Revision: "Z"), null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Bom.ExplodeAsync(Alice, new BomQuery("NOPE"), null)).Kind);
        var phase2 = await Assert.ThrowsAsync<NotSupportedException>(() => env.Bom.ExplodeAsync(Alice, new BomQuery(top, Spec: BomConfigSpec.Effectivity), null));
        Assert.Contains("Phase 2", phase2.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<NotSupportedException>(() => env.Bom.ExplodeAsync(Alice, new BomQuery(top, Spec: BomConfigSpec.Baseline), null));
    }

    [Fact]
    public async Task A_shared_subassembly_is_expanded_under_every_use_with_its_own_path()
    {
        var env = await Setup(pg);
        var leaf = await env.CreatePart(Alice, "Leaf");
        var shared = await env.Assembly(Alice, "Shared", null, (leaf, 4));
        var top = await env.Assembly(Alice, "Top", null, (shared, 1), (shared, 2));

        var e = (await env.Bom.ExplodeAsync(Alice, new BomQuery(top), null)).Value!;
        Assert.Equal(["10", "10.10", "20", "20.10"], e.Lines.Select(l => l.Path));
        Assert.Equal([1m, 4m, 2m, 8m], e.Lines.Select(l => l.ExtendedQuantity));
    }

    [Fact]
    public async Task Config_specs_choose_the_latest_or_the_highest_released_revision_and_flag_unresolved_lines()
    {
        var env = await Setup(pg);
        var released = await env.CreatePart(Alice, "Has a release");
        var neverReleased = await env.CreatePart(Alice, "Still in work");
        var top = await env.Assembly(Alice, "Top", null, (released, 1), (neverReleased, 1));
        await Release(env, released);
        await AddRevisionB(env, released); // A released, B in work and latest

        var latest = (await env.Bom.ExplodeAsync(Alice, new BomQuery(top, Spec: BomConfigSpec.Latest), null)).Value!;
        Assert.Equal(["B", "A"], latest.Lines.Select(l => l.Revision));
        Assert.Equal(0, latest.UnresolvedCount);

        var lr = (await env.Bom.ExplodeAsync(Alice, new BomQuery(top, Spec: BomConfigSpec.LatestReleased), null)).Value!;
        Assert.Equal("A", lr.Lines[0].Revision);
        Assert.Equal("Released", lr.Lines[0].State);
        // No released revision: the line stays, flagged, with its identity (the caller may read it) but no revision.
        var unresolved = lr.Lines[1];
        Assert.True(unresolved.Unresolved);
        Assert.False(unresolved.Redacted);
        Assert.Equal((neverReleased, null), (unresolved.Number, unresolved.Revision));
        Assert.Equal(1, lr.UnresolvedCount);
        Assert.False(unresolved.HasChildren);

        // A released revision makes it resolve; the higher released revision wins.
        await Release(env, neverReleased);
        Assert.Equal("A", (await env.Bom.ExplodeAsync(Alice, new BomQuery(top, Spec: BomConfigSpec.LatestReleased), null)).Value!.Lines[1].Revision);
        await env.Db.ExecuteAsSuperuserAsync($"UPDATE plm.part_revision SET lifecycle_state = 'Released' WHERE revision = 'B' AND master_id = (SELECT id FROM plm.part_master WHERE number = '{released}')");
        Assert.Equal("B", (await env.Bom.ExplodeAsync(Alice, new BomQuery(top, Spec: BomConfigSpec.LatestReleased), null)).Value!.Lines[0].Revision);
    }

    [Fact]
    public async Task Config_spec_is_resolved_per_level_below_the_root()
    {
        var env = await Setup(pg);
        var leaf = await env.CreatePart(Alice, "Leaf");
        var sub = await env.Assembly(Alice, "Sub", null, (leaf, 1));
        var top = await env.Assembly(Alice, "Top", null, (sub, 1));
        await Release(env, leaf);
        await AddRevisionB(env, sub); // sub's latest revision B has no structure; released nothing at sub

        var latest = (await env.Bom.ExplodeAsync(Alice, new BomQuery(top), null)).Value!;
        Assert.Equal(["10"], latest.Lines.Select(l => l.Path)); // revision B of sub is empty
        var lr = (await env.Bom.ExplodeAsync(Alice, new BomQuery(top, Spec: BomConfigSpec.LatestReleased), null)).Value!;
        Assert.Equal(["10"], lr.Lines.Select(l => l.Path));
        Assert.True(lr.Lines[0].Unresolved); // sub has no released revision, so its subtree is not expanded
    }

    [Fact]
    public async Task Circular_structures_that_slip_in_through_config_specs_do_not_loop_forever()
    {
        var env = await Setup(pg);
        var a = await env.Assembly(Alice, "A", null);
        var b = await env.Assembly(Alice, "B", null);
        await env.Checkout(Alice, a); await env.Add(Alice, a, b); await env.CheckIn(Alice, a);
        await Release(env, a); // A.2 (contains B) is released
        await AddRevisionB(env, a); // A's latest is now an empty revision B, so B may add A
        await env.Checkout(Alice, b); await env.Add(Alice, b, a); await env.CheckIn(Alice, b);
        await Release(env, b);

        // Root A revision A -> B (released, contains A) -> A (released revision A, contains B): flagged circular, not expanded further.
        var lr = (await env.Bom.ExplodeAsync(Alice, new BomQuery(a, Revision: "A", Spec: BomConfigSpec.LatestReleased), null)).Value!;
        Assert.Equal(["10", "10.10"], lr.Lines.Select(l => l.Path));
        Assert.True(lr.Lines[1].Circular);
        Assert.False(lr.Lines[1].HasChildren);
    }

    // ---------------------------------------------------------------- summary, where-used, compare

    [Fact]
    public async Task Summary_sums_extended_quantities_per_distinct_part_and_unit()
    {
        var env = await Setup(pg);
        var screw = await env.CreatePart(Alice, "Screw");
        var plate = await env.CreatePart(Alice, "Plate");
        var sub = await env.Assembly(Alice, "Sub", null, (screw, 4), (plate, 1));
        var top = await env.Assembly(Alice, "Top", null, (sub, 3), (screw, 2));

        var leaves = (await env.Bom.SummaryAsync(Alice, new BomQuery(top), true, null)).Value!;
        Assert.Equal([screw, plate], leaves.Lines.Select(l => l.Number)); // screw: 3*4 + 2
        Assert.Equal(14m, leaves.Lines.Single(l => l.Number == screw).TotalQuantity);
        Assert.Equal(2, leaves.Lines.Single(l => l.Number == screw).Occurrences);
        Assert.Equal(3m, leaves.Lines.Single(l => l.Number == plate).TotalQuantity);
        var everything = (await env.Bom.SummaryAsync(Alice, new BomQuery(top), false, null)).Value!;
        Assert.Contains(everything.Lines, l => l.Number == sub && l.TotalQuantity == 3m && l.IsAssembly);
    }

    [Fact]
    public async Task Where_used_lists_parent_revisions_and_climbs_levels()
    {
        var env = await Setup(pg);
        var bolt = await env.CreatePart(Alice, "Bolt");
        var sub = await env.Assembly(Alice, "Sub", null, (bolt, 4));
        var top = await env.Assembly(Alice, "Top", null, (sub, 2));
        var other = await env.Assembly(Alice, "Other", null, (bolt, 1));
        await Release(env, sub);
        await AddRevisionB(env, sub); // revision B is empty, A.2 contains the bolt

        var one = (await env.Bom.WhereUsedAsync(Alice, bolt, 1, null)).Value!;
        Assert.Equal(new[] { other, sub }.Order(StringComparer.Ordinal), one.Rows.Select(r => r.ParentNumber!).Order(StringComparer.Ordinal));
        var viaSub = one.Rows.Single(r => r.ParentNumber == sub);
        Assert.Equal(("A", 4m, 10, 1), (viaSub.ParentRevision, viaSub.Quantity, viaSub.FindNumber, viaSub.Level));
        Assert.Equal("Released", viaSub.ParentState);

        var up = (await env.Bom.WhereUsedAsync(Alice, bolt, 2, null)).Value!;
        Assert.Contains(up.Rows, r => r.Level == 2 && r.ParentNumber == top && r.UsedNumber == sub && r.Quantity == 2m);
        Assert.Equal(2, up.Rows.Count(r => r.Level == 1));
        Assert.Equal(OutcomeKind.Invalid, (await env.Bom.WhereUsedAsync(Alice, bolt, 0, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Bom.WhereUsedAsync(Alice, "NOPE", 1, null)).Kind);
    }

    [Fact]
    public async Task Compare_reports_added_removed_changed_and_renumbered_lines_and_history_stays_viewable()
    {
        var env = await Setup(pg);
        var a = await env.CreatePart(Alice, "Part A");
        var b = await env.CreatePart(Alice, "Part B");
        var c = await env.CreatePart(Alice, "Part C");
        var d = await env.CreatePart(Alice, "Part D");
        var top = await env.Assembly(Alice, "Top", null, (a, 1), (b, 2), (c, 3)); // top A.2

        await env.Checkout(Alice, top);
        var working = (await env.Bom.GetWorkingAsync(Alice, top, null)).Value!;
        await env.Bom.UpdateUsageAsync(Alice, top, working.Lines.Single(l => l.ChildNumber == b).Id, new UpdateUsageCommand(5, "EA", 20), null);
        await env.Bom.UpdateUsageAsync(Alice, top, working.Lines.Single(l => l.ChildNumber == c).Id, new UpdateUsageCommand(3, "EA", 35), null);
        await env.Bom.RemoveUsageAsync(Alice, top, working.Lines.Single(l => l.ChildNumber == a).Id, null);
        await env.Add(Alice, top, d, 7);
        await env.CheckIn(Alice, top); // A.3

        var diff = (await env.Bom.CompareAsync(Alice, top, "A.2", "A.3", null)).Value!;
        Assert.Equal(0, diff.Unchanged);
        Assert.Equal(BomChangeKind.Removed, diff.Lines.Single(l => l.Number == a).Kind);
        Assert.Equal(["Quantity 2 to 5"], diff.Lines.Single(l => l.Number == b).Changes);
        Assert.Equal(["Find number 30 to 35"], diff.Lines.Single(l => l.Number == c).Changes);
        var added = diff.Lines.Single(l => l.Number == d);
        Assert.Equal((BomChangeKind.Added, 7m), (added.Kind, added.ToQuantity));

        // The structure of A.2 is unchanged and still viewable after A.3 replaced it.
        var old = (await env.Bom.ExplodeAsync(Alice, new BomQuery(top, Iteration: 2), null)).Value!;
        Assert.Equal([a, b, c], old.Lines.Select(l => l.Number));
        Assert.Equal([b, c, d], (await env.Bom.ExplodeAsync(Alice, new BomQuery(top), null)).Value!.Lines.Select(l => l.Number!));
        Assert.Equal(OutcomeKind.Invalid, (await env.Bom.CompareAsync(Alice, top, "A", "A.3", null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Bom.CompareAsync(Alice, top, "A.1", "A.9", null)).Kind);
    }

    // ---------------------------------------------------------------- renumber and copy

    [Fact]
    public async Task Renumber_assigns_ten_twenty_thirty_in_the_current_order()
    {
        var env = await Setup(pg);
        var asm = await env.CreatePart(Alice, "Asm", "Assembly");
        var x = await env.CreatePart(Alice, "X");
        var y = await env.CreatePart(Alice, "Y");
        var z = await env.CreatePart(Alice, "Z");
        await env.Checkout(Alice, asm);
        await env.Add(Alice, asm, z, find: 7);
        await env.Add(Alice, asm, x, find: 3);
        await env.Add(Alice, asm, y, find: 5);

        var renumbered = (await env.Bom.RenumberAsync(Alice, asm, 10, 10, null)).Value!;
        Assert.Equal([(10, x), (20, y), (30, z)], renumbered.Lines.Select(l => (l.FindNumber, l.ChildNumber!)));
        Assert.Equal(OutcomeKind.Invalid, (await env.Bom.RenumberAsync(Alice, asm, 0, 10, null)).Kind);
        Assert.Equal([(100, x), (105, y), (110, z)], (await env.Bom.RenumberAsync(Alice, asm, 100, 5, null)).Value!.Lines.Select(l => (l.FindNumber, l.ChildNumber!)));
    }

    [Fact]
    public async Task Copy_brings_in_the_latest_structure_of_another_assembly_and_refuses_loops_and_restricted_content()
    {
        var env = await Setup(pg);
        var p = await env.CreatePart(Alice, "P");
        var q = await env.CreatePart(Alice, "Q");
        var secret = await env.CreatePart(Alice, "Secret", markings: Itar());
        var source = await env.Assembly(Alice, "Source", null, (p, 2), (q, 1));
        var withSecret = await env.Assembly(Alice, "With secret", null, (p, 1), (secret, 1));
        var target = await env.CreatePart(Alice, "Target", "Assembly");

        await env.Checkout(Alice, target);
        Assert.Equal(OutcomeKind.Invalid, (await env.Bom.CopyStructureAsync(Alice, target, target, false, null)).Kind);
        var copied = (await env.Bom.CopyStructureAsync(Alice, target, source, false, null)).Value!;
        Assert.Equal([(10, p, 2m), (20, q, 1m)], copied.Lines.Select(l => (l.FindNumber, l.ChildNumber!, l.Quantity)));
        var again = await env.Bom.CopyStructureAsync(Alice, target, source, false, null);
        Assert.Equal(OutcomeKind.Conflict, again.Kind);
        Assert.Equal(2, (await env.Bom.CopyStructureAsync(Alice, target, source, true, null)).Value!.Lines.Count);

        // A source containing parts the caller cannot read is refused, not copied incompletely.
        await env.Parts.UndoCheckOutAsync(Alice, target, null);
        await env.Checkout(Lena, target);
        var refused = await env.Bom.CopyStructureAsync(Lena, target, withSecret, false, null);
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        Assert.Contains("1 item(s) you are not authorized to see", refused.Errors[0], StringComparison.Ordinal);
        Assert.DoesNotContain(secret, refused.Errors[0], StringComparison.Ordinal);

        // Copying a structure that contains the target would loop.
        var loopTop = await env.Assembly(Alice, "Loop top", null, (target, 1));
        await env.Parts.UndoCheckOutAsync(Lena, target, null);
        await env.Checkout(Alice, target);
        var loop = await env.Bom.CopyStructureAsync(Alice, target, loopTop, false, null);
        Assert.Equal(OutcomeKind.Conflict, loop.Kind);
        Assert.Contains("cannot contain itself", loop.Errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Part_service_behaviour_is_unchanged_for_released_assemblies()
    {
        // Sanity: the participant leaves PartService behaviour alone when a part has no structure.
        var env = await Setup(pg);
        var part = await env.CreatePart(Alice, "Plain", "Assembly");
        await Release(env, part);
        Assert.Equal(OutcomeKind.Conflict, (await env.Parts.CheckOutAsync(Alice, part, null)).Kind);
        Assert.Equal(OutcomeKind.Ok, (await env.Parts.SetStateAsync(Ada, part, "InWork", "reopen", true, null)).Kind);
    }
}
