using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;

namespace Keel.Admin;

public sealed record LifecycleDoc(string Family, string Name, string ObjectType, bool IsSystem, bool Editable, IReadOnlyList<LifecycleStateDef> States, IReadOnlyList<LifecycleTransitionDef> Transitions);
public sealed record RefItemDoc(string Code, string Label, int SortOrder, bool Active, string? LifecycleFamily);
public sealed record NumberingDoc(string ObjectType, string Prefix, int Width);
public sealed record TemplateDoc(string Id, string ObjectType, JsonNode Definition);
public sealed record TemplatePinDoc(string ObjectType, string TemplateId);

/// <summary>Everything an administrator configures, in one canonical form. Contains no secrets, counters, users or object data.</summary>
public sealed record ConfigContent(
    IReadOnlyDictionary<string, IReadOnlyList<AttributeDoc>> Attributes,
    IReadOnlyList<LifecycleDoc> Lifecycles,
    IReadOnlyDictionary<string, IReadOnlyList<RefItemDoc>> Lists,
    IReadOnlyList<NumberingDoc> Numbering,
    IReadOnlyList<ReasonCodeDto> ReasonCodes,
    IReadOnlyList<TemplateDoc> WorkflowTemplates,
    IReadOnlyList<TemplatePinDoc> TemplatePins,
    ClassHierarchyDoc Classification);

/// <summary>The exchange document: a format version, the SHA-256 of the canonical content, and the content.</summary>
public sealed record ConfigDocument(string Format, string Sha256, ConfigContent Content)
{
    public const string CurrentFormat = "keel-config/1";

    public static string HashOf(ConfigContent content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(JsonSerializer.SerializeToNode(content, AdminRunner.Json)))));

    public static ConfigDocument Create(ConfigContent content) => new(CurrentFormat, HashOf(content), content);

    private static readonly JsonSerializerOptions Indented = new(AdminRunner.Json) { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Indented);

    public static (ConfigDocument? Doc, string? Error) FromJson(string json)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<ConfigDocument>(json, AdminRunner.Json);
            if (doc?.Content is null) return (null, "The document has no content.");
            if (doc.Format != CurrentFormat) return (null, $"Unsupported format '{doc.Format}' (expected {CurrentFormat}).");
            return HashOf(doc.Content) == doc.Sha256 ? (doc, null) : (null, "The content hash does not match: the file was changed after it was exported.");
        }
        catch (JsonException e)
        {
            return (null, "The JSON is invalid: " + e.Message);
        }
    }
}

/// <summary>One line of an import plan. <see cref="Kind"/> is create, change, retire, restore or blocked (the import cannot do it; <see cref="Detail"/> says what to do).</summary>
public sealed record ConfigChange(string Section, string Key, string Kind, string Detail);

public sealed record ConfigImportReport(bool DryRun, string Sha256, IReadOnlyList<ConfigChange> Changes, int Unchanged, int Applied);

