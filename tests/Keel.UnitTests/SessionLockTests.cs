using System.Collections.Immutable;
using System.Security.Claims;
using Bunit;
using Keel.Audit;
using Keel.Core;
using Keel.TestSupport;
using Keel.Web.Components.Layout;
using Keel.Web.Components.Shared;
using Keel.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Keel.UnitTests;

/// <summary>An audit log that only remembers what it was asked to write.</summary>
internal sealed class RecordingAudit : IAuditLog
{
    private readonly List<AuditEntry> _entries = [];
    public IReadOnlyList<AuditEntry> Entries { get { lock (_entries) return [.. _entries]; } }
    public IEnumerable<string> Actions => Entries.Select(e => e.Action);

    public Task<AuditRecord> AppendAsync(NpgsqlTransaction transaction, AuditEntry entry, CancellationToken ct = default) => AppendAsync(entry, ct);

    public Task<AuditRecord> AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        lock (_entries) _entries.Add(entry);
        return Task.FromResult(new AuditRecord(_entries.Count, Guid.NewGuid(), DateTimeOffset.UtcNow, entry.Actor.Id, entry.Action, entry.ObjectType, entry.ObjectId, entry.Outcome,
            entry.CorrelationId, "{}", new byte[32], new byte[32]));
    }
}

/// <summary>The idle lock state machine (3.1.10 / 3.1.11) with a manual clock, and the components that show it.</summary>
public sealed class SessionLockTests : BunitContext
{
    private static readonly SessionLockOptions Options = new() { IdleLockMinutes = 15, WarningSeconds = 60, LockedTerminateMinutes = 10 };
    private static readonly ActorSnapshot Alice = new("sub-alice", "alice", true, "USA", "ENG", [], [], ImmutableSortedSet.Create(StringComparer.Ordinal, "plm-viewer"));

