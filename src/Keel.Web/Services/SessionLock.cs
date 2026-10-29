using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Keel.Audit;
using Keel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Keel.Web.Services;

/// <summary>Configuration section <c>Keel:Session</c> (NIST SP 800-171 3.1.10 and 3.1.11, ADR-0026).</summary>
public sealed class SessionLockOptions
{
    public const string Section = "Keel:Session";

    /// <summary>Minutes without user activity before the session locks. 0 turns the lock off: refused in production.</summary>
    public double IdleLockMinutes { get; set; } = 15;
    /// <summary>The warning before the lock: the user is told the session is about to lock and can keep it open.</summary>
    public int WarningSeconds { get; set; } = 60;
    /// <summary>How long a session may stay locked before it is terminated (the cookie is rejected and the user signed out).</summary>
    public double LockedTerminateMinutes { get; set; } = 15;
    /// <summary>The browser reports activity at most this often (seconds).</summary>
    public int ActivityThrottleSeconds { get; set; } = 5;

    /// <summary>
    /// Where the lock state lives: <c>Database</c> (default; shared by all replicas and kept across restarts) or <c>Memory</c> (one node, state lost on restart;
    /// refused in production). Without a database in the host the memory store is used whatever this says.
    /// </summary>
    public string Store { get; set; } = "Database";

    public bool UsesDatabase => string.Equals(Store, "Database", StringComparison.OrdinalIgnoreCase);
    public bool Enabled => IdleLockMinutes > 0;
    public TimeSpan IdleTimeout => TimeSpan.FromMinutes(IdleLockMinutes);
    public TimeSpan Warning => TimeSpan.FromSeconds(WarningSeconds);
    public TimeSpan TerminateAfterLock => TimeSpan.FromMinutes(LockedTerminateMinutes);

    /// <param name="production">RequireFips or a Production environment: the lock cannot be switched off and runs on minutes, not seconds.</param>
    public IReadOnlyList<string> Validate(bool production)
    {
        var errors = new List<string>();
        if (IdleLockMinutes < 0 || IdleLockMinutes > 120) errors.Add("Keel:Session:IdleLockMinutes must be between 0 and 120.");
        else if (IdleLockMinutes == 0 && production)
            errors.Add("Keel:Session:IdleLockMinutes is 0 (idle lock off), which is refused in production (NIST SP 800-171 3.1.10). Use 1 to 120 minutes.");
        else if (production && IdleLockMinutes < 1) errors.Add("Keel:Session:IdleLockMinutes below 1 minute is only for development and tests.");
        if (WarningSeconds is < 0 or > 600) errors.Add("Keel:Session:WarningSeconds must be between 0 and 600.");
        else if (Enabled && WarningSeconds >= IdleLockMinutes * 60) errors.Add("Keel:Session:WarningSeconds must be shorter than the idle timeout.");
        if (LockedTerminateMinutes is <= 0 or > 24 * 60) errors.Add("Keel:Session:LockedTerminateMinutes must be greater than 0 and at most 1440.");
        else if (production && LockedTerminateMinutes < 1) errors.Add("Keel:Session:LockedTerminateMinutes below 1 minute is only for development and tests.");
        if (!UsesDatabase && !string.Equals(Store, "Memory", StringComparison.OrdinalIgnoreCase)) errors.Add("Keel:Session:Store must be Database or Memory.");
        else if (!UsesDatabase && production && Enabled) errors.Add("Keel:Session:Store=Memory is refused in production: the lock state would be lost on restart and not shared between replicas.");
        if (ActivityThrottleSeconds is < 1 or > 60) errors.Add("Keel:Session:ActivityThrottleSeconds must be between 1 and 60.");
        return errors;
    }
}

public enum SessionPhase
{
    /// <summary>In use.</summary>
    Active,
    /// <summary>No activity for the idle timeout, or locked by the user. Everything is refused until the user re-authenticates at the IdP.</summary>
    Locked,
    /// <summary>Locked for longer than <see cref="SessionLockOptions.LockedTerminateMinutes"/>: the session is over, a new sign-in is needed.</summary>
    Terminated,
}

