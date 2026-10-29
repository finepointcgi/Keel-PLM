using System.Security.Claims;
using Bunit;
using Keel.Core;
using Keel.Web.Services;
using Keel.Workspace;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.UnitTests;

/// <summary>Fakes and registrations shared by the UX-1 component tests (shell, palette, tables, feedback components).</summary>
internal static class ShellTestKit
{
    internal sealed class FixedAuth(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    public static ClaimsPrincipal Principal(params string[] roles) => new(new ClaimsIdentity(
        [new Claim("sub", "sub-alice"), new Claim("preferred_username", "alice"), new Claim("name", "Alice Example"), new Claim("us_person", "true"),
         .. roles.Select(r => new Claim("roles", r))], "test", "preferred_username", "roles"));

    /// <summary>An in-memory preference store: records what was saved so a test can check that a choice reached it.</summary>
    internal sealed class FakePreferenceStore : IPreferenceStore
    {
        public UserPreferences Preferences { get; set; } = UserPreferences.Default;
        public Dictionary<string, TablePreferences> Tables { get; } = [];
        public int PreferenceSaves { get; private set; }
        public int TableSaves { get; private set; }

        public Task<UserPreferences> GetPreferencesAsync(Subject subject, CancellationToken ct = default) => Task.FromResult(Preferences);

        public Task<UserPreferences> SetPreferencesAsync(Subject subject, UserPreferences preferences, CancellationToken ct = default)
        {
            Preferences = preferences;
            PreferenceSaves++;
            return Task.FromResult(preferences);
        }

        public Task<TablePreferences> GetTablePreferencesAsync(Subject subject, string tableId, CancellationToken ct = default) =>
            Task.FromResult(Tables.TryGetValue(tableId, out var t) ? t : TablePreferences.Default);

        public Task<bool> SetTablePreferencesAsync(Subject subject, string tableId, TablePreferences prefs, CancellationToken ct = default)
        {
            Tables[tableId] = prefs;
            TableSaves++;
            return Task.FromResult(true);
        }
    }

    /// <summary>A search provider that answers from a list, optionally slowly, and records every query it was asked.</summary>
    internal sealed class FakeSearch : IGlobalSearch
    {
        public List<string> Queries { get; } = [];
        public Func<string, CancellationToken, Task<IReadOnlyList<SearchHit>>>? Handler { get; set; }
        public IReadOnlyList<SearchHit> Recents { get; set; } = [];

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string rawQuery, int perKindLimit, CancellationToken ct)
        {
            lock (Queries) Queries.Add(rawQuery);
            return Handler is null ? Task.FromResult<IReadOnlyList<SearchHit>>([]) : Handler(rawQuery, ct);
        }

        public Task<KindResults> SearchKindAsync(string kind, string text, int limit, int offset, CancellationToken ct) => Task.FromResult(new KindResults(kind, [], 0, UiText.Unclassified));

        public Task<IReadOnlyList<SearchHit>> RecentAsync(int take, CancellationToken ct) => Task.FromResult(Recents);
    }

    public static SearchHit Hit(string kind, string number, string name, string banner = "UNCLASSIFIED · DISTRIBUTION A") =>
        new(kind, number, name, "Released", banner, ObjectKinds.Path(kind, number), ParsedQuery.RankOf(number, number));

    /// <summary>Registers what the shell, palette, tables and toasts inject, with JavaScript interop in loose mode (the browser module is not under test here).</summary>
    public static (FakePreferenceStore Store, FakeSearch Search) Register(BunitContext ctx, params string[] roles)
    {
        var store = new FakePreferenceStore();
        var search = new FakeSearch();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("./_content/Keel.Web/js/shell.js");
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new FixedAuth(Principal(roles)));
        ctx.Services.AddSingleton<IPreferenceStore>(store);
        ctx.Services.AddSingleton<IGlobalSearch>(search);
        ctx.Services.AddLogging();
        ctx.Services.AddScoped<CurrentUser>();
        ctx.Services.AddScoped<ShellJs>();
        ctx.Services.AddScoped<ShellState>();
        ctx.Services.AddScoped<PaletteState>();
        ctx.Services.AddScoped<ToastService>();
        return (store, search);
    }
}
