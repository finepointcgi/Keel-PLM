using Keel.Core;
using Npgsql;

namespace Keel.Admin;

/// <summary>
/// Attribute definitions per object type (ADR-0022). Parts are the only object type that stores attribute values today; asking for
/// another type throws <see cref="NotSupportedException"/> naming the increment that delivers it. Classification attributes are
/// managed by <see cref="ClassificationService"/> with the same rules.
/// </summary>
public sealed class AttributeAdminService(AdminRunner run)
{
    public static readonly IReadOnlyList<string> SupportedObjectTypes = ["part"];

    private static void EnsureSupported(string objectType)
    {
        if (!SupportedObjectTypes.Contains(objectType))
            throw new NotSupportedException(
                $"Attribute values are stored for parts only. Attributes for '{objectType}' objects need iteration attribute storage on documents and change objects, planned for Phase 2c (see docs/increments/2.2-admin-classification.md).");
    }

    public Task<Outcome<IReadOnlyList<AttributeDefinitionDto>>> ListAsync(Subject subject, string objectType, bool includeRetired, CancellationToken ct = default)
    {
        EnsureSupported(objectType);
        return run.ReadAsync<IReadOnlyList<AttributeDefinitionDto>>(subject, "admin.attribute_list", objectType, async conn =>
        {
            var defs = await AttributeTable.ForObjectTypes.LoadAsync(conn, null, objectType, includeRetired, ct);
            return [.. await WithUsageAsync(conn, null, defs, ct)];
        }, ct);
    }

    public Task<Outcome<AttributeChange>> CreateAsync(Subject subject, string objectType, AttributeDefinitionInput input, string? correlationId, CancellationToken ct = default)
    {
        EnsureSupported(objectType);
        return run.ChangeAsync<AttributeChange>(subject, "admin.attribute_create", $"{objectType}/{input.Name}", correlationId, async (conn, tx) =>
        {
            var existing = await AttributeTable.ForObjectTypes.LoadAsync(conn, tx, objectType, true, ct);
            if (existing.Any(d => d.Name == input.Name)) return Step<AttributeChange>.Conflict($"Attribute '{input.Name}' already exists for {objectType} objects.");
            var errors = AttributeRules.Validate(input, null);
            if (errors.Count > 0) return Step<AttributeChange>.Invalid(errors);
            var warnings = new List<string>();
            if (input.Required) warnings.Add("Required attributes are enforced when a part is next saved or checked in; existing iterations are not changed.");
            await AttributeTable.ForObjectTypes.InsertAsync(conn, tx, objectType, input, AdminRunner.Who(subject), run.Clock.GetUtcNow(), ct);
            var created = (await AttributeTable.ForObjectTypes.LoadAsync(conn, tx, objectType, true, ct)).Single(d => d.Name == input.Name);
            return Step<AttributeChange>.Ok(new AttributeChange(created, warnings), ("after", AdminRunner.ToJson(created)));
        }, ct);
    }

    public Task<Outcome<AttributeChange>> UpdateAsync(Subject subject, string objectType, AttributeDefinitionInput input, string? correlationId, CancellationToken ct = default)
    {
        EnsureSupported(objectType);
        return run.ChangeAsync<AttributeChange>(subject, "admin.attribute_update", $"{objectType}/{input.Name}", correlationId, async (conn, tx) =>
        {
            var current = (await WithUsageAsync(conn, tx, await AttributeTable.ForObjectTypes.LoadAsync(conn, tx, objectType, true, ct), ct)).FirstOrDefault(d => d.Name == input.Name);
            if (current is null) return Step<AttributeChange>.NotFound();
            var inUse = current.DataType == "enum" ? await EnumValuesInUseAsync(conn, tx, input.Name, ct) : null;
            var errors = AttributeRules.Validate(input, current, inUse);
            if (errors.Count > 0) return Step<AttributeChange>.Invalid(errors);
            var warnings = new List<string>();
            if (input.Required && !current.Required) warnings.Add("The attribute is now required: parts without a value must get one at their next check-in.");
            if (input.MaxLength is { } max && current.ValuesInUse > 0 && (current.MaxLength is null || max < current.MaxLength))
                warnings.Add("Stored values longer than the new maximum stay readable but must be shortened at the next check-in.");
            if (input.Pattern is not null && input.Pattern != current.Pattern && current.ValuesInUse > 0)
                warnings.Add("Stored values are not re-validated against the new format; they are checked when the part is next saved.");
            await AttributeTable.ForObjectTypes.UpdateAsync(conn, tx, objectType, input, AdminRunner.Who(subject), run.Clock.GetUtcNow(), ct);
            var updated = (await WithUsageAsync(conn, tx, await AttributeTable.ForObjectTypes.LoadAsync(conn, tx, objectType, true, ct), ct)).Single(d => d.Name == input.Name);
            return Step<AttributeChange>.Ok(new AttributeChange(updated, warnings), ("before", AdminRunner.ToJson(current)), ("after", AdminRunner.ToJson(updated)));
        }, ct);
    }