/// <summary>What the UI needs to know about one session's lock state.</summary>
public sealed record SessionLockView(SessionPhase Phase, string? LockReason, DateTimeOffset LastActivity, DateTimeOffset? IdleDeadline,
    DateTimeOffset? WarningStart, DateTimeOffset? LockedAt, DateTimeOffset? TerminateAt, DateTimeOffset AbsoluteExpiry)
{
    public bool IsLocked => Phase != SessionPhase.Active;

    /// <summary>Seconds until the lock, while the warning period is running; null otherwise.</summary>
    public int? WarningSecondsLeft(DateTimeOffset now) =>
        Phase == SessionPhase.Active && WarningStart is { } w && IdleDeadline is { } d && now >= w && now < d ? (int)Math.Ceiling((d - now).TotalSeconds) : null;
}

/// <summary>Raised by <see cref="CurrentUser"/> and the guards when the session is locked: nothing may be read or changed until the user signs in again.</summary>
public sealed class SessionLockedException() : InvalidOperationException("The session is locked. Sign in again to unlock it.");

/// <summary>
/// The server-side idle lock of the UI session (3.1.10) and its termination (3.1.11), shared by every circuit and request of one session cookie.
/// The state is authoritative here: a timer locks the session even when the browser is dead or its script blocked; the browser only reports activity.
/// Locking is per session id (a claim of the cookie), so a second tab locks and unlocks with the first.
/// The state lives in an <see cref="ISessionLockStore"/>: PostgreSQL in the application (it survives a restart and is shared by every replica, so any node
/// may tick, lock and unlock, and each transition is audited once), memory for tests and development. Reads on the request path (<see cref="IsLocked"/>)
/// come from a cache of the store that every node refreshes each tick, so a lock made on another node is enforced here within about one second;
/// a session the cache has never seen is looked up in the store until the first load has completed (docs/increments/7.5-platform.md, GAP-B-1).
/// </summary>
public sealed class SessionLockRegistry
{
    private static readonly TimeSpan ExpiryGrace = TimeSpan.FromMinutes(5);

    private readonly SessionLockOptions _options;
    private readonly TimeProvider _clock;
    private readonly IAuditLog? _audit;
    private readonly ILogger<SessionLockRegistry>? _logger;
    private readonly ISessionLockStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _dirty = new(StringComparer.Ordinal);
    private volatile bool _loaded;

    public SessionLockRegistry(SessionLockOptions options, TimeProvider clock, IAuditLog? audit = null, ILogger<SessionLockRegistry>? logger = null, ISessionLockStore? store = null)
    {
        _options = options;
        _clock = clock;
        _audit = audit;
        _logger = logger;
        _store = store ?? new InMemorySessionLockStore();
        _loaded = !_store.Durable; // a memory store is empty and complete from the start
    }

    private sealed class Entry(SessionLockRow row)
    {
        public string Key { get; } = row.Key;
        /// <summary>The raw session id, known only after this node saw the session (a circuit or request registered it): listeners compare against it.</summary>
        public string? SessionId { get; set; }
        public SessionLockRow Row { get; set; } = row;
    }

    private sealed record Transition(Entry Entry, string Action, (string, string?)[] Details);

    public SessionLockOptions Options => _options;
    public bool Enabled => _options.Enabled;

    /// <summary>Raised (from any thread) with the session id whenever a session's phase changes, here or (seen on the next tick) on another node.</summary>
    public event Action<string>? Changed;

