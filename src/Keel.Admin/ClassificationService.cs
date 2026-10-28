using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Admin;

/// <summary>
/// The classification hierarchy (ADR-0022): nodes with inherited attribute sets, moved and retired with safety checks. Reading the tree is
/// open to every PLM role (it is configuration, not marked data); changing it is for administrators and audited with before and after.
/// </summary>
public sealed partial class ClassificationService(AdminRunner run, IAuditLog audit)
{
    public const int MaxExamples = 10;

    // ---------------------------------------------------------------- read

    internal static async Task<ClassTree> LoadTreeAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var nodes = new List<ClassNodeDto>();
        await using (var cmd = AdminRunner.Cmd(conn, tx, "SELECT id, code, name, description, parent_id, path, depth, sort_order, status FROM cfg.class_node"))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                nodes.Add(new ClassNodeDto(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.GetString(5), r.GetInt32(6), r.GetInt32(7), r.GetString(8)));
        }
        var attributes = new List<AttributeDefinitionDto>();
        foreach (var n in nodes) attributes.AddRange(await AttributeTable.ForClasses.LoadAsync(conn, tx, n.Id, true, ct));
        return new ClassTree(nodes, attributes);
    }

    /// <summary>The whole tree for navigation and pickers. Any PLM role may read it.</summary>
    public async Task<ClassTree> TreeAsync(CancellationToken ct = default)
    {
        await using var conn = await run.DataSource.OpenConnectionAsync(ct);
        return await LoadTreeAsync(conn, null, ct);
    }

    public async Task<ClassDetail?> GetAsync(string code, CancellationToken ct = default)
    {
        await using var conn = await run.DataSource.OpenConnectionAsync(ct);
        var tree = await LoadTreeAsync(conn, null, ct);
        if (tree.ByCode(code) is not { } node) return null;
        var chain = tree.Chain(node.Id);
        var own = new List<AttributeDefinitionDto>();
        foreach (var a in tree.OwnAttributes(node.Id)) own.Add(a with { ValuesInUse = await CountValuesAsync(conn, null, node, a.Name, ct) });
        return new ClassDetail(node, chain.Take(chain.Count - 1).ToList(), tree.Children(node.Id), tree.Effective(node.Id), own, await CountClassifiedAsync(conn, null, node, ct));
    }

    // ---------------------------------------------------------------- nodes

    public Task<Outcome<ClassNodeDto>> CreateNodeAsync(Subject subject, ClassNodeInput input, string? correlationId, CancellationToken ct = default) =>
        run.ChangeAsync<ClassNodeDto>(subject, "admin.class_create", input.Code, correlationId, async (conn, tx) =>
        {
            var errors = ValidateNode(input);
            if (errors.Count > 0) return Step<ClassNodeDto>.Invalid(errors);
            var tree = await LoadTreeAsync(conn, tx, ct);
            if (tree.ByCode(input.Code) is not null) return Step<ClassNodeDto>.Conflict($"A class with the code '{input.Code}' already exists (codes ignore letter case).");
            ClassNodeDto? parent = null;
            if (!string.IsNullOrWhiteSpace(input.ParentCode))
            {
                parent = tree.ByCode(input.ParentCode);
                if (parent is null) return Step<ClassNodeDto>.Invalid($"Parent class '{input.ParentCode}' does not exist.");
                if (!parent.IsActive) return Step<ClassNodeDto>.Invalid($"Parent class '{parent.Code}' is retired.");
                if (parent.Depth >= ClassTree.MaxDepth) return Step<ClassNodeDto>.Invalid($"The hierarchy is at most {ClassTree.MaxDepth} levels deep.");
            }
            var now = run.Clock.GetUtcNow();
            var node = new ClassNodeDto(Guid.CreateVersion7(), input.Code.Trim(), input.Name.Trim(), input.Description?.Trim() ?? "", parent?.Id,
                (parent?.Path ?? "/") + input.Code.Trim() + "/", (parent?.Depth ?? 0) + 1, input.SortOrder, "active");
            await using (var cmd = AdminRunner.Cmd(conn, tx,
                "INSERT INTO cfg.class_node (id, code, name, description, parent_id, path, depth, sort_order, status, created_at, created_by, updated_at, updated_by) " +
                "VALUES (@id, @c, @n, @d, @p, @path, @depth, @o, 'active', @at, @by, @at, @by)",
                ("id", node.Id), ("c", node.Code), ("n", node.Name), ("d", node.Description), ("p", node.ParentId), ("path", node.Path), ("depth", node.Depth), ("o", node.SortOrder),
                ("at", now), ("by", AdminRunner.Who(subject))))
                await cmd.ExecuteNonQueryAsync(ct);
            return Step<ClassNodeDto>.Ok(node, ("after", AdminRunner.ToJson(node)));
        }, ct);

    public Task<Outcome<ClassNodeDto>> UpdateNodeAsync(Subject subject, string code, string name, string? description, int sortOrder, string? correlationId, CancellationToken ct = default) =>
        run.ChangeAsync<ClassNodeDto>(subject, "admin.class_update", code, correlationId, async (conn, tx) =>
        {
            var tree = await LoadTreeAsync(conn, tx, ct);
            if (tree.ByCode(code) is not { } node) return Step<ClassNodeDto>.NotFound();
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) return Step<ClassNodeDto>.Invalid("The name is required (at most 200 characters).");
            if ((description?.Length ?? 0) > 2000) return Step<ClassNodeDto>.Invalid("The description must be at most 2000 characters.");
            var after = node with { Name = name.Trim(), Description = description?.Trim() ?? "", SortOrder = sortOrder };
            await using (var cmd = AdminRunner.Cmd(conn, tx, "UPDATE cfg.class_node SET name = @n, description = @d, sort_order = @o, updated_at = @at, updated_by = @by WHERE id = @id",
                ("n", after.Name), ("d", after.Description), ("o", sortOrder), ("at", run.Clock.GetUtcNow()), ("by", AdminRunner.Who(subject)), ("id", node.Id)))
                await cmd.ExecuteNonQueryAsync(ct);
            return Step<ClassNodeDto>.Ok(after, ("before", AdminRunner.ToJson(node)), ("after", AdminRunner.ToJson(after)));
        }, ct);

    /// <summary>
    /// Moves a class (with its subtree) under another parent, or to the top (null). Refused when the move would create a cycle, exceed the depth limit,
    /// make two attributes of one path share a name, or leave classified parts that no longer satisfy the new effective attribute set.
    /// </summary>
    public Task<Outcome<ClassNodeDto>> MoveNodeAsync(Subject subject, string code, string? newParentCode, string? reason, string? correlationId, CancellationToken ct = default) =>
        run.ChangeAsync<ClassNodeDto>(subject, "admin.class_move", code, correlationId, async (conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) return Step<ClassNodeDto>.Invalid("A reason is required.");
            await using (var l = AdminRunner.Cmd(conn, tx, "SELECT pg_advisory_xact_lock(hashtext('cfg.class_tree'))")) await l.ExecuteNonQueryAsync(ct);
            var tree = await LoadTreeAsync(conn, tx, ct);
            if (tree.ByCode(code) is not { } node) return Step<ClassNodeDto>.NotFound();
            ClassNodeDto? parent = null;
            if (!string.IsNullOrWhiteSpace(newParentCode))
            {
                parent = tree.ByCode(newParentCode);
                if (parent is null) return Step<ClassNodeDto>.Invalid($"Class '{newParentCode}' does not exist.");
                if (parent.Path.StartsWith(node.Path, StringComparison.Ordinal)) return Step<ClassNodeDto>.Invalid("A class cannot move under itself or one of its own subclasses.");
                if (!parent.IsActive) return Step<ClassNodeDto>.Invalid($"Class '{parent.Code}' is retired.");
            }
            if (parent?.Id == node.ParentId) return Step<ClassNodeDto>.Conflict("The class is already there.");
            var newDepth = (parent?.Depth ?? 0) + 1;
            if (newDepth + tree.Height(node.Id) > ClassTree.MaxDepth) return Step<ClassNodeDto>.Invalid($"The move would make the hierarchy deeper than {ClassTree.MaxDepth} levels.");
            var clashes = tree.NameClashesWhenPlacedUnder(node.Id, parent?.Id);
            if (clashes.Count > 0) return Step<ClassNodeDto>.Invalid(clashes);

            // The tree as it will be: subtree paths and parents rewritten. Existing classified parts are checked against it before anything is written.
            var newPrefix = (parent?.Path ?? "/") + node.Code + "/";
            var moved = tree.Subtree(node.Id).Select(n => n.Id == node.Id
                ? n with { ParentId = parent?.Id, Path = newPrefix, Depth = newDepth }
                : n with { Path = newPrefix + n.Path[node.Path.Length..], Depth = n.Depth - node.Depth + newDepth }).ToList();
            var proposed = new ClassTree(tree.Nodes.Where(n => moved.All(m => m.Id != n.Id)).Concat(moved), tree.Nodes.SelectMany(n => tree.OwnAttributes(n.Id)));
            var impact = await ImpactAsync(conn, tx, subject, proposed, [.. moved.Select(n => n.Id)], ct);
            if (impact.Blocks) return Step<ClassNodeDto>.Invalid(ImpactMessages(impact, "move"));

            foreach (var n in moved)
                await using (var cmd = AdminRunner.Cmd(conn, tx, "UPDATE cfg.class_node SET parent_id = @p, path = @path, depth = @d, updated_at = @at, updated_by = @by WHERE id = @id",
                    ("p", n.ParentId), ("path", n.Path), ("d", n.Depth), ("at", run.Clock.GetUtcNow()), ("by", AdminRunner.Who(subject)), ("id", n.Id)))
                    await cmd.ExecuteNonQueryAsync(ct);
            var after = moved.First(n => n.Id == node.Id);
            return Step<ClassNodeDto>.Ok(after, ("before", AdminRunner.ToJson(node)), ("after", AdminRunner.ToJson(after)), ("reason", reason.Trim()),
                ("subtree", moved.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }, ct);

    /// <summary>Retires (or restores) a class. A class with active subclasses or with classified parts cannot be retired; the report names the parts you can see.</summary>
    public Task<Outcome<ClassNodeDto>> SetStatusAsync(Subject subject, string code, bool active, string? reason, string? correlationId, CancellationToken ct = default) =>
        run.ChangeAsync<ClassNodeDto>(subject, active ? "admin.class_restore" : "admin.class_retire", code, correlationId, async (conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) return Step<ClassNodeDto>.Invalid("A reason is required.");
            var tree = await LoadTreeAsync(conn, tx, ct);
            if (tree.ByCode(code) is not { } node) return Step<ClassNodeDto>.NotFound();
            if (node.IsActive == active) return Step<ClassNodeDto>.Conflict(active ? "The class is already active." : "The class is already retired.");
            if (active)
            {
                if (node.ParentId is { } p && tree.ById(p) is { IsActive: false } retiredParent) return Step<ClassNodeDto>.Invalid($"Restore the parent class {retiredParent.Code} first.");
            }
            else
            {
                var activeChildren = tree.Children(node.Id).Where(c => c.IsActive).Select(c => c.Code).ToList();
                if (activeChildren.Count > 0) return Step<ClassNodeDto>.Invalid($"Retire or move the subclasses first: {string.Join(", ", activeChildren)}.");
                var impact = await ClassifiedHereAsync(conn, tx, subject, node, ct);
                if (impact.Blocks)
                    return Step<ClassNodeDto>.Invalid($"{impact.Conflicts} part(s) are classified in {node.Code}: reclassify them (check out, change the class, check in) before retiring it." +
                                                      (impact.ExampleParts.Count > 0 ? $" Examples you can see: {string.Join(", ", impact.ExampleParts)}." : ""));
            }
            var after = node with { Status = active ? "active" : "retired" };
            await using (var cmd = AdminRunner.Cmd(conn, tx, "UPDATE cfg.class_node SET status = @s, updated_at = @at, updated_by = @by WHERE id = @id",
                ("s", after.Status), ("at", run.Clock.GetUtcNow()), ("by", AdminRunner.Who(subject)), ("id", node.Id)))
                await cmd.ExecuteNonQueryAsync(ct);
            return Step<ClassNodeDto>.Ok(after, ("before", AdminRunner.ToJson(node)), ("after", AdminRunner.ToJson(after)), ("reason", reason.Trim()));
        }, ct);

    // ---------------------------------------------------------------- class attributes

    public Task<Outcome<AttributeChange>> SaveAttributeAsync(Subject subject, string classCode, AttributeDefinitionInput input, bool create, string? correlationId, CancellationToken ct = default) =>
        run.ChangeAsync<AttributeChange>(subject, create ? "admin.class_attribute_create" : "admin.class_attribute_update", $"{classCode}/{input.Name}", correlationId, async (conn, tx) =>
        {
            var tree = await LoadTreeAsync(conn, tx, ct);
            if (tree.ByCode(classCode) is not { } node) return Step<AttributeChange>.NotFound();
            var current = tree.OwnAttributes(node.Id).FirstOrDefault(a => a.Name == input.Name);
            if (create && current is not null) return Step<AttributeChange>.Conflict($"The class already defines '{input.Name}'.");
            if (!create && current is null) return Step<AttributeChange>.NotFound();
            if (create && tree.NameClash(node.Id, input.Name) is { } clash) return Step<AttributeChange>.Conflict(clash);

            var inUse = current is null ? 0 : await CountValuesAsync(conn, tx, node, current.Name, ct);
            var currentDto = current is null ? null : current with { ValuesInUse = inUse };
            var errors = AttributeRules.Validate(input, currentDto, currentDto?.DataType == "enum" ? await EnumValuesInUseAsync(conn, tx, node, input.Name, ct) : null);
            if (errors.Count > 0) return Step<AttributeChange>.Invalid(errors);

            var now = run.Clock.GetUtcNow();
            if (create) await AttributeTable.ForClasses.InsertAsync(conn, tx, node.Id, input, AdminRunner.Who(subject), now, ct);
            else await AttributeTable.ForClasses.UpdateAsync(conn, tx, node.Id, input, AdminRunner.Who(subject), now, ct);

            // The change must not strand parts already classified in this class or below it.
            var proposed = await LoadTreeAsync(conn, tx, ct);
            var impact = await ImpactAsync(conn, tx, subject, proposed, [.. proposed.Subtree(node.Id).Select(n => n.Id)], ct);
            if (impact.Blocks) return Step<AttributeChange>.Invalid(ImpactMessages(impact, "change"));

            var saved = proposed.OwnAttributes(node.Id).Single(a => a.Name == input.Name) with { ValuesInUse = inUse };
            var warnings = new List<string>();
            if (inUse > 0 && !create) warnings.Add($"{inUse} stored value(s) exist; the change applies the next time those parts are edited.");
            return Step<AttributeChange>.Ok(new AttributeChange(saved, warnings),
                ("before", currentDto is null ? null : AdminRunner.ToJson(currentDto)), ("after", AdminRunner.ToJson(saved)), ("class", node.Code));
        }, ct);

    public Task<Outcome<AttributeChange>> SetAttributeRetiredAsync(Subject subject, string classCode, string name, bool retired, string? reason, string? correlationId, CancellationToken ct = default) =>
        run.ChangeAsync<AttributeChange>(subject, retired ? "admin.class_attribute_retire" : "admin.class_attribute_restore", $"{classCode}/{name}", correlationId, async (conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) return Step<AttributeChange>.Invalid("A reason is required.");
            var tree = await LoadTreeAsync(conn, tx, ct);
            if (tree.ByCode(classCode) is not { } node) return Step<AttributeChange>.NotFound();
            var current = tree.OwnAttributes(node.Id).FirstOrDefault(a => a.Name == name);
            if (current is null) return Step<AttributeChange>.NotFound();
            if (current.Retired == retired) return Step<AttributeChange>.Conflict(retired ? "Already retired." : "Not retired.");
            await AttributeTable.ForClasses.SetRetiredAsync(conn, tx, node.Id, name, retired, AdminRunner.Who(subject), run.Clock.GetUtcNow(), ct);
            var after = current with { Retired = retired, Revision = current.Revision + 1 };
            return Step<AttributeChange>.Ok(new AttributeChange(after, []), ("before", AdminRunner.ToJson(current)), ("after", AdminRunner.ToJson(after)), ("reason", reason.Trim()), ("class", node.Code));
        }, ct);

    // ---------------------------------------------------------------- impact on classified parts

    private static List<string> ImpactMessages(ClassImpact impact, string verb)
    {
        var list = new List<string> { $"The {verb} would leave {impact.Conflicts} classified part(s) invalid against the new attribute set. Fix them first or adjust the {verb}." };
        list.AddRange(impact.Messages);
        if (impact.ExampleParts.Count > 0) list.Add($"Parts you can see among them: {string.Join(", ", impact.ExampleParts)}.");
        return list;
    }

    /// <summary>
    /// Validates every classified latest iteration and working copy in the given classes against the effective attribute sets of
    /// <paramref name="proposed"/>. Counts are exact (a configuration-safety question); part numbers are listed only for parts the administrator can read.
    /// </summary>
    internal static async Task<ClassImpact> ImpactAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, ClassTree proposed, IReadOnlyCollection<Guid> classIds, CancellationToken ct)
    {
        var rows = new List<(Guid Revision, Guid ClassId, string Attributes)>();
        await using (var cmd = AdminRunner.Cmd(conn, tx,
            """
            SELECT r.id, pc.class_id, pc.attributes::text
            FROM cfg.part_classification pc
            JOIN plm.part_iteration i ON i.id = pc.iteration_id
            JOIN plm.part_revision r ON r.id = i.revision_id AND r.is_latest AND i.iteration = r.latest_iteration
            WHERE pc.class_id = ANY(@c)
            UNION ALL
            SELECT w.revision_id, w.class_id, w.attributes::text FROM cfg.part_checkout_classification w WHERE w.class_id = ANY(@c)
            """, ("c", classIds.ToArray())))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add((r.GetGuid(0), r.GetGuid(1), r.GetString(2)));
        }
        var messages = new SortedSet<string>(StringComparer.Ordinal);
        var bad = new HashSet<Guid>();
        foreach (var (revision, classId, attrs) in rows)
        {
            var specs = proposed.EffectiveSpecs(classId);
            var (_, errors) = AttributeSpecs.Validate(specs, JsonNode.Parse(attrs)!.AsObject());
            if (errors.Count == 0) continue;
            bad.Add(revision);
            foreach (var e in errors) if (messages.Count < 8) messages.Add($"{proposed.ById(classId)?.Code}: {e}");
        }
        var examples = new List<string>();
        if (bad.Count > 0)
        {
            var filter = AccessPolicy.BuildReadFilter(subject, "m");
            await using var cmd = AdminRunner.Cmd(conn, tx,
                $"SELECT m.number FROM plm.part_master m JOIN plm.part_revision r ON r.master_id = m.id WHERE {filter.Sql} AND r.id = ANY(@revs) ORDER BY m.number LIMIT {MaxExamples}",
                [("revs", bad.ToArray()), .. filter.Parameters.Select(p => (p.Key, (object?)p.Value))]);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) examples.Add(r.GetString(0));
        }
        return new ClassImpact(bad.Count, [.. messages], examples);
    }

    private static async Task<ClassImpact> ClassifiedHereAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, ClassNodeDto node, CancellationToken ct)
    {
        // Every classified latest iteration or working copy in exactly this class counts as a blocker.
        var count = await CountClassifiedAsync(conn, tx, node, ct, exactly: true);
        if (count == 0) return ClassImpact.None;
        var filter = AccessPolicy.BuildReadFilter(subject, "m");
        var examples = new List<string>();
        await using var cmd = AdminRunner.Cmd(conn, tx,
            $"""
            SELECT m.number FROM plm.part_master m JOIN plm.part_revision r ON r.master_id = m.id AND r.is_latest
            LEFT JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            LEFT JOIN cfg.part_classification pc ON pc.iteration_id = i.id
            LEFT JOIN cfg.part_checkout_classification w ON w.revision_id = r.id
            WHERE {filter.Sql} AND (pc.class_id = @c OR w.class_id = @c) ORDER BY m.number LIMIT {MaxExamples}
            """, [("c", node.Id), .. filter.Parameters.Select(p => (p.Key, (object?)p.Value))]);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) examples.Add(r.GetString(0));
        return new ClassImpact(count, [], examples);
    }

    /// <summary>Parts whose latest iteration (or working copy) is classified in the node (with its subclasses unless <paramref name="exactly"/>).</summary>
    private static async Task<long> CountClassifiedAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, ClassNodeDto node, CancellationToken ct, bool exactly = false)
    {
        await using var cmd = AdminRunner.Cmd(conn, tx,
            """
            SELECT (SELECT count(*) FROM cfg.part_classification pc
                    JOIN plm.part_iteration i ON i.id = pc.iteration_id
                    JOIN plm.part_revision r ON r.id = i.revision_id AND r.is_latest AND i.iteration = r.latest_iteration
                    JOIN cfg.class_node cn ON cn.id = pc.class_id
                    WHERE CASE WHEN @exact THEN cn.id = @id ELSE cn.path LIKE @prefix END)
                 + (SELECT count(*) FROM cfg.part_checkout_classification w JOIN cfg.class_node cn ON cn.id = w.class_id
                    WHERE CASE WHEN @exact THEN cn.id = @id ELSE cn.path LIKE @prefix END)
            """, ("exact", exactly), ("id", node.Id), ("prefix", EscapeLike(node.Path) + "%"));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<long> CountValuesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, ClassNodeDto node, string name, CancellationToken ct)
    {
        await using var cmd = AdminRunner.Cmd(conn, tx,
            """
            SELECT (SELECT count(*) FROM cfg.part_classification pc JOIN cfg.class_node cn ON cn.id = pc.class_id WHERE cn.path LIKE @prefix AND jsonb_exists(pc.attributes, @n))
                 + (SELECT count(*) FROM cfg.part_checkout_classification w JOIN cfg.class_node cn ON cn.id = w.class_id WHERE cn.path LIKE @prefix AND jsonb_exists(w.attributes, @n))
            """, ("prefix", EscapeLike(node.Path) + "%"), ("n", name));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<IReadOnlySet<string>> EnumValuesInUseAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ClassNodeDto node, string name, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = AdminRunner.Cmd(conn, tx,
            """
            SELECT DISTINCT v FROM (
              SELECT pc.attributes ->> @n AS v FROM cfg.part_classification pc JOIN cfg.class_node cn ON cn.id = pc.class_id WHERE cn.path LIKE @prefix AND jsonb_exists(pc.attributes, @n)
              UNION ALL SELECT w.attributes ->> @n FROM cfg.part_checkout_classification w JOIN cfg.class_node cn ON cn.id = w.class_id WHERE cn.path LIKE @prefix AND jsonb_exists(w.attributes, @n)) x
            WHERE v IS NOT NULL
            """, ("prefix", EscapeLike(node.Path) + "%"), ("n", name));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetString(0));
        return set;
    }

    internal static string EscapeLike(string s) => s.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);

    private static List<string> ValidateNode(ClassNodeInput i)
    {
        var errors = new List<string>();
        if (!CodePattern().IsMatch(i.Code ?? "")) errors.Add("The code must start with a letter or digit and use letters, digits and . _ - (at most 40 characters).");
        if (string.IsNullOrWhiteSpace(i.Name) || i.Name.Trim().Length > 200) errors.Add("The name is required (at most 200 characters).");
        if ((i.Description?.Length ?? 0) > 2000) errors.Add("The description must be at most 2000 characters.");
        return errors;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,39}$")]
    private static partial Regex CodePattern();
}
