using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Keel.Core;
using Keel.Migration.Canonical;

namespace Keel.Migration.Mapping;

/// <summary>
/// Classification (ADR-0022, ADR-0023, FUP-2): the staged hierarchy is merged with Keel's classes (missing classes are created, existing ones never changed, differences
/// reported as conflicts or warnings), and every assignment is validated against the effective attribute set of its class exactly as native classification does. The class
/// data is iteration-owned, so a validated assignment is written onto the iterations of the part (all of them, or the last one: <c>classification.scope</c>).
/// </summary>
public sealed partial class Mapper
{
    private static readonly Regex ClassCodePattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,39}$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex ClassAttributeName = new(@"^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private sealed class Cls
    {
        public string Code = "";
        public string? Parent;
        public bool Active = true;
        public bool IsNew;
        public bool Bad;
        public string Name = "";
        public string? Description;
        public int SortOrder;
        public List<AttributeSpec> Own = [];
    }

    private readonly Dictionary<string, Cls> _classes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Code, string Json)> _classOfItem = new(StringComparer.Ordinal);

    private IReadOnlyList<AttributeSpec> EffectiveSpecs(string code)
    {
        var chain = new List<Cls>();
        for (var c = _classes.GetValueOrDefault(code); c is not null; c = c.Parent is null ? null : _classes.GetValueOrDefault(c.Parent)) chain.Insert(0, c);
        return [.. chain.SelectMany(c => c.Own)];
    }

    private void MapClassification(CanonicalPackage pkg, Dictionary<string, CObject> srcItems, Dictionary<string, PPart> parts)
    {
        if (!cfg.Classification.Load)
        {
            foreach (var c in pkg.Classifications)
            {
                if (!parts.TryGetValue(c.ItemSourceId, out var part)) { Warn("classification", c.ItemSourceId, "CLASS-ITEM-MISSING", $"Classification '{c.ClassCode}' refers to item '{c.ItemSourceId}', which is not planned for import; it is skipped."); continue; }
                _plan.Classifications.Add(new ClassAssignmentPlan(c.ItemSourceId, part.Number, c.ClassCode, c.Attributes));
            }
            if (_plan.Classifications.Count > 0)
                Warn("classification", "*", "CLASS-STAGED", $"{_plan.Classifications.Count} classification assignments are staged but not loaded (classification.load is false in the mapping).");
            return;
        }

        foreach (var d in rd.Classes.Values)
            _classes[d.Code] = new Cls { Code = d.Code, Parent = d.ParentCode, Active = d.Active, Own = [.. d.OwnAttributes] };
        if (cfg.Classification.CreateMissingClasses) MergeStagedHierarchy(pkg.ClassNodes);

        // One class per part (ADM-10): the source gives the class at item level, possibly repeated (PLM XML repeats it on every revision element).
        foreach (var group in pkg.Classifications.GroupBy(c => c.ItemSourceId, StringComparer.Ordinal))
        {
            var itemId = group.Key;
            if (!srcItems.ContainsKey(itemId)) { Warn("classification", itemId, "CLASS-ITEM-MISSING", $"Classification refers to item '{itemId}', which is not in the package; it is skipped."); continue; }
            if (!parts.TryGetValue(itemId, out var part)) continue; // the part itself has errors; they are reported there
            var distinct = group.Select(c => (Code: c.ClassCode.Trim(), Values: string.Join("\u001f", c.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Key + "=" + (a.Value ?? "")))))
                .Distinct().ToList();
            if (distinct.Count > 1)
            {
                Err("part", itemId, "CLASS-MULTIPLE", $"{part.Number} is classified {distinct.Count} times with different classes or values ({string.Join(", ", distinct.Select(d => d.Code).Distinct())}); a part has one class (ADM-10).",
                    "Keep one classification per item in the source export.");
                continue;
            }
            MapAssignment(group.First(), part, itemId);
        }
    }

    private void MergeStagedHierarchy(List<CClassNode> staged)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fresh = new List<(CClassNode Node, Cls Cls)>();
        foreach (var n in staged)
        {
            const string T = "classification";
            n.Code ??= ""; n.Name ??= "";
            if (!ClassCodePattern.IsMatch(n.Code)) { Err(T, n.Code, "CLASS-CODE-INVALID", $"Class code '{n.Code}' must be 1-40 characters of letters, digits, '.', '_' or '-'."); continue; }
            if (!seen.Add(n.Code)) { Err(T, n.Code, "CLASS-DUP", $"Class code '{n.Code}' appears more than once in the staged hierarchy (codes ignore letter case)."); continue; }
            if (string.IsNullOrWhiteSpace(n.Name) || n.Name.Length > 200) { Err(T, n.Code, "CLASS-NAME", $"Class {n.Code} needs a name of 1-200 characters."); continue; }
            var parent = string.IsNullOrWhiteSpace(n.Parent) ? null : n.Parent.Trim();
            if (_classes.TryGetValue(n.Code, out var existing) && !existing.IsNew)
            {
                CheckExisting(n, existing, parent);
                continue;
            }
            var cls = new Cls { Code = n.Code.Trim(), Parent = parent, IsNew = true, Name = n.Name.Trim(), Description = n.Description?.Trim(), SortOrder = n.SortOrder, Active = !string.Equals(n.Status, "retired", StringComparison.OrdinalIgnoreCase) };
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in n.Attributes)
            {
                a.Name ??= ""; a.DataType ??= "";
                if (!ClassAttributeName.IsMatch(a.Name)) { Err(T, n.Code, "CLASS-ATTR-NAME", $"{n.Code}: attribute name '{a.Name}' must be lower case letters, digits and underscores, starting with a letter."); continue; }
                if (!names.Add(a.Name)) { Err(T, n.Code, "CLASS-ATTR-DUP", $"{n.Code}: attribute '{a.Name}' is defined twice."); continue; }
                if (!AttributeSpecs.DataTypes.Contains(a.DataType)) { Err(T, n.Code, "CLASS-ATTR-TYPE", $"{n.Code}/{a.Name}: data type '{a.DataType}' must be string, number, boolean or enum."); continue; }
                if (a.DataType == "enum" && (a.EnumValues is null || a.EnumValues.Count == 0)) { Err(T, n.Code, "CLASS-ATTR-ENUM", $"{n.Code}/{a.Name}: an enum attribute needs values."); continue; }
                cls.Own.Add(new AttributeSpec(a.Name, string.IsNullOrWhiteSpace(a.Label) ? a.Name : a.Label.Trim(), a.DataType, a.Unit, a.EnumValues, a.Required, a.MaxLength, a.SortOrder, a.HelpText, a.Group, a.Pattern, a.Min, a.Max, a.Retired));
            }
            _classes[cls.Code] = cls;
            fresh.Add((n, cls));
        }

        // Parents must exist (in Keel or staged) and be usable; the order of creation is parents first.
        var ordered = new List<Cls>();
        var pending = fresh.Select(f => f.Cls).ToList();
        while (pending.Count > 0)
        {
            var ready = pending.Where(c => c.Parent is null || (_classes.TryGetValue(c.Parent, out var p) && (!p.IsNew || ordered.Contains(p) || p.Bad))).ToList();
            if (ready.Count == 0)
            {
                foreach (var c in pending)
                {
                    c.Bad = true;
                    if (_classes.ContainsKey(c.Parent!)) Err("classification", c.Code, "CLASS-CYCLE", $"Class {c.Code}: the staged parents form a cycle.");
                    else Err("classification", c.Code, "CLASS-PARENT-MISSING", $"Class {c.Code}: parent '{c.Parent}' is neither a staged class nor a class of Keel.", "Add the parent to the hierarchy file or create it in Administration, Classification.");
                }
                break;
            }
            foreach (var c in ready)
            {
                pending.Remove(c);
                var parent = c.Parent is null ? null : _classes[c.Parent];
                if (parent is { Bad: true }) { c.Bad = true; Err("classification", c.Code, "CLASS-PARENT-FAILED", $"Class {c.Code} cannot be created: its parent {parent.Code} has errors."); continue; }
                if (parent is { Active: false }) { c.Bad = true; Err("classification", c.Code, "CLASS-PARENT-RETIRED", $"Class {c.Code} cannot be created under the retired class {parent.Code}."); continue; }
                var inherited = EffectiveSpecs(parent?.Code ?? "").Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
                var clash = c.Own.FirstOrDefault(a => inherited.Contains(a.Name));
                if (clash is not null) { c.Bad = true; Err("classification", c.Code, "CLASS-ATTR-CLASH", $"Class {c.Code}: attribute '{clash.Name}' is already defined by an ancestor; names are unique along a path."); continue; }
                if (!c.Active) { Warn("classification", c.Code, "CLASS-RETIRED-SKIPPED", $"Class {c.Code} is retired in the source: it is not created and parts cannot be classified in it."); continue; }
                ordered.Add(c);
            }
        }
        foreach (var c in ordered)
            _plan.ClassesToCreate.Add(new PClassNode(c.Code, c.Name, c.Description, c.Parent, c.SortOrder, c.Own));
    }

    /// <summary>A staged class that Keel already has: Keel's definition wins. Differences are reported, never applied (existing classes are not changed by an import).</summary>
    private void CheckExisting(CClassNode n, Cls existing, string? parent)
    {
        const string T = "classification";
        if (!string.Equals(existing.Parent, parent, StringComparison.OrdinalIgnoreCase))
        {
            existing.Bad = true;
            Err(T, n.Code, "CLASS-CONFLICT", $"Class {n.Code} is under '{existing.Parent ?? "(top)"}' in Keel but under '{parent ?? "(top)"}' in the source; an import never moves a class.", "Move the class in Administration, Classification (which checks the classified parts) or fix the source hierarchy.");
            return;
        }
        foreach (var a in n.Attributes)
        {
            var current = EffectiveSpecs(n.Code).FirstOrDefault(x => x.Name == a.Name);
            if (current is null)
            {
                Warn(T, n.Code, "CLASS-ATTR-NOT-ADDED", $"Class {n.Code} exists in Keel without attribute '{a.Name}'; the import does not add attributes to existing classes (values for it are errors).");
                continue;
            }
            if (!string.Equals(current.DataType, a.DataType, StringComparison.Ordinal))
            {
                existing.Bad = true;
                Err(T, n.Code, "CLASS-ATTR-TYPE-MISMATCH", $"Class {n.Code}, attribute '{a.Name}': Keel has type {current.DataType}, the source says {a.DataType}.", "Correct the source hierarchy or the attribute in Keel; classified values cannot change type.");
                continue;
            }
            var differs = a.Required != current.Required || a.MaxLength != current.MaxLength || a.Min != current.Min || a.Max != current.Max
                || (a.EnumValues is not null && current.EnumValues is not null && !a.EnumValues.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(current.EnumValues.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal));
            if (differs) Warn(T, n.Code, "CLASS-ATTR-DIFFERS", $"Class {n.Code}, attribute '{a.Name}': the source definition differs from Keel's (required, length, range or values); Keel's definition is used.");
        }
        if (!existing.Active) Warn(T, n.Code, "CLASS-RETIRED-IN-KEEL", $"Class {n.Code} is retired in Keel; assignments to it are errors.");
    }

    private void MapAssignment(CClassAssignment c, PPart part, string itemId)
    {
        var srcClass = c.ClassCode.Trim();
        var keelCode = cfg.Classification.ClassMap.TryGetValue(srcClass, out var mapped) ? mapped.Trim() : srcClass;
        if (!_classes.TryGetValue(keelCode, out var cls))
        {
            Err("part", itemId, "CLASS-UNKNOWN", $"{part.Number}: class '{keelCode}' (source '{srcClass}') is not a class of Keel{(cfg.Classification.CreateMissingClasses ? " or of the staged hierarchy" : " (classification.createMissingClasses is false)")}.",
                "Add the class to the hierarchy file (classification.json), map it in classification.classMap, or create it in Administration, Classification.");
            return;
        }
        if (cls.Bad) { Err("part", itemId, "CLASS-FAILED", $"{part.Number}: class {cls.Code} has errors and is not created.", "Fix the hierarchy errors (CLASS-*) first."); return; }
        if (!cls.Active) { Err("part", itemId, "CLASS-RETIRED", $"{part.Number}: class {cls.Code} is retired; parts cannot be classified in a retired class.", "Map the source class to an active class (classification.classMap) or restore the class in Administration."); return; }

        var specs = EffectiveSpecs(cls.Code);
        var byName = specs.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var values = new JsonObject();
        var ok = true;
        foreach (var (src, raw) in c.Attributes)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue; // an empty value is an absent value (CSV rows carry every column)
            var target = AttributeTarget(srcClass, src);
            if (!byName.TryGetValue(target, out var spec))
            {
                Err("part", itemId, "CLASS-ATTR-UNKNOWN", $"{part.Number}: source attribute '{src}' (class attribute '{target}') is not an attribute of class {cls.Code}.",
                    $"Add classification.attributes['{srcClass}']['{src}'] = one of {string.Join(", ", specs.Select(s => s.Name))}, or define the attribute on the class.");
                ok = false;
                continue;
            }
            if (values.ContainsKey(target)) { Err("part", itemId, "CLASS-ATTR-DUP", $"{part.Number}: two source attributes map to class attribute '{target}'."); ok = false; continue; }
            values[target] = Coerce(spec, raw);
        }
        if (!ok) return;
        var (normalized, errors) = AttributeSpecs.Validate(specs, values);
        // "X is required" next to "X must be a number" is noise: the supplied value is the problem.
        errors = [.. errors.Where(e => !values.Any(v => byName.TryGetValue(v.Key, out var sp) && e == $"{sp.Label} is required."))];
        if (errors.Count > 0)
        {
            foreach (var e in errors)
                Err("part", itemId, "CLASS-VALUE-INVALID", $"{part.Number} (class {cls.Code}): {e}", "Correct the value in the source or the attribute definition; a value is never altered silently.");
            return;
        }
        _classOfItem[itemId] = (cls.Code, normalized.ToJsonString());
        _plan.Classifications.Add(new ClassAssignmentPlan(itemId, part.Number, srcClass, c.Attributes) { KeelClassCode = cls.Code, AttributesJson = normalized.ToJsonString() });
    }

    private string AttributeTarget(string srcClass, string srcAttribute)
    {
        if (cfg.Classification.Attributes.TryGetValue(srcClass, out var own) && own.TryGetValue(srcAttribute, out var t1)) return t1;
        if (cfg.Classification.Attributes.TryGetValue("*", out var all) && all.TryGetValue(srcAttribute, out var t2)) return t2;
        return srcAttribute;
    }

    /// <summary>Source values are text; booleans are spelled in several ways. Anything else is passed on and validated as the attribute's type (a wrong value is an error, not a guess).</summary>
    private static JsonNode? Coerce(AttributeSpec spec, string? raw)
    {
        if (raw is null) return null;
        if (spec.DataType == "boolean")
            switch (raw.Trim().ToLowerInvariant())
            {
                case "true" or "yes" or "y" or "1" or "t": return JsonValue.Create(true);
                case "false" or "no" or "n" or "0" or "f": return JsonValue.Create(false);
            }
        return JsonValue.Create(raw);
    }

    /// <summary>After the cross-object checks: the validated class data goes onto the iterations of the parts that are still planned.</summary>
    private void FinishClassification(Dictionary<string, PPart> parts)
    {
        if (!cfg.Classification.Load) return;
        _plan.Classifications.RemoveAll(c => !parts.ContainsKey(c.ItemSourceId));
        foreach (var (itemId, (code, json)) in _classOfItem)
        {
            if (!parts.TryGetValue(itemId, out var part)) continue;
            var iterations = part.Revisions.SelectMany(r => r.Iterations).ToList();
            if (cfg.Classification.Scope == "latestIteration") iterations = iterations.Count == 0 ? [] : [part.Revisions[^1].Iterations[^1]];
            foreach (var it in iterations) { it.ClassCode = code; it.ClassAttributesJson = json; }
            _plan.ClassIterations[code] = _plan.ClassIterations.GetValueOrDefault(code) + iterations.Count;
        }
        if (_plan.ClassesToCreate.Count > 0)
            Decide("classes-created", _plan.ClassesToCreate.Count);
    }
}