    /// <summary>The store key of a session: SHA-256 of the id, hex. The raw id is never stored or logged.</summary>
    public static string Key(string sessionId) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));

    /// <summary>A stable, non-secret reference to a session for audit records (the session id itself stays out of the log).</summary>
    public static string Reference(string sessionId) => Key(sessionId)[..12];

    /// <summary>Loads the store into the cache once at start-up, before the first request is judged. A failure is logged; lookups then go to the store until a tick succeeds.</summary>
    public void Load()
    {
        try { RefreshAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is not OperationCanceledException) { _logger?.LogError(e, "The session lock store could not be loaded; sessions are looked up one by one until it can."); }
    }

    /// <summary>A circuit (or the first request) of this session. Idempotent; a new session starts active with the clock at now. Blocks on the store: prefer <see cref="RegisterAsync"/>.</summary>
    public void Register(string sessionId, ActorSnapshot actor, DateTimeOffset absoluteExpiry) =>
        RegisterAsync(sessionId, actor, absoluteExpiry).GetAwaiter().GetResult();

    public async Task RegisterAsync(string sessionId, ActorSnapshot actor, DateTimeOffset absoluteExpiry, CancellationToken ct = default)
    {
        var row = await _store.RegisterAsync(Key(sessionId), actor, _clock.GetUtcNow(), absoluteExpiry, ct);
        var changed = false;
        lock (_gate)
        {
            if (_cache.TryGetValue(row.Key, out var e))
            {
                changed = e.Row.Phase != row.Phase;
                e.Row = Merge(e.Row, row);
                e.SessionId = sessionId;
            }
            else _cache[row.Key] = new Entry(row) { SessionId = sessionId };
        }
        if (changed) RaiseChanged(sessionId);
    }

    /// <summary>The store's row, except that activity reported here and not yet written is newer than the store's clock.</summary>
    private static SessionLockRow Merge(SessionLockRow local, SessionLockRow stored) =>
        local.Phase == SessionPhase.Active && stored.Phase == SessionPhase.Active && local.LastActivity > stored.LastActivity ? stored with { LastActivity = local.LastActivity } : stored;

    /// <summary>User activity in the browser. Counts only while the session is active: a locked session is not revived by input. Written to the store on the next tick.</summary>
    public bool Touch(string sessionId)
    {
        var key = Key(sessionId);
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out var e) || e.Row.Phase != SessionPhase.Active) return false;
            var now = _clock.GetUtcNow();
            e.Row = e.Row with { LastActivity = now };
            _dirty[key] = now;
            return true;
        }
    }

    public SessionLockView? Get(string sessionId)
    {
        var e = Find(sessionId);
        if (e is null) return null;
        var row = e.Row;
        DateTimeOffset? idle = _options.Enabled ? row.LastActivity + _options.IdleTimeout : null;
        DateTimeOffset? warn = idle is { } d && _options.WarningSeconds > 0 ? d - _options.Warning : null;
        DateTimeOffset? terminate = row.Phase == SessionPhase.Locked ? row.LockedAt + _options.TerminateAfterLock : null;
        return new SessionLockView(row.Phase, row.LockReason, row.LastActivity, row.Phase == SessionPhase.Active ? idle : null, row.Phase == SessionPhase.Active ? warn : null,
            row.LockedAt, terminate, row.AbsoluteExpiry);
    }

    public bool IsLocked(string sessionId) => Find(sessionId) is { } e && e.Row.Phase != SessionPhase.Active;

    public bool IsTerminated(string sessionId) => Find(sessionId) is { } e && e.Row.Phase == SessionPhase.Terminated;

    /// <summary>The cached entry. Until the first successful load, an unknown session is read from the store, and a store that cannot answer means locked (fail closed).</summary>
    private Entry? Find(string sessionId)
    {
        var key = Key(sessionId);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var e)) return e;
            if (_loaded) return null;
        }
        try
        {
            var row = _store.FindAsync(key, CancellationToken.None).GetAwaiter().GetResult();
            return row is null ? null : new Entry(row);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "The session lock store could not be read; treating the session as locked.");
            var now = _clock.GetUtcNow();
            return new Entry(new SessionLockRow(key, ActorSnapshot.System, SessionPhase.Locked, "store_unavailable", now, now, now.AddHours(1)));
        }
    }

    /// <summary>The user chose "Lock now".</summary>
    public async Task<bool> LockNowAsync(string sessionId, CancellationToken ct = default)
    {
        var key = Key(sessionId);
        await FlushTouchesAsync(ct);
        var row = await _store.TryLockAsync(key, "manual", _clock.GetUtcNow(), ct);
        if (row is null)
        {
            await SyncOneAsync(key, sessionId, ct);
            return false;
        }
        var t = Apply(row, sessionId, "session.lock", [("reason", "manual")]);
        await PublishAsync([t], ct);
        return true;
    }

    /// <summary>
    /// The user re-authenticated at the IdP (prompt=login, max_age=0, same subject, verified by the OIDC callback): the lock is cleared.
    /// This is the only way out of <see cref="SessionPhase.Locked"/>.
    /// </summary>
    public async Task<bool> UnlockAsync(string sessionId, string via, CancellationToken ct = default)
    {
        var key = Key(sessionId);
        var now = _clock.GetUtcNow();
        var before = await _store.FindAsync(key, ct);
        var row = await _store.TryUnlockAsync(key, now, ct);
        if (row is null)
        {
            await SyncOneAsync(key, sessionId, ct);
            return false;
        }
        var lockedFor = before?.LockedAt is { } at ? now - at : TimeSpan.Zero;
        var t = Apply(row, sessionId, "session.unlock", [("via", via), ("lockedSeconds", ((int)lockedFor.TotalSeconds).ToString(CultureInfo.InvariantCulture))]);
        await PublishAsync([t], ct);
        return true;
    }

    /// <summary>
    /// Evaluates every session against the clock: idle sessions lock, sessions locked for too long end, and the changes other nodes made are picked up.
    /// Called every second by <see cref="SessionLockService"/> on every node: each transition is one conditional store operation, so it happens, and is audited, once.
    /// </summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var transitions = new List<Transition>();
        await FlushTouchesAsync(ct);
        if (_options.Enabled)
            foreach (var row in await _store.LockIdleAsync(now - _options.IdleTimeout, now, ct))
                transitions.Add(Apply(row, null, "session.lock", [("reason", "idle")]));
        foreach (var row in await _store.TerminateAsync(now - _options.TerminateAfterLock, ct))
        {
            var lockedFor = row.LockedAt is { } at ? now - at : TimeSpan.Zero;
            var expired = Apply(row, null, "session.lock_expired", [("lockedSeconds", ((int)lockedFor.TotalSeconds).ToString(CultureInfo.InvariantCulture))]);
            transitions.Add(expired);
            transitions.Add(new Transition(expired.Entry, "session.terminated_idle", [("reason", "lock_not_released")]));
        }
        await _store.PurgeAsync(now - ExpiryGrace, ct); // the cookie is invalid by then anyway
        await RefreshAsync(ct);
        if (transitions.Count > 0) await PublishAsync(transitions, ct);
    }

    private async Task FlushTouchesAsync(CancellationToken ct)
    {
        KeyValuePair<string, DateTimeOffset>[] pending;
        lock (_gate)
        {
            if (_dirty.Count == 0) return;
            pending = [.. _dirty];
            _dirty.Clear();
        }
        try { await _store.TouchAsync(pending, ct); }
        catch
        {
            lock (_gate) foreach (var (k, v) in pending) if (!_dirty.TryGetValue(k, out var newer) || newer < v) _dirty[k] = v; // try again next tick
            throw;
        }
    }

    /// <summary>Replaces the cache with the store's rows; a phase another node changed raises <see cref="Changed"/> for sessions this node knows.</summary>
    private async Task RefreshAsync(CancellationToken ct)
    {
        var rows = await _store.LoadAllAsync(_clock.GetUtcNow(), ExpiryGrace, ct);
        var raised = new List<string>();
        lock (_gate)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                seen.Add(row.Key);
                if (!_cache.TryGetValue(row.Key, out var e)) { _cache[row.Key] = new Entry(row); continue; }
                var phaseChanged = e.Row.Phase != row.Phase;
                e.Row = Merge(e.Row, row);
                if (phaseChanged && e.SessionId is { } id) raised.Add(id);
            }
            foreach (var key in _cache.Keys.Where(k => !seen.Contains(k)).ToList()) _cache.Remove(key);
            _loaded = true;
        }
        foreach (var id in raised) RaiseChanged(id);
    }

    private async Task SyncOneAsync(string key, string sessionId, CancellationToken ct)
    {
        var row = await _store.FindAsync(key, ct);
        if (row is null) return;
        var changed = false;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var e)) { changed = e.Row.Phase != row.Phase; e.Row = row; e.SessionId ??= sessionId; }
            else _cache[key] = new Entry(row) { SessionId = sessionId };
        }
        if (changed) RaiseChanged(sessionId);
    }

    private Transition Apply(SessionLockRow row, string? sessionId, string action, (string, string?)[] details)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(row.Key, out var e)) _cache[row.Key] = e = new Entry(row);
            else e.Row = row;
            if (sessionId is not null) e.SessionId = sessionId;
            return new Transition(e, action, details);
        }
    }

    private async Task PublishAsync(IReadOnlyList<Transition> transitions, CancellationToken ct)
    {
        foreach (var t in transitions)
        {
            if (_audit is not null)
            {
                try
                {
                    await _audit.AppendAsync(new AuditEntry
                    {
                        Actor = t.Entry.Row.Actor, Action = t.Action, Outcome = AuditOutcome.Success, ObjectType = "session", ObjectId = t.Entry.Key[..12],
                        Details = AuditDetails.Of(t.Details),
                    }, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A failing audit store must not leave a session open: the state change already happened.
                    _logger?.LogError(e, "Could not audit {Action}", t.Action);
                }
            }
            if (t.Entry.SessionId is { } id) RaiseChanged(id);
        }
    }

    private void RaiseChanged(string sessionId)
    {
        try { Changed?.Invoke(sessionId); }
        catch (Exception e) { _logger?.LogError(e, "A session lock listener failed"); }
    }
}

