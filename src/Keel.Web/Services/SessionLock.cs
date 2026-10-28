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
/// Locking is per session id (a claim of the cookie), so a second tab locks and unlocks with the first. The registry is in memory: one node, or
/// sticky sessions; a DB-backed registry is the Phase 5 step for several replicas (docs/increments/5.0-compliance.md, CMP-4).
/// </summary>
public sealed class SessionLockRegistry
{
    private readonly SessionLockOptions _options;
    private readonly TimeProvider _clock;
    private readonly IAuditLog? _audit;
    private readonly ILogger<SessionLockRegistry>? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    public SessionLockRegistry(SessionLockOptions options, TimeProvider clock, IAuditLog? audit = null, ILogger<SessionLockRegistry>? logger = null)
    {
        _options = options;
        _clock = clock;
        _audit = audit;
        _logger = logger;
    }

    private sealed class Entry(ActorSnapshot actor, DateTimeOffset lastActivity, DateTimeOffset absoluteExpiry)
    {
        public ActorSnapshot Actor { get; } = actor;
        public DateTimeOffset LastActivity { get; set; } = lastActivity;
        public DateTimeOffset AbsoluteExpiry { get; set; } = absoluteExpiry;
        public SessionPhase Phase { get; set; } = SessionPhase.Active;
        public string? LockReason { get; set; }
        public DateTimeOffset? LockedAt { get; set; }
    }

    private sealed record Transition(string SessionId, string Action, Entry Entry, (string, string?)[] Details);

    public SessionLockOptions Options => _options;
    public bool Enabled => _options.Enabled;

    /// <summary>Raised (from any thread) with the session id whenever a session's phase changes.</summary>
    public event Action<string>? Changed;

    /// <summary>A circuit (or the first request) of this session. Idempotent; a new session starts active with the clock at now.</summary>
    public void Register(string sessionId, ActorSnapshot actor, DateTimeOffset absoluteExpiry)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var existing)) { existing.AbsoluteExpiry = absoluteExpiry; return; }
            _sessions[sessionId] = new Entry(actor, _clock.GetUtcNow(), absoluteExpiry);
        }
    }

    /// <summary>User activity in the browser. Counts only while the session is active: a locked session is not revived by input.</summary>
    public bool Touch(string sessionId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var e) || e.Phase != SessionPhase.Active) return false;
            e.LastActivity = _clock.GetUtcNow();
            return true;
        }
    }

    public SessionLockView? Get(string sessionId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var e)) return null;
            DateTimeOffset? idle = _options.Enabled ? e.LastActivity + _options.IdleTimeout : null;
            DateTimeOffset? warn = idle is { } d && _options.WarningSeconds > 0 ? d - _options.Warning : null;
            DateTimeOffset? terminate = e.Phase == SessionPhase.Locked ? e.LockedAt + _options.TerminateAfterLock : null;
            return new SessionLockView(e.Phase, e.LockReason, e.LastActivity, e.Phase == SessionPhase.Active ? idle : null, e.Phase == SessionPhase.Active ? warn : null,
                e.LockedAt, terminate, e.AbsoluteExpiry);
        }
    }

    public bool IsLocked(string sessionId)
    {
        lock (_gate) return _sessions.TryGetValue(sessionId, out var e) && e.Phase != SessionPhase.Active;
    }

    public bool IsTerminated(string sessionId)
    {
        lock (_gate) return _sessions.TryGetValue(sessionId, out var e) && e.Phase == SessionPhase.Terminated;
    }

    /// <summary>The user chose "Lock now".</summary>
    public async Task<bool> LockNowAsync(string sessionId, CancellationToken ct = default)
    {
        Transition? t;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var e) || e.Phase != SessionPhase.Active) return false;
            t = Lock(sessionId, e, "manual", _clock.GetUtcNow());
        }
        await PublishAsync([t], ct);
        return true;
    }

    /// <summary>
    /// The user re-authenticated at the IdP (prompt=login, max_age=0, same subject, verified by the OIDC callback): the lock is cleared.
    /// This is the only way out of <see cref="SessionPhase.Locked"/>.
    /// </summary>
    public async Task<bool> UnlockAsync(string sessionId, string via, CancellationToken ct = default)
    {
        Transition? t;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var e) || e.Phase != SessionPhase.Locked) return false;
            var lockedFor = _clock.GetUtcNow() - e.LockedAt!.Value;
            e.Phase = SessionPhase.Active;
            e.LastActivity = _clock.GetUtcNow();
            e.LockedAt = null;
            e.LockReason = null;
            t = new Transition(sessionId, "session.unlock", e, [("via", via), ("lockedSeconds", ((int)lockedFor.TotalSeconds).ToString(CultureInfo.InvariantCulture))]);
        }
        await PublishAsync([t], ct);
        return true;
    }

    /// <summary>Evaluates every session against the clock: idle sessions lock, sessions locked for too long end. Called every second by <see cref="SessionLockService"/>.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        var transitions = new List<Transition>();
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            foreach (var (id, e) in _sessions.ToList())
            {
                if (e.Phase == SessionPhase.Active && _options.Enabled && now >= e.LastActivity + _options.IdleTimeout)
                    transitions.Add(Lock(id, e, "idle", now));
                else if (e.Phase == SessionPhase.Locked && now >= e.LockedAt!.Value + _options.TerminateAfterLock)
                {
                    e.Phase = SessionPhase.Terminated;
                    transitions.Add(new Transition(id, "session.lock_expired", e, [("lockedSeconds", ((int)(now - e.LockedAt.Value).TotalSeconds).ToString(CultureInfo.InvariantCulture))]));
                    transitions.Add(new Transition(id, "session.terminated_idle", e, [("reason", "lock_not_released")]));
                }
                if (now >= e.AbsoluteExpiry.AddMinutes(5)) _sessions.Remove(id); // the cookie is invalid by then anyway
            }
        }
        if (transitions.Count > 0) await PublishAsync(transitions, ct);
    }

    private static Transition Lock(string id, Entry e, string reason, DateTimeOffset now)
    {
        e.Phase = SessionPhase.Locked;
        e.LockedAt = now;
        e.LockReason = reason;
        return new Transition(id, "session.lock", e, [("reason", reason)]);
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
                        Actor = t.Entry.Actor, Action = t.Action, Outcome = AuditOutcome.Success, ObjectType = "session", ObjectId = Reference(t.SessionId),
                        Details = AuditDetails.Of(t.Details),
                    }, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A failing audit store must not leave a session open: the state change already happened.
                    _logger?.LogError(e, "Could not audit {Action}", t.Action);
                }
            }
            try { Changed?.Invoke(t.SessionId); }
            catch (Exception e) { _logger?.LogError(e, "A session lock listener failed"); }
        }
    }

    /// <summary>A stable, non-secret reference to a session for audit records (the session id itself stays out of the log).</summary>
    public static string Reference(string sessionId) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)))[..12];
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
        _registry.Register(_sessionId, subject is null ? ActorSnapshot.System : ActorSnapshot.From(subject), AbsoluteExpiry ?? DateTimeOffset.UtcNow.AddHours(8));
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
        services.AddSingleton(sp => new SessionLockRegistry(options, sp.GetRequiredService<TimeProvider>(), sp.GetService<IAuditLog>(), sp.GetService<ILogger<SessionLockRegistry>>()));
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
