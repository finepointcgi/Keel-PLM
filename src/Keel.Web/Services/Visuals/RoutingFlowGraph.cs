using System.Globalization;
using Keel.Process;
using Keel.Web.Components.Visuals;

namespace Keel.Web.Services.Visuals;

/// <summary>
/// Builds the routing flow diagram of a plan from what the plan view already holds (no new query, ADR-0029, ADR-0042): each routing is a left-to-right chain of operations,
/// alternate routings are further rows. An inspection point is a hexagon with a dotted pattern, so it is told apart by shape and pattern as well as by its word. Nothing here
/// names a component or tool, so the picture carries only what every plan reader may see.
/// </summary>
public static class RoutingFlowGraphBuilder
{
    public static IReadOnlyList<LegendItem> Legend { get; } =
    [
        new("Operation (rectangle)", "part", LegendItem.Rect),
        new("Operation with an inspection point (hexagon, dotted)", "mbom", LegendItem.Hexagon, "dots"),
    ];

    public static ImpactGraph Build(IReadOnlyList<RoutingView> routings, string planNumber, string banner)
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        var href = "process/plans/" + Uri.EscapeDataString(planNumber);
        foreach (var r in routings.OrderByDescending(r => r.IsPrimary).ThenBy(r => r.Code, StringComparer.Ordinal))
        {
            string? previous = null;
            var layer = 0;
            foreach (var o in r.Operations.OrderBy(o => o.Number))
            {
                var id = $"{r.Code}:{o.Number.ToString(CultureInfo.InvariantCulture)}";
                var facts = new List<KeyValuePair<string, string>>
                {
                    new("Routing", $"{r.Code}{(r.IsPrimary ? " (primary)" : "")}"),
                    new("Work center", o.WorkCenter is null ? "none" : $"{o.WorkCenter}{(o.WorkCenterName is null ? "" : " " + o.WorkCenterName)}"),
                    new("Setup", o.SetupMinutes is { } s ? $"{ProcessRules.Fmt(s)} min" : ""),
                    new("Run per unit", o.RunMinutes is { } u ? $"{ProcessRules.Fmt(u)} min" : ""),
                    new("Components", o.Components.Count.ToString(CultureInfo.InvariantCulture)),
                    new("Tools", o.Tools.Count.ToString(CultureInfo.InvariantCulture)),
                    new("Steps", o.Steps.Count.ToString(CultureInfo.InvariantCulture)),
                };
                if (o.Condition is not null) facts.Add(new("Applies when", o.Condition));
                var description = $"Operation {o.Number}, {o.Name}, routing {r.Code}{(o.Inspection ? ", inspection point" : "")}";
                nodes.Add(new GraphNode(id, o.Inspection ? GraphNodeKind.Mbom : GraphNodeKind.Parent, $"Op {o.Number}", $"{o.Name}{(o.WorkCenter is null ? "" : " · " + o.WorkCenter)}", null, layer++, description, null, 0, o.Inspection, facts));
                if (previous is not null) edges.Add(new GraphEdge(previous, id, "then"));
                previous = id;
            }
        }
        _ = href;
        return new ImpactGraph(nodes, edges, false, 0, nodes.Count == 0 ? 0 : nodes.Max(n => n.Layer), banner);
    }
}