/// <summary>Drives <see cref="SessionLockRegistry.TickAsync"/> once a second, independent of any circuit: the lock happens even when nobody is connected.</summary>
public sealed class SessionLockService(SessionLockRegistry registry, TimeProvider clock, ILogger<SessionLockService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await registry.TickAsync(stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException) { logger.LogError(e, "Session lock tick failed"); }
        }
    }
}

/// <summary>
/// Per-circuit view of the session's lock state, for the layout and the guards. Locking itself lives in <see cref="SessionLockRegistry"/>;
/// this class finds the session id in the circuit's principal, registers the circuit, relays changes and forwards the browser's activity reports.
/// </summary>
public sealed class SessionLockCoordinator : IDisposable
{
    private readonly SessionLockRegistry _registry;
    private readonly AuthenticationStateProvider _auth;
    private string? _sessionId;
    private bool _subscribed;
    private bool _initialized;

    public SessionLockCoordinator(SessionLockRegistry registry, AuthenticationStateProvider auth)
    {
        _registry = registry;
        _auth = auth;
    }

    public SessionLockOptions Options => _registry.Options;
    public bool Enabled => _registry.Enabled;

    /// <summary>Raised on any thread when this session's phase changes; the layout re-renders through <c>InvokeAsync</c>.</summary>
    public event Action? Changed;

