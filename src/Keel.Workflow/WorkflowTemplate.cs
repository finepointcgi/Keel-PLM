using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Keel.Core;

namespace Keel.Workflow;

public enum VoteKind
{
    All,
    Any,
    NofM,
}

/// <summary>
/// How many branches of a Parallel node must approve. The node is decided as soon as the outcome cannot change:
/// approved when <see cref="Required"/> approvals are in, rejected when so many branches rejected that
/// <see cref="Required"/> approvals are no longer reachable. Undecided branches are then skipped (short circuit).
/// </summary>
public sealed record VoteRule(VoteKind Kind, int N = 0)
{
    public int Required(int total) => Kind switch
    {
        VoteKind.All => total,
        VoteKind.Any => Math.Min(1, total),
        VoteKind.NofM => Math.Min(N, total),
        _ => throw new InvalidOperationException($"Unknown vote rule {Kind}."),
    };

    public override string ToString() => Kind == VoteKind.NofM ? $"{N} of M" : Kind.ToString();
}

/// <summary>Named participant group chosen when the workflow starts (for example "approvers").</summary>
public sealed record SlotDef(string Name, string Label, string Role, int Min, int Max);

public enum AssignmentMode
{
    /// <summary>The single user picked for a slot.</summary>
    Slot,
    /// <summary>Any holder of a role who may also read the object (a pool task).</summary>
    Role,
}

public sealed record Assignment(AssignmentMode Mode, string? Slot, string? Role);

public abstract record NodeDef(string Id, string Name);

public sealed record TaskNode(string Id, string Name, string Kind, Assignment Assignment, int? DueDays) : NodeDef(Id, Name);

public sealed record SerialNode(string Id, string Name, IReadOnlyList<string> Steps) : NodeDef(Id, Name);

/// <summary>Fan-out: one task of <paramref name="Kind"/> per participant of <paramref name="Slot"/>.</summary>
public sealed record FanOut(string Slot, string Kind);

public sealed record ParallelNode(string Id, string Name, VoteRule Rule, IReadOnlyList<string> Branches, FanOut? FanOut, int? DueDays) : NodeDef(Id, Name);

public sealed record EndNode(string Id, string Name, string Outcome) : NodeDef(Id, Name);

public sealed record WorkflowTemplate(
    string Id,
    string Name,
    string? Description,
    string ObjectType,
    IReadOnlyList<SlotDef> Slots,
    string Start,
    IReadOnlyDictionary<string, NodeDef> Nodes)
{
    public NodeDef Node(string id) => Nodes[id];
    public SlotDef SlotNamed(string name) => Slots.First(s => s.Name == name);
}

public static class TaskKinds
{
    public const string Review = "review";
    public const string Approval = "approval";
    public static readonly IReadOnlyList<string> All = [Review, Approval];
}

/// <summary>Role semantics for review work. Auditors are independent oversight and never sign.</summary>
public static class WorkflowRoles
{
    private static readonly string[] ViewerOrHigher = [Roles.Viewer, Roles.Author, Roles.Approver, Roles.Admin];

    /// <summary>"plm-viewer" means viewer or any higher role; every other role must be held exactly.</summary>
    public static bool Satisfies(IEnumerable<string> held, string required) =>
        required == Roles.Viewer ? held.Any(r => ViewerOrHigher.Contains(r)) : held.Contains(required);
}

/// <summary>
/// Parses and validates a template definition (ADR-0006). Every problem is reported at once, with the node it concerns,
/// so an administrator can fix an upload in one pass: no orphan nodes, a reachable End, valid vote rules, consistent slots.
/// </summary>
public static partial class TemplateParser
{
    public const int MaxNodes = 100;
    public const string ReleaseOutcome = "Release";

    private static readonly string[] UnsupportedTypes = ["Condition", "SetState", "Notify"];
    private static readonly HashSet<string> RootKeys = ["id", "name", "description", "objectType", "slots", "start", "nodes"];
    private static readonly HashSet<string> SlotKeys = ["name", "label", "role", "min", "max"];
    private static readonly HashSet<string> NodeKeys = ["id", "type", "name", "kind", "assignment", "dueDays", "rule", "branches", "fanOut", "steps", "outcome"];

