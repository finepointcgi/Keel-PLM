using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Workflow;

/// <summary>
/// Review workflow commands and queries (ADR-0006). Like every module service: each call evaluates the access policy
/// (ADR-0004), writes its audit event in the same transaction as its change (ADR-0005), including denials, and
/// serializes per object. Lock order is always instance row first, then the object row, so concurrent signers,
/// cancellations and edits cannot deadlock.
/// </summary>
public sealed partial class WorkflowService(
    NpgsqlDataSource dataSource, IAuditLog audit, IEnumerable<IWorkflowObjectGateway> gateways, WorkflowOptions options, TimeProvider clock)
{
    public const int MaxCommentLength = 2000;

    private IWorkflowObjectGateway? GatewayFor(string objectType) => gateways.FirstOrDefault(g => g.ObjectType == objectType);

    // ---------------------------------------------------------------- start

    /// <summary>
    /// Submits the latest revision of an object for review: moves it to Under Review (locking it), binds the instance to the
    /// exact iteration, and creates the first tasks. The submitter picks participants from the user directory; each must exist
    /// there, hold the slot's role and be eligible to read the object under its markings (ASSUMPTION A50).
    /// </summary>
    public async Task<Outcome<InstanceView>> StartAsync(Subject subject, StartReviewCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        var gateway = GatewayFor(cmd.ObjectType);
        if (gateway is null) return Outcome<InstanceView>.Invalid([$"Objects of type '{cmd.ObjectType}' cannot be submitted for review."]);
        if ((cmd.Comment?.Length ?? 0) > MaxCommentLength) return Outcome<InstanceView>.Invalid([$"The comment must be at most {MaxCommentLength} characters."]);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var obj = await gateway.FindByKeyAsync(conn, tx, cmd.ObjectKey, forUpdate: true, ct);
        if (obj is null) return Outcome<InstanceView>.NotFound();

        var read = AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "workflow.start", AuditOutcome.Denied, cmd.ObjectType, cmd.ObjectKey, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<InstanceView>.NotFound();
        }
        var modify = AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Modify);
        if (!modify.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "workflow.start", AuditOutcome.Denied, cmd.ObjectType, cmd.ObjectKey, correlationId, ("reasons", string.Join(",", modify.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<InstanceView>.Forbidden(modify.Reasons);
        }

        var templates = (await WorkflowStore.LoadTemplatesAsync(conn, tx, activeOnly: true, ct)).Where(t => t.ObjectType == cmd.ObjectType).ToList();
        // 2.2: an administrator can pin the template an object type starts with (wf.template_pin); an explicit choice still wins.
        string? pin;
        await using (var pc = new NpgsqlCommand("SELECT template_id FROM wf.template_pin WHERE object_type = @t", conn, tx))
        {
            pc.Parameters.AddWithValue("t", cmd.ObjectType);
            pin = (string?)await pc.ExecuteScalarAsync(ct);
        }
        TemplateRow? row = cmd.TemplateId is null
            ? (pin is null ? null : templates.FirstOrDefault(t => t.Id == pin)) ?? (templates.Count == 1 ? templates[0] : null)
            : templates.FirstOrDefault(t => t.Id == cmd.TemplateId);
        if (row is null)
            return Outcome<InstanceView>.Invalid([cmd.TemplateId is null
                ? $"Choose a workflow template ({templates.Count} are available for {cmd.ObjectType} objects)."
                : $"There is no active workflow template '{cmd.TemplateId}' for {cmd.ObjectType} objects."]);
        var template = TemplateParser.Parse(row.Definition);
        if (!template.IsSuccess) throw new InvalidOperationException($"Stored template {row.Id} v{row.Version} is invalid: {string.Join("; ", template.Errors)}");

        // Move the object first: its state conflicts are the most useful message. Everything rolls back if participants are rejected.
        var now = SignatureHasher.Truncate(clock.GetUtcNow());
        var begin = await gateway.BeginReviewAsync(conn, tx, obj, subject, now, ct);
        if (!begin.IsOk) return Outcome<InstanceView>.Conflict(begin.Error!);

        var (participants, errors, ineligible) = await ResolveParticipantsAsync(conn, tx, subject, template.Value, cmd.Participants, obj.Markings, ct);
        if (errors.Count > 0)
        {
            await tx.RollbackAsync(ct);
            if (ineligible.Count > 0)
                await audit.AppendAsync(Entry(subject, "workflow.start", AuditOutcome.Denied, cmd.ObjectType, cmd.ObjectKey, correlationId, ("reasons", string.Join(";", ineligible))), ct);
            return Outcome<InstanceView>.Invalid(errors);
        }
        if (await WorkflowStore.HasActiveInstanceAsync(conn, tx, cmd.ObjectType, obj.Id, ct))
        {
            await tx.RollbackAsync(ct);
            return Outcome<InstanceView>.Conflict("This object already has an active review.");
        }

        var (state, advance) = WorkflowEngine.Start(template.Value, participants, now);
        var instance = new InstanceRow(Guid.CreateVersion7(), row.Id, row.Version, row.Definition, WorkflowStore.ParticipantsJson(participants), cmd.ObjectType, obj.Key, obj.Id,
            obj.RevisionId, obj.Revision, obj.IterationId, obj.IterationLabel, InstanceStatus.Active, string.IsNullOrWhiteSpace(cmd.Comment) ? null : cmd.Comment.Trim(),
            subject.Id, subject.Username ?? subject.Id, subject.DisplayName, now, null, null, null, obj.Markings);
        await WorkflowStore.InsertInstanceAsync(conn, tx, instance, ct);
        await WorkflowStore.SaveExecutionAsync(conn, tx, instance.Id, state, ct);

        await WorkflowStore.AppendEventAsync(conn, tx, instance.Id, now, subject.Id, subject.DisplayName, "Started",
            $"Submitted {obj.Key} revision {obj.Revision} ({obj.IterationLabel}) for review using {row.Name} v{row.Version}.", ct);
        await audit.AppendAsync(tx, Entry(subject, "workflow.start", AuditOutcome.Success, cmd.ObjectType, obj.Key, correlationId,
            ("instance", instance.Id.ToString("D")), ("template", $"{row.Id}@{row.Version}"), ("revision", obj.Revision), ("iteration", obj.IterationLabel),
            ("participants", string.Join(";", participants.Select(p => $"{p.Key}={string.Join(",", p.Value.Select(u => u.Username))}")))), ct);
        await RecordCreatedTasksAsync(conn, tx, subject, instance, advance.Created, correlationId, now, ct);

        // Show the object as it is now (Under Review), not as it was before the review began.
        var current = await gateway.FindByRevisionAsync(conn, tx, obj.RevisionId, forUpdate: false, ct) ?? obj;
        var view = await BuildInstanceViewAsync(conn, tx, subject, instance, current, now, ct);
        await tx.CommitAsync(ct);
        return Outcome<InstanceView>.Ok(view);
    }

    private static async Task<(Dictionary<string, IReadOnlyList<Participant>> Participants, List<string> Errors, List<string> Ineligible)> ResolveParticipantsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Subject submitter, WorkflowTemplate template, IReadOnlyDictionary<string, string[]> requested, Markings markings, CancellationToken ct)
    {
        var errors = new List<string>();
        var ineligible = new List<string>();
        var result = new Dictionary<string, IReadOnlyList<Participant>>(StringComparer.Ordinal);

        foreach (var unknown in requested.Keys.Where(k => template.Slots.All(s => s.Name != k)))
            errors.Add($"The template has no participant group '{unknown}'.");

        foreach (var slot in template.Slots)
        {
            var wanted = (requested.GetValueOrDefault(slot.Name) ?? []).Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()).ToList();
            if (wanted.Count < slot.Min || wanted.Count > slot.Max)
            {
                errors.Add(slot.Min == slot.Max
                    ? $"{slot.Label}: choose exactly {slot.Min}."
                    : $"{slot.Label}: choose between {slot.Min} and {slot.Max}.");
                continue;
            }
            var picked = new List<Participant>();
            foreach (var key in wanted)
            {
                var user = await UserDirectory.FindAsync(conn, tx, key, ct);
                if (user is null)
                {
                    errors.Add($"{slot.Label}: '{key}' is not in the user directory (users appear there after they sign in).");
                    continue;
                }
                if (picked.Any(p => p.Sub == user.Sub))
                {
                    errors.Add($"{slot.Label}: {user.Username} was chosen twice.");
                    continue;
                }
                // ASSUMPTION(A51): separation of duties. The submitter can never review or approve their own submission.
                if (user.Sub == submitter.Id)
                {
                    errors.Add($"{slot.Label}: you cannot review or approve your own submission ({user.Username}).");
                    ineligible.Add($"{user.Username}:sod.submitter");
                    continue;
                }
                if (!WorkflowRoles.Satisfies(user.Roles, slot.Role))
                {
                    errors.Add($"{slot.Label}: {user.Username} does not hold the {slot.Role} role.");
                    ineligible.Add($"{user.Username}:role.missing");
                    continue;
                }
                var eligibility = AccessPolicy.Evaluate(user.ToSubject(), markings, PlmAction.Read);
                if (!eligibility.Permitted)
                {
                    errors.Add($"{slot.Label}: {user.Username} is not eligible to see this object ({string.Join(", ", eligibility.Reasons)}).");
                    ineligible.Add($"{user.Username}:{string.Join("|", eligibility.Reasons)}");
                    continue;
                }
                picked.Add(new Participant(user.Sub, user.Username, user.DisplayName));
            }
            result[slot.Name] = picked;
        }
        return (result, errors, ineligible);
    }

    private async Task RecordCreatedTasksAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject actor, InstanceRow instance, IReadOnlyList<TaskState> created, string? correlationId, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var t in created)
        {
            var who = t.AssigneeName is null ? $"anyone holding {t.RequiredRole}" : $"{t.AssigneeName} ({t.AssigneeUsername})";
            await WorkflowStore.AppendEventAsync(conn, tx, instance.Id, now, actor.Id, actor.DisplayName, "Task assigned", $"{t.Name}: {who}.", ct);
            await audit.AppendAsync(tx, Entry(actor, "workflow.task_assign", AuditOutcome.Success, instance.ObjectType, instance.ObjectKey, correlationId,
                ("instance", instance.Id.ToString("D")), ("task", t.Id.ToString("D")), ("name", t.Name), ("assignee", t.AssigneeUsername ?? ("role:" + t.RequiredRole))), ct);
        }
    }

    // ---------------------------------------------------------------- Phase 2 (explicitly not built)

    /// <summary>Delegation (an out-of-office proxy) is Phase 2.</summary>
    public Task<Outcome<InstanceView>> DelegateTaskAsync(Subject subject, Guid taskId, string toUser, string? correlationId, CancellationToken ct = default) =>
        throw new NotSupportedException("Task delegation (an out-of-office proxy with an audit trail) is delivered in Phase 2 (ADR-0006). Administrators can reassign open tasks meanwhile.");

    /// <summary>Escalation timers need the background scheduler (SKIP LOCKED), which is Phase 2.</summary>
    public Task ScheduleEscalationAsync(Guid instanceId, TimeSpan after, CancellationToken ct = default) =>
        throw new NotSupportedException("Escalation timers are delivered in Phase 2 (ADR-0006). Due dates are recorded and shown, but nothing escalates yet.");

    // ---------------------------------------------------------------- shared helpers

    internal static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string objectType, string? objectId, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s),
        Action = action,
        Outcome = outcome,
        ObjectType = objectType,
        ObjectId = objectId,
        CorrelationId = correlationId,
        Details = AuditDetails.Of(details),
    };

    internal static ObjectSummary Summarize(InstanceRow inst, WorkflowObject obj) =>
        new(inst.ObjectType, inst.ObjectKey, obj.Name, inst.ObjectRevision, inst.IterationLabel, obj.StateLabel, obj.Markings.BannerText(), obj.Markings);

    private static string TemplateNameOf(InstanceRow inst) => JsonNode.Parse(inst.Definition)?["name"]?.GetValue<string>() ?? inst.TemplateId;

    private static SignatureView ToView(SignatureRow s) => new(
        s.Id, s.TaskId, s.InstanceId, s.SignerSub, s.SignerUsername, s.SignerName, s.SignerOrg, s.MeaningCode, s.MeaningLabel, s.Decision, s.Comment, s.SignedAt,
        s.ObjectKey, s.Revision, s.IterationId, s.IterationLabel, s.MarkingBanner, Convert.ToHexStringLower(s.ContentHash), Convert.ToHexStringLower(s.SignatureHash),
        SignatureHasher.Manifestation(s.SignerName, s.SignerUsername, s.SignedAt, s.MeaningLabel));

    private static TaskItem ToItem(TaskState t, DateTimeOffset now) => new(
        t.Id, t.Name, t.Kind, t.Status.ToString(), t.Decision, t.AssigneeSub, t.AssigneeUsername, t.AssigneeName, t.RequiredRole, t.DueAt,
        t.Status == WorkTaskStatus.Open && t.DueAt is { } due && due < now, t.CreatedAt, t.CompletedAt, t.SignatureId);

    /// <summary>Full instance view for a caller who is already known to be able to read the object.</summary>
    private static async Task<InstanceView> BuildInstanceViewAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, InstanceRow inst, WorkflowObject obj, DateTimeOffset now, CancellationToken ct)
    {
        var tasks = await WorkflowStore.LoadTasksAsync(conn, tx, inst.Id, ct);
        var signatures = await WorkflowStore.LoadSignaturesAsync(conn, tx, inst.Id, ct);
        var events = await WorkflowStore.LoadEventsAsync(conn, tx, inst.Id, ct);
        var active = inst.Status == InstanceStatus.Active;
        var isAdmin = AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Administer).Permitted;
        return new InstanceView(
            inst.Id, inst.TemplateId, inst.TemplateVersion, TemplateNameOf(inst), inst.Status, Summarize(inst, obj),
            inst.StartedByName, inst.StartedByUsername, inst.StartedAt, inst.CompletedAt, inst.CompletionNote, inst.Comment,
            [.. tasks.Select(t => ToItem(t, now))], [.. signatures.Select(ToView)],
            [.. signatures.Where(s => s.Decision == "reject").Select(s => new RejectionView(s.SignerName, s.SignerUsername, s.MeaningLabel, s.Comment, s.SignedAt))],
            [.. events.Select(e => new EventView(e.At, e.ActorName, e.Action, e.Detail))],
            active && (inst.StartedBy == subject.Id || isAdmin), active && isAdmin, obj.Markings.BannerText());
    }

    internal static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
