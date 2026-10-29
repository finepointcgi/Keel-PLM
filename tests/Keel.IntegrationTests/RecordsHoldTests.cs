using Keel.Audit;
using Keel.Core;
using Keel.Records;

namespace Keel.IntegrationTests;

/// <summary>Legal holds: scopes, re-evaluation, release by signature, disclosure rules and the lock shared with disposition (ADR-0041).</summary>
[Collection(PostgresCollection.Name)]
public sealed class RecordsHoldTests(PostgresFixture pg)
{
    private static HoldScope Program(string program) => new(HoldScopeKinds.Program, Program: program);

    [Fact]
    public async Task A_hold_names_objects_and_an_object_the_placer_cannot_read_is_reported_exactly_like_one_that_does_not_exist()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        var visible = await env.NewDocAsync("Visible");
        var hidden = await env.NewDocAsync("Hidden", RecordsEnv.Itar("ORCA"));

        // Eve is a records manager outside program ORCA.
        var missing = await env.Holds.PlaceAsync(RecordsEnv.Eve, new PlaceHoldCommand("Matter", "Why", new HoldScope(HoldScopeKinds.Objects, [new HoldObjectRef("document", hidden)])), null);
        var nonexistent = await env.Holds.PlaceAsync(RecordsEnv.Eve, new PlaceHoldCommand("Matter", "Why", new HoldScope(HoldScopeKinds.Objects, [new HoldObjectRef("document", "D-999999")])), null);
        Assert.Equal(OutcomeKind.Invalid, missing.Kind);
        Assert.Equal(nonexistent.Errors.Select(e => e.Replace("D-999999", "X", StringComparison.Ordinal)), missing.Errors.Select(e => e.Replace(hidden, "X", StringComparison.Ordinal)));
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM rec.hold"));

