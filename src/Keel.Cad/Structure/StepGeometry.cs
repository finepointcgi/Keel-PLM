using System.Globalization;
using static Keel.Cad.Structure.StepStructureReader;

namespace Keel.Cad.Structure;

/// <summary>
/// Placements and bounding boxes of a STEP file (ADR-0052, A67-A69), collected by <see cref="StepStructureReader"/> in the same pass as the product structure.
/// <para>
/// A placement is the chain <c>NEXT_ASSEMBLY_USAGE_OCCURRENCE ← PRODUCT_DEFINITION_SHAPE ← CONTEXT_DEPENDENT_SHAPE_REPRESENTATION → (REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION)
/// → ITEM_DEFINED_TRANSFORMATION(item_1, item_2) → AXIS2_PLACEMENT_3D</c>. The transformation maps the frame <c>item_1</c> onto the frame <c>item_2</c>; which one lives in the
/// child and which in the parent is decided by the two shape representations of the relationship, matched to the product definitions through
/// <c>SHAPE_DEFINITION_REPRESENTATION</c> (Open CASCADE writes the child as <c>rep_1</c>, other writers the opposite). Child-to-parent is then
/// <c>M(parent frame) · M(child frame)⁻¹</c>. If the representations cannot be matched to the usage, the occurrence has no placement; nothing is guessed (A67).
/// </para>
/// <para>
/// A box is derived from the part's own B-rep: vertex points, the exact extent of circular arcs between their vertices, ellipses and B-spline control points. It is a
/// viewing aid, not a measurement (A68). Everything is iterative and bounded: hostile files cannot recurse or exhaust memory.
/// </para>
/// </summary>
internal sealed class StepGeometry
{
    /// <summary>Entities of topology and curves kept (beyond this many, boxes are not derived and say so by being absent).</summary>
    public const int MaxGeometryEntities = 3_000_000;
    private const int MaxPoints = 4_000_000;

    private static readonly HashSet<string> Topology = new(StringComparer.OrdinalIgnoreCase)
    {
        "MANIFOLD_SOLID_BREP", "BREP_WITH_VOIDS", "FACETED_BREP", "CLOSED_SHELL", "OPEN_SHELL", "ORIENTED_CLOSED_SHELL", "ORIENTED_OPEN_SHELL", "ADVANCED_FACE", "FACE_SURFACE",
        "FACE_BOUND", "FACE_OUTER_BOUND", "EDGE_LOOP", "POLY_LOOP", "ORIENTED_EDGE", "EDGE_CURVE", "VERTEX_POINT", "SHELL_BASED_SURFACE_MODEL", "CIRCLE", "ELLIPSE",
        "B_SPLINE_CURVE_WITH_KNOTS",
    };

    private static readonly HashSet<string> Core = new(StringComparer.OrdinalIgnoreCase)
    {
        "SHAPE_DEFINITION_REPRESENTATION", "PRODUCT_DEFINITION_SHAPE", "CONTEXT_DEPENDENT_SHAPE_REPRESENTATION", "ITEM_DEFINED_TRANSFORMATION",
        "REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION", "AXIS2_PLACEMENT_3D", "CARTESIAN_POINT", "DIRECTION",
    };

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> TopologyLookup = Topology.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> CoreLookup = Core.GetAlternateLookup<ReadOnlySpan<char>>();

    public static bool Wants(ReadOnlySpan<char> keyword) =>
        CoreLookup.Contains(keyword) || TopologyLookup.Contains(keyword) || keyword.EndsWith("SHAPE_REPRESENTATION", StringComparison.OrdinalIgnoreCase);

