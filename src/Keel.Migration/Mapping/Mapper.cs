using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Keel.Core;
using Keel.Migration.Canonical;
using Keel.Migration.Sources;
using Keel.Security;

namespace Keel.Migration.Mapping;

/// <summary>
/// Map and validate: turns the canonical model plus the mapping file into a <see cref="Plan"/> of Keel objects, and a list of problems with
/// codes and fix hints. Pure (no database, no clock): the same package and mapping always give the same plan, which is what the dry-run
/// report hash relies on. An object with an error is listed in <see cref="Plan.Failed"/> and is never loaded.
/// </summary>
public sealed class Mapper(MappingConfig cfg, ReferenceData rd, PackageManifest manifest)
{
    /// <summary>Keel numbers that belong to other objects; a source number among them is a collision.</summary>
    public ExistingKeys Existing { get; init; } = new();

    private static readonly Dictionary<string, string> MediaByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf", [".txt"] = "text/plain", [".xml"] = "application/xml", [".json"] = "application/json", [".csv"] = "text/csv",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".tif"] = "image/tiff", [".tiff"] = "image/tiff", [".dwg"] = "application/acad",
        [".dxf"] = "application/dxf", [".stp"] = "application/step", [".step"] = "application/step", [".igs"] = "model/iges", [".zip"] = "application/zip",
        [".doc"] = "application/msword", [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel", [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    };

    private readonly Plan _plan = new();
    private readonly MarkingsMapper _markings = new(cfg.Markings);
    private DateTimeOffset? _fallback;
    private readonly HashSet<string> _warnedUsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedUsers = new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------------------------------------ problem helpers
    private void Err(string type, string owner, string code, string message, string? hint = null, string? sourceId = null)
    {
        _plan.Problems.Add(new Problem(Severity.Error, code, type, sourceId ?? owner, message, hint));
        _plan.Failed.Add(Plan.Key(type, owner));
    }

    private void Warn(string type, string owner, string code, string message, string? hint = null) =>
        _plan.Problems.Add(new Problem(Severity.Warning, code, type, owner, message, hint));

    private void Decide(string key, long n = 1) => _plan.Decisions[key] = _plan.Decisions.GetValueOrDefault(key) + n;

    // ------------------------------------------------------------------------------------------------ mapping file checks
    /// <summary>Checks the mapping file against Keel's reference data. Errors here make every load impossible.</summary>
    public static List<Problem> ValidateConfig(MappingConfig cfg, ReferenceData rd)
    {
        var p = new List<Problem>();
        void E(string code, string msg, string? hint = null) => p.Add(new Problem(Severity.Error, code, "mapping", "mapping", msg, hint));
        if (cfg.Version != MappingConfig.CurrentVersion) E("MAP-VERSION", $"Mapping version {cfg.Version} is not supported (this tool reads {MappingConfig.CurrentVersion}).");
        if (cfg.AuditGranularity is not ("batch" or "object")) E("MAP-AUDIT", $"auditGranularity '{cfg.AuditGranularity}' must be 'batch' or 'object'.");
        void States(string where, LifecycleMapping m, string lifecycle)
        {
            var valid = rd.LifecycleStates.GetValueOrDefault(lifecycle) ?? [];
            foreach (var (src, to) in m.States)
                if (!valid.Contains(to)) E("MAP-STATE-TARGET", $"{where}.states['{src}'] -> '{to}' is not a state of lifecycle {lifecycle} ({string.Join(", ", valid)}).");
            if (m.Default is not null && !valid.Contains(m.Default)) E("MAP-STATE-TARGET", $"{where}.default '{m.Default}' is not a state of lifecycle {lifecycle}.");
        }
        States("lifecycle", cfg.Lifecycle, "part-default");
        States("documentLifecycle", cfg.DocumentLifecycle ?? cfg.Lifecycle, "document-default");
        if (cfg.Revisions.Order is not ("auto" or "source" or "created" or "label")) E("MAP-REV-ORDER", $"revisions.order '{cfg.Revisions.Order}' must be auto, source, created or label.");
        if (cfg.Revisions.Policy is not ("sequence" or "keepWhenValid")) E("MAP-REV-POLICY", $"revisions.policy '{cfg.Revisions.Policy}' must be sequence or keepWhenValid.");
        foreach (var v in cfg.PartTypes.Map.Values.Append(cfg.PartTypes.Default ?? "Component"))
            if (!ReferenceData.PartTypes.Contains(v)) E("MAP-PARTTYPE-TARGET", $"Part type '{v}' is not one of {string.Join(", ", ReferenceData.PartTypes)}.");
        foreach (var v in cfg.PartSources.Map.Values.Append(cfg.PartSources.Default ?? "Make"))
            if (v is not ("Make" or "Buy")) E("MAP-SOURCE-TARGET", $"Part source '{v}' must be Make or Buy.");
        foreach (var v in cfg.Units.Map.Values.Concat(cfg.Units.Default is null ? [] : [cfg.Units.Default]))
            if (!UnitsOfMeasure.IsValid(v)) E("MAP-UNIT-TARGET", $"Unit '{v}' is not a Keel unit ({string.Join(", ", UnitsOfMeasure.All)}).");
        foreach (var v in cfg.DocumentTypes.Map.Values.Append(cfg.DocumentTypes.Default ?? "Other"))
            if (!ReferenceData.DocumentTypes.Contains(v)) E("MAP-DOCTYPE-TARGET", $"Document type '{v}' is not one of {string.Join(", ", ReferenceData.DocumentTypes)}.");
        foreach (var (src, to) in cfg.Attributes.Map)
            if (!rd.PartAttributes.ContainsKey(to)) E("MAP-ATTR-TARGET", $"Attribute '{src}' maps to '{to}', which is not an attribute definition for parts ({string.Join(", ", rd.PartAttributes.Keys)}).");
        if (!cfg.Attributes.KeepUnmapped) E("MAP-KEEP-UNMAPPED", "attributes.keepUnmapped=false is not supported: unmapped properties are always preserved in legacy_properties.");
        foreach (var rule in cfg.Markings.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Name)) E("MAP-RULE-NAME", "Every markings rule needs a name.");
            CheckCondition(rule.When, rule.Name, E);
            foreach (var a in rule.AppliesTo) if (a is not ("part" or "document" or "change")) E("MAP-RULE-APPLIES", $"Rule '{rule.Name}': appliesTo '{a}' must be part, document or change.");
        }
        foreach (var (type, map) in cfg.Changes.States)
        {
            var lc = type.ToLowerInvariant() + "-default";
            if (!rd.LifecycleStates.TryGetValue(lc, out var valid)) { E("MAP-CHANGE-TYPE", $"changes.states key '{type}' must be PR, ECR or ECN."); continue; }
            foreach (var (src, to) in map) if (!valid.Contains(to)) E("MAP-STATE-TARGET", $"changes.states.{type}['{src}'] -> '{to}' is not a state of {lc}.");
        }
        foreach (var v in cfg.Changes.Types.Map.Values.Concat(cfg.Changes.Types.Default is null ? [] : [cfg.Changes.Types.Default]))
            if (v is not ("PR" or "ECR" or "ECN")) E("MAP-CHANGE-TYPE", $"Change type '{v}' must be PR, ECR or ECN.");
        if (cfg.Changes.DefaultClass is not (null or "I" or "II")) E("MAP-CHANGE-CLASS", "changes.defaultClass must be I or II.");
        foreach (var v in cfg.Changes.ClassMap.Values) if (v is not ("I" or "II")) E("MAP-CHANGE-CLASS", $"changes.classMap value '{v}' must be I or II.");
        if (cfg.Changes.DefaultPriority is not ("Low" or "Medium" or "High" or "Critical")) E("MAP-CHANGE-PRIORITY", "changes.defaultPriority must be Low, Medium, High or Critical.");
        if (cfg.Markings.DefaultMarkings is not null)
        {
            var d = new MarkingsMapper(new MarkingsMapping { DefaultMarkings = cfg.Markings.DefaultMarkings }).Decide([new Dictionary<string, string?>()], "part");
            if (d.Markings is null) E("MAP-DEFAULT-MARKINGS", "defaultMarkings is not valid: " + string.Join(" ", d.Errors));
        }
        foreach (var u in cfg.Users.Map.Values.Concat(cfg.Users.Default is null ? [] : [cfg.Users.Default]))
            if (string.IsNullOrWhiteSpace(u)) E("MAP-USER", "User mapping targets must not be empty.");
        if (cfg.Limits.MaxPropertyValueChars < 1 || cfg.Limits.MaxLegacyJsonChars < 1 || cfg.Limits.MaxUsagesPerIteration < 1) E("MAP-LIMITS", "Limits must be positive.");
        return p;
    }

    private static void CheckCondition(MarkingCondition c, string rule, Action<string, string, string?> e)
    {
        if (c.Property is null && c.All is null) e("MAP-RULE-WHEN", $"Rule '{rule}': 'when' needs a property or an all[] list.", null);
        if (c.Matches is not null)
        {
            try { _ = new Regex(c.Matches); }
            catch (ArgumentException) { e("MAP-RULE-REGEX", $"Rule '{rule}': '{c.Matches}' is not a valid regular expression.", null); }
        }
        foreach (var x in c.All ?? []) CheckCondition(x, rule, e);
    }

    // ------------------------------------------------------------------------------------------------ main entry
    public Plan Map(CanonicalPackage pkg)
    {
        _plan.SourceSystem = pkg.SourceSystem;
        _fallback = pkg.ExportedAt;
        foreach (var p in ValidateConfig(cfg, rd)) { _plan.Problems.Add(p); _plan.Failed.Add(Plan.Key("mapping", "mapping")); }
        if (string.IsNullOrWhiteSpace(pkg.SourceSystem)) Err("package", "package", "PKG-SOURCE-SYSTEM", "The package does not name its source system.", "Set sourceSystem in package.json.");
        else if (cfg.SourceSystem is not null && !string.Equals(cfg.SourceSystem, pkg.SourceSystem, StringComparison.OrdinalIgnoreCase))
            Err("package", "package", "PKG-SOURCE-MISMATCH", $"The mapping is for source system '{cfg.SourceSystem}', the package is '{pkg.SourceSystem}'.");
        if (_plan.IsFailed("mapping", "mapping")) return _plan;

        var docIds = new Dictionary<string, PDocument>(StringComparer.Ordinal);
        foreach (var dup in pkg.Documents.GroupBy(d => d.SourceId).Where(g => g.Count() > 1)) Err("document", dup.Key, "DUP-SOURCE-ID", $"Document source id '{dup.Key}' occurs {dup.Count()} times.");
        foreach (var d in pkg.Documents.GroupBy(d => d.SourceId).Select(g => g.First())) { var pd = MapDocument(d); if (pd is not null) docIds[d.SourceId] = pd; }
        var partIds = new Dictionary<string, PPart>(StringComparer.Ordinal);
        foreach (var dup in pkg.Items.GroupBy(d => d.SourceId).Where(g => g.Count() > 1)) Err("part", dup.Key, "DUP-SOURCE-ID", $"Item source id '{dup.Key}' occurs {dup.Count()} times.");
        var srcItems = pkg.Items.GroupBy(d => d.SourceId).Select(g => g.First()).ToDictionary(i => i.SourceId, StringComparer.Ordinal);
        foreach (var i in srcItems.Values) { var pp = MapPart(i); if (pp is not null) partIds[i.SourceId] = pp; }

        CheckNumbers(pkg);
        CheckStructures(srcItems, partIds, docIds);
        Cascade(partIds, docIds);
        _plan.Parts = partIds.Values.OrderBy(p => p.Number, StringComparer.Ordinal).ToList();
        _plan.Documents = docIds.Values.OrderBy(p => p.Number, StringComparer.Ordinal).ToList();
        MapChanges(pkg, srcItems, partIds);
        foreach (var c in pkg.Classifications)
        {
            if (!partIds.TryGetValue(c.ItemSourceId, out var part)) { Warn("classification", c.ItemSourceId, "CLASS-ITEM-MISSING", $"Classification '{c.ClassCode}' refers to item '{c.ItemSourceId}', which is not planned for import; it is skipped."); continue; }
            _plan.Classifications.Add(new ClassAssignmentPlan(c.ItemSourceId, part.Number, c.ClassCode, c.Attributes));
        }
        if (_plan.Classifications.Count > 0)
            Warn("classification", "*", "CLASS-STAGED", $"{_plan.Classifications.Count} classification assignments are staged but not loaded: classification services arrive with Phase 2b (Keel.Admin).");
        return _plan;
    }

    // ------------------------------------------------------------------------------------------------ users, timestamps, states
    private string User(string? src, string type, string owner)
    {
        if (!string.IsNullOrWhiteSpace(src) && cfg.Users.Map.TryGetValue(src.Trim(), out var mapped))
        {
            if (!rd.Directory.ContainsKey(mapped) && _warnedUsers.Add(mapped))
                Warn("user", mapped, "USER-NOT-IN-DIRECTORY", $"Keel user '{mapped}' has not signed in yet (not in the user directory); created-by shows the name, access follows the IdP.");
            return mapped;
        }
        if (cfg.Users.Default is { } def)
        {
            var key = string.IsNullOrWhiteSpace(src) ? "(none)" : src.Trim();
            _plan.UserFallbacks[key] = _plan.UserFallbacks.GetValueOrDefault(key) + 1;
            return def;
        }
        var name = string.IsNullOrWhiteSpace(src) ? "(none)" : src.Trim();
        if (_failedUsers.Add(name)) _plan.Problems.Add(new Problem(Severity.Error, "USER-UNMAPPED", "user", name, $"Source user '{name}' has no mapping and no users.default is configured.", "Add the user to users.map (value: the Keel username) or set users.default (for example \"migration\")."));
        _plan.Failed.Add(Plan.Key(type, owner));
        return "?";
    }

    private DateTimeOffset Time(DateTimeOffset? value, string type, string owner, params DateTimeOffset?[] fallbacks)
    {
        if (value is not null) return value.Value;
        foreach (var f in fallbacks) if (f is not null) { Decide("timestamp-inherited"); return f.Value; }
        if (_fallback is not null)
        {
            Decide("timestamp-from-export-date");
            return _fallback.Value;
        }
        Err(type, owner, "TS-MISSING", "A required timestamp is missing and the package has no export date to fall back to.", "Add createdAt to the source objects or set exportedAt in the package.");
        return DateTimeOffset.UnixEpoch;
    }

    private string? State(LifecycleMapping m, IReadOnlyList<CState> states, string type, string owner)
    {
        var src = states.Count > 0 ? states[^1].Name : null;
        if (!string.IsNullOrWhiteSpace(src))
        {
            if (m.States.TryGetValue(src.Trim(), out var to)) return to;
            if (m.Default is not null) { Decide("state-defaulted"); Warn(type, owner, "STATE-DEFAULTED", $"Source state '{src}' has no mapping; the default '{m.Default}' is used."); return m.Default; }
            Err(type, owner, "STATE-UNMAPPED", $"Source lifecycle state '{src}' has no mapping.", $"Add lifecycle.states['{src}'] (or documentLifecycle) to the mapping, or set lifecycle.default.");
            return null;
        }
        if (m.Default is not null) { Decide("state-defaulted"); return m.Default; }
        Err(type, owner, "STATE-MISSING", "The revision has no lifecycle state and no default is configured.", "Set lifecycle.default in the mapping.");
        return null;
    }

    // ------------------------------------------------------------------------------------------------ revisions
    private sealed record Ordered(CRevision Source, string Label, int Position);

    private List<Ordered>? OrderRevisions(CObject o, string type)
    {
        var revs = o.Revisions;
        if (revs.Count == 0) { Err(type, o.SourceId, "REV-NONE", $"{o.Number} has no revisions.", "Every item needs at least one revision with one iteration."); return null; }
        var dup = revs.GroupBy(r => r.Label, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) { Err(type, o.SourceId, "REV-DUP-LABEL", $"{o.Number} has revision label '{dup.Key}' more than once (labels are compared ignoring case)."); return null; }
        List<CRevision> ordered;
        if (cfg.Revisions.Explicit.TryGetValue(o.Number, out var explicitOrder))
        {
            var byLabel = revs.ToDictionary(r => r.Label, StringComparer.Ordinal);
            if (explicitOrder.Count != revs.Count || explicitOrder.Any(l => !byLabel.ContainsKey(l)) || explicitOrder.Distinct().Count() != explicitOrder.Count)
            {
                Err(type, o.SourceId, "REV-EXPLICIT-MISMATCH", $"revisions.explicit['{o.Number}'] must list exactly the source labels: {string.Join(", ", revs.Select(r => r.Label))}.");
                return null;
            }
            ordered = explicitOrder.Select(l => byLabel[l]).ToList();
            Decide("revision-order-explicit");
        }
        else
        {
            var mode = cfg.Revisions.Order;
            DateTimeOffset? Date(CRevision r) => r.CreatedAt ?? r.Iterations.Select(i => i.CreatedAt).Where(d => d is not null).Min();
            var byDate = revs.Select((r, i) => (r, i)).OrderBy(t => Date(t.r)).ThenBy(t => t.i).Select(t => t.r).ToList();
            var datesDistinct = revs.All(r => Date(r) is not null) && revs.Select(Date).Distinct().Count() == revs.Count;
            var kinds = revs.Select(r => LabelKey(r.Label).Kind).Where(k => k != 0).Distinct().ToList();
            var labelsComparable = kinds.Count <= 1 && !kinds.Contains(3);
            var byLabel = revs.OrderBy(r => LabelKey(r.Label), LabelComparer.Instance).ToList();
            if (revs.Count == 1) ordered = revs.ToList();
            else switch (mode)
            {
                case "source": ordered = revs.ToList(); break;
                case "created" when datesDistinct: ordered = byDate; break;
                case "created":
                    Err(type, o.SourceId, "REV-ORDER-AMBIGUOUS", $"{o.Number}: revisions.order is 'created' but the revision dates are missing or tied.", $"Use revisions.explicit['{o.Number}'] to give the true order.");
                    return null;
                case "label" when labelsComparable: ordered = byLabel; break;
                case "label":
                    Err(type, o.SourceId, "REV-ORDER-AMBIGUOUS", $"{o.Number}: labels ({string.Join(", ", revs.Select(r => r.Label))}) mix letters and numbers and cannot be ordered by label.", $"Use revisions.explicit['{o.Number}'].");
                    return null;
                default: // auto
                    if (datesDistinct)
                    {
                        ordered = byDate;
                        if (labelsComparable && !byLabel.SequenceEqual(byDate))
                            Warn(type, o.SourceId, "REV-ORDER-DISAGREE", $"{o.Number}: revision dates and label order disagree ({string.Join(" < ", byDate.Select(r => r.Label))} by date); the dates are used.");
                    }
                    else if (labelsComparable) { ordered = byLabel; Decide("revision-order-by-label"); }
                    else
                    {
                        Err(type, o.SourceId, "REV-ORDER-AMBIGUOUS", $"{o.Number}: the order of revisions ({string.Join(", ", revs.Select(r => r.Label))}) cannot be determined: dates are missing or tied and the labels mix letters and numbers.", $"Add revisions.explicit['{o.Number}'] = [labels in true order], or set revisions.order.");
                        return null;
                    }
                    break;
            }
        }
        var keep = cfg.Revisions.Policy == "keepWhenValid" && ordered.All(r => RevisionScheme.IsValid(r.Label)) &&
                   ordered.Select(r => RevisionScheme.ToIndex(r.Label)).Zip(ordered.Select(r => RevisionScheme.ToIndex(r.Label)).Skip(1), (a, b) => a < b).All(x => x);
        var result = new List<Ordered>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var label = keep ? ordered[i].Label : RevisionScheme.FromIndex(i + 1);
            result.Add(new Ordered(ordered[i], label, i + 1));
            _plan.RevisionMaps.Add(new RevisionMapEntry(o.Number, ordered[i].Label, label, i + 1));
        }
        return result;
    }

    private readonly record struct LabelKeyValue(int Kind, decimal[] Numbers, string Text);

    private static LabelKeyValue LabelKey(string label)
    {
        var l = label.Trim();
        if (l.Length == 0 || l == "-") return new(0, [], "");
        var parts = l.Split('.');
        if (parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit))) return new(1, parts.Select(p => decimal.Parse(p, CultureInfo.InvariantCulture)).ToArray(), l);
        if (l.All(char.IsAsciiLetter)) return new(2, [], l.ToUpperInvariant());
        return new(3, [], l);
    }

    private sealed class LabelComparer : IComparer<LabelKeyValue>
    {
        public static readonly LabelComparer Instance = new();
        public int Compare(LabelKeyValue a, LabelKeyValue b)
        {
            if (a.Kind != b.Kind) return a.Kind.CompareTo(b.Kind);
            if (a.Kind == 1)
            {
                for (var i = 0; i < Math.Min(a.Numbers.Length, b.Numbers.Length); i++)
                    if (a.Numbers[i] != b.Numbers[i]) return a.Numbers[i].CompareTo(b.Numbers[i]);
                return a.Numbers.Length.CompareTo(b.Numbers.Length);
            }
            if (a.Text.Length != b.Text.Length) return a.Text.Length.CompareTo(b.Text.Length);
            return string.CompareOrdinal(a.Text, b.Text);
        }
    }

    // ------------------------------------------------------------------------------------------------ properties, attributes, legacy
    private string LegacyJson(string type, string owner, object? sourceInfo, params IReadOnlyDictionary<string, string?>[] propertySets)
    {
        var props = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var set in propertySets)
            foreach (var (k, v) in set)
            {
                if (cfg.Attributes.Map.ContainsKey(k)) continue;
                if (v is not null && v.Length > cfg.Limits.MaxPropertyValueChars)
                {
                    Err(type, owner, "PROP-VALUE-TOO-LARGE", $"Property '{k}' is {v.Length} characters (limit {cfg.Limits.MaxPropertyValueChars}).", "Shorten or drop the property in the export, or raise limits.maxPropertyValueChars.");
                    continue;
                }
                props[k] = v;
                _plan.UnmappedProperties[k] = _plan.UnmappedProperties.GetValueOrDefault(k) + 1;
            }
        var json = JsonSerializer.Serialize(new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["_source"] = sourceInfo, ["properties"] = props });
        if (json.Length > cfg.Limits.MaxLegacyJsonChars)
        {
            Err(type, owner, "PROP-JSON-TOO-LARGE", $"The preserved source properties are {json.Length} characters (limit {cfg.Limits.MaxLegacyJsonChars}).", "Raise limits.maxLegacyJsonChars or reduce the export.");
            return "{}";
        }
        return json;
    }

    private string Attributes(string owner, params IReadOnlyDictionary<string, string?>[] propertySets)
    {
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in propertySets) foreach (var (k, v) in set) merged[k] = v;
        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (src, keelName) in cfg.Attributes.Map)
        {
            if (!merged.TryGetValue(src, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;
            if (!rd.PartAttributes.TryGetValue(keelName, out var def)) continue;
            var value = raw.Trim();
            switch (def.DataType)
            {
                case "number":
                    if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) result[keelName] = n;
                    else Err("part", owner, "ATTR-INVALID", $"Property '{src}' value '{Trunc(value)}' is not a number (attribute {keelName}).", "Correct the value in the source or map the property elsewhere.");
                    break;
                case "boolean":
                    if (value.ToLowerInvariant() is "true" or "yes" or "y" or "1") result[keelName] = true;
                    else if (value.ToLowerInvariant() is "false" or "no" or "n" or "0") result[keelName] = false;
                    else Err("part", owner, "ATTR-INVALID", $"Property '{src}' value '{Trunc(value)}' is not a boolean (attribute {keelName}).");
                    break;
                case "enum":
                    var match = def.EnumValues?.FirstOrDefault(e => string.Equals(e, value, StringComparison.OrdinalIgnoreCase));
                    if (match is not null) result[keelName] = match;
                    else Err("part", owner, "ATTR-INVALID", $"Property '{src}' value '{Trunc(value)}' is not one of {string.Join(", ", def.EnumValues ?? [])} (attribute {keelName}).", "Add a value mapping in the source or extend the attribute definition.");
                    break;
                default:
                    if (def.MaxLength is { } max && value.Length > max)
                        Err("part", owner, "ATTR-TOO-LONG", $"Property '{src}' is {value.Length} characters; attribute {keelName} allows {max}.", "Shorten the value in the source; the full text cannot be kept in this attribute.");
                    else result[keelName] = value;
                    break;
            }
        }
        foreach (var def in rd.PartAttributes.Values.Where(d => d.Required && !result.ContainsKey(d.Name)))
            Err("part", owner, "ATTR-REQUIRED", $"Required attribute '{def.Name}' has no value.", "Map a source property to it.");
        return JsonSerializer.Serialize(result);
    }

    private static string Trunc(string s) => s.Length <= 40 ? s : s[..40] + "...";

    private static Dictionary<string, string?> Virtual(CObject o) => new(StringComparer.OrdinalIgnoreCase) { ["$type"] = o.Type, ["$number"] = o.Number, ["$name"] = o.Name };

    // ------------------------------------------------------------------------------------------------ documents
    private PDocument? MapDocument(CObject o)
    {
        const string T = "document";
        var id = o.SourceId;
        if (string.IsNullOrWhiteSpace(o.Number)) { Err(T, id, "DOC-NUMBER", "The document has no number."); return null; }
        if (string.IsNullOrWhiteSpace(o.Name) || o.Name.Length > 200) Err(T, id, "NAME-INVALID", $"Document {o.Number}: the name must be 1-200 characters (is {o.Name.Length}).", "Shorten the name in the source; it cannot be truncated silently.");
        var type = cfg.DocumentTypes.Map.TryGetValue(o.Type ?? "", out var t) ? t : cfg.DocumentTypes.Default;
        if (type is null) { Err(T, id, "TYPE-UNMAPPED", $"Document type '{o.Type}' has no mapping.", "Add it to documentTypes.map."); type = "Other"; }
        var lc = cfg.DocumentLifecycle ?? cfg.Lifecycle;
        var order = OrderRevisions(o, T);
        var doc = new PDocument { SourceId = id, Number = o.Number, Name = o.Name, DocumentType = type, CreatedBy = User(o.CreatedBy, T, id) };
        var firstDate = o.Revisions.SelectMany(r => r.Iterations.Select(i => i.CreatedAt).Append(r.CreatedAt)).Where(d => d is not null).Min();
        doc.CreatedAt = Time(o.CreatedAt, T, id, firstDate);
        if (order is null) return null;
        var contexts = new List<IReadOnlyDictionary<string, string?>>();
        foreach (var ord in order)
        {
            var r = ord.Source;
            var state = State(lc, r.States, T, r.SourceId);
            if (state is null) { _plan.Failed.Add(Plan.Key(T, id)); continue; }
            var last = r.States.LastOrDefault();
            var pr = new PDocRevision
            {
                SourceId = r.SourceId, Label = ord.Label, LegacyLabel = r.Label, State = state,
                CreatedBy = User(r.CreatedBy ?? o.CreatedBy, T, id),
            };
            pr.CreatedAt = Time(r.CreatedAt, T, id, r.Iterations.Select(i => i.CreatedAt).Where(d => d is not null).Min(), doc.CreatedAt);
            pr.StateChangedAt = Time(last?.At, T, id, pr.CreatedAt);
            pr.StateChangedBy = User(last?.By ?? r.CreatedBy ?? o.CreatedBy, T, id);
            pr.LegacyJson = LegacyJson(T, id, new { sourceId = r.SourceId, sourceLabel = r.Label, createdBy = r.CreatedBy, lifecycleHistory = r.States }, r.Properties);
            if (r.Iterations.Count == 0) { Err(T, id, "ITER-NONE", $"Document {o.Number} revision {r.Label} has no iteration."); continue; }
            var seqs = r.Iterations.Select(i => i.Sequence).ToList();
            if (seqs.Any(s => s < 1) || seqs.Distinct().Count() != seqs.Count) { Err(T, id, "ITER-SEQ-INVALID", $"Document {o.Number} revision {r.Label}: iteration sequences must be unique and at least 1 (are {string.Join(", ", seqs)})."); continue; }
            foreach (var it in r.Iterations.OrderBy(i => i.Sequence))
            {
                var pi = new PDocIteration
                {
                    Sequence = it.Sequence, Comment = it.Comment, Description = it.Description ?? o.Description ?? "", CreatedBy = User(it.CreatedBy ?? r.CreatedBy ?? o.CreatedBy, T, id),
                };
                pi.CreatedAt = Time(it.CreatedAt, T, id, pr.CreatedAt);
                var merged = Merge(o.Properties, r.Properties, it.Properties);
                contexts.Add(Overlay(merged, Virtual(o)));
                pi.LegacyJson = LegacyJson(T, id, new { createdBy = it.CreatedBy }, it.Properties);
                pi.Files = MapFiles(it.Files, o, r, id);
                pr.Iterations.Add(pi);
            }
            doc.Revisions.Add(pr);
        }
        doc.LegacyJson = LegacyJson(T, id, new { sourceId = id, type = o.Type, createdBy = o.CreatedBy }, o.Properties);
        if (contexts.Count == 0) contexts.Add(Overlay(o.Properties, Virtual(o)));
        var decision = _markings.Decide(contexts, "document");
        ApplyMarkings(decision, T, id, o.Number, m => doc.Markings = m, d => doc.Defaulted = d);
        return _plan.IsFailed(T, id) ? null : doc;
    }

    private static Dictionary<string, string?> Merge(params IReadOnlyDictionary<string, string?>[] sets)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sets) foreach (var (k, v) in s) d[k] = v;
        return d;
    }

    private static Dictionary<string, string?> Overlay(IReadOnlyDictionary<string, string?> a, IReadOnlyDictionary<string, string?> b) => Merge(a, b);

    private void ApplyMarkings(MarkingsDecision decision, string type, string id, string number, Action<Markings> set, Action<bool> setDefaulted)
    {
        if (decision.Markings is null)
        {
            var undetermined = decision.Errors.Any(e => e.StartsWith("No rule assigns an export jurisdiction", StringComparison.Ordinal));
            Err(type, id, undetermined ? "MARKINGS-UNDETERMINED" : "MARKINGS-INVALID", $"{number}: {string.Join(" ", decision.Errors)}",
                undetermined ? "Add a markings rule that covers this object, or configure markings.defaultMarkings (imported objects then appear in the 'defaulted markings' review list)." : "Fix the source attributes or the rule; the object is not imported with markings that fail validation.");
            return;
        }
        set(decision.Markings);
        setDefaulted(decision.Defaulted);
        if (decision.Defaulted)
        {
            _plan.Defaulted.Add(new DefaultedMarking(type, id, number, decision.Markings.BannerText()));
            Warn(type, id, "MARKINGS-DEFAULTED", $"{number}: no rule determined the markings; defaultMarkings applied ({decision.Markings.BannerText()}). Review.");
        }
    }

    private List<PFile> MapFiles(List<CFile> files, CObject o, CRevision r, string id)
    {
        var result = new List<PFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var path = PackageSource.Normalize(f.PackagePath);
            var name = string.IsNullOrWhiteSpace(f.Name) ? (path is null ? "" : Path.GetFileName(path)) : f.Name.Trim();
            var where = $"{o.Number} {r.Label} file '{name}'";
            if (path is null) { Err("document", id, "FILE-NO-PATH", $"{where} has no usable package path.", "Give every file a relative path inside the package."); continue; }
            if (name.Length is < 1 or > 255) { Err("document", id, "FILE-NAME", $"{where}: the file name must be 1-255 characters."); continue; }
            if (!names.Add(name)) { Err("document", id, "FILE-DUP-NAME", $"{where} appears twice in one iteration (names are compared ignoring case)."); continue; }
            if (!manifest.Entries.TryGetValue(path, out var entry)) { Err("document", id, "FILE-MISSING", $"{where}: '{path}' is not in the package.", "Add the file to the package or re-export with files."); continue; }
            if (f.Sha256 is not null && !string.Equals(f.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Err("document", id, "FILE-SHA-MISMATCH", $"{where}: declared SHA-256 {f.Sha256[..Math.Min(12, f.Sha256.Length)]}... differs from the file's {entry.Sha256[..12]}...", "The file was altered or corrupted after export; re-export it.");
                continue;
            }
            if (f.SizeBytes is { } size && size != entry.Size) { Err("document", id, "FILE-SIZE-MISMATCH", $"{where}: declared size {size} differs from the file's {entry.Size}."); continue; }
            if (f.Sha256 is null) Decide("file-sha-computed");
            var media = f.MediaType;
            if (string.IsNullOrWhiteSpace(media) || media.Length < 3)
            {
                media = MediaByExtension.GetValueOrDefault(Path.GetExtension(name), "application/octet-stream");
                Decide("file-media-type-inferred");
            }
            result.Add(new PFile { Name = name, MediaType = media, SizeBytes = entry.Size, Sha256 = entry.Sha256, PackagePath = path, Primary = f.Primary });
        }
        if (result.Count(x => x.Primary) > 1) Err("document", id, "FILE-MULTI-PRIMARY", $"{o.Number} {r.Label}: more than one primary file in one iteration.");
        else if (result.Count > 0 && !result.Any(x => x.Primary)) { result[0].Primary = true; Decide("file-primary-assigned"); }
        return result;
    }

    // ------------------------------------------------------------------------------------------------ parts
    private PPart? MapPart(CObject o)
    {
        const string T = "part";
        var id = o.SourceId;
        if (string.IsNullOrWhiteSpace(o.Number)) { Err(T, id, "PART-NUMBER", "The item has no number."); return null; }
        if (string.IsNullOrWhiteSpace(o.Name) || o.Name.Length > 200) Err(T, id, "NAME-INVALID", $"Part {o.Number}: the name must be 1-200 characters (is {o.Name.Length}).", "Shorten the name in the source; it cannot be truncated silently.");
        var hasBom = o.Revisions.Any(r => r.Iterations.Any(i => i.Usages.Count > 0));
        var type = cfg.PartTypes.Map.TryGetValue(o.Type ?? "", out var t) ? t : cfg.PartTypes.Default;
        if (type is null) { Err(T, id, "TYPE-UNMAPPED", $"Part type '{o.Type}' has no mapping.", "Add it to partTypes.map or set partTypes.default."); type = "Component"; }
        if (hasBom && type != "Assembly")
        {
            if (cfg.AssemblyWhenHasBom) { Decide("assembly-promoted"); Warn(T, id, "TYPE-PROMOTED", $"{o.Number} has BOM lines, so its type '{type}' is changed to Assembly (only assemblies own BOM lines)."); type = "Assembly"; }
            else Err(T, id, "BOM-NOT-ASSEMBLY", $"{o.Number} has BOM lines but its type maps to '{type}'.", "Map its type to Assembly or set assemblyWhenHasBom.");
        }
        var source = cfg.PartSources.Map.TryGetValue(o.Source ?? "", out var s) ? s : (o.Source is "Make" or "Buy" ? o.Source : cfg.PartSources.Default);
        if (source is null) { Err(T, id, "SOURCE-UNMAPPED", $"Part source '{o.Source}' has no mapping.", "Add it to partSources.map or set partSources.default."); source = "Make"; }
        var unit = UnitFor(o.Unit, T, id, o.Number);
        var part = new PPart { SourceId = id, Number = o.Number, Name = o.Name, PartType = type, Source = source, Unit = unit ?? "EA", CreatedBy = User(o.CreatedBy, T, id) };
        var firstDate = o.Revisions.SelectMany(r => r.Iterations.Select(i => i.CreatedAt).Append(r.CreatedAt)).Where(d => d is not null).Min();
        part.CreatedAt = Time(o.CreatedAt, T, id, firstDate);
        var order = OrderRevisions(o, T);
        if (order is null) return null;
        var contexts = new List<IReadOnlyDictionary<string, string?>>();
        foreach (var ord in order)
        {
            var r = ord.Source;
            var state = State(cfg.Lifecycle, r.States, T, r.SourceId);
            if (state is null) continue;
            if (state == "UnderReview") Warn(T, r.SourceId, "STATE-UNDERREVIEW", $"{o.Number} {r.Label} is imported as Under Review without a workflow instance; an administrator must set its state or restart the review.");
            var last = r.States.LastOrDefault();
            var pr = new PRevision { SourceId = r.SourceId, Label = ord.Label, LegacyLabel = r.Label, State = state, CreatedBy = User(r.CreatedBy ?? o.CreatedBy, T, id) };
            pr.CreatedAt = Time(r.CreatedAt, T, id, r.Iterations.Select(i => i.CreatedAt).Where(d => d is not null).Min(), part.CreatedAt);
            pr.StateChangedAt = Time(last?.At, T, id, pr.CreatedAt);
            pr.StateChangedBy = User(last?.By ?? r.CreatedBy ?? o.CreatedBy, T, id);
            pr.LegacyJson = LegacyJson(T, id, new { sourceId = r.SourceId, sourceLabel = r.Label, createdBy = r.CreatedBy, lifecycleHistory = r.States }, r.Properties);
            if (r.Iterations.Count == 0) { Err(T, id, "ITER-NONE", $"{o.Number} revision {r.Label} has no iteration."); continue; }
            var seqs = r.Iterations.Select(i => i.Sequence).ToList();
            if (seqs.Any(x => x < 1) || seqs.Distinct().Count() != seqs.Count) { Err(T, id, "ITER-SEQ-INVALID", $"{o.Number} revision {r.Label}: iteration sequences must be unique and at least 1 (are {string.Join(", ", seqs)})."); continue; }
            foreach (var it in r.Iterations.OrderBy(i => i.Sequence))
            {
                var pi = new PIteration
                {
                    Sequence = it.Sequence, Comment = it.Comment, Description = it.Description is { Length: > 0 } ? it.Description : o.Description ?? "",
                    CreatedBy = User(it.CreatedBy ?? r.CreatedBy ?? o.CreatedBy, T, id),
                };
                pi.CreatedAt = Time(it.CreatedAt, T, id, pr.CreatedAt);
                pi.AttributesJson = Attributes(id, o.Properties, r.Properties, it.Properties);
                pi.LegacyJson = LegacyJson(T, id, new { createdBy = it.CreatedBy }, it.Properties);
                contexts.Add(Overlay(Merge(o.Properties, r.Properties, it.Properties), Virtual(o)));
                if (it.Usages.Count > cfg.Limits.MaxUsagesPerIteration) Err(T, id, "BOM-TOO-MANY", $"{o.Number} {r.Label}.{it.Sequence} has {it.Usages.Count} BOM lines (limit {cfg.Limits.MaxUsagesPerIteration}).");
                var usedFind = it.Usages.Where(u => u.FindNumber is > 0).Select(u => u.FindNumber!.Value).ToList();
                var dupFind = usedFind.GroupBy(x => x).FirstOrDefault(g => g.Count() > 1);
                if (dupFind is not null) Err(T, id, "BOM-DUP-FIND", $"{o.Number} {r.Label}.{it.Sequence}: find number {dupFind.Key} is used {dupFind.Count()} times.", "Renumber in the source; find numbers are unique per parent iteration.");
                var nextFind = (usedFind.Count == 0 ? 0 : usedFind.Max()) + 1;
                var designators = new HashSet<string>(StringComparer.Ordinal);
                foreach (var u in it.Usages)
                {
                    var pu = new PUsage { ChildSourceId = u.ChildSourceId, Quantity = u.Quantity, Designators = u.ReferenceDesignators, Unit = u.Unit ?? "" };
                    var where = $"{o.Number} {r.Label}.{it.Sequence} -> {u.ChildSourceId}";
                    if (u.Quantity <= 0) Err(T, id, "BOM-QTY", $"{where}: quantity {u.Quantity} must be greater than 0.");
                    if (u.FindNumber is { } f && f <= 0) Err(T, id, "BOM-FIND", $"{where}: find number must be positive.");
                    pu.FindNumber = u.FindNumber is > 0 ? u.FindNumber.Value : nextFind++;
                    if (u.FindNumber is null) Decide("find-number-assigned");
                    foreach (var d in u.ReferenceDesignators)
                        if (!designators.Add(d)) Err(T, id, "BOM-DUP-DESIGNATOR", $"{where}: reference designator '{d}' is used twice under one parent iteration.");
                    var unitText = u.Unit is null ? null : UnitFor(u.Unit, T, id, where);
                    pu.Unit = unitText ?? "";
                    pu.Notes = u.Notes;
                    if (!string.IsNullOrWhiteSpace(u.Effectivity))
                    {
                        var note = (u.Notes is null ? "" : u.Notes + " | ") + "Source effectivity (not loaded, Phase 2a): " + u.Effectivity;
                        pu.Notes = note.Length > 1000 ? note[..1000] : note;
                        Decide("effectivity-preserved-in-notes");
                    }
                    pi.Usages.Add(pu);
                }
                foreach (var l in it.DocLinks) pi.Links.Add(new PLink { Type = l.Type, DocumentSourceId = l.DocumentSourceId, DocumentRevisionLabel = l.DocumentRevisionLabel });
                pr.Iterations.Add(pi);
            }
            part.Revisions.Add(pr);
        }
        part.LegacyJson = LegacyJson(T, id, new { sourceId = id, type = o.Type, unit = o.Unit, source = o.Source, createdBy = o.CreatedBy }, o.Properties);
        if (contexts.Count == 0) contexts.Add(Overlay(o.Properties, Virtual(o)));
        ApplyMarkings(_markings.Decide(contexts, "part"), T, id, o.Number, m => part.Markings = m, d => part.Defaulted = d);
        return _plan.IsFailed(T, id) ? null : part;
    }

    private string? UnitFor(string? unit, string type, string owner, string where)
    {
        if (string.IsNullOrWhiteSpace(unit))
        {
            if (cfg.Units.Default is { } d) { Decide("unit-defaulted"); return d; }
            Err(type, owner, "UNIT-MISSING", $"{where}: no unit of measure and no units.default.", "Set units.default or add the unit in the source.");
            return null;
        }
        if (cfg.Units.Map.TryGetValue(unit.Trim(), out var mapped)) return mapped;
        if (UnitsOfMeasure.IsValid(unit.Trim())) return unit.Trim();
        if (cfg.Units.Default is { } dd) { Decide("unit-defaulted"); Warn(type, owner, "UNIT-DEFAULTED", $"{where}: unit '{unit}' has no mapping; the default '{dd}' is used."); return dd; }
        Err(type, owner, "UNIT-UNMAPPED", $"{where}: unit '{unit}' is not a Keel unit and has no mapping.", $"Add units.map['{unit}'] = one of {string.Join(", ", UnitsOfMeasure.All)}.");
        return null;
    }

    // ------------------------------------------------------------------------------------------------ cross-object checks
    private void CheckNumbers(CanonicalPackage pkg)
    {
        foreach (var g in pkg.Items.Where(i => !string.IsNullOrWhiteSpace(i.Number)).GroupBy(i => i.Number, StringComparer.Ordinal).Where(g => g.Select(x => x.SourceId).Distinct().Count() > 1))
            foreach (var i in g) Err("part", i.SourceId, "DUP-NUMBER", $"Part number '{g.Key}' is used by {g.Select(x => x.SourceId).Distinct().Count()} different items.", "Part numbers are unique in Keel; renumber or merge in the source.");
        foreach (var g in pkg.Documents.Where(i => !string.IsNullOrWhiteSpace(i.Number)).GroupBy(i => i.Number, StringComparer.Ordinal).Where(g => g.Select(x => x.SourceId).Distinct().Count() > 1))
            foreach (var i in g) Err("document", i.SourceId, "DUP-NUMBER", $"Document number '{g.Key}' is used by {g.Select(x => x.SourceId).Distinct().Count()} different documents.");
        foreach (var i in pkg.Items.Where(i => Existing.PartNumbers.Contains(i.Number)))
            Err("part", i.SourceId, "NUMBER-EXISTS", $"Part number '{i.Number}' already exists in Keel and is not this source object.", "Part numbers are unique: renumber in the source or retire the native part.");
        foreach (var i in pkg.Documents.Where(i => Existing.DocumentNumbers.Contains(i.Number)))
            Err("document", i.SourceId, "NUMBER-EXISTS", $"Document number '{i.Number}' already exists in Keel and is not this source object.", "Document numbers are unique: renumber in the source.");
        foreach (var g in pkg.Items.Where(i => !string.IsNullOrWhiteSpace(i.Number)).GroupBy(i => i.Number.ToUpperInvariant()).Where(g => g.Select(x => x.Number).Distinct().Count() > 1))
            foreach (var i in g) Warn("part", i.SourceId, "NUMBER-CASE", $"Part numbers {string.Join(" / ", g.Select(x => x.Number).Distinct())} differ only in case.");
    }

    private void CheckStructures(Dictionary<string, CObject> srcItems, Dictionary<string, PPart> parts, Dictionary<string, PDocument> docs)
    {
        const string T = "part";
        foreach (var p in parts.Values)
            foreach (var r in p.Revisions)
                foreach (var it in r.Iterations)
                {
                    foreach (var u in it.Usages)
                    {
                        if (!srcItems.ContainsKey(u.ChildSourceId)) { Err(T, p.SourceId, "BOM-CHILD-MISSING", $"{p.Number} {r.LegacyLabel}.{it.Sequence} uses '{u.ChildSourceId}', which is not an item of the package.", "Re-export the child or remove the line."); continue; }
                        if (u.ChildSourceId == p.SourceId) Err(T, p.SourceId, "BOM-SELF", $"{p.Number} contains itself.");
                        if (u.Unit.Length == 0)
                        {
                            if (parts.TryGetValue(u.ChildSourceId, out var child)) u.Unit = child.Unit;
                            else if (cfg.Units.Default is { } d) u.Unit = d;
                            else u.Unit = "EA";
                            Decide("usage-unit-from-child");
                        }
                        if (u.Unit == "EA" && u.Quantity != decimal.Truncate(u.Quantity)) Err(T, p.SourceId, "BOM-EA-WHOLE", $"{p.Number}: quantity {u.Quantity} of '{u.ChildSourceId}' in EA must be a whole number.", "Use a measured unit (mm, kg, ...) for fractional quantities.");
                        if (u.Unit == "EA" && u.Designators.Count > 0 && u.Designators.Count != u.Quantity) Err(T, p.SourceId, "BOM-DESIGNATOR-COUNT", $"{p.Number}: {u.Designators.Count} reference designators for quantity {u.Quantity} of '{u.ChildSourceId}'.", "The number of designators must equal the quantity (or list none).");
                    }
                    foreach (var l in it.Links)
                    {
                        if (l.Type is not ("describes" or "references")) { Err(T, p.SourceId, "LINK-TYPE", $"{p.Number}: link type '{l.Type}' must be describes or references."); continue; }
                        if (!docs.TryGetValue(l.DocumentSourceId, out var doc))
                        {
                            Err(T, p.SourceId, "LINK-DOC-MISSING", $"{p.Number} {r.LegacyLabel}.{it.Sequence} links to document '{l.DocumentSourceId}', which is not planned for import.", "Fix or exclude the document, or remove the link in the source.");
                            continue;
                        }
                        if (l.Type == "references") { l.DocumentRevisionLabel = null; continue; }
                        if (l.DocumentRevisionLabel is null) l.DocumentRevisionLabel = doc.Revisions.Last().Label;
                        else
                        {
                            var hit = doc.Revisions.FirstOrDefault(x => x.LegacyLabel == l.DocumentRevisionLabel);
                            if (hit is null) Err(T, p.SourceId, "LINK-REV-MISSING", $"{p.Number}: link pins revision '{l.DocumentRevisionLabel}' of document {doc.Number}, which does not exist.");
                            else l.DocumentRevisionLabel = hit.Label;
                        }
                    }
                    var dup = it.Links.GroupBy(l => (l.Type, l.DocumentSourceId)).FirstOrDefault(g => g.Count() > 1);
                    if (dup is not null) Err(T, p.SourceId, "LINK-DUP", $"{p.Number} {r.LegacyLabel}.{it.Sequence} has the same {dup.Key.Type} link to '{dup.Key.DocumentSourceId}' twice.");
                }

        // cycles on the master graph (union of all iterations)
        var graph = parts.Values.ToDictionary(p => p.SourceId, p => p.Revisions.SelectMany(r => r.Iterations).SelectMany(i => i.Usages).Select(u => u.ChildSourceId).Where(parts.ContainsKey).Distinct().ToList(), StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var cyclic = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in graph.Keys.Order(StringComparer.Ordinal))
        {
            if (state.GetValueOrDefault(start) != 0) continue;
            var stack = new Stack<(string Node, int Next)>();
            var path = new List<string>();
            stack.Push((start, 0)); state[start] = 1; path.Add(start);
            while (stack.Count > 0)
            {
                var (node, idx) = stack.Pop();
                if (idx < graph[node].Count)
                {
                    stack.Push((node, idx + 1));
                    var next = graph[node][idx];
                    var st = state.GetValueOrDefault(next);
                    if (st == 0) { state[next] = 1; path.Add(next); stack.Push((next, 0)); }
                    else if (st == 1) foreach (var n in path.SkipWhile(x => x != next)) cyclic.Add(n);
                }
                else { state[node] = 2; path.Remove(node); }
            }
        }
        foreach (var c in cyclic)
            Err(T, c, "BOM-CYCLE", $"{parts[c].Number} is part of a BOM cycle.", "Break the cycle in the source: an assembly cannot contain itself directly or indirectly.");
    }

    /// <summary>A part whose structure or links depend on an object that will not be imported cannot be imported either: a BOM with a silently missing line would be wrong.</summary>
    private void Cascade(Dictionary<string, PPart> parts, Dictionary<string, PDocument> docs)
    {
        foreach (var id in parts.Keys.Where(k => _plan.IsFailed("part", k)).ToList()) parts.Remove(id);
        foreach (var id in docs.Keys.Where(k => _plan.IsFailed("document", k)).ToList()) docs.Remove(id);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var p in parts.Values.ToList())
            {
                string? reason = null;
                foreach (var u in p.Revisions.SelectMany(r => r.Iterations).SelectMany(i => i.Usages))
                    if (!parts.ContainsKey(u.ChildSourceId)) { reason = $"its BOM uses '{u.ChildSourceId}', which is not imported (it has errors)"; break; }
                reason ??= p.Revisions.SelectMany(r => r.Iterations).SelectMany(i => i.Links).Where(l => !docs.ContainsKey(l.DocumentSourceId)).Select(l => $"it links to '{l.DocumentSourceId}', which is not imported (it has errors)").FirstOrDefault();
                if (reason is null) continue;
                Err("part", p.SourceId, "DEPENDENCY-FAILED", $"{p.Number} cannot be imported: {reason}.", "Fix the dependency; this part is imported by the next run.");
                parts.Remove(p.SourceId);
                changed = true;
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ changes
    private static readonly Regex NumberPattern = new(@"^(PR|ECR|ECN)-[0-9]{6,}$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private void MapChanges(CanonicalPackage pkg, Dictionary<string, CObject> srcItems, Dictionary<string, PPart> parts)
    {
        const string T = "change";
        var planned = new Dictionary<string, PChange>(StringComparer.Ordinal);
        foreach (var dup in pkg.Changes.GroupBy(c => c.SourceId).Where(g => g.Count() > 1)) Err(T, dup.Key, "DUP-SOURCE-ID", $"Change source id '{dup.Key}' occurs {dup.Count()} times.");
        foreach (var c in pkg.Changes.GroupBy(c => c.SourceId).Select(g => g.First()))
        {
            var id = c.SourceId;
            var type = cfg.Changes.Types.Map.TryGetValue(c.Type, out var tm) ? tm : (c.Type is "PR" or "ECR" or "ECN" ? c.Type : cfg.Changes.Types.Default);
            if (type is null) { Err(T, id, "CHG-TYPE-UNMAPPED", $"Change type '{c.Type}' has no mapping.", "Add it to changes.types.map."); continue; }
            if (cfg.Changes.States.TryGetValue(type, out var sm) && sm.TryGetValue(c.State, out var state)) { }
            else if (rd.LifecycleStates[type.ToLowerInvariant() + "-default"].Contains(c.State)) state = c.State;
            else { Err(T, id, "CHG-STATE-UNMAPPED", $"Change state '{c.State}' of {c.Number} has no mapping for {type}.", $"Add changes.states.{type}['{c.State}'] (valid: {string.Join(", ", rd.LifecycleStates[type.ToLowerInvariant() + "-default"])})."); continue; }
            if (string.IsNullOrWhiteSpace(c.Title) || c.Title.Length > 200) { Err(T, id, "CHG-TITLE", $"{c.Number}: the title must be 1-200 characters."); continue; }
            var description = c.Description ?? "";
            if (description.Length > 8000) { Err(T, id, "CHG-TEXT-TOO-LONG", $"{c.Number}: the description has {description.Length} characters (limit 8000).", "Shorten it in the source."); continue; }
            var reason = c.Reason ?? "";
            if (reason.Length > 4000) { Err(T, id, "CHG-TEXT-TOO-LONG", $"{c.Number}: the reason has {reason.Length} characters (limit 4000)."); continue; }
            string? cls = c.ChangeClass is null ? cfg.Changes.DefaultClass : (cfg.Changes.ClassMap.TryGetValue(c.ChangeClass, out var cm) ? cm : (c.ChangeClass is "I" or "II" ? c.ChangeClass : cfg.Changes.DefaultClass));
            if (type == "PR") cls = null;
            if (type == "ECN" && cls is null) { Err(T, id, "CHG-CLASS-MISSING", $"{c.Number}: a change notice needs class I or II.", "Add changes.classMap or changes.defaultClass."); continue; }
            if (type == "ECR" && cls is null && state is "UnderReview" or "Approved" or "Implemented") { Err(T, id, "CHG-CLASS-MISSING", $"{c.Number}: a reviewed change request needs a class.", "Add changes.defaultClass."); continue; }
            var priority = new[] { "Low", "Medium", "High", "Critical" }.FirstOrDefault(p => string.Equals(p, c.Priority, StringComparison.OrdinalIgnoreCase)) ?? cfg.Changes.DefaultPriority;
            var pc = new PChange
            {
                SourceId = id, ChangeType = type, LegacyNumber = c.Number, Number = NumberPattern.IsMatch(c.Number) && c.Number.StartsWith(type + "-", StringComparison.Ordinal) ? c.Number : null,
                Title = c.Title, Description = description, Priority = priority, ChangeClass = cls, Reason = reason, State = state, ParentSourceId = c.ParentSourceId,
                CreatedBy = User(c.CreatedBy, T, id),
            };
            pc.CreatedAt = Time(c.CreatedAt, T, id);
            pc.StateChangedAt = Time(c.StateChangedAt, T, id, pc.CreatedAt);
            pc.StateChangedBy = User(c.StateChangedBy ?? c.CreatedBy, T, id);
            pc.LegacyJson = LegacyJson(T, id, new { sourceId = id, number = c.Number, sourceType = c.Type, sourceState = c.State, createdBy = c.CreatedBy, sourceClass = c.ChangeClass, sourcePriority = c.Priority }, c.Properties);
            if (pc.Number is null) Decide("change-number-assigned");
            else if (Existing.ChangeNumbers.Contains(pc.Number)) { Err(T, id, "NUMBER-EXISTS", $"Change number '{pc.Number}' already exists in Keel and is not this source object."); continue; }
            foreach (var a in c.Affected)
            {
                if (!parts.TryGetValue(a.ItemSourceId, out var part)) { Err(T, id, "CHG-AFFECTED-MISSING", $"{c.Number} lists '{a.ItemSourceId}', which is not an imported part.", "Fix the part first; this change is imported by the next run."); continue; }
                PRevision? Find(string? label) => label is null ? part.Revisions.LastOrDefault() : part.Revisions.FirstOrDefault(r => r.LegacyLabel == label);
                var rev = Find(a.RevisionLabel);
                if (rev is null) { Err(T, id, "CHG-AFFECTED-REV", $"{c.Number}: revision '{a.RevisionLabel}' of {part.Number} does not exist."); continue; }
                var res = a.ResultingRevisionLabel is null ? null : Find(a.ResultingRevisionLabel);
                if (a.ResultingRevisionLabel is not null && res is null) { Err(T, id, "CHG-AFFECTED-REV", $"{c.Number}: resulting revision '{a.ResultingRevisionLabel}' of {part.Number} does not exist."); continue; }
                var action = a.Action is "revise" or "obsolete" ? a.Action : "none";
                if (action == "revise" && res is null) { action = "none"; Decide("change-affected-revise-without-result-listed-only"); }
                if (type == "PR") action = "none";
                pc.Affected.Add(new PAffected { ItemSourceId = part.SourceId, RevisionSourceId = rev.SourceId, Action = action, ResultingRevisionSourceId = action == "revise" ? res!.SourceId : null });
            }
            var dupAffected = pc.Affected.GroupBy(a => a.RevisionSourceId).FirstOrDefault(g => g.Count() > 1);
            if (dupAffected is not null) { Err(T, id, "CHG-AFFECTED-DUP", $"{c.Number} lists the same part revision twice ({dupAffected.Key})."); continue; }
            planned[id] = pc;
        }
        // markings: rules over the change's own properties, raised to cover every listed part (ADR-0019: a reader of the change learns the parts exist)
        foreach (var pc in planned.Values.ToList())
        {
            var c = pkg.Changes.First(x => x.SourceId == pc.SourceId);
            var ctx = Merge(c.Properties, new Dictionary<string, string?> { ["$type"] = pc.ChangeType, ["$number"] = c.Number });
            var decision = _markings.Decide([ctx], "change");
            ApplyMarkings(decision, T, pc.SourceId, c.Number, m => pc.Markings = m, d => pc.Defaulted = d);
            if (decision.Markings is null) continue;
            var current = decision.Markings;
            var cover = MarkingDominance.MinimalCover(current, pc.Affected.Select(a => parts[a.ItemSourceId].Markings).Distinct());
            if (!cover.IsSuccess) { Err(T, pc.SourceId, "CHG-MARKINGS-COVER", $"{c.Number}: {string.Join(" ", cover.Errors)}", "Split the change by program, or fix the markings of the listed parts."); continue; }
            if (!ReferenceEquals(cover.Value, current))
            {
                pc.MarkingsRaised = true;
                pc.Markings = cover.Value;
                Warn(T, pc.SourceId, "CHG-MARKINGS-RAISED", $"{c.Number}: the change's markings were raised to {cover.Value.BannerText()} so that they cover every part it lists.");
                Decide("change-markings-raised");
            }
        }
        // parent chain and class I rule
        foreach (var pc in planned.Values.ToList())
        {
            if (pc.ParentSourceId is null) continue;
            if (!planned.TryGetValue(pc.ParentSourceId, out var parent))
            {
                if (_plan.IsFailed(T, pc.ParentSourceId) || pkg.Changes.Any(x => x.SourceId == pc.ParentSourceId)) Err(T, pc.SourceId, "DEPENDENCY-FAILED", $"{pc.LegacyNumber}: its parent {pc.ParentSourceId} is not imported (it has errors).");
                else Err(T, pc.SourceId, "CHG-PARENT-INVALID", $"{pc.LegacyNumber}: parent '{pc.ParentSourceId}' is not in the package.");
                continue;
            }
            var ok = (pc.ChangeType, parent.ChangeType) is ("ECR", "PR") or ("ECN", "ECR");
            if (!ok) { Err(T, pc.SourceId, "CHG-PARENT-INVALID", $"{pc.LegacyNumber}: a {pc.ChangeType} can only descend from {(pc.ChangeType == "ECR" ? "a PR" : "an ECR")}, not from a {parent.ChangeType}."); continue; }
            if (pc.ChangeType == "ECN" && pc.ChangeClass == "I" && (parent.ChangeClass != "I" || parent.State is not ("Approved" or "Implemented")))
                Err(T, pc.SourceId, "CHG-CLASS1-PARENT", $"{pc.LegacyNumber}: a Class I change notice needs a Class I change request that was approved (its parent is class {parent.ChangeClass ?? "-"} in state {parent.State}).", "Map the notice to class II, or supply the approved parent request.");
        }
        foreach (var pc in planned.Values.ToList()) if (_plan.IsFailed(T, pc.SourceId)) planned.Remove(pc.SourceId);
        // a change whose parent failed later in this loop
        var again = true;
        while (again)
        {
            again = false;
            foreach (var pc in planned.Values.ToList())
                if (pc.ParentSourceId is not null && !planned.ContainsKey(pc.ParentSourceId))
                {
                    Err(T, pc.SourceId, "DEPENDENCY-FAILED", $"{pc.LegacyNumber}: its parent {pc.ParentSourceId} is not imported.");
                    planned.Remove(pc.SourceId);
                    again = true;
                }
        }
        _plan.Changes = planned.Values.OrderBy(c => c.ChangeType == "PR" ? 0 : c.ChangeType == "ECR" ? 1 : 2).ThenBy(c => c.LegacyNumber, StringComparer.Ordinal).ToList();
    }
}
