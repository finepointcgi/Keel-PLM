using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Cad;

/// <summary>
/// The dependency graph of CAD documents (ADR-0035): package view, where-used of CAD files, stale-drawing detection, and the
/// model-of / drawing-of associations with parts. Every list is read through <see cref="AccessPolicy.BuildReadFilter"/>; a document the
/// caller may not read becomes a redacted node (the edge facts only), never a name.
/// </summary>
public sealed class CadGraphService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock)
{
    public const int MaxLevels = 20;
    public const int MaxNodes = 2000;
    public const int MaxStaleItems = 500;
    public const int MaxPackageMembers = 500;

    internal sealed record Edge(
        Guid ParentMaster, Guid ChildMaster, string SeenRevision, int SeenIteration, string ChildRevision, int ChildIteration,
        Guid? OtherId, string? Number, string? Name, string? DocumentType, Markings? Markings, string? CadRole,
        string OtherRevision, int OtherIteration, string OtherState)
    {
        public bool Stale => ChildRevision != SeenRevision || ChildIteration != SeenIteration;
        public bool Visible => OtherId is not null;
    }

    private sealed record Head(Guid Id, string Number, string Name, string DocumentType, Markings Markings, string Revision, int Iteration, string State, string? CadRole);

    // ---------------------------------------------------------------- edges

