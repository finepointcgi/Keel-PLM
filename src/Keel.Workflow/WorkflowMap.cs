using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;

namespace Keel.Workflow;

/// <summary>A participant on the progress map. <see cref="Redacted"/>: the caller may not read the object, so the map shows "Participant" and no initials.</summary>
public sealed record MapPerson(string Name, string Initials, bool Redacted);

/// <summary>
/// One task (or one not yet created task of a later step) on the map. <see cref="Status"/> is Pending, Signed, Rejected, Skipped, Cancelled or Waiting
/// (the step has not started). <see cref="Meaning"/> is the signature meaning of a signed or rejected task.
/// </summary>
public sealed record MapTask(
    Guid? TaskId, string Name, string Kind, string Status, string? Meaning, MapPerson? Person, string? PoolRole, DateTimeOffset? At, DateTimeOffset? DueAt, bool Overdue);

/// <summary>
/// A step of the workflow in order. <see cref="Type"/> is Task (one task), Parallel (a group with a vote rule: <see cref="Rule"/> reads All, Any or "2 of 3")
/// or End. <see cref="State"/> is Waiting, Active, Approved, Rejected or Skipped.
/// </summary>
public sealed record MapStage(string NodeId, string Name, string Type, string? Rule, int Required, int Total, string State, IReadOnlyList<MapTask> Tasks);

public sealed record WorkflowMap(Guid InstanceId, string TemplateName, string Status, bool Redacted, IReadOnlyList<MapStage> Stages, string Banner);

