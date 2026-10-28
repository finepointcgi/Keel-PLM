using System.Globalization;
using System.Text.Json;
using Keel.Audit;
using Keel.Core;
using Npgsql;

namespace Keel.Admin;

public sealed partial class ClassificationService
{
    public const int MaxImportNodes = 20000;

    /// <summary>The whole hierarchy as a document (parents before children), for promotion between environments and for migration tooling.</summary>
    public async Task<Outcome<ClassHierarchyDoc>> ExportHierarchyAsync(Subject subject, CancellationToken ct = default)
    {
        var o = await run.ReadAsync(subject, "admin.class_export", null, async conn => ToDocument(await LoadTreeAsync(conn, null, ct)), ct);
        return o;
    }

    internal static ClassHierarchyDoc ToDocument(ClassTree tree) =>
        new(ClassHierarchyDoc.CurrentFormat, [.. tree.Nodes.OrderBy(n => n.Path, StringComparer.Ordinal).Select(n => new ClassNodeDoc(n.Code, n.Name, n.Description, n.ParentId is { } p ? tree.ById(p)!.Code : null, n.SortOrder, n.Status,
            [.. tree.OwnAttributes(n.Id).Select(AttributeDoc.From)]))]);

    /// <summary>
    /// Imports a hierarchy: classes are created (parents first) or updated by code, attributes created or updated by name. Nothing is deleted, moved or
    /// retired by an import (those have their own safety checks). A dry run executes exactly the same steps and rolls them back, so its report is what an
    /// apply would do. The whole import is one transaction: any error applies nothing.
    /// </summary>
    public async Task<Outcome<ClassImportReport>> ImportHierarchyAsync(Subject subject, ClassHierarchyDoc doc, bool dryRun, string? reason, string? correlationId, CancellationToken ct = default)
    {
        var action = dryRun ? "admin.class_import_dryrun" : "admin.class_import";
        if (!await run.AdminOnlyAsync(subject, action, null, correlationId, ct)) return Outcome<ClassImportReport>.Forbidden([Security.AccessPolicy.RoleMissing]);
        if (!dryRun && string.IsNullOrWhiteSpace(reason)) return Outcome<ClassImportReport>.Invalid(["A reason is required."]);
        var sha = ClassificationIO.Sha256(JsonSerializer.Serialize(doc, AdminRunner.Json));

        await using var conn = await run.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var l = AdminRunner.Cmd(conn, tx, "SELECT pg_advisory_xact_lock(hashtext('cfg.class_tree'))")) await l.ExecuteNonQueryAsync(ct);
        ClassImportReport report;
        try
        {
            report = await ApplyAsync(conn, tx, subject, doc, sha, dryRun, ct);
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation)
        {
            await tx.RollbackAsync(ct);
            return Outcome<ClassImportReport>.Conflict(e.MessageText);
        }
        if (report.Errors.Count > 0 || dryRun)
        {
            await tx.RollbackAsync(ct);
            return report.Errors.Count > 0 && !dryRun ? Outcome<ClassImportReport>.Invalid(report.Errors) : Outcome<ClassImportReport>.Ok(report);
        }
        await audit.AppendAsync(tx, AdminRunner.Entry(subject, action, AuditOutcome.Success, null, correlationId, ("sha256", sha), ("format", doc.Format),
            ("created", report.Created.ToString(CultureInfo.InvariantCulture)), ("updated", report.Updated.ToString(CultureInfo.InvariantCulture)),
            ("unchanged", report.Unchanged.ToString(CultureInfo.InvariantCulture)), ("reason", reason!.Trim())), ct);
        await tx.CommitAsync(ct);
        return Outcome<ClassImportReport>.Ok(report);
    }

    private async Task<ClassImportReport> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, ClassHierarchyDoc doc, string sha, bool dryRun, CancellationToken ct)
    {
        var lines = new List<ImportLine>();
        var errors = new List<string>();
        ClassImportReport Done(int c, int u, int same) => new(dryRun, sha, lines, errors, c, u, same);

        if (doc.Nodes.Count > MaxImportNodes) { errors.Add($"At most {MaxImportNodes} classes can be imported at once."); return Done(0, 0, 0); }
        foreach (var d in doc.Nodes.GroupBy(n => n.Code, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) errors.Add($"The code '{d.Key}' appears more than once.");
        if (errors.Count > 0) return Done(0, 0, 0);
        var inDoc = doc.Nodes.ToDictionary(n => n.Code, StringComparer.OrdinalIgnoreCase);

        var tree = await LoadTreeAsync(conn, tx, ct);
        var nodes = tree.Nodes.ToList();
        var attrs = tree.Nodes.SelectMany(n => tree.OwnAttributes(n.Id)).ToList();
        var touched = new HashSet<Guid>();
        var pending = doc.Nodes.ToList();
        int created = 0, updated = 0, unchanged = 0;
        var now = run.Clock.GetUtcNow();
        var by = AdminRunner.Who(subject);

        while (pending.Count > 0)
        {
            var ready = pending.Where(n => n.Parent is null || tree.ByCode(n.Parent) is not null).ToList();
            if (ready.Count == 0)
            {
                foreach (var n in pending) errors.Add(inDoc.ContainsKey(n.Parent!) ? $"{n.Code}: the parents form a cycle." : $"{n.Code}: parent '{n.Parent}' does not exist.");
                return Done(created, updated, unchanged);
            }
            foreach (var n in ready)
            {
                pending.Remove(n);
                var existing = tree.ByCode(n.Code);
                var nodeErrors = ValidateNode(new ClassNodeInput(n.Code, n.Name, n.Description, n.Parent, n.SortOrder));
                if (nodeErrors.Count > 0) { errors.AddRange(nodeErrors.Select(e => $"{n.Code}: {e}")); continue; }
                var detail = new List<string>();
                ClassNodeDto node;
                string kind;
                if (existing is null)
                {
                    var parent = n.Parent is null ? null : tree.ByCode(n.Parent);
                    if (parent is { IsActive: false }) { errors.Add($"{n.Code}: parent {parent.Code} is retired."); continue; }
                    if ((parent?.Depth ?? 0) >= ClassTree.MaxDepth) { errors.Add($"{n.Code}: the hierarchy is at most {ClassTree.MaxDepth} levels deep."); continue; }
                    node = new ClassNodeDto(Guid.CreateVersion7(), n.Code.Trim(), n.Name.Trim(), n.Description?.Trim() ?? "", parent?.Id, (parent?.Path ?? "/") + n.Code.Trim() + "/", (parent?.Depth ?? 0) + 1, n.SortOrder, "active");
                    await using (var cmd = AdminRunner.Cmd(conn, tx,
                        "INSERT INTO cfg.class_node (id, code, name, description, parent_id, path, depth, sort_order, status, created_at, created_by, updated_at, updated_by) VALUES (@id, @c, @n, @d, @p, @path, @depth, @o, 'active', @at, @by, @at, @by)",
                        ("id", node.Id), ("c", node.Code), ("n", node.Name), ("d", node.Description), ("p", node.ParentId), ("path", node.Path), ("depth", node.Depth), ("o", node.SortOrder), ("at", now), ("by", by)))
                        await cmd.ExecuteNonQueryAsync(ct);
                    nodes.Add(node);
                    kind = "Created";
                    created++;
                }
                else
                {
                    node = existing;
                    var expectedParent = existing.ParentId is { } pid ? tree.ById(pid)!.Code : null;
                    if (!string.Equals(expectedParent, n.Parent, StringComparison.OrdinalIgnoreCase)) { errors.Add($"{n.Code}: the document puts it under '{n.Parent ?? "(top)"}' but it is under '{expectedParent ?? "(top)"}'; use Move, which checks the classified parts."); continue; }
                    if (!string.Equals(existing.Status, n.Status, StringComparison.Ordinal) && n.Status is "active" or "retired") { errors.Add($"{n.Code}: status differs ({existing.Status} here, {n.Status} in the document); use Retire or Restore, which check the classified parts."); continue; }
                    var changed = existing.Name != n.Name.Trim() || existing.Description != (n.Description?.Trim() ?? "") || existing.SortOrder != n.SortOrder;
                    if (changed)
                    {
                        node = existing with { Name = n.Name.Trim(), Description = n.Description?.Trim() ?? "", SortOrder = n.SortOrder };
                        await using var cmd = AdminRunner.Cmd(conn, tx, "UPDATE cfg.class_node SET name = @n, description = @d, sort_order = @o, updated_at = @at, updated_by = @by WHERE id = @id",
                            ("n", node.Name), ("d", node.Description), ("o", node.SortOrder), ("at", now), ("by", by), ("id", node.Id));
                        await cmd.ExecuteNonQueryAsync(ct);
                        nodes[nodes.FindIndex(x => x.Id == node.Id)] = node;
                        detail.Add("details updated");
                    }
                    kind = changed ? "Updated" : "Unchanged";
                }
                tree = new ClassTree(nodes, attrs);

                var attributeChanged = false;
                foreach (var a in n.Attributes)
                {
                    var input = a.ToInput();
                    var current = tree.OwnAttributes(node.Id).FirstOrDefault(x => x.Name == a.Name);
                    if (current is null)
                    {
                        var rules = AttributeRules.Validate(input, null);
                        if (tree.NameClash(node.Id, a.Name) is { } clash) rules.Add(clash);
                        if (rules.Count > 0) { errors.AddRange(rules.Select(e => $"{n.Code}/{a.Name}: {e}")); continue; }
                        await AttributeTable.ForClasses.InsertAsync(conn, tx, node.Id, input, by, now, ct);
                        if (a.Retired) await AttributeTable.ForClasses.SetRetiredAsync(conn, tx, node.Id, a.Name, true, by, now, ct);
                        detail.Add($"attribute {a.Name} created");
                        attrs.Add(new AttributeDefinitionDto(node.Id.ToString(), a.Name, a.Label, a.DataType, a.Unit, a.EnumValues, a.Required, a.MaxLength, a.SortOrder, a.HelpText, a.Group, a.Pattern, a.Min, a.Max, a.Retired, 1, 0));
                        attributeChanged = true;
                    }
                    else
                    {
                        var inUse = await CountValuesAsync(conn, tx, node, a.Name, ct);
                        var currentDto = current with { ValuesInUse = inUse };
                        if (ConfigDiff.FieldDiff(AttributeDoc.From(current), a).Count == 0) continue;
                        var rules = AttributeRules.Validate(input, currentDto, currentDto.DataType == "enum" ? await EnumValuesInUseAsync(conn, tx, node, a.Name, ct) : null);
                        if (rules.Count > 0) { errors.AddRange(rules.Select(e => $"{n.Code}/{a.Name}: {e}")); continue; }
                        await AttributeTable.ForClasses.UpdateAsync(conn, tx, node.Id, input, by, now, ct);
                        if (a.Retired != current.Retired) await AttributeTable.ForClasses.SetRetiredAsync(conn, tx, node.Id, a.Name, a.Retired, by, now, ct);
                        attrs[attrs.FindIndex(x => x.Owner == current.Owner && x.Name == a.Name)] = current with
                        {
                            Label = a.Label.Trim(), DataType = a.DataType, Unit = a.Unit, EnumValues = a.EnumValues, Required = a.Required, MaxLength = a.MaxLength, SortOrder = a.SortOrder,
                            HelpText = a.HelpText, Group = a.Group, Pattern = a.Pattern, Min = a.Min, Max = a.Max, Retired = a.Retired,
                        };
                        detail.Add($"attribute {a.Name} updated");
                        attributeChanged = true;
                        if (existing is not null) touched.Add(node.Id);
                    }
                }
                if (attributeChanged)
                {
                    tree = new ClassTree(nodes, attrs);
                    if (kind == "Unchanged") { kind = "Updated"; }
                }
                if (kind == "Updated" && existing is not null) updated++;
                else if (kind == "Unchanged") unchanged++;
                lines.Add(new ImportLine(n.Code, kind, string.Join("; ", detail)));
            }
        }

        // Updated attributes of existing classes must not strand parts that are already classified there or below.
        if (errors.Count == 0 && touched.Count > 0)
        {
            var impact = await ImpactAsync(conn, tx, subject, tree, [.. touched.SelectMany(id => tree.Subtree(id)).Select(x => x.Id).Distinct()], ct);
            if (impact.Blocks) errors.AddRange(ImpactMessages(impact, "import"));
        }
        return Done(created, updated, unchanged);
    }
}
