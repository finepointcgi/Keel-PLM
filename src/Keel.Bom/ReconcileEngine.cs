using System.Globalization;
using Keel.Core;

namespace Keel.Bom;

internal sealed record EbomLineData(Guid Id, int Find, int Alt, Guid Child, decimal Quantity, string Unit, EffectivityRange? Eff);

internal sealed record MbomLineData(Guid Id, int Find, int Alt, Guid Child, decimal Quantity, string Unit, decimal? Scrap, bool Phantom, string SupplyType, bool MbomOnly, EffectivityRange? Eff);

internal sealed record LinkData(Guid MbomId, Guid EbomId, string Status, string? Reason);

internal sealed record ChildMbomInfo(bool Exists, bool HasReleased);

/// <summary>Everything the reconciliation compares, as plain data (no access filter: the engine is the system's check; display filters later).</summary>
internal sealed record ReconcileInput(
    IReadOnlyList<EbomLineData> Ebom,
    IReadOnlyList<MbomLineData> Mbom,
    IReadOnlyList<LinkData> Links,
    /// <summary>The EBOM lines that links point at but that are not part of <see cref="Ebom"/> (the EBOM moved on); used to carry a link over by identity.</summary>
    IReadOnlyList<EbomLineData> LinkedElsewhere,
    Guid? DerivedFrom, string? DerivedLabel, Guid EbomIteration, string EbomLabel,
    /// <summary>Child masters that are Make sub-assemblies (type Assembly, source Make).</summary>
    IReadOnlySet<Guid> MakeAssemblies,
    IReadOnlyDictionary<Guid, ChildMbomInfo> ChildMboms);

internal sealed record EngineFinding(
    string Code, string Severity, Guid? EbomUsageId, Guid? MbomUsageId, Guid? ChildMasterId, int? EbomFind, int? MbomFind,
    decimal? Expected, decimal? Actual, string? Unit, string? Detail);

internal sealed record ReconcileOutcome(string Status, int EbomLines, int MbomLines, int CoveredLines, decimal CoveragePct, IReadOnlyList<EngineFinding> Findings)
{
    public int Errors => Findings.Count(f => f.Severity == FindingSeverities.Error);
    public int Warnings => Findings.Count(f => f.Severity == FindingSeverities.Warning);
    public int Infos => Findings.Count(f => f.Severity == FindingSeverities.Info);
}

/// <summary>
/// The reconciliation of one MBOM iteration against one EBOM iteration (ADR-0024). Pure and deterministic.
/// <list type="bullet">
/// <item>Links join MBOM lines to EBOM lines; the connected groups of lines (1:1, split, merge) are compared as a whole.</item>
/// <item>Quantity: <c>sum(EBOM quantity) = sum(MBOM net quantity)</c> within 0.00001, per group, in one unit. Scrap is planned loss on top of the net quantity
/// (requirement = net * (1 + scrap)), so a declared scrap factor never causes a difference; a group whose quantity differs by exactly the scrap is told so.
/// A declared <c>quantity_adjusted</c> link turns the difference into an information finding with its reason. Units are never converted.</item>
/// <item>Effectivity: the union of the EBOM lines' ranges is compared with the union of the MBOM lines' ranges in one dimension; what the EBOM applies to and the MBOM does not
/// is an error, what the MBOM covers beyond the EBOM is a warning.</item>
/// <item>Status: any error gives Not reconciled (Stale when the only error is revision drift); otherwise any warning or declared exception gives Reconciled with
/// exceptions; otherwise Reconciled.</item>
/// </list>
/// </summary>
internal static class ReconcileEngine
{
    public const decimal Tolerance = 0.00001m;

