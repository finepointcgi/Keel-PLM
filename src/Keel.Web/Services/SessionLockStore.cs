using System.Collections.Immutable;
using System.Text.Json;
using Keel.Audit;
using Npgsql;
using NpgsqlTypes;

namespace Keel.Web.Services;

/// <summary>
/// One session as the lock store holds it. The key is the SHA-256 of the session id (hex): the id itself is never stored, so a reader of the table cannot
/// replay a session, and the first 12 characters are the reference the audit log uses.
/// </summary>
public sealed record SessionLockRow(string Key, ActorSnapshot Actor, SessionPhase Phase, string? LockReason, DateTimeOffset LastActivity, DateTimeOffset? LockedAt, DateTimeOffset AbsoluteExpiry);

/// <summary>
/// Where the state of the idle lock lives (ADR-0026, ADR-0032 note on GAP-B-1). Every transition is one atomic, conditional operation that returns the rows it
/// changed, so several nodes can tick, lock and unlock the same sessions and each transition (and its audit event) happens exactly once.
/// </summary>
public interface ISessionLockStore
{
    /// <summary>True when the state outlives the process and is shared between nodes (the database store).</summary>
    bool Durable { get; }

    /// <summary>Creates the session (active, clock at <paramref name="now"/>) or, when it exists, only extends its absolute expiry. Returns the stored row.</summary>
    Task<SessionLockRow> RegisterAsync(string key, ActorSnapshot actor, DateTimeOffset now, DateTimeOffset absoluteExpiry, CancellationToken ct);

    Task<SessionLockRow?> FindAsync(string key, CancellationToken ct);

    /// <summary>Every session whose cookie can still be valid (absolute expiry plus a grace period not yet passed).</summary>
    Task<IReadOnlyList<SessionLockRow>> LoadAllAsync(DateTimeOffset now, TimeSpan grace, CancellationToken ct);

    /// <summary>Moves the idle clock of active sessions forward (never backward). Locked and terminated sessions are not revived.</summary>
    Task TouchAsync(IReadOnlyCollection<KeyValuePair<string, DateTimeOffset>> activity, CancellationToken ct);

    /// <summary>Locks the session if it is active ("Lock now"); null when it was not.</summary>
    Task<SessionLockRow?> TryLockAsync(string key, string reason, DateTimeOffset now, CancellationToken ct);

    /// <summary>Releases a locked session (re-authentication); null when it was not locked. The idle clock restarts at <paramref name="now"/>.</summary>
    Task<SessionLockRow?> TryUnlockAsync(string key, DateTimeOffset now, CancellationToken ct);

    /// <summary>Locks every active session idle since <paramref name="idleSince"/> or earlier; returns the rows it locked.</summary>
    Task<IReadOnlyList<SessionLockRow>> LockIdleAsync(DateTimeOffset idleSince, DateTimeOffset now, CancellationToken ct);

    /// <summary>Terminates every locked session locked at <paramref name="lockedSince"/> or earlier; returns those rows.</summary>
    Task<IReadOnlyList<SessionLockRow>> TerminateAsync(DateTimeOffset lockedSince, CancellationToken ct);

    /// <summary>Forgets sessions whose absolute expiry passed before <paramref name="expiredBefore"/>.</summary>
    Task<int> PurgeAsync(DateTimeOffset expiredBefore, CancellationToken ct);
}

