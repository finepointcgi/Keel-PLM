using System.Data.Common;

namespace Keel.Core;

/// <summary>Names of the administrator-maintained reference lists (table <c>cfg.ref_item</c>, migration 0009).</summary>
public static class RefLists
{
    public const string PartType = "part-type";
    public const string PartSource = "part-source";
    public const string Unit = "unit";
    public const string DocumentType = "document-type";
    public const string ChangePriority = "change-priority";
    public const string ChangeClass = "change-class";
    /// <summary>Picker list of distribution controlling offices. Free text stays valid: this only helps users choose (ADM-9).</summary>
    public const string Office = "vocab.office";
    /// <summary>Picker list of programs. Programs stay security attributes released by the IdP (A2); this list is only a UI picker (ADM-9).</summary>
    public const string Program = "vocab.program";

    public static readonly IReadOnlyList<string> All = [PartType, PartSource, Unit, DocumentType, ChangePriority, ChangeClass, Office, Program];
}

/// <summary>One item of a reference list. Retired items stay valid on existing objects but are not offered for new ones.</summary>
public sealed record RefItem(string List, string Code, string Label, int SortOrder, bool IsActive, string? LifecycleFamily = null);

/// <summary>Cached, read-only access to the reference lists for pickers and metadata endpoints (implemented in Keel.Admin).</summary>
public interface IReferenceData
{
    Task<IReadOnlyList<RefItem>> ListAsync(string list, bool includeRetired = false, CancellationToken ct = default);

    /// <summary>Active codes in display order.</summary>
    async Task<IReadOnlyList<string>> CodesAsync(string list, CancellationToken ct = default) =>
        [.. (await ListAsync(list, false, ct)).Select(i => i.Code)];
}

/// <summary>
/// Transactional checks against the reference lists, for services that validate inside their own transaction.
/// Plain ADO.NET so that Core stays free of a database driver dependency (like <see cref="Lifecycle"/>).
/// </summary>
public static class ReferenceChecks
{
    public static async Task<IReadOnlyList<string>> ActiveCodesAsync(DbConnection conn, DbTransaction? tx, string list, CancellationToken ct)
    {
        await using var c = conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "SELECT code FROM cfg.ref_item WHERE list = @l AND is_active ORDER BY sort_order, code";
        AddParameter(c, "l", list);
        var codes = new List<string>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) codes.Add(r.GetString(0));
        return codes;
    }

    /// <summary>The lifecycle family new objects of this type start in: the type's own binding, else <paramref name="defaultFamily"/>.</summary>
    public static async Task<string> LifecycleFamilyAsync(DbConnection conn, DbTransaction? tx, string list, string code, string defaultFamily, CancellationToken ct)
    {
        await using var c = conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "SELECT lifecycle_family FROM cfg.ref_item WHERE list = @l AND code = @c";
        AddParameter(c, "l", list);
        AddParameter(c, "c", code);
        return await c.ExecuteScalarAsync(ct) as string ?? defaultFamily;
    }

    internal static void AddParameter(DbCommand c, string name, object value)
    {
        var p = c.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        c.Parameters.Add(p);
    }
}

/// <summary>Allocates the next generated number of an object type from its sequence and the configured prefix and width (cfg.numbering).</summary>
public static class Numbering
{
    public static async Task<string> NextAsync(DbConnection conn, DbTransaction tx, string objectType, CancellationToken ct)
    {
        string prefix, sequence;
        int width;
        await using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "SELECT prefix, width, sequence_name FROM cfg.numbering WHERE object_type = @t";
            ReferenceChecks.AddParameter(c, "t", objectType);
            await using var r = await c.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new InvalidOperationException($"No numbering is configured for '{objectType}'.");
            (prefix, width, sequence) = (r.GetString(0), r.GetInt32(1), r.GetString(2));
        }
        await using var next = conn.CreateCommand();
        next.Transaction = tx;
        next.CommandText = "SELECT nextval(@s::regclass)";
        ReferenceChecks.AddParameter(next, "s", sequence);
        var value = Convert.ToInt64(await next.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        return prefix + value.ToString("D" + width, System.Globalization.CultureInfo.InvariantCulture);
    }
}