public sealed partial class WorkflowService
{
    /// <summary>
    /// The progress map of one workflow instance, built from the same stored state as <see cref="GetInstanceAsync"/> (tasks, signatures) and the definition the
    /// instance runs. A caller who may read the object sees everything. A caller who only holds a task of the instance, and may no longer read the object, gets
    /// the steps and their statuses with participants hidden as "Participant" and no signature meanings or times. Anyone else gets NotFound.
    /// </summary>
    public async Task<Outcome<WorkflowMap>> GetMapAsync(Subject subject, Guid instanceId, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var inst = await WorkflowStore.LoadInstanceAsync(conn, tx, instanceId, forUpdate: false, ct);
        if (inst is null) return Outcome<WorkflowMap>.NotFound();
        var gateway = GatewayFor(inst.ObjectType) ?? throw new InvalidOperationException($"Object type {inst.ObjectType} is not registered for review.");
        var obj = await gateway.FindByRevisionAsync(conn, tx, inst.RevisionId, forUpdate: false, ct);
        var read = obj is null ? null : AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Read);
        var tasks = await WorkflowStore.LoadTasksAsync(conn, tx, inst.Id, ct);
        var redacted = obj is null || read is not { Permitted: true };
        if (redacted && !tasks.Any(t => t.AssigneeSub == subject.Id))
        {
            await audit.AppendAsync(tx, Entry(subject, "workflow.map", AuditOutcome.Denied, inst.ObjectType, inst.ObjectKey, correlationId,
                ("instance", inst.Id.ToString("D")), ("reasons", read is null ? "object.missing" : string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<WorkflowMap>.NotFound();
        }

        var execution = await WorkflowStore.LoadExecutionAsync(conn, tx, inst.Id, ct);
        var signatures = (await WorkflowStore.LoadSignaturesAsync(conn, tx, inst.Id, ct)).ToDictionary(s => s.TaskId);
        var template = TemplateParser.Parse(inst.Definition).Value!;
        var participants = WorkflowStore.ParseParticipants(inst.Participants);
        var map = WorkflowMapBuilder.Build(template, execution, signatures, participants, inst.Status, clock.GetUtcNow(), redacted);
        await audit.AppendAsync(tx, Entry(subject, "workflow.map", AuditOutcome.Success, inst.ObjectType, inst.ObjectKey, correlationId,
            ("instance", inst.Id.ToString("D")), ("redacted", redacted.ToString())), ct);
        await tx.CommitAsync(ct);
        return Outcome<WorkflowMap>.Ok(new WorkflowMap(inst.Id, TemplateNameOf(inst), inst.Status, redacted, map, redacted ? UiBanner.Unclassified : obj!.Markings.BannerText()));
    }
}

/// <summary>Flattens a definition and its execution state into ordered stages. Pure: no data access, so it is unit tested directly.</summary>
internal static class WorkflowMapBuilder
{
    internal static IReadOnlyList<MapStage> Build(
        WorkflowTemplate template, ExecutionState execution, IReadOnlyDictionary<Guid, SignatureRow> signatures,
        IReadOnlyDictionary<string, IReadOnlyList<Participant>> participants, string instanceStatus, DateTimeOffset now, bool redacted)
    {
        var stages = new List<MapStage>();
        Walk(template.Node(template.Start));
        return stages;

        void Walk(NodeDef node)
        {
            switch (node)
            {
                case SerialNode s:
                    foreach (var step in s.Steps) Walk(template.Node(step));
                    break;
                case TaskNode t:
                {
                    var token = execution.Tokens.FirstOrDefault(x => x.NodeId == t.Id);
                    var tasks = execution.Tasks.Where(x => x.NodeId == t.Id).Select(x => ToTask(x)).ToList();
                    if (tasks.Count == 0) tasks.Add(Waiting(t.Name, t.Kind, t.Assignment.Mode == AssignmentMode.Slot ? One(t.Assignment.Slot!) : null, t.Assignment.Role));
                    stages.Add(new MapStage(t.Id, t.Name, "Task", null, 1, 1, StageState(token), tasks));
                    break;
                }
                case ParallelNode p:
                {
                    var group = execution.Tokens.FirstOrDefault(x => x.NodeId == p.Id && x.Branch is null || (p.FanOut is null && x.NodeId == p.Id));
                    var tasks = (p.FanOut is not null
                            ? execution.Tasks.Where(x => x.NodeId == p.Id)
                            : execution.Tasks.Where(x => p.Branches.Contains(x.NodeId)))
                        .Select(x => ToTask(x)).ToList();
                    int total;
                    if (tasks.Count == 0 && p.FanOut is { } fan)
                    {
                        var people = participants.GetValueOrDefault(fan.Slot) ?? [];
                        tasks.AddRange(people.Select(x => Waiting(p.Name, fan.Kind, x, null)));
                        total = people.Count;
                    }
                    else if (tasks.Count == 0)
                    {
                        tasks.AddRange(p.Branches.Select(b => template.Node(b) is TaskNode bt ? Waiting(bt.Name, bt.Kind, bt.Assignment.Mode == AssignmentMode.Slot ? One(bt.Assignment.Slot!) : null, bt.Assignment.Role) : Waiting(template.Node(b).Name, "review", null, null)));
                        total = p.Branches.Count;
                    }
                    else total = group is { BranchCount: > 0 } ? group.BranchCount : tasks.Count;
                    var required = total == 0 ? 0 : p.Rule.Required(total);
                    var rule = p.Rule.Kind switch
                    {
                        VoteKind.All => "All",
                        VoteKind.Any => "Any",
                        _ => string.Create(CultureInfo.InvariantCulture, $"{required} of {total}"),
                    };
                    stages.Add(new MapStage(p.Id, p.Name, "Parallel", rule, required, total, StageState(group), tasks));
                    break;
                }
                case EndNode e:
                    stages.Add(new MapStage(e.Id, e.Name, "End", null, 0, 0, instanceStatus == InstanceStatus.Approved ? "Approved" : "Waiting", []));
                    break;
            }
        }

        MapPerson? One(string slot) => participants.GetValueOrDefault(slot) is { Count: > 0 } l ? Person(l[0]) : null;

        MapPerson? Person(Participant p) => redacted ? new MapPerson("Participant", "?", true) : new MapPerson(p.DisplayName, Initials(p.DisplayName), false);

        MapTask Waiting(string name, string kind, object? who, string? role) =>
            new(null, name, kind, "Waiting", null, who switch { Participant p => Person(p), MapPerson m => m, _ => null }, who is null && role is not null ? role : null, null, null, false);

        MapTask ToTask(TaskState t)
        {
            var status = t.Status switch
            {
                WorkTaskStatus.Open => "Pending",
                WorkTaskStatus.Completed => t.Decision == "reject" ? "Rejected" : "Signed",
                WorkTaskStatus.Skipped => "Skipped",
                _ => "Cancelled",
            };
            var person = t.AssigneeName is null ? null : redacted ? new MapPerson("Participant", "?", true) : new MapPerson(t.AssigneeName, Initials(t.AssigneeName), false);
            string? meaning = null;
            if (!redacted && signatures.TryGetValue(t.Id, out var sig)) meaning = sig.MeaningLabel;
            return new MapTask(t.Id, t.Name, t.Kind, status, meaning, person, t.AssigneeName is null ? t.RequiredRole : null,
                redacted ? null : t.CompletedAt, redacted ? null : t.DueAt, !redacted && t.Status == WorkTaskStatus.Open && t.DueAt is { } due && due < now);
        }
    }

    private static string StageState(TokenState? token) => token?.Status switch
    {
        null => "Waiting",
        TokenStatus.Active => "Active",
        TokenStatus.Completed => "Approved",
        TokenStatus.Rejected => "Rejected",
        _ => "Skipped",
    };

    internal static string Initials(string name)
    {
        var parts = name.Split([' ', '.', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        return parts.Length == 1 ? parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant() : $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }
}
