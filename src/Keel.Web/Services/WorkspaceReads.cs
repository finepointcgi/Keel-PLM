using System.Globalization;
using Keel.Audit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Security;
using Keel.Workflow;
using Keel.Workspace;
using Npgsql;

namespace Keel.Web.Services;

/// <summary>An object shown on the Home dashboard or in the palette, read under the viewer's own access just now. Its markings travel with it so a page can compute its banner.</summary>
public sealed record ResolvedItem(string Kind, string Number, string Name, string StateLabel, Markings Markings, string Href, DateTimeOffset At)
{
    public string Banner => Markings.BannerText();
}

/// <summary>An item that needs the user: a workflow task, a change task, or a decided change. A restricted task has no object details at all.</summary>
public sealed record AttentionItem(string Kind, string Title, string Detail, string Href, Markings? Markings, bool Restricted, bool Overdue, DateTimeOffset? At);

/// <summary>Rows of one dashboard card with the aggregate banner of exactly the objects shown in it.</summary>
public sealed record CardResult<T>(IReadOnlyList<T> Items, string Banner);

/// <summary>
/// Everything the Home dashboard and the command palette read about the user's own workspace (ADR-0028). Stored pointers (recent, favorites) are only
/// numbers: each is read again through a SQL statement that carries <c>AccessPolicy.BuildReadFilter</c> for the signed-in user, so an object whose markings
/// were raised, or whose program the user left, silently disappears. Workflow and change tasks come from the module services. Each call audits what it returned
/// (kind and numbers), like the module searches do.
/// </summary>
public sealed class WorkspaceReads(CurrentUser users, WorkspaceService workspace, NpgsqlDataSource db, IAuditLog audit, WorkflowService flow, ChangeService changes, MbomService mboms, TimeProvider clock)
{
    public const int CardSize = 8;
    public static readonly TimeSpan ReleasedWindow = TimeSpan.FromDays(30);
    public static readonly TimeSpan DecisionWindow = TimeSpan.FromDays(14);

    // ---------------------------------------------------------------- personal pointers, re-authorized

    public async Task<CardResult<ResolvedItem>> RecentAsync(int take = CardSize, CancellationToken ct = default)
    {
        var s = await users.GetAsync();
        var pointers = await workspace.ListRecentAsync(s, WorkspaceLimits.RecentItems, ct);
        return await ResolvePointersAsync(s, "recent", pointers, take, ct);
    }

    public async Task<CardResult<ResolvedItem>> FavoritesAsync(int take = CardSize, CancellationToken ct = default)
    {
        var s = await users.GetAsync();
        var pointers = await workspace.ListFavoritesAsync(s, WorkspaceLimits.Favorites, ct);
        return await ResolvePointersAsync(s, "favorites", pointers, take, ct);
    }

    /// <summary>Resolves pointers in their order and keeps the first <paramref name="take"/> the subject may read now; the rest are dropped without a trace.</summary>
    public async Task<CardResult<ResolvedItem>> ResolvePointersAsync(Subject subject, string surface, IReadOnlyList<ObjectPointer> pointers, int take, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(subject, pointers, ct);
        var items = resolved.Take(take).ToList();
        await AuditAsync(subject, surface, items.Select(i => i.Number), ct);
        return new(items, Aggregate(items.Select(i => i.Markings)));
    }