    public static string? SessionId(ClaimsPrincipal user) => user.FindFirst(KeelUi.SessionIdClaim)?.Value;

    /// <summary>When the session ends whatever the user does (the absolute lifetime of the cookie); null until <see cref="InitializeAsync"/> read it.</summary>
    public DateTimeOffset? AbsoluteExpiry { get; private set; }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        var user = (await _auth.GetAuthenticationStateAsync()).User;
        if (long.TryParse(user.FindFirst(KeelUi.SessionExpiresClaim)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exp))
            AbsoluteExpiry = DateTimeOffset.FromUnixTimeSeconds(exp);
        if (!Enabled) return;
        _sessionId = SessionId(user);
        if (_sessionId is null) return;
        var subject = SubjectClaims.FromPrincipal(user);
        await _registry.RegisterAsync(_sessionId, subject is null ? ActorSnapshot.System : ActorSnapshot.From(subject), AbsoluteExpiry ?? DateTimeOffset.UtcNow.AddHours(8));
        _registry.Changed += OnRegistryChanged;
        _subscribed = true;
        // A circuit that connects to a session that is already locked (reload, second tab, restart of the browser) starts locked.
        if (_registry.IsLocked(_sessionId)) Changed?.Invoke();
    }

    private void OnRegistryChanged(string id)
    {
        if (string.Equals(id, _sessionId, StringComparison.Ordinal)) Changed?.Invoke();
    }

    public SessionLockView? View => _sessionId is null ? null : _registry.Get(_sessionId);
    public bool IsLocked => View?.IsLocked == true;
    public bool IsTerminated => View?.Phase == SessionPhase.Terminated;

    public void ReportActivity()
    {
        if (_sessionId is not null && _registry.Touch(_sessionId)) Changed?.Invoke(); // clears a running warning
    }

    /// <summary>The browser came back to the foreground: refresh the screen from the server's state (not counted as activity).</summary>
    public void Refresh() => Changed?.Invoke();

    public Task<bool> LockNowAsync() => _sessionId is null ? Task.FromResult(false) : _registry.LockNowAsync(_sessionId);

    /// <summary>Throws <see cref="SessionLockedException"/> when <paramref name="user"/>'s session is locked or ended. Called by every UI service entry (CurrentUser).</summary>
    public void ThrowIfLocked(ClaimsPrincipal user)
    {
        if (!Enabled) return;
        var id = SessionId(user);
        // A session that cannot be identified cannot be proven unlocked: fail closed.
        if (id is null || _registry.IsLocked(id)) throw new SessionLockedException();
    }

    public void Dispose()
    {
        if (_subscribed) _registry.Changed -= OnRegistryChanged;
    }
}

