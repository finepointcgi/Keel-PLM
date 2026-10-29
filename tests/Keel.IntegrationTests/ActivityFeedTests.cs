using System.Diagnostics;
using System.Text.Json;
using Keel.Audit;
using Keel.Change;
using Keel.Core;
using Keel.Notifications;
using Keel.Security;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// The activity feed (UX-4, ADR-0030) against real Postgres: it reads only the audit rows of the object it is asked about, only for a viewer who may read that object (an oracle
/// over several kinds of user), never lists views or downloads to ordinary viewers, maps every event to a sentence, shows safe details, and uses the existing audit index.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ActivityFeedTests(PostgresFixture pg)
{
    private static readonly Subject Alice = AwarenessEnv.Alice;
    private static readonly Subject Rita = AwarenessEnv.Rita;
    private static readonly Subject Bob = AwarenessEnv.Bob;
    private static readonly Subject Carol = AwarenessEnv.Carol;
    private static readonly Subject Dan = AwarenessEnv.Dan;
    private static readonly Subject Eve = AwarenessEnv.Eve;
    private static readonly Subject Lena = AwarenessEnv.Lena;
    private static readonly Subject Mallory = AwarenessEnv.Mallory;
    private static readonly Subject Ada = AwarenessEnv.Ada;
    private static readonly Subject Auditor = AwarenessEnv.Auditor;

    /// <summary>A part with a real story: created, read by several people, edited, checked in, commented, remarked, submitted, signed and released.</summary>
    private async Task<(AwarenessEnv Env, string Part)> StoryAsync()
    {
        var env = await AwarenessEnv.CreateAsync(pg, o => o.CommentsPerMinutePerUser = 100);
        var part = await env.NewPartAsync("Bracket, sensor");
        foreach (var reader in new[] { Alice, Bob, Dan, Mallory }) Assert.True((await env.Parts.GetAsync(reader, part, null, "corr-read")).IsOk); // part.read events
        Assert.True((await env.Parts.CheckOutAsync(Alice, part, null)).IsOk);
        var detail = (await env.Parts.GetAsync(Alice, part, null, null)).Value!;
        Assert.True((await env.Parts.UpdateWorkingCopyAsync(Alice, part, "Tighter tolerance", detail.Checkout!.Attributes, null)).IsOk);
        Assert.True((await env.Parts.CheckInAsync(Alice, part, "tolerance now 0.05", null)).IsOk);
        Assert.True((await env.Comments.PostAsync(Dan, "part", part, "Looks right to me", null, "corr-c")).IsOk);
        var changed = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.E, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value!;
        Assert.True((await env.Parts.ChangeMarkingsAsync(Alice, part, changed, "Distribution tightened", null)).IsOk);
        await env.ReleasePartAsync(part);
        return (env, part);
    }

    private static async Task<List<long>> OracleSeqsAsync(AwarenessEnv env, string part, bool withViews = false)
    {
        await using var conn = new NpgsqlConnection(env.Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT seq FROM audit.event WHERE object_type = 'part' AND object_id = @n AND action = ANY(@a) AND (@all OR outcome = 'Success') ORDER BY seq DESC", conn);
        cmd.Parameters.AddWithValue("n", part);
        cmd.Parameters.AddWithValue("a", withViews ? [.. ActivityCatalog.EventActions, .. ActivityCatalog.Views.Keys] : ActivityCatalog.EventActions.ToArray());
        cmd.Parameters.AddWithValue("all", withViews);
        var list = new List<long>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetInt64(0));
        return list;
    }

    // ---------------------------------------------------------------- the oracle

    [Fact]
    public async Task The_feed_of_every_kind_of_user_equals_what_the_access_policy_allows()
    {
        var (env, part) = await StoryAsync();
        var markings = (await env.Parts.GetAsync(Alice, part, null, null)).Value!.Markings;
        var expected = await OracleSeqsAsync(env, part);
        Assert.True(expected.Count >= 10, "The story should have produced a good number of events.");
        Assert.True(await env.ScalarAsync("SELECT count(*) FROM audit.event WHERE object_id = @n AND action = 'part.read'", ("n", part)) >= 4); // the reads exist in the log...

        var users = new[] { Alice, Rita, Bob, Carol, Dan, Mallory, Ada, Auditor, Eve, Lena, AwarenessEnv.Kim };
        var allowed = 0;
        foreach (var user in users)
        {
            var may = AccessPolicy.Evaluate(user, markings, PlmAction.Read).Permitted;
            var outcome = await env.Feed.GetAsync(user, "part", part, null, 100, includeReads: false, "corr-feed");
            Assert.Equal(may, outcome.IsOk);
            if (!may)
            {
                Assert.Equal(OutcomeKind.NotFound, outcome.Kind); // no rows, no hint
                continue;
            }
            allowed++;
            var page = outcome.Value!;
            Assert.Equal(expected, page.Entries.Select(e => e.Seq).ToList()); // exactly the rows the log holds for this object, newest first
            Assert.DoesNotContain(page.Entries, e => e.IsView); // ... and none of the views
            Assert.DoesNotContain(page.Entries, e => e.Category == "view" || e.Sentence.Contains("viewed", StringComparison.OrdinalIgnoreCase));
            Assert.Null(page.NextBefore);
        }
        Assert.True(allowed >= 8);
        // Every refusal is audited as a denial, once per attempt.
        Assert.Equal(users.Length - allowed, (await env.AuditAsync("activity.read")).Count(a => a.Outcome == AuditOutcome.Denied));
    }

    [Fact]
    public async Task Only_an_auditor_can_switch_on_views_and_doing_so_is_audited()
    {
        var (env, part) = await StoryAsync();
        var plain = (await env.Feed.GetAsync(Auditor, "part", part, null, 200, includeReads: false, null)).Value!;
        Assert.True(plain.IsAuditor);
        Assert.DoesNotContain(plain.Entries, e => e.IsView);
        Assert.Empty(await env.AuditAsync("activity.read")); // an ordinary view of the feed writes nothing

        var withViews = (await env.Feed.GetAsync(Auditor, "part", part, null, 200, includeReads: true, "corr-aud")).Value!;
        var seqs = await OracleSeqsAsync(env, part, withViews: true);
        Assert.Equal(seqs, withViews.Entries.Select(e => e.Seq).ToList());
        Assert.Contains(withViews.Entries, e => e.IsView && e.Sentence.Contains("viewed the part", StringComparison.Ordinal));
        var logged = Assert.Single(await env.AuditAsync("activity.read"));
        Assert.Equal((Auditor.Id, "part", part, AuditOutcome.Success, "corr-aud"), (logged.Actor, logged.ObjectType, logged.ObjectId, logged.Outcome, logged.CorrelationId));
        Assert.Contains("\"includeViews\":\"true\"", logged.Payload, StringComparison.Ordinal);

        // The flag does nothing for anybody else: no views, no audit event.
        var dan = (await env.Feed.GetAsync(Dan, "part", part, null, 200, includeReads: true, null)).Value!;
        Assert.False(dan.IsAuditor);
        Assert.DoesNotContain(dan.Entries, e => e.IsView);
        Assert.Single(await env.AuditAsync("activity.read"));
    }

    [Fact]
    public async Task Events_read_as_sentences_with_the_login_of_the_actor_and_safe_details_and_never_a_raw_code()
    {
        var (env, part) = await StoryAsync();
        var page = (await env.Feed.GetAsync(Bob, "part", part, null, 200, false, null)).Value!;
        var text = page.Entries.ToDictionary(e => e.Seq, e => e);
        var sentences = page.Entries.Select(e => e.Sentence).ToList();

        Assert.Contains("alice created the part", sentences);
        Assert.Contains("alice checked out the part", sentences);
        Assert.Contains("alice edited the working copy", sentences);
        Assert.Contains("alice checked in version A.2", sentences);
        Assert.Contains("dan commented", sentences);
        Assert.Contains("alice changed the security markings", sentences);
        Assert.Contains("alice submitted for review", sentences);
        Assert.Contains("rita signed and approved", sentences);
        Assert.Contains("carol signed and approved", sentences);
        Assert.Contains("The review finished: approved", sentences);
        Assert.Contains("alice asked rita for Technical review", sentences);
        Assert.All(sentences, s => Assert.DoesNotMatch(@"\b(part|workflow|comment|document|change|bom)\.[a-z_]+", s)); // no raw action code
        Assert.All(sentences, s => Assert.DoesNotContain("{", s, StringComparison.Ordinal));

        var checkin = page.Entries.Single(e => e.Sentence == "alice checked in version A.2");
        Assert.Contains(new ActivityDetail("Comment", "tolerance now 0.05", null), checkin.Details);
        var markingsEvent = page.Entries.Single(e => e.Category == "markings");
        Assert.Equal(["Before", "After", "Reason"], markingsEvent.Details.Select(d => d.Label)); // the banners of THIS object: before and after, and why
        Assert.Contains("DISTRIBUTION D", markingsEvent.Details[0].Value, StringComparison.Ordinal);
        Assert.Contains("DISTRIBUTION E", markingsEvent.Details[1].Value, StringComparison.Ordinal);
        var commentEvent = page.Entries.Single(e => e.Sentence == "dan commented");
        Assert.NotNull(commentEvent.Href);
        Assert.Contains("#comments", commentEvent.Href, StringComparison.Ordinal);
        Assert.DoesNotContain("Looks right to me", AwarenessEnv.Json(page), StringComparison.Ordinal); // the comment text is not in the feed
        Assert.Contains(page.Entries, e => e.Category == "review" && e.Href is not null && e.Href.EndsWith("#review", StringComparison.Ordinal));
        // The released event of the review appears after the signature and before the story's end: order is newest first.
        Assert.True(text.Keys.SequenceEqual(text.Keys.OrderByDescending(k => k)));
    }

    [Fact]
    public async Task The_feed_pages_by_sequence_number_without_gaps_or_repeats()
    {
        var (env, part) = await StoryAsync();
        var all = (await env.Feed.GetAsync(Bob, "part", part, null, 100, false, null)).Value!.Entries.Select(e => e.Seq).ToList();
        var paged = new List<long>();
        long? before = null;
        var pages = 0;
        do
        {
            var page = (await env.Feed.GetAsync(Bob, "part", part, before, 4, false, null)).Value!;
            Assert.True(page.Entries.Count <= 4);
            paged.AddRange(page.Entries.Select(e => e.Seq));
            before = page.NextBefore;
            pages++;
        } while (before is not null && pages < 20);
        Assert.Equal(all, paged);
        Assert.True(pages >= 3);
    }

    [Fact]
    public async Task Edits_of_the_working_copy_collapse_into_one_line()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        Assert.True((await env.Parts.CheckOutAsync(Alice, part, null)).IsOk);
        var detail = (await env.Parts.GetAsync(Alice, part, null, null)).Value!;
        for (var i = 0; i < 5; i++) Assert.True((await env.Parts.UpdateWorkingCopyAsync(Alice, part, $"draft {i}", detail.Checkout!.Attributes, null)).IsOk);

        var page = (await env.Feed.GetAsync(Alice, "part", part, null, 100, false, null)).Value!;
        var edits = page.Entries.Where(e => e.Category == "edit").ToList();
        Assert.Equal(["alice edited the working copy (5 times)"], edits.Select(e => e.Sentence));
        Assert.Equal(5, await env.ScalarAsync("SELECT count(*) FROM audit.event WHERE action = 'part.edit' AND object_id = @n", ("n", part))); // the log itself keeps all five
    }

    [Fact]
    public async Task A_detail_that_names_another_object_is_a_link_only_for_a_reader_of_that_object()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var open = await env.NewPartAsync("Readable part");
        var kraken = await env.NewPartAsync("Kraken part", AwarenessEnv.Itar("KRAKEN"), AwarenessEnv.Kim);
        var ecn = await env.Change.CreateAsync(Alice, new CreateChangeCommand(ChangeTypes.Ecn, "Revise", "Synthetic", "Medium", "Synthetic", AwarenessEnv.Itar(), ChangeClasses.II, null), null);
        Assert.True(ecn.IsOk, string.Join("; ", ecn.Errors));
        var number = ecn.Value!.Number;
        // Two real events of the change that name objects: one a reader of the change can read, one in another program.
        foreach (var named in new[] { open, kraken })
            await env.Audit.AppendAsync(new AuditEntry
            {
                Actor = ActorSnapshot.From(Alice), Action = "change.add_affected", Outcome = AuditOutcome.Success, ObjectType = "ecn", ObjectId = number,
                Details = AuditDetails.Of(("object", named), ("revision", "A"), ("action", "Revise")),
            });

        var page = (await env.Feed.GetAsync(Alice, "change", number, null, 50, false, null)).Value!;
        var added = page.Entries.Where(e => e.Sentence == "alice added an object to the change").ToList();
        Assert.Equal(2, added.Count);
        var details = added.SelectMany(a => a.Details).Where(d => d.Label == "Object").ToList();
        Assert.Contains(new ActivityDetail("Object", open, $"parts/{open}"), details);
        Assert.Contains(new ActivityDetail("Object", "An object you cannot see", null), details); // the KRAKEN part: no number, no link
        Assert.DoesNotContain(kraken, AwarenessEnv.Json(page), StringComparison.Ordinal);
        // Kim belongs to both programs: she sees both numbers.
        var kim = (await env.Feed.GetAsync(AwarenessEnv.Kim, "change", number, null, 50, false, null)).Value!;
        Assert.Contains(new ActivityDetail("Object", kraken, $"parts/{kraken}"), kim.Entries.SelectMany(e => e.Details));
    }

    // ---------------------------------------------------------------- the audit index

    [Fact]
    public async Task The_feed_query_uses_the_object_index_of_the_audit_table_at_a_hundred_thousand_events()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        await env.CheckedInEditAsync(Alice, part, "one");
        var first = await env.ScalarAsync("SELECT coalesce(max(seq), 0) FROM audit.event");
        // 100,000 synthetic events over 500 objects (the table is append-only; a throwaway database may be filled by its owner with plain SQL).
        await env.ExecuteAsync(
            """
            INSERT INTO audit.event (seq, id, occurred_at, actor, action, object_type, object_id, outcome, payload, prev_hash, hash)
            SELECT @first + g, gen_random_uuid(), now() - (g || ' seconds')::interval, 'sub-x',
                   CASE g % 4 WHEN 0 THEN 'part.read' WHEN 1 THEN 'part.edit' WHEN 2 THEN 'part.checkin' ELSE 'part.search' END,
                   'part', 'P-' || lpad((g % 500)::text, 6, '0'), 'Success', '{"actor":{"name":"x"},"details":{}}', decode(repeat('00', 32), 'hex'), decode(repeat('11', 32), 'hex')
            FROM generate_series(1, 100000) g
            """, ("first", first));
        await env.ExecuteAsync("ANALYZE audit.event");
        Assert.True(await env.ScalarAsync("SELECT count(*) FROM audit.event") >= 100_000);

        await using var conn = new NpgsqlConnection(env.Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("EXPLAIN (ANALYZE, FORMAT JSON) " + ActivityService.EventsSql, conn);
        cmd.Parameters.AddWithValue("t", "part");
        cmd.Parameters.AddWithValue("n", "P-000122");
        cmd.Parameters.AddWithValue("before", long.MaxValue);
        cmd.Parameters.AddWithValue("actions", ActivityCatalog.EventActions.ToArray());
        cmd.Parameters.AddWithValue("all", false);
        cmd.Parameters.AddWithValue("limit", 31);
        var plan = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.Contains("event_object_idx", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan", plan, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(plan);
        var executionMs = doc.RootElement[0].GetProperty("Execution Time").GetDouble();
        Assert.True(executionMs < 100, $"The feed query took {executionMs} ms on 100,000 events:\n{plan}");
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "TestResults");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "awareness-activity-plan.json"), plan);

        // Through the service, for an object with about 200 events, the answer is prompt.
        var watch = Stopwatch.StartNew();
        var page = await env.Feed.GetAsync(Alice, "part", part, null, 30, false, null);
        Assert.True(page.IsOk);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");
    }
}
