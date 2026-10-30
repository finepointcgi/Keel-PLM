using System.Text.Json.Nodes;
using Keel.Core;
using Keel.Core.Reports;
using Keel.Documents;
using Keel.Notifications;
using Keel.Parts;
using Keel.Security;
using Keel.Workflow;
using Keel.Workspace;
using Npgsql;

namespace Keel.Api.Infrastructure.Reports;

/// <summary>
/// Bulk operations on documents (migration 0029, ADR-0050): change the description, change markings, submit for review, watch, favorite. Each is the document service's own command
/// (check-out, edit, check-in; markings change; review start), so release locks, check-out ownership, quarantine rules, access checks and audit are the normal ones. Preview reads
/// through the access filter and writes no per-document event.
/// </summary>
public sealed class MassDocumentTarget(NpgsqlDataSource dataSource, DocumentService documents, WorkflowService workflow, WatchService watches, WorkspaceService workspace) : IMassChangeTarget
{
    public const string NotFound = "Not found, or you may not read it.";
    public string Kind => MassKinds.Document;
    public IReadOnlyCollection<string> Operations { get; } = [MassOperations.SetDescription, MassOperations.ChangeMarkings, MassOperations.SubmitReview, MassOperations.Watch, MassOperations.Favorite];

    private sealed record Doc(string Number, string Name, string Revision, string State, string Banner, Markings Markings, bool Locked, string? CheckedOutBy);

