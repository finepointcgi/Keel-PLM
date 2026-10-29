using Keel.Audit;
using Keel.Change;
using Keel.Core;
using Keel.Integration;
using Keel.Notifications;
using Keel.Workflow;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Notifications (UX-4, ADR-0030) against real Postgres: pointer-only rows, rendering after a read check at view time, generation inside the owning transactions and by the
/// outbox fan-out with a per-recipient check, no self-notification, collapsing, caps, retention and strict per-user ownership.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class NotificationTests(PostgresFixture pg)
{
    private static readonly Subject Alice = AwarenessEnv.Alice;
    private static readonly Subject Rita = AwarenessEnv.Rita;
    private static readonly Subject Bob = AwarenessEnv.Bob;
    private static readonly Subject Carol = AwarenessEnv.Carol;
    private static readonly Subject Lena = AwarenessEnv.Lena;
    private static readonly Subject Eve = AwarenessEnv.Eve;
    private static readonly Subject Kim = AwarenessEnv.Kim;
    private static readonly Subject Dan = AwarenessEnv.Dan;
    private static readonly Subject Ada = AwarenessEnv.Ada;

    // ---------------------------------------------------------------- generation at the owning transaction

    [Fact]
    public async Task A_review_request_notifies_the_reviewer_with_pointers_only_and_the_text_is_rendered_from_the_live_object()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync("Bracket, sensor", description: "Synthetic bracket, secret detail");
        var started = await env.StartReviewAsync(part);

        var rows = await env.RowsOfAsync(Rita);
        var row = Assert.Single(rows);
        Assert.Equal((NotificationKindCodes.ReviewRequested, "part", part, Alice.Id), (row.Kind, row.ObjectType, row.ObjectNumber, row.Actor));
        Assert.Equal(started.Tasks.Single(t => t.AssigneeUsername == "rita").Id, row.RefId); // the link lands on the task
        Assert.Empty(await env.RowsOfAsync(Bob)); // the approvers' tasks do not exist yet
        Assert.Empty(await env.RowsOfAsync(Alice)); // the submitter is not told about her own action

        // The row holds no name, description, markings or sentence: not even a fragment of them anywhere in the schema.
        var dump = await env.DumpNtfTextAsync();
        foreach (var marked in new[] { "Bracket", "sensor", "secret detail", "Synthetic", "ITAR", "XI(d)", "ORCA", "asked you to review" })
            Assert.DoesNotContain(marked, dump, StringComparison.OrdinalIgnoreCase);

        // At view time the text is built from the live object.
        var page = await env.Center.ListAsync(Rita, NotificationFilter.All, 0, 10);
        var item = Assert.Single(page.Items);
        Assert.False(item.Restricted);
        Assert.Equal((part, "Bracket, sensor", "Alice Example asked you to review"), (item.Number, item.Name, item.Text));
        Assert.Equal($"tasks/{row.RefId:D}", item.Href);
        Assert.Contains("ITAR", item.Banner, StringComparison.Ordinal);
        Assert.Equal(1, page.Unread);
    }

    [Fact]
    public async Task Approval_tasks_decisions_and_the_actors_own_actions_notify_the_right_people_only()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        var v = await env.StartReviewAsync(part);
        v = await env.SignAsync(Rita, v.Tasks.Single(t => t.Status == "Open").Id, "Reviewed");
        // Reviewer signed: approvers are asked (an approval task is an assignment, not a review request).
        Assert.Equal([NotificationKindCodes.TaskAssigned], (await env.RowsOfAsync(Bob)).Select(r => r.Kind));
        Assert.Equal([NotificationKindCodes.TaskAssigned], (await env.RowsOfAsync(Carol)).Select(r => r.Kind));
        v = await env.SignAsync(Bob, WorkflowEnv.TaskOf(v, "bob"), "Approved");
        v = await env.SignAsync(Carol, WorkflowEnv.TaskOf(v, "carol"), "Approved");

        // The submitter hears the decision, once. The signers hear nothing about their own signatures.
        var alice = await env.RowsOfAsync(Alice);
        var approved = Assert.Single(alice, r => r.Kind == NotificationKindCodes.ReviewApproved);
        Assert.Equal((Carol.Id, v.Id), (approved.Actor, approved.RefId));
        Assert.Equal(1, alice.Count(r => r.Kind is NotificationKindCodes.ReviewApproved or NotificationKindCodes.ReviewRejected));
        Assert.DoesNotContain(await env.RowsOfAsync(Bob), r => r.Actor == Bob.Id);
        Assert.DoesNotContain(await env.RowsOfAsync(Carol), r => r.Actor == Carol.Id);
        var shown = (await env.Center.ListAsync(Alice, NotificationFilter.All, 0, 10)).Items.Single(i => i.Text.StartsWith("Approved after review", StringComparison.Ordinal));
        Assert.Equal("Approved after review (last signature by Carol Example)", shown.Text); // the signer's name is shown: the viewer may see the signatures of the object
    }

    [Fact]
    public async Task A_rejection_notifies_the_submitter()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        var v = await env.StartReviewAsync(part);
        v = await env.SignAsync(Rita, v.Tasks.Single(t => t.Status == "Open").Id, "Rejected", "Wrong material");
        Assert.Equal("Rejected", v.Status);
        var row = Assert.Single(await env.RowsOfAsync(Alice, NotificationKindCodes.ReviewRejected));
        Assert.Equal(Rita.Id, row.Actor);
        Assert.Equal("Rejected in review by Rita Example", (await env.Center.ListAsync(Alice, NotificationFilter.Tasks, 0, 10)).Items.Single().Text);
    }

    [Fact]
    public async Task A_change_task_and_the_decision_on_a_change_notify_the_assignee_and_the_requester()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var ecr = await env.Change.CreateAsync(Alice, new CreateChangeCommand(ChangeTypes.Ecr, "Rework bracket", "Synthetic", "Medium", "Synthetic reason", AwarenessEnv.Itar(), ChangeClasses.II, null), null);
        Assert.True(ecr.IsOk, string.Join("; ", ecr.Errors));
        var ecn = await env.Change.CreateAsync(Alice, new CreateChangeCommand(ChangeTypes.Ecn, "Revise bracket", "Synthetic", "Medium", "Synthetic reason", AwarenessEnv.Itar(), ChangeClasses.II, null), null);
        Assert.True(ecn.IsOk, string.Join("; ", ecn.Errors));
        var task = await env.Change.CreateTaskAsync(Alice, ecn.Value!.Number, new CreateTaskCommand("Update the drawing", "Synthetic", "dan", null), null);
        Assert.True(task.IsOk, string.Join("; ", task.Errors));

        var row = Assert.Single(await env.RowsOfAsync(Dan));
        Assert.Equal((NotificationKindCodes.ChangeTaskAssigned, "change", ecn.Value.Number, Alice.Id), (row.Kind, row.ObjectType, row.ObjectNumber, row.Actor));
        Assert.Equal($"changes/{ecn.Value.Number}#tasks", (await env.Center.ListAsync(Dan, NotificationFilter.Tasks, 0, 10)).Items.Single().Href);

        // A change request is reviewed by the CCB; its approval is reported to the requester (alice).
        var review = await env.Flow.StartAsync(Alice, new StartReviewCommand("ecr", ecr.Value!.Number, new Dictionary<string, string[]> { ["ccb"] = ["bob", "carol"] }), null);
        Assert.True(review.IsOk, string.Join("; ", review.Errors));
        Assert.Equal(2, (await env.RowsAsync()).Count(r => r.Kind == NotificationKindCodes.TaskAssigned && r.ObjectNumber == ecr.Value.Number)); // both CCB members are asked
        var view = review.Value!;
        view = await env.SignAsync(Bob, WorkflowEnv.TaskOf(view, "bob"), "Approved");
        Assert.DoesNotContain(await env.RowsOfAsync(Alice), r => r.Kind == NotificationKindCodes.ChangeDecisionApproved); // not decided yet
        view = await env.SignAsync(Carol, WorkflowEnv.TaskOf(view, "carol"), "Approved");
        Assert.Equal("Approved", view.Status);
        var decision = Assert.Single(await env.RowsOfAsync(Alice, NotificationKindCodes.ChangeDecisionApproved));
        Assert.Equal((ecr.Value.Number, Carol.Id), (decision.ObjectNumber, decision.Actor));
        // The workflow also tells the submitter (she is the requester too); both rows exist and each reads as what it is.
        Assert.Contains(await env.RowsOfAsync(Alice), r => r.Kind == NotificationKindCodes.ReviewApproved && r.ObjectNumber == ecr.Value.Number);
    }

    [Fact]
    public async Task A_step_that_rolls_back_leaves_no_notification()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        // Lena cannot read an ITAR part (not a U.S. person): the whole start is refused and rolled back.
        var refused = await env.Flow.StartAsync(Alice, WorkflowEnv.Start(part, "rita", "bob", "lena"), "corr");
        Assert.False(refused.IsOk);
        Assert.Empty(await env.RowsAsync());
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM integration.outbox WHERE event_type = 'part.released'"));
    }

    [Fact]
    public async Task The_hub_is_told_when_a_notification_is_written_for_a_user()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var heard = 0;
        using var subscription = env.Hub.Subscribe(Rita.Id, () => Interlocked.Increment(ref heard));
        using var other = env.Hub.Subscribe(Bob.Id, () => Assert.Fail("bob was not notified"));
        await env.StartReviewAsync(await env.NewPartAsync());
        Assert.Equal(1, heard);
    }

    // ---------------------------------------------------------------- watching, collapsing, preferences

    [Fact]
    public async Task Watchers_hear_about_new_versions_collapsed_into_one_row_until_they_read_it()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync(); // alice created it: she watches it automatically
        Assert.Equal(WatchLevel.All, await env.Watches.GetAsync(Alice, "part", part));
        Assert.True((await env.Watches.SetAsync(Bob, "part", part, WatchLevel.All, null)).IsOk);

        for (var i = 1; i <= 3; i++) await env.CheckedInEditAsync(Alice, part, $"revision {i}", $"edit {i}");

        var bobRows = await env.RowsOfAsync(Bob);
        var changed = Assert.Single(bobRows);
        Assert.Equal((NotificationKindCodes.WatchedChanged, 3, Alice.Id), (changed.Kind, changed.Occurrences, changed.Actor)); // three check-ins, one row with a counter
        Assert.Empty(await env.RowsOfAsync(Alice)); // never told about her own action
        var item = Assert.Single((await env.Center.ListAsync(Bob, NotificationFilter.Watching, 0, 10)).Items);
        Assert.Equal("Changed (by Alice Example) (3 times)", item.Text + (item.Occurrences > 1 ? $" ({item.Occurrences} times)" : ""));

        // Once read, the next change is news again: a new row.
        await env.Center.MarkReadAsync(Bob, changed.Id);
        await env.CheckedInEditAsync(Alice, part, "revision 4");
        Assert.Equal(2, (await env.RowsOfAsync(Bob)).Count);

        // After the window, an unread row is not extended either.
        await env.CheckedInEditAsync(Alice, part, "revision 5"); // collapses into the unread row of revision 4
        Assert.Equal(2, (await env.RowsOfAsync(Bob)).Count);
        env.Clock.Advance(TimeSpan.FromMinutes(6));
        await env.CheckedInEditAsync(Alice, part, "revision 6");
        Assert.Equal(3, (await env.RowsOfAsync(Bob)).Count);
    }

    [Fact]
    public async Task A_state_change_reaches_watchers_and_a_decisions_only_watcher_does_not_get_it()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        Assert.True((await env.Watches.SetAsync(Bob, "part", part, WatchLevel.All, null)).IsOk);
        Assert.True((await env.Watches.SetAsync(Dan, "part", part, WatchLevel.Decisions, null)).IsOk);

        await env.StartReviewAsync(part); // In Work -> Under Review through the workflow: not a manual transition, so no watcher notification of its own
        await env.Parts.SetStateAsync(Ada, part, "Obsolete", "synthetic", adminOverride: true, null); // the override goes to Obsolete: reported through the outbox, not here
        Assert.DoesNotContain(await env.RowsAsync(), r => r.Kind == NotificationKindCodes.WatchedState);

        var env2 = await AwarenessEnv.CreateAsync(pg);
        var p2 = await env2.NewPartAsync();
        Assert.True((await env2.Watches.SetAsync(Bob, "part", p2, WatchLevel.All, null)).IsOk);
        Assert.True((await env2.Watches.SetAsync(Dan, "part", p2, WatchLevel.Decisions, null)).IsOk);
        await env2.Parts.SetStateAsync(Ada, p2, "UnderReview", "synthetic", adminOverride: true, null);
        Assert.Equal([NotificationKindCodes.WatchedState], (await env2.RowsOfAsync(Bob)).Select(r => r.Kind));
        Assert.Empty(await env2.RowsOfAsync(Dan)); // "decisions only" does not include plain state changes
    }

    [Fact]
    public async Task Preferences_switch_kinds_off_except_the_ones_that_need_action_and_can_include_my_own_actions()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        Assert.True((await env.Watches.SetAsync(Bob, "part", part, WatchLevel.All, null)).IsOk);

        Assert.True(await env.Center.SetKindPreferenceAsync(Bob, NotificationKindCodes.WatchedChanged, inApp: false, notifyOwn: false));
        await env.CheckedInEditAsync(Alice, part, "one");
        Assert.Empty(await env.RowsOfAsync(Bob)); // switched off

        // Mentions and tasks cannot be switched off (the call is refused and nothing is stored).
        Assert.False(await env.Center.SetKindPreferenceAsync(Bob, NotificationKindCodes.CommentMention, inApp: false, notifyOwn: false));
        Assert.False(await env.Center.SetKindPreferenceAsync(Bob, NotificationKindCodes.ReviewRequested, inApp: false, notifyOwn: false));
        Assert.False(await env.Center.SetKindPreferenceAsync(Bob, "no_such_kind", inApp: true, notifyOwn: false));
        var prefs = await env.Center.GetPreferencesAsync(Bob);
        Assert.True(prefs.Kinds.Single(k => k.Kind.Code == NotificationKindCodes.CommentMention).InApp);
        Assert.False(prefs.Kinds.Single(k => k.Kind.Code == NotificationKindCodes.WatchedChanged).InApp);

        // Alice asks to be told about her own check-ins too.
        Assert.True(await env.Center.SetKindPreferenceAsync(Alice, NotificationKindCodes.WatchedChanged, inApp: true, notifyOwn: true));
        await env.CheckedInEditAsync(Alice, part, "two");
        Assert.Single(await env.RowsOfAsync(Alice, NotificationKindCodes.WatchedChanged));

        // Back to the defaults removes the stored row.
        Assert.True(await env.Center.SetKindPreferenceAsync(Alice, NotificationKindCodes.WatchedChanged, inApp: true, notifyOwn: false));
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM ntf.kind_preference WHERE user_sub = @u", ("u", Alice.Id)));
    }

    [Fact]
    public async Task Auto_watch_follows_creating_checking_out_signing_and_commenting_and_can_be_switched_off()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync(author: Alice);
        Assert.Equal(WatchLevel.All, await env.Watches.GetAsync(Alice, "part", part));

        var v = await env.StartReviewAsync(part);
        Assert.Null(await env.Watches.GetAsync(Rita, "part", part)); // being asked to review is not watching
        await env.SignAsync(Rita, v.Tasks.Single(t => t.Status == "Open").Id, "Reviewed");
        Assert.Equal(WatchLevel.All, await env.Watches.GetAsync(Rita, "part", part)); // signing is

        Assert.Null(await env.Watches.GetAsync(Dan, "part", part));
        Assert.True((await env.Comments.PostAsync(Dan, "part", part, "A thought", null, null)).IsOk);
        Assert.Equal(WatchLevel.All, await env.Watches.GetAsync(Dan, "part", part)); // commenting is

        await env.Center.SetAutoWatchAsync(Kim, false);
        Assert.False((await env.Center.GetPreferencesAsync(Kim)).AutoWatch);
        var kimsPart = await env.NewPartAsync("Kim's part", author: Kim);
        Assert.Null(await env.Watches.GetAsync(Kim, "part", kimsPart));
        Assert.True((await env.Center.GetPreferencesAsync(Alice)).AutoWatch); // the default is on
    }

    // ---------------------------------------------------------------- outbox fan-out

    [Fact]
    public async Task The_fan_out_notifies_only_watchers_who_could_read_the_object_by_their_directory_snapshot()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync(); // ITAR, program ORCA
        // Bob, Dan and Carol may read it. Lena is not a U.S. person, Eve has no program, Kim LEFT the program since she started watching.
        foreach (var w in new[] { Bob, Dan, Carol, Lena, Eve, Kim }) await env.InsertWatchAsync(w, "part", part);
        await env.InsertWatchAsync(Rita, "part", part, "decisions"); // decisions only: a release is a decision
        await env.Directory.UpsertAsync(Kim with { Programs = [] }); // the snapshot the fan-out reads now says Kim has no program

        await env.ReleasePartAsync(part); // outbox: part.released, signed last by carol
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM integration.outbox WHERE event_type = 'part.released'"));
        Assert.Equal(1, await env.FanOutAsync() > 0 ? 1 : 0);

        var released = (await env.RowsAsync()).Where(r => r.Kind == NotificationKindCodes.WatchedReleased).ToList();
        Assert.Equal([Alice.Id, Bob.Id, Dan.Id, Rita.Id], [.. released.Select(r => r.Recipient).Order()]); // alice created it and watches it automatically
        Assert.All(released, r => Assert.Equal(("part", part, Carol.Id), (r.ObjectType, r.ObjectNumber, r.Actor)));
        Assert.DoesNotContain(released, r => r.Recipient == Carol.Id); // the last signer is not told about her own release
        foreach (var excluded in new[] { Lena, Eve, Kim }) Assert.Empty(await env.RowsOfAsync(excluded, NotificationKindCodes.WatchedReleased));

        // Idempotent: processing again writes nothing, and the cursor is past the event.
        Assert.Equal(0, await env.FanOutAsync());
        Assert.Equal(released.Count, (await env.RowsAsync()).Count(r => r.Kind == NotificationKindCodes.WatchedReleased));
        var rowsWithEvent = await env.ScalarAsync("SELECT count(*) FROM ntf.notification WHERE kind = 'watched_released' AND event_id IS NOT NULL");
        Assert.Equal(released.Count, rowsWithEvent);
    }

    [Fact]
    public async Task The_fan_out_ignores_uncommitted_events_and_never_sees_a_rolled_back_one()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        await env.InsertWatchAsync(Bob, "part", part);
        var markings = AwarenessEnv.Itar();

        async Task EmitAsync(NpgsqlConnection conn, NpgsqlTransaction tx) => await env.Outbox.AppendAsync(conn, tx,
            new DomainEvent(EventTypes.PartReleased, "part", Guid.NewGuid(), part, "A", markings, new System.Text.Json.Nodes.JsonObject { ["partType"] = "Component" }, DateTimeOffset.UtcNow, "alice"), CancellationToken.None);

        // An event inside an open transaction is invisible, and stays invisible when that transaction rolls back.
        await using (var conn = await env.Ds.OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await EmitAsync(conn, tx);
            Assert.Equal(0, await env.Fan.ProcessBatchAsync(CancellationToken.None));
            await tx.RollbackAsync();
        }
        Assert.Equal(0, await env.FanOutAsync());
        Assert.Empty(await env.RowsAsync());

        // A committed one is delivered once.
        await using (var conn = await env.Ds.OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await EmitAsync(conn, tx);
            await tx.CommitAsync();
        }
        Assert.Equal(1, await env.FanOutAsync());
        Assert.Single(await env.RowsOfAsync(Bob, NotificationKindCodes.WatchedReleased));
    }

    [Fact]
    public async Task A_baseline_or_manufacturing_bom_released_for_a_part_tells_the_parts_watchers()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        await env.InsertWatchAsync(Bob, "part", part);
        await using (var conn = await env.Ds.OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await env.Outbox.AppendAsync(conn, tx, new DomainEvent(EventTypes.MbomReleased, "mbom", Guid.NewGuid(), "MB-000001", "A", AwarenessEnv.Itar(),
                new System.Text.Json.Nodes.JsonObject { ["part"] = new System.Text.Json.Nodes.JsonObject { ["number"] = part } }, DateTimeOffset.UtcNow, "alice"), CancellationToken.None);
            await tx.CommitAsync();
        }
        await env.FanOutAsync();
        var row = Assert.Single(await env.RowsOfAsync(Bob));
        Assert.Equal((NotificationKindCodes.WatchedEstablished, "part", part), (row.Kind, row.ObjectType, row.ObjectNumber));
    }

    // ---------------------------------------------------------------- render-time re-authorization

    [Fact]
    public async Task When_access_is_lost_the_row_becomes_neutral_and_nothing_about_the_object_leaks()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync("Bracket, secret name");
        await env.StartReviewAsync(part); // rita is asked to review
        Assert.Equal(1, await env.Center.CountUnreadAsync(Rita));
        Assert.Equal(part, (await env.Center.ListAsync(Rita, NotificationFilter.All, 0, 10)).Items.Single().Number);

        // The markings are raised to another program rita does not belong to.
        await env.ExecuteAsync("UPDATE plm.part_master SET program = 'KRAKEN' WHERE number = @n", ("n", part));

        var page = await env.Center.ListAsync(Rita, NotificationFilter.All, 0, 10);
        var item = Assert.Single(page.Items);
        Assert.True(item.Restricted);
        Assert.Equal("A notification you can no longer view", item.Text);
        Assert.Null(item.Number);
        Assert.Null(item.Name);
        Assert.Null(item.Href);
        Assert.Null(item.Banner);
        Assert.Null(item.Markings);
        Assert.Null(item.ActorName);
        Assert.Null(item.Group);
        // Nothing the service returned mentions the part, its name, the kind, the actor or the program.
        var serialized = AwarenessEnv.Json(page);
        foreach (var leak in new[] { part, "Bracket", "secret", "KRAKEN", "ORCA", "review", "Alice", "tasks/" }) Assert.DoesNotContain(leak, serialized, StringComparison.OrdinalIgnoreCase);
        // The row still counts once as unread, and the kind tabs (which would reveal the kind) leave it out.
        Assert.Equal(1, page.Unread);
        Assert.Equal(1, await env.Center.CountUnreadAsync(Rita));
        Assert.Empty((await env.Center.ListAsync(Rita, NotificationFilter.Tasks, 0, 10)).Items);
        Assert.Single((await env.Center.ListAsync(Rita, NotificationFilter.Unread, 0, 10)).Items);

        // It can be marked read (the count drops) and a user who regains access sees the real row again.
        Assert.True(await env.Center.MarkReadAsync(Rita, item.Id));
        Assert.Equal(0, await env.Center.CountUnreadAsync(Rita));
        await env.ExecuteAsync("UPDATE plm.part_master SET program = 'ORCA' WHERE number = @n", ("n", part));
        Assert.Equal(part, (await env.Center.ListAsync(Rita, NotificationFilter.All, 0, 10)).Items.Single().Number);
    }

    [Fact]
    public async Task A_notification_for_a_deleted_or_unknown_object_is_neutral_too()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        await env.ExecuteAsync("INSERT INTO ntf.notification (id, recipient_sub, kind, object_type, object_number, created_at) VALUES (@id, @u, 'watched_changed', 'part', 'P-999999', now())",
            ("id", Guid.CreateVersion7()), ("u", Rita.Id));
        var item = Assert.Single((await env.Center.ListAsync(Rita, NotificationFilter.All, 0, 10)).Items);
        Assert.True(item.Restricted);
        Assert.DoesNotContain("P-999999", AwarenessEnv.Json(item), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_listing_is_audited_by_numbers_and_the_audit_event_carries_no_names()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync("Bracket, audited name");
        await env.StartReviewAsync(part);
        await env.Center.ListAsync(Rita, NotificationFilter.All, 0, 10);
        var read = Assert.Single(await env.AuditAsync("workspace.read"), a => a.ObjectId == "notifications");
        Assert.Contains(part, read.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("audited name", read.Payload, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- isolation

    [Fact]
    public async Task No_service_method_reaches_another_users_notifications_watches_or_preferences()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        await env.StartReviewAsync(part); // rita has one unread notification
        var ritasRow = Assert.Single(await env.RowsOfAsync(Rita));
        Assert.True((await env.Watches.SetAsync(Rita, "part", part, WatchLevel.All, null)).IsOk);
        var ritasWatch = Assert.Single(await env.Watches.ListAsync(Rita));
        await env.Center.SetKindPreferenceAsync(Rita, NotificationKindCodes.WatchedChanged, inApp: false, notifyOwn: false);
        await env.Center.SetAutoWatchAsync(Rita, false);

        // Bob tries everything with rita's ids.
        Assert.Equal(0, await env.Center.CountUnreadAsync(Bob));
        Assert.Empty((await env.Center.ListAsync(Bob, NotificationFilter.All, 0, 50)).Items);
        Assert.False(await env.Center.MarkReadAsync(Bob, ritasRow.Id));
        Assert.Equal(0, await env.Center.MarkReadAsync(Bob, [ritasRow.Id]));
        Assert.Equal(0, await env.Center.MarkAllReadAsync(Bob));
        Assert.False(await env.Watches.RemoveByIdAsync(Bob, ritasWatch.Id));
        Assert.False(await env.Watches.SetLevelByIdAsync(Bob, ritasWatch.Id, WatchLevel.Decisions));
        Assert.Empty(await env.Watches.ListAsync(Bob));
        Assert.Null(await env.Watches.GetAsync(Bob, "part", part));
        await env.Watches.RemoveAsync(Bob, "part", part);
        Assert.True((await env.Center.GetPreferencesAsync(Bob)).AutoWatch);
        Assert.True((await env.Center.GetPreferencesAsync(Bob)).Kinds.All(k => k.InApp));

        // Everything of rita's is untouched.
        Assert.Equal(1, await env.Center.CountUnreadAsync(Rita));
        Assert.Equal(WatchLevel.All, (await env.Watches.ListAsync(Rita)).Single().Level);
        Assert.False((await env.Center.GetPreferencesAsync(Rita)).AutoWatch);
        // The mark-all of one user touches only that user's rows.
        Assert.Equal(1, await env.Center.MarkAllReadAsync(Rita));
    }

    // ---------------------------------------------------------------- caps and retention

    [Fact]
    public async Task Only_the_newest_notifications_per_user_are_kept()
    {
        var env = await AwarenessEnv.CreateAsync(pg, o => o.MaxPerUser = 10);
        var part = await env.NewPartAsync();
        var markings = AwarenessEnv.Itar();
        for (var i = 1; i <= 15; i++)
        {
            await using var conn = await env.Ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await env.Sink.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.CommentAdded, "part", part, markings, Alice.Id, Bob.Id, Guid.NewGuid()), CancellationToken.None);
            await tx.CommitAsync();
            env.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(10, (await env.RowsOfAsync(Bob)).Count);
        await using (var conn = await env.Ds.OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await env.Sink.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.CommentAdded, "part", part, markings, Alice.Id, Rita.Id), CancellationToken.None);
            await tx.CommitAsync();
        }
        Assert.Equal(10, (await env.RowsOfAsync(Bob)).Count); // another user's write does not trim bob's
    }

    [Fact]
    public async Task Retention_purges_old_read_notifications_only_and_is_off_by_default()
    {
        var off = await AwarenessEnv.CreateAsync(pg);
        Assert.Equal(0, off.Options.RetentionDays);
        Assert.Equal(0, await off.Retention.PurgeAsync(CancellationToken.None));

        var env = await AwarenessEnv.CreateAsync(pg, o => o.RetentionDays = 30);
        var part = await env.NewPartAsync();
        await env.StartReviewAsync(part); // rita: one notification
        await env.Parts.SetStateAsync(Ada, part, "InWork", "synthetic", true, null);
        var rita = Assert.Single(await env.RowsOfAsync(Rita));
        await env.Center.MarkReadAsync(Rita, rita.Id);
        await env.Center.MarkAllReadAsync(Rita);

        Assert.Equal(0, await env.Retention.PurgeAsync(CancellationToken.None)); // too young
        env.Clock.Advance(TimeSpan.FromDays(31));
        // An unread one of the same age is never purged by age.
        await env.ExecuteAsync("INSERT INTO ntf.notification (id, recipient_sub, kind, object_type, object_number, created_at) VALUES (@id, @u, 'watched_changed', 'part', @n, now() - interval '40 days')",
            ("id", Guid.CreateVersion7()), ("u", Rita.Id), ("n", part));
        Assert.Equal(1, await env.Retention.PurgeAsync(CancellationToken.None));
        var left = await env.RowsOfAsync(Rita);
        Assert.Single(left);
        Assert.False(left[0].Read);
    }

    [Fact]
    public async Task The_notification_tables_are_least_privilege_and_the_comment_table_is_append_only_for_the_application()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        await using var conn = await env.Ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT has_table_privilege('keel_app', 'ntf.comment', 'DELETE'), has_table_privilege('keel_app', 'ntf.comment', 'UPDATE'), has_table_privilege('keel_app', 'ntf.fanout_cursor', 'INSERT'), has_table_privilege('keel_app', 'ntf.fanout_cursor', 'DELETE')", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        Assert.Equal((false, true, false, false), (r.GetBoolean(0), r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3)));
    }
}