/// <summary>The store of a single node, development and the unit tests: the state dies with the process and is not shared.</summary>
public sealed class InMemorySessionLockStore : ISessionLockStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionLockRow> _rows = new(StringComparer.Ordinal);

    public bool Durable => false;

    public Task<SessionLockRow> RegisterAsync(string key, ActorSnapshot actor, DateTimeOffset now, DateTimeOffset absoluteExpiry, CancellationToken ct)
    {
        lock (_gate)
        {
            var row = _rows.TryGetValue(key, out var existing)
                ? existing with { AbsoluteExpiry = absoluteExpiry }
                : new SessionLockRow(key, actor, SessionPhase.Active, null, now, null, absoluteExpiry);
            _rows[key] = row;
            return Task.FromResult(row);
        }
    }

    public Task<SessionLockRow?> FindAsync(string key, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_rows.GetValueOrDefault(key));
    }

    public Task<IReadOnlyList<SessionLockRow>> LoadAllAsync(DateTimeOffset now, TimeSpan grace, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<SessionLockRow>>([.. _rows.Values.Where(r => now < r.AbsoluteExpiry + grace)]);
    }

    public Task TouchAsync(IReadOnlyCollection<KeyValuePair<string, DateTimeOffset>> activity, CancellationToken ct)
    {
        lock (_gate)
            foreach (var (key, at) in activity)
                if (_rows.TryGetValue(key, out var r) && r.Phase == SessionPhase.Active && at > r.LastActivity) _rows[key] = r with { LastActivity = at };
        return Task.CompletedTask;
    }

    public Task<SessionLockRow?> TryLockAsync(string key, string reason, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(key, out var r) || r.Phase != SessionPhase.Active) return Task.FromResult<SessionLockRow?>(null);
            return Task.FromResult<SessionLockRow?>(_rows[key] = r with { Phase = SessionPhase.Locked, LockReason = reason, LockedAt = now });
        }
    }

    public Task<SessionLockRow?> TryUnlockAsync(string key, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(key, out var r) || r.Phase != SessionPhase.Locked) return Task.FromResult<SessionLockRow?>(null);
            return Task.FromResult<SessionLockRow?>(_rows[key] = r with { Phase = SessionPhase.Active, LockReason = null, LockedAt = null, LastActivity = now });
        }
    }

    public Task<IReadOnlyList<SessionLockRow>> LockIdleAsync(DateTimeOffset idleSince, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            var changed = new List<SessionLockRow>();
            foreach (var (key, r) in _rows.ToList())
                if (r.Phase == SessionPhase.Active && r.LastActivity <= idleSince)
                    changed.Add(_rows[key] = r with { Phase = SessionPhase.Locked, LockReason = "idle", LockedAt = now });
            return Task.FromResult<IReadOnlyList<SessionLockRow>>(changed);
        }
    }

    public Task<IReadOnlyList<SessionLockRow>> TerminateAsync(DateTimeOffset lockedSince, CancellationToken ct)
    {
        lock (_gate)
        {
            var changed = new List<SessionLockRow>();
            foreach (var (key, r) in _rows.ToList())
                if (r.Phase == SessionPhase.Locked && r.LockedAt <= lockedSince)
                    changed.Add(_rows[key] = r with { Phase = SessionPhase.Terminated });
            return Task.FromResult<IReadOnlyList<SessionLockRow>>(changed);
        }
    }

    public Task<int> PurgeAsync(DateTimeOffset expiredBefore, CancellationToken ct)
    {
        lock (_gate)
        {
            var gone = _rows.Where(p => p.Value.AbsoluteExpiry < expiredBefore).Select(p => p.Key).ToList();
            foreach (var k in gone) _rows.Remove(k);
            return Task.FromResult(gone.Count);
        }
    }
}

/// <summary>
/// The lock state in PostgreSQL (<c>identity.session_lock</c>, migration 0016): it survives a restart and is shared by every node, so a session locked or
/// unlocked on one node is locked or unlocked on all of them (each node picks the change up on its next one-second tick). Time comes from the callers'
/// clock, so nodes need synchronized clocks (they already do for token validation).
/// </summary>
public sealed class PostgresSessionLockStore(NpgsqlDataSource dataSource) : ISessionLockStore
{
    private const string Columns = "session_key, actor, phase, lock_reason, last_activity, locked_at, absolute_expiry";

    public bool Durable => true;