    public static ReconcileOutcome Run(ReconcileInput input)
    {
        var findings = new List<EngineFinding>();
        var ebom = input.Ebom.OrderBy(e => e.Find).ThenBy(e => e.Alt).ToList();
        var mbom = input.Mbom.OrderBy(m => m.Find).ThenBy(m => m.Alt).ToList();
        var ebomById = ebom.ToDictionary(e => e.Id);
        var mbomById = mbom.ToDictionary(m => m.Id);

        var links = EffectiveLinks(input, ebom, ebomById, mbomById);

        // ---- groups of lines joined by links (union-find over EBOM lines then MBOM lines)
        var index = new Dictionary<Guid, int>(ebom.Count + mbom.Count);
        foreach (var e in ebom) index[e.Id] = index.Count;
        foreach (var m in mbom) index[m.Id] = index.Count;
        var parent = Enumerable.Range(0, index.Count).ToArray();
        int Root(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        foreach (var l in links)
        {
            var a = Root(index[l.MbomId]);
            var b = Root(index[l.EbomId]);
            if (a != b) parent[a] = b;
        }
        var linkedMbom = links.Select(l => l.MbomId).ToHashSet();
        var linkedEbom = links.Select(l => l.EbomId).ToHashSet();

        // ---- per link: declared relationships
        foreach (var l in links)
        {
            var e = ebomById[l.EbomId];
            var m = mbomById[l.MbomId];
            if (l.Status == LinkStatuses.Matched && e.Child != m.Child)
                findings.Add(new(FindingCodes.LinkChildMismatch, FindingSeverities.Error, e.Id, m.Id, m.Child, e.Find, m.Find, null, null, null,
                    "The link says matched but the lines are different parts; declare a substitution with a reason."));
            else if (l.Status == LinkStatuses.Substituted && e.Child == m.Child)
                findings.Add(new(FindingCodes.SubstitutionSamePart, FindingSeverities.Warning, e.Id, m.Id, m.Child, e.Find, m.Find, null, null, null,
                    $"Declared as a substitution ({l.Reason}) but both lines are the same part."));
            else if (l.Status == LinkStatuses.Substituted)
                findings.Add(new(FindingCodes.Substitution, FindingSeverities.Warning, e.Id, m.Id, m.Child, e.Find, m.Find, null, null, null, l.Reason));
        }

        // ---- per group: quantity, unit, effectivity
        var groups = new Dictionary<int, (List<EbomLineData> E, List<MbomLineData> M, List<LinkData> L)>();
        foreach (var e in ebom.Where(e => linkedEbom.Contains(e.Id)))
            Group(groups, Root(index[e.Id])).E.Add(e);
        foreach (var m in mbom.Where(m => linkedMbom.Contains(m.Id)))
            Group(groups, Root(index[m.Id])).M.Add(m);
        foreach (var l in links)
            Group(groups, Root(index[l.MbomId])).L.Add(l);
        foreach (var g in groups.Values.OrderBy(g => g.M.Min(m => m.Find)).ThenBy(g => g.E.Min(e => e.Find)))
            CompareGroup(g.E, g.M, g.L, findings);

        // ---- lines without a link
        foreach (var m in mbom)
        {
            if (linkedMbom.Contains(m.Id))
            {
                if (m.MbomOnly)
                    findings.Add(new(FindingCodes.MbomOnlyWithLinks, FindingSeverities.Warning, null, m.Id, m.Child, null, m.Find, null, null, null, "The line is flagged MBOM-only but also implements an EBOM line; remove one of the two."));
                continue;
            }
            if (m.MbomOnly)
                findings.Add(new(FindingCodes.MbomOnlyLine, FindingSeverities.Info, null, m.Id, m.Child, null, m.Find, m.Quantity, null, m.Unit, "Manufacturing-only item (no EBOM source)."));
            else
                findings.Add(new(FindingCodes.MbomLineUnexplained, FindingSeverities.Error, null, m.Id, m.Child, null, m.Find, m.Quantity, null, m.Unit,
                    "The line has no EBOM source and is not flagged MBOM-only."));
        }
        var unlinkedMbomByChild = mbom.Where(m => !linkedMbom.Contains(m.Id)).GroupBy(m => m.Child).ToDictionary(g => g.Key, g => g.First());
        foreach (var e in ebom.Where(e => !linkedEbom.Contains(e.Id)))
        {
            var hint = unlinkedMbomByChild.TryGetValue(e.Child, out var candidate) ? $"MBOM line {candidate.Find.ToString(CultureInfo.InvariantCulture)} is the same part but is not linked to this EBOM line." : null;
            findings.Add(new(FindingCodes.EbomLineMissing, FindingSeverities.Error, e.Id, null, e.Child, e.Find, null, e.Quantity, null, e.Unit, hint ?? "No MBOM line implements this EBOM line."));
        }

        // ---- multi-level completeness: Make sub-assemblies need a released MBOM of the same plant
        foreach (var m in mbom)
        {
            if (m.SupplyType != "Make" || !input.MakeAssemblies.Contains(m.Child)) continue;
            var info = input.ChildMboms.GetValueOrDefault(m.Child);
            if (info is null || !info.Exists)
                findings.Add(new(FindingCodes.ChildMbomMissing, FindingSeverities.Error, null, m.Id, m.Child, null, m.Find, null, null, null, "The sub-assembly is made in house but has no MBOM for this plant."));
            else if (!info.HasReleased)
                findings.Add(new(FindingCodes.ChildMbomUnreleased, FindingSeverities.Warning, null, m.Id, m.Child, null, m.Find, null, null, null, "The MBOM of the sub-assembly for this plant has no released revision yet."));
        }

        // ---- revision drift
        if (input.DerivedFrom is null)
            findings.Add(new(FindingCodes.NotDerived, FindingSeverities.Info, null, null, null, null, null, null, null, null, "The MBOM was not derived from an EBOM revision."));
        else if (input.DerivedFrom != input.EbomIteration)
            findings.Add(new(FindingCodes.RevisionDrift, FindingSeverities.Error, null, null, null, null, null, null, null, null,
                $"The MBOM was derived from EBOM {input.DerivedLabel ?? "an earlier iteration"}; it is reconciled against {input.EbomLabel}. Regenerate or confirm the changes."));

        var ordered = findings
            .OrderBy(f => f.Severity == FindingSeverities.Error ? 0 : f.Severity == FindingSeverities.Warning ? 1 : 2)
            .ThenBy(f => f.Code, StringComparer.Ordinal).ThenBy(f => f.MbomFind ?? int.MaxValue).ThenBy(f => f.EbomFind ?? int.MaxValue).ToList();

        var covered = ebom.Count(e => linkedEbom.Contains(e.Id));
        var coverage = ebom.Count == 0 ? 100m : decimal.Round(100m * covered / ebom.Count, 2);
        var errors = ordered.Where(f => f.Severity == FindingSeverities.Error).ToList();
        var status = errors.Count > 0
            ? (errors.All(f => f.Code == FindingCodes.RevisionDrift) ? ReconcileStatuses.Stale : ReconcileStatuses.NotReconciled)
            : ordered.Count > 0 ? ReconcileStatuses.WithExceptions : ReconcileStatuses.Reconciled;
        return new ReconcileOutcome(status, ebom.Count, mbom.Count, covered, coverage, ordered);
    }

    private static (List<EbomLineData> E, List<MbomLineData> M, List<LinkData> L) Group(Dictionary<int, (List<EbomLineData>, List<MbomLineData>, List<LinkData>)> groups, int root)
    {
        if (!groups.TryGetValue(root, out var g)) groups[root] = g = ([], [], []);
        return g;
    }

    /// <summary>
    /// Links whose EBOM line is part of the compared EBOM iteration count as they are. A link to a line of an earlier EBOM iteration is carried over to the line
    /// of the compared iteration with the same (part, find number, alternate), else the same part and find number, else the only line of that part; otherwise it counts for nothing.
    /// </summary>
    internal static List<LinkData> EffectiveLinks(ReconcileInput input, List<EbomLineData> ebom, Dictionary<Guid, EbomLineData> ebomById, Dictionary<Guid, MbomLineData> mbomById)
    {
        var elsewhere = input.LinkedElsewhere.ToDictionary(e => e.Id);
        var byIdentity = new Dictionary<(Guid, int, int), EbomLineData>();
        foreach (var e in ebom) byIdentity.TryAdd((e.Child, e.Find, e.Alt), e);
        var byFind = new Dictionary<(Guid, int), EbomLineData>();
        foreach (var e in ebom) byFind.TryAdd((e.Child, e.Find), e);
        var byChild = ebom.GroupBy(e => e.Child).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());

        var result = new List<LinkData>();
        var seen = new HashSet<(Guid, Guid)>();
        foreach (var l in input.Links.OrderBy(l => mbomById.TryGetValue(l.MbomId, out var m) ? m.Find : int.MaxValue))
        {
            if (!mbomById.ContainsKey(l.MbomId)) continue;
            Guid? target = null;
            if (ebomById.ContainsKey(l.EbomId)) target = l.EbomId;
            else if (elsewhere.TryGetValue(l.EbomId, out var old))
            {
                if (byIdentity.TryGetValue((old.Child, old.Find, old.Alt), out var a)) target = a.Id;
                else if (byFind.TryGetValue((old.Child, old.Find), out var b)) target = b.Id;
                else if (byChild.TryGetValue(old.Child, out var c)) target = c.Id;
            }
            if (target is { } t && seen.Add((l.MbomId, t))) result.Add(l with { EbomId = t });
        }
        return result;
    }

