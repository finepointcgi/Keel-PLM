using Keel.Audit;
using Keel.Core;
using Keel.Notifications;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Comments and mentions (UX-4, ADR-0030) against real Postgres: access follows the object dynamically, writing needs read access and a role, mentions reach only people who could
/// read the object, comments are immutable at the database level with tombstone deletion, and the audit chain never holds the text.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CommentTests(PostgresFixture pg)
{
    private static readonly Subject Alice = AwarenessEnv.Alice;
    private static readonly Subject Rita = AwarenessEnv.Rita;
    private static readonly Subject Bob = AwarenessEnv.Bob;
    private static readonly Subject Lena = AwarenessEnv.Lena;
    private static readonly Subject Eve = AwarenessEnv.Eve;
    private static readonly Subject Dan = AwarenessEnv.Dan;
    private static readonly Subject Ada = AwarenessEnv.Ada;
    private static readonly Subject Auditor = AwarenessEnv.Auditor;

    private static async Task<Guid> PostAsync(AwarenessEnv env, Subject who, string number, string body, Guid? parent = null)
    {
        var r = await env.Comments.PostAsync(who, "part", number, body, parent, "corr-comment");
        Assert.True(r.IsOk, $"{who.Username}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!.Id;
    }

    // ---------------------------------------------------------------- access

    [Fact]
    public async Task Comments_follow_the_markings_of_their_object_and_a_refusal_is_audited_like_not_found()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        var id = await PostAsync(env, Alice, part, "Torque spec is on the drawing.");

        var seen = await env.Comments.ListAsync(Bob, "part", part, newestFirst: true, 0, 20, null);
        Assert.True(seen.IsOk);
        var thread = Assert.Single(seen.Value!.Threads);
        Assert.Equal(("Torque spec is on the drawing.", "Alice Example", id), (thread.Body, thread.AuthorName, thread.Id));
        Assert.Equal(1, await env.Comments.CountAsync(Bob, "part", part));

        // Eve has no program: not found, no count, no suggestions, and the refusal is in the audit log.
        Assert.Equal(OutcomeKind.NotFound, (await env.Comments.ListAsync(Eve, "part", part, true, 0, 20, "corr-eve")).Kind);
        Assert.Null(await env.Comments.CountAsync(Eve, "part", part));
        Assert.Equal(OutcomeKind.NotFound, (await env.Comments.MentionCandidatesAsync(Eve, "part", part, "b")).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Comments.ListAsync(Lena, "part", part, true, 0, 20, null)).Kind); // not a U.S. person
        var denial = Assert.Single(await env.AuditAsync("comment.read"), a => a.Actor == Eve.Id);
        Assert.Equal((AuditOutcome.Denied, "part", part, "corr-eve"), (denial.Outcome, denial.ObjectType, denial.ObjectId, denial.CorrelationId));
        // A part that does not exist is not found either, without an audit event for a made-up number.
        Assert.Equal(OutcomeKind.NotFound, (await env.Comments.ListAsync(Eve, "part", "P-999999", true, 0, 20, null)).Kind);
        Assert.DoesNotContain(await env.AuditAsync("comment.read"), a => a.ObjectId == "P-999999");

        // When the markings are raised, the comments follow: rita read them a moment ago, now she cannot.
        Assert.True((await env.Comments.ListAsync(Rita, "part", part, true, 0, 20, null)).IsOk);
        await env.ExecuteAsync("UPDATE plm.part_master SET program = 'KRAKEN' WHERE number = @n", ("n", part));
        Assert.Equal(OutcomeKind.NotFound, (await env.Comments.ListAsync(Rita, "part", part, true, 0, 20, null)).Kind);
        Assert.Null(await env.Comments.CountAsync(Rita, "part", part));
        // ... and a user of that program reads the same comments.
        Assert.Single((await env.Comments.ListAsync(AwarenessEnv.Kim, "part", part, true, 0, 20, null)).Value!.Threads);
    }

    [Fact]
    public async Task Writing_needs_read_access_and_a_role_and_refusals_are_audited()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();

        // A viewer may comment (viewer or higher; no extra role).
        Assert.True((await env.Comments.PostAsync(AwarenessEnv.Mallory, "part", part, "Viewer here", null, null)).IsOk);
        Assert.True((await env.Comments.PostAsync(Dan, "part", part, "Another viewer", null, null)).IsOk);
        // Someone who cannot read the part cannot write on it, and gets no hint that it exists.
        var eve = await env.Comments.PostAsync(Eve, "part", part, "Let me in", null, "corr-eve");
        Assert.Equal(OutcomeKind.NotFound, eve.Kind);
        // An auditor reads but does not write.
        var auditor = await env.Comments.PostAsync(Auditor, "part", part, "Independent", null, null);
        Assert.Equal(OutcomeKind.Forbidden, auditor.Kind);
        Assert.Equal(["role.missing"], auditor.Errors);
        Assert.Equal(2, await env.ScalarAsync("SELECT count(*) FROM ntf.comment"));
        Assert.Equal(2, (await env.AuditAsync("comment.create")).Count(a => a.Outcome == AuditOutcome.Denied));
        Assert.Equal(2, (await env.AuditAsync("comment.create")).Count(a => a.Outcome == AuditOutcome.Success));

        // Invalid text is refused before anything is read or written.
        Assert.Equal(OutcomeKind.Invalid, (await env.Comments.PostAsync(Alice, "part", part, "  ", null, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Comments.PostAsync(Alice, "part", part, new string('x', 4001), null, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Comments.PostAsync(Alice, "part", part, "bell\u0007", null, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Comments.PostAsync(Alice, "part", part, "orphan reply", Guid.NewGuid(), null)).Kind);
    }

    [Fact]
    public async Task Text_is_stored_exactly_as_written_and_never_interpreted()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        const string payload = "<script>alert('x')</script> & <img src=x onerror=alert(1)>\nsecond line\t\"quoted\"";
        await PostAsync(env, Alice, part, payload);
        var thread = Assert.Single((await env.Comments.ListAsync(Bob, "part", part, true, 0, 20, null)).Value!.Threads);
        Assert.Equal(payload, thread.Body); // no stripping, no escaping in storage: encoding happens when it is rendered
    }

    // ---------------------------------------------------------------- mentions and notifications

    [Fact]
    public async Task A_mention_notifies_only_people_who_could_read_the_object_and_the_suggestions_never_offer_the_others()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync(); // ITAR, ORCA: bob and dan can read it; lena (not a U.S. person) and eve (no program) cannot

        var offered = (await env.Comments.MentionCandidatesAsync(Alice, "part", part, "")).Value!.Select(c => c.Username).ToList();
        Assert.Contains("bob", offered);
        Assert.Contains("dan", offered);
        Assert.DoesNotContain("lena", offered);
        Assert.DoesNotContain("eve", offered);
        Assert.Empty((await env.Comments.MentionCandidatesAsync(Alice, "part", part, "le")).Value!); // lena is not suggested even when typed in full
        Assert.Empty((await env.Comments.MentionCandidatesAsync(Alice, "part", part, "lena")).Value!);
        Assert.Equal(["bob"], (await env.Comments.MentionCandidatesAsync(Alice, "part", part, "bo")).Value!.Select(c => c.Username));
        Assert.Empty((await env.Comments.MentionCandidatesAsync(Alice, "part", part, new string('x', 100))).Value!);
        // Wildcards typed by a user are text, not patterns.
        Assert.Empty((await env.Comments.MentionCandidatesAsync(Alice, "part", part, "%")).Value!);

        // Mentioning everybody tells only the eligible ones; a login nobody has and one's own login tell no one.
        var id = await PostAsync(env, Alice, part, "@bob please check, cc @lena @eve @nobody @alice and @dan");
        var rows = (await env.RowsAsync()).Where(r => r.Kind == NotificationKindCodes.CommentMention).ToList();
        Assert.Equal([Bob.Id, Dan.Id], [.. rows.Select(r => r.Recipient).Order()]);
        Assert.All(rows, r => Assert.Equal((id, Alice.Id, part), (r.RefId, r.Actor, r.ObjectNumber)));
        foreach (var s in new[] { Lena, Eve, Alice }) Assert.Empty(await env.RowsOfAsync(s));

        // It leads to the comment itself.
        var item = Assert.Single((await env.Center.ListAsync(Bob, NotificationFilter.Mentions, 0, 10)).Items);
        Assert.Equal($"parts/{part}?comment={id:D}#comments", item.Href);
        Assert.Equal("Alice Example mentioned you in a comment", item.Text);
    }

    [Fact]
    public async Task A_mention_is_still_checked_when_the_comment_is_written_not_when_the_name_was_typed()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        Assert.Contains("dan", (await env.Comments.MentionCandidatesAsync(Alice, "part", part, "da")).Value!.Select(c => c.Username));
        await env.Directory.UpsertAsync(Dan with { Programs = [] }); // dan leaves the program before the comment is posted
        await PostAsync(env, Alice, part, "@dan ping");
        Assert.Empty(await env.RowsOfAsync(Dan));
    }

    [Fact]
    public async Task A_mention_outranks_a_plain_comment_notification_and_replies_tell_the_author_of_the_thread()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        Assert.True((await env.Watches.SetAsync(Bob, "part", part, WatchLevel.All, null)).IsOk);
        Assert.True((await env.Watches.SetAsync(Dan, "part", part, WatchLevel.All, null)).IsOk);
        Assert.True((await env.Watches.SetAsync(Rita, "part", part, WatchLevel.Decisions, null)).IsOk);

        var first = await PostAsync(env, Alice, part, "@bob look");
        // Bob is both a mention target and a watcher: exactly one notification, the mention. Dan (watcher) gets the plain comment. Rita (decisions only) nothing.
        Assert.Equal([NotificationKindCodes.CommentMention], (await env.RowsOfAsync(Bob)).Select(r => r.Kind));
        Assert.Equal([NotificationKindCodes.CommentAdded], (await env.RowsOfAsync(Dan)).Select(r => r.Kind));
        Assert.Empty(await env.RowsOfAsync(Rita));
        Assert.Empty(await env.RowsOfAsync(Alice));

        // Bob answers in the thread: alice (watching since she created the part, and the author of the comment) is told.
        var reply = await PostAsync(env, Bob, part, "Looks fine", first);
        var alice = await env.RowsOfAsync(Alice);
        Assert.Equal([NotificationKindCodes.CommentAdded], alice.Select(r => r.Kind)); // once, although she is both the replied-to author and a watcher
        Assert.Equal(reply, alice[0].RefId);
        Assert.Equal(2, (await env.RowsOfAsync(Dan)).Count); // the watcher hears both comments
        Assert.Single(await env.RowsOfAsync(Bob)); // not about his own reply
    }

    // ---------------------------------------------------------------- immutability

    [Fact]
    public async Task The_database_refuses_to_edit_or_delete_a_comment_for_every_role()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        var id = await PostAsync(env, Alice, part, "Original words");

        // As the application role.
        await AssertRefusedAsync(env.Ds, "UPDATE ntf.comment SET body = 'Edited' WHERE id = @id", id);
        await AssertRefusedAsync(env.Ds, "UPDATE ntf.comment SET author_sub = 'sub-mallory' WHERE id = @id", id);
        await AssertRefusedAsync(env.Ds, "UPDATE ntf.comment SET created_at = now() WHERE id = @id", id);
        await AssertRefusedAsync(env.Ds, "UPDATE ntf.comment SET deleted_at = NULL WHERE id = @id", id); // not a deletion
        await AssertRefusedAsync(env.Ds, "DELETE FROM ntf.comment WHERE id = @id", id);
        // ... and as the owner of the table (the trigger applies to every role).
        await using var owner = new NpgsqlDataSourceBuilder(env.Db.SuperuserConnectionString).Build();
        await AssertRefusedAsync(owner, "UPDATE ntf.comment SET body = 'Edited' WHERE id = @id", id);
        await AssertRefusedAsync(owner, "DELETE FROM ntf.comment WHERE id = @id", id);
        await using (var truncate = owner.CreateCommand("TRUNCATE ntf.comment"))
            await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync());

        Assert.Equal("Original words", (await env.Comments.ListAsync(Bob, "part", part, true, 0, 5, null)).Value!.Threads.Single().Body);

        // The one allowed change is the one-time deletion marker; it keeps the body, and a second marking is refused.
        await using (var mark = env.Ds.CreateCommand("UPDATE ntf.comment SET deleted_at = now(), deleted_by = 'sub-x', deleted_reason = 'r' WHERE id = @id"))
        {
            mark.Parameters.AddWithValue("id", id);
            Assert.Equal(1, await mark.ExecuteNonQueryAsync());
        }
        await AssertRefusedAsync(env.Ds, "UPDATE ntf.comment SET deleted_reason = 'again' WHERE id = @id", id);
        Assert.Equal("Original words", await env.TextAsync("SELECT body FROM ntf.comment WHERE id = '" + id.ToString("D") + "'"));
    }

    private static async Task AssertRefusedAsync(NpgsqlDataSource ds, string sql, Guid id)
    {
        await using var cmd = ds.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Matches("immutable|never deleted|permission denied", ex.MessageText); // the trigger, or the missing privilege in front of it
    }

    [Fact]
    public async Task Replies_go_one_level_deep_and_only_to_a_comment_of_the_same_object()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        var other = await env.NewPartAsync("Other part");
        var root = await PostAsync(env, Alice, part, "Question");
        var reply = await PostAsync(env, Bob, part, "Answer", root);

        var deeper = await env.Comments.PostAsync(Alice, "part", part, "Reply to the reply", reply, null);
        Assert.Equal(OutcomeKind.Invalid, deeper.Kind);
        var elsewhere = await env.Comments.PostAsync(Alice, "part", other, "Wrong object", root, null);
        Assert.Equal(OutcomeKind.Invalid, elsewhere.Kind);

        // The database enforces the same, whatever the application does.
        await using var cmd = env.Ds.CreateCommand("INSERT INTO ntf.comment (id, object_type, object_number, author_sub, author_name, body, created_at, parent_id) VALUES (@id, 'part', @n, 'sub-x', 'X', 'deep', now(), @p)");
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("n", part);
        cmd.Parameters.AddWithValue("p", reply);
        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());

        var page = (await env.Comments.ListAsync(Bob, "part", part, false, 0, 20, null)).Value!;
        Assert.Equal((1, 2), (page.TotalThreads, page.TotalComments));
        Assert.Equal([reply], page.Threads.Single().Replies.Select(r => r.Id));
    }

    // ---------------------------------------------------------------- deletion with tombstone

    [Fact]
    public async Task Removing_a_comment_leaves_a_tombstone_keeps_the_text_for_audit_and_needs_the_author_or_an_administrator()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        var mine = await PostAsync(env, Alice, part, "Secret remark about the bracket");
        var other = await PostAsync(env, Bob, part, "Bob's remark");

        // Only the author or an administrator (and only with a reason).
        Assert.Equal(OutcomeKind.Invalid, (await env.Comments.DeleteAsync(Alice, mine, "  ", null)).Kind);
        var notMine = await env.Comments.DeleteAsync(Dan, mine, "I do not like it", "corr-del");
        Assert.Equal((OutcomeKind.Forbidden, "comment.not_author_or_admin"), (notMine.Kind, notMine.Errors[0]));
        Assert.Contains(await env.AuditAsync("comment.delete"), a => a.Outcome == AuditOutcome.Denied && a.Actor == Dan.Id);
        Assert.Equal(OutcomeKind.NotFound, (await env.Comments.DeleteAsync(Eve, mine, "x", null)).Kind); // cannot even read the object
        Assert.Equal(OutcomeKind.NotFound, (await env.Comments.DeleteAsync(Alice, Guid.NewGuid(), "x", null)).Kind);

        Assert.True((await env.Comments.DeleteAsync(Alice, mine, "Posted on the wrong part", "corr-del2")).IsOk);
        Assert.Equal(OutcomeKind.Conflict, (await env.Comments.DeleteAsync(Alice, mine, "again", null)).Kind);
        Assert.True((await env.Comments.DeleteAsync(Ada, other, "Moderation", null)).IsOk); // an administrator may remove anybody's comment

        // Others see a tombstone, never the text.
        var view = (await env.Comments.ListAsync(Dan, "part", part, false, 0, 20, null)).Value!;
        Assert.All(view.Threads, t => Assert.True(t.Removed));
        Assert.All(view.Threads, t => Assert.Null(t.Body));
        Assert.DoesNotContain("Secret remark", AwarenessEnv.Json(view), StringComparison.Ordinal);
        Assert.Equal((0, 2), (view.TotalComments, view.TotalThreads)); // the live count excludes removed comments
        Assert.Equal(0, await env.Comments.CountAsync(Dan, "part", part));
        Assert.Equal("Alice Example", view.Threads[0].RemovedByName);
        Assert.Null(view.Threads[0].RemovedReason); // the reason is for the author, administrators and auditors
        Assert.Equal("Posted on the wrong part", (await env.Comments.ListAsync(Alice, "part", part, false, 0, 20, null)).Value!.Threads[0].RemovedReason);

        // An auditor can still read the removed text. The table keeps it.
        var audited = (await env.Comments.ListAsync(Auditor, "part", part, false, 0, 20, null)).Value!;
        Assert.Equal("Secret remark about the bracket", audited.Threads[0].Body);
        Assert.True(audited.IsAuditor);
        Assert.False(audited.CanPost);
        Assert.Equal("Secret remark about the bracket", await env.TextAsync("SELECT body FROM ntf.comment WHERE id = '" + mine.ToString("D") + "'"));
    }

    [Fact]
    public async Task Audit_events_name_the_comment_and_the_object_but_never_the_text_and_commit_with_the_comment()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        const string unique = "ZEBRA-TOKEN-4711 marked remark";
        var id = await PostAsync(env, Alice, part, unique);
        await env.Comments.DeleteAsync(Alice, id, "Typo", "corr-d");

        var create = Assert.Single(await env.AuditAsync("comment.create"));
        var delete = Assert.Single(await env.AuditAsync("comment.delete"));
        Assert.Equal(("part", part, AuditOutcome.Success), (create.ObjectType, create.ObjectId, create.Outcome));
        Assert.Contains(id.ToString("D"), create.Payload, StringComparison.Ordinal);
        Assert.Contains(id.ToString("D"), delete.Payload, StringComparison.Ordinal);
        foreach (var record in await env.AuditAsync())
        {
            Assert.DoesNotContain("ZEBRA", record.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("marked remark", record.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("Typo", record.Payload, StringComparison.Ordinal); // the removal reason lives with the comment too
        }
        // The chain still verifies (comments did not disturb it).
        Assert.Equal(AuditOutcome.Success, create.Outcome);

        // Same transaction: a comment that fails to insert leaves no audit event. (A reply to a missing parent is refused before the insert.)
        var before = (await env.AuditAsync("comment.create")).Count;
        Assert.False((await env.Comments.PostAsync(Alice, "part", part, "orphan", Guid.NewGuid(), null)).IsOk);
        Assert.Equal(before, (await env.AuditAsync("comment.create")).Count);
        Assert.Equal(await env.ScalarAsync("SELECT count(*) FROM ntf.comment"), (await env.AuditAsync("comment.create")).Count(a => a.Outcome == AuditOutcome.Success));
    }

    // ---------------------------------------------------------------- limits

    [Fact]
    public async Task A_user_cannot_flood_an_object_and_an_object_cannot_collect_unlimited_comments()
    {
        var env = await AwarenessEnv.CreateAsync(pg, o => { o.CommentsPerMinutePerUser = 3; o.MaxCommentsPerObject = 5; });
        var part = await env.NewPartAsync();
        for (var i = 1; i <= 3; i++) await PostAsync(env, Alice, part, $"comment {i}");
        var tooFast = await env.Comments.PostAsync(Alice, "part", part, "comment 4", null, null);
        Assert.Equal(OutcomeKind.Conflict, tooFast.Kind);
        Assert.Contains("too quickly", tooFast.Errors[0], StringComparison.Ordinal);
        await PostAsync(env, Bob, part, "bob can still comment"); // the limit is per user

        env.Clock.Advance(TimeSpan.FromMinutes(2));
        await PostAsync(env, Alice, part, "comment 5"); // the minute has passed
        var full = await env.Comments.PostAsync(Dan, "part", part, "one too many", null, null);
        Assert.Equal(OutcomeKind.Conflict, full.Kind);
        Assert.Contains("limit of 5", full.Errors[0], StringComparison.Ordinal);
        Assert.Equal(5, await env.ScalarAsync("SELECT count(*) FROM ntf.comment"));
    }

    // ---------------------------------------------------------------- references in text

    [Fact]
    public async Task Object_numbers_in_text_resolve_only_for_people_who_can_read_that_object()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var open = await env.NewPartAsync("Public bracket", AwarenessEnv.Itar());
        var other = await env.NewPartAsync("Kraken bracket", AwarenessEnv.Itar("KRAKEN"), AwarenessEnv.Kim);

        var kim = await env.Comments.ResolveReferencesAsync(AwarenessEnv.Kim, [open, other, "P-424242"]);
        Assert.Equal([open, other], [.. kim.Keys.Order()]);
        Assert.Equal($"parts/{open}", kim[open].Href);

        var alice = await env.Comments.ResolveReferencesAsync(Alice, [open, other, "P-424242"]);
        Assert.Equal([open], [.. alice.Keys]); // the KRAKEN part and the unknown number are indistinguishable: plain text
        Assert.Empty(await env.Comments.ResolveReferencesAsync(Eve, [open, other]));
    }
}
