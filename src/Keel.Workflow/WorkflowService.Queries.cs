using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Workflow;

public sealed partial class WorkflowService
{
    public const int MaxListLimit = 200;

    // ---------------------------------------------------------------- inbox

    /// <summary>
    /// The caller's open tasks: tasks assigned to them, and pool tasks for a role they hold. A task assigned to the caller
    /// whose object they can no longer read stays in the list, redacted (no object name, number or markings) and unsignable;
    /// pool tasks they cannot read are simply absent.
    /// </summary>
    /// <summary>
    /// The sort keys the task list accepts (GAP-A-1). The order is applied to the items AFTER redaction, from the values the caller is shown
    /// (a redacted task has no item and no starter, so those sort last), so a hidden object never influences it.
    /// </summary>
    public static readonly SortMap SortKeys = new(("task", []), ("item", []), ("started", []), ("due", []));

    public Task<MyTasks> MyTasksAsync(Subject subject, string? correlationId, CancellationToken ct = default) => MyTasksAsync(subject, null, correlationId, ct);

    public async Task<MyTasks> MyTasksAsync(Subject subject, SortSpec? sort, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var tasks = (await WorkflowStore.LoadOpenTasksForAsync(conn, tx, subject, ct))
            .Where(t => t.AssigneeSub == subject.Id || WorkflowRoles.Satisfies(subject.Roles, t.RequiredRole)).ToList();

        var instances = new Dictionary<Guid, InstanceRow>();
        foreach (var id in tasks.Select(t => t.InstanceId).Distinct())
            if (await WorkflowStore.LoadInstanceAsync(conn, tx, id, forUpdate: false, ct) is { } row) instances[id] = row;

        var objects = new Dictionary<Guid, WorkflowObject>();
        foreach (var group in instances.Values.GroupBy(i => i.ObjectType))
            if (GatewayFor(group.Key) is { } gateway)
                foreach (var (key, value) in await gateway.FindManyByRevisionAsync(conn, tx, [.. group.Select(i => i.RevisionId)], ct))
                    objects[key] = value;

        var items = new List<TaskSummary>();
        var shown = new List<Markings>();
        var redacted = 0;
        foreach (var t in tasks)
        {
            var inst = instances[t.InstanceId];
            // The submitter never signs their own submission, so such pool tasks are not theirs to see.
            if (inst.StartedBy == subject.Id && t.AssigneeSub is null) continue;
            var isPool = t.AssigneeSub is null;
            objects.TryGetValue(inst.RevisionId, out var obj);
            var canRead = obj is not null && AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Read).Permitted;
            var overdue = t.DueAt is { } due && due < now;
            if (canRead)
            {
                shown.Add(obj!.Markings);
                items.Add(new TaskSummary(t.Id, inst.Id, t.Name, t.Kind, false, Summarize(inst, obj), inst.StartedByName, inst.StartedAt, t.DueAt, overdue, isPool));
            }
            else if (!isPool)
            {
                redacted++;
                items.Add(new TaskSummary(t.Id, inst.Id, t.Name, t.Kind, true, null, null, null, t.DueAt, overdue, false));
            }
        }
        if (sort is not null) items = SortTasks(items, sort);
        await audit.AppendAsync(tx, Entry(subject, "workflow.tasks_list", AuditOutcome.Success, "workflow_task", null, correlationId,
            ("returned", items.Count.ToString(CultureInfo.InvariantCulture)), ("redacted", redacted.ToString(CultureInfo.InvariantCulture)),
            ("sort", $"{sort?.Key} {sort?.DirectionText}".Trim())), ct);
        await tx.CommitAsync(ct);
        return new MyTasks(items, Markings.AggregateBanner(shown));
    }

    private static List<TaskSummary> SortTasks(List<TaskSummary> items, SortSpec sort)
    {
        if (!SortKeys.Allows(sort.Key)) throw new ArgumentException($"Cannot sort by '{sort.Key}'.", nameof(sort));
        IComparable? KeyOf(TaskSummary t) => sort.Key switch
        {
            "task" => t.Name.ToUpperInvariant(),
            "item" => t.Target?.Key.ToUpperInvariant(),
            "started" => t.StartedByName?.ToUpperInvariant(),
            _ => t.DueAt,
        };
        // Nulls last in both directions; ties keep the order the list was built in (a stable sort).
        var keyed = items.Select((t, i) => (t, i, k: KeyOf(t))).ToList();
        keyed.Sort((a, b) =>
        {
            if (a.k is null || b.k is null) return a.k is null && b.k is null ? a.i.CompareTo(b.i) : a.k is null ? 1 : -1;
            var c = a.k.CompareTo(b.k);
            if (sort.Descending) c = -c;
            return c != 0 ? c : a.i.CompareTo(b.i);
        });
        return [.. keyed.Select(x => x.t)];
    }

    // ---------------------------------------------------------------- task page

    /// <summary>
    /// Task detail for its assignee (or role-pool member), the person who started the workflow, or an administrator. Anyone
    /// who cannot read the object gets NotFound, except the assignee, who gets a redacted task that cannot be signed.
    /// </summary>
    public async Task<Outcome<TaskDetail>> GetTaskAsync(Subject subject, Guid taskId, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var instanceId = await WorkflowStore.InstanceIdOfTaskAsync(conn, tx, taskId, ct);
        if (instanceId is null) return Outcome<TaskDetail>.NotFound();
        var inst = (await WorkflowStore.LoadInstanceAsync(conn, tx, instanceId.Value, forUpdate: false, ct))!;
        var tasks = await WorkflowStore.LoadTasksAsync(conn, tx, inst.Id, ct);
        var task = tasks.Single(t => t.Id == taskId);
        var now = clock.GetUtcNow();

        var isAssignee = task.AssigneeSub == subject.Id;
        var isPoolMember = task.AssigneeSub is null && task.Status == WorkTaskStatus.Open && WorkflowRoles.Satisfies(subject.Roles, task.RequiredRole);
        var isStarter = inst.StartedBy == subject.Id;
        var isAdmin = AccessPolicy.HasRoleFor(subject, PlmAction.Administer);
        if (!isAssignee && !isPoolMember && !isStarter && !isAdmin) return Outcome<TaskDetail>.NotFound();

        var gateway = GatewayFor(inst.ObjectType) ?? throw new InvalidOperationException($"No gateway for object type {inst.ObjectType}.");
        var obj = await gateway.FindByRevisionAsync(conn, tx, inst.RevisionId, forUpdate: false, ct);
        var read = obj is null ? null : AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Read);
        if (obj is null || read is not { Permitted: true })
        {
            await audit.AppendAsync(tx, Entry(subject, "workflow.task_read", AuditOutcome.Denied, inst.ObjectType, inst.ObjectKey, correlationId,
                ("task", taskId.ToString("D")), ("reasons", read is null ? "object.missing" : string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            if (!isAssignee) return Outcome<TaskDetail>.NotFound();
            // Redacted: the assignee knows the task exists; nothing about the object is revealed.
            var redactedSummary = new TaskSummary(task.Id, inst.Id, task.Name, task.Kind, true, null, null, null, task.DueAt, task.Status == WorkTaskStatus.Open && task.DueAt is { } d && d < now, false);
            return Outcome<TaskDetail>.Ok(new TaskDetail(redactedSummary, task.Status.ToString(), task.Decision, null, null, null, [], false, ["task.not_eligible"],
                false, options.StepUpMaxAgeSeconds, UiBanner.Unclassified));
        }

        var check = CheckCanSign(subject, inst, task, obj);
        var codes = (await WorkflowStore.LoadReasonCodesAsync(conn, tx, ct)).Where(c => c.AppliesTo.Contains(task.Kind))
            .Select(c => new ReasonCodeView(c.Code, c.Label, c.Decision, c.RequiresComment)).ToList();
        var content = await gateway.LoadIterationContentAsync(conn, tx, inst.IterationId, ct);
        var view = await BuildInstanceViewAsync(conn, tx, subject, inst, obj, now, ct);
        await audit.AppendAsync(tx, Entry(subject, "workflow.task_read", AuditOutcome.Success, inst.ObjectType, inst.ObjectKey, correlationId,
            ("task", taskId.ToString("D")), ("instance", inst.Id.ToString("D"))), ct);
        await tx.CommitAsync(ct);

        var summary = new TaskSummary(task.Id, inst.Id, task.Name, task.Kind, false, Summarize(inst, obj), inst.StartedByName, inst.StartedAt, task.DueAt,
            task.Status == WorkTaskStatus.Open && task.DueAt is { } due && due < now, task.AssigneeSub is null);
        return Outcome<TaskDetail>.Ok(new TaskDetail(summary, task.Status.ToString(), task.Decision, content?.Description,
            content is null ? null : JsonNode.Parse(content.AttributesJson)!.AsObject(), view, codes, check.IsAllowed, check.IsAllowed ? [] : check.Reasons,
            check.IsAllowed && !StepUp.IsFresh(subject, now, options.StepUpMaxAge), options.StepUpMaxAgeSeconds, obj.Markings.BannerText(),
            content is null ? null : new ReviewSummary(
                Convert.ToHexStringLower(SignatureHasher.ContentHash(new SignedContent(content.ObjectType, content.ObjectKey, content.Revision, content.IterationId,
                    content.IterationLabel, content.Description, content.AttributesJson, obj.Markings))),
                inst.Comment, view.TemplateName, view.TemplateVersion)));
    }

    // ---------------------------------------------------------------- instance pages

    /// <summary>Instance detail for anyone who may read the object (404 otherwise; the denial is audited).</summary>
    public async Task<Outcome<InstanceView>> GetInstanceAsync(Subject subject, Guid instanceId, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var inst = await WorkflowStore.LoadInstanceAsync(conn, tx, instanceId, forUpdate: false, ct);
        if (inst is null) return Outcome<InstanceView>.NotFound();
        var gateway = GatewayFor(inst.ObjectType) ?? throw new InvalidOperationException($"No gateway for object type {inst.ObjectType}.");
        var obj = await gateway.FindByRevisionAsync(conn, tx, inst.RevisionId, forUpdate: false, ct);
        var read = obj is null ? null : AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Read);
        if (obj is null || read is not { Permitted: true })
        {
            await audit.AppendAsync(tx, Entry(subject, "workflow.read", AuditOutcome.Denied, inst.ObjectType, inst.ObjectKey, correlationId,
                ("instance", inst.Id.ToString("D")), ("reasons", read is null ? "object.missing" : string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<InstanceView>.NotFound();
        }
        var view = await BuildInstanceViewAsync(conn, tx, subject, inst, obj, clock.GetUtcNow(), ct);
        await audit.AppendAsync(tx, Entry(subject, "workflow.read", AuditOutcome.Success, inst.ObjectType, inst.ObjectKey, correlationId, ("instance", inst.Id.ToString("D"))), ct);
        await tx.CommitAsync(ct);
        return Outcome<InstanceView>.Ok(view);
    }

    /// <summary>
    /// Administrator view of all workflow instances. The SQL access filter runs over the marking snapshot stored on each instance,
    /// and every row is checked again against the live markings, so an administrator who may not read an ITAR part never sees its reviews.
    /// </summary>
    public async Task<Outcome<InstancePage>> ListInstancesAsync(Subject subject, string? status, int limit, int offset, string? correlationId, CancellationToken ct = default)
    {
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Administer))
        {
            await audit.AppendAsync(Entry(subject, "workflow.list", AuditOutcome.Denied, "workflow", null, correlationId, ("reasons", AccessPolicy.RoleMissing)), ct);
            return Outcome<InstancePage>.Forbidden([AccessPolicy.RoleMissing]);
        }
        limit = Math.Clamp(limit, 1, MaxListLimit);
        offset = Math.Max(0, offset);
        var filter = AccessPolicy.BuildReadFilter(subject, "i");

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var rows = new List<(InstanceRow Row, int Open)>();
        long total = 0;
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT i.id, count(*) OVER (), (SELECT count(*) FROM wf.task t WHERE t.instance_id = i.id AND t.status = 'Open')
            FROM wf.instance i
            WHERE {filter.Sql} AND (@status::text IS NULL OR i.status = @status)
            ORDER BY i.started_at DESC, i.id
            LIMIT @limit OFFSET @offset
            """, conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("status", (object?)status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);
            var ids = new List<(Guid Id, int Open)>();
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                {
                    ids.Add((r.GetGuid(0), (int)r.GetInt64(2)));
                    total = r.GetInt64(1);
                }
            foreach (var (id, open) in ids)
                if (await WorkflowStore.LoadInstanceAsync(conn, tx, id, forUpdate: false, ct) is { } row) rows.Add((row, open));
        }

        var objects = new Dictionary<Guid, WorkflowObject>();
        foreach (var group in rows.GroupBy(r => r.Row.ObjectType))
            if (GatewayFor(group.Key) is { } gateway)
                foreach (var (key, value) in await gateway.FindManyByRevisionAsync(conn, tx, [.. group.Select(r => r.Row.RevisionId)], ct))
                    objects[key] = value;

        var items = new List<InstanceSummary>();
        foreach (var (row, open) in rows)
        {
            if (!objects.TryGetValue(row.RevisionId, out var obj) || !AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Read).Permitted) continue;
            items.Add(new InstanceSummary(row.Id, row.Status, Summarize(row, obj), TemplateNameOf(row), row.StartedByName, row.StartedAt, row.CompletedAt, open));
        }
        await audit.AppendAsync(tx, Entry(subject, "workflow.list", AuditOutcome.Success, "workflow", null, correlationId,
            ("status", status), ("returned", items.Count.ToString(CultureInfo.InvariantCulture)), ("instances", string.Join(",", items.Select(i => i.Id.ToString("D"))))), ct);
        await tx.CommitAsync(ct);
        return Outcome<InstancePage>.Ok(new InstancePage(items, total, Markings.AggregateBanner(items.Select(i => i.Target.Markings))));
    }

    /// <summary>Running review, latest review and release history of one object, for its page. NotFound when the caller cannot read it.</summary>
    public async Task<Outcome<ObjectWorkflowStatus>> StatusForObjectAsync(Subject subject, string objectType, string objectKey, string? correlationId, CancellationToken ct = default)
    {
        var gateway = GatewayFor(objectType);
        if (gateway is null) return Outcome<ObjectWorkflowStatus>.NotFound();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var obj = await gateway.FindByKeyAsync(conn, tx, objectKey, forUpdate: false, ct);
        var read = obj is null ? null : AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Read);
        if (obj is null || read is not { Permitted: true })
        {
            if (read is not null)
                await audit.AppendAsync(tx, Entry(subject, "workflow.status", AuditOutcome.Denied, objectType, objectKey, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<ObjectWorkflowStatus>.NotFound();
        }

        var ids = new List<Guid>();
        await using (var cmd = new NpgsqlCommand("SELECT i.id FROM wf.instance i WHERE i.object_id = @oid AND i.object_type = @t ORDER BY i.started_at DESC, i.id", conn, tx))
        {
            cmd.Parameters.AddWithValue("oid", obj.Id);
            cmd.Parameters.AddWithValue("t", objectType);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add(r.GetGuid(0));
        }
        var now = clock.GetUtcNow();
        InstanceView? active = null, last = null;
        var releases = new List<ReleaseRecord>();
        foreach (var id in ids)
        {
            var row = (await WorkflowStore.LoadInstanceAsync(conn, tx, id, forUpdate: false, ct))!;
            if (row.Status == InstanceStatus.Active && active is null) active = last = await BuildInstanceViewAsync(conn, tx, subject, row, obj, now, ct);
            else if (last is null) last = await BuildInstanceViewAsync(conn, tx, subject, row, obj, now, ct);
            if (row.Status == InstanceStatus.Approved)
                releases.Add(new ReleaseRecord(row.Id, row.ObjectRevision, row.IterationLabel, row.CompletedAt!.Value,
                    [.. (await WorkflowStore.LoadSignaturesAsync(conn, tx, row.Id, ct)).Select(ToView)]));
        }
        await audit.AppendAsync(tx, Entry(subject, "workflow.status", AuditOutcome.Success, objectType, objectKey, correlationId, ("instances", ids.Count.ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return Outcome<ObjectWorkflowStatus>.Ok(new ObjectWorkflowStatus(active, last, releases));
    }

    // ---------------------------------------------------------------- verification

    /// <summary>
    /// Recomputes every signature hash of an instance from the stored iteration and reports tampering. The content is read from
    /// the live tables; the markings and identity fields come from the signature record, so a change to the signed iteration,
    /// to the record, or to the object identity is flagged. Markings that changed after signing are reported but not tampering.
    /// </summary>
    public async Task<Outcome<IReadOnlyList<SignatureVerification>>> VerifyInstanceAsync(Subject subject, Guid instanceId, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var inst = await WorkflowStore.LoadInstanceAsync(conn, tx, instanceId, forUpdate: false, ct);
        if (inst is null) return Outcome<IReadOnlyList<SignatureVerification>>.NotFound();
        var gateway = GatewayFor(inst.ObjectType) ?? throw new InvalidOperationException($"No gateway for object type {inst.ObjectType}.");
        var obj = await gateway.FindByRevisionAsync(conn, tx, inst.RevisionId, forUpdate: false, ct);
        var read = obj is null ? null : AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Read);
        if (obj is null || read is not { Permitted: true })
        {
            await audit.AppendAsync(tx, Entry(subject, "workflow.verify", AuditOutcome.Denied, inst.ObjectType, inst.ObjectKey, correlationId,
                ("instance", inst.Id.ToString("D")), ("reasons", read is null ? "object.missing" : string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<IReadOnlyList<SignatureVerification>>.NotFound();
        }

        var results = new List<SignatureVerification>();
        foreach (var s in await WorkflowStore.LoadSignaturesAsync(conn, tx, inst.Id, ct))
        {
            var problems = new List<string>();
            var markingsChanged = false;
            var stored = await gateway.LoadIterationContentAsync(conn, tx, s.IterationId, ct);
            if (stored is null) problems.Add("The signed iteration no longer exists.");
            else
            {
                var signedMarkings = SignatureHasher.ParseMarkings(JsonNode.Parse(s.MarkingsJson)!);
                // Markings is a record over immutable sets (reference equality), so compare canonical forms.
                markingsChanged = CanonicalJson.Serialize(SignatureHasher.MarkingsJson(signedMarkings)) != CanonicalJson.Serialize(SignatureHasher.MarkingsJson(obj.Markings));
                var content = new SignedContent(stored.ObjectType, stored.ObjectKey, stored.Revision, stored.IterationId, stored.IterationLabel, stored.Description, stored.AttributesJson, signedMarkings);
                if (!SignatureHasher.ContentHash(content).AsSpan().SequenceEqual(s.ContentHash)) problems.Add("The signed iteration content (description or attributes) was changed after signing.");
                var statement = new SignedStatement(content, s.MeaningCode, s.Decision, s.SignerSub, s.Comment, s.SignedAt);
                if (!SignatureHasher.SignatureHash(statement).AsSpan().SequenceEqual(s.SignatureHash))
                    problems.Add("The signature record does not match its hash (identity, meaning, signer, comment or time was changed).");
                if (stored.ObjectKey != s.ObjectKey || stored.Revision != s.Revision || stored.IterationLabel != s.IterationLabel)
                    problems.Add("The object identity recorded in the signature differs from the stored iteration.");
            }
            results.Add(new SignatureVerification(s.Id, problems.Count == 0, problems, markingsChanged,
                SignatureHasher.Manifestation(s.SignerName, s.SignerUsername, s.SignedAt, s.MeaningLabel)));
        }
        await audit.AppendAsync(tx, Entry(subject, "workflow.verify", results.All(r => r.Valid) ? AuditOutcome.Success : AuditOutcome.Failure, inst.ObjectType, inst.ObjectKey, correlationId,
            ("instance", inst.Id.ToString("D")), ("signatures", results.Count.ToString(CultureInfo.InvariantCulture)),
            ("invalid", string.Join(",", results.Where(r => !r.Valid).Select(r => r.SignatureId.ToString("D"))))), ct);
        await tx.CommitAsync(ct);
        return Outcome<IReadOnlyList<SignatureVerification>>.Ok(results);
    }
}

internal static class UiBanner
{
    public const string Unclassified = "UNCLASSIFIED";
}
