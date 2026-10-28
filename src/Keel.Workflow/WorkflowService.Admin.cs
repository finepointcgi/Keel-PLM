using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Workflow;

public sealed partial class WorkflowService
{
    // ---------------------------------------------------------------- cancel

    /// <summary>
    /// Cancels an active workflow and returns the object to work. Only the person who started it, or an administrator,
    /// may do so, and a reason is required. If the object was moved out of review by an administrator's override, the
    /// workflow is still closed (that is the way to clear such a desynchronized instance) and the object is left as it is.
    /// </summary>
    public async Task<Outcome<InstanceView>> CancelAsync(Subject subject, Guid instanceId, string? reason, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var inst = await WorkflowStore.LoadInstanceAsync(conn, tx, instanceId, forUpdate: true, ct);
        if (inst is null) return Outcome<InstanceView>.NotFound();
        var gateway = GatewayFor(inst.ObjectType) ?? throw new InvalidOperationException($"No gateway for object type {inst.ObjectType}.");
        var obj = await gateway.FindByRevisionAsync(conn, tx, inst.RevisionId, forUpdate: true, ct) ?? throw new InvalidOperationException("The reviewed object no longer exists.");

        var read = AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "workflow.cancel", AuditOutcome.Denied, inst.ObjectType, inst.ObjectKey, correlationId,
                ("instance", inst.Id.ToString("D")), ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<InstanceView>.NotFound();
        }
        var isAdmin = AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Administer).Permitted;
        if (inst.StartedBy != subject.Id && !isAdmin)
        {
            await audit.AppendAsync(tx, Entry(subject, "workflow.cancel", AuditOutcome.Denied, inst.ObjectType, inst.ObjectKey, correlationId,
                ("instance", inst.Id.ToString("D")), ("reasons", "workflow.cancel_not_starter_or_admin")), ct);
            await tx.CommitAsync(ct);
            return Outcome<InstanceView>.Forbidden(["workflow.cancel_not_starter_or_admin"]);
        }
        if (inst.Status != InstanceStatus.Active) return Outcome<InstanceView>.Conflict("This workflow is no longer active.");
        if (string.IsNullOrWhiteSpace(reason)) return Outcome<InstanceView>.Invalid(["A reason is required to cancel a workflow."]);
        if (reason.Length > MaxCommentLength) return Outcome<InstanceView>.Invalid([$"The reason must be at most {MaxCommentLength} characters."]);

        var now = SignatureHasher.Truncate(clock.GetUtcNow());
        var exec = await WorkflowStore.LoadExecutionAsync(conn, tx, inst.Id, ct);
        foreach (var t in exec.Tasks.Where(t => t.Status == WorkTaskStatus.Open))
        {
            t.Status = WorkTaskStatus.Cancelled;
            t.CompletedAt = now;
            t.IsDirty = true;
        }
        foreach (var token in exec.Tokens.Where(t => t.Status == TokenStatus.Active))
        {
            token.Status = TokenStatus.Skipped;
            token.CompletedAt = now;
            token.IsDirty = true;
        }
        await WorkflowStore.SaveExecutionAsync(conn, tx, inst.Id, exec, ct);

        var returned = false;
        if (obj.InReview)
        {
            var step = await gateway.ReturnToInWorkAsync(conn, tx, obj, subject, now, ct);
            if (!step.IsOk)
            {
                await tx.RollbackAsync(ct);
                return Outcome<InstanceView>.Conflict(step.Error!);
            }
            returned = true;
        }
        var note = $"Cancelled by {subject.DisplayName}: {reason.Trim()}";
        await WorkflowStore.CompleteInstanceAsync(conn, tx, inst.Id, InstanceStatus.Cancelled, now, subject.Id, note, ct);
        await WorkflowStore.AppendEventAsync(conn, tx, inst.Id, now, subject.Id, subject.DisplayName, "Cancelled", note, ct);
        await audit.AppendAsync(tx, Entry(subject, "workflow.cancel", AuditOutcome.Success, inst.ObjectType, inst.ObjectKey, correlationId,
            ("instance", inst.Id.ToString("D")), ("reason", reason.Trim()), ("byAdmin", (inst.StartedBy != subject.Id).ToString()), ("returnedToWork", returned.ToString())), ct);

        var finalInstance = (await WorkflowStore.LoadInstanceAsync(conn, tx, inst.Id, forUpdate: false, ct))!;
        var refreshed = await gateway.FindByRevisionAsync(conn, tx, inst.RevisionId, forUpdate: false, ct) ?? obj;
        var view = await BuildInstanceViewAsync(conn, tx, subject, finalInstance, refreshed, now, ct);
        await tx.CommitAsync(ct);
        return Outcome<InstanceView>.Ok(view);
    }

    // ---------------------------------------------------------------- reassign

    /// <summary>
    /// An administrator moves an open task to another eligible user, with a reason (audited). The new assignee must be in the
    /// directory, hold the task's role, be eligible to read the object, not be the submitter, and not already hold another
    /// task of the same step.
    /// </summary>
    public async Task<Outcome<InstanceView>> ReassignTaskAsync(Subject subject, Guid taskId, string assignee, string? reason, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var instanceId = await WorkflowStore.InstanceIdOfTaskAsync(conn, tx, taskId, ct);
        if (instanceId is null) return Outcome<InstanceView>.NotFound();
        var inst = (await WorkflowStore.LoadInstanceAsync(conn, tx, instanceId.Value, forUpdate: true, ct))!;
        var gateway = GatewayFor(inst.ObjectType) ?? throw new InvalidOperationException($"No gateway for object type {inst.ObjectType}.");
        var obj = await gateway.FindByRevisionAsync(conn, tx, inst.RevisionId, forUpdate: false, ct) ?? throw new InvalidOperationException("The reviewed object no longer exists.");

        var read = AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "workflow.reassign", AuditOutcome.Denied, inst.ObjectType, inst.ObjectKey, correlationId,
                ("instance", inst.Id.ToString("D")), ("task", taskId.ToString("D")), ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<InstanceView>.NotFound();
        }
        var admin = AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Administer);
        if (!admin.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "workflow.reassign", AuditOutcome.Denied, inst.ObjectType, inst.ObjectKey, correlationId,
                ("instance", inst.Id.ToString("D")), ("task", taskId.ToString("D")), ("reasons", string.Join(",", admin.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<InstanceView>.Forbidden(admin.Reasons);
        }
        var exec = await WorkflowStore.LoadExecutionAsync(conn, tx, inst.Id, ct);
        var task = exec.Tasks.Single(t => t.Id == taskId);
        if (inst.Status != InstanceStatus.Active || task.Status != WorkTaskStatus.Open) return Outcome<InstanceView>.Conflict("Only open tasks of an active workflow can be reassigned.");
        if (string.IsNullOrWhiteSpace(reason)) return Outcome<InstanceView>.Invalid(["A reason is required to reassign a task."]);
        if (reason.Length > MaxCommentLength) return Outcome<InstanceView>.Invalid([$"The reason must be at most {MaxCommentLength} characters."]);

        var target = await UserDirectory.FindAsync(conn, tx, assignee.Trim(), ct);
        var problems = new List<string>();
        if (target is null) problems.Add($"'{assignee}' is not in the user directory.");
        else
        {
            if (target.Sub == inst.StartedBy) problems.Add($"{target.Username} submitted this review and cannot take part in it.");
            if (!WorkflowRoles.Satisfies(target.Roles, task.RequiredRole)) problems.Add($"{target.Username} does not hold the {task.RequiredRole} role.");
            var eligibility = AccessPolicy.Evaluate(target.ToSubject(), obj.Markings, PlmAction.Read);
            if (!eligibility.Permitted) problems.Add($"{target.Username} is not eligible to see this object ({string.Join(", ", eligibility.Reasons)}).");
            if (exec.Tasks.Any(t => t.Id != task.Id && t.NodeId == task.NodeId && t.AssigneeSub == target.Sub && t.Status is WorkTaskStatus.Open or WorkTaskStatus.Completed))
                problems.Add($"{target.Username} already has a task in this step.");
        }
        if (problems.Count > 0)
        {
            await audit.AppendAsync(tx, Entry(subject, "workflow.reassign", AuditOutcome.Denied, inst.ObjectType, inst.ObjectKey, correlationId,
                ("instance", inst.Id.ToString("D")), ("task", taskId.ToString("D")), ("reasons", string.Join(";", problems))), ct);
            await tx.CommitAsync(ct);
            return Outcome<InstanceView>.Invalid(problems);
        }

        var now = SignatureHasher.Truncate(clock.GetUtcNow());
        var previous = task.AssigneeUsername ?? "the role pool";
        (task.AssigneeSub, task.AssigneeUsername, task.AssigneeName) = (target!.Sub, target.Username, target.DisplayName);
        task.IsDirty = true;
        await WorkflowStore.SaveExecutionAsync(conn, tx, inst.Id, exec, ct);
        await WorkflowStore.AppendEventAsync(conn, tx, inst.Id, now, subject.Id, subject.DisplayName, "Task reassigned",
            $"{task.Name}: from {previous} to {target.DisplayName} ({target.Username}). Reason: {reason.Trim()}", ct);
        await audit.AppendAsync(tx, Entry(subject, "workflow.reassign", AuditOutcome.Success, inst.ObjectType, inst.ObjectKey, correlationId,
            ("instance", inst.Id.ToString("D")), ("task", taskId.ToString("D")), ("from", previous), ("to", target.Username), ("reason", reason.Trim())), ct);

        var view = await BuildInstanceViewAsync(conn, tx, subject, inst, obj, now, ct);
        await tx.CommitAsync(ct);
        return Outcome<InstanceView>.Ok(view);
    }

    // ---------------------------------------------------------------- templates

    /// <summary>Active templates (or every version) for display. Read-only for everyone with a PLM role; the admin UI is Phase 2.</summary>
    public async Task<IReadOnlyList<TemplateView>> ListTemplatesAsync(Subject subject, bool activeOnly, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await WorkflowStore.LoadTemplatesAsync(conn, null, activeOnly, ct);
        await audit.AppendAsync(Entry(subject, "workflow.templates_list", AuditOutcome.Success, "workflow_template", null, correlationId, ("returned", rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))), ct);
        return [.. rows.Select(ToView)];
    }

    internal static TemplateView ToView(TemplateRow r)
    {
        var parsed = TemplateParser.Parse(r.Definition);
        var slots = parsed.IsSuccess ? parsed.Value.Slots.Select(s => new SlotView(s.Name, s.Label, s.Role, s.Min, s.Max)).ToList() : [];
        return new TemplateView(r.Id, r.Version, r.Name, r.Description, r.ObjectType, r.IsActive, slots, r.CreatedAt, r.CreatedBy, JsonNode.Parse(r.Definition)!);
    }

    /// <summary>
    /// Administrator upload: validates the definition and stores it as the next version of its template id, making it the active
    /// one. Running instances keep the definition they started with.
    /// </summary>
    public async Task<Outcome<TemplateView>> UploadTemplateAsync(Subject subject, string definitionJson, string? correlationId, CancellationToken ct = default)
    {
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Administer))
        {
            await audit.AppendAsync(Entry(subject, "workflow.template_upload", AuditOutcome.Denied, "workflow_template", null, correlationId, ("reasons", AccessPolicy.RoleMissing)), ct);
            return Outcome<TemplateView>.Forbidden([AccessPolicy.RoleMissing]);
        }
        var parsed = TemplateParser.Parse(definitionJson);
        if (!parsed.IsSuccess) return Outcome<TemplateView>.Invalid(parsed.Errors);
        var template = parsed.Value;
        var errors = new List<string>();
        if (GatewayFor(template.ObjectType) is null) errors.Add($"No workflow can run on objects of type '{template.ObjectType}'.");
        if (template.Name.Length > 200) errors.Add("The template name must be at most 200 characters.");
        if (errors.Count > 0) return Outcome<TemplateView>.Invalid(errors);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@k))", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("k", "wf.template:" + template.Id);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }
        int version;
        await using (var cmd = new NpgsqlCommand("SELECT COALESCE(max(version), 0) + 1 FROM wf.template WHERE id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", template.Id);
            version = (int)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await using (var deactivate = new NpgsqlCommand("UPDATE wf.template SET is_active = false WHERE id = @id AND is_active", conn, tx))
        {
            deactivate.Parameters.AddWithValue("id", template.Id);
            await deactivate.ExecuteNonQueryAsync(ct);
        }
        var now = clock.GetUtcNow();
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO wf.template (id, version, name, description, object_type, definition, is_active, created_at, created_by) VALUES (@id, @v, @name, @desc, @ot, @def::jsonb, true, @at, @by)", conn, tx))
        {
            insert.Parameters.AddWithValue("id", template.Id);
            insert.Parameters.AddWithValue("v", version);
            insert.Parameters.AddWithValue("name", template.Name);
            insert.Parameters.AddWithValue("desc", (object?)template.Description ?? DBNull.Value);
            insert.Parameters.AddWithValue("ot", template.ObjectType);
            insert.Parameters.AddWithValue("def", definitionJson);
            insert.Parameters.AddWithValue("at", now);
            insert.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "workflow.template_upload", AuditOutcome.Success, "workflow_template", template.Id, correlationId,
            ("version", version.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("objectType", template.ObjectType), ("name", template.Name)), ct);
        var stored = (await WorkflowStore.LoadTemplateVersionAsync(conn, tx, template.Id, version, ct))!;
        await tx.CommitAsync(ct);
        return Outcome<TemplateView>.Ok(ToView(stored));
    }

    // ---------------------------------------------------------------- user directory search

    /// <summary>
    /// Directory search for participant pickers. Callers get usernames, display names and roles only. When an object is given,
    /// the caller must be able to read it, and only users who hold the role and are eligible to read it are returned; the caller
    /// is excluded (separation of duties).
    /// </summary>
    public async Task<Outcome<IReadOnlyList<DirectoryUserView>>> SearchUsersAsync(
        Subject subject, string? text, string? role, string? objectType, string? objectKey, int limit, string? correlationId, CancellationToken ct = default)
    {
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Read))
        {
            await audit.AppendAsync(Entry(subject, "workflow.users_search", AuditOutcome.Denied, "user_directory", null, correlationId, ("reasons", AccessPolicy.RoleMissing)), ct);
            return Outcome<IReadOnlyList<DirectoryUserView>>.Forbidden([AccessPolicy.RoleMissing]);
        }
        limit = Math.Clamp(limit, 1, 100);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        WorkflowObject? obj = null;
        if (objectType is not null || objectKey is not null)
        {
            var gateway = objectType is null ? null : GatewayFor(objectType);
            obj = gateway is null || objectKey is null ? null : await gateway.FindByKeyAsync(conn, tx, objectKey, forUpdate: false, ct);
            var read = obj is null ? null : AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Read);
            if (obj is null || read is not { Permitted: true })
            {
                if (read is not null)
                    await audit.AppendAsync(tx, Entry(subject, "workflow.users_search", AuditOutcome.Denied, objectType!, objectKey, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
                await tx.CommitAsync(ct);
                return Outcome<IReadOnlyList<DirectoryUserView>>.NotFound();
            }
        }
        var found = await UserDirectory.SearchAsync(conn, tx, text, 500, ct);
        IEnumerable<DirectoryUser> users = found;
        if (role is not null) users = users.Where(u => WorkflowRoles.Satisfies(u.Roles, role));
        if (obj is not null) users = users.Where(u => u.Sub != subject.Id && AccessPolicy.Evaluate(u.ToSubject(), obj.Markings, PlmAction.Read).Permitted);
        var result = users.Take(limit).Select(u => u.ToView()).ToList();
        await audit.AppendAsync(tx, Entry(subject, "workflow.users_search", AuditOutcome.Success, obj?.ObjectType ?? "user_directory", obj?.Key, correlationId,
            ("query", text), ("role", role), ("returned", result.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return Outcome<IReadOnlyList<DirectoryUserView>>.Ok(result);
    }
}
