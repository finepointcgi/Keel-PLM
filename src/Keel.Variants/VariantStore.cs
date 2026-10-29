using System.Text.Json.Nodes;
using Keel.Core;
using Keel.Security;
using Npgsql;
using NpgsqlTypes;

namespace Keel.Variants;

internal sealed record ProductRef(Guid Id, string Number, string Name, string PartType, Markings Markings);

internal sealed record ValueRow(Guid Id, string Key, string Name, string? Description, bool Retired, int SortOrder);

internal sealed record FamilyRow(Guid Id, string Key, string Name, string? Description, bool Required, bool Retired, int SortOrder, List<ValueRow> Values);

internal sealed record RuleRow(Guid Id, RuleKind Kind, OptionRef? Subject, List<OptionRef> Members, string? Reason);

internal sealed record VariantRow(Guid Id, string Name, string? Description, string State, Dictionary<string, string> Selection,
    DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset? ReleasedAt, string? ReleasedBy);

internal sealed record ModelData(Guid ProductId, int Version, List<FamilyRow> Families, List<RuleRow> Rules, List<VariantRow> Variants)
{
    public OptionModelDef ToDef() => new(
        [.. Families.OrderBy(f => f.SortOrder).ThenBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new OptionFamilyDef(f.Key, f.Name, f.Required, f.Retired,
                [.. f.Values.OrderBy(v => v.SortOrder).ThenBy(v => v.Key, StringComparer.Ordinal).Select(v => new OptionValueDef(v.Key, v.Name, v.Retired))]))],
        [.. Rules.OrderBy(r => r.Id).Select(r => new OptionRuleDef(r.Id, r.Kind, r.Subject, r.Members, r.Reason))]);
}

/// <summary>The SQL of the variants module. Product lookups are by number (a key lookup; the caller evaluates the access policy on the markings it returns) or go through the read filter.</summary>
internal static class VariantStore
{
    public static string KindText(RuleKind k) => k switch { RuleKind.Requires => "requires", RuleKind.Excludes => "excludes", _ => "mutex" };

    public static RuleKind ParseKind(string s) => s switch { "requires" => RuleKind.Requires, "excludes" => RuleKind.Excludes, _ => RuleKind.Mutex };

    public static async Task<ProductRef?> LoadProductAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string number, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT m.id, m.number, m.name, m.part_type, {MarkingColumns.Select("m")} FROM plm.part_master m WHERE m.number = @n", conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ProductRef(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), MarkingColumns.Read(r, 4)) : null;
    }

    /// <summary>Locks the model row (serializing writers of one product) and returns its version, or null when the product has no model.</summary>
    public static async Task<int?> LockModelAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid product, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT version FROM var.model WHERE product_master_id = @p FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("p", product);
        return await cmd.ExecuteScalarAsync(ct) is int v ? v : null;
    }

    public static async Task<ModelData?> LoadModelAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid product, CancellationToken ct)
    {
        int version;
        await using (var cmd = new NpgsqlCommand("SELECT version FROM var.model WHERE product_master_id = @p", conn, tx))
        {
            cmd.Parameters.AddWithValue("p", product);
            if (await cmd.ExecuteScalarAsync(ct) is not int v) return null;
            version = v;
        }
        var families = new List<FamilyRow>();
        var byId = new Dictionary<Guid, FamilyRow>();
        await using (var cmd = new NpgsqlCommand("SELECT id, key, name, description, required, retired, sort_order FROM var.option_family WHERE product_master_id = @p ORDER BY sort_order, key", conn, tx))
        {
            cmd.Parameters.AddWithValue("p", product);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var f = new FamilyRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4), r.GetBoolean(5), r.GetInt32(6), []);
                families.Add(f);
                byId[f.Id] = f;
            }
        }
        await using (var cmd = new NpgsqlCommand(
            "SELECT v.id, v.family_id, v.key, v.name, v.description, v.retired, v.sort_order FROM var.option_value v JOIN var.option_family f ON f.id = v.family_id WHERE f.product_master_id = @p ORDER BY v.sort_order, v.key", conn, tx))
        {
            cmd.Parameters.AddWithValue("p", product);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (byId.TryGetValue(r.GetGuid(1), out var f))
                    f.Values.Add(new ValueRow(r.GetGuid(0), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetBoolean(5), r.GetInt32(6)));
        }
        var rules = new List<RuleRow>();
        await using (var cmd = new NpgsqlCommand("SELECT id, kind, subject_family, subject_value, members::text, reason FROM var.option_rule WHERE product_master_id = @p ORDER BY created_at, id", conn, tx))
        {
            cmd.Parameters.AddWithValue("p", product);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rules.Add(new RuleRow(r.GetGuid(0), ParseKind(r.GetString(1)), r.IsDBNull(2) ? null : new OptionRef(r.GetString(2), r.GetString(3)), ParseMembers(r.GetString(4)), r.IsDBNull(5) ? null : r.GetString(5)));
        }
        var variants = new List<VariantRow>();
        var variantById = new Dictionary<Guid, VariantRow>();
        await using (var cmd = new NpgsqlCommand("SELECT id, name, description, state, created_at, created_by, released_at, released_by FROM var.variant WHERE product_master_id = @p ORDER BY lower(name)", conn, tx))
        {
            cmd.Parameters.AddWithValue("p", product);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var v = new VariantRow(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), new Dictionary<string, string>(StringComparer.Ordinal),
                    r.GetFieldValue<DateTimeOffset>(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetString(7));
                variants.Add(v);
                variantById[v.Id] = v;
            }
        }
        await using (var cmd = new NpgsqlCommand(
            "SELECT s.variant_id, f.key, v.key FROM var.variant_selection s JOIN var.option_family f ON f.id = s.family_id JOIN var.option_value v ON v.id = s.value_id JOIN var.variant x ON x.id = s.variant_id WHERE x.product_master_id = @p", conn, tx))
        {
            cmd.Parameters.AddWithValue("p", product);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (variantById.TryGetValue(r.GetGuid(0), out var variant)) variant.Selection[r.GetString(1)] = r.GetString(2);
        }
        return new ModelData(product, version, families, rules, variants);
    }

    public static List<OptionRef> ParseMembers(string json)
    {
        var list = new List<OptionRef>();
        if (JsonNode.Parse(json) is JsonArray arr)
            foreach (var n in arr)
                if (n?["family"]?.GetValue<string>() is { } f && n["value"]?.GetValue<string>() is { } v) list.Add(new OptionRef(f, v));
        return list;
    }

    public static string MembersJson(IEnumerable<OptionRef> members) =>
        new JsonArray([.. members.Select(m => (JsonNode?)new JsonObject { ["family"] = m.Family, ["value"] = m.Value })]).ToJsonString();

    public static NpgsqlParameter Jsonb(string name, string json) => new(name, NpgsqlDbType.Jsonb) { Value = json };

    public static async Task BumpAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid product, string by, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("UPDATE var.model SET version = version + 1, updated_at = @at, updated_by = @by WHERE product_master_id = @p", conn, tx);
        cmd.Parameters.AddWithValue("p", product);
        cmd.Parameters.AddWithValue("at", at);
        cmd.Parameters.AddWithValue("by", by);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