    /// <summary>Retires (or restores) an attribute. Retired attributes disappear from forms; their stored values stay readable on every iteration.</summary>
    public Task<Outcome<AttributeChange>> SetRetiredAsync(Subject subject, string objectType, string name, bool retired, string? reason, string? correlationId, CancellationToken ct = default)
    {
        EnsureSupported(objectType);
        return run.ChangeAsync<AttributeChange>(subject, retired ? "admin.attribute_retire" : "admin.attribute_restore", $"{objectType}/{name}", correlationId, async (conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) return Step<AttributeChange>.Invalid("A reason is required.");
            var current = (await WithUsageAsync(conn, tx, await AttributeTable.ForObjectTypes.LoadAsync(conn, tx, objectType, true, ct), ct)).FirstOrDefault(d => d.Name == name);
            if (current is null) return Step<AttributeChange>.NotFound();
            if (current.Retired == retired) return Step<AttributeChange>.Conflict(retired ? "The attribute is already retired." : "The attribute is not retired.");
            await AttributeTable.ForObjectTypes.SetRetiredAsync(conn, tx, objectType, name, retired, AdminRunner.Who(subject), run.Clock.GetUtcNow(), ct);
            var after = current with { Retired = retired, Revision = current.Revision + 1 };
            return Step<AttributeChange>.Ok(new AttributeChange(after, retired && current.ValuesInUse > 0
                    ? [$"{current.ValuesInUse} stored value(s) remain readable on existing iterations and are carried forward unchanged."] : []),
                ("before", AdminRunner.ToJson(current)), ("after", AdminRunner.ToJson(after)), ("reason", reason.Trim()));
        }, ct);
    }

    // ---------------------------------------------------------------- usage

    private static async Task<List<AttributeDefinitionDto>> WithUsageAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, List<AttributeDefinitionDto> defs, CancellationToken ct)
    {
        var result = new List<AttributeDefinitionDto>();
        foreach (var d in defs) result.Add(d with { ValuesInUse = await CountValuesAsync(conn, tx, d.Name, ct) });
        return result;
    }

    /// <summary>Stored values of a part attribute across all iterations and working copies (an administrator's configuration-safety count, no part data).</summary>
    private static async Task<long> CountValuesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string name, CancellationToken ct)
    {
        await using var cmd = AdminRunner.Cmd(conn, tx,
            "SELECT (SELECT count(*) FROM plm.part_iteration WHERE jsonb_exists(attributes, @n)) + (SELECT count(*) FROM plm.part_checkout WHERE jsonb_exists(attributes, @n))", ("n", name));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<IReadOnlySet<string>> EnumValuesInUseAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string name, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = AdminRunner.Cmd(conn, tx,
            """
            SELECT DISTINCT v FROM (
              SELECT attributes ->> @n AS v FROM plm.part_iteration WHERE jsonb_exists(attributes, @n)
              UNION ALL SELECT attributes ->> @n FROM plm.part_checkout WHERE jsonb_exists(attributes, @n)) x WHERE v IS NOT NULL
            """, ("n", name));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetString(0));
        return set;
    }
}
