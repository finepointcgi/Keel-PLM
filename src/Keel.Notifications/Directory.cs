using System.Collections.Immutable;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Notifications;

/// <summary>A user of the directory snapshot (<c>identity.user_directory</c>, A50) as a policy subject, with the names the UI shows.</summary>
public sealed record DirectoryEntry(string Sub, string Username, string DisplayName, Subject Subject);

/// <summary>
/// Reads the user directory the workflow module fills at sign-in. Notifications use it for what the workflow module uses it for: deciding, from the stored
/// attributes of a person who is not signed in right now, whether they could read an object (eligibility). The snapshot can be older than the person's live
/// token, which is why every notification is checked again with the live subject when it is shown.
/// </summary>
internal static class DirectoryReader
{
    private const string Columns = "sub, username, display_name, org, roles, us_person, citizenship, programs, export_authorizations, cui_categories";

    public static async Task<IReadOnlyDictionary<string, DirectoryEntry>> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, IReadOnlyCollection<string> subs, CancellationToken ct)
    {
        var result = new Dictionary<string, DirectoryEntry>(StringComparer.Ordinal);
        if (subs.Count == 0) return result;
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM identity.user_directory WHERE sub = ANY(@subs)", conn, tx);
        cmd.Parameters.AddWithValue("subs", subs.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { var e = Read(r); result[e.Sub] = e; }
        return result;
    }

    public static async Task<DirectoryEntry?> FindByUsernameAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string username, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM identity.user_directory WHERE lower(username) = lower(@u) LIMIT 1", conn, tx);
        cmd.Parameters.AddWithValue("u", username);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    /// <summary>Users whose login or name starts with the text (a picker's candidates), up to <paramref name="limit"/> rows. Eligibility is decided by the caller.</summary>
    public static async Task<IReadOnlyList<DirectoryEntry>> SearchAsync(NpgsqlConnection conn, string prefix, int limit, CancellationToken ct)
    {
        var like = Escape(prefix.Trim()) + "%";
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM identity.user_directory WHERE username ILIKE @like OR display_name ILIKE @like ORDER BY lower(username) LIMIT @limit", conn);
        cmd.Parameters.AddWithValue("like", like);
        cmd.Parameters.AddWithValue("limit", limit);
        var list = new List<DirectoryEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    private static string Escape(string s) => s.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);

    private static DirectoryEntry Read(NpgsqlDataReader r)
    {
        var sub = r.GetString(0);
        var username = r.GetString(1);
        var name = r.GetString(2);
        var subject = new Subject
        {
            Id = sub,
            DisplayName = name,
            Username = username,
            Org = r.IsDBNull(3) ? null : r.GetString(3),
            Roles = ((string[])r.GetValue(4)).ToImmutableSortedSet(StringComparer.Ordinal),
            UsPerson = r.GetBoolean(5),
            Citizenship = r.IsDBNull(6) ? null : r.GetString(6),
            Programs = ((string[])r.GetValue(7)).ToImmutableSortedSet(StringComparer.Ordinal),
            ExportAuthorizations = ((string[])r.GetValue(8)).ToImmutableSortedSet(StringComparer.Ordinal),
            CuiCategories = ((string[])r.GetValue(9)).ToImmutableSortedSet(StringComparer.Ordinal),
        };
        return new DirectoryEntry(sub, username, name, subject);
    }
}

/// <summary>
/// Finds objects through the <see cref="IObjectAccess"/> implementations of the owning modules and applies the access policy. A missing object and an object
/// the subject may not read are indistinguishable to every caller (null).
/// </summary>
public sealed class ObjectAccessResolver(IEnumerable<IObjectAccess> access)
{
    private readonly IObjectAccess[] _access = access.ToArray();

    /// <summary>The object, whatever the viewer may read: for system checks that then evaluate a subject themselves (fan-out). Callers must not show the result to a user without <see cref="CanRead"/>.</summary>
    public async Task<ObjectInfo?> FindAsync(string kind, string number, CancellationToken ct)
    {
        if (!AwarenessKinds.IsKnown(kind) || string.IsNullOrEmpty(number) || number.Length > 64) return null;
        var source = _access.FirstOrDefault(a => a.Kind == kind);
        return source is null ? null : await source.FindAsync(number, ct);
    }

    /// <summary>The object when <paramref name="subject"/> may read it right now, otherwise null.</summary>
    public async Task<ObjectInfo?> ReadableAsync(Subject subject, string kind, string number, CancellationToken ct)
    {
        var info = await FindAsync(kind, number, ct);
        return info is not null && CanRead(subject, info.Markings) ? info : null;
    }

    public static bool CanRead(Subject subject, Markings markings) => AccessPolicy.Evaluate(subject, markings, PlmAction.Read).Permitted;
}
