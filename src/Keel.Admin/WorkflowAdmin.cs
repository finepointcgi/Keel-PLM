using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Keel.Core;

namespace Keel.Admin;

public sealed class SlotDraft
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Role { get; set; } = "plm-approver";
    public int Min { get; set; } = 1;
    public int Max { get; set; } = 1;
}

/// <summary>One node of the structured editor. Which fields apply depends on <see cref="Type"/> (Serial, Parallel, Task or End).</summary>
public sealed class NodeDraft
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "Task";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "review";
    /// <summary>Task: "Slot" (the user picked for a slot) or "Role" (a pool task for a role).</summary>
    public string AssignmentMode { get; set; } = "Slot";
    public string? Slot { get; set; }
    public string? Role { get; set; }
    public int? DueDays { get; set; }
    /// <summary>Parallel: All, Any or NofM.</summary>
    public string Rule { get; set; } = "All";
    public int RuleN { get; set; } = 2;
    /// <summary>Serial: step node ids, in order. Parallel with branches: branch node ids.</summary>
    public List<string> Steps { get; set; } = [];
    public string? FanOutSlot { get; set; }
    public string FanOutKind { get; set; } = "approval";
    public string Outcome { get; set; } = "Release";
}

/// <summary>The structured form of a workflow template. Business rules stay in the engine's parser; this only converts to and from the JSON it reads.</summary>
public sealed class TemplateDraft
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string ObjectType { get; set; } = "part";
    public string Start { get; set; } = "";
    public List<SlotDraft> Slots { get; set; } = [];
    public List<NodeDraft> Nodes { get; set; } = [];

    public JsonObject ToJson()
    {
        var root = new JsonObject { ["id"] = Id, ["name"] = Name };
        if (!string.IsNullOrWhiteSpace(Description)) root["description"] = Description;
        root["objectType"] = ObjectType;
        root["slots"] = new JsonArray([.. Slots.Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["label"] = s.Label, ["role"] = s.Role, ["min"] = s.Min, ["max"] = s.Max })]);
        root["start"] = Start;
        root["nodes"] = new JsonArray([.. Nodes.Select(NodeJson)]);
        return root;
    }

    private static JsonNode NodeJson(NodeDraft n)
    {
        var o = new JsonObject { ["id"] = n.Id, ["type"] = n.Type, ["name"] = n.Name };
        switch (n.Type)
        {
            case "Task":
                o["kind"] = n.Kind;
                o["assignment"] = n.AssignmentMode == "Role"
                    ? new JsonObject { ["mode"] = "Role", ["role"] = n.Role }
                    : new JsonObject { ["mode"] = "Slot", ["slot"] = n.Slot };
                if (n.DueDays is { } d) o["dueDays"] = d;
                break;
            case "Serial":
                o["steps"] = new JsonArray([.. n.Steps.Select(s => (JsonNode)JsonValue.Create(s)!)]);
                break;
            case "Parallel":
                o["rule"] = n.Rule == "NofM" ? new JsonObject { ["type"] = "NofM", ["n"] = n.RuleN } : new JsonObject { ["type"] = n.Rule };
                if (n.FanOutSlot is { Length: > 0 }) o["fanOut"] = new JsonObject { ["slot"] = n.FanOutSlot, ["kind"] = n.FanOutKind };
                else o["branches"] = new JsonArray([.. n.Steps.Select(s => (JsonNode)JsonValue.Create(s)!)]);
                if (n.DueDays is { } pd) o["dueDays"] = pd;
                break;
            case "End":
                o["outcome"] = n.Outcome;
                break;
        }
        return o;
    }

    /// <summary>Reads a template definition; null with a message when the JSON is not in the shape the editor can show (use the JSON view then).</summary>
    public static (TemplateDraft? Draft, string? Error) FromJson(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return (null, "The definition must be a JSON object.");
            var d = new TemplateDraft
            {
                Id = o["id"]?.GetValue<string>() ?? "", Name = o["name"]?.GetValue<string>() ?? "", Description = o["description"]?.GetValue<string>(),
                ObjectType = o["objectType"]?.GetValue<string>() ?? "part", Start = o["start"]?.GetValue<string>() ?? "",
            };
            foreach (var s in o["slots"]?.AsArray() ?? [])
                d.Slots.Add(new SlotDraft { Name = s!["name"]!.GetValue<string>(), Label = s["label"]!.GetValue<string>(), Role = s["role"]!.GetValue<string>(), Min = s["min"]!.GetValue<int>(), Max = s["max"]!.GetValue<int>() });
            foreach (var n in o["nodes"]?.AsArray() ?? [])
            {
                var node = new NodeDraft { Id = n!["id"]!.GetValue<string>(), Type = n["type"]!.GetValue<string>(), Name = n["name"]?.GetValue<string>() ?? "" };
                switch (node.Type)
                {
                    case "Task":
                        node.Kind = n["kind"]?.GetValue<string>() ?? "review";
                        node.AssignmentMode = n["assignment"]?["mode"]?.GetValue<string>() ?? "Slot";
                        node.Slot = n["assignment"]?["slot"]?.GetValue<string>();
                        node.Role = n["assignment"]?["role"]?.GetValue<string>();
                        node.DueDays = n["dueDays"]?.GetValue<int>();
                        break;
                    case "Serial":
                        node.Steps = [.. n["steps"]?.AsArray().Select(x => x!.GetValue<string>()) ?? []];
                        break;
                    case "Parallel":
                        node.Rule = n["rule"]?["type"]?.GetValue<string>() ?? "All";
                        node.RuleN = n["rule"]?["n"]?.GetValue<int>() ?? 2;
                        node.FanOutSlot = n["fanOut"]?["slot"]?.GetValue<string>();
                        node.FanOutKind = n["fanOut"]?["kind"]?.GetValue<string>() ?? "approval";
                        node.Steps = [.. n["branches"]?.AsArray().Select(x => x!.GetValue<string>()) ?? []];
                        node.DueDays = n["dueDays"]?.GetValue<int>();
                        break;
                    case "End":
                        node.Outcome = n["outcome"]?.GetValue<string>() ?? "Release";
                        break;
                    default:
                        return (null, $"Node '{node.Id}' has the type '{node.Type}', which the structured editor does not show. Use the JSON view.");
                }
                d.Nodes.Add(node);
            }
            return (d, null);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NullReferenceException)
        {
            return (null, "The definition is not in a shape the structured editor can show. Use the JSON view.");
        }
    }
}