    private static string? Str(JsonObject p, string k) => p[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The documents among <paramref name="numbers"/> that the caller may read (one query with the read filter; no per-document event).</summary>
    private async Task<Dictionary<string, Doc>> FindAsync(Subject subject, IReadOnlyList<string> numbers, CancellationToken ct)
    {
        var found = new Dictionary<string, Doc>(StringComparer.Ordinal);
        if (numbers.Count == 0) return found;
        var filter = AccessPolicy.BuildReadFilter(subject, "m");
        var rows = new List<(string Number, string Name, string Revision, string State, Markings Markings, string LifecycleId, string? Out)>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT m.number, m.name, r.revision, r.lifecycle_state, m.lifecycle_id, c.user_name, {MarkingColumns.Select("m")}
            FROM plm.document_master m
            JOIN plm.document_revision r ON r.master_id = m.id AND r.is_latest
            LEFT JOIN plm.document_checkout c ON c.revision_id = r.id
            WHERE {filter.Sql} AND m.number = ANY(@numbers)
            """, conn))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("numbers", numbers.ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), MarkingColumns.Read(r, 6), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        }
        var lifecycles = new Dictionary<string, Lifecycle?>(StringComparer.Ordinal);
        foreach (var d in rows)
        {
            if (!lifecycles.TryGetValue(d.LifecycleId, out var lc)) lifecycles[d.LifecycleId] = lc = await Lifecycle.LoadAsync(conn, null, d.LifecycleId, ct);
            found[d.Number] = new Doc(d.Number, d.Name, d.Revision, d.State, d.Markings.BannerText(), d.Markings, lc?.IsLocked(d.State) ?? true, d.Out);
        }
        return found;
    }

    public async Task<IReadOnlySet<string>> ReadableAsync(Subject subject, IReadOnlyList<string> keys, CancellationToken ct = default) =>
        (await FindAsync(subject, keys, ct)).Keys.ToHashSet(StringComparer.Ordinal);

    // ---------------------------------------------------------------- preview

    public async Task<IReadOnlyDictionary<string, MassVerdict>> PreviewAsync(Subject subject, string operation, JsonObject parameters, IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        var found = await FindAsync(subject, keys, ct);
        string? general = null;
        Markings? newMarkings = null;
        if (operation == MassOperations.ChangeMarkings)
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
            if (!found.TryGetValue(key, out var d)) { result[key] = new MassVerdict(false, NotFound); continue; }
            string? reason = general;
            if (reason is null && operation is MassOperations.SetDescription or MassOperations.ChangeMarkings or MassOperations.SubmitReview)
            {
                var modify = AccessPolicy.Evaluate(subject, d.Markings, PlmAction.Modify);
                if (!modify.Permitted) reason = "You may not change it (" + string.Join(", ", modify.Reasons) + ").";
                else if (d.Locked && operation == MassOperations.ChangeMarkings) reason = $"Revision {d.Revision} is {d.State} and locked. Markings of locked data change through change control.";
                else if (d.Locked) reason = $"Revision {d.Revision} is {d.State} and locked. Revise it first.";
                else if (operation == MassOperations.SetDescription && d.CheckedOutBy is not null) reason = $"It is checked out by {d.CheckedOutBy}.";
                else if (operation == MassOperations.ChangeMarkings && newMarkings == d.Markings) reason = "The markings are unchanged.";
            }
            result[key] = new MassVerdict(reason is null, reason, d.Name, d.Banner, d.State);
        }
        return result;
    }

    // ---------------------------------------------------------------- apply

    public async Task<Outcome<JsonObject?>> ApplyAsync(Subject subject, string operation, JsonObject parameters, string key, string correlationId, CancellationToken ct = default)
    {
        switch (operation)
        {
            case MassOperations.SetDescription:
                return await SetDescriptionAsync(subject, key, Str(parameters, "description")!, null, $"Mass change {correlationId[..8]}", correlationId, ct);
            case MassOperations.ChangeMarkings:
            {
                var current = (await FindAsync(subject, [key], ct)).GetValueOrDefault(key);
                if (current is null) return Outcome<JsonObject?>.NotFound();
                var parsed = MassPartTarget.ParseMarkings(parameters["markings"] as JsonObject);
                if (!parsed.IsSuccess) return Outcome<JsonObject?>.Invalid(parsed.Errors);
                var changed = await documents.ChangeMarkingsAsync(subject, key, parsed.Value, Str(parameters, "reason"), correlationId, ct);
                return changed.IsOk ? Outcome<JsonObject?>.Ok(new JsonObject { ["previous"] = MassPartTarget.ToJson(current.Markings) }) : new Outcome<JsonObject?>(changed.Kind, null, changed.Errors);
            }
            case MassOperations.SubmitReview:
            {
                var participants = ((JsonObject)parameters["participants"]!).ToDictionary(kv => kv.Key, kv => (kv.Value as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray() ?? [], StringComparer.Ordinal);
                var started = await workflow.StartAsync(subject, new StartReviewCommand("document", key, participants, Str(parameters, "templateId"), Str(parameters, "comment")), correlationId, ct);
                return started.IsOk ? Outcome<JsonObject?>.Ok(null) : new Outcome<JsonObject?>(started.Kind, null, started.Errors);
            }
            case MassOperations.Watch:
            {
                var level = Str(parameters, "level") == "decisions" ? WatchLevel.Decisions : WatchLevel.All;
                var before = await watches.GetAsync(subject, AwarenessKinds.Document, key, ct);
                var set = await watches.SetAsync(subject, AwarenessKinds.Document, key, level, correlationId, ct);
                return set.IsOk ? Outcome<JsonObject?>.Ok(new JsonObject { ["previous"] = before is null ? null : WatchLevels.ToDb(before.Value) }) : new Outcome<JsonObject?>(set.Kind, null, set.Errors);
            }
            case MassOperations.Favorite:
            {
                if (!(await FindAsync(subject, [key], ct)).ContainsKey(key)) return Outcome<JsonObject?>.NotFound();
                if (await workspace.IsFavoriteAsync(subject, AwarenessKinds.Document, key, ct)) return Outcome<JsonObject?>.Ok(null);
                return await workspace.AddFavoriteAsync(subject, AwarenessKinds.Document, key, ct) ? Outcome<JsonObject?>.Ok([]) : Outcome<JsonObject?>.Conflict("You have reached the maximum number of favorites.");
            }
            default: return Outcome<JsonObject?>.Invalid(["Unknown operation."]);
        }
    }

    private async Task<Outcome<JsonObject?>> SetDescriptionAsync(Subject subject, string key, string description, string? expectedCurrent, string comment, string corr, CancellationToken ct)
    {
        var checkedOut = await documents.CheckOutAsync(subject, key, corr, ct);
        if (!checkedOut.IsOk) return new Outcome<JsonObject?>(checkedOut.Kind, null, checkedOut.Errors);
        async Task<Outcome<JsonObject?>> Fail(OutcomeKind kind, IReadOnlyList<string> errors)
        {
            await documents.UndoCheckOutAsync(subject, key, corr, CancellationToken.None);
            return new Outcome<JsonObject?>(kind == OutcomeKind.Ok ? OutcomeKind.Conflict : kind, null, errors.Count == 0 ? ["The change was refused."] : errors);
        }
        var previous = checkedOut.Value!.Checkout?.Description ?? "";
        if (expectedCurrent is not null && previous != expectedCurrent) return await Fail(OutcomeKind.Conflict, ["The description was changed after the mass change; it is left as it is."]);
        if (previous == description) return await Fail(OutcomeKind.Conflict, ["It already has that description."]);
        var saved = await documents.UpdateWorkingCopyAsync(subject, key, description, corr, ct);
        if (!saved.IsOk) return await Fail(saved.Kind, saved.Errors);
        var done = await documents.CheckInAsync(subject, key, comment, corr, ct);
        if (!done.IsOk) return await Fail(done.Kind, done.Errors);
        return Outcome<JsonObject?>.Ok(new JsonObject { ["previous"] = previous, ["after"] = description });
    }

    // ---------------------------------------------------------------- undo

    public async Task<Outcome<string>> UndoAsync(Subject subject, string operation, string key, JsonObject undo, string correlationId, CancellationToken ct = default)
    {
        switch (operation)
        {
            case MassOperations.SetDescription:
            {
                var r = await SetDescriptionAsync(subject, key, undo["previous"]?.GetValue<string>() ?? "", undo["after"]?.GetValue<string>(), $"Undo mass change {correlationId[..8]}", correlationId, ct);
                return r.IsOk ? Outcome<string>.Ok("Description restored in a new iteration.") : new Outcome<string>(r.Kind, null, r.Errors);
            }
            case MassOperations.ChangeMarkings:
            {
                var prev = MassPartTarget.ParseMarkings(undo["previous"] as JsonObject);
                if (!prev.IsSuccess) return Outcome<string>.Invalid(prev.Errors);
                var r = await documents.ChangeMarkingsAsync(subject, key, prev.Value, $"Undo mass change {correlationId[..8]}", correlationId, ct);
                return r.IsOk ? Outcome<string>.Ok("Markings restored.") : new Outcome<string>(r.Kind, null, r.Errors);
            }
            case MassOperations.Watch:
            {
                var previous = undo["previous"]?.GetValue<string>();
                if (previous is null) await watches.RemoveAsync(subject, AwarenessKinds.Document, key, ct);
                else await watches.SetAsync(subject, AwarenessKinds.Document, key, WatchLevels.FromDb(previous), correlationId, ct);
                return Outcome<string>.Ok("Watch restored.");
            }
            case MassOperations.Favorite:
                await workspace.RemoveFavoriteAsync(subject, AwarenessKinds.Document, key, ct);
                return Outcome<string>.Ok("Favorite removed.");
            default: return Outcome<string>.Invalid(["This operation cannot be undone."]);
        }
    }
}
