using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Keel.Workflow;

/// <summary>A directory entry as shown to participant pickers: what the caller needs and nothing more.</summary>
public sealed record DirectoryUserView(string Sub, string Username, string DisplayName, IReadOnlyList<string> Roles);

/// <summary>The stored attribute snapshot of one user (ASSUMPTION A50).</summary>
public sealed record DirectoryUser(
    string Sub, string Username, string DisplayName, string? Org, IReadOnlyList<string> Roles, bool UsPerson, string? Citizenship,
    IReadOnlyList<string> Programs, IReadOnlyList<string> ExportAuthorizations, IReadOnlyList<string> CuiCategories, DateTimeOffset LastSeen)
{
    /// <summary>The user as a policy subject, built from the snapshot, for eligibility checks at assignment time.</summary>
    public Subject ToSubject() => new()
    {
        Id = Sub,
        DisplayName = DisplayName,
        Username = Username,
        UsPerson = UsPerson,
        Citizenship = Citizenship,
        Org = Org,
        Programs = Programs.ToImmutableSortedSet(StringComparer.Ordinal),
        ExportAuthorizations = ExportAuthorizations.ToImmutableSortedSet(StringComparer.Ordinal),
        CuiCategories = CuiCategories.ToImmutableSortedSet(StringComparer.Ordinal),
        Roles = Roles.ToImmutableSortedSet(StringComparer.Ordinal),
    };

    public DirectoryUserView ToView() => new(Sub, Username, DisplayName, Roles);
}

/// <summary>
/// There is no local user table: identities live in the IdP (A1). The directory records each user the application has
/// seen (UI sign-in, throttled API calls) so participants can be picked and checked before a task is assigned.
/// </summary>
public sealed class UserDirectory(NpgsqlDataSource dataSource, TimeProvider clock, WorkflowOptions options, ILogger<UserDirectory> logger)
{
    private readonly ConcurrentDictionary<string, (byte[] Fingerprint, DateTimeOffset At)> _recent = new(StringComparer.Ordinal);

    /// <summary>Upserts the user now. Used at UI sign-in, where fresh attributes arrive with the token.</summary>
    public async Task UpsertAsync(Subject s, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await using var cmd = dataSource.CreateCommand(
            """
            INSERT INTO identity.user_directory (sub, username, display_name, org, roles, us_person, citizenship, programs, export_authorizations, cui_categories, first_seen, last_seen)
            VALUES (@sub, @username, @name, @org, @roles, @us, @cit, @programs, @auths, @cui, @now, @now)
            ON CONFLICT (sub) DO UPDATE SET username = EXCLUDED.username, display_name = EXCLUDED.display_name, org = EXCLUDED.org,
                roles = EXCLUDED.roles, us_person = EXCLUDED.us_person, citizenship = EXCLUDED.citizenship, programs = EXCLUDED.programs,
                export_authorizations = EXCLUDED.export_authorizations, cui_categories = EXCLUDED.cui_categories, last_seen = EXCLUDED.last_seen
            """);
        cmd.Parameters.AddWithValue("sub", s.Id);
        cmd.Parameters.AddWithValue("username", s.Username ?? s.Id);
        cmd.Parameters.AddWithValue("name", s.DisplayName);
        cmd.Parameters.AddWithValue("org", (object?)s.Org ?? DBNull.Value);
        cmd.Parameters.AddWithValue("roles", s.Roles.ToArray());
        cmd.Parameters.AddWithValue("us", s.UsPerson);
        cmd.Parameters.AddWithValue("cit", (object?)s.Citizenship ?? DBNull.Value);
        cmd.Parameters.AddWithValue("programs", s.Programs.ToArray());
        cmd.Parameters.AddWithValue("auths", s.ExportAuthorizations.ToArray());
        cmd.Parameters.AddWithValue("cui", s.CuiCategories.ToArray());
        cmd.Parameters.AddWithValue("now", now);
        await cmd.ExecuteNonQueryAsync(ct);
        _recent[s.Id] = (Fingerprint(s), now);
    }

    /// <summary>
    /// Lazy refresh for authenticated API calls: writes when the user is new to this process, their attributes changed,
    /// or the last write is older than the refresh interval. Failures never fail the request.
    /// </summary>
    public async Task TouchAsync(Subject s, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        if (_recent.TryGetValue(s.Id, out var last)
            && now - last.At < TimeSpan.FromSeconds(options.DirectoryRefreshSeconds)
            && last.Fingerprint.AsSpan().SequenceEqual(Fingerprint(s)))
            return;
        try
        {
            await UpsertAsync(s, ct);
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            logger.LogWarning(e, "User directory refresh failed for a subject; continuing without it");
        }
    }

    public async Task<DirectoryUser?> FindAsync(string subOrUsername, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await FindAsync(conn, null, subOrUsername, ct);
    }

    internal static async Task<DirectoryUser?> FindAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string subOrUsername, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM identity.user_directory WHERE sub = @k OR lower(username) = lower(@k) ORDER BY (sub = @k) DESC LIMIT 1", conn, tx);
        cmd.Parameters.AddWithValue("k", subOrUsername);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    /// <summary>Directory search for pickers: users whose name or login contains the text, newest activity first.</summary>
    internal static async Task<List<DirectoryUser>> SearchAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string? text, int limit, CancellationToken ct)
    {
        var like = "%" + (text ?? "").Trim().Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%";
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM identity.user_directory WHERE username ILIKE @like OR display_name ILIKE @like ORDER BY lower(display_name), username LIMIT @limit", conn, tx);
        cmd.Parameters.AddWithValue("like", like);
        cmd.Parameters.AddWithValue("limit", limit);
        var list = new List<DirectoryUser>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    private const string Columns = "sub, username, display_name, org, roles, us_person, citizenship, programs, export_authorizations, cui_categories, last_seen";

    private static DirectoryUser Read(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), (string[])r.GetValue(4), r.GetBoolean(5),
        r.IsDBNull(6) ? null : r.GetString(6), (string[])r.GetValue(7), (string[])r.GetValue(8), (string[])r.GetValue(9),
        new DateTimeOffset(r.GetDateTime(10), TimeSpan.Zero));

    private static byte[] Fingerprint(Subject s) => SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f',
        s.Id, s.Username, s.DisplayName, s.Org, s.UsPerson, s.Citizenship, string.Join(',', s.Roles), string.Join(',', s.Programs),
        string.Join(',', s.ExportAuthorizations), string.Join(',', s.CuiCategories))));
}
