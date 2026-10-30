namespace Keel.Cad.Structure;

/// <summary>
/// One placed instance in the expanded assembly: a path from the root, the part, and where it sits (<see cref="World"/> maps the part's own coordinates to the
/// coordinates of the root, row-major as in <see cref="StructureOccurrence"/>). <see cref="Placed"/> is false when the file carries no placement for the occurrence;
/// the instance then sits at its parent's origin and is drawn as such, never at an invented position.
/// </summary>
public sealed record PlacedInstance(
    int Index, int ParentIndex, int Depth, string Path, string Ref, string? Number, string Name, string OccurrenceId, bool IsAssembly, bool Placed,
    IReadOnlyList<double> World, BoundingBox? LocalBox, BoundingBox? WorldBox);

public sealed record AssemblyLayout(
    IReadOnlyList<PlacedInstance> Instances, BoundingBox? WorldBox, bool Truncated, int Unplaced, string? LengthUnit, double? LengthUnitMm);

/// <summary>Expands a structure into placed instances (a depth-first walk with no recursion; bounded by <paramref name="maxInstances"/> and <see cref="StructureLimits.MaxDepth"/>).</summary>
public static class AssemblyLayouts
{
    public const int DefaultMaxInstances = 5_000;

    public static readonly IReadOnlyList<double> Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    public static AssemblyLayout Expand(StructureModel model, int maxInstances = DefaultMaxInstances)
    {
        var parts = model.Parts.ToDictionary(p => p.Ref, StringComparer.Ordinal);
        // children of each parent: occurrences when the file has them, otherwise usage lines (one unplaced instance per piece)
        var children = new Dictionary<string, List<StructureOccurrence>>(StringComparer.Ordinal);
        if (model.Occurrences.Count > 0)
        {
            foreach (var o in model.Occurrences)
            {
                if (!children.TryGetValue(o.ParentRef, out var list)) children[o.ParentRef] = list = [];
                list.Add(o);
            }
        }
        else
        {
            foreach (var l in model.Lines)
            {
                if (!children.TryGetValue(l.ParentRef, out var list)) children[l.ParentRef] = list = [];
                var count = (int)Math.Min(Math.Max(1m, Math.Ceiling(l.Quantity)), 1000m);
                for (var i = 0; i < count; i++) list.Add(new StructureOccurrence(l.ParentRef, l.ChildRef, l.FindNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), null, null));
            }
        }

        var instances = new List<PlacedInstance>();
        var truncated = false;
        var unplaced = 0;
        var root = parts[model.RootRef];
        instances.Add(new PlacedInstance(0, -1, 0, "0", root.Ref, root.Number, root.Name, "", children.ContainsKey(root.Ref), true, Identity, root.Box, null));
        var stack = new Stack<(int Index, int Next)>();
        stack.Push((0, 0));
        var worlds = new List<double[]> { Identity.ToArray() };
        while (stack.Count > 0)
        {
            var (index, next) = stack.Pop();
            var node = instances[index];
            if (!children.TryGetValue(node.Ref, out var kids) || next >= kids.Count || node.Depth >= StructureLimits.MaxDepth) continue;
            stack.Push((index, next + 1));
            var o = kids[next];
            if (!parts.TryGetValue(o.ChildRef, out var child)) continue;
            if (instances.Count >= maxInstances) { truncated = true; stack.Clear(); break; }
            var placed = o.Transform is { Count: 16 };
            if (!placed) unplaced++;
            var world = placed ? StepGeometry.Multiply(worlds[index], [.. o.Transform!]) : worlds[index];
            var i = instances.Count;
            instances.Add(new PlacedInstance(i, index, node.Depth + 1, node.Path + "/" + (next + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), child.Ref, child.Number, child.Name,
                o.OccurrenceId, children.ContainsKey(child.Ref), placed, world, child.Box, null));
            worlds.Add(world);
            stack.Push((i, 0));
        }

        // world boxes: own geometry first, then every assembly is the union of what it holds (children always have a higher index than their parent)
        var boxes = new BoundingBox?[instances.Count];
        for (var i = 0; i < instances.Count; i++) boxes[i] = instances[i].LocalBox is { } b ? Transform(b, worlds[i]) : null;
        for (var i = instances.Count - 1; i > 0; i--)
        {
            var p = instances[i].ParentIndex;
            boxes[p] = Union(boxes[p], boxes[i]);
        }
        var result = new List<PlacedInstance>(instances.Count);
        for (var i = 0; i < instances.Count; i++) result.Add(instances[i] with { WorldBox = boxes[i] });
        return new AssemblyLayout(result, boxes[0], truncated, unplaced, model.LengthUnit, model.LengthUnitMm);
    }

    /// <summary>The axis-aligned box of the eight corners of <paramref name="b"/> after <paramref name="m"/>.</summary>
    public static BoundingBox Transform(BoundingBox b, IReadOnlyList<double> m)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        foreach (var x in new[] { b.MinX, b.MaxX })
            foreach (var y in new[] { b.MinY, b.MaxY })
                foreach (var z in new[] { b.MinZ, b.MaxZ })
                {
                    var px = m[0] * x + m[1] * y + m[2] * z + m[3];
                    var py = m[4] * x + m[5] * y + m[6] * z + m[7];
                    var pz = m[8] * x + m[9] * y + m[10] * z + m[11];
                    minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
                    minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
                    minZ = Math.Min(minZ, pz); maxZ = Math.Max(maxZ, pz);
                }
        return new BoundingBox(Round(minX), Round(minY), Round(minZ), Round(maxX), Round(maxY), Round(maxZ));
    }

    private static double Round(double v) { var r = Math.Round(v, 6); return r == 0 ? 0 : r; }

    private static BoundingBox? Union(BoundingBox? a, BoundingBox? b) =>
        a is null ? b : b is null ? a
        : new BoundingBox(Math.Min(a.MinX, b.MinX), Math.Min(a.MinY, b.MinY), Math.Min(a.MinZ, b.MinZ), Math.Max(a.MaxX, b.MaxX), Math.Max(a.MaxY, b.MaxY), Math.Max(a.MaxZ, b.MaxZ));
}
