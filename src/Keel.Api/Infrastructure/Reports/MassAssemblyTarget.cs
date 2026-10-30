using System.Text.Json.Nodes;
using Keel.Bom;
using Keel.Core;
using Keel.Core.Reports;
using Keel.Security;

namespace Keel.Api.Infrastructure.Reports;

/// <summary>
/// Bulk structure edits (migration 0029, ADR-0050): add, replace or remove one component on many assemblies. The object is the assembly (its part number); where-used of the component
/// fills the selection. Every edit is <see cref="ICadPartPort.ApplyBomAsync"/>, which is the part's check-out, the BOM working-copy rules and a check-in (a new iteration), so release locks,
/// cycle and quantity rules and audit are the normal ones. A Released assembly is refused unless the mass change is attached to a change notice, which revises it first.
/// </summary>
public sealed class MassAssemblyTarget(ICadPartPort partPort, BomService bom, IMassChangeBinder? binder = null) : IMassChangeTarget
{
    public const string NotFound = "Not found, or you may not read it.";
    public string Kind => MassKinds.Assembly;
    public IReadOnlyCollection<string> Operations { get; } = [MassOperations.AddComponent, MassOperations.ReplaceComponent, MassOperations.RemoveComponent, MassOperations.CheckRead];
    public IReadOnlyCollection<string> Selectors { get; } = [MassSelectors.WhereUsed];

    private static string? Str(JsonObject p, string k) => p[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static decimal? Num(JsonObject p, string k)
    {
        if (p[k] is not JsonValue v) return null;
        if (v.TryGetValue<decimal>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var dd)) return (decimal)dd;
        return v.TryGetValue<string>(out var s) && decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static int? Int(JsonObject p, string k) => Num(p, k) is { } n && n == decimal.Truncate(n) && n > 0 && n < int.MaxValue ? (int)n : null;

    private static IReadOnlyList<string> Designators(JsonObject p) =>
        p["designators"] is JsonArray a ? [.. a.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0)] : [];

    // ---------------------------------------------------------------- selection (where-used)

