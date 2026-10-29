using Keel.Audit;
using Keel.Core;
using Keel.Notifications;

namespace Keel.IntegrationTests;

/// <summary>Watch lists (UX-4, ADR-0030) against real Postgres: a watch needs read access, the list is re-authorized on every read, and the Home card shows only readable objects.</summary>
[Collection(PostgresCollection.Name)]
public sealed class WatchListTests(PostgresFixture pg)
{
    private static readonly Subject Alice = AwarenessEnv.Alice;
    private static readonly Subject Bob = AwarenessEnv.Bob;
    private static readonly Subject Rita = AwarenessEnv.Rita;
    private static readonly Subject Eve = AwarenessEnv.Eve;
    private static readonly Subject Lena = AwarenessEnv.Lena;

    [Fact]
    public async Task Watching_needs_read_access_and_a_refusal_looks_like_not_found_and_is_audited()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();

        Assert.Equal(WatchLevel.All, (await env.Watches.SetAsync(Bob, "part", part, WatchLevel.All, null)).Value);
        Assert.Equal(WatchLevel.Decisions, (await env.Watches.SetAsync(Bob, "part", part, WatchLevel.Decisions, null)).Value); // the same call changes the level
        Assert.Single(await env.Watches.ListAsync(Bob));

        foreach (var outsider in new[] { Eve, Lena })
        {
            var refused = await env.Watches.SetAsync(outsider, "part", part, WatchLevel.All, "corr-w");
            Assert.Equal(OutcomeKind.NotFound, refused.Kind);
            Assert.Null(await env.Watches.GetAsync(outsider, "part", part));
        }
        Assert.Equal(2, (await env.AuditAsync("watch.set")).Count(a => a.Outcome == AuditOutcome.Denied && a.ObjectId == part));
        Assert.Equal(OutcomeKind.NotFound, (await env.Watches.SetAsync(Bob, "part", "P-999999", WatchLevel.All, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Watches.SetAsync(Bob, "bogus", part, WatchLevel.All, null)).Kind);
        Assert.Equal(2, await env.ScalarAsync("SELECT count(*) FROM ntf.watch WHERE object_number = @n", ("n", part))); // alice (creator, automatic) and bob; nobody else
    }

    [Fact]
    public async Task A_user_can_watch_only_so_many_objects()
    {
        var env = await AwarenessEnv.CreateAsync(pg, o => o.MaxWatchesPerUser = 3);
        var parts = new List<string>();
        for (var i = 0; i < 4; i++) parts.Add(await env.NewPartAsync($"Part {i}"));
        foreach (var p in parts.Take(3)) Assert.True((await env.Watches.SetAsync(Bob, "part", p, WatchLevel.All, null)).IsOk);
        var over = await env.Watches.SetAsync(Bob, "part", parts[3], WatchLevel.All, null);
        Assert.Equal(OutcomeKind.Conflict, over.Kind);
        Assert.True((await env.Watches.SetAsync(Bob, "part", parts[0], WatchLevel.Decisions, null)).IsOk); // an existing watch can still change
        // Automatic watching stops at the cap too (alice created four parts: only three watches exist).
        Assert.Equal(3, (await env.Watches.ListAsync(Alice)).Count);
    }

    [Fact]
    public async Task The_list_is_re_authorized_and_a_hidden_object_is_a_nameless_row_that_can_still_be_removed()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var visible = await env.NewPartAsync("Visible bracket");
        var hidden = await env.NewPartAsync("Hidden bracket");
        Assert.True((await env.Watches.SetAsync(Rita, "part", visible, WatchLevel.All, null)).IsOk);
        Assert.True((await env.Watches.SetAsync(Rita, "part", hidden, WatchLevel.Decisions, null)).IsOk);
        Assert.All(await env.Watches.ListAsync(Rita), w => Assert.True(w.Available));

        await env.ExecuteAsync("UPDATE plm.part_master SET program = 'KRAKEN' WHERE number = @n", ("n", hidden)); // markings raised

        var list = await env.Watches.ListAsync(Rita);
        var shown = Assert.Single(list, w => w.Available);
        Assert.Equal((visible, "Visible bracket"), (shown.Number, shown.Name));
        var gone = Assert.Single(list, w => !w.Available);
        Assert.Equal((null, null, null, null, null, null), (gone.Number, gone.Name, gone.Kind, gone.Banner, gone.Href, gone.StateLabel));
        Assert.Equal(WatchLevel.Decisions, gone.Level);
        foreach (var leak in new[] { hidden, "Hidden", "KRAKEN" }) Assert.DoesNotContain(leak, AwarenessEnv.Json(list), StringComparison.Ordinal);

        Assert.True(await env.Watches.SetLevelByIdAsync(Rita, gone.Id, WatchLevel.All)); // the row can be managed without revealing anything
        Assert.True(await env.Watches.RemoveByIdAsync(Rita, gone.Id));
        Assert.Single(await env.Watches.ListAsync(Rita));
    }

    [Fact]
    public async Task The_home_card_shows_the_latest_events_of_watched_objects_the_user_can_still_read()
    {
        var env = await AwarenessEnv.CreateAsync(pg);
        var readable = await env.NewPartAsync("Readable");
        var hidden = await env.NewPartAsync("Hidden");
        foreach (var p in new[] { readable, hidden }) Assert.True((await env.Watches.SetAsync(Bob, "part", p, WatchLevel.All, null)).IsOk);
        await env.CheckedInEditAsync(Alice, readable, "a change", "first");
        await env.CheckedInEditAsync(Alice, hidden, "a change", "second");

        var both = await env.Feed.WatchedActivityAsync(Bob, 10);
        Assert.Equal(new[] { readable, hidden }.Order(), both.Select(w => w.Number).Distinct().Order());
        Assert.All(both, w => Assert.DoesNotMatch(@"\b(part|bom)\.[a-z_]+", w.Entry.Sentence));
        Assert.True(both.Zip(both.Skip(1)).All(p => p.First.Entry.At >= p.Second.Entry.At)); // newest first

        await env.ExecuteAsync("UPDATE plm.part_master SET program = 'KRAKEN' WHERE number = @n", ("n", hidden));
        var onlyReadable = await env.Feed.WatchedActivityAsync(Bob, 10);
        Assert.Equal([readable], onlyReadable.Select(w => w.Number).Distinct());
        Assert.DoesNotContain("Hidden", AwarenessEnv.Json(onlyReadable), StringComparison.Ordinal);
        Assert.Empty(await env.Feed.WatchedActivityAsync(Eve, 10)); // a user who watches nothing sees nothing
    }
}
