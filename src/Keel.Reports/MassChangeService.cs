using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Core.Reports;
using Npgsql;

namespace Keel.Reports;

public sealed record MassItem(string Key, string? Name, string? Banner, string? State, bool CanApply, string? Reason, string Result, string? Detail, bool Reversible);

public sealed record MassPreview(
    string Number, string Operation, string ObjectKind, string Status, IReadOnlyList<MassItem> Items, int WillChange, int Refused, int Applied, int Failed, int Undone, DateTimeOffset CreatedAt, string CorrelationId)
{
    public bool CanApply => Status == "previewed" && Items.Any(i => i.CanApply && i.Result is "pending" or "failed");
    public bool CanUndo => Status == "applied" && Items.Any(i => i.Result == "applied" && i.Reversible);
}

public sealed record MassRequest(string ObjectKind, IReadOnlyList<string> Keys, string Operation, JsonObject? Parameters);

/// <summary>
/// Mass change (ADR-0044). Preview decides, per object and without changing anything, what would happen and why not (access, release lock, state); apply runs object by object through the
/// owning services (each writes its own audit event, all with the same correlation id), reports partial failure, can be repeated (finished objects are skipped), and records what is
/// needed to undo the reversible operations. Hard caps: <see cref="ReportsLimits.MaxMassObjects"/> objects per operation, <see cref="ReportsLimits.MaxMassPerHour"/> previews per user per hour.
/// </summary>
public sealed class MassChangeService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, IEnumerable<IMassChangeTarget> targets)
{
    private static readonly string[] Operations = [MassOperations.SetAttribute, MassOperations.ChangeMarkings, MassOperations.BaselineProposal, MassOperations.SubmitReview, MassOperations.Watch, MassOperations.Favorite];
    private static readonly string[] Reversible = [MassOperations.SetAttribute, MassOperations.ChangeMarkings, MassOperations.BaselineProposal, MassOperations.Watch, MassOperations.Favorite];

    private readonly Dictionary<string, IMassChangeTarget> byKind = targets.ToDictionary(t => t.Kind, StringComparer.Ordinal);

    public IReadOnlyList<string> KindsFor(string operation) => [.. byKind.Values.Where(t => t.Operations.Contains(operation) || operation == MassOperations.BaselineProposal).Select(t => t.Kind)];
    public static IReadOnlyList<string> AllOperations => Operations;
    public static bool IsReversible(string operation) => Reversible.Contains(operation);

