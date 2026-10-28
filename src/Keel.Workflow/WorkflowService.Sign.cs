using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Workflow;

public sealed partial class WorkflowService
{
    /// <summary>The refusal reasons exposed to the caller, and the (more detailed) ones written to the audit log.</summary>
    private sealed record SignCheck(OutcomeKind Kind, string[] Reasons, string[] AuditReasons)
    {
        public static readonly SignCheck Allowed = new(OutcomeKind.Ok, [], []);
        public bool IsAllowed => Kind == OutcomeKind.Ok;
    }

    private static SignCheck Refuse(OutcomeKind kind, string reason) => new(kind, [reason], [reason]);

    /// <summary>
    /// Whether <paramref name="subject"/> may sign <paramref name="task"/> right now, from the live token attributes. Used by
    /// <see cref="SignAsync"/> and, so the UI can explain a disabled button, by the task view. Eligibility is therefore checked
    /// twice in the life of a task: when the participant is picked (directory snapshot) and here (live token, A50).
    /// </summary>
    private static SignCheck CheckCanSign(Subject subject, InstanceRow inst, TaskState task, WorkflowObject obj)
    {
        if (inst.Status != InstanceStatus.Active) return Refuse(OutcomeKind.Conflict, "instance.not_active");
        if (task.Status != WorkTaskStatus.Open) return Refuse(OutcomeKind.Conflict, "task.not_open");
        if (task.AssigneeSub is not null && task.AssigneeSub != subject.Id) return Refuse(OutcomeKind.Forbidden, "task.not_assignee");
        // ASSUMPTION(A51): the submitter cannot sign as reviewer or approver of their own submission.
        if (subject.Id == inst.StartedBy) return Refuse(OutcomeKind.Forbidden, "sod.submitter");
        if (!WorkflowRoles.Satisfies(subject.Roles, task.RequiredRole)) return Refuse(OutcomeKind.Forbidden, AccessPolicy.RoleMissing);
        var read = AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Read);
        if (!read.Permitted) return new SignCheck(OutcomeKind.Forbidden, ["task.not_eligible"], [.. read.Reasons]);
        if (!obj.InReview || obj.IterationId != inst.IterationId || obj.RevisionId != inst.RevisionId) return Refuse(OutcomeKind.Conflict, "object.state_changed");
        return SignCheck.Allowed;
    }

    private static string ConflictMessage(string code) => code switch
    {
        "instance.not_active" => "This workflow is no longer active.",
        "task.not_open" => "This task has already been completed or skipped.",
        "object.state_changed" => "The object is no longer under review at the iteration this workflow covers; ask an administrator to cancel the workflow.",
        _ => code,
    };

    /// <summary>
    /// Completes a task with an electronic signature (A9, ADR-0007): checks the live token, separation of duties and
    /// recent authentication, writes the immutable signature with its content hash, advances the workflow and, when the
    /// last decision is in, releases or returns the object, all in one transaction.
    /// </summary>
    public async Task<Outcome<InstanceView>> SignAsync(Subject subject, Guid taskId, string reasonCode, string? comment, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var instanceId = await WorkflowStore.InstanceIdOfTaskAsync(conn, tx, taskId, ct);
        if (instanceId is null) return Outcome<InstanceView>.NotFound();
        // Lock the instance first, then the object (the order every workflow mutation uses). Concurrent signers queue here.
        var inst = (await WorkflowStore.LoadInstanceAsync(conn, tx, instanceId.Value, forUpdate: true, ct))!;
        var gateway = GatewayFor(inst.ObjectType) ?? throw new InvalidOperationException($"No gateway for object type {inst.ObjectType}.");
        var obj = await gateway.FindByRevisionAsync(conn, tx, inst.RevisionId, forUpdate: true, ct) ?? throw new InvalidOperationException("The reviewed object no longer exists.");
        var exec = await WorkflowStore.LoadExecutionAsync(conn, tx, inst.Id, ct);
        var task = exec.Tasks.Single(t => t.Id == taskId);

        var check = CheckCanSign(subject, inst, task, obj);
        if (!check.IsAllowed)
        {
            await audit.AppendAsync(tx, Entry(subject, "workflow.sign", AuditOutcome.Denied, inst.ObjectType, inst.ObjectKey, correlationId,
                ("instance", inst.Id.ToString("D")), ("task", taskId.ToString("D")), ("reasons", string.Join(",", check.AuditReasons))), ct);
            await tx.CommitAsync(ct);
            return check.Kind == OutcomeKind.Conflict ? Outcome<InstanceView>.Conflict(ConflictMessage(check.Reasons[0])) : Outcome<InstanceView>.Forbidden(check.Reasons);
        }

        var codes = await WorkflowStore.LoadReasonCodesAsync(conn, tx, ct);
        var code = codes.FirstOrDefault(c => c.Code == reasonCode && c.AppliesTo.Contains(task.Kind));
        var trimmed = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        var problems = new List<string>();
        if (code is null) problems.Add($"'{reasonCode}' is not a valid signature meaning for a {task.Kind} task.");
        else if (code.RequiresComment && trimmed is null) problems.Add($"A comment is required to sign as '{code.Label}'.");
        if ((trimmed?.Length ?? 0) > MaxCommentLength) problems.Add($"The comment must be at most {MaxCommentLength} characters.");
        if (problems.Count > 0) return Outcome<InstanceView>.Invalid(problems);

        var now = SignatureHasher.Truncate(clock.GetUtcNow());
        if (!StepUp.IsFresh(subject, now, options.StepUpMaxAge))
        {
            await audit.AppendAsync(tx, Entry(subject, "workflow.sign", AuditOutcome.Denied, inst.ObjectType, inst.ObjectKey, correlationId,
                ("instance", inst.Id.ToString("D")), ("task", taskId.ToString("D")), ("reasons", "stepup.required"),
                ("authTime", subject.AuthTime is { } a ? Iso(a) : null)), ct);
            await tx.CommitAsync(ct);
            return Outcome<InstanceView>.StepUpRequired("stepup.required", options.StepUpMaxAgeSeconds);
        }

        // The signature covers the stored iteration and the markings in force now (locked while under review).
        var stored = await gateway.LoadIterationContentAsync(conn, tx, inst.IterationId, ct) ?? throw new InvalidOperationException("The bound iteration no longer exists.");
        var content = new SignedContent(stored.ObjectType, stored.ObjectKey, stored.Revision, stored.IterationId, stored.IterationLabel, stored.Description, stored.AttributesJson, obj.Markings);
        var signatureId = Guid.CreateVersion7();
        var statement = new SignedStatement(content, code!.Code, code.Decision, subject.Id, trimmed, now);
        var contentHash = SignatureHasher.ContentHash(content);
        var signatureHash = SignatureHasher.SignatureHash(statement);
        var username = subject.Username ?? subject.Id;
        await WorkflowStore.InsertSignatureAsync(conn, tx, new SignatureRow(signatureId, inst.Id, taskId, subject.Id, username, subject.DisplayName, subject.Org, code.Code, code.Label,
            code.Decision, trimmed, now, subject.AuthTime, stored.ObjectType, stored.ObjectKey, stored.Revision, stored.IterationId, stored.IterationLabel,
            obj.Markings.BannerText(), SignatureHasher.MarkingsJson(obj.Markings).ToJsonString(), contentHash, signatureHash), ct);

        // A pool task is claimed by whoever signs it.
        if (task.AssigneeSub is null)
        {
            (task.AssigneeSub, task.AssigneeUsername, task.AssigneeName) = (subject.Id, username, subject.DisplayName);
            task.IsDirty = true;
        }
        var template = TemplateParser.Parse(inst.Definition);
        if (!template.IsSuccess) throw new InvalidOperationException($"Stored instance definition is invalid: {string.Join("; ", template.Errors)}");
        var advance = WorkflowEngine.Complete(template.Value, exec, WorkflowStore.ParseParticipants(inst.Participants), taskId, code.Decision == "approve", signatureId, now);
        await WorkflowStore.SaveExecutionAsync(conn, tx, inst.Id, exec, ct);

        var hashHex = Convert.ToHexStringLower(signatureHash);
        await WorkflowStore.AppendEventAsync(conn, tx, inst.Id, now, subject.Id, subject.DisplayName, "Signed",
            $"{task.Name}: {code.Label}{(trimmed is null ? "" : $" — {trimmed}")}", ct);
        await audit.AppendAsync(tx, Entry(subject, "workflow.sign", AuditOutcome.Success, inst.ObjectType, inst.ObjectKey, correlationId,
            ("instance", inst.Id.ToString("D")), ("task", taskId.ToString("D")), ("signature", signatureId.ToString("D")), ("meaning", code.Code),
            ("decision", code.Decision), ("revision", stored.Revision), ("iteration", stored.IterationLabel), ("iterationId", stored.IterationId.ToString("D")),
            ("hash", hashHex), ("contentHash", Convert.ToHexStringLower(contentHash)), ("authTime", subject.AuthTime is { } at ? Iso(at) : null)), ct);
        foreach (var skipped in advance.Skipped)
        {
            await WorkflowStore.AppendEventAsync(conn, tx, inst.Id, now, subject.Id, subject.DisplayName, "Task skipped", $"{skipped.Name}: {skipped.AssigneeName ?? skipped.RequiredRole} (decision already reached).", ct);
            await audit.AppendAsync(tx, Entry(subject, "workflow.task_skip", AuditOutcome.Success, inst.ObjectType, inst.ObjectKey, correlationId,
                ("instance", inst.Id.ToString("D")), ("task", skipped.Id.ToString("D")), ("reason", "vote.decided")), ct);
        }
        await RecordCreatedTasksAsync(conn, tx, subject, inst, advance.Created, correlationId, now, ct);

        var finalInstance = inst;
        if (advance.Outcome is { } outcome)
        {
            var released = outcome == InstanceOutcome.Approved;
            var step = released
                ? await gateway.CompleteReleaseAsync(conn, tx, obj, inst.IterationId, subject, now, ct)
                : await gateway.CompleteRejectionAsync(conn, tx, obj, subject, now, ct); // 1.6: an ECR has a distinct Rejected state
            if (!step.IsOk)
            {
                await tx.RollbackAsync(ct);
                await audit.AppendAsync(Entry(subject, "workflow.sign", AuditOutcome.Failure, inst.ObjectType, inst.ObjectKey, correlationId,
                    ("instance", inst.Id.ToString("D")), ("task", taskId.ToString("D")), ("reasons", "object.state_changed"), ("detail", step.Error)), ct);
                return Outcome<InstanceView>.Conflict(step.Error!);
            }
            var note = released
                ? "All required approvals received; released."
                : $"Rejected by {subject.DisplayName}: {trimmed}";
            await WorkflowStore.CompleteInstanceAsync(conn, tx, inst.Id, released ? InstanceStatus.Approved : InstanceStatus.Rejected, now, subject.Id, note, ct);
            await WorkflowStore.AppendEventAsync(conn, tx, inst.Id, now, subject.Id, subject.DisplayName, released ? "Released" : "Rejected", note, ct);
            await audit.AppendAsync(tx, Entry(subject, "workflow.complete", AuditOutcome.Success, inst.ObjectType, inst.ObjectKey, correlationId,
                ("instance", inst.Id.ToString("D")), ("outcome", outcome.ToString()), ("revision", inst.ObjectRevision), ("iteration", inst.IterationLabel),
                ("objectState", released ? "released" : "in_work")), ct);
            finalInstance = (await WorkflowStore.LoadInstanceAsync(conn, tx, inst.Id, forUpdate: false, ct))!;
        }

        var refreshed = await gateway.FindByRevisionAsync(conn, tx, inst.RevisionId, forUpdate: false, ct) ?? obj;
        var view = await BuildInstanceViewAsync(conn, tx, subject, finalInstance, refreshed, now, ct);
        await tx.CommitAsync(ct);
        return Outcome<InstanceView>.Ok(view);
    }
}
