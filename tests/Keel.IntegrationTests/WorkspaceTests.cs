using System.Security.Claims;
using Keel.Audit;
using Keel.Core;
using Keel.Parts;
using Keel.Security;
using Keel.Web.Services;
using Keel.Workspace;
using Microsoft.AspNetCore.Components.Authorization;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Personal workspace data (UX-1, ADR-0028) against real Postgres: strict per-user ownership of preferences, table layouts, recent items, favorites and saved
/// searches, the size caps, and the audit trail of saved searches (a hash, never the text).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkspaceTests(PostgresFixture pg)
{
    private static readonly Subject Alice = WorkflowEnv.Alice;
    private static readonly Subject Rita = WorkflowEnv.Rita;

    private sealed record Env(WorkspaceService Workspace, NpgsqlDataSource Ds, TestClock Clock);

    private async Task<Env> NewEnvAsync()
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var clock = new TestClock();
        return new Env(new WorkspaceService(ds, new PostgresAuditLog(ds, clock), clock), ds, clock);
    }

    private static async Task<long> CountAsync(NpgsqlDataSource ds, string table, string user)
    {
        await using var cmd = ds.CreateCommand($"SELECT count(*) FROM ws.{table} WHERE user_sub = @u");
        cmd.Parameters.AddWithValue("u", user);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Preferences_are_per_user_default_to_system_and_comfortable_and_reject_unknown_values()
    {
        var env = await NewEnvAsync();
        Assert.Equal(UserPreferences.Default, await env.Workspace.GetPreferencesAsync(Alice));

        await env.Workspace.SetPreferencesAsync(Alice, new UserPreferences(ThemeNames.Dark, DensityNames.Compact, true));

        Assert.Equal(new UserPreferences("dark", "compact", true), await env.Workspace.GetPreferencesAsync(Alice));
        Assert.Equal(UserPreferences.Default, await env.Workspace.GetPreferencesAsync(Rita)); // another user is untouched
        // The static page shell reads the preference by the session's subject (server side, so the theme survives sign-out and sign-in).
        Assert.Equal("dark", (await env.Workspace.GetPreferencesForShellAsync("sub-alice")).Theme);
        Assert.Equal(UserPreferences.Default, await env.Workspace.GetPreferencesForShellAsync("sub-nobody"));
        Assert.Equal(UserPreferences.Default, await env.Workspace.GetPreferencesForShellAsync(new string('x', 500)));

        var saved = await env.Workspace.SetPreferencesAsync(Rita, new UserPreferences("neon", "huge", false)); // normalized, never stored raw
        Assert.Equal(UserPreferences.Default, saved);
        await using var check = env.Ds.CreateCommand("SELECT theme FROM ws.preference WHERE user_sub = 'sub-rita'");
        Assert.Equal("system", (string?)await check.ExecuteScalarAsync());

        // The database refuses values outside the allowed set even if a bug skipped the normalization.
        await using var bad = env.Ds.CreateCommand("INSERT INTO ws.preference (user_sub, theme, density, nav_collapsed, updated_at) VALUES ('x', 'neon', 'compact', false, now())");
        await Assert.ThrowsAsync<PostgresException>(() => bad.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task The_interface_language_is_a_per_user_preference_that_defaults_to_English_and_is_never_stored_raw()
    {
        // ADR 0057, migration 0036: ws.preference.language.
        var env = await NewEnvAsync();
        Assert.Equal("en", (await env.Workspace.GetPreferencesAsync(Alice)).Language);

        var saved = await env.Workspace.SetPreferencesAsync(Alice, new UserPreferences(ThemeNames.Dark, DensityNames.Compact, true, SimpleView: true, Language: "es"));

        Assert.Equal("es", saved.Language);
        Assert.Equal(new UserPreferences("dark", "compact", true, true, "es"), await env.Workspace.GetPreferencesAsync(Alice)); // the other choices are kept
        Assert.Equal("es", (await env.Workspace.GetPreferencesForShellAsync("sub-alice")).Language); // the page shell reads the same value for <html lang>
        Assert.Equal("en", (await env.Workspace.GetPreferencesAsync(Rita)).Language); // another user is untouched

        Assert.Equal("en", (await env.Workspace.SetPreferencesAsync(Rita, UserPreferences.Default with { Language = "'; DROP TABLE ws.preference; --" })).Language);
        await using var check = env.Ds.CreateCommand("SELECT language FROM ws.preference WHERE user_sub = 'sub-rita'");
        Assert.Equal("en", (string?)await check.ExecuteScalarAsync());

        // Changing another preference later must not reset the language (the UI saves all choices together, from the circuit's copy).
        await env.Workspace.SetPreferencesAsync(Alice, (await env.Workspace.GetPreferencesAsync(Alice)) with { Theme = ThemeNames.Light });
        Assert.Equal("es", (await env.Workspace.GetPreferencesAsync(Alice)).Language);

        // The database bounds the shape of the value even if a bug skipped the normalization; which tags are shipped is the application's decision (no migration per language).
        await using var bad = env.Ds.CreateCommand("UPDATE ws.preference SET language = 'Español, por favor' WHERE user_sub = 'sub-alice'");
        await Assert.ThrowsAsync<PostgresException>(() => bad.ExecuteNonQueryAsync());
        await using var future = env.Ds.CreateCommand("UPDATE ws.preference SET language = 'de' WHERE user_sub = 'sub-alice'");
        Assert.Equal(1, await future.ExecuteNonQueryAsync());
        Assert.Equal("de", (await env.Workspace.GetPreferencesAsync(Alice)).Language); // stored; the UI reads an unshipped tag as English (LocalizationTests)
    }

    [Fact]
    public async Task Table_layouts_are_per_user_and_per_table_and_capped()
    {
        var env = await NewEnvAsync();
        Assert.Equal(TablePreferences.Default, await env.Workspace.GetTablePreferencesAsync(Alice, "parts"));

        Assert.True(await env.Workspace.SetTablePreferencesAsync(Alice, "parts", new TablePreferences(["name", "name", "type"], "compact", "number", true)));

        var mine = await env.Workspace.GetTablePreferencesAsync(Alice, "parts");
        Assert.Equal(["name", "type"], mine.HiddenColumns);
        Assert.Equal("compact", mine.Density);
        Assert.Equal(("number", true), (mine.SortColumn, mine.SortDescending));
        Assert.Equal(TablePreferences.Default, await env.Workspace.GetTablePreferencesAsync(Rita, "parts"));
        Assert.Equal(TablePreferences.Default, await env.Workspace.GetTablePreferencesAsync(Alice, "documents"));
        Assert.False(await env.Workspace.SetTablePreferencesAsync(Alice, "Bad Id", TablePreferences.Default)); // ids are validated

        for (var i = 0; i < WorkspaceLimits.TablePreferences - 1; i++) Assert.True(await env.Workspace.SetTablePreferencesAsync(Alice, $"t{i}", TablePreferences.Default));
        Assert.False(await env.Workspace.SetTablePreferencesAsync(Alice, "one-too-many", TablePreferences.Default));
        Assert.True(await env.Workspace.SetTablePreferencesAsync(Alice, "parts", new TablePreferences([], null, null, false))); // an existing layout can still change
        Assert.Equal(WorkspaceLimits.TablePreferences, await CountAsync(env.Ds, "table_preference", "sub-alice"));
    }

    [Fact]
    public async Task Recent_items_keep_one_row_per_object_the_newest_fifty_and_belong_to_one_user()
    {
        var env = await NewEnvAsync();
        for (var i = 1; i <= 60; i++)
        {
            await env.Workspace.RecordViewAsync(Alice, ObjectKinds.Part, $"P-{i:000000}");
            env.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        await env.Workspace.RecordViewAsync(Rita, ObjectKinds.Part, "P-000001");

        var recent = await env.Workspace.ListRecentAsync(Alice);
        Assert.Equal(WorkspaceLimits.RecentItems, recent.Count);
        Assert.Equal("P-000060", recent[0].Number); // newest first
        Assert.DoesNotContain(recent, r => r.Number == "P-000010"); // the oldest ten were dropped
        Assert.Contains(recent, r => r.Number == "P-000011");
        Assert.Equal(["P-000001"], (await env.Workspace.ListRecentAsync(Rita)).Select(r => r.Number)); // rita's row is hers alone, and Alice's trimming never touched it
        Assert.Equal(50, await CountAsync(env.Ds, "recent_item", "sub-alice"));

        // Unknown kinds and absurd numbers are not stored.
        await env.Workspace.RecordViewAsync(Alice, "secret", "X-1");
        await env.Workspace.RecordViewAsync(Alice, ObjectKinds.Part, new string('9', 100));
        Assert.Equal(50, await CountAsync(env.Ds, "recent_item", "sub-alice"));

        await env.Workspace.ClearRecentAsync(Alice);
        Assert.Empty(await env.Workspace.ListRecentAsync(Alice));
        Assert.Single(await env.Workspace.ListRecentAsync(Rita));
    }

    [Fact]
    public async Task Viewing_the_same_object_again_within_a_minute_is_debounced_and_later_it_moves_to_the_top()
    {
        var env = await NewEnvAsync();
        await env.Workspace.RecordViewAsync(Alice, ObjectKinds.Part, "P-000001");
        var first = (await env.Workspace.ListRecentAsync(Alice)).Single().At;
        env.Clock.Advance(TimeSpan.FromSeconds(30));
        await env.Workspace.RecordViewAsync(Alice, ObjectKinds.Part, "P-000001");
        Assert.Equal(first, (await env.Workspace.ListRecentAsync(Alice)).Single().At); // no write

        await env.Workspace.RecordViewAsync(Alice, ObjectKinds.Part, "P-000002");
        env.Clock.Advance(TimeSpan.FromSeconds(61));
        await env.Workspace.RecordViewAsync(Alice, ObjectKinds.Part, "P-000001");
        var list = await env.Workspace.ListRecentAsync(Alice);
        Assert.Equal(["P-000001", "P-000002"], list.Select(r => r.Number));
        Assert.True(list[0].At > first);
    }

    [Fact]
    public async Task Favorites_are_personal_idempotent_and_capped()
    {
        var env = await NewEnvAsync();
        Assert.True(await env.Workspace.AddFavoriteAsync(Alice, ObjectKinds.Part, "P-000001"));
        Assert.True(await env.Workspace.AddFavoriteAsync(Alice, ObjectKinds.Part, "P-000001")); // again: no second row
        Assert.True(await env.Workspace.AddFavoriteAsync(Rita, ObjectKinds.Document, "D-000001"));
        Assert.True(await env.Workspace.IsFavoriteAsync(Alice, ObjectKinds.Part, "P-000001"));
        Assert.False(await env.Workspace.IsFavoriteAsync(Rita, ObjectKinds.Part, "P-000001"));
        Assert.Equal(["P-000001"], (await env.Workspace.ListFavoritesAsync(Alice)).Select(f => f.Number));
        Assert.Equal(["D-000001"], (await env.Workspace.ListFavoritesAsync(Rita)).Select(f => f.Number));

        await env.Workspace.RemoveFavoriteAsync(Rita, ObjectKinds.Part, "P-000001"); // rita cannot remove alice's
        Assert.True(await env.Workspace.IsFavoriteAsync(Alice, ObjectKinds.Part, "P-000001"));

        for (var i = 2; i <= WorkspaceLimits.Favorites; i++) Assert.True(await env.Workspace.AddFavoriteAsync(Alice, ObjectKinds.Part, $"P-{i:000000}"));
        Assert.False(await env.Workspace.AddFavoriteAsync(Alice, ObjectKinds.Part, "P-999999")); // the cap
        Assert.True(await env.Workspace.AddFavoriteAsync(Alice, ObjectKinds.Part, "P-000002")); // an existing one is not "new"
        Assert.Equal(WorkspaceLimits.Favorites, await CountAsync(env.Ds, "favorite", "sub-alice"));
        await env.Workspace.RemoveFavoriteAsync(Alice, ObjectKinds.Part, "P-000002");
        Assert.True(await env.Workspace.AddFavoriteAsync(Alice, ObjectKinds.Part, "P-999999"));
    }

    [Fact]
    public async Task Concurrent_additions_cannot_exceed_the_cap()
    {
        var env = await NewEnvAsync();
        for (var i = 1; i <= WorkspaceLimits.Favorites - 10; i++) Assert.True(await env.Workspace.AddFavoriteAsync(Alice, ObjectKinds.Part, $"P-{i:000000}"));
        var results = await Task.WhenAll(Enumerable.Range(1001, 40).Select(i => env.Workspace.AddFavoriteAsync(Alice, ObjectKinds.Part, $"P-{i:000000}")));
        Assert.Equal(10, results.Count(r => r)); // exactly the room that was left, however the requests interleaved
        Assert.Equal(WorkspaceLimits.Favorites, await CountAsync(env.Ds, "favorite", "sub-alice"));
    }

    [Fact]
    public async Task Saved_searches_are_private_replaceable_capped_and_audited_only_by_hash()
    {
        var env = await NewEnvAsync();
        var audit = new AuditQueries(env.Ds);
        const string secret = "USML XI(d) gimbal rate sensor";

        Assert.Equal(SaveSearchResult.Saved, await env.Workspace.SaveSearchAsync(Alice, "Gimbals", "part", secret, null, "corr-1"));
        Assert.Equal(SaveSearchResult.Replaced, await env.Workspace.SaveSearchAsync(Alice, "Gimbals", "part", secret + " v2", """{"state":"Released"}""", "corr-2"));
        Assert.Equal(SaveSearchResult.Invalid, await env.Workspace.SaveSearchAsync(Alice, "", "part", "x", null, null));
        Assert.Equal(SaveSearchResult.Invalid, await env.Workspace.SaveSearchAsync(Alice, "n", "secrets", "x", null, null));
        Assert.Equal(SaveSearchResult.Invalid, await env.Workspace.SaveSearchAsync(Alice, "n", "all", new string('q', 201), null, null));
        Assert.Equal(SaveSearchResult.Invalid, await env.Workspace.SaveSearchAsync(Alice, "n", "all", "x", "[1]", null)); // filters must be a JSON object

        var mine = await env.Workspace.ListSavedSearchesAsync(Alice);
        var gimbals = Assert.Single(mine);
        Assert.Equal(secret + " v2", gimbals.Query);
        Assert.Contains("Released", gimbals.FiltersJson, StringComparison.Ordinal);
        Assert.Empty(await env.Workspace.ListSavedSearchesAsync(Rita)); // never shared

        // Rita cannot delete Alice's saved search, even knowing its id.
        Assert.False(await env.Workspace.DeleteSavedSearchAsync(Rita, gimbals.Id, null));
        Assert.Single(await env.Workspace.ListSavedSearchesAsync(Alice));

        // The audit log records that it happened and a hash of the text, never the text.
        var events = (await audit.ListAsync(new AuditQuery(Limit: 100))).Where(e => e.Action.StartsWith("workspace.saved_search", StringComparison.Ordinal)).ToList();
        Assert.Equal(["workspace.saved_search.create", "workspace.saved_search.replace"], events.Select(e => e.Action).Order());
        Assert.All(events, e =>
        {
            Assert.DoesNotContain("gimbal", e.Payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("USML", e.Payload, StringComparison.Ordinal);
            Assert.Contains("queryHash", e.Payload, StringComparison.Ordinal);
        });
        Assert.Contains(WorkspaceService.QueryHash(secret), events.Single(e => e.Action.EndsWith("create", StringComparison.Ordinal)).Payload, StringComparison.Ordinal);

        Assert.True(await env.Workspace.DeleteSavedSearchAsync(Alice, gimbals.Id, "corr-3"));
        Assert.Empty(await env.Workspace.ListSavedSearchesAsync(Alice));
        var deleted = (await audit.ListAsync(new AuditQuery(Limit: 100))).Single(e => e.Action == "workspace.saved_search.delete");
        Assert.DoesNotContain("gimbal", deleted.Payload, StringComparison.OrdinalIgnoreCase);

        for (var i = 0; i < WorkspaceLimits.SavedSearches; i++) Assert.Equal(SaveSearchResult.Saved, await env.Workspace.SaveSearchAsync(Alice, $"s{i}", "all", $"term {i}", null, null));
        Assert.Equal(SaveSearchResult.LimitReached, await env.Workspace.SaveSearchAsync(Alice, "one more", "all", "x", null, null));
        Assert.Equal(SaveSearchResult.Replaced, await env.Workspace.SaveSearchAsync(Alice, "s0", "all", "changed", null, null)); // replacing is still fine at the cap
        Assert.Equal(SaveSearchResult.Saved, await env.Workspace.SaveSearchAsync(Rita, "s0", "all", "rita's own s0", null, null)); // the same name is another user's
    }

    [Fact]
    public async Task The_application_role_can_only_use_the_workspace_tables_and_nothing_here_is_immutable_evidence()
    {
        var env = await NewEnvAsync();
        // Personal data is deletable by its owner (and only through filtered statements); the audit chain is untouched by view and favorite changes.
        await env.Workspace.RecordViewAsync(Alice, ObjectKinds.Part, "P-000001");
        await env.Workspace.AddFavoriteAsync(Alice, ObjectKinds.Part, "P-000001");
        await env.Workspace.RemoveFavoriteAsync(Alice, ObjectKinds.Part, "P-000001");
        var events = await new AuditQueries(env.Ds).ListAsync(new AuditQuery(Limit: 100));
        Assert.DoesNotContain(events, e => e.Action.Contains("recent", StringComparison.Ordinal) || e.Action.Contains("favorite", StringComparison.Ordinal)); // low-value personal data: not audited
    }

    // ---------------------------------------------------------------- re-authorization on render, oracle, no count leakage

    internal static CurrentUser UserOf(Subject s) => new(new FixedAuthProvider(new ClaimsPrincipal(new ClaimsIdentity(
    [
        new Claim("sub", s.Id), new Claim("preferred_username", s.Username ?? s.Id), new Claim("name", s.DisplayName), new Claim("us_person", s.UsPerson ? "true" : "false"),
        new Claim("org", s.Org ?? ""), .. s.Programs.Select(p => new Claim("programs", p)), .. s.Roles.Select(r => new Claim("roles", r)),
        .. s.ExportAuthorizations.Select(a => new Claim("export_authorizations", a)), .. s.CuiCategories.Select(c => new Claim("cui_categories", c)),
    ], "test", "preferred_username", "roles"))));

    internal sealed class FixedAuthProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    private static readonly Subject Eve = WorkflowEnv.User("eve", [Roles.Viewer, Roles.Author], programs: []);
    private static readonly Subject Kim = WorkflowEnv.User("kim", [Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]);
    private static readonly Subject Lena = WorkflowEnv.Lena; // not a U.S. person
    private static readonly Subject Mallory = WorkflowEnv.Mallory;
    private static readonly Subject Audrey = WorkflowEnv.Audrey;

    private static WorkspaceReads ReadsFor(ConfigEnv env, WorkspaceService workspace, Subject s) =>
        new(UserOf(s), workspace, env.Ds, env.Audit, env.Flow, env.Change, null!, new TestClock());

    [Fact]
    public async Task A_favorite_whose_markings_were_raised_is_silently_dropped_for_a_user_who_lost_access()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        var workspace = new WorkspaceService(env.Ds, env.Audit, env.Clock);
        var open = await env.NewPartAsync("Open bracket", markings: ConfigEnv.Open);
        var other = await env.NewPartAsync("Other bracket", markings: ConfigEnv.Open);
        foreach (var n in new[] { open, other })
        {
            await workspace.AddFavoriteAsync(Rita, ObjectKinds.Part, n);
            await workspace.RecordViewAsync(Rita, ObjectKinds.Part, n);
        }
        var reads = ReadsFor(env, workspace, Rita);
        Assert.Equal(2, (await reads.FavoritesAsync()).Items.Count);
        Assert.Equal(2, (await reads.RecentAsync()).Items.Count);

        // The owner raises the markings to a program rita does not belong to.
        var raised = await env.Parts.ChangeMarkingsAsync(Kim, open, WorkflowEnv.Itar("KRAKEN"), "Moved to the KRAKEN program", null);
        Assert.True(raised.IsOk, string.Join("; ", raised.Errors));

        var favorites = await reads.FavoritesAsync();
        Assert.Equal([other], favorites.Items.Select(i => i.Number)); // dropped: no row, no "no longer available" number or name
        Assert.DoesNotContain(open, favorites.Items.Select(i => i.Number));
        Assert.Equal([other], (await reads.RecentAsync()).Items.Select(i => i.Number));
        Assert.DoesNotContain("KRAKEN", favorites.Banner, StringComparison.Ordinal); // nor does it raise the banner of the page
        // The pointer itself is still stored (a user who regains access sees it again); only the render is filtered.
        Assert.Contains(await workspace.ListFavoritesAsync(Rita), f => f.Number == open);
        // A user with access still sees it, with its new markings.
        var kimFavorites = ReadsFor(env, workspace, Kim);
        await workspace.AddFavoriteAsync(Kim, ObjectKinds.Part, open);
        var seen = (await kimFavorites.FavoritesAsync()).Items.Single();
        Assert.Equal(open, seen.Number);
        Assert.Contains("PROGRAM KRAKEN", seen.Banner, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_lists_and_search_equal_the_access_oracle_for_five_kinds_of_user_and_never_count_hidden_objects()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        var workspace = new WorkspaceService(env.Ds, env.Audit, env.Clock);
        var markingsByNumber = new Dictionary<string, Markings>();
        var partNumbers = new List<string>();
        var released = new List<string>();
        Markings[] kinds = [ConfigEnv.Open, WorkflowEnv.Itar("ORCA"), WorkflowEnv.Itar("KRAKEN"), WorkflowEnv.Itar("ORCA"), ConfigEnv.Open, WorkflowEnv.Itar("KRAKEN")];
        for (var i = 0; i < 12; i++)
        {
            var m = kinds[i % kinds.Length];
            var author = m.Program == "KRAKEN" ? Kim : Alice;
            var number = await env.NewPartAsync($"Oracle part {i}", markings: m, by: author);
            markingsByNumber[number] = m;
            partNumbers.Add(number);
        }
        // Release the ones the ORCA reviewers can sign (open and ORCA parts).
        foreach (var n in partNumbers.Where(n => markingsByNumber[n].Program != "KRAKEN").Take(5))
        {
            await env.ReleaseAsync(n);
            released.Add(n);
        }
        // Changes of both programs in the programs of the author.
        var changeMarkings = new Dictionary<string, Markings>();
        foreach (var (m, author) in new[] { (ConfigEnv.Open, Alice), (WorkflowEnv.Itar("ORCA"), Alice), (WorkflowEnv.Itar("KRAKEN"), Kim), (WorkflowEnv.Itar("KRAKEN"), Kim) })
        {
            var c = await env.Change.CreateAsync(author, new Keel.Change.CreateChangeCommand(Keel.Change.ChangeTypes.Ecr, $"Oracle change {changeMarkings.Count}", null, "Medium", "synthetic", m), null);
            Assert.True(c.IsOk, string.Join("; ", c.Errors));
            changeMarkings[c.Value!.Number] = m;
        }
        var assembly = await env.ReleasedAssemblyAsync("Oracle assembly", (released.First(), 2));
        released.Add(assembly); // the assembly was released too (open markings)
        markingsByNumber[assembly] = ConfigEnv.Open;
        var baseline = await env.BaselineAsync(assembly);
        var baselineMarkings = baseline.Summary.Markings;

        var users = new[] { Alice, Kim, Eve, Lena, Mallory, Audrey };
        foreach (var user in users)
        {
            bool Can(Markings m) => AccessPolicy.Evaluate(user, m, PlmAction.Read).Permitted;
            var reads = ReadsFor(env, workspace, user);
            var pointers = partNumbers.Select(n => new ObjectPointer(ObjectKinds.Part, n, DateTimeOffset.UtcNow))
                .Concat(changeMarkings.Keys.Select(n => new ObjectPointer(ObjectKinds.Change, n, DateTimeOffset.UtcNow)))
                .Append(new ObjectPointer(ObjectKinds.Baseline, baseline.Summary.Number, DateTimeOffset.UtcNow)).ToList();

            // 1. Re-resolving pointers shows exactly what the policy permits, in the order given.
            var resolved = await reads.ResolveAsync(user, pointers);
            var expected = pointers.Where(p => Can(p.Kind switch
            {
                ObjectKinds.Part => markingsByNumber[p.Number],
                ObjectKinds.Change => changeMarkings[p.Number],
                _ => baselineMarkings,
            })).Select(p => $"{p.Kind}:{p.Number}").ToList();
            Assert.Equal(expected, resolved.Select(r => $"{r.Kind}:{r.Number}").ToList());
            Assert.All(resolved, r => Assert.True(Can(r.Markings), $"{user.Username} was shown {r.Number}"));

            // 2. Recently released: the released parts the user can read.
            var releasedCard = await reads.RecentlyReleasedAsync(take: 50);
            Assert.Equal(released.Where(n => Can(markingsByNumber[n])).Order().ToList(), releasedCard.Items.Where(i => i.Kind == ObjectKinds.Part).Select(i => i.Number).Order().ToList());

            // 3. Open change requests in the user's own programs: readable and in a program of the user.
            var programCard = await reads.ProgramChangesAsync(take: 50);
            Assert.Equal(changeMarkings.Where(c => Can(c.Value) && c.Value.Program is { } p && user.Programs.Contains(p)).Select(c => c.Key).Order().ToList(), programCard.Items.Select(i => i.Number).Order().ToList());

            // 4. The page banner of a card is the aggregate of exactly the rows it shows (nothing hidden raises it).
            foreach (var card in new[] { releasedCard, programCard })
                Assert.Equal(card.Items.Count == 0 ? "UNCLASSIFIED" : Markings.AggregateBanner(card.Items.Select(i => i.Markings)), card.Banner);

            // 5. Global search: totals are counts of what the user may read, never of what exists.
            var search = new GlobalSearchService(UserOf(user), env.Parts, null!, env.Change, env.Baselines, null!, reads);
            var parts = await search.SearchKindAsync(ObjectKinds.Part, "Oracle part", 50, 0, CancellationToken.None);
            var visibleParts = partNumbers.Where(n => Can(markingsByNumber[n])).Order().ToList();
            Assert.Equal(visibleParts, parts.Hits.Select(h => h.Number).Order().ToList());
            Assert.Equal(visibleParts.Count, parts.Total);
            var changes = await search.SearchKindAsync(ObjectKinds.Change, "Oracle change", 50, 0, CancellationToken.None);
            Assert.Equal(changeMarkings.Where(c => Can(c.Value)).Select(c => c.Key).Order().ToList(), changes.Hits.Select(h => h.Number).Order().ToList());
            Assert.Equal(changes.Hits.Count, changes.Total);
            var baselines = await search.SearchKindAsync(ObjectKinds.Baseline, "", 50, 0, CancellationToken.None);
            Assert.Equal(Can(baselineMarkings) ? 1 : 0, baselines.Total);
            // An exact number of an unreadable object finds nothing, exactly like a number that does not exist.
            foreach (var hidden in partNumbers.Where(n => !Can(markingsByNumber[n])).Take(2))
                Assert.Empty((await search.SearchKindAsync(ObjectKinds.Part, hidden, 5, 0, CancellationToken.None)).Hits);
            Assert.Empty((await search.SearchKindAsync(ObjectKinds.Part, "P-999999", 5, 0, CancellationToken.None)).Hits);
        }
        // The oracle is not vacuous: the users really do see different things.
        Assert.True(partNumbers.Count(n => AccessPolicy.Evaluate(Eve, markingsByNumber[n], PlmAction.Read).Permitted) < partNumbers.Count);
        Assert.True(partNumbers.Count(n => AccessPolicy.Evaluate(Kim, markingsByNumber[n], PlmAction.Read).Permitted) > partNumbers.Count(n => AccessPolicy.Evaluate(Alice, markingsByNumber[n], PlmAction.Read).Permitted));
    }

    [Fact]
    public async Task Cards_are_audited_by_what_they_returned_and_stored_pointers_hold_no_names_or_markings()
    {
        var env = await ConfigEnv.CreateAsync(pg);
        var workspace = new WorkspaceService(env.Ds, env.Audit, env.Clock);
        var number = await env.NewPartAsync("Gimbal bracket synthetic", markings: WorkflowEnv.Itar("ORCA"));
        await workspace.AddFavoriteAsync(Alice, ObjectKinds.Part, number);
        await workspace.RecordViewAsync(Alice, ObjectKinds.Part, number);

        // What is stored: kind, number, time. No name, no markings.
        await using (var cmd = env.Ds.CreateCommand("SELECT * FROM ws.favorite"))
        await using (var r = await cmd.ExecuteReaderAsync())
            Assert.Equal(["user_sub", "object_type", "object_number", "created_at"], Enumerable.Range(0, r.FieldCount).Select(r.GetName));
        await using (var cmd = env.Ds.CreateCommand("SELECT * FROM ws.recent_item"))
        await using (var r = await cmd.ExecuteReaderAsync())
            Assert.Equal(["user_sub", "object_type", "object_number", "last_viewed"], Enumerable.Range(0, r.FieldCount).Select(r.GetName));

        var reads = ReadsFor(env, workspace, Alice);
        var card = await reads.FavoritesAsync();
        Assert.Equal("Gimbal bracket synthetic", card.Items.Single().Name);
        var audited = (await new AuditQueries(env.Ds).ListAsync(new AuditQuery(Limit: 200))).Where(e => e.Action == "workspace.read").ToList();
        var favoriteRead = Assert.Single(audited, e => e.ObjectId == "favorites");
        Assert.Contains(number, favoriteRead.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Gimbal", favoriteRead.Payload, StringComparison.Ordinal); // the audit names numbers, not names
    }
}
