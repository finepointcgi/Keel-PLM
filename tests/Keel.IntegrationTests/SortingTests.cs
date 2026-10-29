using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Documents;
using Keel.Workflow;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// GAP-A-1: server-side sorting of the module search APIs. The contract proved here for every list: (1) a sort key is a whitelisted lookup, never SQL text;
/// (2) sorting never adds, drops or exposes a row the caller may not read (the SQL oracle: the order a restricted caller sees is exactly the order of the
/// full list with the rows they cannot read removed, so no sort key carries information about hidden rows); (3) the totals do not change with the sort.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SortingTests(PostgresFixture pg)
{
    private sealed record Page(IReadOnlyList<string> Ids, long Total);

    /// <summary>The oracle shared by every list: full and restricted callers, every key, both directions.</summary>
    private static async Task AssertContractAsync(IEnumerable<string> keys, Func<Subject, SortSpec?, Task<Page>> query, Subject full, Subject restricted, int expectedHidden)
    {
        var baseFull = await query(full, null);
        var baseRestricted = await query(restricted, null);
        Assert.Equal(baseFull.Ids.Count - expectedHidden, baseRestricted.Ids.Count);
        Assert.True(baseRestricted.Ids.Count >= 2, "the fixture must leave the restricted caller several rows to order");
        var visible = baseRestricted.Ids.ToHashSet(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            foreach (var descending in new[] { false, true })
            {
                var spec = new SortSpec(key, descending);
                var f = await query(full, spec);
                var r = await query(restricted, spec);
                // Same rows, same totals: a sort neither drops nor adds rows.
                Assert.Equal(baseFull.Ids.Order(StringComparer.Ordinal), f.Ids.Order(StringComparer.Ordinal));
                Assert.Equal(baseRestricted.Ids.Order(StringComparer.Ordinal), r.Ids.Order(StringComparer.Ordinal));
                Assert.Equal((baseFull.Total, baseRestricted.Total), (f.Total, r.Total));
                // The oracle: the restricted order is the full order minus the hidden rows. A key that depended on a hidden row could not satisfy this for every key.
                Assert.Equal(f.Ids.Where(visible.Contains), r.Ids);
            }
        }
    }

    private static async Task<IReadOnlyList<string>> OrderedAsync(Func<SortSpec?, Task<Page>> query, string key, bool descending) => (await query(new SortSpec(key, descending))).Ids;

    private static void AssertIsMonotone(IReadOnlyList<string> ascending, IReadOnlyList<string> descending) => Assert.Equal(ascending.Reverse(), descending);

    // ---------------------------------------------------------------- the whitelist itself

    [Fact]
    public async Task A_key_that_is_not_on_the_whitelist_is_refused_even_when_it_is_handed_straight_to_a_service()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        await env.NewPartAsync("Bracket", ChangeEnv.Open);
        foreach (var evil in new[] { "name; DROP TABLE plm.part_master; --", "m.number", "(SELECT 1)", "name DESC, (SELECT pg_sleep(5))", "" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => env.Parts.SearchAsync(ChangeEnv.Alice, "", null, 50, 0, new SortSpec(evil, false), null));
            Assert.False(PartsSortKeys.Allows(evil));
        }
        // The table is untouched and the request that names a good key still works.
        Assert.Single((await env.Parts.SearchAsync(ChangeEnv.Alice, "", null, 50, 0, new SortSpec("name", true), null)).Items);
        Assert.False(PartsSortKeys.TryResolve("name", "sideways", out _, out var error));
        Assert.Contains("asc or desc", error, StringComparison.Ordinal);
        Assert.Null(PartsSortKeys.Resolve(null, "desc"));
    }

    private static SortMap PartsSortKeys => Keel.Parts.PartService.SortKeys;

    // ---------------------------------------------------------------- parts

    [Fact]
    public async Task Parts_sort_on_the_server_across_pages_and_never_by_what_the_caller_cannot_read()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        foreach (var (name, markings) in new[] { ("Zeta bracket", ChangeEnv.Open), ("alpha plate", ChangeEnv.Itar()), ("Mike gasket", ChangeEnv.Open), ("bravo housing", ChangeEnv.Itar()), ("Delta clip", ChangeEnv.Open), ("echo pin", ChangeEnv.Open) })
            await env.NewPartAsync(name, markings);

        Task<Page> Query(Subject who, SortSpec? sort) => env.Parts.SearchAsync(who, "", null, 50, 0, sort, null).ContinueWith(t => new Page([.. t.Result.Items.Select(i => i.Number)], t.Result.Total));
        await AssertContractAsync(Keel.Parts.PartService.SortKeys.Keys, Query, ChangeEnv.Alice, ChangeEnv.Eve, expectedHidden: 2);

        // The order is by the whole result, not by one page: page two of a name sort holds the last names.
        var byName = await env.Parts.SearchAsync(ChangeEnv.Alice, "", null, 4, 0, new SortSpec("name", false), null);
        var page2 = await env.Parts.SearchAsync(ChangeEnv.Alice, "", null, 4, 4, new SortSpec("name", false), null);
        Assert.Equal(["alpha plate", "bravo housing", "Delta clip", "echo pin"], byName.Items.Select(i => i.Name));
        Assert.Equal(["Mike gasket", "Zeta bracket"], page2.Items.Select(i => i.Name));
        Assert.Equal(6, page2.Total);
        var desc = await env.Parts.SearchAsync(ChangeEnv.Alice, "", null, 3, 0, new SortSpec("name", true), null);
        Assert.Equal(["Zeta bracket", "Mike gasket", "echo pin"], desc.Items.Select(i => i.Name));
        // Eve's names, in order, with the two ITAR parts nowhere in her answer.
        var eve = await env.Parts.SearchAsync(ChangeEnv.Eve, "", null, 50, 0, new SortSpec("name", false), null);
        Assert.Equal(["Delta clip", "echo pin", "Mike gasket", "Zeta bracket"], eve.Items.Select(i => i.Name));
        // No sort keeps the relevance order (number order for an empty query).
        Assert.Equal(["P-000001", "P-000002", "P-000003"], (await env.Parts.SearchAsync(ChangeEnv.Alice, "", null, 3, 0, null)).Items.Select(i => i.Number));
        // The sort is part of the audited search.
        Assert.Equal(["P-000006", "P-000005", "P-000004"], (await env.Parts.SearchAsync(ChangeEnv.Alice, "", null, 3, 0, new SortSpec("number", true), "c")).Items.Select(i => i.Number));
        Assert.Contains(await Audited(env.Ds, "part.search"), p => p.Contains("number desc", StringComparison.Ordinal));
        AssertIsMonotone(await OrderedAsync(s => Query(ChangeEnv.Alice, s), "number", false), await OrderedAsync(s => Query(ChangeEnv.Alice, s), "number", true));
    }

    private static async Task<List<string>> Audited(NpgsqlDataSource ds, string action)
    {
        var rows = new List<string>();
        await using var cmd = ds.CreateCommand("SELECT payload::text FROM audit.event WHERE action = @a");
        cmd.Parameters.AddWithValue("a", action);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add(r.GetString(0));
        return rows;
    }

    // ---------------------------------------------------------------- documents

    [Fact]
    public async Task Documents_sort_on_the_server_with_the_same_guarantees()
    {
        var env = await ChangeEnv.CreateAsync(pg, documents: true);
        foreach (var (name, markings, type) in new[] { ("Zulu spec", ChangeEnv.Open, "Specification"), ("alpha drawing", ChangeEnv.Itar(), "Drawing"), ("Mike memo", ChangeEnv.Open, "Report"), ("bravo spec", ChangeEnv.Itar(), "Specification"), ("Delta note", ChangeEnv.Open, "Report") })
        {
            var r = await env.Docs!.CreateAsync(ChangeEnv.Alice, new CreateDocumentCommand(name, type, markings), null, null);
            Assert.True(r.IsOk, string.Join("; ", r.Errors));
        }

        Task<Page> Query(Subject who, SortSpec? sort) => env.Docs!.SearchAsync(who, "", null, null, 50, 0, sort, null).ContinueWith(t => new Page([.. t.Result.Items.Select(i => i.Number)], t.Result.Total));
        await AssertContractAsync(DocumentService.SortKeys.Keys, Query, ChangeEnv.Alice, ChangeEnv.Eve, expectedHidden: 2);

        var eve = await env.Docs!.SearchAsync(ChangeEnv.Eve, "", null, null, 50, 0, new SortSpec("name", false), null);
        Assert.Equal(["Delta note", "Mike memo", "Zulu spec"], eve.Items.Select(i => i.Name));
        var types = await env.Docs.SearchAsync(ChangeEnv.Alice, "", null, null, 50, 0, new SortSpec("type", true), null);
        Assert.Equal(["Specification", "Specification", "Report", "Report", "Drawing"], types.Items.Select(i => i.DocumentType));
        await Assert.ThrowsAsync<ArgumentException>(() => env.Docs.SearchAsync(ChangeEnv.Alice, "", null, null, 50, 0, new SortSpec("name; select 1", false), null));
    }

    // ---------------------------------------------------------------- changes

    [Fact]
    public async Task Changes_sort_on_the_server_with_the_same_guarantees()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        await env.NewChangeAsync(ChangeEnv.Alice, ChangeTypes.Pr, "Zebra crossing", ChangeEnv.Open, priority: "Low");
        await env.NewChangeAsync(ChangeEnv.Alice, ChangeTypes.Pr, "alpha problem", ChangeEnv.Itar(), priority: "High");
        await env.NewChangeAsync(ChangeEnv.Alice, ChangeTypes.Ecr, "Mango request", ChangeEnv.Open, priority: "High");
        await env.NewChangeAsync(ChangeEnv.Alice, ChangeTypes.Pr, "bravo secret", ChangeEnv.Itar(), priority: "Medium");
        await env.NewChangeAsync(ChangeEnv.Alice, ChangeTypes.Pr, "Delta fix", ChangeEnv.Open, priority: "Medium");

        Task<Page> Query(Subject who, SortSpec? sort) => env.Change.SearchAsync(who, new ChangeSearchQuery(Sort: sort), null).ContinueWith(t => new Page([.. t.Result.Items.Select(i => i.Number)], t.Result.Total));
        await AssertContractAsync(ChangeService.SortKeys.Keys, Query, ChangeEnv.Alice, ChangeEnv.Eve, expectedHidden: 2);

        var eve = await env.Change.SearchAsync(ChangeEnv.Eve, new ChangeSearchQuery(Sort: new SortSpec("title", false)), null);
        Assert.Equal(["Delta fix", "Mango request", "Zebra crossing"], eve.Items.Select(i => i.Title));
        var byPriority = await env.Change.SearchAsync(ChangeEnv.Alice, new ChangeSearchQuery(Sort: new SortSpec("priority", false)), null);
        Assert.Equal(["High", "High", "Low", "Medium", "Medium"], byPriority.Items.Select(i => i.Priority));
        // Without a sort the newest change comes first (the old default).
        var newest = await env.Change.SearchAsync(ChangeEnv.Alice, new ChangeSearchQuery(), null);
        Assert.Equal("Delta fix", newest.Items[0].Title);
        // Paging composes with the sort: the filter and total see only readable rows.
        var paged = await env.Change.SearchAsync(ChangeEnv.Eve, new ChangeSearchQuery(Limit: 2, Offset: 2, Sort: new SortSpec("title", false)), null);
        Assert.Equal(3L, paged.Total);
        Assert.Equal(["Zebra crossing"], paged.Items.Select(i => i.Title));
        Assert.Contains(await Audited(env.Ds, "change.search"), p => p.Contains("title asc", StringComparison.Ordinal));
        await Assert.ThrowsAsync<ArgumentException>(() => env.Change.SearchAsync(ChangeEnv.Alice, new ChangeSearchQuery(Sort: new SortSpec("title) --", false)), null));
    }

    // ---------------------------------------------------------------- baselines

    [Fact]
    public async Task Baselines_sort_on_the_server_with_the_same_guarantees()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        var parts = new List<string>();
        foreach (var name in new[] { "Root one", "Root two", "Root three" }) parts.Add(await env.ReleasedAssemblyAsync(name, (await env.ReleasedComponentAsync("Child " + name), 1m)));
        await env.BaselineAsync(parts[0], name: "Zulu baseline", type: "Design");
        await env.BaselineAsync(parts[1], name: "alpha baseline", type: "Design", markings: ConfigEnv.Itar(), confirm: true);
        await env.BaselineAsync(parts[2], name: "Mike baseline", type: "Design");
        await env.BaselineAsync(parts[0], name: "bravo baseline", type: "Design", markings: ConfigEnv.Itar(), confirm: true);
        await env.BaselineAsync(parts[1], name: "Delta baseline", type: "Design");

        async Task<Page> Query(Subject who, SortSpec? sort)
        {
            var o = await env.Baselines.SearchAsync(who, new BaselineSearch(Sort: sort), null);
            Assert.True(o.IsOk, string.Join("; ", o.Errors));
            return new Page([.. o.Value!.Items.Select(i => i.Number)], o.Value.Total);
        }
        await AssertContractAsync(BaselineService.SortKeys.Keys, Query, ConfigEnv.Alice, ConfigEnv.Eve, expectedHidden: 2);

        var eve = (await env.Baselines.SearchAsync(ConfigEnv.Eve, new BaselineSearch(Sort: new SortSpec("name", true)), null)).Value!;
        Assert.Equal(["Zulu baseline", "Mike baseline", "Delta baseline"], eve.Items.Select(i => i.Name));
        var paged = (await env.Baselines.SearchAsync(ConfigEnv.Alice, new BaselineSearch(Limit: 2, Offset: 3, Sort: new SortSpec("name", false)), null)).Value!;
        Assert.Equal(5, paged.Total);
        Assert.Equal(["Mike baseline", "Zulu baseline"], paged.Items.Select(i => i.Name));
        await Assert.ThrowsAsync<ArgumentException>(() => env.Baselines.SearchAsync(ConfigEnv.Alice, new BaselineSearch(Sort: new SortSpec("b.id; --", false)), null));
    }

    // ---------------------------------------------------------------- MBOMs

    [Fact]
    public async Task Mboms_sort_on_the_server_with_the_same_guarantees()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var parts = new List<string>();
        foreach (var name in new[] { "Top a", "Top b", "Top c", "Top d", "Top e" }) parts.Add(await env.C.NewPartAsync(name, "Assembly"));
        // Two MBOMs carry export markings Eve cannot read, though the parts are open.
        var plants = new[] { "PLANT-A", "PLANT-B", "PLANT-A", "PLANT-B", "PLANT-A" };
        var hidden = new[] { false, true, false, true, false };
        for (var i = 0; i < parts.Count; i++)
        {
            var r = await env.Mbom.CreateAsync(MbomEnv.Alice, new CreateMbomCommand(parts[i], plants[i], hidden[i] ? ConfigEnv.Itar() : null), "c");
            Assert.True(r.IsOk, string.Join("; ", r.Errors));
        }

        async Task<Page> Query(Subject who, SortSpec? sort)
        {
            var o = await env.Mbom.SearchAsync(who, new MbomSearch(Sort: sort), null);
            Assert.True(o.IsOk, string.Join("; ", o.Errors));
            return new Page([.. o.Value!.Items.Select(i => i.Number)], o.Value.Total);
        }
        await AssertContractAsync(MbomService.SortKeys.Keys, Query, MbomEnv.Alice, MbomEnv.Eve, expectedHidden: 2);

        var byPlant = (await env.Mbom.SearchAsync(MbomEnv.Alice, new MbomSearch(Sort: new SortSpec("plant", true)), null)).Value!;
        Assert.Equal(["PLANT-B", "PLANT-B", "PLANT-A", "PLANT-A", "PLANT-A"], byPlant.Items.Select(i => i.Plant));
        var eve = (await env.Mbom.SearchAsync(MbomEnv.Eve, new MbomSearch(Sort: new SortSpec("part", true)), null)).Value!;
        Assert.Equal([parts[4], parts[2], parts[0]], eve.Items.Select(i => i.PartNumber));
        var paged = (await env.Mbom.SearchAsync(MbomEnv.Alice, new MbomSearch(Limit: 2, Offset: 4, Sort: new SortSpec("mbom", false)), null)).Value!;
        Assert.Equal(5L, paged.Total);
        Assert.Single(paged.Items);
        await Assert.ThrowsAsync<ArgumentException>(() => env.Mbom.SearchAsync(MbomEnv.Alice, new MbomSearch(Sort: new SortSpec("mm.id; --", false)), null));
    }

    // ---------------------------------------------------------------- workflow tasks

    [Fact]
    public async Task Workflow_tasks_sort_after_redaction_from_the_values_the_caller_is_shown()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var numbers = new List<string>();
        foreach (var name in new[] { "Zinc bracket", "Alpha bracket", "Mid bracket" })
        {
            var n = await env.NewPartAsync(name, ChangeEnv.Open);
            numbers.Add(n);
            var started = await env.Flow.StartAsync(ChangeEnv.Alice, WorkflowEnv.Start(n, "rita", "bob", "carol"), "c");
            Assert.True(started.IsOk, string.Join("; ", started.Errors));
        }

        var plain = await env.Flow.MyTasksAsync(ChangeEnv.Rita, null);
        Assert.Equal(3, plain.Items.Count);
        var byItem = await env.Flow.MyTasksAsync(ChangeEnv.Rita, new SortSpec("item", false), null);
        Assert.Equal(numbers.Order(StringComparer.Ordinal), byItem.Items.Select(t => t.Target!.Key));
        var byItemDesc = await env.Flow.MyTasksAsync(ChangeEnv.Rita, new SortSpec("item", true), null);
        AssertIsMonotone([.. byItem.Items.Select(t => t.Id.ToString())], [.. byItemDesc.Items.Select(t => t.Id.ToString())]);
        foreach (var key in WorkflowService.SortKeys.Keys)
            foreach (var d in new[] { false, true })
            {
                var sorted = await env.Flow.MyTasksAsync(ChangeEnv.Rita, new SortSpec(key, d), null);
                Assert.Equal(plain.Items.Select(t => t.Id).Order(), sorted.Items.Select(t => t.Id).Order()); // the same tasks, only reordered
                Assert.Equal(plain.PageBanner, sorted.PageBanner);
            }
        // A caller with no tasks gets none, whatever the key.
        Assert.Empty((await env.Flow.MyTasksAsync(ChangeEnv.Mallory, new SortSpec("due", true), null)).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => env.Flow.MyTasksAsync(ChangeEnv.Rita, new SortSpec("name; --", false), null));
        Assert.Contains(await Audited(env.Ds, "workflow.tasks_list"), p => p.Contains("item desc", StringComparison.Ordinal));
    }
}
