using System.Data.Common;
using Keel.Core;
using Npgsql;

namespace Keel.Admin;

/// <summary>
/// Loads migrated classification (ADR-0023) through the real classification code: classes are created by the hierarchy import code, class data is written by the
/// same routine check-in uses (<see cref="ClassificationParticipant.InsertFrozenAsync"/>), so imported iterations carry the same rows and the same invariants
/// as native ones (iteration-owned, immutable, one typed row per value for the facet search).
/// </summary>
public sealed class MigrationClassificationLoader(ClassificationService classes) : IClassificationLoader
{
    private readonly object _gate = new();
    private DbTransaction? _cachedTx;
    private ClassTree? _cachedTree;

    private static (NpgsqlConnection, NpgsqlTransaction?) Unwrap(DbConnection conn, DbTransaction? tx) =>
        (conn as NpgsqlConnection ?? throw new InvalidOperationException("The classification loader requires an Npgsql connection."), tx as NpgsqlTransaction);

    private void Invalidate() { lock (_gate) { _cachedTx = null; _cachedTree = null; } }

    public async Task<IReadOnlyList<ClassDefinition>> LoadDefinitionsAsync(DbConnection conn, DbTransaction? tx, CancellationToken ct)
    {
        var (c, t) = Unwrap(conn, tx);
        var tree = await ClassificationService.LoadTreeAsync(c, t, ct);
        return [.. tree.Nodes.OrderBy(n => n.Path, StringComparer.Ordinal).Select(n =>
            new ClassDefinition(n.Code, n.ParentId is { } p ? tree.ById(p)!.Code : null, n.IsActive, [.. tree.OwnAttributes(n.Id).Select(a => a.ToSpec())]))];
    }

    public async Task<IReadOnlyList<string>> CreateMissingClassesAsync(DbConnection conn, DbTransaction tx, IReadOnlyList<ClassNodeSpec> nodes, string actor, DateTimeOffset at, CancellationToken ct)
    {
        var (c, t) = Unwrap(conn, tx);
        var existing = await ClassificationService.LoadTreeAsync(c, t, ct);
        var missing = nodes.Where(n => existing.ByCode(n.Code) is null).ToList();
        if (missing.Count == 0) return [];
        var doc = new ClassHierarchyDoc(ClassHierarchyDoc.CurrentFormat, [.. missing.Select(n => new ClassNodeDoc(n.Code, n.Name, n.Description, n.ParentCode, n.SortOrder, "active",
            [.. n.Attributes.Select(a => new AttributeDoc(a.Name, a.Label, a.DataType, a.Unit, a.EnumValues, a.Required, a.MaxLength, a.SortOrder, a.HelpText, a.Group, a.Pattern, a.Min, a.Max, a.Retired))]))]);
        var report = await classes.ApplyForMigrationAsync(c!, t!, actor, doc, ct);
        Invalidate();
        if (report.Errors.Count > 0) throw new ClassificationLoadException("The classification hierarchy could not be created: " + string.Join(" ", report.Errors));
        return [.. report.Lines.Where(l => l.Action == "Created").Select(l => l.Code)];
    }

    public async Task AssignAsync(DbConnection conn, DbTransaction tx, Guid partIterationId, ClassAssignment assignment, CancellationToken ct)
    {
        var (c, t) = Unwrap(conn, tx);
        ClassTree tree;
        lock (_gate) tree = ReferenceEquals(_cachedTx, tx) && _cachedTree is not null ? _cachedTree : null!;
        if (tree is null)
        {
            tree = await ClassificationService.LoadTreeAsync(c, t, ct);
            lock (_gate) { _cachedTx = tx; _cachedTree = tree; }
        }
        var node = tree.ByCode(assignment.ClassCode) ?? throw new ClassificationLoadException($"Class '{assignment.ClassCode}' does not exist.");
        if (!node.IsActive) throw new ClassificationLoadException($"Class {node.Code} is retired.");
        var (normalized, errors) = AttributeSpecs.Validate(tree.EffectiveSpecs(node.Id), assignment.Attributes);
        if (errors.Count > 0) throw new ClassificationLoadException($"Attributes of class {node.Code} are invalid: {string.Join(" ", errors)}");
        await ClassificationParticipant.InsertFrozenAsync(c, t!, partIterationId, node.Id, node.Code, normalized, ct);
    }

    public async Task<IReadOnlyList<ClassifiedIteration>> ReadAsync(DbConnection conn, IReadOnlyCollection<Guid> partIterationIds, CancellationToken ct)
    {
        var (c, _) = Unwrap(conn, null);
        var list = new List<ClassifiedIteration>();
        await using var cmd = AdminRunner.Cmd(c, null,
            """
            SELECT pc.iteration_id, pc.class_code, pc.attributes::text, (SELECT count(*) FROM cfg.part_class_value v WHERE v.iteration_id = pc.iteration_id)
            FROM cfg.part_classification pc WHERE pc.iteration_id = ANY(@ids)
            """, ("ids", partIterationIds.ToArray()));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ClassifiedIteration(r.GetGuid(0), r.GetString(1), r.GetString(2), (int)r.GetInt64(3)));
        return list;
    }
}