    private static (SessionLockRegistry Registry, ManualTimeProvider Clock, RecordingAudit Audit) NewRegistry(SessionLockOptions? options = null)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
        var audit = new RecordingAudit();
        return (new SessionLockRegistry(options ?? Options, clock, audit), clock, audit);
    }

    private static DateTimeOffset Expiry(ManualTimeProvider clock) => clock.GetUtcNow().AddHours(8);

    // ---------------------------------------------------------------- state machine

    [Fact]
    public async Task An_idle_session_locks_at_the_timeout_and_the_lock_is_audited()
    {
        var (registry, clock, audit) = NewRegistry();
        registry.Register("s1", Alice, Expiry(clock));
        var changed = new List<string>();
        registry.Changed += changed.Add;

        clock.Advance(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(59));
        await registry.TickAsync();
        Assert.False(registry.IsLocked("s1"));

        clock.Advance(TimeSpan.FromSeconds(1));
        await registry.TickAsync();

        Assert.True(registry.IsLocked("s1"));
        Assert.Equal(SessionPhase.Locked, registry.Get("s1")!.Phase);
        Assert.Equal("idle", registry.Get("s1")!.LockReason);
        Assert.Equal(["s1"], changed);
        var lockEvent = Assert.Single(audit.Entries, e => e.Action == "session.lock");
        Assert.Equal(("sub-alice", AuditOutcome.Success, "idle"), (lockEvent.Actor.Id, lockEvent.Outcome, lockEvent.Details["reason"]));
        Assert.Equal(SessionLockRegistry.Reference("s1"), lockEvent.ObjectId); // a reference, never the session id itself
        Assert.DoesNotContain("s1", lockEvent.ObjectId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Activity_restarts_the_idle_clock_and_the_warning_window_is_reported()
    {
        var (registry, clock, _) = NewRegistry();
        registry.Register("s1", Alice, Expiry(clock));

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(registry.Touch("s1"));
        clock.Advance(TimeSpan.FromMinutes(10)); // 20 minutes since sign-in, 10 since the last activity
        await registry.TickAsync();
        Assert.False(registry.IsLocked("s1"));

        Assert.Null(registry.Get("s1")!.WarningSecondsLeft(clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(30)); // 14:30 idle: inside the last minute
        var view = registry.Get("s1")!;
        Assert.Equal(30, view.WarningSecondsLeft(clock.GetUtcNow()));
        Assert.True(registry.Touch("s1")); // the user is back: the warning is gone
        Assert.Null(registry.Get("s1")!.WarningSecondsLeft(clock.GetUtcNow()));
    }

    [Fact]
    public async Task A_locked_session_is_not_revived_by_activity_and_unlock_needs_the_reauthentication_path()
    {
        var (registry, clock, audit) = NewRegistry();
        registry.Register("s1", Alice, Expiry(clock));
        Assert.True(await registry.LockNowAsync("s1"));
        Assert.Equal("manual", registry.Get("s1")!.LockReason);

        Assert.False(registry.Touch("s1"));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(registry.Touch("s1"));
        Assert.True(registry.IsLocked("s1"));
        Assert.False(await registry.LockNowAsync("s1")); // already locked: no second event

        Assert.True(await registry.UnlockAsync("s1", "step-up"));
        Assert.False(registry.IsLocked("s1"));
        var unlock = Assert.Single(audit.Entries, e => e.Action == "session.unlock");
        Assert.Equal("step-up", unlock.Details["via"]);
        Assert.False(await registry.UnlockAsync("s1", "step-up")); // nothing to unlock any more
        Assert.Equal(["session.lock", "session.unlock"], audit.Actions);

        // The idle clock restarts at the unlock, not at the old activity.
        clock.Advance(TimeSpan.FromMinutes(14));
        await registry.TickAsync();
        Assert.False(registry.IsLocked("s1"));
    }

    [Fact]
    public async Task A_session_locked_longer_than_the_grace_is_terminated_and_cannot_be_unlocked()
    {
        var (registry, clock, audit) = NewRegistry();
        registry.Register("s1", Alice, Expiry(clock));
        clock.Advance(TimeSpan.FromMinutes(15));
        await registry.TickAsync();
        Assert.Equal(SessionPhase.Locked, registry.Get("s1")!.Phase);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromMinutes(10), registry.Get("s1")!.TerminateAt);

        clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        await registry.TickAsync();
        Assert.False(registry.IsTerminated("s1"));
        clock.Advance(TimeSpan.FromSeconds(1));
        await registry.TickAsync();

        Assert.True(registry.IsTerminated("s1"));
        Assert.Equal(["session.lock", "session.lock_expired", "session.terminated_idle"], audit.Actions);
        Assert.False(await registry.UnlockAsync("s1", "step-up")); // too late: a new sign-in is needed
        Assert.True(registry.IsLocked("s1"));
    }

    [Fact]
    public async Task Sessions_are_independent_and_circuits_of_one_session_share_its_lock()
    {
        var (registry, clock, _) = NewRegistry();
        registry.Register("a", Alice, Expiry(clock));
        registry.Register("b", Alice with { Id = "sub-bob" }, Expiry(clock));
        registry.Register("a", Alice, Expiry(clock)); // a second tab: same entry, not a new clock

        clock.Advance(TimeSpan.FromMinutes(8));
        registry.Touch("b");
        clock.Advance(TimeSpan.FromMinutes(7));
        await registry.TickAsync();

        Assert.True(registry.IsLocked("a"));
        Assert.False(registry.IsLocked("b"));
        Assert.Null(registry.Get("unknown"));
    }

    [Fact]
    public async Task The_timer_service_locks_a_session_nobody_is_connected_to()
    {
        var (registry, clock, _) = NewRegistry();
        registry.Register("s1", Alice, Expiry(clock));
        var service = new SessionLockService(registry, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionLockService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(300); // the host runs ExecuteAsync in the background: let its timer be created before the clock moves

        clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(2)); // the one-second timer fires on the manual clock
        for (var i = 0; i < 100 && !registry.IsLocked("s1"); i++) await Task.Delay(20);

        Assert.True(registry.IsLocked("s1"));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Disabled_lock_never_locks_and_expired_sessions_are_forgotten()
    {
        var (registry, clock, _) = NewRegistry(new SessionLockOptions { IdleLockMinutes = 0 });
        registry.Register("s1", Alice, clock.GetUtcNow().AddHours(1));
        clock.Advance(TimeSpan.FromHours(3));
        await registry.TickAsync();
        Assert.False(registry.IsLocked("s1"));
        Assert.Null(registry.Get("s1")); // past its absolute lifetime (plus a margin)
    }

    // ---------------------------------------------------------------- configuration

    [Fact]
    public void Defaults_are_fifteen_minutes_with_a_one_minute_warning_and_a_fifteen_minute_grace()
    {
        var o = new SessionLockOptions();
        Assert.Equal((15.0, 60, 15.0), (o.IdleLockMinutes, o.WarningSeconds, o.LockedTerminateMinutes));
        Assert.Empty(o.Validate(production: true));
    }

    [Fact]
    public void The_lock_cannot_be_switched_off_in_production()
    {
        var off = new SessionLockOptions { IdleLockMinutes = 0 };
        Assert.Contains(off.Validate(production: true), e => e.Contains("refused in production", StringComparison.Ordinal));
        Assert.Empty(off.Validate(production: false));
        Assert.NotEmpty(new SessionLockOptions { IdleLockMinutes = 0.5 }.Validate(production: true));
        Assert.Empty(new SessionLockOptions { IdleLockMinutes = 0.1, WarningSeconds = 3, LockedTerminateMinutes = 0.2 }.Validate(production: false));
        Assert.NotEmpty(new SessionLockOptions { IdleLockMinutes = 200 }.Validate(false));
        Assert.NotEmpty(new SessionLockOptions { IdleLockMinutes = 1, WarningSeconds = 60 }.Validate(false)); // warning as long as the timeout
        Assert.NotEmpty(new SessionLockOptions { LockedTerminateMinutes = 0 }.Validate(false));
    }

    // ---------------------------------------------------------------- the circuit's refusal

    private sealed class FixedAuth(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    private static ClaimsPrincipal Principal(string? sessionId, DateTimeOffset? expires = null) => new(new ClaimsIdentity(
        [
            new Claim("sub", "sub-alice"), new Claim("preferred_username", "alice"), new Claim("name", "Alice Example"), new Claim("roles", "plm-viewer"),
            new Claim("roles", "plm-admin"), new Claim("roles", "plm-auditor"), new Claim("us_person", "true"),
            .. sessionId is null ? Array.Empty<Claim>() : [new Claim(KeelUi.SessionIdClaim, sessionId)],
            new Claim(KeelUi.SessionExpiresClaim, (expires ?? DateTimeOffset.UtcNow.AddHours(8)).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ], "test", "preferred_username", "roles"));

    [Fact]
    public async Task Current_user_fails_closed_while_the_session_is_locked()
    {
        var (registry, clock, _) = NewRegistry();
        var auth = new FixedAuth(Principal("s1", Expiry(clock)));
        using var coordinator = new SessionLockCoordinator(registry, auth);
        var current = new CurrentUser(auth, coordinator);
        await coordinator.InitializeAsync();

        Assert.Equal("sub-alice", (await current.GetAsync()).Id);
        await registry.LockNowAsync("s1");
        await Assert.ThrowsAsync<SessionLockedException>(() => current.GetAsync());
        await registry.UnlockAsync("s1", "step-up");
        Assert.Equal("sub-alice", (await current.GetAsync()).Id);
    }

    [Fact]
    public async Task A_session_that_cannot_be_identified_is_refused_when_the_lock_is_on()
    {
        var (registry, _, _) = NewRegistry();
        var auth = new FixedAuth(Principal(sessionId: null));
        using var coordinator = new SessionLockCoordinator(registry, auth);
        await Assert.ThrowsAsync<SessionLockedException>(() => new CurrentUser(auth, coordinator).GetAsync());

        var disabled = new SessionLockRegistry(new SessionLockOptions { IdleLockMinutes = 0 }, TimeProvider.System);
        using var off = new SessionLockCoordinator(disabled, auth);
        Assert.Equal("sub-alice", (await new CurrentUser(auth, off).GetAsync()).Id);
        Assert.Equal("sub-alice", (await new CurrentUser(auth).GetAsync()).Id); // no coordinator registered at all (tests, tools)
    }

    [Fact]
    public async Task The_authentication_state_reports_the_lock()
    {
        var (registry, clock, _) = NewRegistry();
        registry.Register("s1", Alice, Expiry(clock));
        var provider = new SessionRevalidatingStateProvider(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, clock, registry);
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(Principal("s1", Expiry(clock)))));

        Assert.False((await provider.GetAuthenticationStateAsync()).User.HasClaim(c => c.Type == KeelUi.LockedClaim));
        await registry.LockNowAsync("s1");
        var locked = (await provider.GetAuthenticationStateAsync()).User;
        Assert.True(locked.HasClaim(KeelUi.LockedClaim, "true"));
        Assert.Equal("sub-alice", locked.FindFirst("sub")!.Value); // identity intact: only the marker is added
    }

    // ---------------------------------------------------------------- components

    private sealed class AllowAll : Microsoft.AspNetCore.Authorization.IAuthorizationService
    {
        public Task<Microsoft.AspNetCore.Authorization.AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<Microsoft.AspNetCore.Authorization.IAuthorizationRequirement> requirements) =>
            Task.FromResult(Microsoft.AspNetCore.Authorization.AuthorizationResult.Success());

        public Task<Microsoft.AspNetCore.Authorization.AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(Microsoft.AspNetCore.Authorization.AuthorizationResult.Success());
    }

    private sealed class FakeAntiforgery : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => null;
    }

    private (SessionLockRegistry Registry, ManualTimeProvider Clock, SessionLockCoordinator Coordinator) Setup(string sessionId = "s1", SessionLockOptions? options = null)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
        var registry = new SessionLockRegistry(options ?? Options, clock, new RecordingAudit());
        var auth = new FixedAuth(Principal(sessionId, clock.GetUtcNow().AddHours(8)));
        Services.AddSingleton<TimeProvider>(clock);
        Services.AddSingleton(registry);
        Services.AddSingleton<AuthenticationStateProvider>(auth);
        Services.AddAuthorizationCore(o =>
        {
            o.AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin));
            o.AddPolicy(Policies.Auditor, p => p.RequireRole(Roles.Auditor));
        });
        Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationService, AllowAll>(); // the test principal holds every role
        Services.AddSingleton<AntiforgeryStateProvider, FakeAntiforgery>();
        Services.AddCascadingAuthenticationState();
        Services.AddScoped<SessionLockCoordinator>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Keel.Web/session-lock.js");
        // The shell (UX-1) also needs its own module, preference store, search provider and toast queue.
        _ = ShellTestKit.Register(this, Roles.Viewer, Roles.Admin, Roles.Auditor);
        Services.AddSingleton<AuthenticationStateProvider>(auth); // the session's principal, not the kit's default
        var coordinator = Services.GetRequiredService<SessionLockCoordinator>();
        coordinator.InitializeAsync().GetAwaiter().GetResult();
        return (registry, clock, coordinator);
    }

    [Fact]
    public async Task The_lock_screen_is_an_accessible_modal_dialog_with_no_page_data()
    {
        var (registry, _, _) = Setup();
        await registry.LockNowAsync("s1");
        var cut = Render<LockedPanel>(p => p.Add(x => x.View, registry.Get("s1")));

        var dialog = cut.Find("[role=dialog]");
        Assert.Equal("true", dialog.GetAttribute("aria-modal"));
        Assert.Equal("lock-title", dialog.GetAttribute("aria-labelledby"));
        Assert.Equal("lock-description", dialog.GetAttribute("aria-describedby"));
        Assert.Equal("-1", dialog.GetAttribute("tabindex"));
        Assert.Equal("Session locked", cut.Find("#lock-title").TextContent);
        Assert.NotNull(cut.Find("#lock-description"));
        // Reachable by keyboard: native buttons, in a sensible order.
        Assert.Equal(["Sign in to unlock", "Sign out"], cut.FindAll("button").Select(b => b.TextContent.Trim()));
        Assert.Contains("10 min", cut.Find("[data-lock-countdown]").TextContent, StringComparison.Ordinal);
        // The trap is requested from the browser module once the dialog is on screen.
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "trapFocus");
    }

    [Fact]
    public async Task Unlocking_goes_through_the_idp_and_returns_to_the_same_page()
    {
        var (registry, _, _) = Setup();
        await registry.LockNowAsync("s1");
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/parts/P-000123?tab=bom");
        var cut = Render<LockedPanel>(p => p.Add(x => x.View, registry.Get("s1")));

        cut.Find("[data-action=unlock]").Click();

        Assert.Equal("http://localhost/account/step-up?returnUrl=" + Uri.EscapeDataString("/parts/P-000123?tab=bom"), nav.Uri);
    }

    private IRenderedComponent<CascadingAuthenticationState> RenderLayout(string pageData)
    {
        return Render<CascadingAuthenticationState>(p => p.AddChildContent<MainLayout>(l => l.Add(x => x.Body, (RenderFragment)(b =>
        {
            b.OpenElement(0, "article");
            b.AddAttribute(1, "id", "page-data");
            b.AddContent(2, pageData);
            b.CloseElement();
        }))));
    }

    [Fact]
    public async Task Locking_replaces_the_page_so_none_of_its_data_remains_in_the_dom()
    {
        var (registry, _, _) = Setup();
        var cut = RenderLayout("Bracket P-000123 ITAR USML XI(d)");
        Assert.Contains("Bracket P-000123", cut.Markup, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("nav[aria-label=Main]"));
        Assert.NotNull(cut.Find("[data-action=lock-now]"));
        Assert.Contains("Session expires in", cut.Find("[data-session-hint]").TextContent, StringComparison.Ordinal);

        await registry.LockNowAsync("s1");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[role=dialog]")));

        Assert.DoesNotContain("Bracket", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("P-000123", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("USML", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("#page-data"));
        Assert.Empty(cut.FindAll("nav")); // not even the navigation (admin entries would reveal roles)
        Assert.Empty(cut.FindAll("[data-action=lock-now]"));

        // Re-authenticating clears the lock and the page comes back (it is rendered again, from the services, under the user's access).
        await registry.UnlockAsync("s1", "step-up");
        cut.WaitForAssertion(() => Assert.Contains("Bracket P-000123", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[role=dialog]"));
    }

    [Fact]
    public async Task The_shell_has_a_skip_link_landmarks_and_a_palette_that_the_lock_takes_away()
    {
        var (registry, _, _) = Setup();
        var cut = RenderLayout("page body");

        Assert.EndsWith("#main", cut.Find("a.skip-link").GetAttribute("href"), StringComparison.Ordinal);
        Assert.NotNull(cut.Find("header.topbar"));
        Assert.NotNull(cut.Find("main#main"));
        Assert.NotNull(cut.Find("nav[aria-label=Main]"));
        Assert.NotNull(cut.Find("[data-action=open-palette]"));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "registerShortcuts"); // Ctrl/Cmd+K is listened for while unlocked

        await registry.LockNowAsync("s1");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-phase=locked]")));

        Assert.Empty(cut.FindAll("[data-action=open-palette]")); // no search while locked
        Assert.Empty(cut.FindAll("[data-palette]"));
        Assert.Empty(cut.FindAll("#user-menu-button")); // no user menu (roles, name) while locked
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "unregisterShortcuts"); // the shortcut listener is gone
    }

    [Fact]
    public async Task The_lock_now_button_and_the_idle_timer_both_lock_the_layout_and_a_second_circuit_follows()
    {
        var (registry, clock, _) = Setup();
        var cut = RenderLayout("secret page");

        cut.Find("[data-action=lock-now]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-phase=locked]")));
        Assert.True(registry.IsLocked("s1"));

        await registry.UnlockAsync("s1", "step-up");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-phase=locked]")));

        clock.Advance(TimeSpan.FromMinutes(15));
        await registry.TickAsync();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-phase=locked]")));
        Assert.DoesNotContain("secret page", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_warning_appears_shortly_before_the_lock_and_stay_signed_in_dismisses_it()
    {
        var (registry, clock, _) = Setup();
        var cut = Render<SessionStatus>();
        Assert.Empty(cut.FindAll("[data-session-warning]"));

        clock.Advance(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(10)); // 50 s left of the 15 minutes
        cut.WaitForAssertion(() => Assert.Contains("Locking in 50 s", cut.Find("[data-session-warning]").TextContent, StringComparison.Ordinal));
        Assert.Equal("alert", cut.Find("[data-session-warning]").GetAttribute("role"));

        cut.Find("[data-action=stay]").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-session-warning]")));
        Assert.False(registry.IsLocked("s1"));
        Assert.Null(registry.Get("s1")!.WarningSecondsLeft(clock.GetUtcNow()));
    }

    [Fact]
    public void The_hint_counts_down_to_the_absolute_session_lifetime()
    {
        var (_, clock, _) = Setup();
        var cut = Render<SessionStatus>();
        Assert.Equal("Session expires in 480 min", cut.Find("[data-session-hint]").TextContent);
        clock.Advance(TimeSpan.FromMinutes(61));
        cut.WaitForAssertion(() => Assert.Equal("Session expires in 419 min", cut.Find("[data-session-hint]").TextContent));
    }

    [Fact]
    public async Task A_circuit_that_connects_to_an_already_locked_session_starts_locked()
    {
        var (registry, _, _) = Setup();
        await registry.LockNowAsync("s1");
        var cut = RenderLayout("never rendered");
        Assert.NotNull(cut.Find("[data-phase=locked]"));
        Assert.DoesNotContain("never rendered", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_activity_reporter_module_is_loaded_with_the_configured_throttle()
    {
        Setup(options: new SessionLockOptions { IdleLockMinutes = 15, ActivityThrottleSeconds = 7 });
        Render<SessionActivity>();
        var start = Assert.Single(JSInterop.Invocations, i => i.Identifier == "start");
        Assert.Equal(7000, start.Arguments[1]);
    }
}
