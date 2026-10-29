using System.Globalization;
using Keel.Requirements;
using Keel.Web.Components.Visuals;

namespace Keel.Web.Services.Visuals;

/// <summary>
/// Builds the traceability graph of a specification revision from the trace the service already produced (no new query, ADR-0029, ADR-0038): requirements as nodes, upstream
/// requirements on the left, allocated parts, linked documents and changes on the right. A link whose other end the caller may not read is a restricted node with no label, number or
/// type; a restricted requirement is not drawn at all (it has no readable links). Suspect links and failing requirements are marked by a dotted pattern as well as by colour.
/// </summary>
public static class TraceGraphBuilder
{
    public const string RestrictedLabel = "Restricted item";

    public static IReadOnlyList<LegendItem> Legend { get; } =
    [
        new("Requirement (rounded, thick outline); dotted when a link is suspect or its verification failed", "requirement", LegendItem.Rounded, "dots"),
        new("Allocated part", "tracepart", LegendItem.Rect),
        new("Linked document (cut corner)", "tracedocument", LegendItem.Document),
        new("Linked change (rounded)", "tracechange", LegendItem.Rounded),
        new("Restricted item, you may not see it (hatched)", "restricted", LegendItem.Rect, "hatch"),
    ];

    public static ImpactGraph Build(SpecTrace trace, IReadOnlySet<string>? only = null, int nodeCap = 500)
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        var index = new HashSet<string>(StringComparer.Ordinal);
        var reqs = trace.Rows.Where(r => !r.Redacted && (only is null || only.Contains(r.Key!))).ToList();
        var inSpec = reqs.Select(r => r.Key!).ToHashSet(StringComparer.Ordinal);
        var specHref = "requirements/" + Uri.EscapeDataString(trace.SpecNumber);

        // Layers: a requirement sits one column to the right of the requirements it derives from or refines.
        var upstream = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var l in trace.Links.Where(l => !l.Redacted && l.TargetKind == LinkTargetKinds.Requirement && l.TargetNumber == trace.SpecNumber && inSpec.Contains(l.SourceKey) && inSpec.Contains(l.TargetKey!)))
        {
            if (!upstream.TryGetValue(l.SourceKey, out var list)) upstream[l.SourceKey] = list = [];
            list.Add(l.TargetKey!);
        }
        var layer = reqs.ToDictionary(r => r.Key!, _ => 0, StringComparer.Ordinal);
        for (var pass = 0; pass < reqs.Count; pass++)
        {
            var changed = false;
            foreach (var (k, ups) in upstream)
            {
                var want = ups.Max(u => layer[u]) + 1;
                if (want > layer[k] && want <= reqs.Count) { layer[k] = want; changed = true; }
            }
            if (!changed) break;
        }
        var maxReq = layer.Count == 0 ? 0 : layer.Values.Max();
        var objectLayer = maxReq + 1;

        bool Add(GraphNode n) { if (!index.Add(n.Id)) return false; nodes.Add(n); return true; }
        string State(string key) => trace.Verification.GetValueOrDefault(key, VerificationStates.None);

        foreach (var r in reqs)
        {
            var suspect = trace.Links.Any(l => l.SourceKey == r.Key && !l.Redacted && l.Suspect);
            var failed = State(r.Key!) == VerificationStates.Failed;
            Add(new GraphNode($"r:{r.Key}", GraphNodeKind.Requirement, r.Key!, r.Title, specHref, layer[r.Key!],
                $"Requirement {r.Key}, {r.Title}; status {ReqStatuses.Label(r.Status!)}; verification: {VerificationStates.Label(State(r.Key!))}{(suspect ? "; has a suspect link" : "")}", r.Banner, 0, suspect || failed,
                [new("Type", ReqTypes.Label(r.Type!)), new("Priority", ReqPriorities.Label(r.Priority!)), new("Status", ReqStatuses.Label(r.Status!)), new("Verification", VerificationStates.Label(State(r.Key!)))]));
        }
        var restricted = 0;
        foreach (var l in trace.Links.Where(l => inSpec.Contains(l.SourceKey)))
        {
            var source = $"r:{l.SourceKey}";
            if (l.Redacted)
            {
                var id = $"x:{l.Id:N}";
                if (Add(new GraphNode(id, GraphNodeKind.Restricted, RestrictedLabel, null, null, objectLayer, $"Restricted item linked to {l.SourceKey}", null))) restricted++;
                AddEdge(source, id, "linked");
                continue;
            }
            var label = l.Suspect ? $"{l.Label} · suspect" : l.Label;
            switch (l.TargetKind)
            {
                case LinkTargetKinds.Requirement:
                {
                    var local = l.TargetNumber == trace.SpecNumber;
                    var id = local ? $"r:{l.TargetKey}" : $"r:{l.TargetNumber}/{l.TargetKey}";
                    if (local && !inSpec.Contains(l.TargetKey!)) break; // the other end is not part of this picture
                    if (!local)
                        Add(new GraphNode(id, GraphNodeKind.Requirement, $"{l.TargetNumber} {l.TargetKey}", l.TargetName, "requirements/" + Uri.EscapeDataString(l.TargetNumber!), 0,
                            $"Requirement {l.TargetKey} of {l.TargetNumber}, {l.TargetName}", l.Banner));
                    AddEdge(id, source, label);
                    break;
                }
                case LinkTargetKinds.Part:
                    Add(new GraphNode($"p:{l.TargetNumber}", GraphNodeKind.TracePart, l.TargetNumber!, $"{l.TargetName} · {l.TargetRevision}", "parts/" + Uri.EscapeDataString(l.TargetNumber!), objectLayer,
                        $"Part {l.TargetNumber} {l.TargetName}, revision {l.TargetRevision}", l.Banner, 0, false, [new("Revision", l.TargetRevision ?? "")]));
                    AddEdge(source, $"p:{l.TargetNumber}", label);
                    break;
                case LinkTargetKinds.Document:
                    Add(new GraphNode($"d:{l.TargetNumber}", GraphNodeKind.TraceDocument, l.TargetNumber!, $"{l.TargetName} · {l.TargetRevision}", "documents/" + Uri.EscapeDataString(l.TargetNumber!), objectLayer,
                        $"Document {l.TargetNumber} {l.TargetName}, revision {l.TargetRevision}", l.Banner, 0, false, [new("Revision", l.TargetRevision ?? "")]));
                    AddEdge(source, $"d:{l.TargetNumber}", label);
                    break;
                default:
                    Add(new GraphNode($"c:{l.TargetNumber}", GraphNodeKind.TraceChange, l.TargetNumber!, l.TargetName, "changes/" + Uri.EscapeDataString(l.TargetNumber!), objectLayer,
                        $"Change {l.TargetNumber} {l.TargetName}", l.Banner));
                    AddEdge(source, $"c:{l.TargetNumber}", label);
                    break;
            }
        }
        _ = restricted;

        void AddEdge(string from, string to, string label)
        {
            if (index.Contains(from) && index.Contains(to) && !edges.Any(e => e.From == from && e.To == to && e.Label == label)) edges.Add(new GraphEdge(from, to, label));
        }

        var truncated = nodes.Count > nodeCap;
        if (truncated)
        {
            nodes = [.. nodes.Take(nodeCap)];
            var kept = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            edges = [.. edges.Where(e => kept.Contains(e.From) && kept.Contains(e.To))];
        }
        return new ImpactGraph(nodes, edges, truncated, 0, nodes.Count == 0 ? 0 : nodes.Max(n => n.Layer), trace.Banner);
    }

    internal static string Fmt(int n) => n.ToString(CultureInfo.InvariantCulture);
}