/// <summary>Registration and request pipeline of the idle lock.</summary>
public static class SessionLockSetup
{
    /// <summary>Validates <c>Keel:Session</c> (the lock cannot be off in production) and registers the registry, its timer and the per-circuit coordinator.</summary>
    public static IServiceCollection AddKeelSessionLock(this IServiceCollection services, SessionLockOptions options, bool production)
    {
        var errors = options.Validate(production);
        if (errors.Count > 0) throw new InvalidOperationException("Invalid session configuration: " + string.Join(" ", errors));
        services.AddSingleton(options);
        services.AddSingleton<ISessionLockStore>(sp => options.UsesDatabase && sp.GetService<NpgsqlDataSource>() is { } ds ? new PostgresSessionLockStore(ds) : new InMemorySessionLockStore());
        services.AddSingleton(sp =>
        {
            var registry = new SessionLockRegistry(options, sp.GetRequiredService<TimeProvider>(), sp.GetService<IAuditLog>(), sp.GetService<ILogger<SessionLockRegistry>>(), sp.GetRequiredService<ISessionLockStore>());
            registry.Load(); // the persisted state is in the cache before the first request is judged
            return registry;
        });
        if (options.Enabled) services.AddHostedService<SessionLockService>();
        services.AddScoped<SessionLockCoordinator>();
        return services;
    }

    private static readonly string[] AllowedPrefixes = ["/_blazor", "/_framework", "/_content", "/account", "/health", "/metrics", "/signin-oidc", "/signout-callback-oidc"];

    /// <summary>
    /// While a session is locked, its cookie may load the application shell (so the lock screen can appear and the user can sign in again), the
    /// framework files and the sign-in endpoints, and nothing else: a script or a pasted URL gets 423 for downloads and any other cookie endpoint,
    /// including endpoints added by later increments. Bearer-token API requests have no session and are not affected. Call after authentication.
    /// </summary>
    public static IApplicationBuilder UseKeelSessionLock(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var registry = ctx.RequestServices.GetService<SessionLockRegistry>();
        if (registry is { Enabled: true } && ctx.User.FindFirst(KeelUi.SessionIdClaim)?.Value is { } id && registry.IsLocked(id) && !AllowedWhileLocked(ctx))
        {
            ctx.Response.StatusCode = StatusCodes.Status423Locked;
            ctx.Response.ContentType = "application/problem+json";
            await ctx.Response.WriteAsync("{\"status\":423,\"title\":\"Session locked\",\"detail\":\"Sign in again to unlock the session.\"}");
            return;
        }
        await next(ctx);
    });

    private static bool AllowedWhileLocked(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        if (AllowedPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase))) return true;
        if (Path.HasExtension(path.Value)) return true; // static files (stylesheets, scripts, fonts)
        // The Razor component pages are an empty shell until a circuit starts (no prerendering, ADR-0014); the circuit refuses service calls while locked.
        // Only a page load (GET): a form post to a page route is not needed by the lock screen.
        return (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
            && ctx.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Components.Endpoints.ComponentTypeMetadata>() is not null;
    }
}