    private readonly Dictionary<int, (double X, double Y, double Z)> _points = [];
    private readonly Dictionary<int, (double X, double Y, double Z)> _directions = [];
    private readonly Dictionary<int, (int Point, int? Axis, int? RefDirection)> _axes = [];
    private readonly Dictionary<int, (string Name, List<object?> Args)> _entities = [];
    private readonly Dictionary<int, int> _pdsDefinition = [];
    private readonly List<(int Pds, int Rep)> _definitionReps = [];
    private readonly List<(int Relationship, int Pds)> _contextShapes = [];
    private readonly Dictionary<int, (int A, int B)> _transformations = [];
    private readonly Dictionary<int, (int Rep1, int Rep2, int? Transformation)> _relationships = [];
    private bool _truncated;
    private string? _unit;
    private double? _unitMm;
    private bool _unitConflict;

    public string? UnitLabel => _unitConflict ? null : _unit;
    public double? UnitMm => _unitConflict ? null : _unitMm;

    public void Add(int id, string name, List<object?> args)
    {
        switch (name)
        {
            case "CARTESIAN_POINT":
                if (_points.Count >= MaxPoints) _truncated = true;
                else if (Vector(args, 1) is { } p) _points[id] = p;
                return;
            case "DIRECTION":
                if (Vector(args, 1) is { } d) _directions[id] = d;
                return;
            case "AXIS2_PLACEMENT_3D":
                if (RefAt(args, 1) is { } loc) _axes[id] = (loc, RefAt(args, 2), RefAt(args, 3));
                return;
            case "SHAPE_DEFINITION_REPRESENTATION":
                if (RefAt(args, 0) is { } pds && RefAt(args, 1) is { } rep) _definitionReps.Add((pds, rep));
                return;
            case "PRODUCT_DEFINITION_SHAPE":
                if (RefAt(args, 2) is { } def) _pdsDefinition[id] = def;
                return;
            case "CONTEXT_DEPENDENT_SHAPE_REPRESENTATION":
                if (RefAt(args, 0) is { } rr && RefAt(args, 1) is { } pds2) _contextShapes.Add((rr, pds2));
                return;
            case "ITEM_DEFINED_TRANSFORMATION":
                if (RefAt(args, 2) is { } a && RefAt(args, 3) is { } b) _transformations[id] = (a, b);
                return;
            case "REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION":
                if (RefAt(args, 2) is { } r1 && RefAt(args, 3) is { } r2) _relationships[id] = (r1, r2, RefAt(args, 4));
                return;
        }
        if (_entities.Count >= MaxGeometryEntities) { _truncated = true; return; }
        _entities[id] = (name, args);
    }

    public void AddRelationship(int id, Dictionary<string, List<object?>>? parts)
    {
        if (parts is null || !parts.TryGetValue("REPRESENTATION_RELATIONSHIP", out var rel) || !parts.TryGetValue("REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION", out var with)) return;
        if (RefAt(rel, 2) is { } r1 && RefAt(rel, 3) is { } r2) _relationships[id] = (r1, r2, RefAt(with, 0));
    }

    /// <summary>The length unit of the file: <c>SI_UNIT(.MILLI.,.METRE.)</c> or a conversion based unit named inch or foot. Two different units in one file leave the unit unknown.</summary>
    public void AddLengthUnit(Dictionary<string, List<object?>>? parts)
    {
        if (parts is null) return;
        string? label = null;
        double? mm = null;
        if (parts.TryGetValue("SI_UNIT", out var si) && si.Count > 1 && si[1] is Enumeration { Name: "METRE" })
        {
            var prefix = si[0] is Enumeration e ? e.Name : "";
            (label, mm) = prefix switch
            {
                "" => ((string?)"m", (double?)1000d), "MILLI" => ("mm", 1d), "CENTI" => ("cm", 10d), "DECI" => ("dm", 100d), "KILO" => ("km", 1_000_000d), "MICRO" => ("um", 0.001d),
                _ => (null, null),
            };
        }
        else if (parts.TryGetValue("CONVERSION_BASED_UNIT", out var cb) && cb.Count > 0 && cb[0] is string name)
        {
            var n = name.Trim().ToUpperInvariant();
            (label, mm) = n switch { "INCH" or "IN" => ((string?)"in", (double?)25.4d), "FOOT" or "FT" => ("ft", 304.8d), "MILLIMETRE" or "MILLIMETER" => ("mm", 1d), _ => (null, null) };
        }
        if (label is null || mm is null) { _unitConflict = true; return; }
        if (_unit is null) { _unit = label; _unitMm = mm; }
        else if (_unit != label) _unitConflict = true;
    }