    /// <summary>One level of edges. <paramref name="down"/>: from parents to what they reference; otherwise from documents to what references them (latest iterations only).</summary>
    internal static async Task<List<Edge>> QueryEdgesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Subject subject, IReadOnlyCollection<Guid> ids, bool down, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "om");
        var otherId = down ? "dep.child_master_id" : "pr.master_id";
        var inputColumn = down ? "pr.master_id" : "dep.child_master_id";
        var oRev = down ? "cr" : "pr";
        var sql =
            $"""
            SELECT pr.master_id, dep.child_master_id, dep.seen_revision, dep.seen_iteration, cr.revision, cr.latest_iteration,
                   v.id, v.number, v.name, v.document_type, {MarkingColumns.Select("v")}, cd.role,
                   {oRev}.revision, {oRev}.latest_iteration, {oRev}.lifecycle_state
            FROM cad.dependency dep
            JOIN plm.document_iteration pi ON pi.id = dep.parent_iteration_id
            JOIN plm.document_revision pr ON pr.id = pi.revision_id AND pr.is_latest AND pi.iteration = pr.latest_iteration
            JOIN plm.document_revision cr ON cr.master_id = dep.child_master_id AND cr.is_latest
            LEFT JOIN LATERAL (SELECT om.* FROM plm.document_master om WHERE om.id = {otherId} AND {filter.Sql}) v ON true
            LEFT JOIN cad.cad_document cd ON cd.document_master_id = v.id
            WHERE {inputColumn} = ANY(@ids)
            ORDER BY v.number NULLS LAST, dep.id
            """;
        var rows = new List<Edge>();
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var mk = 10;
        while (await r.ReadAsync(ct))
        {
            var visible = !r.IsDBNull(6);
            rows.Add(new Edge(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetInt32(5),
                visible ? r.GetGuid(6) : null, visible ? r.GetString(7) : null, visible ? r.GetString(8) : null, visible ? r.GetString(9) : null,
                visible ? MarkingColumns.Read(r, mk) : null, visible && !r.IsDBNull(mk + MarkingColumns.Count) ? r.GetString(mk + MarkingColumns.Count) : null,
                r.GetString(mk + MarkingColumns.Count + 1), r.GetInt32(mk + MarkingColumns.Count + 2), r.GetString(mk + MarkingColumns.Count + 3)));
        }
        return rows;
    }

    private static async Task<Head?> LoadHeadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string number, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT m.id, m.number, m.name, m.document_type, {MarkingColumns.Select("m")}, r.revision, r.latest_iteration, r.lifecycle_state, cd.role
            FROM plm.document_master m
            JOIN plm.document_revision r ON r.master_id = m.id AND r.is_latest
            LEFT JOIN cad.cad_document cd ON cd.document_master_id = m.id
            WHERE m.number = @n
            """, conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var mk = 4;
        return new Head(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), MarkingColumns.Read(r, mk), r.GetString(mk + MarkingColumns.Count),
            r.GetInt32(mk + MarkingColumns.Count + 1), r.GetString(mk + MarkingColumns.Count + 2), r.IsDBNull(mk + MarkingColumns.Count + 3) ? null : r.GetString(mk + MarkingColumns.Count + 3));
    }

    /// <summary>The numbers and CAD roles of a package: the top, everything it references (recursively) and the derived documents (drawings, PDFs, STEP) of each of those.</summary>
    internal static async Task<(List<(Guid Id, string Number, string Role)> Members, int Redacted)> ResolvePackageAsync(
        NpgsqlConnection conn, Subject subject, Guid top, string topNumber, string topRole, string topType, PackageOptions options, CancellationToken ct)
    {
        var members = new List<(Guid, string, string)> { (top, topNumber, topRole) };
        var seen = new HashSet<Guid> { top };
        var redacted = 0;
        var roleFilter = options.Roles is { Count: > 0 } r ? r.ToHashSet(StringComparer.Ordinal) : null;
        if (!options.IncludeDependents) return (members, redacted);

        var frontier = new List<Guid> { top };
        for (var level = 0; level < MaxLevels && frontier.Count > 0 && members.Count < MaxPackageMembers; level++)
        {
            var next = new List<Guid>();
            foreach (var e in await QueryEdgesAsync(conn, null, subject, frontier, down: true, ct))
            {
                if (!e.Visible) { redacted++; continue; }
                if (!seen.Add(e.OtherId!.Value)) continue;
                var role = e.CadRole ?? DefaultRole(e.DocumentType!);
                if (roleFilter is not null && !roleFilter.Contains(role)) continue;
                members.Add((e.OtherId.Value, e.Number!, role));
                next.Add(e.OtherId.Value);
            }
            frontier = next;
        }
        // Derived documents (drawings, PDFs, STEP) point at the model they were made from: take the ones that reference a member.
        foreach (var e in await QueryEdgesAsync(conn, null, subject, members.Select(m => m.Item1).ToArray(), down: false, ct))
        {
            if (!e.Visible) { redacted++; continue; }
            var role = e.CadRole ?? DefaultRole(e.DocumentType!);
            if (!CadRoles.IsDerived(role) || !seen.Add(e.OtherId!.Value)) continue;
            if (roleFilter is not null && !roleFilter.Contains(role)) continue;
            members.Add((e.OtherId.Value, e.Number!, role));
        }
        return (members, redacted);
    }


    /// <summary>A document with no registered CAD role is a drawing when its type says so, a model otherwise.</summary>
    public static string DefaultRole(string documentType) => documentType == "Drawing" ? CadRoles.Drawing : CadRoles.Model;

    // ---------------------------------------------------------------- views

    private static DependencyNode ToNode(Edge e, bool cycle, IReadOnlyList<DependencyNode> children) =>
        e.Visible
            ? new DependencyNode(e.Number, e.Name, e.CadRole ?? DefaultRole(e.DocumentType!), e.OtherRevision, e.OtherIteration, e.OtherState, e.Markings!.BannerText(), e.Markings,
                false, e.SeenRevision, e.SeenIteration, e.Stale, cycle, children)
            : new DependencyNode(null, null, null, null, null, null, null, null, true, e.SeenRevision, e.SeenIteration, e.Stale, false, []);

    private static async Task<(List<DependencyNode> Tree, int Redacted, int Stale)> BuildTreeAsync(NpgsqlConnection conn, Subject subject, Guid root, bool down, int levels, CancellationToken ct)
    {
        levels = Math.Clamp(levels, 1, MaxLevels);
        var byInput = new Dictionary<Guid, List<Edge>>();
        var expanded = new HashSet<Guid>();
        var frontier = new List<Guid> { root };
        var nodes = 0;
        for (var level = 0; level < levels && frontier.Count > 0 && nodes < MaxNodes; level++)
        {
            var batch = frontier.Where(expanded.Add).ToList();
            if (batch.Count == 0) break;
            var edges = await QueryEdgesAsync(conn, null, subject, batch, down, ct);
            nodes += edges.Count;
            foreach (var e in edges)
            {
                var input = down ? e.ParentMaster : e.ChildMaster;
                if (!byInput.TryGetValue(input, out var list)) byInput[input] = list = [];
                list.Add(e);
            }
            frontier = [.. edges.Where(e => e.Visible).Select(e => e.OtherId!.Value).Distinct()];
        }
        int redacted = 0, stale = 0;
        List<DependencyNode> Build(Guid id, HashSet<Guid> path)
        {
            var result = new List<DependencyNode>();
            foreach (var e in byInput.GetValueOrDefault(id) ?? [])
            {
                if (!e.Visible) redacted++;
                if (e.Stale) stale++;
                if (!e.Visible) { result.Add(ToNode(e, false, [])); continue; }
                var cycle = path.Contains(e.OtherId!.Value);
                if (cycle || path.Count >= levels) { result.Add(ToNode(e, cycle, [])); continue; }
                path.Add(e.OtherId.Value);
                result.Add(ToNode(e, false, Build(e.OtherId.Value, path)));
                path.Remove(e.OtherId.Value);
            }
            return result;
        }
        var tree = Build(root, [root]);
        return (tree, redacted, stale);
    }

    /// <summary>The package view of a document: what it was built against (with stale flags), what uses it, and the parts it is the model or drawing of.</summary>
    public async Task<Outcome<CadPackageView>> GetPackageAsync(Subject subject, string number, int levels, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var head = await LoadHeadAsync(conn, null, number, ct);
        if (head is null) return Outcome<CadPackageView>.NotFound();
        if (!AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read).Permitted)
        {
            await audit.AppendAsync(Entry(subject, "cad.package.read", AuditOutcome.Denied, number, correlationId, ("reasons", "read")), ct);
            return Outcome<CadPackageView>.NotFound();
        }
        var (uses, redactedDown, staleDown) = await BuildTreeAsync(conn, subject, head.Id, down: true, levels, ct);
        var (usedBy, redactedUp, _) = await BuildTreeAsync(conn, subject, head.Id, down: false, levels, ct);
        var parts = await AssociationsOfDocumentAsync(conn, subject, head.Id, staleDown > 0, ct);
        var banners = new List<Markings> { head.Markings };
        void Collect(IEnumerable<DependencyNode> ns) { foreach (var n in ns) { if (n.Markings is not null) banners.Add(n.Markings); Collect(n.Children); } }
        Collect(uses); Collect(usedBy);
        var banner = Markings.AggregateBanner(banners);
        await audit.AppendAsync(Entry(subject, "cad.package.read", AuditOutcome.Success, number, correlationId,
            ("uses", Count(uses).ToString(CultureInfo.InvariantCulture)), ("usedBy", Count(usedBy).ToString(CultureInfo.InvariantCulture)),
            ("redacted", (redactedDown + redactedUp).ToString(CultureInfo.InvariantCulture)), ("stale", staleDown.ToString(CultureInfo.InvariantCulture))), ct);
        return Outcome<CadPackageView>.Ok(new CadPackageView(head.Number, head.Name, head.CadRole, head.CadRole is not null, head.Revision, head.Iteration, uses, usedBy,
            parts, redactedDown + redactedUp, staleDown, banner));
    }

    private static int Count(IEnumerable<DependencyNode> nodes) => nodes.Sum(n => 1 + Count(n.Children));

    /// <summary>Where-used of a CAD file: the documents whose latest iteration references it, up to <paramref name="levels"/> levels.</summary>
    public async Task<Outcome<IReadOnlyList<DependencyNode>>> WhereUsedAsync(Subject subject, string number, int levels, string? correlationId, CancellationToken ct = default)
    {
        var view = await GetPackageAsync(subject, number, levels, correlationId, ct);
        return view.IsOk ? Outcome<IReadOnlyList<DependencyNode>>.Ok(view.Value!.UsedBy) : new Outcome<IReadOnlyList<DependencyNode>>(view.Kind, null, view.Errors);
    }

    /// <summary>Drawings, PDFs and STEP files whose model has a later iteration than the one they were made from.</summary>
    public async Task<StaleList> ListStaleAsync(Subject subject, int limit, string? correlationId, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxStaleItems);
        var drawingFilter = AccessPolicy.BuildReadFilter(subject, "dm");
        var modelFilter = AccessPolicy.BuildReadFilter(subject, "mm");
        var items = new List<StaleItem>();
        var markings = new List<Markings>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT v.number, v.name, cd.role, {MarkingColumns.Select("v")}, w.number, w.name, dep.seen_revision, dep.seen_iteration, cr.revision, cr.latest_iteration, w.id IS NULL
            FROM cad.dependency dep
            JOIN plm.document_iteration pi ON pi.id = dep.parent_iteration_id
            JOIN plm.document_revision pr ON pr.id = pi.revision_id AND pr.is_latest AND pi.iteration = pr.latest_iteration
            JOIN cad.cad_document cd ON cd.document_master_id = pr.master_id AND cd.role IN ('drawing', 'derived-pdf', 'step')
            JOIN plm.document_revision cr ON cr.master_id = dep.child_master_id AND cr.is_latest
            JOIN LATERAL (SELECT dm.* FROM plm.document_master dm WHERE dm.id = pr.master_id AND {drawingFilter.Sql}) v ON true
            LEFT JOIN LATERAL (SELECT mm.* FROM plm.document_master mm WHERE mm.id = dep.child_master_id AND {modelFilter.Sql}) w ON true
            WHERE cr.revision <> dep.seen_revision OR cr.latest_iteration <> dep.seen_iteration
            ORDER BY v.number
            LIMIT @limit
            """, conn))
        {
            foreach (var (k, v) in drawingFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
            foreach (var (k, v) in modelFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("limit", limit);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var mk = 3;
            var o = mk + MarkingColumns.Count;
            while (await r.ReadAsync(ct))
            {
                var m = MarkingColumns.Read(r, mk);
                markings.Add(m);
                var hidden = r.GetBoolean(o + 6);
                items.Add(new StaleItem(r.GetString(0), r.GetString(1), r.GetString(2), m.BannerText(), hidden ? null : r.GetString(o), hidden ? null : r.GetString(o + 1), hidden,
                    $"{r.GetString(o + 2)}.{r.GetInt32(o + 3).ToString(CultureInfo.InvariantCulture)}", $"{r.GetString(o + 4)}.{r.GetInt32(o + 5).ToString(CultureInfo.InvariantCulture)}"));
            }
        }
        await audit.AppendAsync(Entry(subject, "cad.stale.list", AuditOutcome.Success, null, correlationId, ("returned", items.Count.ToString(CultureInfo.InvariantCulture))), ct);
        return new StaleList(items, Markings.AggregateBanner(markings));
    }

    // ---------------------------------------------------------------- CAD role

    public async Task<Outcome<string>> RegisterRoleAsync(Subject subject, string number, string role, string? cadSystem, string? correlationId, CancellationToken ct = default)
    {
        if (!CadRoles.All.Contains(role)) return Outcome<string>.Invalid([$"Role must be one of: {string.Join(", ", CadRoles.All)}."]);
        if (cadSystem is { Length: > 60 }) return Outcome<string>.Invalid(["CAD system must be at most 60 characters."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var head = await LoadHeadAsync(conn, null, number, ct);
        var denied = await Authorize(subject, head, PlmAction.Modify, "cad.role.register", number, correlationId, ct);
        if (denied is not null) return denied;
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO cad.cad_document (document_master_id, role, cad_system, registered_at, registered_by) VALUES (@id, @role, @sys, @at, @by)
            ON CONFLICT (document_master_id) DO UPDATE SET role = EXCLUDED.role, cad_system = EXCLUDED.cad_system
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", head!.Id);
            cmd.Parameters.AddWithValue("role", role);
            cmd.Parameters.AddWithValue("sys", (object?)cadSystem ?? DBNull.Value);
            cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
            cmd.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "cad.role.register", AuditOutcome.Success, number, correlationId, ("role", role), ("previous", head.CadRole)), ct);
        await tx.CommitAsync(ct);
        return Outcome<string>.Ok(role);
    }

    // ---------------------------------------------------------------- associations

    public async Task<Outcome<AssociationView>> AddAssociationAsync(Subject subject, string documentNumber, string partNumber, string kind, string? correlationId, CancellationToken ct = default)
    {
        if (!AssociationKinds.All.Contains(kind)) return Outcome<AssociationView>.Invalid([$"Kind must be one of: {string.Join(", ", AssociationKinds.All)}."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var head = await LoadHeadAsync(conn, null, documentNumber, ct);
        var denied = await Authorize(subject, head, PlmAction.Modify, "cad.association.add", documentNumber, correlationId, ct);
        if (denied is not null) return new Outcome<AssociationView>(denied.Kind, null, denied.Errors);

        // The part: a key lookup the caller's access is evaluated on; not readable is reported as not found.
        Guid partId; string partName; Markings partMarkings;
        await using (var cmd = new NpgsqlCommand($"SELECT p.id, p.name, {MarkingColumns.Select("p")} FROM plm.part_master p WHERE p.number = @n", conn))
        {
            cmd.Parameters.AddWithValue("n", partNumber);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Outcome<AssociationView>.Invalid([$"Part '{partNumber}' was not found, or you are not authorized to see it."]);
            partId = r.GetGuid(0); partName = r.GetString(1); partMarkings = MarkingColumns.Read(r, 2);
        }
        if (!AccessPolicy.Evaluate(subject, partMarkings, PlmAction.Read).Permitted)
        {
            await audit.AppendAsync(Entry(subject, "cad.association.add", AuditOutcome.Denied, documentNumber, correlationId, ("reasons", "part.not_readable")), ct);
            return Outcome<AssociationView>.Invalid([$"Part '{partNumber}' was not found, or you are not authorized to see it."]);
        }
        var id = Guid.CreateVersion7();
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO cad.association (id, kind, document_master_id, part_master_id, created_at, created_by) VALUES (@id, @k, @d, @p, @at, @by) ON CONFLICT (kind, document_master_id, part_master_id) DO NOTHING RETURNING id", conn, tx))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("k", kind);
            ins.Parameters.AddWithValue("d", head!.Id);
            ins.Parameters.AddWithValue("p", partId);
            ins.Parameters.AddWithValue("at", clock.GetUtcNow());
            ins.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            if (await ins.ExecuteScalarAsync(ct) is null)
            {
                await tx.RollbackAsync(ct);
                return Outcome<AssociationView>.Conflict($"{documentNumber} is already the {kind} {partNumber}.");
            }
        }
        await audit.AppendAsync(tx, Entry(subject, "cad.association.add", AuditOutcome.Success, documentNumber, correlationId, ("kind", kind), ("part", partNumber)), ct);
        await tx.CommitAsync(ct);
        return Outcome<AssociationView>.Ok(new AssociationView(id, kind, partNumber, partName, head.Number, head.Name, Markings.AggregateBanner([partMarkings, head.Markings]), false, false));
    }

    public async Task<Outcome<bool>> RemoveAssociationAsync(Subject subject, Guid id, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        string? number = null; Markings? markings = null;
        await using (var cmd = new NpgsqlCommand(
            $"SELECT d.number, {MarkingColumns.Select("d")} FROM cad.association a JOIN plm.document_master d ON d.id = a.document_master_id WHERE a.id = @id", conn))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { number = r.GetString(0); markings = MarkingColumns.Read(r, 1); }
        }
        if (number is null) return Outcome<bool>.NotFound();
        if (!AccessPolicy.Evaluate(subject, markings!, PlmAction.Read).Permitted)
        {
            await audit.AppendAsync(Entry(subject, "cad.association.remove", AuditOutcome.Denied, number, correlationId, ("reasons", "read")), ct);
            return Outcome<bool>.NotFound();
        }
        var modify = AccessPolicy.Evaluate(subject, markings!, PlmAction.Modify);
        if (!modify.Permitted)
        {
            await audit.AppendAsync(Entry(subject, "cad.association.remove", AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", modify.Reasons))), ct);
            return Outcome<bool>.Forbidden(modify.Reasons);
        }
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var del = new NpgsqlCommand("DELETE FROM cad.association WHERE id = @id", conn, tx))
        {
            del.Parameters.AddWithValue("id", id);
            await del.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "cad.association.remove", AuditOutcome.Success, number, correlationId), ct);
        await tx.CommitAsync(ct);
        return Outcome<bool>.Ok(true);
    }

    /// <summary>The CAD documents (models, drawings) associated with a part, readable by the caller, with a stale flag.</summary>
    public async Task<Outcome<IReadOnlyList<AssociationView>>> ListForPartAsync(Subject subject, string partNumber, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        Guid partId; Markings partMarkings;
        await using (var cmd = new NpgsqlCommand($"SELECT p.id, {MarkingColumns.Select("p")} FROM plm.part_master p WHERE p.number = @n", conn))
        {
            cmd.Parameters.AddWithValue("n", partNumber);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Outcome<IReadOnlyList<AssociationView>>.NotFound();
            partId = r.GetGuid(0); partMarkings = MarkingColumns.Read(r, 1);
        }
        if (!AccessPolicy.Evaluate(subject, partMarkings, PlmAction.Read).Permitted)
        {
            await audit.AppendAsync(Entry(subject, "cad.association.list", AuditOutcome.Denied, partNumber, correlationId, ("reasons", "read")), ct);
            return Outcome<IReadOnlyList<AssociationView>>.NotFound();
        }
        var filter = AccessPolicy.BuildReadFilter(subject, "dm");
        var list = new List<AssociationView>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT a.id, a.kind, v.number, v.name, {MarkingColumns.Select("v")},
                   EXISTS (SELECT 1 FROM cad.dependency dep
                           JOIN plm.document_iteration pi ON pi.id = dep.parent_iteration_id
                           JOIN plm.document_revision pr ON pr.id = pi.revision_id AND pr.is_latest AND pi.iteration = pr.latest_iteration
                           JOIN plm.document_revision cr ON cr.master_id = dep.child_master_id AND cr.is_latest
                           WHERE pr.master_id = a.document_master_id AND (cr.revision <> dep.seen_revision OR cr.latest_iteration <> dep.seen_iteration))
            FROM cad.association a
            LEFT JOIN LATERAL (SELECT dm.* FROM plm.document_master dm WHERE dm.id = a.document_master_id AND {filter.Sql}) v ON true
            WHERE a.part_master_id = @part
            ORDER BY a.kind, v.number NULLS LAST
            """, conn))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("part", partId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var visible = !r.IsDBNull(2);
                list.Add(new AssociationView(r.GetGuid(0), r.GetString(1), partNumber, null, visible ? r.GetString(2) : null, visible ? r.GetString(3) : null,
                    visible ? MarkingColumns.Read(r, 4).BannerText() : null, !visible, r.GetBoolean(4 + MarkingColumns.Count)));
            }
        }
        await audit.AppendAsync(Entry(subject, "cad.association.list", AuditOutcome.Success, partNumber, correlationId, ("returned", list.Count.ToString(CultureInfo.InvariantCulture))), ct);
        return Outcome<IReadOnlyList<AssociationView>>.Ok(list);
    }

    private static async Task<List<AssociationView>> AssociationsOfDocumentAsync(NpgsqlConnection conn, Subject subject, Guid documentId, bool stale, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "pm");
        var list = new List<AssociationView>();
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT a.id, a.kind, v.number, v.name, {MarkingColumns.Select("v")}
            FROM cad.association a
            LEFT JOIN LATERAL (SELECT pm.* FROM plm.part_master pm WHERE pm.id = a.part_master_id AND {filter.Sql}) v ON true
            WHERE a.document_master_id = @doc
            ORDER BY a.kind, v.number NULLS LAST
            """, conn);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("doc", documentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var visible = !r.IsDBNull(2);
            list.Add(new AssociationView(r.GetGuid(0), r.GetString(1), visible ? r.GetString(2) : null, visible ? r.GetString(3) : null, null, null,
                visible ? MarkingColumns.Read(r, 4).BannerText() : null, !visible, stale));
        }
        return list;
    }

    // ---------------------------------------------------------------- helpers

    private async Task<Outcome<string>?> Authorize(Subject subject, Head? head, PlmAction action, string auditAction, string number, string? correlationId, CancellationToken ct)
    {
        if (head is null) return Outcome<string>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(Entry(subject, auditAction, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            return Outcome<string>.NotFound();
        }
        var decision = AccessPolicy.Evaluate(subject, head.Markings, action);
        if (decision.Permitted) return null;
        await audit.AppendAsync(Entry(subject, auditAction, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", decision.Reasons))), ct);
        return Outcome<string>.Forbidden(decision.Reasons);
    }

    internal static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string? objectId, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s),
        Action = action,
        Outcome = outcome,
        ObjectType = "cad",
        ObjectId = objectId,
        CorrelationId = correlationId,
        Details = AuditDetails.Of(details),
    };
}
