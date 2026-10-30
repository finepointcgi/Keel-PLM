using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Core.Reports;
using Keel.Reports.Tabular;
using Npgsql;

namespace Keel.Reports;

public sealed record MassItem(string Key, string? Name, string? Banner, string? State, bool CanApply, string? Reason, string Result, string? Detail, bool Reversible);

public sealed record MassPreview(
    string Number, string Operation, string ObjectKind, string Status, IReadOnlyList<MassItem> Items, int WillChange, int Refused, int Applied, int Failed, int Undone, DateTimeOffset CreatedAt, string CorrelationId,
    string? ChangeNumber = null, string Source = "typed", string? SourceDetail = null)
{
    public bool CanApply => Status == "previewed" && Items.Any(i => i.CanApply && i.Result is "pending" or "failed");
    public bool CanUndo => Status == "applied" && Items.Any(i => i.Result == "applied" && i.Reversible);
}

/// <param name="ChangeNumber">Optional ECN the change is attached to (parts and assemblies only).</param>
/// <param name="ItemParameters">Parameters that belong to one object (from an uploaded file); they override the shared ones for that object.</param>
/// <param name="Source">typed, where_used or upload: where the selection came from (recorded, shown).</param>
public sealed record MassRequest(string ObjectKind, IReadOnlyList<string> Keys, string Operation, JsonObject? Parameters, string? ChangeNumber = null,
    IReadOnlyDictionary<string, JsonObject>? ItemParameters = null, string Source = "typed", string? SourceDetail = null);

/// <summary>A selection filled by a selector (where-used of a component), read-filtered for the caller. <see cref="Truncated"/> says there were more than the cap allows.</summary>
public sealed record MassSelection(IReadOnlyList<string> Keys, bool Truncated);

public sealed record MassUploadRequest(string FileName, byte[] Content, string? ObjectKind, string? Operation, JsonObject? Parameters, string? ChangeNumber);

/// <summary>
/// Mass change (ADR-0044). Preview decides, per object and without changing anything, what would happen and why not (access, release lock, state); apply runs object by object through the
/// owning services (each writes its own audit event, all with the same correlation id), reports partial failure, can be repeated (finished objects are skipped), and records what is
/// needed to undo the reversible operations. Hard caps: <see cref="ReportsLimits.MaxMassObjects"/> objects per operation, <see cref="ReportsLimits.MaxMassPerHour"/> previews per user per hour.
/// </summary>
public sealed class MassChangeService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, IEnumerable<IMassChangeTarget> targets, IMassChangeBinder? binder = null)
{
    private static readonly string[] Operations = [MassOperations.SetAttribute, MassOperations.ChangeMarkings, MassOperations.BaselineProposal, MassOperations.SubmitReview, MassOperations.Watch, MassOperations.Favorite,
        MassOperations.SetDescription, MassOperations.AddComponent, MassOperations.ReplaceComponent, MassOperations.RemoveComponent, MassOperations.SetRequirementField];
    private static readonly string[] Reversible = [MassOperations.SetAttribute, MassOperations.ChangeMarkings, MassOperations.BaselineProposal, MassOperations.Watch, MassOperations.Favorite,
        MassOperations.SetDescription, MassOperations.AddComponent, MassOperations.ReplaceComponent, MassOperations.RemoveComponent, MassOperations.SetRequirementField];
    /// <summary>Operations that change an object's data and so may be attached to a change order (parts and assemblies).</summary>
    private static readonly string[] Attachable = [MassOperations.SetAttribute, MassOperations.ChangeMarkings, MassOperations.AddComponent, MassOperations.ReplaceComponent, MassOperations.RemoveComponent];

    private readonly Dictionary<string, IMassChangeTarget> byKind = targets.ToDictionary(t => t.Kind, StringComparer.Ordinal);

    public IReadOnlyList<string> KindsFor(string operation) =>
        [.. MassKinds.All.Where(k => byKind.TryGetValue(k, out var t) && (t.Operations.Contains(operation) || operation == MassOperations.BaselineProposal && MassKinds.IsPartLike(k)))];
    public static bool CanAttachToChange(string kind, string operation) => MassKinds.IsPartLike(kind) && Attachable.Contains(operation);
    public static string KindLabel(string kind) => kind switch { MassKinds.Document => "Documents", MassKinds.Assembly => "Assembly structure", MassKinds.Requirement => "Requirements", _ => "Parts" };
    public static IReadOnlyList<string> AllOperations => Operations;
    public static bool IsReversible(string operation) => Reversible.Contains(operation);