    private static void CompareGroup(List<EbomLineData> e, List<MbomLineData> m, List<LinkData> links, List<EngineFinding> findings)
    {
        var firstE = e.OrderBy(x => x.Find).ThenBy(x => x.Alt).First();
        var firstM = m.OrderBy(x => x.Find).ThenBy(x => x.Alt).First();
        var child = firstM.Child;

        // ---- quantity and unit
        var eUnits = e.Select(x => x.Unit).Distinct(StringComparer.Ordinal).ToList();
        var mUnits = m.Select(x => x.Unit).Distinct(StringComparer.Ordinal).ToList();
        var declared = links.Where(l => l.Status == LinkStatuses.QuantityAdjusted).Select(l => l.Reason).Where(r => r is not null).Distinct().ToList();
        var expected = e.Sum(x => x.Quantity);
        var actual = m.Sum(x => x.Quantity);
        if (eUnits.Count != 1 || mUnits.Count != 1 || eUnits[0] != mUnits[0])
        {
            var text = $"EBOM unit {string.Join("/", eUnits)}; MBOM unit {string.Join("/", mUnits)}. Units are not converted.";
            if (declared.Count > 0)
                findings.Add(new(FindingCodes.QuantityAdjusted, FindingSeverities.Info, firstE.Id, firstM.Id, child, firstE.Find, firstM.Find, expected, actual, mUnits.FirstOrDefault(),
                    $"Declared: {string.Join("; ", declared)}. {text}"));
            else
                findings.Add(new(FindingCodes.UnitMismatch, FindingSeverities.Error, firstE.Id, firstM.Id, child, firstE.Find, firstM.Find, expected, actual, null, text));
        }
        else if (Math.Abs(actual - expected) > Tolerance)
        {
            if (declared.Count > 0)
                findings.Add(new(FindingCodes.QuantityAdjusted, FindingSeverities.Info, firstE.Id, firstM.Id, child, firstE.Find, firstM.Find, expected, actual, mUnits[0], $"Declared: {string.Join("; ", declared)}"));
            else
            {
                string? hint = null;
                if (m.Count == 1 && m[0].Scrap is { } s && s > 0 && Math.Abs(actual - expected * (1m + s)) <= Tolerance)
                    hint = $"The MBOM quantity equals the EBOM quantity plus the declared scrap ({MbomText.Qty(s)}); enter the net quantity and keep the scrap factor.";
                findings.Add(new(FindingCodes.QuantityDifference, FindingSeverities.Error, firstE.Id, firstM.Id, child, firstE.Find, firstM.Find, expected, actual, mUnits[0],
                    hint ?? $"Expected {MbomText.Qty(expected)} {mUnits[0]} (EBOM), the MBOM has {MbomText.Qty(actual)}."));
            }
        }

        // ---- effectivity
        var ranges = e.Select(x => x.Eff).Concat(m.Select(x => x.Eff)).ToList();
        var dimensions = ranges.Where(r => r is not null).Select(r => (r!.Kind, r.Context)).Distinct().ToList();
        if (dimensions.Count == 0) return;
        if (dimensions.Count > 1)
        {
            findings.Add(new(FindingCodes.EffectivityDimension, FindingSeverities.Warning, firstE.Id, firstM.Id, child, firstE.Find, firstM.Find, null, null, null,
                $"The lines use different effectivity types or contexts ({string.Join("; ", dimensions.Select(d => $"{d.Kind}{(d.Context is null ? "" : " " + d.Context)}"))}); they cannot be compared."));
            return;
        }
        var (kind, context) = dimensions[0];
        var lo = kind == EffectivityKinds.Date ? 0L : 1L;
        var ebomSet = Union(e.Select(x => x.Eff), lo);
        var mbomSet = Union(m.Select(x => x.Eff), lo);
        var gap = Subtract(ebomSet, mbomSet);
        var excess = Subtract(mbomSet, ebomSet);
        if (gap.Count > 0)
            findings.Add(new(FindingCodes.EffectivityGap, FindingSeverities.Error, firstE.Id, firstM.Id, child, firstE.Find, firstM.Find, null, null, null,
                $"EBOM applies to {Describe(kind, context, ebomSet, lo)}; the MBOM to {Describe(kind, context, mbomSet, lo)}. Not covered by the MBOM: {Describe(kind, context, gap, lo)}."));
        if (excess.Count > 0)
            findings.Add(new(FindingCodes.EffectivityExcess, FindingSeverities.Warning, firstE.Id, firstM.Id, child, firstE.Find, firstM.Find, null, null, null,
                $"EBOM applies to {Describe(kind, context, ebomSet, lo)}; the MBOM to {Describe(kind, context, mbomSet, lo)}. Covered by the MBOM beyond the EBOM: {Describe(kind, context, excess, lo)}."));
    }

