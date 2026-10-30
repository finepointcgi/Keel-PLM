using System.Text.Json.Nodes;

namespace Keel.Admin;

/// <summary>
/// One step of the "steps" view of a workflow template (ADR-0056). <see cref="Who"/> is <c>person</c> (one user chosen when the review starts: a Task assigned to a
/// one-person group), <c>role</c> (anyone holding a role: a pool Task) or <c>group</c> (several users chosen when the review starts, deciding by a vote rule: a
/// Parallel node that fans out to a group). Nothing here is an engine feature of its own: it is exactly the Task and Parallel nodes the engine already runs.
/// </summary>
public sealed class StepDraft
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The kind of task: "review" or "approval". It decides which signature meanings the signer is offered.</summary>
    public string Kind { get; set; } = "review";
    public string Who { get; set; } = StepWho.Person;
    public string Role { get; set; } = "plm-approver";
    /// <summary>The label of the group a review's starter fills in ("Technical reviewer"). Used for person and group steps.</summary>
    public string GroupLabel { get; set; } = "";
    /// <summary>The group's name inside the definition. Kept as read from an existing template; a new step uses its id.</summary>
    public string? Slot { get; set; }
    public int Min { get; set; } = 1;
    public int Max { get; set; } = 3;
    /// <summary>For a group: All, Any or NofM.</summary>
    public string Rule { get; set; } = "All";
    public int RuleN { get; set; } = 2;
    public int? DueDays { get; set; }
}

public static class StepWho
{
    public const string Person = "person";
    public const string Role = "role";
    public const string Group = "group";
}