    // ---------------------------------------------------------------- placements

    /// <summary>The child-to-parent matrix of each usage occurrence, keyed by the occurrence's entity number; null for an occurrence whose placement cannot be determined.</summary>
    public Dictionary<int, IReadOnlyList<double>?> Resolve(List<(int Nauo, int ParentDefinition, int ChildDefinition)> usages)
    {
        var repsOf = new Dictionary<int, HashSet<int>>();
        foreach (var (pds, rep) in _definitionReps)
            if (_pdsDefinition.TryGetValue(pds, out var pd))
            {
                if (!repsOf.TryGetValue(pd, out var set)) repsOf[pd] = set = [];
                set.Add(rep);
            }
        var byUsage = new Dictionary<int, int>(); // usage entity -> relationship entity
        foreach (var (relationship, pds) in _contextShapes)
            if (_pdsDefinition.TryGetValue(pds, out var usage)) byUsage.TryAdd(usage, relationship);

        var result = new Dictionary<int, IReadOnlyList<double>?>();
        foreach (var (nauo, parent, child) in usages)
        {
            result[nauo] = null;
            if (!byUsage.TryGetValue(nauo, out var rrId) || !_relationships.TryGetValue(rrId, out var rr) || rr.Transformation is not { } tId || !_transformations.TryGetValue(tId, out var t)) continue;
            if (!repsOf.TryGetValue(parent, out var parentReps) || !repsOf.TryGetValue(child, out var childReps)) continue;
            if (AxisMatrix(t.A) is not { } ma || AxisMatrix(t.B) is not { } mb) continue;
            double[] m;
            if (childReps.Contains(rr.Rep1) && parentReps.Contains(rr.Rep2)) m = Multiply(mb, InvertRigid(ma));
            else if (parentReps.Contains(rr.Rep1) && childReps.Contains(rr.Rep2)) m = Multiply(ma, InvertRigid(mb));
            else continue;
            result[nauo] = Clean(m);
        }
        return result;
    }

    /// <summary>The 4x4 matrix (row-major) of an AXIS2_PLACEMENT_3D per ISO 10303-42 (z = axis, x = reference direction made orthogonal to z), or null when degenerate.</summary>
    private double[]? AxisMatrix(int axisId)
    {
        if (!_axes.TryGetValue(axisId, out var ax) || !_points.TryGetValue(ax.Point, out var loc)) return null;
        (double X, double Y, double Z) z = (0, 0, 1);
        if (ax.Axis is { } zi && !_directions.TryGetValue(zi, out z)) return null;
        if (!Normalize(ref z)) return null;
        (double X, double Y, double Z) refDir;
        if (ax.RefDirection is { } ri) { if (!_directions.TryGetValue(ri, out refDir)) return null; }
        else refDir = z is { X: 0, Y: 0, Z: 1 } ? (1, 0, 0) : (0, 0, 1); // the default of the standard's first_proj_axis
        var dot = refDir.X * z.X + refDir.Y * z.Y + refDir.Z * z.Z;
        var x = (X: refDir.X - dot * z.X, Y: refDir.Y - dot * z.Y, Z: refDir.Z - dot * z.Z);
        if (!Normalize(ref x)) return null;
        var y = (X: z.Y * x.Z - z.Z * x.Y, Y: z.Z * x.X - z.X * x.Z, Z: z.X * x.Y - z.Y * x.X);
        return
        [
            x.X, y.X, z.X, loc.X,
            x.Y, y.Y, z.Y, loc.Y,
            x.Z, y.Z, z.Z, loc.Z,
            0, 0, 0, 1,
        ];
    }