        var ok = await env.Holds.PlaceAsync(RecordsEnv.Rex, new PlaceHoldCommand("Matter", "Litigation", new HoldScope(HoldScopeKinds.Objects, [new HoldObjectRef("document", visible), new HoldObjectRef("document", visible)])), null);
        Assert.True(ok.IsOk, string.Join("; ", ok.Errors));
        Assert.Equal(1, ok.Value!.ReadableMembers);
        Assert.Matches("^LH-\\d{6}$", ok.Value.Number);
        Assert.True((await env.Holds.StatusOfAsync(RecordsEnv.Rex, "document", visible))!.Held);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Holds.PlaceAsync(RecordsEnv.Alice, new PlaceHoldCommand("x", "y", new HoldScope(HoldScopeKinds.Objects, [new HoldObjectRef("document", visible)])), null)).Kind);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "records.hold_place" && e.Outcome == AuditOutcome.Success && e.ObjectId == ok.Value.Number);
    }

    [Fact]
    public async Task A_program_hold_covers_what_its_placer_cannot_see_and_shows_each_reader_only_what_that_reader_can_see()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        var open = await env.NewDocAsync("Open doc", RecordsEnv.Open);
        var orca = await env.NewDocAsync("Orca doc", RecordsEnv.Itar("ORCA"));
        var kraken = await env.NewDocAsync("Kraken doc", RecordsEnv.Itar("KRAKEN"), by: RecordsEnv.Kay);

        // Eve (no programs) places a hold on program KRAKEN: it must protect the KRAKEN document although she cannot see it.
        var placed = await env.Holds.PlaceAsync(RecordsEnv.Eve, new PlaceHoldCommand("Kraken matter", "Investigation", Program("KRAKEN")), null);
        Assert.True(placed.IsOk, string.Join("; ", placed.Errors));
        Assert.Equal(0, placed.Value!.ReadableMembers); // she is told nothing about what it covers
        Assert.Empty((await env.Holds.MembersAsync(RecordsEnv.Eve, placed.Value.Number, 100, 0, null)).Value!);
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM rec.hold_member WHERE hold_id = (SELECT id FROM rec.hold WHERE number = @n)", ("n", placed.Value.Number)));
        Assert.True(await env.ScalarAsync("SELECT CASE WHEN rec.is_held('document', (SELECT id FROM plm.document_master WHERE number = @n)) THEN 1 ELSE 0 END", ("n", kraken)) == 1);
        Assert.Null(await env.Holds.StatusOfAsync(RecordsEnv.Eve, "document", kraken)); // existence of a held hidden object is not disclosed
        Assert.Null(await env.Holds.StatusOfAsync(RecordsEnv.Eve, "document", "D-999999"));
        Assert.Equal(0, (await env.Holds.GetAsync(RecordsEnv.Eve, placed.Value.Number, null)).Value!.ReadableMembers);

        // Rex (ORCA) cannot see it either; a KRAKEN records manager can.
        Assert.Equal(0, (await env.Holds.GetAsync(RecordsEnv.Rex, placed.Value.Number, null)).Value!.ReadableMembers);
        var kayRecords = WorkflowEnv.User("kayrec", [Roles.Viewer, Roles.Author, Roles.RecordsManager], programs: ["KRAKEN"]);
        Assert.Equal(1, (await env.Holds.GetAsync(kayRecords, placed.Value.Number, null)).Value!.ReadableMembers);
        Assert.Equal(kraken, Assert.Single((await env.Holds.MembersAsync(kayRecords, placed.Value.Number, 100, 0, null)).Value!).Number);

        // The queue of a reader never counts or names a held object the reader cannot see.
        await env.PublishScheduleAsync(RecordsEnv.DocClass("ALL", months: 0));
        foreach (var d in new[] { open, orca }) await env.ReleaseDocAsync(d);
        await env.ClassifyAsync();
        var queue = (await env.Disposition.EligibleAsync(RecordsEnv.Rex, new EligibleQuery(), null)).Value!;
        Assert.Equal(0, queue.HeldReadable);
        Assert.Equal(new[] { open, orca }.Order(StringComparer.Ordinal), queue.Items.Select(i => i.Number).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_program_or_query_hold_is_evaluated_again_and_gains_objects_created_after_it_was_placed()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        var first = await env.NewDocAsync("First", RecordsEnv.Itar("ORCA"), type: "Drawing");
        var hold = (await env.Holds.PlaceAsync(RecordsEnv.Rex, new PlaceHoldCommand("Orca", "Why", Program("ORCA")), null)).Value!;
        var query = (await env.Holds.PlaceAsync(RecordsEnv.Rex, new PlaceHoldCommand("Drawings", "Why", new HoldScope(HoldScopeKinds.Query, Query: new HoldQuery(Kind: "document", ObjectType: "Drawing"))), null)).Value!;
        Assert.Equal(1, hold.ReadableMembers);
        Assert.Equal(1, query.ReadableMembers);

        var later = await env.NewDocAsync("Later", RecordsEnv.Itar("ORCA"), type: "Drawing");
        var spec = await env.NewDocAsync("A spec", RecordsEnv.Itar("ORCA"));
        Assert.False((await env.Holds.StatusOfAsync(RecordsEnv.Rex, "document", later))!.Held); // not covered until the hold is evaluated again
        Assert.Equal(2, (await env.Holds.ReevaluateAsync(RecordsEnv.Rex, hold.Number, null)).Value); // later and spec
        Assert.Equal(1, (await env.Holds.ReevaluateAsync(RecordsEnv.Rex, query.Number, null)).Value); // only the drawing
        Assert.Equal(0, (await env.Holds.ReevaluateAsync(RecordsEnv.Rex, query.Number, null)).Value); // idempotent
        Assert.True((await env.Holds.StatusOfAsync(RecordsEnv.Rex, "document", later))!.Held);
        Assert.True((await env.Holds.StatusOfAsync(RecordsEnv.Rex, "document", spec))!.Held);
        Assert.Equal(3, (await env.Holds.GetAsync(RecordsEnv.Rex, hold.Number, null)).Value!.ReadableMembers);
        _ = first;

        // Execution evaluates every active hold again by itself, so no manual step can be forgotten.
        await env.PublishScheduleAsync(RecordsEnv.DocClass("ALL", months: 0));
        await env.ReleaseDocAsync(spec);
        var sneaky = await env.NewDocAsync("Created after the request", RecordsEnv.Itar("ORCA"));
        await env.ReleaseDocAsync(sneaky);
        await env.Holds.ReevaluateAsync(RecordsEnv.Rex, hold.Number, null);
        await env.ClassifyAsync();
        Assert.Empty((await env.Disposition.EligibleAsync(RecordsEnv.Rex, new EligibleQuery(), null)).Value!.Items); // everything of ORCA is held
        var query2 = await env.Holds.PlaceAsync(RecordsEnv.Rex, new PlaceHoldCommand("Bad", "x", new HoldScope(HoldScopeKinds.Query, Query: new HoldQuery())), null);
        Assert.Equal(OutcomeKind.Invalid, query2.Kind);
    }

    [Fact]
    public async Task A_hold_is_released_only_by_signature_and_keeps_blocking_until_then()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        await env.PublishScheduleAsync(RecordsEnv.DocClass("ALL", months: 0));
        var doc = await env.ReleasedDocAsync("Held doc");
        var hold = await env.PlaceObjectHoldAsync("document", doc);
        await env.ClassifyAsync();
        Assert.Empty((await env.Disposition.EligibleAsync(RecordsEnv.Rex, new EligibleQuery(), null)).Value!.Items);
        Assert.Equal(1, (await env.Disposition.EligibleAsync(RecordsEnv.Rex, new EligibleQuery(), null)).Value!.HeldReadable);

        // A release request is a review; while it runs the hold still holds. The submitter cannot sign it.
        var started = await env.Flow.StartAsync(RecordsEnv.Rex, new Keel.Workflow.StartReviewCommand("legal_hold", hold.Number, new Dictionary<string, string[]> { ["approvers"] = ["bob"] }), null);
        Assert.True(started.IsOk, string.Join("; ", started.Errors));
        Assert.True((await env.Holds.GetAsync(RecordsEnv.Rex, hold.Number, null)).Value!.InRelease);
        Assert.True((await env.Holds.StatusOfAsync(RecordsEnv.Rex, "document", doc))!.Held);
        Assert.Equal(OutcomeKind.Conflict, (await env.Flow.StartAsync(RecordsEnv.Rhea, new Keel.Workflow.StartReviewCommand("legal_hold", hold.Number, new Dictionary<string, string[]> { ["approvers"] = ["carol"] }), null)).Kind);
        // The description the approver signs says nothing about how many objects the hold covers.
        await using (var conn = await env.Ds.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            var content = await new HoldWorkflowGateway(env.Audit).LoadIterationContentAsync(conn, tx, await HoldIdAsync(env, hold.Number), CancellationToken.None);
            Assert.NotNull(content);
            Assert.DoesNotContain("1 object", content!.Description, StringComparison.Ordinal);
            Assert.Contains("membershipSha256", content.AttributesJson, StringComparison.Ordinal);
        }
        var signed = await env.SignAsync(RecordsEnv.Bob, WorkflowEnv.TaskOf(started.Value!, "bob"), "Approved");
        Assert.Equal("Approved", signed.Status);

        var after = (await env.Holds.GetAsync(RecordsEnv.Rex, hold.Number, null)).Value!;
        Assert.Equal("released", after.Status);
        Assert.Equal("bob", after.ReleasedBy);
        Assert.False((await env.Holds.StatusOfAsync(RecordsEnv.Rex, "document", doc))!.Held);
        await env.ClassifyAsync();
        Assert.Equal(doc, Assert.Single((await env.Disposition.EligibleAsync(RecordsEnv.Rex, new EligibleQuery(), null)).Value!.Items).Number);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "records.hold_release" && e.ObjectId == hold.Number);

        // A released hold is final, and its membership is history that stays.
        Assert.Contains("final", await env.AppSqlAsync("UPDATE rec.hold SET in_release = true WHERE number = @n", ("n", hold.Number)) ?? "ran", StringComparison.Ordinal);
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM rec.hold_member"));
        Assert.NotNull(await env.AppSqlAsync("DELETE FROM rec.hold_member"));
    }

    private static async Task<Guid> HoldIdAsync(RecordsEnv env, string number)
    {
        await using var conn = await env.Ds.OpenConnectionAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT id FROM rec.hold WHERE number = @n", conn);
        cmd.Parameters.AddWithValue("n", number);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task A_hold_cannot_be_edited_or_deleted_and_is_visible_to_records_roles_only()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        var doc = await env.NewDocAsync("Doc");
        var hold = await env.PlaceObjectHoldAsync("document", doc);
        Assert.Contains("identity and scope", await env.AppSqlAsync("UPDATE rec.hold SET name = 'renamed' WHERE number = @n", ("n", hold.Number)) ?? "ran", StringComparison.Ordinal);
        Assert.NotNull(await env.AppSqlAsync("DELETE FROM rec.hold"));
        Assert.True((await env.Holds.GetAsync(RecordsEnv.Audrey, hold.Number, null)).IsOk); // auditors read
        Assert.Equal(OutcomeKind.Forbidden, (await env.Holds.GetAsync(RecordsEnv.Alice, hold.Number, null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Holds.MembersAsync(RecordsEnv.Bob, hold.Number, 10, 0, null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Holds.ReevaluateAsync(RecordsEnv.Audrey, hold.Number, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Holds.GetAsync(RecordsEnv.Rex, "LH-999999", null)).Kind);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "records.hold_read" && e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task A_hold_and_a_disposition_never_pass_each_other_whichever_commits_first()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        await env.PublishScheduleAsync(RecordsEnv.DocClass("ALL", months: 0));
        const int Rounds = 8;
        var docs = new List<string>();
        var requests = new List<string>();
        for (var i = 0; i < Rounds; i++)
        {
            var d = await env.ReleasedDocAsync("Race " + i);
            docs.Add(d);
        }
        await env.ClassifyAsync();
        foreach (var d in docs) requests.Add(await env.ApprovedRequestAsync(("document", d)));
        // Place the holds and execute the requests at the same time.
        var tasks = new List<Task>();
        for (var i = 0; i < Rounds; i++)
        {
            var d = docs[i]; var r = requests[i];
            tasks.Add(Task.Run(async () => await env.PlaceObjectHoldAsync("document", d)));
            tasks.Add(Task.Run(async () => Assert.True((await env.Disposition.ExecuteAsync(RecordsEnv.Rex, r, null)).IsOk)));
        }
        await Task.WhenAll(tasks);
        // Invariant: an object is never tombstoned AFTER a hold covering it was placed (and every object is either tombstoned or skipped as held).
        Assert.Equal(0, await env.ScalarAsync(
            "SELECT count(*) FROM rec.tombstone t JOIN rec.hold_member m ON m.object_kind = t.object_kind AND m.object_id = t.object_id JOIN rec.hold h ON h.id = m.hold_id WHERE h.placed_at < t.tombstoned_at"));
        Assert.Equal(Rounds, await env.ScalarAsync("SELECT count(*) FROM rec.disposition_item WHERE result IN ('destroyed', 'skipped')"));
        Assert.Equal(await env.ScalarAsync("SELECT count(*) FROM rec.disposition_item WHERE result = 'skipped' AND result_code = 'held'"), Rounds - await env.ScalarAsync("SELECT count(*) FROM rec.tombstone"));
        Assert.True((await env.VerifyAuditChainAsync()).Valid);
    }
}