/// <summary>Compares two configurations item by item. Items missing from the incoming document are left alone (an import never deletes).</summary>
public static class ConfigDiff
{
    public static (List<ConfigChange> Changes, int Unchanged) Compare(ConfigContent current, ConfigContent incoming)
    {
        var changes = new List<ConfigChange>();
        var unchanged = 0;

        void Items<T>(string section, IEnumerable<T> have, IEnumerable<T> want, Func<T, string> key, Func<T, bool>? isRetired = null)
        {
            var haveByKey = have.ToDictionary(key, StringComparer.Ordinal);
            foreach (var w in want)
            {
                var k = key(w);
                if (!haveByKey.TryGetValue(k, out var h)) { changes.Add(new ConfigChange(section, k, "create", "new")); continue; }
                var fields = FieldDiff(h, w);
                if (fields.Count == 0) { unchanged++; continue; }
                var retiredOnly = isRetired is not null && isRetired(h) != isRetired(w) && fields.Count == 1;
                changes.Add(new ConfigChange(section, k, retiredOnly ? (isRetired!(w) ? "retire" : "restore") : "change", string.Join("; ", fields)));
            }
        }

        foreach (var (type, want) in incoming.Attributes)
            Items($"attributes/{type}", current.Attributes.GetValueOrDefault(type) ?? [], want, a => a.Name, a => a.Retired);
        Items("lifecycles", current.Lifecycles, incoming.Lifecycles, l => l.Family);
        foreach (var (list, want) in incoming.Lists)
            Items($"lists/{list}", current.Lists.GetValueOrDefault(list) ?? [], want, i => i.Code, i => !i.Active);
        Items("numbering", current.Numbering, incoming.Numbering, n => n.ObjectType);
        Items("reasonCodes", current.ReasonCodes, incoming.ReasonCodes, c => $"{c.Kind}/{c.Scope}/{c.Code}", c => !c.IsActive);
        Items("workflowTemplates", current.WorkflowTemplates, incoming.WorkflowTemplates, t => t.Id);
        Items("templatePins", current.TemplatePins, incoming.TemplatePins, p => p.ObjectType);

        var haveNodes = current.Classification.Nodes.ToDictionary(n => n.Code, StringComparer.OrdinalIgnoreCase);
        foreach (var n in incoming.Classification.Nodes)
        {
            if (!haveNodes.TryGetValue(n.Code, out var h)) { changes.Add(new ConfigChange("classification", n.Code, "create", "new class")); continue; }
            var fields = FieldDiff(h with { Attributes = [] }, n with { Attributes = [] });
            var blocked = fields.Where(f => f.StartsWith("parent", StringComparison.Ordinal) || f.StartsWith("status", StringComparison.Ordinal)).ToList();
            var haveAttrs = h.Attributes.ToDictionary(a => a.Name, StringComparer.Ordinal);
            foreach (var a in n.Attributes)
            {
                if (!haveAttrs.TryGetValue(a.Name, out var ha)) fields.Add($"attribute {a.Name} created");
                else if (FieldDiff(ha, a).Count > 0) fields.Add($"attribute {a.Name}: {string.Join(", ", FieldDiff(ha, a))}");
            }
            if (blocked.Count > 0) changes.Add(new ConfigChange("classification", n.Code, "blocked", $"{string.Join(", ", blocked)}: use Move, Retire or Restore, which check the classified parts"));
            else if (fields.Count > 0) changes.Add(new ConfigChange("classification", n.Code, "change", string.Join("; ", fields)));
            else unchanged++;
        }
        return (changes, unchanged);
    }

    /// <summary>"field: before → after" for every top-level property that differs.</summary>
    public static List<string> FieldDiff<T>(T before, T after)
    {
        var a = JsonSerializer.SerializeToNode(before, AdminRunner.Json) as JsonObject ?? [];
        var b = JsonSerializer.SerializeToNode(after, AdminRunner.Json) as JsonObject ?? [];
        var list = new List<string>();
        foreach (var key in a.Select(p => p.Key).Union(b.Select(p => p.Key)).Order(StringComparer.Ordinal))
        {
            var x = a[key] is null ? null : CanonicalJson.Serialize(a[key]);
            var y = b[key] is null ? null : CanonicalJson.Serialize(b[key]);
            if (x != y) list.Add($"{key}: {Short(x)} → {Short(y)}");
        }
        return list;
    }

    private static string Short(string? s) => s is null ? "(none)" : s.Length > 80 ? s[..77] + "..." : s;
}

