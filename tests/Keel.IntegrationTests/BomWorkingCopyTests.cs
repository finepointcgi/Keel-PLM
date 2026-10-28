using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Npgsql;
using static Keel.IntegrationTests.BomTestKit;

namespace Keel.IntegrationTests;

/// <summary>Check-out / check-in semantics of BOM lines, database guards, concurrency and auditing.</summary>
[Collection(PostgresCollection.Name)]
public sealed class BomWorkingCopyTests(PostgresFixture pg)
{
    private static Task<long> Count(TestDatabase db, string table) => BomStructureTests.Scalar<long>(db, $"SELECT count(*) FROM {table}");

    [Fact]
    public async Task Check_out_copies_the_structure_undo_discards_and_check_in_freezes_the_next_iteration()
    {
        var env = await Setup(pg);
        var x = await env.CreatePart(Alice, "X");
        var y = await env.CreatePart(Alice, "Y");
        var asm = await env.Assembly(Alice, "Asm", null, (x, 2)); // A.2 contains X

        // Check-out copies the latest iteration's lines into the private working copy.
        await env.Checkout(Alice, asm);
        Assert.Equal(1, await Count(env.Db, "plm.bom_working_usage"));
        Assert.Equal([x], (await env.Bom.GetWorkingAsync(Alice, asm, null)).Value!.Lines.Select(l => l.ChildNumber));

        // Edits are private: the committed structure does not change until check-in.
        await env.Add(Alice, asm, y, 5);
        Assert.Equal([x], (await env.Bom.ExplodeAsync(Alice, new BomQuery(asm), null)).Value!.Lines.Select(l => l.Number));
        Assert.Equal(1, await Count(env.Db, "plm.bom_usage"));

        // Undo discards the working rows (cascade) and nothing reaches the committed structure.
        Assert.True((await env.Parts.UndoCheckOutAsync(Alice, asm, null)).IsOk);
        Assert.Equal(0, await Count(env.Db, "plm.bom_working_usage"));
        Assert.Equal(1, await Count(env.Db, "plm.bom_usage"));
        await env.Checkout(Alice, asm);
        Assert.Equal([x], (await env.Bom.GetWorkingAsync(Alice, asm, null)).Value!.Lines.Select(l => l.ChildNumber)); // starts from A.2 again

        // Check-in freezes the working rows into A.3 and clears the working copy.
        await env.Add(Alice, asm, y, 5);
        await env.CheckIn(Alice, asm, "add Y");
        Assert.Equal(0, await Count(env.Db, "plm.bom_working_usage"));
        Assert.Equal(3, await Count(env.Db, "plm.bom_usage")); // A.2: X; A.3: X, Y
        Assert.Equal([x, y], (await env.Bom.ExplodeAsync(Alice, new BomQuery(asm), null)).Value!.Lines.Select(l => l.Number));
        Assert.Equal("A.3", $"{(await env.Bom.ExplodeAsync(Alice, new BomQuery(asm), null)).Value!.RootRevision}.{(await env.Bom.ExplodeAsync(Alice, new BomQuery(asm), null)).Value!.RootIteration}");

        // History: A.2 still shows its original structure, as does A.1 (empty).
        Assert.Equal([x], (await env.Bom.ExplodeAsync(Alice, new BomQuery(asm, Iteration: 2), null)).Value!.Lines.Select(l => l.Number));
        Assert.Empty((await env.Bom.ExplodeAsync(Alice, new BomQuery(asm, Iteration: 1), null)).Value!.Lines);
        Assert.Equal(OutcomeKind.NotFound, (await env.Bom.ExplodeAsync(Alice, new BomQuery(asm, Iteration: 9), null)).Kind);

        // An unchanged check-in still freezes a complete copy, and another user can start from it.
        await env.Checkout(Bob, asm);
        await env.CheckIn(Bob, asm);
        Assert.Equal(5, await Count(env.Db, "plm.bom_usage"));
        Assert.Equal([x, y], (await env.Bom.ExplodeAsync(Alice, new BomQuery(asm, Iteration: 4), null)).Value!.Lines.Select(l => l.Number));
    }

