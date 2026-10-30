using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Keel.Cad.Structure;

/// <summary>
/// A small reader of the product structure in an ISO 10303-21 (STEP physical file) exchange file, written for Keel (no third-party parser,
/// no geometry kernel, no vendor SDK). It understands what AP242 and AP214 (and AP203) files share for assemblies:
/// <c>PRODUCT</c>, <c>PRODUCT_DEFINITION_FORMATION</c> (also with specified source), <c>PRODUCT_DEFINITION</c> and
/// <c>NEXT_ASSEMBLY_USAGE_OCCURRENCE</c> / <c>ASSEMBLY_COMPONENT_USAGE</c>. Geometry, PMI, colours, placements and every other entity are skipped
/// unread. Each usage occurrence counts as one piece, so the quantity of a child is the number of occurrences under its parent; the occurrence
/// id becomes the find number when it is a unique whole number under that parent; the reference designator is kept when it looks like one.
/// Explicit quantity entities (<c>QUANTIFIED_ASSEMBLY_COMPONENT_USAGE</c>) are not read (documented limit, FEAT-CAD-7).
/// </summary>
public static partial class StepStructureReader
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_./]{0,39}$")]
    private static partial Regex DesignatorPattern();

    internal sealed record Ref(int Id);
    internal sealed record Enumeration(string Name);
    internal sealed record Typed(string Name, List<object?> Args);
    private sealed record Measure(string Type, decimal? Value);

    private sealed record Product(string Id, string Name);
    private sealed record Formation(int ProductId, string? MakeOrBuy);
    private sealed record Occurrence(string Id, int ParentDefinition, int ChildDefinition, string? Designator, int? QuantityRef = null, int Entity = 0, string? Name = null);

    private const int MaxEntities = 5_000_000;
    private const int MaxNesting = 32;

    public static StructureModel Read(string text, StructureReadOptions options)
    {
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        if (!text.AsSpan(start).StartsWith("ISO-10303-21", StringComparison.Ordinal)) throw new StructureFormatException("The file is not an ISO 10303-21 (STEP) exchange file.");
        var dataAt = IndexOfKeyword(text, "DATA", start);
        if (dataAt < 0) throw new StructureFormatException("The STEP file has no DATA section.");
        var header = text.Substring(start, Math.Min(dataAt - start, 20_000));
        var protocol = ProtocolOf(header);

        var products = new Dictionary<int, Product>();
        var formations = new Dictionary<int, Formation>();
        var definitions = new Dictionary<int, int>(); // product definition -> formation
        var measures = new Dictionary<int, Measure>();
        var occurrences = new List<Occurrence>();
        var geo = options.Placements ? new StepGeometry() : null;

        var pos = SkipStatement(text, dataAt); // "DATA;" or the edition 3 form "DATA('name',(schema));"
        var entities = 0;
        while (true)
        {
            pos = SkipBlank(text, pos);
            if (pos >= text.Length) throw new StructureFormatException("The STEP DATA section is not closed (ENDSEC missing).");
            if (text[pos] != '#')
            {
                if (string.CompareOrdinal(text, pos, "ENDSEC", 0, 6) == 0)
                {
                    // Edition 3 files may carry more than one DATA section (with ANCHOR or REFERENCE sections between them).
                    var next = NextDataSection(text, SkipStatement(text, pos));
                    if (next < 0) break;
                    pos = SkipStatement(text, next);
                    continue;
                }
                throw new StructureFormatException($"Unexpected text in the STEP DATA section near entity {entities + 1}.");
            }
            if (++entities > MaxEntities) throw new StructureFormatException("The STEP file has too many entities.");
            pos++;
            var idStart = pos;
            while (pos < text.Length && char.IsAsciiDigit(text[pos])) pos++;
            if (pos == idStart || !int.TryParse(text.AsSpan(idStart, pos - idStart), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                throw new StructureFormatException($"Bad entity number near entity {entities}.");
            pos = SkipBlank(text, pos);
            if (pos >= text.Length || text[pos] != '=') throw new StructureFormatException($"Entity #{id} has no '='.");
            pos = SkipBlank(text, pos + 1);
            if (pos < text.Length && text[pos] == '(')
            {
                // Complex entity instance. Nearly all are geometry (skipped unread); one that carries a usage is parsed.
                var end = SkipStatement(text, pos);
                var slice = text.AsSpan(pos, end - pos);
                if (slice.Contains("PRODUCT_DEFINITION_RELATIONSHIP", StringComparison.Ordinal)
                    && (slice.Contains("ASSEMBLY_COMPONENT_USAGE", StringComparison.Ordinal) || slice.Contains("NEXT_ASSEMBLY_USAGE_OCCURRENCE", StringComparison.Ordinal))
                    && ComplexUsage(text.Substring(pos, end - pos), id) is { } complex)
                    occurrences.Add(complex);
                else if (geo is not null && slice.Contains("REPRESENTATION_RELATIONSHIP", StringComparison.Ordinal) && slice.Contains("WITH_TRANSFORMATION", StringComparison.Ordinal))
                    geo.AddRelationship(id, ComplexParts(text.Substring(pos, end - pos), id));
                else if (geo is not null && slice.Contains("LENGTH_UNIT", StringComparison.Ordinal))
                    geo.AddLengthUnit(ComplexParts(text.Substring(pos, end - pos), id));
                pos = end;
                continue;
            }
            var kwStart = pos;
            while (pos < text.Length && (char.IsAsciiLetterOrDigit(text[pos]) || text[pos] == '_')) pos++;
            var keyword = text.AsSpan(kwStart, pos - kwStart);
            if (geo is not null && StepGeometry.Wants(keyword))
            {
                var geoName = keyword.ToString().ToUpperInvariant();
                pos = SkipBlank(text, pos);
                var geoArgs = ParseList(text, ref pos, 0, id);
                pos = SkipBlank(text, pos);
                if (pos >= text.Length || text[pos] != ';') throw new StructureFormatException($"Entity #{id} is not terminated by ';'.");
                pos++;
                geo.Add(id, geoName, geoArgs);
                continue;
            }
            var wanted = Wanted(keyword);
            if (!wanted) { pos = SkipStatement(text, pos); continue; }
            pos = SkipBlank(text, pos);
            var args = ParseList(text, ref pos, 0, id);
            pos = SkipBlank(text, pos);
            if (pos >= text.Length || text[pos] != ';') throw new StructureFormatException($"Entity #{id} is not terminated by ';'.");
            pos++;
            var name = keyword.ToString().ToUpperInvariant();
            switch (name)
            {
                case "PRODUCT":
                    products[id] = new Product(Text(args, 0) ?? "", Text(args, 1) ?? "");
                    break;
                case "PRODUCT_DEFINITION_FORMATION" or "PRODUCT_DEFINITION_FORMATION_WITH_SPECIFIED_SOURCE":
                    if (Reference(args, 2) is { } p) formations[id] = new Formation(p, args.Count > 3 && args[3] is Enumeration e ? e.Name : null);
                    break;
                case "PRODUCT_DEFINITION":
                    if (Reference(args, 2) is { } f) definitions[id] = f;
                    break;
                case "MEASURE_WITH_UNIT":
                    if (args.Count > 0 && args[0] is Typed t) measures[id] = new Measure(t.Name.ToUpperInvariant(), t.Args.Count > 0 && t.Args[0] is string v ? ParseNumber(v) : null);
                    break;
                default: // NEXT_ASSEMBLY_USAGE_OCCURRENCE, SPECIFIED_HIGHER_USAGE_OCCURRENCE, ASSEMBLY_COMPONENT_USAGE, QUANTIFIED_ASSEMBLY_COMPONENT_USAGE (quantity: 7th argument)
                    if (Reference(args, 3) is { } parent && Reference(args, 4) is { } child)
                        occurrences.Add(new Occurrence(Text(args, 0) ?? "", parent, child, Text(args, 5), name == "QUANTIFIED_ASSEMBLY_COMPONENT_USAGE" ? Reference(args, 6) : null, id, Text(args, 1)));
                    break;
            }
        }
        if (products.Count == 0) throw new StructureFormatException("The STEP file has no PRODUCT entities.");

        // Several PRODUCT entities with the same id are one part number (an exporter writes one product per configuration or per copied shape):
        // they are merged into the first of them, so the BOM gets one line and one part, not two parts with one number.
        var canonical = new Dictionary<int, int>();
        var firstWithId = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (k, pr) in products.OrderBy(kv => kv.Key))
        {
            var key = pr.Id.Trim();
            canonical[k] = key.Length > 0 && !firstWithId.TryAdd(key, k) ? firstWithId[key] : k;
        }

        int? ProductOf(int definition) =>
            definitions.TryGetValue(definition, out var fm) && formations.TryGetValue(fm, out var fo) && products.ContainsKey(fo.ProductId) ? canonical[fo.ProductId] : null;

        // Aggregate occurrences: one line per (parent product, child product). An explicit quantity (QUANTIFIED_ASSEMBLY_COMPONENT_USAGE) counts
        // as that many pieces when it is a count; any other measure is not a piece count, so it is noted and the occurrence counts as one.
        var lineMap = new Dictionary<(int, int), (decimal Count, List<string> Refs, List<string> Notes, string? Find, List<string> Ids)>();
        var order = new List<(int, int)>();
        foreach (var o in occurrences)
        {
            if (ProductOf(o.ParentDefinition) is not { } pp || ProductOf(o.ChildDefinition) is not { } cp) continue;
            if (!lineMap.TryGetValue((pp, cp), out var agg)) { agg = (0m, [], [], null, []); order.Add((pp, cp)); }
            var refs = agg.Refs; var notes = agg.Notes; var ids = agg.Ids;
            if (!string.IsNullOrWhiteSpace(o.Designator))
            {
                var d = o.Designator.Trim();
                if (DesignatorPattern().IsMatch(d)) refs.Add(d); else notes.Add(d.Length > 80 ? d[..80] : d);
            }
            var pieces = 1m;
            if (o.QuantityRef is { } qr && measures.TryGetValue(qr, out var measure) && measure.Value is { } value)
            {
                if (measure.Type == "COUNT_MEASURE" && value > 0) pieces = value;
                else notes.Add($"quantity {value.ToString(CultureInfo.InvariantCulture)} ({measure.Type}) is not a piece count and was not applied");
            }
            ids.Add(o.Id);
            lineMap[(pp, cp)] = (agg.Count + pieces, refs, notes, agg.Find, ids);
        }
        var parents = order.Select(k => k.Item1).ToHashSet();
        var childrenSet = order.Select(k => k.Item2).ToHashSet();

        int rootId;
        var roots = products.Keys.Where(k => canonical[k] == k).Where(k => (parents.Contains(k) || order.Count == 0) && !childrenSet.Contains(k)).OrderBy(k => k).ToList();
        if (!string.IsNullOrWhiteSpace(options.RootNumber))
        {
            var wantedRoot = options.RootNumber.Trim();
            var hit = products.Where(kv => canonical[kv.Key] == kv.Key && kv.Value.Id.Trim() == wantedRoot && (parents.Contains(kv.Key) || order.Count == 0 || childrenSet.Contains(kv.Key))).Select(kv => kv.Key).ToList();
            rootId = hit.Count == 1 ? hit[0] : throw new StructureFormatException($"The STEP file has no single product with the number '{Trunc(wantedRoot)}'.");
        }
        else if (roots.Count == 1) rootId = roots[0];
        else
            throw new StructureFormatException(roots.Count == 0 ? "The STEP file has no top-level product."
                : $"The STEP file has {roots.Count} top-level products ({string.Join(", ", roots.Take(5).Select(r => Trunc(products[r].Id)))}); give the number of the one to import as the root.");

        // Keep what is reachable from the chosen root.
        var reachable = new HashSet<int> { rootId };
        var stack = new Stack<int>([rootId]);
        var byParent = order.GroupBy(k => k.Item1).ToDictionary(g => g.Key, g => g.Select(k => k.Item2).ToList());
        while (stack.Count > 0)
            foreach (var c in byParent.GetValueOrDefault(stack.Pop()) ?? [])
                if (reachable.Add(c)) stack.Push(c);

        var makeOrBuy = new Dictionary<int, string?>();
        foreach (var fo in formations.Values.Where(f => products.ContainsKey(f.ProductId))) makeOrBuy.TryAdd(canonical[fo.ProductId], fo.MakeOrBuy);
        // Placements and boxes (ADR-0052): resolved after the whole file is read, because the entities that carry them may come in any order.
        var productDefinitions = new Dictionary<int, List<int>>(); // canonical product -> its product definitions
        foreach (var (pd, fm) in definitions)
            if (formations.TryGetValue(fm, out var fo2) && products.ContainsKey(fo2.ProductId)) { var cp2 = canonical[fo2.ProductId]; if (!productDefinitions.TryGetValue(cp2, out var l2)) productDefinitions[cp2] = l2 = []; l2.Add(pd); }
        var placed = geo?.Resolve(occurrences.Where(o => ProductOf(o.ParentDefinition) is { } a && reachable.Contains(a) && ProductOf(o.ChildDefinition) is not null)
            .Select(o => (o.Entity, o.ParentDefinition, o.ChildDefinition)).ToList());
        var parts = reachable.OrderBy(k => k).Select(k =>
        {
            var pr = products[k];
            var number = string.IsNullOrWhiteSpace(pr.Id) ? null : pr.Id.Trim();
            var name = string.IsNullOrWhiteSpace(pr.Name) ? number ?? $"Product #{k}" : pr.Name.Trim();
            var source = makeOrBuy.GetValueOrDefault(k) switch { { } s when s.Contains("BOUGHT", StringComparison.Ordinal) => "Buy", { } s when s.Contains("MADE", StringComparison.Ordinal) => "Make", _ => null };
            return new StructurePart("#" + k.ToString(CultureInfo.InvariantCulture), number, name.Length > 200 ? name[..200] : name, byParent.ContainsKey(k) ? "Assembly" : null, null, source,
                new Dictionary<string, string>(), [], geo?.BoxOf(productDefinitions.GetValueOrDefault(k) ?? []));
        }).ToList();

        var lines = new List<StructureLine>();
        foreach (var group in order.Where(k => reachable.Contains(k.Item1)).GroupBy(k => k.Item1))
        {
            var usedFinds = new HashSet<int>();
            foreach (var key in group)
            {
                var agg = lineMap[key];
                int? find = null;
                if (agg.Ids.Count == 1 && int.TryParse(agg.Ids[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 999_999 && usedFinds.Add(n)) find = n;
                lines.Add(new StructureLine("#" + key.Item1.ToString(CultureInfo.InvariantCulture), "#" + key.Item2.ToString(CultureInfo.InvariantCulture), agg.Count, null, find,
                    [.. agg.Refs.Distinct(StringComparer.Ordinal)], agg.Notes.Count == 0 ? null : string.Join("; ", agg.Notes.Distinct(StringComparer.Ordinal))));
            }
        }
        var occurrenceList = new List<StructureOccurrence>();
        if (placed is not null)
            foreach (var o in occurrences.OrderBy(o => o.Entity))
            {
                if (ProductOf(o.ParentDefinition) is not { } pp2 || !reachable.Contains(pp2) || ProductOf(o.ChildDefinition) is not { } cp3) continue;
                if (occurrenceList.Count >= StructureLimits.MaxLines) break;
                var occName = string.IsNullOrWhiteSpace(o.Name) ? null : o.Name.Trim();
                occurrenceList.Add(new StructureOccurrence("#" + pp2.ToString(CultureInfo.InvariantCulture), "#" + cp3.ToString(CultureInfo.InvariantCulture),
                    o.Id.Length > 100 ? o.Id[..100] : o.Id, occName is { Length: > 200 } ? occName[..200] : occName, placed.GetValueOrDefault(o.Entity)));
            }
        return new StructureModel(protocol, "#" + rootId.ToString(CultureInfo.InvariantCulture), parts, lines)
        {
            Occurrences = occurrenceList,
            LengthUnit = geo?.UnitLabel,
            LengthUnitMm = geo?.UnitMm,
        };
    }

    private static bool Wanted(ReadOnlySpan<char> keyword) =>
        keyword.Equals("PRODUCT", StringComparison.OrdinalIgnoreCase) || keyword.Equals("PRODUCT_DEFINITION", StringComparison.OrdinalIgnoreCase)
        || keyword.Equals("PRODUCT_DEFINITION_FORMATION", StringComparison.OrdinalIgnoreCase)
        || keyword.Equals("PRODUCT_DEFINITION_FORMATION_WITH_SPECIFIED_SOURCE", StringComparison.OrdinalIgnoreCase)
        || keyword.Equals("NEXT_ASSEMBLY_USAGE_OCCURRENCE", StringComparison.OrdinalIgnoreCase)
        || keyword.Equals("SPECIFIED_HIGHER_USAGE_OCCURRENCE", StringComparison.OrdinalIgnoreCase)
        || keyword.Equals("ASSEMBLY_COMPONENT_USAGE", StringComparison.OrdinalIgnoreCase)
        || keyword.Equals("QUANTIFIED_ASSEMBLY_COMPONENT_USAGE", StringComparison.OrdinalIgnoreCase)
        || keyword.Equals("MEASURE_WITH_UNIT", StringComparison.OrdinalIgnoreCase);

    private static string Trunc(string s) => s.Length <= 40 ? s : s[..40] + "...";

    /// <summary>The protocol is decided by the FILE_SCHEMA entry only; other header text (file names, descriptions) must not decide it.</summary>
    private static string ProtocolOf(string header)
    {
        var at = header.IndexOf("FILE_SCHEMA", StringComparison.Ordinal);
        var schema = at < 0 ? "" : header[at..];
        var end = schema.IndexOf(';', StringComparison.Ordinal);
        if (end > 0) schema = schema[..end];
        return schema.Contains("AP242", StringComparison.OrdinalIgnoreCase) || schema.Contains("MANAGED_MODEL_BASED_3D_ENGINEERING", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(schema, @"\b10303\s+442\b") ? "STEP AP242"
            : schema.Contains("AP214", StringComparison.OrdinalIgnoreCase) || schema.Contains("AUTOMOTIVE_DESIGN", StringComparison.OrdinalIgnoreCase) ? "STEP AP214"
            : schema.Contains("CONFIG_CONTROL_DESIGN", StringComparison.OrdinalIgnoreCase) || schema.Contains("AP203", StringComparison.OrdinalIgnoreCase) ? "STEP AP203" : "STEP";
    }

    /// <summary>The position of the next <c>DATA</c> section keyword after <paramref name="from"/> (skipping other sections), or -1.</summary>
    private static int NextDataSection(string text, int from)
    {
        var pos = from;
        while (true)
        {
            pos = SkipBlank(text, pos);
            if (pos >= text.Length || text[pos] == '#') return -1;
            var kwStart = pos;
            while (pos < text.Length && (char.IsAsciiLetterOrDigit(text[pos]) || text[pos] == '_')) pos++;
            var kw = text.AsSpan(kwStart, pos - kwStart);
            if (kw.Equals("END-ISO-10303-21", StringComparison.Ordinal) || kw.Length == 0) return -1;
            if (kw.Equals("DATA", StringComparison.Ordinal)) return kwStart;
            // ANCHOR; REFERENCE; SIGNATURE; and other sections: skip to their ENDSEC;
            var e = text.IndexOf("ENDSEC", pos, StringComparison.Ordinal);
            if (e < 0) return -1;
            pos = SkipStatement(text, e);
        }
    }

    /// <summary>Reads a complex entity instance such as <c>(ASSEMBLY_COMPONENT_USAGE($) NEXT_ASSEMBLY_USAGE_OCCURRENCE() PRODUCT_DEFINITION_RELATIONSHIP('1','','',#2,#3) QUANTIFIED_ASSEMBLY_COMPONENT_USAGE(#4))</c>:
    /// each partial entity lists only its own attributes, so the usage is put together from the ones present.</summary>
    private static Occurrence? ComplexUsage(string statement, int entity)
    {
        if (ComplexParts(statement, entity) is not { } parts) return null;
        if (!parts.TryGetValue("PRODUCT_DEFINITION_RELATIONSHIP", out var rel)) return null;
        if (Reference(rel, 3) is not { } parent || Reference(rel, 4) is not { } child) return null;
        parts.TryGetValue("ASSEMBLY_COMPONENT_USAGE", out var acu);
        parts.TryGetValue("QUANTIFIED_ASSEMBLY_COMPONENT_USAGE", out var qacu);
        return new Occurrence(Text(rel, 0) ?? "", parent, child, acu is null ? null : Text(acu, 0), qacu is null ? null : Reference(qacu, 0), entity, Text(rel, 1));
    }

    /// <summary>The partial entities of a complex instance by upper-case name, each with its own attributes; null when the instance is not well formed.</summary>
    private static Dictionary<string, List<object?>>? ComplexParts(string statement, int entity)
    {
        var pos = 0;
        pos = SkipBlank(statement, pos);
        if (pos >= statement.Length || statement[pos] != '(') return null;
        pos++;
        var parts = new Dictionary<string, List<object?>>(StringComparer.Ordinal);
        while (true)
        {
            pos = SkipBlank(statement, pos);
            if (pos >= statement.Length) return null;
            if (statement[pos] == ')') break;
            var s = pos;
            while (pos < statement.Length && (char.IsAsciiLetterOrDigit(statement[pos]) || statement[pos] == '_')) pos++;
            if (pos == s) return null;
            var name = statement.Substring(s, pos - s).ToUpperInvariant();
            pos = SkipBlank(statement, pos);
            parts[name] = ParseList(statement, ref pos, 0, entity);
            if (parts.Count > 32) return null;
        }
        return parts;
    }

    internal static decimal? ParseNumber(string token) =>
        decimal.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? Text(List<object?> args, int i) => i < args.Count ? args[i] as string : null;
    private static int? Reference(List<object?> args, int i) => i < args.Count && args[i] is Ref r ? r.Id : null;

    private static int IndexOfKeyword(string text, string keyword, int from)
    {
        var at = from;
        while ((at = text.IndexOf(keyword, at, StringComparison.Ordinal)) >= 0)
        {
            var end = at + keyword.Length;
            var before = at == 0 || !(char.IsAsciiLetterOrDigit(text[at - 1]) || text[at - 1] == '_');
            var after = end < text.Length && SkipBlank(text, end) is var q && q < text.Length && (text[q] == ';' || text[q] == '(');
            if (before && after) return at;
            at = end;
        }
        return -1;
    }

    /// <summary>Skips whitespace and <c>/* comments */</c>.</summary>
    private static int SkipBlank(string text, int pos)
    {
        while (pos < text.Length)
        {
            if (char.IsWhiteSpace(text[pos])) pos++;
            else if (text[pos] == '/' && pos + 1 < text.Length && text[pos + 1] == '*')
            {
                var end = text.IndexOf("*/", pos + 2, StringComparison.Ordinal);
                if (end < 0) throw new StructureFormatException("A comment in the STEP file is not closed.");
                pos = end + 2;
            }
            else break;
        }
        return pos;
    }

    /// <summary>Skips to just after the ';' that ends the current statement, honouring string literals and comments.</summary>
    private static int SkipStatement(string text, int pos)
    {
        while (pos < text.Length)
        {
            var c = text[pos];
            if (c == '\'')
            {
                pos++;
                while (pos < text.Length)
                {
                    if (text[pos] == '\'') { if (pos + 1 < text.Length && text[pos + 1] == '\'') { pos += 2; continue; } break; }
                    pos++;
                }
                pos++;
            }
            else if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '*') pos = SkipBlank(text, pos);
            else if (c == ';') return pos + 1;
            else pos++;
        }
        throw new StructureFormatException("The STEP file ends inside an entity.");
    }

    private static List<object?> ParseList(string text, ref int pos, int depth, int entity)
    {
        if (depth > MaxNesting) throw new StructureFormatException($"Entity #{entity} is nested too deeply.");
        if (pos >= text.Length || text[pos] != '(') throw new StructureFormatException($"Entity #{entity} has no argument list.");
        pos++;
        var items = new List<object?>();
        pos = SkipBlank(text, pos);
        if (pos < text.Length && text[pos] == ')') { pos++; return items; }
        while (true)
        {
            pos = SkipBlank(text, pos);
            if (pos >= text.Length) throw new StructureFormatException($"Entity #{entity} ends inside its arguments.");
            var c = text[pos];
            object? value;
            if (c == '\'') value = ParseString(text, ref pos, entity);
            else if (c == '#')
            {
                var s = ++pos;
                while (pos < text.Length && char.IsAsciiDigit(text[pos])) pos++;
                value = int.TryParse(text.AsSpan(s, pos - s), NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? new Ref(r) : throw new StructureFormatException($"Entity #{entity} has a bad reference.");
            }
            else if (c is '$' or '*') { pos++; value = null; }
            else if (c == '.')
            {
                var end = text.IndexOf('.', pos + 1);
                if (end < 0) throw new StructureFormatException($"Entity #{entity} has an unclosed enumeration.");
                value = new Enumeration(text.Substring(pos + 1, end - pos - 1).ToUpperInvariant());
                pos = end + 1;
            }
            else if (c == '(') value = ParseList(text, ref pos, depth + 1, entity);
            else
            {
                var s = pos;
                while (pos < text.Length && text[pos] is not (',' or ')' or '(')) pos++;
                var token = text.AsSpan(s, pos - s).Trim().ToString();
                if (pos < text.Length && text[pos] == '(') value = new Typed(token, ParseList(text, ref pos, depth + 1, entity)); // typed value such as COUNT_MEASURE(4.)
                else value = token;
            }
            items.Add(value);
            pos = SkipBlank(text, pos);
            if (pos >= text.Length) throw new StructureFormatException($"Entity #{entity} ends inside its arguments.");
            if (text[pos] == ',') { pos++; continue; }
            if (text[pos] == ')') { pos++; return items; }
            throw new StructureFormatException($"Entity #{entity} has an unexpected character in its arguments.");
        }
    }

    private static string ParseString(string text, ref int pos, int entity)
    {
        var sb = new StringBuilder();
        pos++;
        while (true)
        {
            if (pos >= text.Length) throw new StructureFormatException($"Entity #{entity} has an unterminated string.");
            var c = text[pos];
            if (c == '\'')
            {
                if (pos + 1 < text.Length && text[pos + 1] == '\'') { sb.Append('\''); pos += 2; continue; }
                pos++;
                break;
            }
            if (c == '\\' && pos + 3 < text.Length && text[pos + 1] == 'X' && text[pos + 2] == '2' && text[pos + 3] == '\\')
            {
                var end = text.IndexOf("\\X0\\", pos + 4, StringComparison.Ordinal);
                if (end > 0 && (end - pos - 4) % 4 == 0)
                {
                    for (var h = pos + 4; h < end; h += 4)
                        if (int.TryParse(text.AsSpan(h, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)) sb.Append((char)code);
                    pos = end + 4;
                    continue;
                }
            }
            if (c == '\\' && pos + 3 < text.Length && text[pos + 1] == 'S' && text[pos + 2] == '\\')
            {
                // \S\c : the character c with the high bit set (ISO 8859-1 upper half), the escape of edition 1 and 2 files.
                sb.Append((char)(text[pos + 3] + 128));
                pos += 4;
                continue;
            }
            if (c == '\\' && pos + 1 < text.Length && text[pos + 1] == '\\') { sb.Append('\\'); pos += 2; continue; }
            sb.Append(c);
            pos++;
        }
        return sb.ToString();
    }
}