/// <summary>Exports the configuration as one canonical document and imports it elsewhere with a dry-run diff (dev to test to production promotion).</summary>
public sealed class ConfigTransferService(
    AdminRunner run, IAuditLog audit, AttributeAdminService attributes, LifecycleAdminService lifecycles, ReferenceAdminService references, NumberingAdminService numbering,
    ReasonCodeAdminService reasons, WorkflowAdminService workflows, ClassificationService classification)
{
    public async Task<Outcome<ConfigContent>> ReadContentAsync(Subject subject, CancellationToken ct = default)
    {
        var attrs = await attributes.ListAsync(subject, "part", true, ct);
        if (!attrs.IsOk) return Outcome<ConfigContent>.Forbidden(attrs.Errors);
        var families = (await lifecycles.ListFamiliesAsync(subject, ct)).Value!;
        var lifecycleDocs = new List<LifecycleDoc>();
        foreach (var f in families.OrderBy(f => f.Family, StringComparer.Ordinal))
        {
            var d = (await lifecycles.GetAsync(subject, f.Family, f.CurrentVersion, ct)).Value!.Definition;
            lifecycleDocs.Add(new LifecycleDoc(f.Family, d.Name, d.ObjectType, f.IsSystem, f.Editable, d.States, d.Transitions));
        }
        var lists = new SortedDictionary<string, IReadOnlyList<RefItemDoc>>(StringComparer.Ordinal);
        foreach (var list in RefLists.All.Order(StringComparer.Ordinal))
            lists[list] = [.. (await references.ListAsync(subject, list, ct)).Value!.Select(i => new RefItemDoc(i.Code, i.Label, i.SortOrder, i.IsActive, i.LifecycleFamily))];
        var nums = (await numbering.ListAsync(subject, ct)).Value!.Select(n => new NumberingDoc(n.ObjectType, n.Prefix, n.Width)).OrderBy(n => n.ObjectType, StringComparer.Ordinal).ToList();
        var codes = (await reasons.ListAsync(subject, ct)).Value!.OrderBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.Scope, StringComparer.Ordinal).ThenBy(c => c.Code, StringComparer.Ordinal).ToList();
        var templates = (await workflows.ListAsync(subject, false, null, ct)).Value!.OrderBy(t => t.Id, StringComparer.Ordinal).Select(t => new TemplateDoc(t.Id, t.ObjectType, t.Definition)).ToList();
        var pins = (await workflows.ListPinsAsync(subject, ct)).Value!.OrderBy(p => p.ObjectType, StringComparer.Ordinal).Select(p => new TemplatePinDoc(p.ObjectType, p.TemplateId)).ToList();
        var hierarchy = (await classification.ExportHierarchyAsync(subject, ct)).Value!;
        return Outcome<ConfigContent>.Ok(new ConfigContent(new SortedDictionary<string, IReadOnlyList<AttributeDoc>>(StringComparer.Ordinal) { ["part"] = [.. attrs.Value!.Select(AttributeDoc.From)] },
            lifecycleDocs, lists, nums, codes, templates, pins, hierarchy));
    }

    public async Task<Outcome<ConfigDocument>> ExportAsync(Subject subject, string? correlationId, CancellationToken ct = default)
    {
        if (!await run.AdminOnlyAsync(subject, "admin.config_export", null, correlationId, ct)) return Outcome<ConfigDocument>.Forbidden([Security.AccessPolicy.RoleMissing]);
        var content = await ReadContentAsync(subject, ct);
        if (!content.IsOk) return Outcome<ConfigDocument>.Forbidden(content.Errors);
        var doc = ConfigDocument.Create(content.Value!);
        await audit.AppendAsync(AdminRunner.Entry(subject, "admin.config_export", AuditOutcome.Success, null, correlationId, ("sha256", doc.Sha256), ("format", doc.Format)), ct);
        return Outcome<ConfigDocument>.Ok(doc);
    }

    /// <summary>The plan an import would execute, computed without writing anything.</summary>
    public async Task<Outcome<ConfigImportReport>> DryRunAsync(Subject subject, ConfigDocument doc, CancellationToken ct = default)
    {
        if (!await run.AdminOnlyAsync(subject, "admin.config_import_dryrun", null, null, ct)) return Outcome<ConfigImportReport>.Forbidden([Security.AccessPolicy.RoleMissing]);
        var verified = Verify(doc);
        if (verified is not null) return Outcome<ConfigImportReport>.Invalid([verified]);
        var current = await ReadContentAsync(subject, ct);
        if (!current.IsOk) return Outcome<ConfigImportReport>.Forbidden(current.Errors);
        var (changes, unchanged) = ConfigDiff.Compare(current.Value!, doc.Content);
        return Outcome<ConfigImportReport>.Ok(new ConfigImportReport(true, doc.Sha256, changes, unchanged, 0));
    }

    private static string? Verify(ConfigDocument doc) =>
        doc.Format != ConfigDocument.CurrentFormat ? $"Unsupported format '{doc.Format}'."
        : ConfigDocument.HashOf(doc.Content) != doc.Sha256 ? "The content hash does not match: the file was changed after it was exported." : null;

    private static bool Same<T>(T a, T b) => ConfigDiff.FieldDiff(a, b).Count == 0;

    /// <summary>
    /// Applies the plan item by item through the same services (and therefore the same validation, database guards and audit events) as the UI, then
    /// writes one summary event. Not atomic across items: on the first failure it stops and says what was applied; re-running continues from there
    /// because applied items no longer differ (ADM-14).
    /// </summary>
    public async Task<Outcome<ConfigImportReport>> ApplyAsync(Subject subject, ConfigDocument doc, string? reason, string? correlationId, CancellationToken ct = default)
    {
        var plan = await DryRunAsync(subject, doc, ct);
        if (!plan.IsOk) return plan;
        if (string.IsNullOrWhiteSpace(reason)) return Outcome<ConfigImportReport>.Invalid(["A reason is required."]);
        var report = plan.Value!;
        var blocked = report.Changes.Where(c => c.Kind == "blocked").ToList();
        if (blocked.Count > 0) return Outcome<ConfigImportReport>.Invalid([.. blocked.Select(b => $"{b.Section} {b.Key}: {b.Detail}")]);
        var note = $"Config import {doc.Sha256[..12]}: {reason.Trim()}";
        var applied = 0;
        var current = (await ReadContentAsync(subject, ct)).Value!;

        Outcome<ConfigImportReport> Fail(string what, IReadOnlyList<string> errors) =>
            Outcome<ConfigImportReport>.Invalid([$"{what}: {string.Join(" ", errors)}", $"{applied} change(s) were applied before this error; fix it and run the import again to continue."]);

        // Order matters: templates before lifecycles that use them; lifecycles before the types bound to them.
        foreach (var c in doc.Content.ReasonCodes)
        {
            var have = current.ReasonCodes.FirstOrDefault(x => x.Kind == c.Kind && x.Scope == c.Scope && x.Code == c.Code);
            if (have is not null && Same(have, c)) continue;
            if (have is null || !Same(have with { IsActive = c.IsActive }, c))
            {
                var o = await reasons.SaveAsync(subject, c, correlationId, ct);
                if (!o.IsOk) return Fail($"Reason code {c.Code}", o.Errors);
                applied++;
            }
            if ((have?.IsActive ?? true) != c.IsActive)
            {
                var o = await reasons.SetActiveAsync(subject, c.Kind, c.Scope, c.Code, c.IsActive, note, correlationId, ct);
                if (!o.IsOk) return Fail($"Reason code {c.Code}", o.Errors);
                applied++;
            }
        }
        foreach (var n in doc.Content.Numbering)
        {
            var have = current.Numbering.FirstOrDefault(x => x.ObjectType == n.ObjectType);
            if (have is null || Same(have, n)) continue;
            var o = await numbering.UpdateAsync(subject, n.ObjectType, n.Prefix, n.Width, note, correlationId, ct);
            if (!o.IsOk) return Fail($"Numbering {n.ObjectType}", o.Errors);
            applied++;
        }
        foreach (var t in doc.Content.WorkflowTemplates)
        {
            var have = current.WorkflowTemplates.FirstOrDefault(x => x.Id == t.Id);
            if (have is not null && CanonicalJson.Serialize(have.Definition) == CanonicalJson.Serialize(t.Definition)) continue;
            var o = await workflows.PublishAsync(subject, t.Definition.ToJsonString(), correlationId, ct);
            if (!o.IsOk) return Fail($"Workflow template {t.Id}", o.Errors);
            applied++;
        }
        foreach (var l in doc.Content.Lifecycles)
        {
            var have = current.Lifecycles.FirstOrDefault(x => x.Family == l.Family);
            if (have is not null && Same(have, l)) continue;
            var draft = new LifecycleDefinition(l.Family, l.Name, l.ObjectType, 1, "draft", l.States, l.Transitions);
            var o = have is null
                ? await lifecycles.CreateFamilyAsync(subject, l.Family, l.Name, l.ObjectType, draft, correlationId, ct)
                : await lifecycles.PublishVersionAsync(subject, l.Family, draft, note, correlationId, ct);
            if (!o.IsOk) return Fail($"Lifecycle {l.Family}", o.Errors);
            applied++;
        }
        foreach (var (list, items) in doc.Content.Lists)
            foreach (var i in items)
            {
                var have = current.Lists.GetValueOrDefault(list)?.FirstOrDefault(x => x.Code == i.Code);
                if (have is not null && Same(have, i)) continue;
                if (have is null || !Same(have with { Active = i.Active }, i))
                {
                    var o = await references.SaveAsync(subject, new RefItemInput(list, i.Code, i.Label, i.SortOrder, i.LifecycleFamily), correlationId, ct);
                    if (!o.IsOk) return Fail($"{list} {i.Code}", o.Errors);
                    applied++;
                }
                if (have is null ? !i.Active : have.Active != i.Active)
                {
                    var o = await references.SetActiveAsync(subject, list, i.Code, i.Active, note, correlationId, ct);
                    if (!o.IsOk) return Fail($"{list} {i.Code}", o.Errors);
                    applied++;
                }
            }
        foreach (var (type, items) in doc.Content.Attributes)
            foreach (var a in items)
            {
                var have = current.Attributes.GetValueOrDefault(type)?.FirstOrDefault(x => x.Name == a.Name);
                if (have is not null && Same(have, a)) continue;
                if (have is null || !Same(have with { Retired = a.Retired }, a))
                {
                    var o = have is null ? await attributes.CreateAsync(subject, type, a.ToInput(), correlationId, ct) : await attributes.UpdateAsync(subject, type, a.ToInput(), correlationId, ct);
                    if (!o.IsOk) return Fail($"Attribute {type}/{a.Name}", o.Errors);
                    applied++;
                }
                if ((have?.Retired ?? false) != a.Retired)
                {
                    var o = await attributes.SetRetiredAsync(subject, type, a.Name, a.Retired, note, correlationId, ct);
                    if (!o.IsOk) return Fail($"Attribute {type}/{a.Name}", o.Errors);
                    applied++;
                }
            }
        foreach (var p in doc.Content.TemplatePins)
        {
            if (current.TemplatePins.Any(x => Same(x, p))) continue;
            var o = await workflows.PinAsync(subject, p.ObjectType, p.TemplateId, correlationId, ct);
            if (!o.IsOk) return Fail($"Template pin {p.ObjectType}", o.Errors);
            applied++;
        }
        if (report.Changes.Any(c => c.Section == "classification"))
        {
            var o = await classification.ImportHierarchyAsync(subject, doc.Content.Classification, false, note, correlationId, ct);
            if (!o.IsOk) return Fail("Classification", o.Errors);
            applied += o.Value!.Created + o.Value.Updated;
        }

        await audit.AppendAsync(AdminRunner.Entry(subject, "admin.config_import", AuditOutcome.Success, null, correlationId, ("sha256", doc.Sha256), ("applied", applied.ToString(CultureInfo.InvariantCulture)),
            ("planned", report.Changes.Count.ToString(CultureInfo.InvariantCulture)), ("reason", reason.Trim())), ct);
        return Outcome<ConfigImportReport>.Ok(report with { DryRun = false, Applied = applied });
    }
}