    [Fact]
    public async Task Only_the_check_out_owner_can_edit_and_see_the_working_copy()
    {
        var env = await Setup(pg);
        var x = await env.CreatePart(Alice, "X");
        var asm = await env.CreatePart(Alice, "Asm", "Assembly");

        var notOut = await env.Bom.AddUsageAsync(Alice, asm, new AddUsageCommand(x, 1), null);
        Assert.Equal((OutcomeKind.Conflict, "Check the part out before editing its BOM."), (notOut.Kind, notOut.Errors[0]));
        Assert.Equal(OutcomeKind.Conflict, (await env.Bom.GetWorkingAsync(Alice, asm, null)).Kind);

        await env.Checkout(Alice, asm);
        var line = await env.Add(Alice, asm, x);
        var bobAdd = await env.Bom.AddUsageAsync(Bob, asm, new AddUsageCommand(x, 1, FindNumber: 50), null);
        Assert.Equal(OutcomeKind.Conflict, bobAdd.Kind);
        Assert.Contains("Checked out by alice", bobAdd.Errors[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Conflict, (await env.Bom.UpdateUsageAsync(Bob, asm, line.Id, new UpdateUsageCommand(9, "EA", 10), null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Bom.RemoveUsageAsync(Bob, asm, line.Id, null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Bom.RenumberAsync(Bob, asm, 10, 10, null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Bom.GetWorkingAsync(Bob, asm, null)).Kind);

        // A read-only user may not edit at all, even with the part checked out by someone else's rights.
        var viewer = await env.Bom.AddUsageAsync(Viewer, asm, new AddUsageCommand(x, 1, FindNumber: 60), null);
        Assert.Equal(OutcomeKind.Forbidden, viewer.Kind);
        Assert.Contains("role.missing", viewer.Errors);
        Assert.Single((await env.Bom.GetWorkingAsync(Alice, asm, null)).Value!.Lines);

        // Update and remove address lines of this check-out only.
        Assert.Equal(OutcomeKind.NotFound, (await env.Bom.UpdateUsageAsync(Alice, asm, Guid.NewGuid(), new UpdateUsageCommand(1, "EA", 70), null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Bom.RemoveUsageAsync(Alice, asm, Guid.NewGuid(), null)).Kind);
        var updated = (await env.Bom.UpdateUsageAsync(Alice, asm, line.Id, new UpdateUsageCommand(4, "EA", 15, ["A", "B", "C", "D"], "n"), null)).Value!;
        Assert.Equal((15, 4m, "n"), (updated.FindNumber, updated.Quantity, updated.Notes));
        var removed = (await env.Bom.RemoveUsageAsync(Alice, asm, line.Id, null)).Value!;
        Assert.Equal((15, 0), (removed.FindNumber, removed.Remaining));
    }

    [Fact]
    public async Task The_database_rejects_changes_to_frozen_rows_and_to_locked_revisions()
    {
        var env = await Setup(pg);
        var x = await env.CreatePart(Alice, "X");
        var asm = await env.Assembly(Alice, "Asm", null, (x, 1)); // A.2
        var sup = env.Db;

        // Rows of an iteration never change or disappear, for any role (trigger), and the app role has no UPDATE/DELETE privilege at all.
        var update = await Assert.ThrowsAsync<PostgresException>(() => sup.ExecuteAsSuperuserAsync("UPDATE plm.bom_usage SET quantity = 99"));
        Assert.Contains("immutable", update.Message, StringComparison.Ordinal);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => sup.ExecuteAsSuperuserAsync("DELETE FROM plm.bom_usage"));
        Assert.Contains("immutable", delete.Message, StringComparison.Ordinal);
        await using (var conn = await env.Ds.OpenConnectionAsync())
        {
            await using var cmd = new NpgsqlCommand("UPDATE plm.bom_usage SET quantity = 99", conn);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync())).SqlState);
        }

        await env.Checkout(Alice, asm);
        // The EA rules are also database constraints.
        var constraint = await Assert.ThrowsAsync<PostgresException>(() => sup.ExecuteAsSuperuserAsync(
            """
            INSERT INTO plm.bom_working_usage (id, checkout_revision_id, child_master_id, quantity, unit, find_number, created_at, created_by)
            SELECT gen_random_uuid(), checkout_revision_id, child_master_id, 1.5, 'EA', 98, now(), 'sql' FROM plm.bom_working_usage LIMIT 1
            """));
        Assert.Equal("23514", constraint.SqlState);

        // Release locking: once the revision is locked no row can be added or changed, whatever the caller does.
        await sup.ExecuteAsSuperuserAsync($"UPDATE plm.part_revision SET lifecycle_state = 'Released' WHERE master_id = (SELECT id FROM plm.part_master WHERE number = '{asm}')");
        var workingUpdate = await Assert.ThrowsAsync<PostgresException>(() => sup.ExecuteAsSuperuserAsync("UPDATE plm.bom_working_usage SET quantity = 5"));
        Assert.Contains("locked state", workingUpdate.Message, StringComparison.Ordinal);
        var workingInsert = await Assert.ThrowsAsync<PostgresException>(() => sup.ExecuteAsSuperuserAsync(
            $"""
            INSERT INTO plm.bom_working_usage (id, checkout_revision_id, child_master_id, quantity, unit, find_number, created_at, created_by)
            SELECT gen_random_uuid(), c.revision_id, (SELECT id FROM plm.part_master WHERE number = '{x}'), 1, 'EA', 99, now(), 'sql' FROM plm.part_checkout c
            """));
        Assert.Contains("locked state", workingInsert.Message, StringComparison.Ordinal);
        var usageInsert = await Assert.ThrowsAsync<PostgresException>(() => sup.ExecuteAsSuperuserAsync(
            $"""
            INSERT INTO plm.bom_usage (id, parent_iteration_id, child_master_id, quantity, unit, find_number, created_at, created_by)
            SELECT gen_random_uuid(), i.id, (SELECT id FROM plm.part_master WHERE number = '{x}'), 1, 'EA', 99, now(), 'sql'
            FROM plm.part_iteration i JOIN plm.part_revision r ON r.id = i.revision_id WHERE r.master_id = (SELECT id FROM plm.part_master WHERE number = '{asm}')
            """));
        Assert.Contains("locked state", usageInsert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_adds_by_the_check_out_owner_stay_consistent()
    {
        var env = await Setup(pg);
        var asm = await env.CreatePart(Alice, "Asm", "Assembly");
        var children = new List<string>();
        for (var i = 0; i < 16; i++) children.Add(await env.CreatePart(Alice, $"Child {i}"));
        await env.Checkout(Alice, asm);

        var results = await Task.WhenAll(children.Select(c => env.Bom.AddUsageAsync(Alice, asm, new AddUsageCommand(c, 1), null)));
        Assert.All(results, r => Assert.True(r.IsOk, string.Join(" ", r.Errors)));
        var finds = results.Select(r => r.Value!.FindNumber).Order().ToList();
        Assert.Equal(Enumerable.Range(1, 16).Select(i => i * 10), finds); // serialized on the part row: no duplicates, no gaps
        Assert.Equal(16, (await env.Bom.GetWorkingAsync(Alice, asm, null)).Value!.Lines.Count);

        // Racing for the same explicit find number: exactly one wins.
        var extra = await env.CreatePart(Alice, "Extra");
        var race = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => env.Bom.AddUsageAsync(Alice, asm, new AddUsageCommand(extra, 1, FindNumber: 500), null)));
        Assert.Equal(1, race.Count(r => r.IsOk));
        Assert.All(race.Where(r => !r.IsOk), r => Assert.Equal(OutcomeKind.Conflict, r.Kind));
    }

    [Fact]
    public async Task Two_check_outs_that_would_close_a_loop_are_stopped_at_check_in()
    {
        var env = await Setup(pg);
        var a = await env.CreatePart(Alice, "A", "Assembly");
        var b = await env.CreatePart(Alice, "B", "Assembly");
        await env.Checkout(Alice, a);
        await env.Checkout(Bob, b);
        await env.Add(Alice, a, b); // allowed: B has no structure yet
        await env.Add(Bob, b, a); // allowed: A's committed structure is still empty
        await env.CheckIn(Alice, a);

        var veto = await env.Parts.CheckInAsync(Bob, b, "loop", null);
        Assert.Equal(OutcomeKind.Conflict, veto.Kind);
        Assert.Contains("find number 10 would now create a circular structure", veto.Errors[0], StringComparison.Ordinal);
        Assert.DoesNotContain(a, veto.Errors[0], StringComparison.Ordinal);
        // Nothing was written: B is still checked out and its working copy is intact; removing the line lets the check-in through.
        var working = (await env.Bom.GetWorkingAsync(Bob, b, null)).Value!;
        Assert.Single(working.Lines);
        Assert.Equal(1, await Count(env.Db, "plm.bom_usage"));
        await env.Bom.RemoveUsageAsync(Bob, b, working.Lines[0].Id, null);
        await env.CheckIn(Bob, b);
    }

    [Fact]
    public async Task Every_operation_is_audited_including_denials_and_failures_and_reads_log_counts_not_lines()
    {
        var env = await Setup(pg);
        var x = await env.CreatePart(Alice, "X");
        var y = await env.CreatePart(Alice, "Y", markings: Itar());
        var asm = await env.CreatePart(Alice, "Asm", "Assembly");
        await env.Checkout(Alice, asm);
        var line = await env.Add(Alice, asm, x, 2, find: 10);
        await env.Bom.AddUsageAsync(Alice, asm, new AddUsageCommand(x, 0), "corr-fail"); // failure
        await env.Bom.AddUsageAsync(Lena, asm, new AddUsageCommand(y, 1), null); // checked out by Alice: conflict
        await env.Bom.UpdateUsageAsync(Alice, asm, line.Id, new UpdateUsageCommand(3, "EA", 10), null);
        await env.Bom.RenumberAsync(Alice, asm, 10, 10, null);
        await env.Bom.GetWorkingAsync(Alice, asm, null);
        await env.Bom.RemoveUsageAsync(Alice, asm, line.Id, null);
        await env.Add(Alice, asm, x, 2);
        await env.CheckIn(Alice, asm);
        var other = await env.CreatePart(Alice, "Other", "Assembly");
        await env.Checkout(Alice, other);
        await env.Bom.CopyStructureAsync(Alice, other, asm, false, "corr-copy");
        await env.Parts.UndoCheckOutAsync(Alice, other, null);
        await env.Bom.ExplodeAsync(Alice, new BomQuery(asm, Spec: BomConfigSpec.LatestReleased, Depth: 3), "corr-explode");
        await env.Bom.SummaryAsync(Alice, new BomQuery(asm), true, null);
        await env.Bom.WhereUsedAsync(Alice, x, 2, null);
        await env.Bom.CompareAsync(Alice, asm, "A.1", "A.2", null);
        var export = (await env.Bom.ExportCsvAsync(Alice, new BomQuery(asm), "corr-export")).Value!;

        var audit = await env.Audit();
        foreach (var action in new[] { "bom.add_usage", "bom.update_usage", "bom.remove_usage", "bom.renumber", "bom.copy_structure", "bom.working_read",
                     "bom.explode", "bom.summary", "bom.where_used", "bom.compare", "bom.export" })
            Assert.Contains(audit, e => e.Action == action && e.Outcome == AuditOutcome.Success);
        Assert.Contains(audit, e => e.Action == "bom.add_usage" && e.Outcome == AuditOutcome.Failure && e.CorrelationId == "corr-fail");
        Assert.Contains(audit, e => e.Action == "bom.add_usage" && e.Outcome == AuditOutcome.Failure && e.Payload.Contains("Checked out by alice", StringComparison.Ordinal));

        // Reads record root, spec, depth and counts, never a line per row.
        var explode = Assert.Single(audit, e => e.CorrelationId == "corr-explode");
        Assert.Equal((asm, "part"), (explode.ObjectId, explode.ObjectType));
        Assert.Contains("\"spec\":\"LatestReleased\"", explode.Payload, StringComparison.Ordinal);
        Assert.Contains("\"depth\":\"3\"", explode.Payload, StringComparison.Ordinal);
        Assert.Contains("\"lines\":\"1\"", explode.Payload, StringComparison.Ordinal);
        Assert.Contains("\"unresolved\":\"1\"", explode.Payload, StringComparison.Ordinal); // nothing released: the line is flagged

        var exported = Assert.Single(audit, e => e.CorrelationId == "corr-export");
        Assert.Contains("\"lines\":\"1\"", exported.Payload, StringComparison.Ordinal);
        Assert.Contains("\"banner\":\"UNCLASSIFIED", exported.Payload, StringComparison.Ordinal);
        Assert.Contains("\"format\":\"csv\"", exported.Payload, StringComparison.Ordinal);
        Assert.Equal(1, export.Lines);

        var copy = Assert.Single(audit, e => e.CorrelationId == "corr-copy");
        Assert.Contains(asm, copy.Payload, StringComparison.Ordinal);
    }
}
