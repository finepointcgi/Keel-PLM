namespace Keel.Workflow;

public enum TokenStatus
{
    Active,
    Completed,
    Rejected,
    Skipped,
}

public enum WorkTaskStatus
{
    Open,
    Completed,
    Skipped,
    Cancelled,
}

public enum InstanceOutcome
{
    Approved,
    Rejected,
}

/// <summary>A node activation. Serial tokens remember their step, Parallel tokens their branch count.</summary>
public sealed class TokenState
{
    public required Guid Id { get; init; }
    public required string NodeId { get; init; }
    public Guid? ParentId { get; init; }
    public int? Branch { get; init; }
    public int StepIndex { get; set; }
    public int BranchCount { get; set; }
    public TokenStatus Status { get; set; } = TokenStatus.Active;
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool IsNew { get; set; }
    public bool IsDirty { get; set; }
}

public sealed class TaskState
{
    public required Guid Id { get; init; }
    public Guid InstanceId { get; set; }
    public required Guid TokenId { get; init; }
    public required string NodeId { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string? AssigneeSub { get; set; }
    public string? AssigneeUsername { get; set; }
    public string? AssigneeName { get; set; }
    public required string RequiredRole { get; init; }
    public WorkTaskStatus Status { get; set; } = WorkTaskStatus.Open;
    public string? Decision { get; set; }
    public Guid? SignatureId { get; set; }
    public DateTimeOffset? DueAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool IsNew { get; set; }
    public bool IsDirty { get; set; }
}

public sealed record Participant(string Sub, string Username, string DisplayName);

/// <summary>Everything the engine needs to continue an instance: its tokens and tasks.</summary>
public sealed class ExecutionState
{
    public List<TokenState> Tokens { get; } = [];
    public List<TaskState> Tasks { get; } = [];
    public InstanceOutcome? Outcome { get; set; }
}

/// <summary>What one engine step changed, for audit and notification.</summary>
public sealed record Advance(IReadOnlyList<TaskState> Created, IReadOnlyList<TaskState> Skipped, InstanceOutcome? Outcome);

/// <summary>
/// The workflow interpreter (ADR-0006): a pure state machine over a template and the persisted tokens and tasks. It does
/// no I/O, so vote rules and progression are unit-testable; <see cref="WorkflowService"/> loads the state, calls the
/// engine and writes the changes (flagged <c>IsNew</c> / <c>IsDirty</c>) in one transaction.
/// </summary>
public static class WorkflowEngine
{
    public static (ExecutionState State, Advance Advance) Start(
        WorkflowTemplate template, IReadOnlyDictionary<string, IReadOnlyList<Participant>> participants, DateTimeOffset now)
    {
        var state = new ExecutionState();
        var runner = new Runner(template, state, participants, now);
        runner.Activate(template.Node(template.Start), null, null);
        return (state, runner.Result());
    }

    /// <summary>Records the decision on an open task and advances the instance.</summary>
    public static Advance Complete(
        WorkflowTemplate template, ExecutionState state, IReadOnlyDictionary<string, IReadOnlyList<Participant>> participants,
        Guid taskId, bool approve, Guid? signatureId, DateTimeOffset now)
    {
        var task = state.Tasks.FirstOrDefault(t => t.Id == taskId) ?? throw new InvalidOperationException($"Task {taskId} is not part of this instance.");
        if (task.Status != WorkTaskStatus.Open) throw new InvalidOperationException($"Task {taskId} is {task.Status}, not open.");
        if (state.Outcome is not null) throw new InvalidOperationException("The instance has already completed.");

        task.Status = WorkTaskStatus.Completed;
        task.Decision = approve ? "approve" : "reject";
        task.SignatureId = signatureId;
        task.CompletedAt = now;
        task.IsDirty = true;

        var runner = new Runner(template, state, participants, now);
        runner.Finish(state.Tokens.Single(t => t.Id == task.TokenId), approve);
        runner.CancelLeftovers();
        return runner.Result();
    }