    public static double[] Multiply(double[] a, double[] b)
    {
        var r = new double[16];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
            {
                double sum = 0;
                for (var k = 0; k < 4; k++) sum += a[i * 4 + k] * b[k * 4 + j];
                r[i * 4 + j] = sum;
            }
        return r;
    }

    /// <summary>Inverse of a rotation plus translation (the matrices built by <see cref="AxisMatrix"/> have orthonormal axes).</summary>
    private static double[] InvertRigid(double[] m)
    {
        var r = new double[16];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++) r[i * 4 + j] = m[j * 4 + i];
        for (var i = 0; i < 3; i++) r[i * 4 + 3] = -(r[i * 4] * m[3] + r[i * 4 + 1] * m[7] + r[i * 4 + 2] * m[11]);
        r[15] = 1;
        return r;
    }

    private static double[] Clean(double[] m)
    {
        for (var i = 0; i < m.Length; i++)
        {
            var v = Math.Round(m[i], 9);
            m[i] = v == 0 ? 0 : v; // also turns -0 into 0
        }
        return m;
    }

    private static bool Normalize(ref (double X, double Y, double Z) v)
    {
        var len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        if (!double.IsFinite(len) || len < 1e-12) return false;
        v = (v.X / len, v.Y / len, v.Z / len);
        return true;
    }

    // ---------------------------------------------------------------- boxes

    /// <summary>The box of the geometry of the given product definitions' own shape representations (not of their children), or null when none can be derived.</summary>
    public BoundingBox? BoxOf(List<int> definitions)
    {
        if (_truncated || definitions.Count == 0) return null;
        var reps = new HashSet<int>();
        foreach (var (pds, rep) in _definitionReps)
            if (_pdsDefinition.TryGetValue(pds, out var pd) && definitions.Contains(pd)) reps.Add(rep);
        if (reps.Count == 0) return null;

        var extent = new Extent();
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        foreach (var r in reps) stack.Push(r);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id)) continue;
            if (_points.TryGetValue(id, out var point)) { extent.Add(point); continue; }
            if (!_entities.TryGetValue(id, out var e)) continue;
            switch (e.Name)
            {
                case "EDGE_CURVE":
                    AddEdge(e.Args, extent);
                    break;
                case "VERTEX_POINT":
                    if (RefAt(e.Args, 1) is { } pt && _points.TryGetValue(pt, out var vp)) extent.Add(vp);
                    break;
                case "CIRCLE" or "ELLIPSE" or "B_SPLINE_CURVE_WITH_KNOTS":
                    break; // reached only through an edge; never traversed on its own
                default:
                    foreach (var o in e.Args) Push(o, stack);
                    break;
            }
        }
        return extent.ToBox();
    }

    private static void Push(object? o, Stack<int> stack)
    {
        switch (o)
        {
            case StepStructureReader.Ref r: stack.Push(r.Id); break;
            case List<object?> list: foreach (var i in list) Push(i, stack); break;
        }
    }

    private void AddEdge(List<object?> args, Extent extent)
    {
        var v1 = RefAt(args, 1);
        var v2 = RefAt(args, 2);
        (double X, double Y, double Z)? p1 = VertexPoint(v1), p2 = VertexPoint(v2);
        if (p1 is { } q1) extent.Add(q1);
        if (p2 is { } q2) extent.Add(q2);
        if (RefAt(args, 3) is not { } curveId || !_entities.TryGetValue(curveId, out var curve)) return;
        switch (curve.Name)
        {
            case "CIRCLE" or "ELLIPSE":
                {
                    if (RefAt(curve.Args, 1) is not { } axisId || AxisMatrix(axisId) is not { } m) return;
                    if (Number(curve.Args, 2) is not { } a1) return;
                    var circle = curve.Name == "CIRCLE";
                    double a2;
                    if (circle) a2 = a1;
                    else if (Number(curve.Args, 3) is { } second) a2 = second;
                    else return;
                    var sameSense = args.Count > 4 && args[4] is Enumeration { Name: "T" };
                    // Exact for a circle between its vertices; an ellipse (or an edge whose vertices are unknown) is taken whole, which can only make the box larger.
                    var whole = !circle || (v1 is not null && v1 == v2) || p1 is null || p2 is null;
                    double start = 0, sweep = 2 * Math.PI;
                    if (!whole)
                    {
                        start = Angle(sameSense ? p1!.Value : p2!.Value, m);
                        sweep = Mod2Pi(Angle(sameSense ? p2!.Value : p1!.Value, m) - start);
                    }
                    for (var axis = 0; axis < 3; axis++)
                    {
                        double xi = m[axis * 4], yi = m[axis * 4 + 1], ci = m[axis * 4 + 3];
                        var amplitude = Math.Sqrt(a1 * a1 * xi * xi + a2 * a2 * yi * yi);
                        if (amplitude < 1e-12) continue;
                        if (whole) { extent.AddAxis(axis, ci - amplitude); extent.AddAxis(axis, ci + amplitude); continue; }
                        var peak = Math.Atan2(a2 * yi, a1 * xi);
                        if (Mod2Pi(peak - start) <= sweep + 1e-9) extent.AddAxis(axis, ci + amplitude);
                        if (Mod2Pi(peak + Math.PI - start) <= sweep + 1e-9) extent.AddAxis(axis, ci - amplitude);
                    }
                    break;
                }
            case "B_SPLINE_CURVE_WITH_KNOTS":
                if (curve.Args.Count > 2 && curve.Args[2] is List<object?> controls)
                    foreach (var c in controls)
                        if (c is StepStructureReader.Ref cr && _points.TryGetValue(cr.Id, out var cp)) extent.Add(cp);
                break;
        }
    }

    private (double X, double Y, double Z)? VertexPoint(int? vertex) =>
        vertex is { } v && _entities.TryGetValue(v, out var e) && e.Name == "VERTEX_POINT" && RefAt(e.Args, 1) is { } pt && _points.TryGetValue(pt, out var xyz) ? xyz : null;

    private static double Angle((double X, double Y, double Z) p, double[] m)
    {
        double dx = p.X - m[3], dy = p.Y - m[7], dz = p.Z - m[11];
        return Math.Atan2(dx * m[1] + dy * m[5] + dz * m[9], dx * m[0] + dy * m[4] + dz * m[8]);
    }

    private static double Mod2Pi(double a)
    {
        var r = a % (2 * Math.PI);
        return r < 0 ? r + 2 * Math.PI : r;
    }

    private sealed class Extent
    {
        private readonly double[] _min = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
        private readonly double[] _max = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];

        public void Add((double X, double Y, double Z) p) { AddAxis(0, p.X); AddAxis(1, p.Y); AddAxis(2, p.Z); }

        public void AddAxis(int axis, double v)
        {
            if (!double.IsFinite(v)) return;
            if (v < _min[axis]) _min[axis] = v;
            if (v > _max[axis]) _max[axis] = v;
        }

        public BoundingBox? ToBox() =>
            _min.Any(double.IsPositiveInfinity) ? null : new BoundingBox(R(_min[0]), R(_min[1]), R(_min[2]), R(_max[0]), R(_max[1]), R(_max[2]));

        private static double R(double v) { var r = Math.Round(v, 6); return r == 0 ? 0 : r; }
    }

    // ---------------------------------------------------------------- argument helpers

    private static int? RefAt(List<object?> args, int i) => i < args.Count && args[i] is StepStructureReader.Ref r ? r.Id : null;

    private static double? Number(List<object?> args, int i) =>
        i < args.Count && args[i] is string s && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    private static (double X, double Y, double Z)? Vector(List<object?> args, int i)
    {
        if (i >= args.Count || args[i] is not List<object?> list || list.Count < 3) return null;
        var v = new double[3];
        for (var k = 0; k < 3; k++)
            if (list[k] is not string s || !double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[k]) || !double.IsFinite(v[k])) return null;
        return (v[0], v[1], v[2]);
    }
}