/// <summary>Human-readable difference between two template versions.</summary>
public static class TemplateDiff
{
    public static IReadOnlyList<string> Compare(JsonNode from, JsonNode to)
    {
        var lines = new List<string>();
        foreach (var key in new[] { "name", "description", "objectType", "start" })
        {
            var a = from[key]?.ToJsonString();
            var b = to[key]?.ToJsonString();
            if (a != b) lines.Add($"{key}: {a ?? "(none)"} → {b ?? "(none)"}");
        }
        Compare(lines, "slot", Index(from["slots"], "name"), Index(to["slots"], "name"));
        Compare(lines, "node", Index(from["nodes"], "id"), Index(to["nodes"], "id"));
        return lines;
    }

    private static Dictionary<string, JsonNode> Index(JsonNode? array, string key) =>
        (array as JsonArray)?.Where(n => n?[key] is not null).ToDictionary(n => n![key]!.GetValue<string>(), n => n!, StringComparer.Ordinal) ?? [];

    private static void Compare(List<string> lines, string what, Dictionary<string, JsonNode> a, Dictionary<string, JsonNode> b)
    {
        foreach (var k in b.Keys.Except(a.Keys).Order(StringComparer.Ordinal)) lines.Add($"Added {what} '{k}'.");
        foreach (var k in a.Keys.Except(b.Keys).Order(StringComparer.Ordinal)) lines.Add($"Removed {what} '{k}'.");
        foreach (var k in a.Keys.Intersect(b.Keys).Order(StringComparer.Ordinal))
        {
            var x = (JsonObject)a[k];
            var y = (JsonObject)b[k];
            foreach (var prop in x.Select(p => p.Key).Union(y.Select(p => p.Key)).Order(StringComparer.Ordinal))
            {
                var before = Canon(x[prop]);
                var after = Canon(y[prop]);
                if (before != after) lines.Add($"Changed {what} '{k}', {prop}: {before ?? "(none)"} → {after ?? "(none)"}.");
            }
        }
    }

