using System.Globalization;
using Keel.Change;
using Keel.Core;

namespace Keel.Web.Services.Visuals;

public enum GraphNodeKind { Change, Affected, Parent, Document, Conflict, Mbom, Restricted, More }

/// <summary>
/// A node of the impact map. A <see cref="GraphNodeKind.Restricted"/> node carries no number, name, banner or link: <see cref="Label"/> is the fixed text
/// "Restricted item" and <see cref="Description"/> only says where it sits in the structure (the same position the impact tables already show).
/// </summary>
public sealed record GraphNode(
    string Id, GraphNodeKind Kind, string Label, string? Sublabel, string? Href, int Layer, string Description, string? Banner,
    int Level = 0, bool Highlight = false, IReadOnlyList<KeyValuePair<string, string>>? Facts = null)
{
    public bool Openable => Href is not null;
    public bool IsRestricted => Kind == GraphNodeKind.Restricted;
}

public sealed record GraphEdge(string From, string To, string Label);

/// <summary>
/// The impact graph ready to lay out. <see cref="Truncated"/>: more than <see cref="ImpactGraphOptions.NodeCap"/> nodes were requested and the outermost were not
/// drawn (no count is given, because a count could include items the viewer may not see). <see cref="MoreParents"/> is the number of READABLE parents folded into the
/// "more" node; restricted parents folded there are not counted (VIS-4).
/// </summary>
public sealed record ImpactGraph(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges, bool Truncated, int MoreParents, int MaxLevel, string Banner);

public sealed record ImpactGraphOptions(int ShowLevels = 3, IReadOnlySet<GraphNodeKind>? HiddenKinds = null, bool OnlyConflicts = false, int NodeCap = 500);

/// <summary>
/// Builds the impact graph from the impact analysis the service already produced (no new query, ADR-0029): the change at the left, its affected part revisions,
/// the where-used parents by level, linked documents, stale manufacturing BOMs and other open changes that touch the same objects. Restricted items stay
/// placeholders exactly where the service returned them. The banner is the union of the banners of the nodes that are drawn.
/// </summary>
public static class ImpactGraphBuilder
{
    public const string RestrictedLabel = "Restricted item";