    // ---------------------------------------------------------------- interval arithmetic (closed integer intervals, null end = open)

    internal static List<(long From, long To)> Union(IEnumerable<EffectivityRange?> ranges, long domainLo)
    {
        var intervals = ranges
            .Select(r => r is null ? (From: domainLo, To: long.MaxValue) : (From: Math.Max(r.From ?? domainLo, domainLo), To: r.To ?? long.MaxValue))
            .Where(i => i.From <= i.To).OrderBy(i => i.From).ToList();
        var merged = new List<(long From, long To)>();
        foreach (var i in intervals)
        {
            if (merged.Count > 0 && (merged[^1].To == long.MaxValue || i.From <= merged[^1].To + 1))
                merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, i.To));
            else merged.Add(i);
        }
        return merged;
    }

    /// <summary>The parts of <paramref name="a"/> that <paramref name="b"/> does not cover. Both are sorted, merged interval lists.</summary>
    internal static List<(long From, long To)> Subtract(List<(long From, long To)> a, List<(long From, long To)> b)
    {
        var result = new List<(long From, long To)>();
        foreach (var (from, to) in a)
        {
            var start = from;
            var open = true;
            foreach (var (bf, bt) in b)
            {
                if (bt < start) continue;
                if (bf > to) break;
                if (bf > start) result.Add((start, bf - 1));
                if (bt >= to) { open = false; break; }
                start = bt + 1;
            }
            if (open && start <= to) result.Add((start, to));
        }
        return result;
    }

    private static string Describe(string kind, string? context, List<(long From, long To)> intervals, long lo) =>
        string.Join(", ", intervals.Select(i => new EffectivityRange(kind, context,
            i.From <= lo && kind == EffectivityKinds.Date ? null : i.From, i.To == long.MaxValue ? null : i.To).Describe()));
}
