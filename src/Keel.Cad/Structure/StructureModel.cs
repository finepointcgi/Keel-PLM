using System.Globalization;
using System.Text.RegularExpressions;

namespace Keel.Cad.Structure;

/// <summary>An attribute value that identifies an existing part (for example a CAGE code) when the CAD number is not a Keel number.</summary>
public sealed record MatchRule(string Attribute, string Value);

/// <summary>One part of a CAD structure. <see cref="Ref"/> is the CAD system's own key, unique inside the structure.</summary>
public sealed record StructurePart(
    string Ref, string? Number, string Name, string? PartType, string? Unit, string? Source,
    IReadOnlyDictionary<string, string> Attributes, IReadOnlyList<MatchRule> MatchOn);

/// <summary>A usage: <see cref="ParentRef"/> contains <see cref="Quantity"/> of <see cref="ChildRef"/>.</summary>
public sealed record StructureLine(
    string ParentRef, string ChildRef, decimal Quantity, string? Unit, int? FindNumber, IReadOnlyList<string> ReferenceDesignators, string? Notes);

public sealed record StructureModel(string? SourceSystem, string RootRef, IReadOnlyList<StructurePart> Parts, IReadOnlyList<StructureLine> Lines);

/// <summary>The structure could not be read or is not a valid structure. The message is safe to show (it never echoes file content beyond a line or entity number).</summary>
public sealed class StructureFormatException(string message) : Exception(message);

public static class StructureFormats
{
    public const string Json = "json";
    public const string Step = "step";
    public const string Csv = "csv";
    public const string Xml = "xml";
    public static readonly IReadOnlyList<string> All = [Json, Step, Csv, Xml];
}

public static class StructureLimits
{
    public const int MaxBytes = 64 * 1024 * 1024;
    public const int MaxParts = 20_000;
    public const int MaxLines = 100_000;
    public const int MaxReferenceDesignators = 2_000;
    public const int MaxDepth = 30;
}

/// <summary>Validation shared by every importer, applied to the neutral model so a malformed STEP file and a malformed CSV fail the same way.</summary>
public static partial class StructureValidator
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_./]{0,39}$")]
    private static partial Regex DesignatorPattern();

    /// <summary>Returns the structure unchanged, or throws <see cref="StructureFormatException"/> naming the first problems.</summary>
    public static StructureModel Validate(StructureModel m)
    {
        var errors = new List<string>();
        void Err(string e) { if (errors.Count < 20) errors.Add(e); }

        if (m.Parts.Count == 0) Err("The structure has no parts.");
        if (m.Parts.Count > StructureLimits.MaxParts) Err($"The structure has more than {StructureLimits.MaxParts} parts.");
        if (m.Lines.Count > StructureLimits.MaxLines) Err($"The structure has more than {StructureLimits.MaxLines} usage lines.");
        var refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in m.Parts)
        {
            if (string.IsNullOrWhiteSpace(p.Ref) || p.Ref.Length > 200) Err("A part has no key or a key longer than 200 characters.");
            else if (!refs.Add(p.Ref)) Err($"The part key '{p.Ref}' appears twice.");
            if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 200) Err($"Part '{Trunc(p.Ref)}' needs a name of at most 200 characters.");
            if (p.Number is { Length: > 100 }) Err($"Part '{Trunc(p.Ref)}' has a number longer than 100 characters.");
        }
        if (!refs.Contains(m.RootRef)) Err($"The root '{Trunc(m.RootRef)}' is not a part of the structure.");
        var children = new HashSet<string>(StringComparer.Ordinal);
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var l in m.Lines)
        {
            if (!refs.Contains(l.ParentRef) || !refs.Contains(l.ChildRef)) { Err("A usage line names a part that is not in the structure."); continue; }
            if (l.ParentRef == l.ChildRef) Err($"Part '{Trunc(l.ParentRef)}' contains itself.");
            if (l.Quantity <= 0 || l.Quantity > 1_000_000m) Err($"A usage of '{Trunc(l.ChildRef)}' has a quantity outside 0 to 1,000,000.");
            if (l.FindNumber is < 1 or > 999_999) Err($"A usage of '{Trunc(l.ChildRef)}' has a find number outside 1 to 999999.");
            if (l.ReferenceDesignators.Count > StructureLimits.MaxReferenceDesignators) Err($"A usage of '{Trunc(l.ChildRef)}' has too many reference designators.");
            foreach (var d in l.ReferenceDesignators)
                if (!DesignatorPattern().IsMatch(d)) { Err($"'{Trunc(d)}' is not a valid reference designator."); break; }
            children.Add(l.ChildRef);
            if (!edges.TryGetValue(l.ParentRef, out var list)) edges[l.ParentRef] = list = [];
            list.Add(l.ChildRef);
        }
        if (children.Contains(m.RootRef)) Err("The root part is used by another part; choose the top assembly as the root.");
        if (errors.Count == 0 && HasCycle(edges)) Err("The structure contains a cycle (a part contains itself through other parts).");
        if (errors.Count == 0 && Depth(edges, m.RootRef) > StructureLimits.MaxDepth) Err($"The structure is deeper than {StructureLimits.MaxDepth} levels.");
        if (errors.Count > 0) throw new StructureFormatException(string.Join(" ", errors));
        return m;
    }

    private static string Trunc(string? s) => s is null ? "" : s.Length <= 40 ? s : s[..40] + "...";

    /// <summary>Iterative three-colour search; no recursion, so a hostile file cannot overflow the stack.</summary>
    private static bool HasCycle(Dictionary<string, List<string>> edges)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = on the path, 2 = done
        foreach (var start in edges.Keys)
        {
            if (state.ContainsKey(start)) continue;
            var stack = new Stack<(string Node, int Next)>();
            stack.Push((start, 0));
            state[start] = 1;
            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                var kids = edges.GetValueOrDefault(node);
                if (kids is null || next >= kids.Count) { state[node] = 2; continue; }
                stack.Push((node, next + 1));
                var child = kids[next];
                var s = state.GetValueOrDefault(child);
                if (s == 1) return true;
                if (s == 0) { state[child] = 1; stack.Push((child, 0)); }
            }
        }
        return false;
    }

    private static int Depth(Dictionary<string, List<string>> edges, string root)
    {
        var deepest = 0;
        var queue = new Queue<(string Node, int Level)>();
        queue.Enqueue((root, 1));
        var seenAt = new Dictionary<string, int>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var (node, level) = queue.Dequeue();
            if (seenAt.TryGetValue(node, out var best) && best >= level) continue;
            seenAt[node] = level;
            deepest = Math.Max(deepest, level);
            if (deepest > StructureLimits.MaxDepth) return deepest;
            foreach (var c in edges.GetValueOrDefault(node) ?? []) queue.Enqueue((c, level + 1));
        }
        return deepest;
    }

    public static decimal ParseQuantity(string text, string what) =>
        decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var q) ? q : throw new StructureFormatException($"{what}: '{Trunc(text)}' is not a number.");

    public static int ParseFind(string text, string what) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var f) ? f : throw new StructureFormatException($"{what}: '{Trunc(text)}' is not a find number.");

    /// <summary>Splits reference designators separated by spaces, commas or semicolons.</summary>
    public static IReadOnlyList<string> SplitDesignators(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : [.. text.Split([' ', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