    public async Task<SessionLockRow> RegisterAsync(string key, ActorSnapshot actor, DateTimeOffset now, DateTimeOffset absoluteExpiry, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"""
            INSERT INTO identity.session_lock (session_key, actor, phase, last_activity, absolute_expiry, created_at, updated_at)
            VALUES (@k, @actor, 'active', @now, @exp, @now, @now)
            ON CONFLICT (session_key) DO UPDATE SET absolute_expiry = EXCLUDED.absolute_expiry, updated_at = EXCLUDED.updated_at
            RETURNING {Columns}
            """);
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.Add(new NpgsqlParameter("actor", NpgsqlDbType.Jsonb) { Value = ActorJson.Serialize(actor) });
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        cmd.Parameters.AddWithValue("exp", absoluteExpiry.ToUniversalTime());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return Read(r);
    }

    public async Task<SessionLockRow?> FindAsync(string key, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {Columns} FROM identity.session_lock WHERE session_key = @k");
        cmd.Parameters.AddWithValue("k", key);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<IReadOnlyList<SessionLockRow>> LoadAllAsync(DateTimeOffset now, TimeSpan grace, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {Columns} FROM identity.session_lock WHERE absolute_expiry > @cut");
        cmd.Parameters.AddWithValue("cut", (now - grace).ToUniversalTime());
        return await ReadAllAsync(cmd, ct);
    }

    public async Task TouchAsync(IReadOnlyCollection<KeyValuePair<string, DateTimeOffset>> activity, CancellationToken ct)
    {
        if (activity.Count == 0) return;
        await using var cmd = dataSource.CreateCommand(
            """
            UPDATE identity.session_lock t SET last_activity = v.at, updated_at = v.at
            FROM unnest(@keys, @ats) AS v(key, at)
            WHERE t.session_key = v.key AND t.phase = 'active' AND t.last_activity < v.at
            """);
        cmd.Parameters.AddWithValue("keys", activity.Select(a => a.Key).ToArray());
        cmd.Parameters.AddWithValue("ats", activity.Select(a => a.Value.ToUniversalTime()).ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SessionLockRow?> TryLockAsync(string key, string reason, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"UPDATE identity.session_lock SET phase = 'locked', lock_reason = @reason, locked_at = @now, updated_at = @now WHERE session_key = @k AND phase = 'active' RETURNING {Columns}");
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.AddWithValue("reason", reason);
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        return await ReadAllAsync(cmd, ct) is [var one, ..] ? one : null;
    }

    public async Task<SessionLockRow?> TryUnlockAsync(string key, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"UPDATE identity.session_lock SET phase = 'active', lock_reason = NULL, locked_at = NULL, last_activity = @now, updated_at = @now WHERE session_key = @k AND phase = 'locked' RETURNING {Columns}");
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        return await ReadAllAsync(cmd, ct) is [var one, ..] ? one : null;
    }

    public async Task<IReadOnlyList<SessionLockRow>> LockIdleAsync(DateTimeOffset idleSince, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"UPDATE identity.session_lock SET phase = 'locked', lock_reason = 'idle', locked_at = @now, updated_at = @now WHERE phase = 'active' AND last_activity <= @since RETURNING {Columns}");
        cmd.Parameters.AddWithValue("since", idleSince.ToUniversalTime());
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<SessionLockRow>> TerminateAsync(DateTimeOffset lockedSince, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"UPDATE identity.session_lock SET phase = 'terminated', updated_at = locked_at + (@since - locked_at) WHERE phase = 'locked' AND locked_at <= @since RETURNING {Columns}");
        cmd.Parameters.AddWithValue("since", lockedSince.ToUniversalTime());
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<int> PurgeAsync(DateTimeOffset expiredBefore, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM identity.session_lock WHERE absolute_expiry < @cut");
        cmd.Parameters.AddWithValue("cut", expiredBefore.ToUniversalTime());
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<SessionLockRow>> ReadAllAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var rows = new List<SessionLockRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(Read(r));
        return rows;
    }

    private static SessionLockRow Read(NpgsqlDataReader r) => new(
        r.GetString(0), ActorJson.Deserialize(r.GetString(1)),
        r.GetString(2) switch { "locked" => SessionPhase.Locked, "terminated" => SessionPhase.Terminated, _ => SessionPhase.Active },
        r.IsDBNull(3) ? null : r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5), r.GetFieldValue<DateTimeOffset>(6));

    /// <summary>The actor is kept so another node can write the audit event of a transition with the identity that owned the session.</summary>
    private static class ActorJson
    {
        private sealed record Dto(string? Id, string? Name, bool UsPerson, string? Citizenship, string? Org, string[]? Programs, string[]? ExportAuthorizations, string[]? Roles);

        public static string Serialize(ActorSnapshot a) =>
            JsonSerializer.Serialize(new Dto(a.Id, a.Name, a.UsPerson, a.Citizenship, a.Org, [.. a.Programs], [.. a.ExportAuthorizations], [.. a.Roles]));

        public static ActorSnapshot Deserialize(string json)
        {
            // A damaged row must not stop the whole lock registry from refreshing: it reads as an unknown actor, and the session still locks and unlocks normally.
            Dto? d = null;
            try { d = JsonSerializer.Deserialize<Dto>(json); }
            catch (JsonException) { /* handled below */ }
            ImmutableSortedSet<string> Set(string[]? v) => ImmutableSortedSet.CreateRange(StringComparer.Ordinal, v ?? []);
            return d?.Id is null
                ? ActorSnapshot.System with { Id = "unknown", Name = "unreadable session record" }
                : new ActorSnapshot(d.Id, d.Name, d.UsPerson, d.Citizenship, d.Org, Set(d.Programs), Set(d.ExportAuthorizations), Set(d.Roles));
        }
    }
}
