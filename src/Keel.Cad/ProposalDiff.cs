using Keel.Core;

namespace Keel.Cad;

/// <summary>A proposed usage with its child resolved to a Keel part number where one exists (null for a part that will be created, or one the caller cannot see).</summary>
public sealed record ResolvedLine(string ChildRef, string? ChildNumber, string ChildLabel, decimal Quantity, string? Unit, int? FindNumber,
    IReadOnlyList<string> ReferenceDesignators, string? Notes, bool Resolvable);

/// <summary>
/// The difference between the proposed direct children of a parent and the children of its current EBOM iteration. Pure and deterministic, so
/// the review page and the apply step compute exactly the same rows and the same edits.
/// </summary>
public static class ProposalDiff
{
    public sealed record Pair(DiffKind Kind, ResolvedLine? Proposed, CadBomLine? Current);

    public static IReadOnlyList<Pair> Compute(IReadOnlyList<ResolvedLine> proposed, IReadOnlyList<CadBomLine> current)
    {
        var usedCurrent = new bool[current.Count];
        var pairedWith = new int[proposed.Count];
        Array.Fill(pairedWith, -1);

        // Pass 1: same child and same find number. Pass 2: same child, any remaining line.
        for (var pass = 1; pass <= 2; pass++)
            for (var i = 0; i < proposed.Count; i++)
            {
                if (pairedWith[i] >= 0 || proposed[i].ChildNumber is null) continue;
                if (pass == 1 && proposed[i].FindNumber is null) continue;
                for (var j = 0; j < current.Count; j++)
                {
                    if (usedCurrent[j] || current[j].ChildNumber != proposed[i].ChildNumber) continue;
                    if (pass == 1 && current[j].FindNumber != proposed[i].FindNumber) continue;
                    pairedWith[i] = j;
                    usedCurrent[j] = true;
                    break;
                }
            }

        var result = new List<Pair>();
        for (var i = 0; i < proposed.Count; i++)
        {
            if (pairedWith[i] < 0) { result.Add(new Pair(DiffKind.Added, proposed[i], null)); continue; }
            var cur = current[pairedWith[i]];
            result.Add(new Pair(Same(proposed[i], cur) ? DiffKind.Unchanged : DiffKind.Changed, proposed[i], cur));
        }
        for (var j = 0; j < current.Count; j++)
            if (!usedCurrent[j]) result.Add(new Pair(DiffKind.Removed, null, current[j]));
        return result;
    }

    /// <summary>A value the CAD file does not carry (no unit, find number or designators) is not a change.</summary>
    private static bool Same(ResolvedLine p, CadBomLine c) =>
        p.Quantity == c.Quantity
        && (p.Unit is null || string.Equals(p.Unit, c.Unit, StringComparison.Ordinal))
        && (p.FindNumber is null || p.FindNumber == c.FindNumber)
        && (p.ReferenceDesignators.Count == 0 || p.ReferenceDesignators.Order(StringComparer.Ordinal).SequenceEqual(c.ReferenceDesignators.Order(StringComparer.Ordinal)));

    public static DiffSide? Side(CadBomLine? c) => c is null ? null : new DiffSide(c.Quantity, c.Unit, c.FindNumber, c.ReferenceDesignators);
    public static DiffSide? Side(ResolvedLine? p) => p is null ? null : new DiffSide(p.Quantity, p.Unit ?? "", p.FindNumber, p.ReferenceDesignators);

    /// <summary>The edits that make the parent's BOM match the proposal. Removals are produced only when the proposal asks for them.</summary>
    public static IReadOnlyList<CadBomEdit> Edits(IReadOnlyList<Pair> pairs, bool applyRemovals)
    {
        var edits = new List<CadBomEdit>();
        foreach (var pair in pairs)
        {
            switch (pair.Kind)
            {
                case DiffKind.Added when pair.Proposed!.Resolvable && pair.Proposed.ChildNumber is not null:
                    var a = pair.Proposed;
                    edits.Add(new CadBomEdit(CadBomEditKind.Add, a.ChildNumber!, a.Quantity, a.Unit ?? "", a.FindNumber, a.ReferenceDesignators, a.Notes));
                    break;
                case DiffKind.Changed when pair.Proposed!.Resolvable:
                    var p = pair.Proposed; var c = pair.Current!;
                    edits.Add(new CadBomEdit(CadBomEditKind.Update, c.ChildNumber, p.Quantity, p.Unit ?? c.Unit, p.FindNumber ?? c.FindNumber,
                        p.ReferenceDesignators.Count > 0 ? p.ReferenceDesignators : c.ReferenceDesignators, p.Notes ?? c.Notes, c.FindNumber));
                    break;
                case DiffKind.Removed when applyRemovals:
                    var r = pair.Current!;
                    edits.Add(new CadBomEdit(CadBomEditKind.Remove, r.ChildNumber, r.Quantity, r.Unit, r.FindNumber, r.ReferenceDesignators, r.Notes, r.FindNumber));
                    break;
            }
        }
        // Removals first: they free find numbers that additions may reuse.
        return [.. edits.OrderBy(e => e.Kind == CadBomEditKind.Remove ? 0 : e.Kind == CadBomEditKind.Update ? 1 : 2)];
    }
}