/// <summary>
/// The simple shape of a template: an ordered list of steps followed by the release. Templates in this shape open in the steps editor; anything else (nested
/// serial blocks, branches, a group shared by two steps) opens in the advanced node editor or the JSON view. Business rules stay in the engine's parser:
/// this class only converts to and from the JSON the engine reads.
/// </summary>
// ASSUMPTION(A82): the steps view exposes only what the engine runs (Task, Parallel with a vote rule, End); there are no conditions or fixed assignees to edit.
public sealed class StepsDraft
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string ObjectType { get; set; } = "part";
    public List<StepDraft> Steps { get; set; } = [];
    public string FlowId { get; set; } = "main";
    public string FlowName { get; set; } = "Review";
    public string EndId { get; set; } = "release";
    public string EndName { get; set; } = "Release";

    /// <summary>The next unused step id ("step-1", "step-2", ...), so a new step never collides with an existing node or group name.</summary>
    public string NextStepId()
    {
        var used = Steps.SelectMany(s => new[] { s.Id, s.Slot }).Concat([FlowId, EndId]).Where(x => x is not null).ToHashSet(StringComparer.Ordinal);
        for (var i = 1; ; i++)
            if (!used.Contains($"step-{i}")) return $"step-{i}";
    }

    public JsonObject ToJson()
    {
        var slots = new JsonArray();
        var nodes = new JsonArray();
        var flowSteps = new JsonArray();
        foreach (var s in Steps)
        {
            var slot = s.Slot ?? s.Id;
            flowSteps.Add(s.Id);
            var node = new JsonObject { ["id"] = s.Id, ["name"] = s.Name };
            switch (s.Who)
            {
                case StepWho.Role:
                    node["type"] = "Task";
                    node["kind"] = s.Kind;
                    node["assignment"] = new JsonObject { ["mode"] = "Role", ["role"] = s.Role };
                    if (s.DueDays is { } rd) node["dueDays"] = rd;
                    break;
                case StepWho.Group:
                    slots.Add(new JsonObject { ["name"] = slot, ["label"] = Label(s), ["role"] = s.Role, ["min"] = s.Min, ["max"] = s.Max });
                    node["type"] = "Parallel";
                    node["rule"] = s.Rule == "NofM" ? new JsonObject { ["type"] = "NofM", ["n"] = s.RuleN } : new JsonObject { ["type"] = s.Rule };
                    node["fanOut"] = new JsonObject { ["slot"] = slot, ["kind"] = s.Kind };
                    if (s.DueDays is { } gd) node["dueDays"] = gd;
                    break;
                default:
                    slots.Add(new JsonObject { ["name"] = slot, ["label"] = Label(s), ["role"] = s.Role, ["min"] = 1, ["max"] = 1 });
                    node["type"] = "Task";
                    node["kind"] = s.Kind;
                    node["assignment"] = new JsonObject { ["mode"] = "Slot", ["slot"] = slot };
                    if (s.DueDays is { } pd) node["dueDays"] = pd;
                    break;
            }
            nodes.Add(node);
        }
        flowSteps.Add(EndId);
        nodes.Insert(0, new JsonObject { ["id"] = FlowId, ["type"] = "Serial", ["name"] = FlowName, ["steps"] = flowSteps });
        nodes.Add(new JsonObject { ["id"] = EndId, ["type"] = "End", ["name"] = EndName, ["outcome"] = "Release" });

        var root = new JsonObject { ["id"] = Id, ["name"] = Name };
        if (!string.IsNullOrWhiteSpace(Description)) root["description"] = Description;
        root["objectType"] = ObjectType;
        root["slots"] = slots;
        root["start"] = FlowId;
        root["nodes"] = nodes;
        return root;
    }

    private static string Label(StepDraft s) => string.IsNullOrWhiteSpace(s.GroupLabel) ? s.Name : s.GroupLabel;

    /// <summary>
    /// Reads a definition into steps. Returns a null draft and the reason when the definition is not in the simple shape (the caller then offers the advanced
    /// editor or the JSON view). A definition the engine would reject may still be shown here; validation reports it.
    /// </summary>
    public static (StepsDraft? Draft, string? Reason) FromJson(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return (null, "The definition must be a JSON object.");
            var d = new StepsDraft
            {
                Id = o["id"]?.GetValue<string>() ?? "", Name = o["name"]?.GetValue<string>() ?? "", Description = o["description"]?.GetValue<string>(),
                ObjectType = o["objectType"]?.GetValue<string>() ?? "part",
            };
            var slots = (o["slots"]?.AsArray() ?? []).Select(s => (Name: s!["name"]!.GetValue<string>(), Label: s["label"]?.GetValue<string>() ?? "", Role: s["role"]!.GetValue<string>(),
                Min: s["min"]!.GetValue<int>(), Max: s["max"]!.GetValue<int>())).ToDictionary(s => s.Name, StringComparer.Ordinal);
            var nodes = (o["nodes"]?.AsArray() ?? []).ToDictionary(n => n!["id"]!.GetValue<string>(), n => n!.AsObject(), StringComparer.Ordinal);
            var start = o["start"]?.GetValue<string>() ?? "";
            if (!nodes.TryGetValue(start, out var flow) || flow["type"]?.GetValue<string>() != "Serial") return (null, "This workflow does not start with a plain list of steps, so it is shown in the advanced view.");
            d.FlowId = start;
            d.FlowName = flow["name"]?.GetValue<string>() ?? "Review";

            var order = (flow["steps"]?.AsArray() ?? []).Select(x => x!.GetValue<string>()).ToList();
            var usedSlots = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < order.Count; i++)
            {
                if (!nodes.TryGetValue(order[i], out var n)) return (null, $"Step '{order[i]}' does not exist, so the workflow is shown in the advanced view.");
                var type = n["type"]?.GetValue<string>();
                if (type == "End")
                {
                    if (i != order.Count - 1) return (null, "The release is not the last step, so the workflow is shown in the advanced view.");
                    d.EndId = order[i];
                    d.EndName = n["name"]?.GetValue<string>() ?? "Release";
                    continue;
                }
                var step = new StepDraft { Id = order[i], Name = n["name"]?.GetValue<string>() ?? order[i], DueDays = n["dueDays"]?.GetValue<int>() };
                if (type == "Task")
                {
                    step.Kind = n["kind"]?.GetValue<string>() ?? "review";
                    var a = n["assignment"]?.AsObject();
                    if (a?["mode"]?.GetValue<string>() == "Role")
                    {
                        step.Who = StepWho.Role;
                        step.Role = a["role"]?.GetValue<string>() ?? "plm-approver";
                    }
                    else
                    {
                        var slot = a?["slot"]?.GetValue<string>() ?? "";
                        if (!slots.TryGetValue(slot, out var def) || def.Min != 1 || def.Max != 1 || !usedSlots.Add(slot))
                            return (null, $"Step '{step.Id}' uses a group that is shared or not for exactly one person, so the workflow is shown in the advanced view.");
                        step.Who = StepWho.Person;
                        step.Slot = slot;
                        step.Role = def.Role;
                        step.GroupLabel = def.Label;
                    }
                }
                else if (type == "Parallel" && n["fanOut"] is JsonObject fan)
                {
                    var slot = fan["slot"]?.GetValue<string>() ?? "";
                    if (!slots.TryGetValue(slot, out var def) || !usedSlots.Add(slot)) return (null, $"Step '{step.Id}' uses a group that is shared, so the workflow is shown in the advanced view.");
                    step.Who = StepWho.Group;
                    step.Kind = fan["kind"]?.GetValue<string>() ?? "approval";
                    step.Slot = slot;
                    step.Role = def.Role;
                    step.GroupLabel = def.Label;
                    step.Min = def.Min;
                    step.Max = def.Max;
                    step.Rule = n["rule"]?["type"]?.GetValue<string>() ?? "All";
                    step.RuleN = n["rule"]?["n"]?.GetValue<int>() ?? 2;
                }
                else return (null, $"Step '{step.Id}' is of a kind the steps view does not show (a nested block or fixed branches), so the workflow is shown in the advanced view.");
                d.Steps.Add(step);
            }
            if (nodes.Count != d.Steps.Count + 2) return (null, "The workflow has steps outside the main list, so it is shown in the advanced view.");
            return (d, null);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException or NullReferenceException or KeyNotFoundException or ArgumentException)
        {
            return (null, "The definition is not in a shape the steps view can show.");
        }
    }
}