    /// <summary>The pointers the subject can read right now, with name, state and markings, in the order given. Unreadable and vanished objects are absent.</summary>
    public async Task<IReadOnlyList<ResolvedItem>> ResolveAsync(Subject subject, IReadOnlyList<ObjectPointer> pointers, CancellationToken ct = default)
    {
        var found = new Dictionary<(string, string), ResolvedItem>();
        var corr = CurrentUser.NewCorrelationId();
        foreach (var group in pointers.GroupBy(p => p.Kind))
        {
            var at = group.ToDictionary(p => p.Number, p => p.At, StringComparer.Ordinal);
            var numbers = at.Keys.ToArray();
            IReadOnlyList<(string Number, string Name, string State, Markings M)> rows = group.Key switch
            {
                ObjectKinds.Part => await QueryAsync(subject, ObjectKinds.Part, numbers, ct),
                ObjectKinds.Document => await QueryAsync(subject, ObjectKinds.Document, numbers, ct),
                ObjectKinds.Change => await QueryAsync(subject, ObjectKinds.Change, numbers, ct),
                ObjectKinds.Baseline => await QueryAsync(subject, ObjectKinds.Baseline, numbers, ct),
                ObjectKinds.Mbom => await MbomRowsAsync(subject, numbers, corr, ct),
                ObjectKinds.Supplier => await QueryAsync(subject, ObjectKinds.Supplier, numbers, ct),
                _ => [],
            };
            foreach (var r in rows)
                found[(group.Key, r.Number)] = new ResolvedItem(group.Key, r.Number, r.Name, r.State, r.M, ObjectKinds.Path(group.Key, r.Number), at[r.Number]);
        }
        return pointers.Select(p => found.TryGetValue((p.Kind, p.Number), out var item) ? item : null).OfType<ResolvedItem>().ToList();
    }