    private static string? Canon(JsonNode? n) => n is null ? null : CanonicalJson.Serialize(n);
}

/// <summary>Canonical JSON for hashing and comparison: keys sorted by ordinal comparison, no insignificant whitespace.</summary>
public static class CanonicalJson
{
    public static string Serialize(JsonNode? node)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream)) Write(w, node);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter w, JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
                w.WriteStartObject();
                foreach (var (k, v) in o.OrderBy(p => p.Key, StringComparer.Ordinal)) { w.WritePropertyName(k); Write(w, v); }
                w.WriteEndObject();
                break;
            case JsonArray a:
                w.WriteStartArray();
                foreach (var item in a) Write(w, item);
                w.WriteEndArray();
                break;
            case null: w.WriteNullValue(); break;
            default: n.WriteTo(w); break;
        }
    }
}

/// <summary>Template management for the UI and API: everything the engine owns goes through <see cref="IWorkflowTemplateAdmin"/>.</summary>
public sealed class WorkflowAdminService(IWorkflowTemplateAdmin templates)
{
    public IReadOnlyList<string> ReviewableObjectTypes => templates.ReviewableObjectTypes;

    public Task<Outcome<IReadOnlyList<TemplateVersionInfo>>> ListAsync(Subject subject, bool allVersions, string? correlationId, CancellationToken ct = default) =>
        templates.ListVersionsAsync(subject, allVersions, correlationId, ct);

    public Task<Outcome<TemplateVersionInfo>> ValidateAsync(Subject subject, string definitionJson, CancellationToken ct = default) =>
        templates.ValidateAsync(subject, definitionJson, ct);

    public Task<Outcome<TemplateVersionInfo>> PublishAsync(Subject subject, string definitionJson, string? correlationId, CancellationToken ct = default) =>
        templates.PublishAsync(subject, definitionJson, correlationId, ct);

    public Task<Outcome<IReadOnlyList<TemplateDraftInfo>>> ListDraftsAsync(Subject subject, CancellationToken ct = default) => templates.ListDraftsAsync(subject, ct);

    public Task<Outcome<TemplateDraftInfo>> SaveDraftAsync(Subject subject, string templateId, int baseVersion, string definitionJson, string? correlationId, CancellationToken ct = default) =>
        templates.SaveDraftAsync(subject, templateId, baseVersion, definitionJson, correlationId, ct);

    public Task<Outcome<bool>> DiscardDraftAsync(Subject subject, string templateId, string? correlationId, CancellationToken ct = default) =>
        templates.DiscardDraftAsync(subject, templateId, correlationId, ct);

    public Task<Outcome<IReadOnlyList<SignatureMeaning>>> ListSignatureMeaningsAsync(Subject subject, CancellationToken ct = default) => templates.ListSignatureMeaningsAsync(subject, ct);

    public Task<Outcome<IReadOnlyList<TemplatePin>>> ListPinsAsync(Subject subject, CancellationToken ct = default) => templates.ListPinsAsync(subject, ct);

    public Task<Outcome<TemplatePin?>> PinAsync(Subject subject, string objectType, string? templateId, string? correlationId, CancellationToken ct = default) =>
        templates.PinAsync(subject, objectType, templateId, correlationId, ct);

    /// <summary>Differences between two versions of one template, or between a version and a draft (version 0 reads the draft text).</summary>
    public async Task<Outcome<IReadOnlyList<string>>> DiffAsync(Subject subject, string id, int fromVersion, int toVersion, CancellationToken ct = default)
    {
        var all = await templates.ListVersionsAsync(subject, true, null, ct);
        if (!all.IsOk) return Outcome<IReadOnlyList<string>>.Forbidden(all.Errors);
        var a = all.Value!.FirstOrDefault(t => t.Id == id && t.Version == fromVersion);
        var b = all.Value!.FirstOrDefault(t => t.Id == id && t.Version == toVersion);
        return a is null || b is null ? Outcome<IReadOnlyList<string>>.NotFound() : Outcome<IReadOnlyList<string>>.Ok(TemplateDiff.Compare(a.Definition, b.Definition));
    }
}