    private static string? Str(JsonObject p, string k) => p[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static decimal? Num(JsonObject p, string k)
    {
        if (p[k] is not JsonValue v) return null;
        if (v.TryGetValue<decimal>(out var d)) return d;
        if (v.TryGetValue<double>(out var dd)) return (decimal)dd;
        if (v.TryGetValue<int>(out var i)) return i;
        return v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    /// <summary>The parameters one object is changed with: the shared ones, overridden by that object's own (from an uploaded file).</summary>
    private static JsonObject Effective(JsonObject shared, JsonObject? own)
    {
        var merged = (JsonObject)shared.DeepClone();
        if (own is not null) foreach (var (k, v) in own) if (k != MassParameterNames.Change) merged[k] = v?.DeepClone();
        return merged;
    }

    /// <summary>
    /// Where-used driven selection: the keys a selector yields for a value, read-filtered for the caller (an assembly the caller may not read is not offered, and not counted).
    /// Audited; changes nothing.
    /// </summary>
    public async Task<Outcome<MassSelection>> SelectAsync(Subject subject, string objectKind, string selector, string value, string? correlationId, CancellationToken ct = default)
    {
        if (!byKind.TryGetValue(objectKind ?? "", out var target) || !target.Selectors.Contains(selector ?? "")) return Outcome<MassSelection>.Invalid(["That kind of object cannot be selected that way."]);
        value = (value ?? "").Trim();
        if (value.Length is 0 or > 64) return Outcome<MassSelection>.Invalid(["Give the part number of the component."]);
        var keys = await target.SelectAsync(subject, selector!, value, ReportsLimits.MaxMassObjects + 1, ct);
        if (keys is null)
        {
            await audit.AppendAsync(ReportsAudit.Entry(subject, "output.mass.select", AuditOutcome.Failure, "output", null, correlationId, ("kind", objectKind), ("selector", selector), ("value", value)), ct);
            return Outcome<MassSelection>.NotFound();
        }
        var truncated = keys.Count > ReportsLimits.MaxMassObjects;
        var taken = truncated ? [.. keys.Take(ReportsLimits.MaxMassObjects)] : keys;
        await audit.AppendAsync(ReportsAudit.Entry(subject, "output.mass.select", AuditOutcome.Success, "output", null, correlationId,
            ("kind", objectKind), ("selector", selector), ("value", value), ("selected", taken.Count.ToString(CultureInfo.InvariantCulture)), ("truncated", truncated.ToString(CultureInfo.InvariantCulture))), ct);
        return Outcome<MassSelection>.Ok(new MassSelection(taken, truncated));
    }

    /// <summary>
    /// Preview from an uploaded CSV or .xlsx (UTF-8 CSV or the first sheet of a workbook): the file names the objects and, optionally, each object's own values. The result is
    /// an ordinary preview (same checks, same stored verdicts); the file is not kept, only its name and SHA-256 in the audit trail.
    /// </summary>
    public async Task<Outcome<MassPreview>> PreviewUploadAsync(Subject subject, MassUploadRequest upload, string? correlationId, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(upload.FileName ?? "mass-change.csv");
        if (fileName.Length > 200) fileName = fileName[..200];
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(upload.Content ?? []));
        MassUploadParse parsed;
        try { parsed = MassUpload.Parse(TableReader.Read(fileName, upload.Content ?? [])); }
        catch (XlsxFormatException e)
        {
            await audit.AppendAsync(ReportsAudit.Entry(subject, "output.mass.upload", AuditOutcome.Failure, "output", null, correlationId, ("file", fileName), ("sha256", hash), ("reason", "format")), ct);
            return Outcome<MassPreview>.Invalid([e.Message]);
        }
        var errors = parsed.Errors.ToList();
        var kind = string.IsNullOrWhiteSpace(upload.ObjectKind) ? parsed.Kind : upload.ObjectKind;
        var operation = string.IsNullOrWhiteSpace(upload.Operation) ? parsed.Operation : upload.Operation;
        if (!string.IsNullOrWhiteSpace(upload.ObjectKind) && parsed.Kind is not null && parsed.Kind != upload.ObjectKind) errors.Add($"The file says the kind is '{parsed.Kind}' but you chose '{upload.ObjectKind}'.");
        if (!string.IsNullOrWhiteSpace(upload.Operation) && parsed.Operation is not null && parsed.Operation != upload.Operation) errors.Add($"The file says the operation is '{parsed.Operation}' but you chose '{upload.Operation}'.");
        if (kind is null) errors.Add("Say which kind of object the file changes (choose it, or add a kind column).");
        if (operation is null) errors.Add("Say which operation the file runs (choose it, or add an operation column).");
        if (errors.Count > 0)
        {
            await audit.AppendAsync(ReportsAudit.Entry(subject, "output.mass.upload", AuditOutcome.Failure, "output", null, correlationId, ("file", fileName), ("sha256", hash), ("reason", "content"), ("errors", errors.Count.ToString(CultureInfo.InvariantCulture))), ct);
            return Outcome<MassPreview>.Invalid(errors);
        }
        await audit.AppendAsync(ReportsAudit.Entry(subject, "output.mass.upload", AuditOutcome.Success, "output", null, correlationId,
            ("file", fileName), ("sha256", hash), ("rows", parsed.DataRows.ToString(CultureInfo.InvariantCulture)), ("kind", kind), ("operation", operation)), ct);
        return await PreviewAsync(subject, new MassRequest(kind!, parsed.Keys, operation!, upload.Parameters, upload.ChangeNumber, parsed.ItemParameters, "upload", fileName), correlationId, ct);
    }

    public async Task<Outcome<MassPreview>> PreviewAsync(Subject subject, MassRequest request, string? correlationId, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (!Operations.Contains(request.Operation)) errors.Add("Choose one of the operations offered.");
        if (!byKind.TryGetValue(request.ObjectKind ?? "", out var target)) errors.Add("That kind of object cannot be changed in bulk yet.");
        var keys = (request.Keys ?? []).Select(k => (k ?? "").Trim()).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0) errors.Add("Select at least one object.");
        if (keys.Count > ReportsLimits.MaxMassObjects) errors.Add($"At most {ReportsLimits.MaxMassObjects} objects can be changed in one operation; you selected {keys.Count}.");
        // The reserved change-order parameter is set here from the checked change order and nowhere else.
        var parameters = request.Parameters is null ? [] : (JsonObject)request.Parameters.DeepClone();
        parameters.Remove(MassParameterNames.Change);
        var own = (request.ItemParameters ?? new Dictionary<string, JsonObject>()).Where(kv => keys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (k, v) in own.ToList()) { v.Remove(MassParameterNames.Change); if (v.Count == 0) own.Remove(k); }
        if (errors.Count == 0 && target is not null)
        {
            if (request.Operation != MassOperations.BaselineProposal && !target.Operations.Contains(request.Operation)) errors.Add("That operation is not available for this kind of object.");
            if (request.Operation == MassOperations.BaselineProposal && !MassKinds.IsPartLike(request.ObjectKind!)) errors.Add("Only parts and assemblies can be added to a baseline proposal.");
        }
        if (errors.Count == 0)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var eff = Effective(parameters, own.GetValueOrDefault(key));
                if (!seen.Add(eff.ToJsonString())) continue;
                foreach (var e in ValidateParameters(request.Operation, eff)) if (!errors.Contains(e) && errors.Count < 10) errors.Add(e);
            }
        }
        MassChangeOrder? order = null;
        if (!string.IsNullOrWhiteSpace(request.ChangeNumber))
        {
            if (errors.Count == 0 && !CanAttachToChange(request.ObjectKind!, request.Operation))
                errors.Add("A change order lists parts and assemblies. Attach it only to a change of part attributes, part markings or assembly structure.");
            else if (errors.Count == 0)
            {
                var found = binder is null ? Outcome<MassChangeOrder>.Conflict("Change orders are not available.") : await binder.FindAsync(subject, request.ChangeNumber.Trim(), ct);
                if (found.IsOk) { order = found.Value; parameters[MassParameterNames.Change] = order!.Number; }
                else errors.Add(found.Kind == OutcomeKind.NotFound ? "That change order was not found, or you may not use it." : string.Join(" ", found.Errors.DefaultIfEmpty("You may not attach to that change order.")));
            }
        }
        if (errors.Count > 0 || target is null) return Outcome<MassPreview>.Invalid(errors);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var rate = new NpgsqlCommand("SELECT count(*) FROM rpt.mass_change WHERE created_by_sub = @s AND created_at > @since", conn))
        {
            rate.Parameters.AddWithValue("s", subject.Id);
            rate.Parameters.AddWithValue("since", clock.GetUtcNow().AddHours(-1));
            if ((long)(await rate.ExecuteScalarAsync(ct))! >= ReportsLimits.MaxMassPerHour)
                return Outcome<MassPreview>.Conflict($"You have started {ReportsLimits.MaxMassPerHour} bulk operations in the last hour. Try again later.");
        }
        var previewOp = request.Operation == MassOperations.BaselineProposal ? MassOperations.CheckRead : request.Operation;
        // One verdict query per distinct set of parameters (one for a typed list; one per row when a file gives each row its own values).
        var verdicts = new Dictionary<string, MassVerdict>(StringComparer.Ordinal);
        foreach (var group in keys.GroupBy(k => Effective(parameters, own.GetValueOrDefault(k)).ToJsonString()))
            foreach (var (k, v) in await target.PreviewAsync(subject, previewOp, Effective(parameters, own.GetValueOrDefault(group.First())), [.. group], ct)) verdicts[k] = v;
        var id = Guid.CreateVersion7();
        var corr = Guid.NewGuid().ToString("N");
        var now = clock.GetUtcNow();
        var source = request.Source is "where_used" or "upload" ? request.Source : "typed";
        var sourceDetail = request.SourceDetail is { Length: > 200 } sd ? sd[..200] : request.SourceDetail;
        await using var tx = await conn.BeginTransactionAsync(ct);
        long seq;
        await using (var s = new NpgsqlCommand("SELECT nextval('rpt.mass_number_seq')", conn, tx)) seq = (long)(await s.ExecuteScalarAsync(ct))!;
        var number = "MCH-" + seq.ToString("D6", CultureInfo.InvariantCulture);
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO rpt.mass_change (id, number, correlation_id, operation, object_kind, parameters, status, created_at, created_by, created_by_sub, change_number, source, source_detail) VALUES (@id, @n, @c, @op, @k, @p::jsonb, 'previewed', @at, @by, @sub, @chg, @src, @srcd)", conn, tx))
        {
            ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("n", number); ins.Parameters.AddWithValue("c", corr);
            ins.Parameters.AddWithValue("op", request.Operation); ins.Parameters.AddWithValue("k", request.ObjectKind!);
            ins.Parameters.AddWithValue("p", parameters.ToJsonString()); ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", subject.Username ?? subject.DisplayName); ins.Parameters.AddWithValue("sub", subject.Id);
            ins.Parameters.AddWithValue("chg", (object?)order?.Number ?? DBNull.Value); ins.Parameters.AddWithValue("src", source); ins.Parameters.AddWithValue("srcd", (object?)sourceDetail ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync(ct);
        }
        var items = new List<MassItem>();
        var seqNo = 0;
        foreach (var key in keys)
        {
            var v = verdicts.TryGetValue(key, out var found) ? found : new MassVerdict(false, "Not found, or you may not read it.");
            if (key.Length > 64) v = new MassVerdict(false, "Not found, or you may not read it.");
            await using var item = new NpgsqlCommand("INSERT INTO rpt.mass_change_item (change_id, seq, object_key, verdict, reason, item_parameters) VALUES (@c, @s, @k, @v, @r, @ip::jsonb)", conn, tx);
            item.Parameters.AddWithValue("c", id); item.Parameters.AddWithValue("s", seqNo++); item.Parameters.AddWithValue("k", key.Length > 64 ? key[..64] : key);
            item.Parameters.AddWithValue("v", v.CanApply ? "will_change" : "refused"); item.Parameters.AddWithValue("r", (object?)v.Reason ?? DBNull.Value);
            item.Parameters.AddWithValue("ip", own.TryGetValue(key, out var ownParams) ? ownParams.ToJsonString() : DBNull.Value);
            await item.ExecuteNonQueryAsync(ct);
            items.Add(new MassItem(key, v.Name, v.Banner, v.State, v.CanApply, v.Reason, "pending", null, IsReversible(request.Operation)));
        }
        await audit.AppendAsync(tx, ReportsAudit.Entry(subject, "output.mass.preview", AuditOutcome.Success, "output", number, corr,
            ("operation", request.Operation), ("kind", request.ObjectKind), ("selected", keys.Count.ToString(CultureInfo.InvariantCulture)),
            ("will_change", items.Count(i => i.CanApply).ToString(CultureInfo.InvariantCulture)), ("refused", items.Count(i => !i.CanApply).ToString(CultureInfo.InvariantCulture)),
            ("source", source), ("source_detail", sourceDetail), ("change_order", order?.Number)), ct);
        await tx.CommitAsync(ct);
        return Outcome<MassPreview>.Ok(Build(number, request.Operation, request.ObjectKind!, "previewed", items, now, corr, order?.Number, source, sourceDetail));
    }

    private static IEnumerable<string> ValidateParameters(string op, JsonObject p)
    {
        switch (op)
        {
            case MassOperations.SetAttribute:
                if (string.IsNullOrWhiteSpace(Str(p, "name")) || Str(p, "name")!.Length > 60) yield return "Name the attribute to change.";
                if (Str(p, "value") is null || Str(p, "value")!.Length > 500) yield return "Give the new value (at most 500 characters).";
                break;
            case MassOperations.ChangeMarkings:
                if (string.IsNullOrWhiteSpace(Str(p, "reason"))) yield return "A reason is required to change markings.";
                if (p["markings"] is not JsonObject) yield return "Give the new markings.";
                break;
            case MassOperations.BaselineProposal:
                if (string.IsNullOrWhiteSpace(Str(p, "name")) || Str(p, "name")!.Length > 120) yield return "Name the baseline proposal (at most 120 characters).";
                break;
            case MassOperations.Watch:
                if (Str(p, "level") is not (null or "all" or "decisions")) yield return "Watch level must be all or decisions.";
                break;
            case MassOperations.SubmitReview:
                if (p["participants"] is not JsonObject) yield return "Name the reviewers for each review slot.";
                break;
            case MassOperations.SetDescription:
                if (Str(p, "description") is not { } d || d.Length > 4000) yield return "Give the new description (at most 4000 characters).";
                break;
            case MassOperations.AddComponent:
                if (string.IsNullOrWhiteSpace(Str(p, "component")) || Str(p, "component")!.Length > 64) yield return "Name the component to add by its part number.";
                if (Num(p, "quantity") is not > 0) yield return "Give a quantity above zero for the component to add.";
                if (p["find_number"] is not null && Num(p, "find_number") is not > 0) yield return "The find number must be a whole number above zero.";
                break;
            case MassOperations.ReplaceComponent:
                if (string.IsNullOrWhiteSpace(Str(p, "component")) || Str(p, "component")!.Length > 64) yield return "Name the component to replace by its part number.";
                if (string.IsNullOrWhiteSpace(Str(p, "replacement")) || Str(p, "replacement")!.Length > 64) yield return "Name the replacement component by its part number.";
                else if (Str(p, "replacement") == Str(p, "component")) yield return "The replacement is the same part as the component it replaces.";
                if (p["quantity"] is not null && Num(p, "quantity") is not > 0) yield return "The quantity must be above zero.";
                break;
            case MassOperations.RemoveComponent:
                if (string.IsNullOrWhiteSpace(Str(p, "component")) || Str(p, "component")!.Length > 64) yield return "Name the component to remove by its part number.";
                break;
            case MassOperations.SetRequirementField:
                if (Str(p, "field") is not ("priority" or "method" or "status" or "type" or "rationale")) yield return "Choose the field to change: priority, method, status, type or rationale.";
                if (Str(p, "value") is not { } v || v.Length > 4000) yield return "Give the new value.";
                break;
        }
    }

    private static MassPreview Build(string number, string op, string kind, string status, IReadOnlyList<MassItem> items, DateTimeOffset at, string corr, string? changeNumber = null, string source = "typed", string? sourceDetail = null) =>
        new(number, op, kind, status, items, items.Count(i => i.CanApply), items.Count(i => !i.CanApply), items.Count(i => i.Result == "applied"), items.Count(i => i.Result is "failed" or "undo_failed"),
            items.Count(i => i.Result == "undone"), at, corr, changeNumber, source, sourceDetail);

    private sealed record Header(Guid Id, string Number, string Correlation, string Operation, string Kind, JsonObject Parameters, string Status, DateTimeOffset At, string Sub, string? ChangeNumber, string Source, string? SourceDetail);

    private async Task<(Header? H, List<(int Seq, MassItem Item, JsonObject? Undo, JsonObject? Own)> Items)> LoadAsync(string number, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        Header? h = null;
        await using (var cmd = new NpgsqlCommand("SELECT id, number, correlation_id, operation, object_kind, parameters::text, status, created_at, created_by_sub, change_number, source, source_detail FROM rpt.mass_change WHERE number = @n", conn))
        {
            cmd.Parameters.AddWithValue("n", number ?? "");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) h = new Header(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), JsonNode.Parse(r.GetString(5)) as JsonObject ?? [], r.GetString(6), r.GetFieldValue<DateTimeOffset>(7), r.GetString(8),
                r.IsDBNull(9) ? null : r.GetString(9), r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11));
        }
        var items = new List<(int, MassItem, JsonObject?, JsonObject?)>();
        if (h is null) return (null, items);
        await using var cmd2 = new NpgsqlCommand("SELECT seq, object_key, verdict, reason, result, detail, undo::text, item_parameters::text FROM rpt.mass_change_item WHERE change_id = @c ORDER BY seq", conn);
        cmd2.Parameters.AddWithValue("c", h.Id);
        await using var r2 = await cmd2.ExecuteReaderAsync(ct);
        while (await r2.ReadAsync(ct))
            items.Add((r2.GetInt32(0), new MassItem(r2.GetString(1), null, null, null, r2.GetString(2) == "will_change", r2.IsDBNull(3) ? null : r2.GetString(3), r2.GetString(4), r2.IsDBNull(5) ? null : r2.GetString(5),
                IsReversible(h.Operation) && !r2.IsDBNull(6)), r2.IsDBNull(6) ? null : JsonNode.Parse(r2.GetString(6)) as JsonObject,
                r2.IsDBNull(7) ? null : JsonNode.Parse(r2.GetString(7)) as JsonObject));
        return (h, items);
    }

    public async Task<Outcome<MassPreview>> GetAsync(Subject subject, string number, CancellationToken ct = default)
    {
        var (h, items) = await LoadAsync(number, ct);
        if (h is null || h.Sub != subject.Id) return Outcome<MassPreview>.NotFound();
        // Objects are re-authorized when shown: one the owner can no longer read appears as a restricted row.
        var readable = byKind.TryGetValue(h.Kind, out var t) ? await t.ReadableAsync(subject, [.. items.Select(i => i.Item.Key)], ct) : new HashSet<string>();
        var shown = items.Select(i => readable.Contains(i.Item.Key) ? i.Item : i.Item with { Key = "(restricted)", Detail = null, Reason = i.Item.Reason is null ? null : "Restricted." }).ToList();
        return Outcome<MassPreview>.Ok(Build(h.Number, h.Operation, h.Kind, h.Status, shown, h.At, h.Correlation, h.ChangeNumber, h.Source, h.SourceDetail));
    }

    public async Task<IReadOnlyList<string>> ListAsync(Subject subject, CancellationToken ct = default)
    {
        var list = new List<string>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT number FROM rpt.mass_change WHERE created_by_sub = @s ORDER BY created_at DESC LIMIT 30", conn);
        cmd.Parameters.AddWithValue("s", subject.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    public async Task<Outcome<MassPreview>> ApplyAsync(Subject subject, string number, string? requestCorrelationId, CancellationToken ct = default)
    {
        var (h, items) = await LoadAsync(number, ct);
        if (h is null || h.Sub != subject.Id) return Outcome<MassPreview>.NotFound();
        if (h.Status != "previewed") return Outcome<MassPreview>.Conflict($"This operation is {h.Status}.");
        if (!byKind.TryGetValue(h.Kind, out var target)) return Outcome<MassPreview>.NotFound();
        var todo = items.Where(i => i.Item.CanApply && i.Item.Result is "pending" or "failed").ToList(); // finished objects are skipped: a retry is safe
        if (todo.Count == 0) return Outcome<MassPreview>.Conflict("There is nothing left to apply.");
        Outcome<MassPreview>? stepUp = null;
        foreach (var (seq, item, _, own) in todo)
        {
            string result, detail; JsonObject? undo = null;
            if (h.Operation == MassOperations.BaselineProposal)
            {
                if ((await target.ReadableAsync(subject, [item.Key], ct)).Contains(item.Key))
                {
                    await AddToProposalAsync(subject, h.Parameters["name"]!.GetValue<string>(), item.Key, ct);
                    result = "applied"; detail = "Added to the proposal."; undo = new JsonObject { ["name"] = h.Parameters["name"]!.GetValue<string>() };
                }
                else { result = "failed"; detail = "Not found, or you may not read it."; }
            }
            else
            {
                var applied = await target.ApplyAsync(subject, h.Operation, Effective(h.Parameters, own), item.Key, h.Correlation, ct);
                if (applied.Kind == OutcomeKind.StepUpRequired)
                {
                    stepUp = new Outcome<MassPreview>(OutcomeKind.StepUpRequired, null, applied.Errors);
                    break; // the rest stays pending; sign in again and apply again
                }
                if (applied.IsOk) { result = "applied"; detail = "Changed."; undo = applied.Value; }
                else { result = "failed"; detail = applied.Kind == OutcomeKind.NotFound ? "Not found, or you may not read it." : string.Join(" ", applied.Errors); }
            }
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var upd = new NpgsqlCommand("UPDATE rpt.mass_change_item SET result = @r, detail = @d, undo = @u::jsonb WHERE change_id = @c AND seq = @s", conn);
            upd.Parameters.AddWithValue("r", result); upd.Parameters.AddWithValue("d", detail);
            upd.Parameters.AddWithValue("u", (object?)undo?.ToJsonString() ?? DBNull.Value); upd.Parameters.AddWithValue("c", h.Id); upd.Parameters.AddWithValue("s", seq);
            await upd.ExecuteNonQueryAsync(ct);
        }
        var (_, after) = await LoadAsync(number, ct);
        var complete = after.Where(i => i.Item.CanApply).All(i => i.Item.Result == "applied");
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            if (complete)
            {
                await using var upd = new NpgsqlCommand("UPDATE rpt.mass_change SET status = 'applied', applied_at = @at WHERE id = @id", conn, tx);
                upd.Parameters.AddWithValue("at", clock.GetUtcNow()); upd.Parameters.AddWithValue("id", h.Id);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await audit.AppendAsync(tx, ReportsAudit.Entry(subject, "output.mass.apply", complete ? AuditOutcome.Success : AuditOutcome.Failure, "output", number, h.Correlation,
                ("operation", h.Operation), ("change_order", h.ChangeNumber), ("applied", after.Count(i => i.Item.Result == "applied").ToString(CultureInfo.InvariantCulture)),
                ("failed", after.Count(i => i.Item.Result == "failed").ToString(CultureInfo.InvariantCulture)), ("pending", after.Count(i => i.Item.CanApply && i.Item.Result == "pending").ToString(CultureInfo.InvariantCulture))), ct);
            await tx.CommitAsync(ct);
        }
        if (stepUp is not null) return stepUp;
        var view = await GetAsync(subject, number, ct);
        return !complete && view.IsOk
            ? new Outcome<MassPreview>(OutcomeKind.Conflict, view.Value, [$"{view.Value!.Failed} object(s) could not be changed. The others were changed; fix the reasons and apply again to retry only the failed ones."])
            : view;
    }

    private async Task AddToProposalAsync(Subject subject, string name, string part, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("INSERT INTO rpt.baseline_proposal (name, owner_sub, part_number, added_at) VALUES (@n, @o, @p, @at) ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue("n", name); cmd.Parameters.AddWithValue("o", subject.Id); cmd.Parameters.AddWithValue("p", part); cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The parts of a baseline proposal that the owner can still read.</summary>
    public async Task<IReadOnlyList<string>> ProposalPartsAsync(Subject subject, string name, CancellationToken ct = default)
    {
        var keys = new List<string>();
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand("SELECT part_number FROM rpt.baseline_proposal WHERE owner_sub = @o AND name = @n ORDER BY part_number LIMIT 5000", conn))
        {
            cmd.Parameters.AddWithValue("o", subject.Id); cmd.Parameters.AddWithValue("n", name);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) keys.Add(r.GetString(0));
        }
        if (!byKind.TryGetValue("part", out var t)) return [];
        var readable = await t.ReadableAsync(subject, keys, ct);
        return [.. keys.Where(readable.Contains)];
    }

    public async Task<Outcome<MassPreview>> UndoAsync(Subject subject, string number, string? requestCorrelationId, CancellationToken ct = default)
    {
        var (h, items) = await LoadAsync(number, ct);
        if (h is null || h.Sub != subject.Id) return Outcome<MassPreview>.NotFound();
        if (h.Status != "applied") return Outcome<MassPreview>.Conflict(h.Status == "undone" ? "This operation was already undone." : "Only a fully applied operation can be undone. Apply it first.");
        if (!IsReversible(h.Operation)) return Outcome<MassPreview>.Conflict("This operation cannot be undone.");
        if (!byKind.TryGetValue(h.Kind, out var target)) return Outcome<MassPreview>.NotFound();
        var corr = h.Correlation;
        foreach (var (seq, item, undo, _) in items.Where(i => i.Item.Result is "applied" or "undo_failed" && i.Undo is not null))
        {
            string result, detail;
            if (h.Operation == MassOperations.BaselineProposal)
            {
                await using var c0 = await dataSource.OpenConnectionAsync(ct);
                await using var del = new NpgsqlCommand("DELETE FROM rpt.baseline_proposal WHERE owner_sub = @o AND name = @n AND part_number = @p", c0);
                del.Parameters.AddWithValue("o", subject.Id); del.Parameters.AddWithValue("n", undo!["name"]!.GetValue<string>()); del.Parameters.AddWithValue("p", item.Key);
                await del.ExecuteNonQueryAsync(ct);
                result = "undone"; detail = "Removed from the proposal.";
            }
            else
            {
                var r = await target.UndoAsync(subject, h.Operation, item.Key, undo!, corr, ct);
                if (r.IsOk) { result = "undone"; detail = r.Value ?? "Restored."; }
                else { result = "undo_failed"; detail = string.Join(" ", r.Errors); }
            }
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var upd = new NpgsqlCommand("UPDATE rpt.mass_change_item SET result = @r, detail = @d WHERE change_id = @c AND seq = @s", conn);
            upd.Parameters.AddWithValue("r", result); upd.Parameters.AddWithValue("d", detail); upd.Parameters.AddWithValue("c", h.Id); upd.Parameters.AddWithValue("s", seq);
            await upd.ExecuteNonQueryAsync(ct);
        }
        var (_, after) = await LoadAsync(number, ct);
        var failed = after.Count(i => i.Item.Result == "undo_failed");
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            if (failed == 0)
            {
                await using var upd = new NpgsqlCommand("UPDATE rpt.mass_change SET status = 'undone', undone_at = @at WHERE id = @id", conn, tx);
                upd.Parameters.AddWithValue("at", clock.GetUtcNow()); upd.Parameters.AddWithValue("id", h.Id);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await audit.AppendAsync(tx, ReportsAudit.Entry(subject, "output.mass.undo", failed == 0 ? AuditOutcome.Success : AuditOutcome.Failure, "output", number, corr,
                ("operation", h.Operation), ("undone", after.Count(i => i.Item.Result == "undone").ToString(CultureInfo.InvariantCulture)), ("undo_failed", failed.ToString(CultureInfo.InvariantCulture))), ct);
            await tx.CommitAsync(ct);
        }
        return await GetAsync(subject, number, ct);
    }
}

/// <summary>Builds the parameter objects of mass operations, so pages and API clients share one shape.</summary>
public static class MassParameters
{
    public static JsonObject MarkingsJson(Markings m) => new()
    {
        ["jurisdiction"] = m.ExportJurisdiction.ToString(), ["eccn"] = m.Eccn, ["usmlCategory"] = m.UsmlCategory, ["cuiCategories"] = new JsonArray([.. m.CuiCategories.Select(c => (JsonNode?)c)]),
        ["distributionStatement"] = m.DistributionStatement.ToString(), ["controllingOffice"] = m.ControllingOffice, ["distributionReason"] = m.DistributionReason, ["program"] = m.Program,
        ["owningOrg"] = m.OwningOrg, ["exportAuthorizations"] = new JsonArray([.. m.ExportAuthorizations.Select(c => (JsonNode?)c)]),
    };
}