    /// <summary>One read statement per kind. Each is a list over a marked table and carries the read filter of the signed-in user (architecture test).</summary>
    private async Task<IReadOnlyList<(string, string, string, Markings)>> QueryAsync(Subject subject, string kind, string[] numbers, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "m");
        var columns = MarkingColumns.Select("m");
        var sql = kind switch
        {
            ObjectKinds.Part => $"""
                SELECT m.number, m.name, COALESCE(ls.label, r.lifecycle_state), {columns}
                FROM plm.part_master m
                JOIN plm.part_revision r ON r.master_id = m.id AND r.is_latest
                LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = m.lifecycle_id AND ls.state = r.lifecycle_state
                WHERE {filter.Sql} AND m.number = ANY(@numbers)
                """,
            ObjectKinds.Document => $"""
                SELECT m.number, m.name, COALESCE(ls.label, r.lifecycle_state), {columns}
                FROM plm.document_master m
                JOIN plm.document_revision r ON r.master_id = m.id AND r.is_latest
                LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = m.lifecycle_id AND ls.state = r.lifecycle_state
                WHERE {filter.Sql} AND m.number = ANY(@numbers)
                """,
            ObjectKinds.Change => $"""
                SELECT m.number, m.title, COALESCE(ls.label, m.lifecycle_state), {columns}
                FROM chg.change m
                LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = m.lifecycle_id AND ls.state = m.lifecycle_state
                WHERE {filter.Sql} AND m.number = ANY(@numbers)
                """,
            ObjectKinds.Baseline => $"""
                SELECT m.number, m.name, COALESCE(ls.label, m.lifecycle_state), {columns}
                FROM cfg.baseline m
                LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = m.lifecycle_id AND ls.state = m.lifecycle_state
                WHERE {filter.Sql} AND m.number = ANY(@numbers)
                """,
            ObjectKinds.Supplier => $"""
                SELECT m.number, m.name, initcap(m.status), {columns}
                FROM src.supplier m
                WHERE {filter.Sql} AND m.number = ANY(@numbers)
                """,
            _ => throw new NotSupportedException($"'{kind}' pointers are not read here."),
        };
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("numbers", numbers);
        var rows = new List<(string, string, string, Markings)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), MarkingColumns.Read(r, 3)));
        return rows;
    }

    /// <summary>Manufacturing BOMs live in the BOM module's schema, so they are read through its search (exact number), which applies the read filter itself.</summary>
    private async Task<IReadOnlyList<(string, string, string, Markings)>> MbomRowsAsync(Subject subject, string[] numbers, string corr, CancellationToken ct)
    {
        var rows = new List<(string, string, string, Markings)>();
        foreach (var number in numbers.Take(CardSize * 2))
        {
            var o = await mboms.SearchAsync(subject, new MbomSearch(Query: number, Limit: 5), corr, ct);
            if (o.Value?.Items.FirstOrDefault(m => m.Number == number) is { } m) rows.Add((m.Number, $"{m.PartName} ({m.Plant})", m.StateLabel, m.Markings));
        }
        return rows;
    }

    // ---------------------------------------------------------------- dashboard cards

    /// <summary>Recently released parts and documents the user can read (state Released, changed in the last 30 days), newest first.</summary>
    public async Task<CardResult<ResolvedItem>> RecentlyReleasedAsync(int take = CardSize, CancellationToken ct = default)
    {
        var s = await users.GetAsync();
        var since = clock.GetUtcNow() - ReleasedWindow;
        var items = new List<ResolvedItem>();
        items.AddRange(await ReleasedAsync(s, ObjectKinds.Part, since, take, ct));
        items.AddRange(await ReleasedAsync(s, ObjectKinds.Document, since, take, ct));
        var top = items.OrderByDescending(i => i.At).ThenBy(i => i.Number, StringComparer.Ordinal).Take(take).ToList();
        await AuditAsync(s, "released", top.Select(i => i.Number), ct);
        return new(top, Aggregate(top.Select(i => i.Markings)));
    }

    private async Task<IReadOnlyList<ResolvedItem>> ReleasedAsync(Subject s, string kind, DateTimeOffset since, int take, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(s, "m");
        // ASSUMPTION(UX-8): "released" is the state code Released of the part and document lifecycles (the seeded default); an administrator-defined lifecycle that names it otherwise is not listed here.
        var sql = kind == ObjectKinds.Part
            ? $"""
              SELECT m.number, m.name, COALESCE(ls.label, r.lifecycle_state), r.state_changed_at, {MarkingColumns.Select("m")}
              FROM plm.part_master m
              JOIN plm.part_revision r ON r.master_id = m.id AND r.is_latest
              LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = m.lifecycle_id AND ls.state = r.lifecycle_state
              WHERE {filter.Sql} AND r.lifecycle_state = 'Released' AND r.state_changed_at >= @since
              ORDER BY r.state_changed_at DESC, m.number LIMIT @take
              """
            : $"""
              SELECT m.number, m.name, COALESCE(ls.label, r.lifecycle_state), r.state_changed_at, {MarkingColumns.Select("m")}
              FROM plm.document_master m
              JOIN plm.document_revision r ON r.master_id = m.id AND r.is_latest
              LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = m.lifecycle_id AND ls.state = r.lifecycle_state
              WHERE {filter.Sql} AND r.lifecycle_state = 'Released' AND r.state_changed_at >= @since
              ORDER BY r.state_changed_at DESC, m.number LIMIT @take
              """;
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("since", since);
        cmd.Parameters.AddWithValue("take", take);
        var rows = new List<ResolvedItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new ResolvedItem(kind, r.GetString(0), r.GetString(1), r.GetString(2), MarkingColumns.Read(r, 4), ObjectKinds.Path(kind, r.GetString(0)), r.GetFieldValue<DateTimeOffset>(3)));
        return rows;
    }

    /// <summary>Open change requests and notices of the programs the user belongs to (through the change service, so the access filter applies), newest first.</summary>
    public async Task<CardResult<ResolvedItem>> ProgramChangesAsync(int take = CardSize, CancellationToken ct = default)
    {
        var s = await users.GetAsync();
        if (s.Programs.Count == 0) return new([], UiText.Unclassified);
        // The program filter is part of the query (a client-side filter over the newest 200 open changes of all programs would miss a program's changes on a busy system); the read filter still applies.
        var open = await changes.SearchAsync(s, new ChangeSearchQuery(OpenOnly: true, Limit: 200, Programs: [.. s.Programs]), CurrentUser.NewCorrelationId(), ct);
        var items = open.Items
            .Where(c => c.Type is "ECR" or "ECN" && c.Markings.Program is { } p && s.Programs.Contains(p))
            .OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Number, StringComparer.Ordinal).Take(take)
            .Select(c => new ResolvedItem(ObjectKinds.Change, c.Number, c.Title, c.StateLabel, c.Markings, ObjectKinds.Path(ObjectKinds.Change, c.Number), c.CreatedAt)).ToList();
        return new(items, Aggregate(items.Select(i => i.Markings)));
    }

    /// <summary>Workflow tasks and review requests, change tasks assigned to the user, and changes the user requested that were decided in the last 14 days.</summary>
    public async Task<CardResult<AttentionItem>> AttentionAsync(CancellationToken ct = default)
    {
        var s = await users.GetAsync();
        var corr = CurrentUser.NewCorrelationId();
        var items = new List<AttentionItem>();
        var tasks = await flow.MyTasksAsync(s, corr, ct);
        foreach (var t in tasks.Items)
            items.Add(t.Redacted || t.Target is null
                ? new AttentionItem("task", t.Name, "Restricted item: you are no longer eligible to see it.", $"tasks/{t.Id}", null, true, t.Overdue, t.DueAt)
                : new AttentionItem("task", t.Name, $"{t.Target.Key} · {t.Target.Name}", $"tasks/{t.Id}", t.Target.Markings, false, t.Overdue, t.DueAt));
        var changeTasks = await changes.MyTasksAsync(s, corr, ct);
        foreach (var t in changeTasks.Items)
            items.Add(new AttentionItem("change task", $"{t.Number}: {t.Title}", $"For {t.EcnNumber} · {t.EcnTitle}", ObjectKinds.Path(ObjectKinds.Change, t.EcnNumber), t.Markings, false, t.Overdue,
                t.DueDate is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null));
        items.AddRange(await DecidedAsync(s, ct));
        var shown = items.Take(CardSize * 2).ToList();
        await AuditAsync(s, "attention", shown.Where(i => !i.Restricted).Select(i => i.Title), ct);
        return new(shown, Aggregate(shown.Where(i => i.Markings is not null).Select(i => i.Markings!)));
    }

    private async Task<IReadOnlyList<AttentionItem>> DecidedAsync(Subject s, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(s, "m");
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT m.number, m.title, COALESCE(ls.label, m.lifecycle_state), m.state_changed_at, {MarkingColumns.Select("m")}
            FROM chg.change m
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = m.lifecycle_id AND ls.state = m.lifecycle_state
            WHERE {filter.Sql} AND m.requester_sub = @sub AND m.change_type IN ('ECR', 'ECN')
              AND m.lifecycle_state IN ('Released', 'Implemented', 'Cancelled', 'Approved', 'Rejected') AND m.state_changed_at >= @since
            ORDER BY m.state_changed_at DESC, m.number LIMIT @take
            """, conn);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("sub", s.Id);
        cmd.Parameters.AddWithValue("since", clock.GetUtcNow() - DecisionWindow);
        cmd.Parameters.AddWithValue("take", CardSize);
        var rows = new List<AttentionItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new AttentionItem("decision", $"{r.GetString(0)}: {r.GetString(1)}", $"Your request is now {r.GetString(2).ToLowerInvariant()}", ObjectKinds.Path(ObjectKinds.Change, r.GetString(0)),
                MarkingColumns.Read(r, 4), false, false, r.GetFieldValue<DateTimeOffset>(3)));
        return rows;
    }

    // ---------------------------------------------------------------- helpers

    private static string Aggregate(IEnumerable<Markings> shown)
    {
        var list = shown.ToList();
        return list.Count == 0 ? UiText.Unclassified : Markings.AggregateBanner(list);
    }

    private async Task AuditAsync(Subject s, string surface, IEnumerable<string> shown, CancellationToken ct)
    {
        var list = shown.ToList();
        await audit.AppendAsync(new AuditEntry
        {
            Actor = ActorSnapshot.From(s),
            Action = "workspace.read",
            Outcome = AuditOutcome.Success,
            ObjectType = "workspace",
            ObjectId = surface,
            Details = AuditDetails.Of(("returned", list.Count.ToString(CultureInfo.InvariantCulture)), ("items", string.Join(",", list))),
        }, ct);
    }
}