    public async Task<IReadOnlyList<string>?> SelectAsync(Subject subject, string selector, string value, int limit, CancellationToken ct = default)
    {
        if (selector != MassSelectors.WhereUsed) return null;
        var used = await bom.WhereUsedAsync(subject, value, 1, null, ct);
        if (!used.IsOk) return null; // unknown and unreadable look alike
        // Direct parents only (level 1) that the caller may read: a placeholder row names nothing and is not offered.
        return [.. used.Value!.Rows.Where(r => !r.Redacted && r.Level == 1 && r.ParentNumber is not null).Select(r => r.ParentNumber!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(limit)];
    }

    // ---------------------------------------------------------------- preview

    public async Task<IReadOnlyDictionary<string, MassVerdict>> PreviewAsync(Subject subject, string operation, JsonObject parameters, IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        var found = (await partPort.FindManyAsync(subject, keys, ct)).ToDictionary(p => p.Number, StringComparer.Ordinal);
        var result = new Dictionary<string, MassVerdict>(StringComparer.Ordinal);
        string? general = null;
        string? component = Str(parameters, "component"), replacement = Str(parameters, "replacement");
        if (operation != MassOperations.CheckRead)
        {
            var named = new[] { component, operation == MassOperations.ReplaceComponent ? replacement : null }.Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToList();
            var parts = (await partPort.FindManyAsync(subject, named, ct)).Select(p => p.Number).ToHashSet(StringComparer.Ordinal);
            var missing = named.FirstOrDefault(n => !parts.Contains(n));
            if (missing is not null) general = $"{missing} was not found, or you may not read it.";
        }
        var order = operation == MassOperations.CheckRead ? null : MassBinding.Order(parameters);
        foreach (var key in keys)
        {
            if (!found.TryGetValue(key, out var p)) { result[key] = new MassVerdict(false, NotFound); continue; }
            if (operation == MassOperations.CheckRead) { result[key] = new MassVerdict(true, null, p.Name, p.Banner, p.State); continue; }
            string? reason = general;
            string? note = null;
            if (reason is null)
            {
                var modify = AccessPolicy.Evaluate(subject, p.Markings, PlmAction.Modify);
                if (!modify.Permitted) reason = "You may not change it (" + string.Join(", ", modify.Reasons) + ").";
                else if (key == component || key == replacement) reason = "An assembly cannot contain itself.";
                else if (p.IsLocked && order is null) reason = $"Revision {p.Revision} is {p.State} and locked. Change it through an ECN, or attach the mass change to a change notice.";
                else if (p.IsLocked && p.State != "Released") reason = $"Revision {p.Revision} is {p.State} and locked. Only a Released revision can be revised through {order}.";
                else
                {
                    var snapshot = await partPort.GetBomAsync(subject, key, null, ct);
                    if (!snapshot.IsOk) reason = NotFound;
                    else
                    {
                        var s = snapshot.Value!;
                        var lines = s.Lines.Where(l => l.ChildNumber == component).ToList();
                        if (s.CheckedOutBy is not null) reason = $"It is checked out by {s.CheckedOutBy}.";
                        else if (operation == MassOperations.AddComponent && lines.Count > 0) reason = $"It already uses {component} ({lines.Count} line(s)).";
                        else if (operation != MassOperations.AddComponent && lines.Count == 0) reason = $"It does not use {component}{(s.RedactedCount > 0 ? " that you can see" : "")}.";
                        else if (operation == MassOperations.ReplaceComponent && s.Lines.Any(l => l.ChildNumber == replacement)) reason = $"It already uses {replacement}.";
                        else note = MassBinding.Note(order, p);
                    }
                }
            }
            result[key] = new MassVerdict(reason is null, reason ?? note, p.Name, p.Banner, p.State);
        }
        return result;
    }

    public async Task<IReadOnlySet<string>> ReadableAsync(Subject subject, IReadOnlyList<string> keys, CancellationToken ct = default) =>
        (await partPort.FindManyAsync(subject, keys, ct)).Select(p => p.Number).ToHashSet(StringComparer.Ordinal);

    // ---------------------------------------------------------------- apply

    private static JsonObject LineJson(CadBomLine l) => new()
    {
        ["child"] = l.ChildNumber, ["quantity"] = l.Quantity, ["unit"] = l.Unit, ["find"] = l.FindNumber, ["notes"] = l.Notes,
        ["designators"] = new JsonArray([.. l.ReferenceDesignators.Select(d => (JsonNode?)d)]),
    };

    private static CadBomEdit AddEdit(string child, decimal qty, string unit, int? find, IReadOnlyList<string> designators, string? notes) =>
        new(CadBomEditKind.Add, child, qty, unit, find, designators, notes);

    private static CadBomEdit RemoveEdit(string child, int find) =>
        new(CadBomEditKind.Remove, child, 0m, "", null, [], null, find);

    public async Task<Outcome<JsonObject?>> ApplyAsync(Subject subject, string operation, JsonObject parameters, string key, string correlationId, CancellationToken ct = default)
    {
        var component = Str(parameters, "component")!;
        var infos = await partPort.FindManyAsync(subject, [key], ct);
        var info = infos.Count == 0 ? null : infos[0];
        if (info is null) return Outcome<JsonObject?>.NotFound();
        if (await MassBinding.ListAsync<JsonObject?>(binder, subject, MassBinding.Order(parameters), info, correlationId, ct) is { } failed) return failed;
        var before = await partPort.GetBomAsync(subject, key, correlationId, ct); // after the listing: a Released assembly now has a revision in work
        if (!before.IsOk) return new Outcome<JsonObject?>(before.Kind, null, before.Errors);
        var current = before.Value!.Lines;
        var edits = new List<CadBomEdit>();
        var undo = new JsonObject { ["operation"] = operation, ["component"] = component };
        switch (operation)
        {
            case MassOperations.AddComponent:
                edits.Add(AddEdit(component, Num(parameters, "quantity")!.Value, Str(parameters, "unit") ?? "", Int(parameters, "find_number"), Designators(parameters), Str(parameters, "notes")));
                break;
            case MassOperations.RemoveComponent:
            {
                var lines = current.Where(l => l.ChildNumber == component).ToList();
                if (lines.Count == 0) return Outcome<JsonObject?>.Conflict($"It no longer uses {component}.");
                edits.AddRange(lines.Select(l => RemoveEdit(component, l.FindNumber)));
                undo["removed"] = new JsonArray([.. lines.Select(l => (JsonNode?)LineJson(l))]);
                break;
            }
            case MassOperations.ReplaceComponent:
            {
                var replacement = Str(parameters, "replacement")!;
                var lines = current.Where(l => l.ChildNumber == component).ToList();
                if (lines.Count == 0) return Outcome<JsonObject?>.Conflict($"It no longer uses {component}.");
                if (current.Any(l => l.ChildNumber == replacement)) return Outcome<JsonObject?>.Conflict($"It already uses {replacement}.");
                foreach (var l in lines)
                {
                    edits.Add(RemoveEdit(component, l.FindNumber));
                    edits.Add(AddEdit(replacement, Num(parameters, "quantity") ?? l.Quantity, Str(parameters, "unit") ?? l.Unit, l.FindNumber, l.ReferenceDesignators, Str(parameters, "notes") ?? l.Notes));
                }
                undo["replacement"] = replacement;
                undo["removed"] = new JsonArray([.. lines.Select(l => (JsonNode?)LineJson(l))]);
                break;
            }
            default: return Outcome<JsonObject?>.Invalid(["Unknown operation."]);
        }
        var applied = await partPort.ApplyBomAsync(subject, key, edits, $"Mass change {correlationId[..8]}", correlationId, ct);
        if (!applied.IsOk) return new Outcome<JsonObject?>(applied.Kind, null, applied.Errors);
        // What was added is read back (a find number may have been chosen by the system), so undo removes exactly those lines.
        var after = await partPort.GetBomAsync(subject, key, correlationId, ct);
        var added = operation == MassOperations.RemoveComponent || !after.IsOk ? [] : after.Value!.Lines
            .Where(l => l.ChildNumber == (operation == MassOperations.AddComponent ? component : Str(parameters, "replacement")) && !before.Value.Lines.Any(b => b.ChildNumber == l.ChildNumber && b.FindNumber == l.FindNumber)).ToList();
        undo["added"] = new JsonArray([.. added.Select(l => (JsonNode?)new JsonObject { ["child"] = l.ChildNumber, ["find"] = l.FindNumber })]);
        return Outcome<JsonObject?>.Ok(undo);
    }

    // ---------------------------------------------------------------- undo

    public async Task<Outcome<string>> UndoAsync(Subject subject, string operation, string key, JsonObject undo, string correlationId, CancellationToken ct = default)
    {
        var edits = new List<CadBomEdit>();
        foreach (var n in undo["added"] as JsonArray ?? [])
            if (n is JsonObject a) edits.Add(RemoveEdit(a["child"]!.GetValue<string>(), a["find"]!.GetValue<int>()));
        foreach (var n in undo["removed"] as JsonArray ?? [])
            if (n is JsonObject r)
                edits.Add(AddEdit(r["child"]!.GetValue<string>(), r["quantity"]!.GetValue<decimal>(), r["unit"]!.GetValue<string>(), r["find"]!.GetValue<int>(),
                    [.. (r["designators"] as JsonArray ?? []).Select(d => d?.GetValue<string>() ?? "")], r["notes"]?.GetValue<string>()));
        if (edits.Count == 0) return Outcome<string>.Invalid(["There is nothing to undo."]);
        var done = await partPort.ApplyBomAsync(subject, key, edits, $"Undo mass change {correlationId[..8]}", correlationId, ct);
        return done.IsOk ? Outcome<string>.Ok($"Structure restored in iteration {done.Value}.") : new Outcome<string>(done.Kind, null, done.Errors);
    }
}
