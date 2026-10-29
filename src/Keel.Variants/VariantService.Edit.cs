using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Core;
using Keel.Core.Variants;
using Npgsql;

namespace Keel.Variants;

public sealed partial class VariantService
{
    private static async Task<ModelView> ViewAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ProductRef product, CancellationToken ct) =>
        BuildView(product, (await VariantStore.LoadModelAsync(conn, tx, product.Id, ct))!) with { CanModify = true };

    private static string? CleanText(string? s, int max, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        if (t.Length > max) error = $"Use at most {max} characters.";
        if (t.Any(char.IsControl)) error = "Remove control characters.";
        return t;
    }

    private static bool IsUsedByVariant(ModelData m, string familyKey, string? valueKey) =>
        m.Variants.Any(v => v.Selection.TryGetValue(familyKey, out var x) && (valueKey is null || x == valueKey));

    private static bool IsUsedByRule(ModelData m, string familyKey, string? valueKey) =>
        m.Rules.Any(r => r.Members.Concat(r.Subject is null ? [] : [r.Subject]).Any(o => o.Family == familyKey && (valueKey is null || o.Value == valueKey)));

    // ---------------------------------------------------------------- model, families, values

    public Task<Outcome<ModelView>> CreateModelAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        WriteAsync<ModelView>(subject, number, "options.model_create", correlationId, false,
            async (conn, tx, product, _) => Step<ModelView>.Done(await ViewAsync(conn, tx, product, ct)), ct, creating: true);

    public Task<Outcome<ModelView>> AddFamilyAsync(Subject subject, string number, NewFamily cmd, string? correlationId, CancellationToken ct = default) =>
        WriteAsync<ModelView>(subject, number, "options.family_add", correlationId, true, async (conn, tx, product, m) =>
        {
            if (!OptionExpressions.IsKey(cmd.Key)) return Step<ModelView>.Invalid($"'{cmd.Key}' is not a valid option key. {OptionExpressions.KeyRule}");
            var name = CleanText(cmd.Name, 120, out var e1);
            if (name is null || e1 is not null) return Step<ModelView>.Invalid("Name is required (at most 120 characters).");
            var desc = CleanText(cmd.Description, 500, out var e2);
            if (e2 is not null) return Step<ModelView>.Invalid("Description: " + e2);
            if (m.Families.Count >= VariantLimits.MaxFamilies) return Step<ModelView>.Conflict($"A product can have at most {VariantLimits.MaxFamilies} option families.");
            if (m.Families.Any(f => f.Key == cmd.Key)) return Step<ModelView>.Conflict($"The option '{cmd.Key}' already exists.");
            await using (var ins = new NpgsqlCommand(
                "INSERT INTO var.option_family (id, product_master_id, key, name, description, required, sort_order, created_at, created_by) VALUES (@id, @p, @k, @n, @d, @r, @s, @at, @by)", conn, tx))
            {
                ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
                ins.Parameters.AddWithValue("p", product.Id);
                ins.Parameters.AddWithValue("k", cmd.Key);
                ins.Parameters.AddWithValue("n", name);
                ins.Parameters.AddWithValue("d", (object?)desc ?? DBNull.Value);
                ins.Parameters.AddWithValue("r", cmd.Required);
                ins.Parameters.AddWithValue("s", cmd.SortOrder);
                ins.Parameters.AddWithValue("at", clock.GetUtcNow());
                ins.Parameters.AddWithValue("by", Who(subject));
                await ins.ExecuteNonQueryAsync(ct);
            }
            return Step<ModelView>.Done(await ViewAsync(conn, tx, product, ct), ("family", cmd.Key), ("required", cmd.Required.ToString()));
        }, ct);

    public Task<Outcome<ModelView>> UpdateFamilyAsync(Subject subject, string number, string familyKey, FamilyChange change, string? correlationId, CancellationToken ct = default) =>
        WriteAsync<ModelView>(subject, number, "options.family_update", correlationId, true, async (conn, tx, product, m) =>
        {
            var f = m.Families.FirstOrDefault(x => x.Key == familyKey);
            if (f is null) return Step<ModelView>.Missing();
            string? e1 = null;
            var name = change.Name is null ? f.Name : CleanText(change.Name, 120, out e1);
            if (string.IsNullOrWhiteSpace(name) || (change.Name is not null && e1 is not null)) return Step<ModelView>.Invalid("Name is required (at most 120 characters).");
            string? e2 = null;
            var desc = change.Description is null ? f.Description : CleanText(change.Description, 500, out e2);
            if (change.Description is not null && e2 is not null) return Step<ModelView>.Invalid("Description: " + e2);
            await using var upd = new NpgsqlCommand("UPDATE var.option_family SET name = @n, description = @d, required = @r, retired = @t, sort_order = @s WHERE id = @id", conn, tx);
            upd.Parameters.AddWithValue("n", name);
            upd.Parameters.AddWithValue("d", (object?)desc ?? DBNull.Value);
            upd.Parameters.AddWithValue("r", change.Required ?? f.Required);
            upd.Parameters.AddWithValue("t", change.Retired ?? f.Retired);
            upd.Parameters.AddWithValue("s", change.SortOrder ?? f.SortOrder);
            upd.Parameters.AddWithValue("id", f.Id);
            await upd.ExecuteNonQueryAsync(ct);
            return Step<ModelView>.Done(await ViewAsync(conn, tx, product, ct), ("family", familyKey), ("retired", (change.Retired ?? f.Retired).ToString()), ("required", (change.Required ?? f.Required).ToString()));
        }, ct);

    public Task<Outcome<ModelView>> DeleteFamilyAsync(Subject subject, string number, string familyKey, string? correlationId, CancellationToken ct = default) =>
        WriteAsync<ModelView>(subject, number, "options.family_delete", correlationId, true, async (conn, tx, product, m) =>
        {
            var f = m.Families.FirstOrDefault(x => x.Key == familyKey);
            if (f is null) return Step<ModelView>.Missing();
            if (IsUsedByVariant(m, familyKey, null)) return Step<ModelView>.Conflict($"'{f.Name}' is used by a variant. Retire the option instead.");
            if (IsUsedByRule(m, familyKey, null)) return Step<ModelView>.Conflict($"'{f.Name}' is named in a rule. Remove the rule first.");
            await using (var del = new NpgsqlCommand("DELETE FROM var.option_value WHERE family_id = @id; DELETE FROM var.option_family WHERE id = @id", conn, tx))
            {
                del.Parameters.AddWithValue("id", f.Id);
                await del.ExecuteNonQueryAsync(ct);
            }
            return Step<ModelView>.Done(await ViewAsync(conn, tx, product, ct), ("family", familyKey));
        }, ct);

    public Task<Outcome<ModelView>> AddValueAsync(Subject subject, string number, string familyKey, NewValue cmd, string? correlationId, CancellationToken ct = default) =>
        WriteAsync<ModelView>(subject, number, "options.value_add", correlationId, true, async (conn, tx, product, m) =>
        {
            var f = m.Families.FirstOrDefault(x => x.Key == familyKey);
            if (f is null) return Step<ModelView>.Missing();
            if (!OptionExpressions.IsKey(cmd.Key)) return Step<ModelView>.Invalid($"'{cmd.Key}' is not a valid value key. {OptionExpressions.KeyRule}");
            var name = CleanText(cmd.Name, 120, out var e1);
            if (name is null || e1 is not null) return Step<ModelView>.Invalid("Name is required (at most 120 characters).");
            var desc = CleanText(cmd.Description, 500, out var e2);
            if (e2 is not null) return Step<ModelView>.Invalid("Description: " + e2);
            if (f.Values.Count >= VariantLimits.MaxValuesPerFamily) return Step<ModelView>.Conflict($"An option can have at most {VariantLimits.MaxValuesPerFamily} values.");
            if (f.Values.Any(v => v.Key == cmd.Key)) return Step<ModelView>.Conflict($"'{f.Name}' already has the value '{cmd.Key}'.");
            await using (var ins = new NpgsqlCommand(
                "INSERT INTO var.option_value (id, family_id, key, name, description, sort_order, created_at, created_by) VALUES (@id, @f, @k, @n, @d, @s, @at, @by)", conn, tx))
            {
                ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
                ins.Parameters.AddWithValue("f", f.Id);
                ins.Parameters.AddWithValue("k", cmd.Key);
                ins.Parameters.AddWithValue("n", name);
                ins.Parameters.AddWithValue("d", (object?)desc ?? DBNull.Value);
                ins.Parameters.AddWithValue("s", cmd.SortOrder);
                ins.Parameters.AddWithValue("at", clock.GetUtcNow());
                ins.Parameters.AddWithValue("by", Who(subject));
                await ins.ExecuteNonQueryAsync(ct);
            }
            return Step<ModelView>.Done(await ViewAsync(conn, tx, product, ct), ("family", familyKey), ("value", cmd.Key));
        }, ct);

    public Task<Outcome<ModelView>> UpdateValueAsync(Subject subject, string number, string familyKey, string valueKey, ValueChange change, string? correlationId, CancellationToken ct = default) =>
        WriteAsync<ModelView>(subject, number, "options.value_update", correlationId, true, async (conn, tx, product, m) =>
        {
            var v = m.Families.FirstOrDefault(x => x.Key == familyKey)?.Values.FirstOrDefault(x => x.Key == valueKey);
            if (v is null) return Step<ModelView>.Missing();
            string? e1 = null;
            var name = change.Name is null ? v.Name : CleanText(change.Name, 120, out e1);
            if (string.IsNullOrWhiteSpace(name) || (change.Name is not null && e1 is not null)) return Step<ModelView>.Invalid("Name is required (at most 120 characters).");
            string? e2 = null;
            var desc = change.Description is null ? v.Description : CleanText(change.Description, 500, out e2);
            if (change.Description is not null && e2 is not null) return Step<ModelView>.Invalid("Description: " + e2);
            await using var upd = new NpgsqlCommand("UPDATE var.option_value SET name = @n, description = @d, retired = @t, sort_order = @s WHERE id = @id", conn, tx);
            upd.Parameters.AddWithValue("n", name);
            upd.Parameters.AddWithValue("d", (object?)desc ?? DBNull.Value);
            upd.Parameters.AddWithValue("t", change.Retired ?? v.Retired);
            upd.Parameters.AddWithValue("s", change.SortOrder ?? v.SortOrder);
            upd.Parameters.AddWithValue("id", v.Id);
            await upd.ExecuteNonQueryAsync(ct);
            return Step<ModelView>.Done(await ViewAsync(conn, tx, product, ct), ("family", familyKey), ("value", valueKey), ("retired", (change.Retired ?? v.Retired).ToString()));
        }, ct);

    public Task<Outcome<ModelView>> DeleteValueAsync(Subject subject, string number, string familyKey, string valueKey, string? correlationId, CancellationToken ct = default) =>
        WriteAsync<ModelView>(subject, number, "options.value_delete", correlationId, true, async (conn, tx, product, m) =>
        {
            var v = m.Families.FirstOrDefault(x => x.Key == familyKey)?.Values.FirstOrDefault(x => x.Key == valueKey);
            if (v is null) return Step<ModelView>.Missing();
            if (IsUsedByVariant(m, familyKey, valueKey)) return Step<ModelView>.Conflict($"'{v.Name}' is used by a variant. Retire the value instead.");
            if (IsUsedByRule(m, familyKey, valueKey)) return Step<ModelView>.Conflict($"'{v.Name}' is named in a rule. Remove the rule first.");
            await using (var del = new NpgsqlCommand("DELETE FROM var.option_value WHERE id = @id", conn, tx))
            {
                del.Parameters.AddWithValue("id", v.Id);
                await del.ExecuteNonQueryAsync(ct);
            }
            return Step<ModelView>.Done(await ViewAsync(conn, tx, product, ct), ("family", familyKey), ("value", valueKey));
        }, ct);

    // ---------------------------------------------------------------- rules

    public Task<Outcome<ModelView>> AddRuleAsync(Subject subject, string number, NewRule cmd, string? correlationId, CancellationToken ct = default) =>
        WriteAsync<ModelView>(subject, number, "options.rule_add", correlationId, true, async (conn, tx, product, m) =>
        {
            var members = cmd.Members ?? [];
            if (cmd.Kind == RuleKind.Mutex)
            {
                if (cmd.Subject is not null) return Step<ModelView>.Invalid("A mutually exclusive rule has no subject; list the options that cannot be combined.");
                if (members.Count < 2) return Step<ModelView>.Invalid("List at least two options that cannot be combined.");
            }
            else
            {
                if (cmd.Subject is null) return Step<ModelView>.Invalid("Name the option the rule is about.");
                if (members.Count < 1) return Step<ModelView>.Invalid(cmd.Kind == RuleKind.Requires ? "List the options that are needed." : "List the options that are ruled out.");
            }
            if (members.Count > VariantLimits.MaxMembersPerRule) return Step<ModelView>.Invalid($"A rule can list at most {VariantLimits.MaxMembersPerRule} options.");
            var reason = CleanText(cmd.Reason, 500, out var er);
            if (er is not null) return Step<ModelView>.Invalid("Reason: " + er);
            foreach (var o in members.Concat(cmd.Subject is null ? [] : [cmd.Subject]))
            {
                var f = m.Families.FirstOrDefault(x => x.Key == o.Family);
                var v = f?.Values.FirstOrDefault(x => x.Key == o.Value);
                if (f is null || v is null) return Step<ModelView>.Invalid($"The option {o} does not exist.");
                if (f.Retired || v.Retired) return Step<ModelView>.Invalid($"The option {o} is retired.");
            }
            if (cmd.Subject is { } s && members.Any(x => x == s)) return Step<ModelView>.Invalid("A rule cannot relate an option to itself.");
            if (m.Rules.Count >= VariantLimits.MaxRules) return Step<ModelView>.Conflict($"A product can have at most {VariantLimits.MaxRules} rules.");

            var errorsBefore = RuleEngine.ValidateModel(m.ToDef()).Where(f => f.Severity == FindingSeverity.Error || f.Code == "option.unusable").Select(f => f.Code + "|" + f.Message).ToHashSet();
            var id = Guid.CreateVersion7();
            await using (var ins = new NpgsqlCommand(
                "INSERT INTO var.option_rule (id, product_master_id, kind, subject_family, subject_value, members, reason, created_at, created_by) VALUES (@id, @p, @k, @sf, @sv, @m, @r, @at, @by)", conn, tx))
            {
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("p", product.Id);
                ins.Parameters.AddWithValue("k", VariantStore.KindText(cmd.Kind));
                ins.Parameters.AddWithValue("sf", (object?)cmd.Subject?.Family ?? DBNull.Value);
                ins.Parameters.AddWithValue("sv", (object?)cmd.Subject?.Value ?? DBNull.Value);
                ins.Parameters.Add(VariantStore.Jsonb("m", VariantStore.MembersJson(members)));
                ins.Parameters.AddWithValue("r", (object?)reason ?? DBNull.Value);
                ins.Parameters.AddWithValue("at", clock.GetUtcNow());
                ins.Parameters.AddWithValue("by", Who(subject));
                await ins.ExecuteNonQueryAsync(ct);
            }
            var after = (await VariantStore.LoadModelAsync(conn, tx, product.Id, ct))!;
            // A rule is refused when it adds an error, or when it makes an option unusable (no valid configuration can contain it): both are nearly always a mistake.
            var introduced = RuleEngine.ValidateModel(after.ToDef()).Where(f => (f.Severity == FindingSeverity.Error || f.Code == "option.unusable") && !errorsBefore.Contains(f.Code + "|" + f.Message)).Select(f => f.Message).ToList();
            if (introduced.Count > 0) return Step<ModelView>.Conflict("This rule would make the model inconsistent: " + string.Join(" ", introduced));
            return Step<ModelView>.Done(BuildView(product, after) with { CanModify = true }, ("rule", id.ToString("D")), ("kind", VariantStore.KindText(cmd.Kind)),
                ("subject", cmd.Subject?.ToString()), ("members", string.Join("; ", members.Select(x => x.ToString()))));
        }, ct);

    public Task<Outcome<ModelView>> DeleteRuleAsync(Subject subject, string number, Guid ruleId, string? correlationId, CancellationToken ct = default) =>
        WriteAsync<ModelView>(subject, number, "options.rule_delete", correlationId, true, async (conn, tx, product, m) =>
        {
            if (m.Rules.All(r => r.Id != ruleId)) return Step<ModelView>.Missing();
            await using (var del = new NpgsqlCommand("DELETE FROM var.option_rule WHERE id = @id AND product_master_id = @p", conn, tx))
            {
                del.Parameters.AddWithValue("id", ruleId);
                del.Parameters.AddWithValue("p", product.Id);
                await del.ExecuteNonQueryAsync(ct);
            }
            return Step<ModelView>.Done(await ViewAsync(conn, tx, product, ct), ("rule", ruleId.ToString("D")));
        }, ct);

    // ---------------------------------------------------------------- variants

    private static string? CheckSelection(ModelData m, IReadOnlyDictionary<string, string> selection)
    {
        foreach (var (fk, vk) in selection)
        {
            var f = m.Families.FirstOrDefault(x => x.Key == fk);
            if (f is null) return $"The option '{fk}' does not exist.";
            var v = f.Values.FirstOrDefault(x => x.Key == vk);
            if (v is null) return $"'{vk}' is not a value of '{f.Name}'.";
            if (f.Retired || v.Retired) return $"{f.Name} = {v.Name} is retired.";
        }
        return null;
    }

    private static async Task WriteSelectionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ModelData m, Guid variantId, IReadOnlyDictionary<string, string> selection, CancellationToken ct)
    {
        await using (var del = new NpgsqlCommand("DELETE FROM var.variant_selection WHERE variant_id = @v", conn, tx))
        {
            del.Parameters.AddWithValue("v", variantId);
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var (fk, vk) in selection)
        {
            var f = m.Families.First(x => x.Key == fk);
            var v = f.Values.First(x => x.Key == vk);
            await using var ins = new NpgsqlCommand("INSERT INTO var.variant_selection (variant_id, family_id, value_id) VALUES (@v, @f, @x)", conn, tx);
            ins.Parameters.AddWithValue("v", variantId);
            ins.Parameters.AddWithValue("f", f.Id);
            ins.Parameters.AddWithValue("x", v.Id);
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    public Task<Outcome<ModelView>> CreateVariantAsync(Subject subject, string number, NewVariant cmd, string? correlationId, CancellationToken ct = default) =>
        WriteAsync<ModelView>(subject, number, "variant.create", correlationId, false, async (conn, tx, product, m) =>
        {
            var name = CleanText(cmd.Name, 80, out var e1);
            if (name is null || e1 is not null) return Step<ModelView>.Invalid("Name is required (at most 80 characters).");
            var desc = CleanText(cmd.Description, 500, out var e2);
            if (e2 is not null) return Step<ModelView>.Invalid("Description: " + e2);
            if (CheckSelection(m, cmd.Selection) is { } bad) return Step<ModelView>.Invalid(bad);
            if (m.Variants.Count >= VariantLimits.MaxVariants) return Step<ModelView>.Conflict($"A product can have at most {VariantLimits.MaxVariants} variants.");
            if (m.Variants.Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))) return Step<ModelView>.Conflict($"A variant named '{name}' already exists.");
            var id = Guid.CreateVersion7();
            await using (var ins = new NpgsqlCommand("INSERT INTO var.variant (id, product_master_id, name, description, created_at, created_by) VALUES (@id, @p, @n, @d, @at, @by)", conn, tx))
            {
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("p", product.Id);
                ins.Parameters.AddWithValue("n", name);
                ins.Parameters.AddWithValue("d", (object?)desc ?? DBNull.Value);
                ins.Parameters.AddWithValue("at", clock.GetUtcNow());
                ins.Parameters.AddWithValue("by", Who(subject));
                await ins.ExecuteNonQueryAsync(ct);
            }
            await WriteSelectionAsync(conn, tx, m, id, cmd.Selection, ct);
            return Step<ModelView>.Done(await ViewAsync(conn, tx, product, ct), ("variant", name), ("choices", Fmt(cmd.Selection.Count)));
        }, ct);

    public Task<Outcome<ModelView>> UpdateVariantAsync(Subject subject, string number, string variantName, VariantChange change, string? correlationId, CancellationToken ct = default) =>
        WriteAsync<ModelView>(subject, number, "variant.update", correlationId, false, async (conn, tx, product, m) =>
        {
            var v = m.Variants.FirstOrDefault(x => string.Equals(x.Name, variantName, StringComparison.OrdinalIgnoreCase));
            if (v is null) return Step<ModelView>.Missing();
            if (v.State != VariantStates.Draft) return Step<ModelView>.Conflict($"'{v.Name}' is {v.State.ToLowerInvariant()} and cannot be changed. Create a new variant instead.");
            string? e1 = null;
            var name = change.Name is null ? v.Name : CleanText(change.Name, 80, out e1);
            if (string.IsNullOrWhiteSpace(name) || (change.Name is not null && e1 is not null)) return Step<ModelView>.Invalid("Name is required (at most 80 characters).");
            string? e2 = null;
            var desc = change.Description is null ? v.Description : CleanText(change.Description, 500, out e2);
            if (change.Description is not null && e2 is not null) return Step<ModelView>.Invalid("Description: " + e2);
            if (change.Selection is not null && CheckSelection(m, change.Selection) is { } bad) return Step<ModelView>.Invalid(bad);
            if (m.Variants.Any(x => x.Id != v.Id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) return Step<ModelView>.Conflict($"A variant named '{name}' already exists.");
            await using (var upd = new NpgsqlCommand("UPDATE var.variant SET name = @n, description = @d WHERE id = @id", conn, tx))
            {
                upd.Parameters.AddWithValue("n", name);
                upd.Parameters.AddWithValue("d", (object?)desc ?? DBNull.Value);
                upd.Parameters.AddWithValue("id", v.Id);
                await upd.ExecuteNonQueryAsync(ct);
            }
            if (change.Selection is not null) await WriteSelectionAsync(conn, tx, m, v.Id, change.Selection, ct);
            return Step<ModelView>.Done(await ViewAsync(conn, tx, product, ct), ("variant", name));
        }, ct);

    public Task<Outcome<ModelView>> DeleteVariantAsync(Subject subject, string number, string variantName, string? correlationId, CancellationToken ct = default) =>
        WriteAsync<ModelView>(subject, number, "variant.delete", correlationId, false, async (conn, tx, product, m) =>
        {
            var v = m.Variants.FirstOrDefault(x => string.Equals(x.Name, variantName, StringComparison.OrdinalIgnoreCase));
            if (v is null) return Step<ModelView>.Missing();
            if (v.State != VariantStates.Draft) return Step<ModelView>.Conflict($"'{v.Name}' is {v.State.ToLowerInvariant()}; a released variant is retired, never deleted.");
            await using (var del = new NpgsqlCommand("DELETE FROM var.variant_selection WHERE variant_id = @id; DELETE FROM var.variant WHERE id = @id", conn, tx))
            {
                del.Parameters.AddWithValue("id", v.Id);
                await del.ExecuteNonQueryAsync(ct);
            }
            return Step<ModelView>.Done(await ViewAsync(conn, tx, product, ct), ("variant", v.Name));
        }, ct);

    /// <summary>Releases a draft variant. It must be a valid configuration. Writes the <c>variant.released</c> event in the same transaction.</summary>
    public Task<Outcome<ModelView>> ReleaseVariantAsync(Subject subject, string number, string variantName, string? correlationId, CancellationToken ct = default) =>
        WriteAsync<ModelView>(subject, number, "variant.release", correlationId, false, async (conn, tx, product, m) =>
        {
            var v = m.Variants.FirstOrDefault(x => string.Equals(x.Name, variantName, StringComparison.OrdinalIgnoreCase));
            if (v is null) return Step<ModelView>.Missing();
            if (v.State != VariantStates.Draft) return Step<ModelView>.Conflict($"'{v.Name}' is already {v.State.ToLowerInvariant()}.");
            var check = RuleEngine.Check(m.ToDef(), v.Selection);
            if (!check.Valid) return Step<ModelView>.Invalid([.. check.Problems.Select(p => $"Not valid: {p}")]);
            var now = clock.GetUtcNow();
            await using (var upd = new NpgsqlCommand("UPDATE var.variant SET state = 'Released', released_at = @at, released_by = @by WHERE id = @id", conn, tx))
            {
                upd.Parameters.AddWithValue("at", now);
                upd.Parameters.AddWithValue("by", Who(subject));
                upd.Parameters.AddWithValue("id", v.Id);
                await upd.ExecuteNonQueryAsync(ct);
            }
            if (events is not null)
            {
                var payload = new JsonObject
                {
                    ["product"] = new JsonObject { ["partId"] = product.Id.ToString("D"), ["number"] = product.Number, ["name"] = product.Name },
                    ["variant"] = new JsonObject { ["name"] = v.Name, ["description"] = v.Description, ["state"] = VariantStates.Released },
                    ["choices"] = Choices(product, m, v)["choices"]!.DeepClone(),
                    ["modelVersion"] = m.Version,
                    ["releasedAt"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                };
                await events.AppendAsync(conn, tx, new DomainEvent(EventTypes.VariantReleased, ObjectType, product.Id, product.Number, null, product.Markings, payload, now, Who(subject), correlationId), ct);
            }
            return Step<ModelView>.Done(await ViewAsync(conn, tx, product, ct), ("variant", v.Name), ("choices", Fmt(v.Selection.Count)));
        }, ct);

    public Task<Outcome<ModelView>> RetireVariantAsync(Subject subject, string number, string variantName, string? correlationId, CancellationToken ct = default) =>
        WriteAsync<ModelView>(subject, number, "variant.retire", correlationId, false, async (conn, tx, product, m) =>
        {
            var v = m.Variants.FirstOrDefault(x => string.Equals(x.Name, variantName, StringComparison.OrdinalIgnoreCase));
            if (v is null) return Step<ModelView>.Missing();
            if (v.State != VariantStates.Released) return Step<ModelView>.Conflict($"'{v.Name}' is {v.State.ToLowerInvariant()}; only a released variant is retired.");
            await using (var upd = new NpgsqlCommand("UPDATE var.variant SET state = 'Retired' WHERE id = @id", conn, tx))
            {
                upd.Parameters.AddWithValue("id", v.Id);
                await upd.ExecuteNonQueryAsync(ct);
            }
            return Step<ModelView>.Done(await ViewAsync(conn, tx, product, ct), ("variant", v.Name));
        }, ct);
}
