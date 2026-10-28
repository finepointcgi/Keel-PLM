using System.Globalization;
using Keel.Core;
using Keel.Migration.Canonical;

namespace Keel.Migration.Mapping;

/// <summary>
/// BOM lines and their effectivity (ADR-0021, FUP-3). A source line with structured effectivity becomes one Keel line per range (the lines of one find number are
/// alternates, told apart by their ranges); the same rules as native editing apply: serial and lot ranges need a Keel context, dates are UTC days, From must not be
/// after To, and alternates of one find number must have the same dimension and context and must not overlap. Violations are reported per source id.
/// </summary>
public sealed partial class Mapper
{
    private const long MaxSerial = 9_999_999_999L;
    private static readonly long MaxDay = EffectivityRange.DayNumber(new DateOnly(2200, 12, 31));

    private static string RawText(CEffectivity e) =>
        $"{e.Kind} {e.From}{(string.IsNullOrWhiteSpace(e.To) || e.EndCondition == "openEnded" ? (string.IsNullOrWhiteSpace(e.From) ? "" : " and up") : "-" + e.To)}{(string.IsNullOrWhiteSpace(e.Unit) ? "" : $" ({e.Unit})")}";

    private void MapUsages(CObject o, CRevision r, CIteration it, PIteration pi, string id, string T)
    {
        var loading = cfg.Effectivity.Load;
        // Without effectivity the find number is unique; with it, lines that carry ranges may share one (they are alternates).
        var usedFind = it.Usages.Where(u => u.FindNumber is > 0).Select(u => u.FindNumber!.Value).ToList();
        foreach (var g in usedFind.GroupBy(x => x).Where(g => g.Count() > 1))
        {
            var members = it.Usages.Where(u => u.FindNumber == g.Key).ToList();
            if (!loading || members.Any(m => m.EffectivityRanges.Count == 0))
                Err(T, id, "BOM-DUP-FIND", $"{o.Number} {r.Label}.{it.Sequence}: find number {g.Key} is used {g.Count()} times.",
                    loading ? "Lines that share a find number are alternates and each needs an effectivity range; renumber in the source or give every alternate a range."
                            : "Renumber in the source; find numbers are unique per parent iteration (alternates need effectivity.load).");
            else break;
        }
        var nextFind = (usedFind.Count == 0 ? 0 : usedFind.Max()) + 1;
        var designators = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in it.Usages)
        {
            var where = $"{o.Number} {r.Label}.{it.Sequence} -> {u.ChildSourceId}";
            var template = new PUsage { ChildSourceId = u.ChildSourceId, Quantity = u.Quantity, Designators = u.ReferenceDesignators, Unit = u.Unit ?? "" };
            if (u.Quantity <= 0) Err(T, id, "BOM-QTY", $"{where}: quantity {u.Quantity} must be greater than 0.");
            if (u.FindNumber is { } f && f <= 0) Err(T, id, "BOM-FIND", $"{where}: find number must be positive.");
            template.FindNumber = u.FindNumber is > 0 ? u.FindNumber.Value : nextFind++;
            if (u.FindNumber is null) Decide("find-number-assigned");
            foreach (var d in u.ReferenceDesignators)
                if (!designators.Add(d)) Err(T, id, "BOM-DUP-DESIGNATOR", $"{where}: reference designator '{d}' is used twice under one parent iteration.");
            var unitText = u.Unit is null ? null : UnitFor(u.Unit, T, id, where);
            template.Unit = unitText ?? "";
            template.Notes = u.Notes;

            if (loading && u.EffectivityRanges.Count > 0)
            {
                foreach (var e in u.EffectivityRanges)
                {
                    var range = MapRange(e, where, T, id);
                    pi.Usages.Add(new PUsage
                    {
                        ChildSourceId = template.ChildSourceId, Quantity = template.Quantity, Unit = template.Unit, FindNumber = template.FindNumber, Designators = template.Designators,
                        Notes = template.Notes, Effectivity = range,
                    });
                    Decide("effectivity-mapped");
                }
                continue;
            }
            var text = !string.IsNullOrWhiteSpace(u.Effectivity) ? u.Effectivity : u.EffectivityRanges.Count > 0 ? string.Join("; ", u.EffectivityRanges.Select(RawText)) : null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                var label = cfg.Effectivity.Load ? "Source effectivity (text only, not loaded)" : "Source effectivity (not loaded, effectivity.load is false)";
                var note = (u.Notes is null ? "" : u.Notes + " | ") + label + ": " + text;
                template.Notes = note.Length > 1000 ? note[..1000] : note;
                Decide("effectivity-preserved-in-notes");
            }
            pi.Usages.Add(template);
        }

        if (!loading) return;
        // Alternates of one find number: same dimension and context, no overlap, each with a range (mirrors cfg.guard_usage_alternatives).
        foreach (var g in pi.Usages.GroupBy(u => u.FindNumber).Where(g => g.Count() > 1))
        {
            var rows = g.ToList();
            if (rows.Any(x => x.Effectivity is null)) continue; // already reported as BOM-DUP-FIND
            for (var a = 0; a < rows.Count; a++)
                for (var b = a + 1; b < rows.Count; b++)
                {
                    var ra = rows[a].Effectivity!; var rb = rows[b].Effectivity!;
                    if (ra.Kind != rb.Kind || !string.Equals(ra.Context, rb.Context, StringComparison.Ordinal))
                        Err(T, id, "EFF-MIXED", $"{o.Number} {r.Label}.{it.Sequence}: find number {g.Key} has alternates effective by {ra.Describe()} and {rb.Describe()}; alternates of one find number must use the same type and context.",
                            "Use one dimension and one end-item context per find number.");
                    else if (ra.Overlaps(rb))
                        Err(T, id, "EFF-OVERLAP", $"{o.Number} {r.Label}.{it.Sequence}: find number {g.Key} has overlapping alternates ({ra.Describe()} and {rb.Describe()}).",
                            "Alternates of one find number must not share a unit; correct the ranges in the source.");
                }
        }
    }

    private EffectivityRange? MapRange(CEffectivity e, string where, string T, string id)
    {
        var kind = e.Kind?.Trim().ToLowerInvariant() ?? "";
        if (!EffectivityKinds.IsValid(kind)) { Err(T, id, "EFF-KIND", $"{where}: effectivity type '{e.Kind}' must be serial, lot or date."); return null; }
        string? context = null;
        if (kind != EffectivityKinds.Date)
        {
            var unit = e.Unit?.Trim();
            if (string.IsNullOrEmpty(unit)) { Err(T, id, "EFF-CONTEXT-MISSING", $"{where}: {kind} effectivity has no end item / unit.", "Name the end item in the source (EffectivityUnit, contextRef or unit)."); return null; }
            if (!cfg.Effectivity.Contexts.TryGetValue(unit, out var mapped))
            {
                if (rd.EffectivityContexts.ContainsKey(unit)) mapped = unit;
                else
                {
                    Err(T, id, "EFF-CONTEXT-UNMAPPED", $"{where}: end item '{unit}' has no mapping to a Keel effectivity context.",
                        $"Add effectivity.contexts['{unit}'] = the id of a context created in Administration, Effectivity{(rd.EffectivityContexts.Count > 0 ? " (" + string.Join(", ", rd.EffectivityContexts.Keys.Order(StringComparer.Ordinal)) + ")" : "")}.");
                    return null;
                }
            }
            if (rd.FromDatabase && !rd.EffectivityContexts.ContainsKey(mapped))
            {
                Err(T, id, "EFF-CONTEXT-UNKNOWN", $"{where}: Keel has no effectivity context '{mapped}'.", "Create the context in Administration, Effectivity first (contexts are configuration, never created by an import).");
                return null;
            }
            context = mapped.ToUpperInvariant();
        }
        else if (!string.IsNullOrWhiteSpace(e.Unit)) Warn(T, id, "EFF-DATE-UNIT-IGNORED", $"{where}: date effectivity has no end-item context; unit '{e.Unit}' is ignored.");

        var openEnded = string.Equals(e.EndCondition, "openEnded", StringComparison.OrdinalIgnoreCase);
        if (openEnded && !string.IsNullOrWhiteSpace(e.To)) Warn(T, id, "EFF-END-IGNORED", $"{where}: the range is open-ended, so its end '{e.To}' is ignored.");
        var (from, e1) = End(kind, context, e.From, "From", where);
        var (to, e2) = openEnded ? ((long?)null, (string?)null) : End(kind, context, e.To, "To", where);
        if (e1 is not null || e2 is not null) { Err(T, id, "EFF-VALUE", e1 ?? e2!, "Serials and lots are whole numbers from 1 (lots may be block names), dates are ISO 8601 and read as UTC."); return null; }
        if (from is null && to is null) { Err(T, id, "EFF-EMPTY", $"{where}: the effectivity is open on both ends; an effectivity needs a From, a To or both."); return null; }
        if (from is { } f && to is { } t && f > t) { Err(T, id, "EFF-RANGE", $"{where}: effectivity From {e.From} is after To {e.To}."); return null; }
        return new EffectivityRange(kind, context, from, to);
    }

    private (long? Value, string? Error) End(string kind, string? context, string? raw, string label, string where)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return (null, null);
        if (kind == EffectivityKinds.Date)
        {
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
                return (null, $"{where}: {label} '{text}' is not a date (ISO 8601 expected).");
            var day = EffectivityRange.DayNumber(DateOnly.FromDateTime(d.UtcDateTime));
            return day < 0 || day > MaxDay ? (null, $"{where}: {label} '{text}' must be between 1970-01-01 and 2200-12-31.") : (day, null);
        }
        if (kind == EffectivityKinds.Lot)
        {
            if (cfg.Effectivity.Blocks.TryGetValue(context!, out var mine) && mine.TryGetValue(text, out var b1)) return (b1, null);
            if (rd.EffectivityContexts.TryGetValue(context!, out var theirs) && theirs.TryGetValue(text, out var b2)) return (b2, null);
        }
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > MaxSerial)
            return (null, kind == EffectivityKinds.Lot
                ? $"{where}: {label} '{text}' is not a lot number or a block name of context {context} (map it in effectivity.blocks)."
                : $"{where}: {label} '{text}' is not a serial number (a whole number from 1 to {MaxSerial.ToString("N0", CultureInfo.InvariantCulture)}).");
        return (n, null);
    }

    /// <summary>Serial numbers per context chosen from the data (every range boundary and its neighbour), thinned evenly: the reconciliation compares the as-of-serial explosion at these.</summary>
    private void FinishEffectivity(Dictionary<string, PPart> parts)
    {
        _plan.EffectivitySerials.Clear();
        var used = parts.Values.SelectMany(p => p.Revisions).SelectMany(r => r.Iterations).SelectMany(i => i.Usages).Where(u => u.Effectivity is { Kind: EffectivityKinds.Serial }).ToList();
        _plan.EffectivityLines = used.Count;
        foreach (var g in used.GroupBy(u => u.Effectivity!.Context!))
        {
            var marks = new SortedSet<long>();
            foreach (var u in g)
            {
                var e = u.Effectivity!;
                if (e.From is { } fr) { marks.Add(fr); if (fr > 1) marks.Add(fr - 1); }
                if (e.To is { } to) { marks.Add(to); if (to < MaxSerial) marks.Add(to + 1); }
            }
            var all = marks.ToList();
            var take = Math.Max(1, cfg.Effectivity.SampleSerials);
            var chosen = all.Count <= take ? all : Enumerable.Range(0, take).Select(i => all[(int)Math.Round(i * (all.Count - 1) / (double)Math.Max(1, take - 1))]).Distinct().ToList();
            _plan.EffectivitySerials[g.Key] = chosen;
        }
    }

    // ------------------------------------------------------------------------------------------------ mapping file checks for both sections
    private static void ValidateClassificationAndEffectivityConfig(MappingConfig cfg, ReferenceData rd, Action<string, string, string?> e)
    {
        if (cfg.Classification.Scope is not ("allIterations" or "latestIteration")) e("MAP-CLASS-SCOPE", $"classification.scope '{cfg.Classification.Scope}' must be allIterations or latestIteration.", null);
        foreach (var (src, to) in cfg.Classification.ClassMap)
            if (string.IsNullOrWhiteSpace(to)) e("MAP-CLASS-TARGET", $"classification.classMap['{src}'] must not be empty.", null);
        foreach (var (cls, map) in cfg.Classification.Attributes)
            foreach (var (src, to) in map)
                if (string.IsNullOrWhiteSpace(to) || !ClassAttributeName.IsMatch(to)) e("MAP-CLASS-ATTR-TARGET", $"classification.attributes['{cls}']['{src}'] -> '{to}' is not a valid class attribute name (lower case letters, digits and underscores, starting with a letter).", null);
        if (cfg.Effectivity.RevisionLevel) { /* refused by the pipeline: NotSupportedException */ }
        if (cfg.Effectivity.SampleSerials < 1 || cfg.Effectivity.SampleSerials > 50) e("MAP-EFF-SAMPLES", "effectivity.sampleSerials must be between 1 and 50.", null);
        foreach (var (src, to) in cfg.Effectivity.Contexts)
        {
            if (string.IsNullOrWhiteSpace(to)) e("MAP-EFF-CONTEXT", $"effectivity.contexts['{src}'] must name a Keel context.", null);
            else if (rd.FromDatabase && !rd.EffectivityContexts.ContainsKey(to)) e("MAP-EFF-CONTEXT", $"effectivity.contexts['{src}'] -> '{to}' is not an effectivity context of Keel ({string.Join(", ", rd.EffectivityContexts.Keys.Order(StringComparer.Ordinal))}).", "Create the context in Administration first.");
        }
        foreach (var (ctx, blocks) in cfg.Effectivity.Blocks)
        {
            if (rd.FromDatabase && !rd.EffectivityContexts.ContainsKey(ctx)) e("MAP-EFF-BLOCKS", $"effectivity.blocks names context '{ctx}', which Keel does not have.", null);
            foreach (var (name, n) in blocks) if (n < 1 || (long)n > MaxSerial) e("MAP-EFF-BLOCKS", $"effectivity.blocks['{ctx}']['{name}'] = {n} must be a positive whole number.", null);
        }
    }
}