    private sealed class Runner(
        WorkflowTemplate template, ExecutionState state, IReadOnlyDictionary<string, IReadOnlyList<Participant>> participants, DateTimeOffset now)
    {
        private readonly List<TaskState> _created = [];
        private readonly List<TaskState> _skipped = [];

        public Advance Result() => new(_created, _skipped, state.Outcome);

        private TokenState NewToken(string nodeId, Guid? parent, int? branch)
        {
            var token = new TokenState { Id = Guid.CreateVersion7(), NodeId = nodeId, ParentId = parent, Branch = branch, CreatedAt = now, IsNew = true };
            state.Tokens.Add(token);
            return token;
        }

        public void Activate(NodeDef node, TokenState? parent, int? branch)
        {
            var token = NewToken(node.Id, parent?.Id, branch);
            switch (node)
            {
                case TaskNode t:
                    CreateTask(token, t.Name, t.Kind, t.DueDays, t.Assignment.Mode == AssignmentMode.Slot
                        ? (participants[t.Assignment.Slot!][0], template.SlotNamed(t.Assignment.Slot!).Role)
                        : (null, t.Assignment.Role!));
                    break;
                case SerialNode s:
                    token.StepIndex = 0;
                    Activate(template.Node(s.Steps[0]), token, null);
                    break;
                case ParallelNode p when p.FanOut is { } fan:
                    var users = participants[fan.Slot];
                    token.BranchCount = users.Count;
                    for (var i = 0; i < users.Count; i++)
                        CreateTask(NewToken(p.Id, token.Id, i), p.Name, fan.Kind, p.DueDays, (users[i], template.SlotNamed(fan.Slot).Role));
                    break;
                case ParallelNode p:
                    token.BranchCount = p.Branches.Count;
                    for (var i = 0; i < p.Branches.Count; i++) Activate(template.Node(p.Branches[i]), token, i);
                    break;
                case EndNode:
                    Finish(token, true);
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled node {node.GetType().Name}.");
            }
        }

        private void CreateTask(TokenState token, string name, string kind, int? dueDays, (Participant? Who, string Role) assignment)
        {
            var task = new TaskState
            {
                Id = Guid.CreateVersion7(),
                TokenId = token.Id,
                NodeId = token.NodeId,
                Name = name,
                Kind = kind,
                AssigneeSub = assignment.Who?.Sub,
                AssigneeUsername = assignment.Who?.Username,
                AssigneeName = assignment.Who?.DisplayName,
                RequiredRole = assignment.Role,
                DueAt = dueDays is { } d ? now.AddDays(d) : null,
                CreatedAt = now,
                IsNew = true,
            };
            state.Tasks.Add(task);
            _created.Add(task);
        }

        /// <summary>A token reached a terminal state: tell its parent, which may activate the next step or decide a vote.</summary>
        public void Finish(TokenState token, bool approved)
        {
            if (token.Status != TokenStatus.Active) return;
            token.Status = approved ? TokenStatus.Completed : TokenStatus.Rejected;
            token.CompletedAt = now;
            token.IsDirty = true;

            if (token.ParentId is not { } parentId)
            {
                state.Outcome = approved ? InstanceOutcome.Approved : InstanceOutcome.Rejected;
                return;
            }
            var parent = state.Tokens.Single(t => t.Id == parentId);
            if (parent.Status != TokenStatus.Active) return;

            switch (template.Node(parent.NodeId))
            {
                case SerialNode s:
                    if (!approved) Finish(parent, false);
                    else if (parent.StepIndex + 1 < s.Steps.Count)
                    {
                        parent.StepIndex++;
                        parent.IsDirty = true;
                        Activate(template.Node(s.Steps[parent.StepIndex]), parent, null);
                    }
                    else Finish(parent, true);
                    break;
                case ParallelNode p:
                    var children = state.Tokens.Where(t => t.ParentId == parent.Id).ToList();
                    var required = p.Rule.Required(parent.BranchCount);
                    var approvals = children.Count(t => t.Status == TokenStatus.Completed);
                    var rejections = children.Count(t => t.Status == TokenStatus.Rejected);
                    if (approvals >= required)
                    {
                        SkipActiveBelow(parent);
                        Finish(parent, true);
                    }
                    else if (rejections > parent.BranchCount - required)
                    {
                        SkipActiveBelow(parent);
                        Finish(parent, false);
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Node {parent.NodeId} cannot have children.");
            }
        }

        /// <summary>Short circuit: once a vote is decided the branches still running are skipped, with their open tasks.</summary>
        private void SkipActiveBelow(TokenState parent)
        {
            foreach (var child in state.Tokens.Where(t => t.ParentId == parent.Id).ToList())
            {
                if (child.Status == TokenStatus.Active)
                {
                    SkipActiveBelow(child);
                    child.Status = TokenStatus.Skipped;
                    child.CompletedAt = now;
                    child.IsDirty = true;
                }
                foreach (var task in state.Tasks.Where(t => t.TokenId == child.Id && t.Status == WorkTaskStatus.Open))
                {
                    task.Status = WorkTaskStatus.Skipped;
                    task.CompletedAt = now;
                    task.IsDirty = true;
                    _skipped.Add(task);
                }
            }
        }

        /// <summary>Once the instance has an outcome nothing may stay open.</summary>
        public void CancelLeftovers()
        {
            if (state.Outcome is null) return;
            foreach (var task in state.Tasks.Where(t => t.Status == WorkTaskStatus.Open))
            {
                task.Status = WorkTaskStatus.Skipped;
                task.CompletedAt = now;
                task.IsDirty = true;
                _skipped.Add(task);
            }
        }
    }
}