    public async Task<Outcome<MassPreview>> PreviewAsync(Subject subject, MassRequest request, string? correlationId, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (!Operations.Contains(request.Operation)) errors.Add("Choose one of the operations offered.");
        if (!byKind.TryGetValue(request.ObjectKind ?? "", out var target)) errors.Add("That kind of object cannot be changed in bulk yet.");
        var keys = (request.Keys ?? []).Select(k => (k ?? "").Trim()).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0) errors.Add("Select at least one object.");
        if (keys.Count > ReportsLimits.MaxMassObjects) errors.Add($"At most {ReportsLimits.MaxMassObjects} objects can be changed in one operation; you selected {keys.Count}.");
        var parameters = request.Parameters ?? [];
        errors.AddRange(ValidateParameters(request.Operation, parameters));
        if (errors.Count > 0 || target is null) return Outcome<MassPreview>.Invalid(errors);
        if (request.Operation != MassOperations.BaselineProposal && !target.Operations.Contains(request.Operation)) return Outcome<MassPreview>.Invalid(["That operation is not available for this kind of object."]);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var rate = new NpgsqlCommand("SELECT count(*) FROM rpt.mass_change WHERE created_by_sub = @s AND created_at > @since", conn))
        {
            rate.Parameters.AddWithValue("s", subject.Id);
            rate.Parameters.AddWithValue("since", clock.GetUtcNow().AddHours(-1));
            if ((long)(await rate.ExecuteScalarAsync(ct))! >= ReportsLimits.MaxMassPerHour)
                return Outcome<MassPreview>.Conflict($"You have started {ReportsLimits.MaxMassPerHour} bulk operations in the last hour. Try again later.");
        }
        var verdicts = await target.PreviewAsync(subject, request.Operation == MassOperations.BaselineProposal ? MassOperations.CheckRead : request.Operation, parameters, keys, ct);
        var id = Guid.CreateVersion7();
        var corr = Guid.NewGuid().ToString("N");
        var now = clock.GetUtcNow();
        await using var tx = await conn.BeginTransactionAsync(ct);
        long seq;
        await using (var s = new NpgsqlCommand("SELECT nextval('rpt.mass_number_seq')", conn, tx)) seq = (long)(await s.ExecuteScalarAsync(ct))!;
        var number = "MCH-" + seq.ToString("D6", CultureInfo.InvariantCulture);
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO rpt.mass_change (id, number, correlation_id, operation, object_kind, parameters, status, created_at, created_by, created_by_sub) VALUES (@id, @n, @c, @op, @k, @p::jsonb, 'previewed', @at, @by, @sub)", conn, tx))
        {
            ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("n", number); ins.Parameters.AddWithValue("c", corr);
            ins.Parameters.AddWithValue("op", request.Operation); ins.Parameters.AddWithValue("k", request.ObjectKind!);
            ins.Parameters.AddWithValue("p", parameters.ToJsonString()); ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", subject.Username ?? subject.DisplayName); ins.Parameters.AddWithValue("sub", subject.Id);
            await ins.ExecuteNonQueryAsync(ct);
        }
        var items = new List<MassItem>();
        var seqNo = 0;
        foreach (var key in keys)
        {
            var v = verdicts.TryGetValue(key, out var found) ? found : new MassVerdict(false, "Not found, or you may not read it.");
            if (key.Length > 64) v = new MassVerdict(false, "Not found, or you may not read it.");
            await using var item = new NpgsqlCommand("INSERT INTO rpt.mass_change_item (change_id, seq, object_key, verdict, reason) VALUES (@c, @s, @k, @v, @r)", conn, tx);
            item.Parameters.AddWithValue("c", id); item.Parameters.AddWithValue("s", seqNo++); item.Parameters.AddWithValue("k", key.Length > 64 ? key[..64] : key);
            item.Parameters.AddWithValue("v", v.CanApply ? "will_change" : "refused"); item.Parameters.AddWithValue("r", (object?)v.Reason ?? DBNull.Value);
            await item.ExecuteNonQueryAsync(ct);
            items.Add(new MassItem(key, v.Name, v.Banner, v.State, v.CanApply, v.Reason, "pending", null, IsReversible(request.Operation)));
        }
        await audit.AppendAsync(tx, ReportsAudit.Entry(subject, "output.mass.preview", AuditOutcome.Success, "output", number, corr,
            ("operation", request.Operation), ("kind", request.ObjectKind), ("selected", keys.Count.ToString(CultureInfo.InvariantCulture)),
            ("will_change", items.Count(i => i.CanApply).ToString(CultureInfo.InvariantCulture)), ("refused", items.Count(i => !i.CanApply).ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return Outcome<MassPreview>.Ok(Build(number, request.Operation, request.ObjectKind!, "previewed", items, now, corr));
    }

    private static IEnumerable<string> ValidateParameters(string op, JsonObject p)
    {
        string? S(string k) => p[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        switch (op)
        {
            case MassOperations.SetAttribute:
                if (string.IsNullOrWhiteSpace(S("name")) || S("name")!.Length > 60) yield return "Name the attribute to change.";
                if (S("value") is null || S("value")!.Length > 500) yield return "Give the new value (at most 500 characters).";
                break;
            case MassOperations.ChangeMarkings:
                if (string.IsNullOrWhiteSpace(S("reason"))) yield return "A reason is required to change markings.";
                if (p["markings"] is not JsonObject) yield return "Give the new markings.";
                break;
            case MassOperations.BaselineProposal:
                if (string.IsNullOrWhiteSpace(S("name")) || S("name")!.Length > 120) yield return "Name the baseline proposal (at most 120 characters).";
                break;
            case MassOperations.Watch:
                if (S("level") is not (null or "all" or "decisions")) yield return "Watch level must be all or decisions.";
                break;
            case MassOperations.SubmitReview:
                if (p["participants"] is not JsonObject) yield return "Name the reviewers for each review slot.";
                break;
        }
    }

    private static MassPreview Build(string number, string op, string kind, string status, IReadOnlyList<MassItem> items, DateTimeOffset at, string corr) =>
        new(number, op, kind, status, items, items.Count(i => i.CanApply), items.Count(i => !i.CanApply), items.Count(i => i.Result == "applied"), items.Count(i => i.Result is "failed" or "undo_failed"),
            items.Count(i => i.Result == "undone"), at, corr);

    private sealed record Header(Guid Id, string Number, string Correlation, string Operation, string Kind, JsonObject Parameters, string Status, DateTimeOffset At, string Sub);

    private async Task<(Header? H, List<(int Seq, MassItem Item, JsonObject? Undo)> Items)> LoadAsync(string number, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        Header? h = null;
        await using (var cmd = new NpgsqlCommand("SELECT id, number, correlation_id, operation, object_kind, parameters::text, status, created_at, created_by_sub FROM rpt.mass_change WHERE number = @n", conn))
        {
            cmd.Parameters.AddWithValue("n", number ?? "");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) h = new Header(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), JsonNode.Parse(r.GetString(5)) as JsonObject ?? [], r.GetString(6), r.GetFieldValue<DateTimeOffset>(7), r.GetString(8));
        }
        var items = new List<(int, MassItem, JsonObject?)>();
        if (h is null) return (null, items);
        await using var cmd2 = new NpgsqlCommand("SELECT seq, object_key, verdict, reason, result, detail, undo::text FROM rpt.mass_change_item WHERE change_id = @c ORDER BY seq", conn);
        cmd2.Parameters.AddWithValue("c", h.Id);
        await using var r2 = await cmd2.ExecuteReaderAsync(ct);
        while (await r2.ReadAsync(ct))
            items.Add((r2.GetInt32(0), new MassItem(r2.GetString(1), null, null, null, r2.GetString(2) == "will_change", r2.IsDBNull(3) ? null : r2.GetString(3), r2.GetString(4), r2.IsDBNull(5) ? null : r2.GetString(5),
                IsReversible(h.Operation) && !r2.IsDBNull(6)), r2.IsDBNull(6) ? null : JsonNode.Parse(r2.GetString(6)) as JsonObject));
        return (h, items);
    }

    public async Task<Outcome<MassPreview>> GetAsync(Subject subject, string number, CancellationToken ct = default)
    {
        var (h, items) = await LoadAsync(number, ct);
        if (h is null || h.Sub != subject.Id) return Outcome<MassPreview>.NotFound();
        // Objects are re-authorized when shown: one the owner can no longer read appears as a restricted row.
        var readable = byKind.TryGetValue(h.Kind, out var t) ? await t.ReadableAsync(subject, [.. items.Select(i => i.Item.Key)], ct) : new HashSet<string>();
        var shown = items.Select(i => readable.Contains(i.Item.Key) ? i.Item : i.Item with { Key = "(restricted)", Detail = null, Reason = i.Item.Reason is null ? null : "Restricted." }).ToList();
        return Outcome<MassPreview>.Ok(Build(h.Number, h.Operation, h.Kind, h.Status, shown, h.At, h.Correlation));
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
        foreach (var (seq, item, _) in todo)
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
                var applied = await target.ApplyAsync(subject, h.Operation, h.Parameters, item.Key, h.Correlation, ct);
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
                ("operation", h.Operation), ("applied", after.Count(i => i.Item.Result == "applied").ToString(CultureInfo.InvariantCulture)),
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
        foreach (var (seq, item, undo) in items.Where(i => i.Item.Result is "applied" or "undo_failed" && i.Undo is not null))
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
