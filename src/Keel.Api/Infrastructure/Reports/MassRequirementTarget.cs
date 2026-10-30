using System.Text.Json.Nodes;
using Keel.Core;
using Keel.Core.Reports;
using Keel.Requirements;
using Keel.Security;

namespace Keel.Api.Infrastructure.Reports;

/// <summary>
/// Bulk operations on requirements (migration 0029, ADR-0050): set one field (priority, verification method, status, type, rationale) or change the markings of requirements. The key is
/// "SPEC-number/R-0001". Every change is <see cref="RequirementService.EditAsync"/>, which writes one new iteration of the specification (so the revision lock, markings dominance,
/// per-requirement access and suspect-link marking are the normal ones). Reads go through the specification's own read, which is audited once per specification.
/// </summary>
public sealed class MassRequirementTarget(RequirementService requirements) : IMassChangeTarget
{
    public const string NotFound = "Not found, or you may not read it.";
    public string Kind => MassKinds.Requirement;
    public IReadOnlyCollection<string> Operations { get; } = [MassOperations.SetRequirementField, MassOperations.ChangeMarkings];

    private static string? Str(JsonObject p, string k) => p[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static bool TrySplit(string key, out string spec, out string req)
    {
        var i = key.IndexOf('/', StringComparison.Ordinal);
        (spec, req) = i > 0 ? (key[..i], key[(i + 1)..]) : ("", "");
        return spec.Length > 0 && RequirementRules.IsKey(req);
    }

    /// <summary>The value as the field stores it, or the words that explain why it is not allowed.</summary>
    public static string? Normalize(string field, string value, out string? error)
    {
        error = null;
        var v = value.Trim();
        string[]? allowed = field switch
        {
            "priority" => [.. ReqPriorities.All], "method" => [.. VerificationMethods.All], "status" => [.. ReqStatuses.All], "type" => [.. ReqTypes.All], _ => null,
        };
        if (field == "rationale")
        {
            if (v.Length > RequirementRules.MaxRationale) error = $"The rationale must be at most {RequirementRules.MaxRationale} characters.";
            return v;
        }
        v = v.ToLowerInvariant();
        if (allowed is null || !allowed.Contains(v)) { error = $"The {field} must be one of: {string.Join(", ", allowed ?? [])}."; return null; }
        return v;
    }

    private async Task<Dictionary<string, (RequirementSet Set, RequirementRow Row)>> LoadAsync(Subject subject, IReadOnlyList<string> keys, string? corr, CancellationToken ct)
    {
        var found = new Dictionary<string, (RequirementSet, RequirementRow)>(StringComparer.Ordinal);
        foreach (var group in keys.Where(k => TrySplit(k, out _, out _)).GroupBy(k => { TrySplit(k, out var s, out _); return s; }))
        {
            var set = await requirements.GetRequirementsAsync(subject, group.Key, null, corr, ct);
            if (!set.IsOk) continue;
            foreach (var key in group)
            {
                TrySplit(key, out _, out var req);
                var row = set.Value!.Rows.FirstOrDefault(r => !r.Redacted && r.Key == req);
                if (row is not null) found[key] = (set.Value, row);
            }
        }
        return found;
    }

    public async Task<IReadOnlySet<string>> ReadableAsync(Subject subject, IReadOnlyList<string> keys, CancellationToken ct = default) =>
        (await LoadAsync(subject, keys, null, ct)).Keys.ToHashSet(StringComparer.Ordinal);

    public async Task<IReadOnlyDictionary<string, MassVerdict>> PreviewAsync(Subject subject, string operation, JsonObject parameters, IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        var found = await LoadAsync(subject, keys, null, ct);
        string? general = null;
        Markings? newMarkings = null;
        if (operation == MassOperations.SetRequirementField) Normalize(Str(parameters, "field")!, Str(parameters, "value")!, out general);
        else
        {
            var parsed = MassPartTarget.ParseMarkings(parameters["markings"] as JsonObject);
            if (!parsed.IsSuccess) general = string.Join(" ", parsed.Errors);
            else
            {
                newMarkings = parsed.Value;
                var d = AccessPolicy.Evaluate(subject, newMarkings, PlmAction.Modify);
                if (!d.Permitted) general = "You may not set these markings: you could not change data carrying them (" + string.Join(", ", d.Reasons) + ").";
            }
        }
        var result = new Dictionary<string, MassVerdict>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!found.TryGetValue(key, out var f)) { result[key] = new MassVerdict(false, NotFound); continue; }
            var (set, row) = f;
            string? reason = general;
            if (reason is null)
            {
                var modify = AccessPolicy.Evaluate(subject, row.Markings!, PlmAction.Modify);
                if (!modify.Permitted) reason = "You may not change it (" + string.Join(", ", modify.Reasons) + ").";
                else if (!set.IsLatest || set.State != SpecStates.InWork) reason = $"{set.SpecNumber} {set.Revision} is {SpecStates.Label(set.State)}. Only the latest revision of a specification that is In Work can be edited; revise it first.";
                else if (operation == MassOperations.ChangeMarkings && newMarkings == row.Markings) reason = "The markings are unchanged.";
                else if (operation == MassOperations.SetRequirementField && Current(row, Str(parameters, "field")!) == Normalize(Str(parameters, "field")!, Str(parameters, "value")!, out _)) reason = "It already has that value.";
            }
            result[key] = new MassVerdict(reason is null, reason, row.Title, row.Banner, SpecStates.Label(set.State));
        }
        return result;
    }

    private static string? Current(RequirementRow row, string field) => field switch
    {
        "priority" => row.Priority, "method" => row.Method, "status" => row.Status, "type" => row.Type, "rationale" => row.Rationale ?? "", _ => null,
    };

    private static RequirementInput With(RequirementRow r, string field, string value, Markings? markings) => new(
        r.Title!, r.Text!, field == "type" ? value : r.Type!, field == "priority" ? value : r.Priority!, field == "method" ? value : r.Method!,
        field == "rationale" ? value : r.Rationale, field == "status" ? value : r.Status!, Markings: markings);

    private async Task<Outcome<JsonObject?>> UpdateAsync(Subject subject, string key, string? field, string? value, string? expectedCurrent, Markings? markings, Markings? expectedMarkings, string comment, string corr, CancellationToken ct)
    {
        if (!TrySplit(key, out var spec, out var req)) return Outcome<JsonObject?>.NotFound();
        var set = await requirements.GetRequirementsAsync(subject, spec, null, corr, ct);
        var row = set.IsOk ? set.Value!.Rows.FirstOrDefault(r => !r.Redacted && r.Key == req) : null;
        if (row is null) return Outcome<JsonObject?>.NotFound();
        var previous = field is null ? null : Current(row, field);
        if (expectedCurrent is not null && previous != expectedCurrent) return Outcome<JsonObject?>.Conflict("The value was changed after the mass change; it is left as it is.");
        if (expectedMarkings is not null && row.Markings != expectedMarkings) return Outcome<JsonObject?>.Conflict("The markings were changed after the mass change; they are left as they are.");
        var input = With(row, field ?? "", value ?? "", markings);
        var edited = await requirements.EditAsync(subject, spec, new EditCommand(set.Value!.Iteration, comment, [new EditOp("update", req, input)]), corr, ct);
        if (!edited.IsOk) return new Outcome<JsonObject?>(edited.Kind, null, edited.Errors);
        return Outcome<JsonObject?>.Ok(field is not null
            ? new JsonObject { ["field"] = field, ["previous"] = previous, ["after"] = value }
            : new JsonObject { ["previous"] = MassPartTarget.ToJson(row.Markings!), ["after"] = MassPartTarget.ToJson(markings!) });
    }

    public async Task<Outcome<JsonObject?>> ApplyAsync(Subject subject, string operation, JsonObject parameters, string key, string correlationId, CancellationToken ct = default)
    {
        var comment = $"Mass change {correlationId[..8]}";
        if (operation == MassOperations.SetRequirementField)
        {
            var field = Str(parameters, "field")!;
            var value = Normalize(field, Str(parameters, "value")!, out var error);
            return value is null ? Outcome<JsonObject?>.Invalid([error!]) : await UpdateAsync(subject, key, field, value, null, null, null, comment, correlationId, ct);
        }
        var parsed = MassPartTarget.ParseMarkings(parameters["markings"] as JsonObject);
        return parsed.IsSuccess ? await UpdateAsync(subject, key, null, null, null, parsed.Value, null, comment, correlationId, ct) : Outcome<JsonObject?>.Invalid(parsed.Errors);
    }

    public async Task<Outcome<string>> UndoAsync(Subject subject, string operation, string key, JsonObject undo, string correlationId, CancellationToken ct = default)
    {
        var comment = $"Undo mass change {correlationId[..8]}";
        Outcome<JsonObject?> r;
        if (operation == MassOperations.SetRequirementField)
            r = await UpdateAsync(subject, key, undo["field"]!.GetValue<string>(), undo["previous"]?.GetValue<string>() ?? "", undo["after"]?.GetValue<string>(), null, null, comment, correlationId, ct);
        else
        {
            var prev = MassPartTarget.ParseMarkings(undo["previous"] as JsonObject);
            var after = MassPartTarget.ParseMarkings(undo["after"] as JsonObject);
            if (!prev.IsSuccess || !after.IsSuccess) return Outcome<string>.Invalid(["The recorded markings cannot be read."]);
            r = await UpdateAsync(subject, key, null, null, null, prev.Value, after.Value, comment, correlationId, ct);
        }
        return r.IsOk ? Outcome<string>.Ok("Restored in a new iteration of the specification.") : new Outcome<string>(r.Kind, null, r.Errors);
    }
}
