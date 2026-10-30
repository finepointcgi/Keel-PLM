using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Core;
using Keel.Core.Reports;
using Keel.Notifications;
using Keel.Parts;
using Keel.Security;
using Keel.Workflow;
using Keel.Workspace;
using Npgsql;

namespace Keel.Api.Infrastructure.Reports;

/// <summary>
/// Bulk operations on parts (ADR-0044). Every change is the owning service's own command (check-out, edit, check-in; markings change; review start; watch; favorite), so release locks,
/// iteration rules, access checks and audit are the normal ones. Preview makes no change and writes no per-part event.
/// </summary>
public sealed class MassPartTarget(NpgsqlDataSource dataSource, PartService parts, ICadPartPort partPort, WorkflowService workflow, WatchService watches, WorkspaceService workspace, IMassChangeBinder? binder = null) : IMassChangeTarget
{
    public const string NotFound = "Not found, or you may not read it.";
    public string Kind => "part";
    public IReadOnlyCollection<string> Operations { get; } = [MassOperations.SetAttribute, MassOperations.ChangeMarkings, MassOperations.SubmitReview, MassOperations.Watch, MassOperations.Favorite, MassOperations.CheckRead];

    private static string? Str(JsonObject p, string k) => p[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ---------------------------------------------------------------- preview

    public async Task<IReadOnlyDictionary<string, MassVerdict>> PreviewAsync(Subject subject, string operation, JsonObject parameters, IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        var found = (await partPort.FindManyAsync(subject, keys, ct)).ToDictionary(p => p.Number, StringComparer.Ordinal);
        string? general = null;
        Markings? newMarkings = null;
        if (operation == MassOperations.SetAttribute)
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            var def = (await AttributeValidator.LoadAsync(conn, null, PartService.ObjectType, ct)).FirstOrDefault(d => d.Name == Str(parameters, "name"));
            if (def is null || def.Retired) general = $"'{Str(parameters, "name")}' is not a part attribute defined by your administrator.";
            else if (Convert(def.DataType, Str(parameters, "value")!) is null) general = $"'{Str(parameters, "value")}' is not a valid {def.DataType} value for {def.Label}.";
        }
        else if (operation == MassOperations.ChangeMarkings)
        {
            var parsed = ParseMarkings(parameters["markings"] as JsonObject);
            if (!parsed.IsSuccess) general = string.Join(" ", parsed.Errors);
            else
            {
                newMarkings = parsed.Value;
                var d = AccessPolicy.Evaluate(subject, newMarkings, PlmAction.Modify);
                if (!d.Permitted) general = "You may not set these markings: you could not change data carrying them (" + string.Join(", ", d.Reasons) + ").";
            }
        }
        var order = operation is MassOperations.SetAttribute or MassOperations.ChangeMarkings ? MassBinding.Order(parameters) : null;
        var result = new Dictionary<string, MassVerdict>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!found.TryGetValue(key, out var p)) { result[key] = new MassVerdict(false, NotFound); continue; }
            string? reason = general;
            if (reason is null)
                switch (operation)
                {
                    case MassOperations.SetAttribute or MassOperations.ChangeMarkings or MassOperations.SubmitReview:
                        var modify = AccessPolicy.Evaluate(subject, p.Markings, PlmAction.Modify);
                        if (!modify.Permitted) reason = "You may not change it (" + string.Join(", ", modify.Reasons) + ").";
                        else if (p.IsLocked && order is not null && p.State != "Released") reason = $"Revision {p.Revision} is {p.State} and locked. Only a Released revision can be revised through {order}.";
                        else if (p.IsLocked && order is not null) reason = operation == MassOperations.ChangeMarkings && newMarkings == p.Markings ? "The markings are unchanged." : null;
                        else if (p.IsLocked && operation == MassOperations.ChangeMarkings) reason = $"Revision {p.Revision} is {p.State} and locked. Markings of locked data change through change control.";
                        else if (p.IsLocked) reason = $"Revision {p.Revision} is {p.State} and locked. Change it through an ECN.";
                        else if (operation == MassOperations.ChangeMarkings && newMarkings == p.Markings) reason = "The markings are unchanged.";
                        break;
                }
            result[key] = new MassVerdict(reason is null, reason ?? MassBinding.Note(order, p), p.Name, p.Banner, p.State);
        }
        return result;
    }

    public async Task<IReadOnlySet<string>> ReadableAsync(Subject subject, IReadOnlyList<string> keys, CancellationToken ct = default) =>
        (await partPort.FindManyAsync(subject, keys, ct)).Select(p => p.Number).ToHashSet(StringComparer.Ordinal);

    // ---------------------------------------------------------------- apply

    public async Task<Outcome<JsonObject?>> ApplyAsync(Subject subject, string operation, JsonObject parameters, string key, string correlationId, CancellationToken ct = default)
    {
        switch (operation)
        {
            case MassOperations.SetAttribute:
            {
                if (MassBinding.Order(parameters) is { } order)
                {
                    var infos = await partPort.FindManyAsync(subject, [key], ct);
                    var info = infos.Count == 0 ? null : infos[0];
                    if (info is null) return Outcome<JsonObject?>.NotFound();
                    if (await MassBinding.ListAsync<JsonObject?>(binder, subject, order, info, correlationId, ct) is { } failed) return failed;
                }
                return await SetAttributeAsync(subject, key, Str(parameters, "name")!, Str(parameters, "value")!, null, $"Mass change {correlationId[..8]}", correlationId, ct);
            }
            case MassOperations.ChangeMarkings:
            {
                var currentList = await partPort.FindManyAsync(subject, [key], ct);
                var current = currentList.Count == 0 ? null : currentList[0];
                if (current is null) return Outcome<JsonObject?>.NotFound();
                if (await MassBinding.ListAsync<JsonObject?>(binder, subject, MassBinding.Order(parameters), current, correlationId, ct) is { } failed) return failed;
                var parsed = ParseMarkings(parameters["markings"] as JsonObject);
                if (!parsed.IsSuccess) return Outcome<JsonObject?>.Invalid(parsed.Errors);
                var changed = await parts.ChangeMarkingsAsync(subject, key, parsed.Value, Str(parameters, "reason"), correlationId, ct);
                return changed.IsOk ? Outcome<JsonObject?>.Ok(new JsonObject { ["previous"] = ToJson(current.Markings) }) : new Outcome<JsonObject?>(changed.Kind, null, changed.Errors);
            }
            case MassOperations.SubmitReview:
            {
                var participants = ((JsonObject)parameters["participants"]!).ToDictionary(kv => kv.Key, kv => (kv.Value as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray() ?? [], StringComparer.Ordinal);
                var started = await workflow.StartAsync(subject, new StartReviewCommand("part", key, participants, Str(parameters, "templateId"), Str(parameters, "comment")), correlationId, ct);
                return started.IsOk ? Outcome<JsonObject?>.Ok(null) : new Outcome<JsonObject?>(started.Kind, null, started.Errors);
            }
            case MassOperations.Watch:
            {
                var level = Str(parameters, "level") == "decisions" ? WatchLevel.Decisions : WatchLevel.All;
                var before = await watches.GetAsync(subject, "part", key, ct);
                var set = await watches.SetAsync(subject, "part", key, level, correlationId, ct);
                return set.IsOk ? Outcome<JsonObject?>.Ok(new JsonObject { ["previous"] = before is null ? null : WatchLevels.ToDb(before.Value) }) : new Outcome<JsonObject?>(set.Kind, null, set.Errors);
            }
            case MassOperations.Favorite:
            {
                if ((await partPort.FindManyAsync(subject, [key], ct)).Count == 0) return Outcome<JsonObject?>.NotFound();
                if (await workspace.IsFavoriteAsync(subject, "part", key, ct)) return Outcome<JsonObject?>.Ok(null); // already a favorite: nothing to undo
                return await workspace.AddFavoriteAsync(subject, "part", key, ct) ? Outcome<JsonObject?>.Ok([]) : Outcome<JsonObject?>.Conflict("You have reached the maximum number of favorites.");
            }
            default: return Outcome<JsonObject?>.Invalid(["Unknown operation."]);
        }
    }

    private async Task<Outcome<JsonObject?>> SetAttributeAsync(Subject subject, string key, string name, string value, string? expectedCurrent, string comment, string corr, CancellationToken ct, bool remove = false)
    {
        var checkedOut = await parts.CheckOutAsync(subject, key, corr, ct);
        if (!checkedOut.IsOk) return new Outcome<JsonObject?>(checkedOut.Kind, null, checkedOut.Errors);
        async Task<Outcome<JsonObject?>> Fail(OutcomeKind kind, IReadOnlyList<string> errors)
        {
            await parts.UndoCheckOutAsync(subject, key, corr, CancellationToken.None);
            return new Outcome<JsonObject?>(kind == OutcomeKind.Ok ? OutcomeKind.Conflict : kind, null, errors.Count == 0 ? ["The change was refused."] : errors);
        }
        var working = checkedOut.Value!.Checkout;
        var attributes = working?.Attributes is { } a ? (JsonObject)a.DeepClone() : [];
        var previous = attributes[name] is JsonValue pv ? pv.ToString() : null;
        if (expectedCurrent is not null && previous != expectedCurrent) return await Fail(OutcomeKind.Conflict, ["The value was changed after the mass change; it is left as it is."]);
        JsonValue? typed = null;
        if (!remove)
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            var def = (await AttributeValidator.LoadAsync(conn, null, PartService.ObjectType, ct)).FirstOrDefault(d => d.Name == name);
            typed = def is null ? null : Convert(def.DataType, value);
            if (typed is null) return await Fail(OutcomeKind.Invalid, [$"'{value}' is not a valid value for {name}."]);
            if (previous == typed.ToString()) return await Fail(OutcomeKind.Conflict, ["It already has that value."]);
        }
        if (remove) attributes.Remove(name); else attributes[name] = typed;
        var saved = await parts.UpdateWorkingCopyAsync(subject, key, working?.Description, attributes, corr, ct);
        if (!saved.IsOk) return await Fail(saved.Kind, saved.Errors);
        var done = await parts.CheckInAsync(subject, key, comment, corr, ct);
        if (!done.IsOk) return await Fail(done.Kind, done.Errors);
        return Outcome<JsonObject?>.Ok(new JsonObject { ["name"] = name, ["previous"] = previous, ["after"] = typed?.ToString() });
    }

    // ---------------------------------------------------------------- undo

    public async Task<Outcome<string>> UndoAsync(Subject subject, string operation, string key, JsonObject undo, string correlationId, CancellationToken ct = default)
    {
        switch (operation)
        {
            case MassOperations.SetAttribute:
            {
                var name = undo["name"]!.GetValue<string>();
                var previous = undo["previous"]?.GetValue<string>();
                var r = await SetAttributeAsync(subject, key, name, previous ?? "", undo["after"]?.GetValue<string>(), $"Undo mass change {correlationId[..8]}", correlationId, ct, remove: previous is null);
                return r.IsOk ? Outcome<string>.Ok("Value restored in a new iteration.") : new Outcome<string>(r.Kind, null, r.Errors);
            }
            case MassOperations.ChangeMarkings:
            {
                var prev = ParseMarkings(undo["previous"] as JsonObject);
                if (!prev.IsSuccess) return Outcome<string>.Invalid(prev.Errors);
                var r = await parts.ChangeMarkingsAsync(subject, key, prev.Value, $"Undo mass change {correlationId[..8]}", correlationId, ct);
                return r.IsOk ? Outcome<string>.Ok("Markings restored.") : new Outcome<string>(r.Kind, null, r.Errors);
            }
            case MassOperations.Watch:
            {
                var previous = undo["previous"]?.GetValue<string>();
                if (previous is null) await watches.RemoveAsync(subject, "part", key, ct);
                else await watches.SetAsync(subject, "part", key, WatchLevels.FromDb(previous), correlationId, ct);
                return Outcome<string>.Ok("Watch restored.");
            }
            case MassOperations.Favorite:
                await workspace.RemoveFavoriteAsync(subject, "part", key, ct);
                return Outcome<string>.Ok("Favorite removed.");
            default: return Outcome<string>.Invalid(["This operation cannot be undone."]);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static JsonValue? Convert(string dataType, string value) => dataType switch
    {
        "number" when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) => JsonValue.Create(n),
        "number" => null,
        "boolean" when bool.TryParse(value, out var b) => JsonValue.Create(b),
        "boolean" => null,
        _ => JsonValue.Create(value),
    };

    public static JsonObject ToJson(Markings m) => Keel.Reports.MassParameters.MarkingsJson(m);

    public static Result<Markings> ParseMarkings(JsonObject? o)
    {
        if (o is null) return Result<Markings>.Fail(["Give the new markings."]);
        string? S(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        string[] L(string k) => o[k] is JsonArray a ? [.. a.Select(n => n?.GetValue<string>() ?? "")] : [];
        if (!Enum.TryParse<ExportJurisdiction>(S("jurisdiction") ?? "None", true, out var j)) return Result<Markings>.Fail(["Export jurisdiction must be None, Ear or Itar."]);
        if (!Enum.TryParse<DistributionStatement>(S("distributionStatement") ?? "", true, out var d)) return Result<Markings>.Fail(["Distribution statement must be A to F."]);
        return Markings.Create(j, d, S("owningOrg") ?? "", S("eccn"), S("usmlCategory"), L("cuiCategories"), S("controllingOffice"), S("distributionReason"), S("program"), L("exportAuthorizations"));
    }
}