    public static Result<WorkflowTemplate> Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException e)
        {
            return Result<WorkflowTemplate>.Fail([$"The template is not valid JSON: {e.Message}"]);
        }
        if (root is not JsonObject obj) return Result<WorkflowTemplate>.Fail(["The template must be a JSON object."]);

        var errors = new List<string>();
        foreach (var key in obj.Select(p => p.Key).Where(k => !RootKeys.Contains(k)))
            errors.Add($"Unknown template property '{key}'.");

        var id = Str(obj, "id", errors, "template");
        if (id is not null && !IdPattern().IsMatch(id)) errors.Add("Template id must be lowercase letters, digits and '-', starting with a letter (max 63).");
        var name = Str(obj, "name", errors, "template");
        var objectType = Str(obj, "objectType", errors, "template");
        var description = obj["description"]?.GetValueKind() == JsonValueKind.String ? obj["description"]!.GetValue<string>() : null;
        var start = Str(obj, "start", errors, "template");

        var slots = ParseSlots(obj, errors);
        var nodes = ParseNodes(obj, slots, errors);
        if (id is null || name is null || objectType is null || start is null) return Result<WorkflowTemplate>.Fail(errors);

        ValidateGraph(start, nodes, slots, errors);
        if (errors.Count > 0) return Result<WorkflowTemplate>.Fail(errors);
        return Result<WorkflowTemplate>.Ok(new WorkflowTemplate(id, name, description, objectType, slots, start, nodes));
    }

    // ---------------------------------------------------------------- slots

    private static List<SlotDef> ParseSlots(JsonObject obj, List<string> errors)
    {
        var slots = new List<SlotDef>();
        if (obj["slots"] is null) return slots;
        if (obj["slots"] is not JsonArray array)
        {
            errors.Add("'slots' must be an array.");
            return slots;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is not JsonObject s)
            {
                errors.Add("Every slot must be an object.");
                continue;
            }
            foreach (var key in s.Select(p => p.Key).Where(k => !SlotKeys.Contains(k))) errors.Add($"Unknown slot property '{key}'.");
            var name = Str(s, "name", errors, "slot");
            var label = Str(s, "label", errors, $"slot '{name}'") ?? name;
            var role = Str(s, "role", errors, $"slot '{name}'");
            var min = Int(s, "min", errors, $"slot '{name}'");
            var max = Int(s, "max", errors, $"slot '{name}'");
            if (name is null || role is null || min is null || max is null) continue;
            if (!IdPattern().IsMatch(name)) errors.Add($"Slot name '{name}' must be lowercase letters, digits and '-'.");
            if (!names.Add(name)) errors.Add($"Slot '{name}' is declared twice.");
            if (!Roles.All.Contains(role) || role == Roles.Auditor) errors.Add($"Slot '{name}': role '{role}' must be one of plm-viewer, plm-author, plm-approver, plm-admin.");
            if (min < 1) errors.Add($"Slot '{name}': min must be at least 1.");
            if (max < min || max > 50) errors.Add($"Slot '{name}': max must be between min and 50.");
            slots.Add(new SlotDef(name, label ?? name, role, min.Value, max.Value));
        }
        return slots;
    }

    // ---------------------------------------------------------------- nodes

    private static Dictionary<string, NodeDef> ParseNodes(JsonObject obj, List<SlotDef> slots, List<string> errors)
    {
        var nodes = new Dictionary<string, NodeDef>(StringComparer.Ordinal);
        if (obj["nodes"] is not JsonArray array || array.Count == 0)
        {
            errors.Add("'nodes' must be a non-empty array.");
            return nodes;
        }
        if (array.Count > MaxNodes) errors.Add($"A template may have at most {MaxNodes} nodes.");
        foreach (var item in array)
        {
            if (item is not JsonObject n)
            {
                errors.Add("Every node must be an object.");
                continue;
            }
            var id = Str(n, "id", errors, "node");
            var type = Str(n, "type", errors, $"node '{id}'");
            if (id is null || type is null) continue;
            if (!IdPattern().IsMatch(id)) errors.Add($"Node id '{id}' must be lowercase letters, digits and '-'.");
            if (nodes.ContainsKey(id)) { errors.Add($"Node id '{id}' is used twice."); continue; }
            foreach (var key in n.Select(p => p.Key).Where(k => !NodeKeys.Contains(k))) errors.Add($"Node '{id}': unknown property '{key}'.");
            var name = n["name"]?.GetValueKind() == JsonValueKind.String ? n["name"]!.GetValue<string>() : id;

            NodeDef? node = type switch
            {
                "Task" => ParseTask(n, id, name, slots, errors),
                "Serial" => ParseSerial(n, id, name, errors),
                "Parallel" => ParseParallel(n, id, name, slots, errors),
                "End" => ParseEnd(n, id, name, errors),
                _ when UnsupportedTypes.Contains(type) => Unsupported(id, type, errors),
                _ => Unknown(id, type, errors),
            };
            if (node is not null) nodes[id] = node;
        }
        return nodes;
    }

    private static NodeDef? Unsupported(string id, string type, List<string> errors)
    {
        errors.Add($"Node '{id}': node type '{type}' is not supported yet.");
        return null;
    }

    private static NodeDef? Unknown(string id, string type, List<string> errors)
    {
        errors.Add($"Node '{id}': unknown node type '{type}' (expected Task, Parallel, Serial or End).");
        return null;
    }

    private static TaskNode? ParseTask(JsonObject n, string id, string name, List<SlotDef> slots, List<string> errors)
    {
        var kind = Str(n, "kind", errors, $"node '{id}'");
        if (kind is not null && !TaskKinds.All.Contains(kind)) errors.Add($"Node '{id}': kind must be 'review' or 'approval'.");
        var due = OptionalDays(n, id, errors);
        if (n["assignment"] is not JsonObject a)
        {
            errors.Add($"Node '{id}': Task nodes need an 'assignment'.");
            return null;
        }
        var mode = Str(a, "mode", errors, $"node '{id}' assignment");
        Assignment? assignment = null;
        switch (mode)
        {
            case "Slot":
                var slot = Str(a, "slot", errors, $"node '{id}' assignment");
                if (slot is not null)
                {
                    var def = slots.FirstOrDefault(s => s.Name == slot);
                    if (def is null) errors.Add($"Node '{id}': slot '{slot}' is not declared.");
                    else if (def.Min != 1 || def.Max != 1) errors.Add($"Node '{id}': a Task takes exactly one user, so slot '{slot}' must have min 1 and max 1 (use a Parallel fanOut for several users).");
                    assignment = new Assignment(AssignmentMode.Slot, slot, null);
                }
                break;
            case "Role":
                var role = Str(a, "role", errors, $"node '{id}' assignment");
                if (role is not null)
                {
                    if (!Roles.All.Contains(role) || role == Roles.Auditor) errors.Add($"Node '{id}': role '{role}' cannot be assigned tasks.");
                    assignment = new Assignment(AssignmentMode.Role, null, role);
                }
                break;
            case null:
                break;
            default:
                errors.Add($"Node '{id}': assignment mode '{mode}' is not supported (expected Slot or Role).");
                break;
        }
        return kind is null || assignment is null ? null : new TaskNode(id, name, kind, assignment, due);
    }

    private static SerialNode? ParseSerial(JsonObject n, string id, string name, List<string> errors)
    {
        var steps = StringList(n, "steps", id, errors);
        if (steps is { Count: 0 }) errors.Add($"Node '{id}': a Serial node needs at least one step.");
        return steps is null ? null : new SerialNode(id, name, steps);
    }

    private static ParallelNode? ParseParallel(JsonObject n, string id, string name, List<SlotDef> slots, List<string> errors)
    {
        var due = OptionalDays(n, id, errors);
        VoteRule? rule = null;
        if (n["rule"] is not JsonObject r) errors.Add($"Node '{id}': Parallel nodes need a vote 'rule'.");
        else
        {
            switch (Str(r, "type", errors, $"node '{id}' rule"))
            {
                case "All": rule = new VoteRule(VoteKind.All); break;
                case "Any": rule = new VoteRule(VoteKind.Any); break;
                case "NofM":
                    var count = Int(r, "n", errors, $"node '{id}' rule");
                    if (count is < 1) errors.Add($"Node '{id}': an N-of-M rule needs n of at least 1.");
                    else if (count is { } c) rule = new VoteRule(VoteKind.NofM, c);
                    break;
                case null: break;
                case var other: errors.Add($"Node '{id}': unknown vote rule '{other}' (expected All, Any or NofM)."); break;
            }
        }

        var hasBranches = n["branches"] is not null;
        var hasFanOut = n["fanOut"] is not null;
        if (hasBranches == hasFanOut) errors.Add($"Node '{id}': a Parallel node needs exactly one of 'branches' or 'fanOut'.");
        List<string> branches = [];
        FanOut? fan = null;
        if (hasBranches)
        {
            branches = StringList(n, "branches", id, errors) ?? [];
            if (branches.Count == 0) errors.Add($"Node '{id}': 'branches' must list at least one node.");
            else if (rule is { Kind: VoteKind.NofM } nm && nm.N > branches.Count) errors.Add($"Node '{id}': the rule needs {nm.N} approvals but there are only {branches.Count} branches.");
        }
        if (hasFanOut)
        {
            if (n["fanOut"] is not JsonObject f) errors.Add($"Node '{id}': 'fanOut' must be an object.");
            else
            {
                var slot = Str(f, "slot", errors, $"node '{id}' fanOut");
                var kind = Str(f, "kind", errors, $"node '{id}' fanOut");
                if (kind is not null && !TaskKinds.All.Contains(kind)) errors.Add($"Node '{id}': fanOut kind must be 'review' or 'approval'.");
                if (slot is not null && kind is not null)
                {
                    var def = slots.FirstOrDefault(s => s.Name == slot);
                    if (def is null) errors.Add($"Node '{id}': slot '{slot}' is not declared.");
                    else if (rule is { Kind: VoteKind.NofM } nm && nm.N > def.Min) errors.Add($"Node '{id}': the rule needs {nm.N} approvals but slot '{slot}' may hold as few as {def.Min} users.");
                    fan = new FanOut(slot, kind);
                }
            }
        }
        return rule is null || (hasBranches && hasFanOut) || (!hasBranches && fan is null) ? null : new ParallelNode(id, name, rule, branches, fan, due);
    }

    private static EndNode? ParseEnd(JsonObject n, string id, string name, List<string> errors)
    {
        var outcome = Str(n, "outcome", errors, $"node '{id}'");
        if (outcome is not null && outcome != ReleaseOutcome) errors.Add($"Node '{id}': End outcome must be '{ReleaseOutcome}'.");
        return outcome is null ? null : new EndNode(id, name, outcome);
    }

    // ---------------------------------------------------------------- graph rules

    private static void ValidateGraph(string start, Dictionary<string, NodeDef> nodes, List<SlotDef> slots, List<string> errors)
    {
        if (nodes.Count == 0) return;
        if (!nodes.TryGetValue(start, out var root))
        {
            errors.Add($"Start node '{start}' does not exist.");
            return;
        }

        // Walk from the start node. A node reached twice is either shared or part of a cycle; both are rejected, so the
        // nodes form a tree, which is what the executor's token model assumes.
        var visited = new HashSet<string>(StringComparer.Ordinal) { root.Id };
        void Walk(NodeDef node)
        {
            IReadOnlyList<string> children = node switch
            {
                SerialNode s => s.Steps,
                ParallelNode p => p.Branches,
                _ => [],
            };
            foreach (var child in children)
            {
                if (!nodes.TryGetValue(child, out var next))
                {
                    if (!errors.Any(e => e.Contains($"'{child}'", StringComparison.Ordinal))) errors.Add($"Node '{node.Id}' refers to '{child}', which does not exist or is invalid.");
                    continue;
                }
                if (!visited.Add(next.Id))
                {
                    errors.Add($"Node '{next.Id}' is referenced more than once or forms a cycle; each node may appear in exactly one place.");
                    continue;
                }
                Walk(next);
            }
        }
        Walk(root);

        foreach (var orphan in nodes.Keys.Where(k => !visited.Contains(k)).Order(StringComparer.Ordinal))
            errors.Add($"Node '{orphan}' is not reachable from the start node (orphan).");

        // The release path: the start node is a Serial whose last step is End. End anywhere else has no defined meaning.
        if (root is not SerialNode serial) errors.Add($"The start node '{start}' must be a Serial node ending in an End node.");
        else if (serial.Steps.Count == 0 || nodes.GetValueOrDefault(serial.Steps[^1]) is not EndNode) errors.Add($"The start sequence '{start}' must end with an End node so the workflow can complete.");
        foreach (var end in nodes.Values.OfType<EndNode>())
        {
            var isFinalStep = root is SerialNode r && r.Steps.Count > 0 && r.Steps[^1] == end.Id;
            if (!isFinalStep) errors.Add($"End node '{end.Id}' must be the final step of the start sequence.");
        }
        foreach (var p in nodes.Values.OfType<ParallelNode>())
            foreach (var b in p.Branches.Where(b => nodes.GetValueOrDefault(b) is EndNode))
                errors.Add($"Node '{p.Id}': branch '{b}' is an End node; End cannot run in parallel.");

        if (!nodes.Values.Any(n => n is TaskNode or ParallelNode)) errors.Add("The template must contain at least one Task or Parallel node (a workflow with no review cannot release anything).");

        var used = nodes.Values.OfType<TaskNode>().Select(t => t.Assignment.Slot)
            .Concat(nodes.Values.OfType<ParallelNode>().Select(p => p.FanOut?.Slot)).Where(s => s is not null).ToHashSet();
        foreach (var slot in slots.Where(s => !used.Contains(s.Name)))
            errors.Add($"Slot '{slot.Name}' is not used by any node.");
    }

    // ---------------------------------------------------------------- JSON helpers

    private static string? Str(JsonObject o, string name, List<string> errors, string what)
    {
        if (o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetValue<string>())) return v.GetValue<string>().Trim();
        errors.Add($"{Capital(what)}: '{name}' is required and must be a non-empty string.");
        return null;
    }

    private static int? Int(JsonObject o, string name, List<string> errors, string what)
    {
        if (o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<int>(out var i)) return i;
        errors.Add($"{Capital(what)}: '{name}' is required and must be an integer.");
        return null;
    }

    private static int? OptionalDays(JsonObject n, string id, List<string> errors)
    {
        if (n["dueDays"] is null) return null;
        if (n["dueDays"] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<int>(out var d) && d is >= 1 and <= 365) return d;
        errors.Add($"Node '{id}': dueDays must be an integer from 1 to 365.");
        return null;
    }

    private static List<string>? StringList(JsonObject n, string name, string id, List<string> errors)
    {
        if (n[name] is not JsonArray array)
        {
            errors.Add($"Node '{id}': '{name}' must be an array of node ids.");
            return null;
        }
        var list = new List<string>();
        foreach (var item in array)
        {
            if (item is JsonValue v && v.GetValueKind() == JsonValueKind.String) list.Add(v.GetValue<string>());
            else errors.Add($"Node '{id}': '{name}' may contain only node ids.");
        }
        return list;
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    [GeneratedRegex("^[a-z][a-z0-9-]{0,62}$")]
    private static partial Regex IdPattern();
}