    public static ImpactGraph Build(ChangeDetail change, ImpactReport report, ImpactGraphOptions? options = null)
    {
        options ??= new ImpactGraphOptions();
        var hidden = options.HiddenKinds ?? new HashSet<GraphNodeKind>();
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var parentLevels = new Dictionary<string, (int Min, int Max)>(StringComparer.Ordinal);

        GraphNode Add(GraphNode node)
        {
            if (index.TryGetValue(node.Id, out var i)) return nodes[i];
            index[node.Id] = nodes.Count;
            nodes.Add(node);
            return node;
        }
        void Link(string from, string to, string label)
        {
            if (!edges.Any(e => e.From == from && e.To == to && e.Label == label)) edges.Add(new GraphEdge(from, to, label));
        }

        var centre = Add(new GraphNode("chg", GraphNodeKind.Change, change.Number, change.Title, ChangeUi(change.Number), 0,
            $"{ChangeTypeName(change.Type)} {change.Number}: {change.Title}, {change.StateLabel}", change.Banner, 0, false,
            [new("Type", ChangeTypeName(change.Type)), new("State", change.StateLabel), new("Priority", change.Priority)]));

        var affectedViews = change.Affected.ToDictionary(a => a.Id);
        var objIndex = 0;
        var maxLevel = 0;
        foreach (var o in report.Objects)
        {
            affectedViews.TryGetValue(o.AffectedId, out var view);
            var action = ActionLabel(o.Action);
            if (o.Redacted)
            {
                var id = $"ar:{o.AffectedId:N}";
                Add(new GraphNode(id, GraphNodeKind.Restricted, RestrictedLabel, null, null, 1, $"Restricted affected item, {action}", null));
                Link("chg", id, action);
                objIndex++;
                continue;
            }
            var aid = $"a:{o.Key}";
            var resulting = view?.ResultingRevision;
            var revText = resulting is null ? $"revision {o.Revision}" : $"revision {o.Revision} to {resulting}";
            Add(new GraphNode(aid, GraphNodeKind.Affected, o.Key!, $"{o.Name} · {(resulting is null ? o.Revision : $"{o.Revision} → {resulting}")}", $"parts/{Uri.EscapeDataString(o.Key!)}", 1,
                $"Affected part {o.Key} {o.Name}, {revText}, {action}", o.Banner, 0, false,
                [new("Revision", resulting is null ? o.Revision! : $"{o.Revision} to {resulting}"), new("Action", action), new("State", view?.State ?? "")]));
            Link("chg", aid, view?.Effectivity is { } eff ? $"{action} · effective {eff}" : action);

            var n = 0;
            foreach (var w in o.WhereUsed)
            {
                maxLevel = Math.Max(maxLevel, w.Level);
                var usedId = w.Level == 1 || w.UsedKey is null ? aid : $"p:{w.UsedKey}";
                var edgeLabel = $"{Qty(w.Quantity)} {w.Unit} · find {w.FindNumber}{(w.Effectivity is null ? "" : $" · {w.Effectivity}")}";
                string pid;
                if (w.Redacted)
                {
                    pid = $"pr:{objIndex}:{n++}";
                    Add(new GraphNode(pid, GraphNodeKind.Restricted, RestrictedLabel, null, null, 1 + w.Level, $"Restricted assembly, level {w.Level} where used", null, w.Level));
                }
                else
                {
                    pid = $"p:{w.ParentKey}";
                    parentLevels[pid] = parentLevels.TryGetValue(pid, out var lv) ? (Math.Min(lv.Min, w.Level), Math.Max(lv.Max, w.Level)) : (w.Level, w.Level);
                    Add(new GraphNode(pid, GraphNodeKind.Parent, w.ParentKey!, $"{w.ParentName} · {w.ParentRevision}", $"parts/{Uri.EscapeDataString(w.ParentKey!)}", 1 + w.Level,
                        $"Assembly {w.ParentKey} {w.ParentName}, revision {w.ParentRevision}, {w.ParentState}, uses the affected part at level {w.Level}", w.Banner, w.Level, false,
                        [new("Revision", w.ParentRevision ?? ""), new("State", w.ParentState ?? ""), new("Level", w.Level.ToString(CultureInfo.InvariantCulture))]));
                }
                Link(usedId, pid, edgeLabel);
            }

            var d = 0;
            foreach (var doc in o.Documents)
            {
                if (doc.Redacted)
                {
                    var rid = $"dr:{objIndex}:{d++}";
                    Add(new GraphNode(rid, GraphNodeKind.Restricted, RestrictedLabel, null, null, 2, $"Restricted document, {doc.LinkType}", null));
                    Link(aid, rid, doc.LinkType);
                }
                else
                {
                    var did = $"d:{doc.Number}";
                    Add(new GraphNode(did, GraphNodeKind.Document, doc.Number!, $"{doc.Name} · {doc.Revision}", $"documents/{Uri.EscapeDataString(doc.Number!)}", 2,
                        $"Document {doc.Number} {doc.Name}, revision {doc.Revision}, {doc.State}, {doc.LinkType} the part", doc.Banner, 0, false,
                        [new("Type", doc.DocumentType ?? ""), new("Revision", doc.Revision ?? ""), new("State", doc.State ?? "")]));
                    Link(aid, did, doc.LinkType);
                }
            }

            foreach (var m in o.StaleMboms ?? [])
            {
                var mid = $"m:{m.MbomNumber}";
                Add(new GraphNode(mid, GraphNodeKind.Mbom, m.MbomNumber, $"Plant {m.Plant} · {m.Revision}.{m.Iteration}", $"mboms/{Uri.EscapeDataString(m.MbomNumber)}", 2,
                    $"Manufacturing BOM {m.MbomNumber} for plant {m.Plant}, {m.StateLabel}, derived from EBOM {m.DerivedFrom}; {(m.StaleNow ? $"the EBOM is now at {m.CurrentEbom}" : "stale once the revision exists")}",
                    m.Banner, 0, true, [new("Plant", m.Plant), new("Derived from", m.DerivedFrom), new("EBOM now", m.CurrentEbom)]));
                Link(aid, mid, "stale when revised");
            }
            for (var h = 0; h < o.HiddenMboms; h++)
            {
                var rid = $"mr:{objIndex}:{h}";
                Add(new GraphNode(rid, GraphNodeKind.Restricted, RestrictedLabel, null, null, 2, "Restricted manufacturing BOM", null));
                Link(aid, rid, "stale when revised");
            }
            objIndex++;
        }

        // Other open changes touching the same objects: attached to the node they touch (an affected part or a readable parent), one layer to its right.
        foreach (var c in report.Conflicts)
        {
            var cid = $"c:{c.ChangeNumber}";
            var target = c.ObjectKey is null ? null : nodes.FirstOrDefault(n => n.Id == $"a:{c.ObjectKey}" || n.Id == $"p:{c.ObjectKey}");
            var layer = target is null ? 2 : target.Layer + 1;
            Add(new GraphNode(cid, GraphNodeKind.Conflict, c.ChangeNumber, $"{c.Title} · {c.StateLabel}", $"changes/{Uri.EscapeDataString(c.ChangeNumber)}", layer,
                $"Conflict: open {c.ChangeType} {c.ChangeNumber} {c.Title}, {c.StateLabel}, {c.Kind}", c.Banner, 0, true,
                [new("Type", c.ChangeType), new("State", c.StateLabel), new("Touches", c.Kind)]));
            if (target is not null) Link(target.Id, cid, c.Kind);
            else foreach (var a in nodes.Where(n => n.Kind == GraphNodeKind.Affected).ToList()) Link(a.Id, cid, c.Kind);
        }

        // Filters. Levels first: parents deeper than ShowLevels fold into one "more" node, which counts readable parents only.
        var visibleLevels = Math.Max(1, options.ShowLevels);
        var folded = nodes.Where(n => n.Kind == GraphNodeKind.Parent && parentLevels[n.Id].Min > visibleLevels).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var foldedRestricted = nodes.Where(n => n.Kind == GraphNodeKind.Restricted && n.Id.StartsWith("pr:", StringComparison.Ordinal) && n.Level > visibleLevels).Select(n => n.Id);
        folded.UnionWith(foldedRestricted);
        var moreReadable = nodes.Count(n => n.Kind == GraphNodeKind.Parent && folded.Contains(n.Id));
        // A conflict attached only to folded parents folds with them (it would otherwise hang loose in the picture).
        foreach (var c in nodes.Where(n => n.Kind == GraphNodeKind.Conflict))
        {
            var incoming = edges.Where(e => e.To == c.Id).ToList();
            if (incoming.Count > 0 && incoming.All(e => folded.Contains(e.From))) folded.Add(c.Id);
        }

        var keep = nodes.Where(n => !folded.Contains(n.Id) && !hidden.Contains(n.Kind) || n.Kind == GraphNodeKind.Change).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        if (options.OnlyConflicts)
        {
            var conflictIds = nodes.Where(n => n.Kind == GraphNodeKind.Conflict).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            var path = new HashSet<string>(conflictIds, StringComparer.Ordinal) { "chg" };
            bool grew;
            do
            {
                grew = false;
                foreach (var e in edges)
                    if (path.Contains(e.To) && keep.Contains(e.From) && path.Add(e.From)) grew = true;
            } while (grew);
            keep.IntersectWith(path);
        }

        var drawn = nodes.Where(n => keep.Contains(n.Id)).ToList();
        var drawnEdges = edges.Where(e => keep.Contains(e.From) && keep.Contains(e.To)).ToList();

        if (moreReadable > 0 && !options.OnlyConflicts && !hidden.Contains(GraphNodeKind.Parent))
        {
            var edgeSources = edges.Where(e => folded.Contains(e.To) && keep.Contains(e.From)).Select(e => e.From).Distinct().ToList();
            var more = new GraphNode("more", GraphNodeKind.More, $"{moreReadable} more", $"levels {visibleLevels + 1} to {maxLevel}", null, visibleLevels + 2,
                $"{moreReadable} more assemblies at levels {visibleLevels + 1} to {maxLevel}, not shown yet (restricted ones are not counted); activate to show one more level", null);
            drawn.Add(more);
            foreach (var from in edgeSources) drawnEdges.Add(new GraphEdge(from, "more", "more levels"));
        }

        var truncated = false;
        if (drawn.Count > options.NodeCap)
        {
            truncated = true;
            var ordered = drawn.Select((n, i) => (n, i)).OrderBy(x => x.n.Kind == GraphNodeKind.Change ? -1 : x.n.Layer).ThenBy(x => x.i).Take(options.NodeCap).Select(x => x.n.Id).ToHashSet(StringComparer.Ordinal);
            drawn = [.. drawn.Where(n => ordered.Contains(n.Id))];
            drawnEdges = [.. drawnEdges.Where(e => ordered.Contains(e.From) && ordered.Contains(e.To))];
        }

        var banner = BannerMerge.Union([.. drawn.Select(n => n.Banner)]);
        return new ImpactGraph(drawn, drawnEdges, truncated, moreReadable, maxLevel, banner);
    }

    private static string ChangeUi(string number) => $"changes/{Uri.EscapeDataString(number)}";

    private static string ChangeTypeName(string type) => type switch { "ECN" => "Change notice", "ECR" => "Change request", "PR" => "Problem report", _ => type };

    public static string ActionLabel(string action) => action switch { "revise" => "Revise", "obsolete" => "Obsolete", _ => "Reference only" };

    private static string Qty(decimal d) => d.ToString("0.######", CultureInfo.InvariantCulture);
}
