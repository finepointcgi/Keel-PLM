using System.Globalization;
using System.Text;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Programs;

public sealed partial class ProgramService
{
    private async Task<string?> ProgramCodeOfGateAsync(string gateNumber, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = ProgramStore.Cmd(conn, null, "SELECT p.code FROM prg.item i JOIN prg.program p ON p.id = i.program_id WHERE i.gate_number = @g");
        cmd.Parameters.AddWithValue("g", gateNumber?.Trim() ?? "");
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    internal async Task<GateDetail?> LoadGateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, PHead head, string gateNumber, CancellationToken ct)
    {
        var item = await ProgramStore.LoadItemByGateAsync(conn, tx, gateNumber, false, ct);
        if (item is null || item.ProgramId != head.Id || !AccessPolicy.Evaluate(subject, item.Markings, PlmAction.Read).Permitted) return null;
        string desc, state, status;
        string? ownerName;
        DateOnly? actualEnd;
        await using (var c = ProgramStore.Cmd(conn, tx, "SELECT description, gate_state, status, owner_name, actual_end FROM prg.item WHERE id = @id"))
        {
            c.Parameters.AddWithValue("id", item.Id);
            await using var r = await c.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            (desc, state, status, ownerName, actualEnd) = (r.GetString(0), r.GetString(1), r.GetString(2), ProgramStore.Str(r, 3), ProgramStore.DateAt(r, 4));
        }
        var checklist = new List<ChecklistRow>();
        await using (var c = ProgramStore.Cmd(conn, tx, "SELECT id, seq, text, required, status, note, checked_by, checked_at FROM prg.checklist WHERE item_id = @id ORDER BY seq"))
        {
            c.Parameters.AddWithValue("id", item.Id);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                checklist.Add(new ChecklistRow(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetBoolean(3), r.GetString(4), r.GetString(5), ProgramStore.Str(r, 6), r.IsDBNull(7) ? null : ProgramStore.Utc(r, 7)));
        }
        var deliverables = await LoadDeliverablesAsync(conn, tx, subject, head, item.Id, ct);
        var canModify = AccessPolicy.Evaluate(subject, item.Markings, PlmAction.Modify).Permitted;
        var complete = checklist.Count > 0 && checklist.All(x => !x.Required || x.Status != "Open");
        return new GateDetail(gateNumber, head.Code, head.Name, item.Key, item.Title, desc, item.GateType!, state, item.PlannedEnd, actualEnd, status, ownerName, checklist, deliverables.Items,
            item.Submission, canModify, state == GateStates.Draft && canModify, complete, item.Markings, Markings.AggregateBanner([item.Markings, head.Markings, .. deliverables.Items.Select(d => d.Markings)]));
    }

    public async Task<Outcome<GateDetail>> GetGateAsync(Subject subject, string gateNumber, string? corr, CancellationToken ct = default)
    {
        var code = await ProgramCodeOfGateAsync(gateNumber, ct);
        if (code is null) return Outcome<GateDetail>.NotFound();
        try
        {
            return await ReadAsync(subject, code, "program.gate_read", corr, async (conn, tx, head) => await LoadGateAsync(conn, tx, subject, head, gateNumber.Trim(), ct) ?? throw new HiddenGateException(),
                g => [("gate", g.Number)], ct);
        }
        catch (HiddenGateException) { return Outcome<GateDetail>.NotFound(); }
    }

    private sealed class HiddenGateException : Exception;

    private async Task<Outcome<GateDetail>> MutateGateAsync(Subject subject, string gateNumber, string action, string? corr,
        Func<PHead, IHead, NpgsqlConnection, NpgsqlTransaction, DateTimeOffset, Task<PStep>> body, CancellationToken ct)
    {
        var code = await ProgramCodeOfGateAsync(gateNumber, ct);
        if (code is null) return Outcome<GateDetail>.NotFound();
        return await MutateAsync<GateDetail>(subject, code, action, corr, PlmAction.Read, false, async (head, conn, tx, now) =>
        {
            var item = await ProgramStore.LoadItemByGateAsync(conn, tx, gateNumber.Trim(), true, ct);
            if (item is null || item.ProgramId != head.Id) return PStep.Missing();
            if (RequireItem(subject, item, PlmAction.Modify) is { } denied) return denied;
            if (item.GateState != GateStates.Draft) return PStep.Fail($"Gate review {item.GateNumber} is {GateStates.Label(item.GateState!).ToLowerInvariant()}; its checklist is frozen.");
            return await body(head, item, conn, tx, now);
        }, async (conn, tx, head) => await LoadGateAsync(conn, tx, subject, head, gateNumber.Trim(), ct), ct);
    }

    public Task<Outcome<GateDetail>> AddChecklistItemAsync(Subject subject, string gateNumber, string text, bool required, string? corr, CancellationToken ct = default) =>
        MutateGateAsync(subject, gateNumber, "program.gate_checklist_add", corr, async (head, item, conn, tx, now) =>
        {
            var t = text?.Trim() ?? "";
            if (t.Length is 0 or > 500) return PStep.Invalid("The criterion is required (at most 500 characters).");
            await using var c = ProgramStore.Cmd(conn, tx, "INSERT INTO prg.checklist (id, item_id, seq, text, required) SELECT @id, @item, COALESCE(max(seq), 0) + 1, @text, @req FROM prg.checklist WHERE item_id = @item");
            c.Parameters.AddWithValue("id", Guid.CreateVersion7());
            c.Parameters.AddWithValue("item", item.Id);
            c.Parameters.AddWithValue("text", t);
            c.Parameters.AddWithValue("req", required);
            await c.ExecuteNonQueryAsync(ct);
            return PStep.Done(("gate", item.GateNumber), ("required", required ? "yes" : "no"));
        }, ct);

    public Task<Outcome<GateDetail>> SetChecklistStatusAsync(Subject subject, string gateNumber, Guid checklistId, string status, string? note, string? corr, CancellationToken ct = default) =>
        MutateGateAsync(subject, gateNumber, "program.gate_check", corr, async (head, item, conn, tx, now) =>
        {
            if (status is not ("Open" or "Met" or "NotApplicable")) return PStep.Invalid("Status must be Open, Met or NotApplicable.");
            if ((note?.Length ?? 0) > 1000) return PStep.Invalid("The note must be at most 1000 characters.");
            await using var c = ProgramStore.Cmd(conn, tx, "UPDATE prg.checklist SET status = @s, note = @n, checked_by = @by, checked_at = @at WHERE id = @id AND item_id = @item");
            c.Parameters.AddWithValue("s", status);
            c.Parameters.AddWithValue("n", note?.Trim() ?? "");
            c.Parameters.AddWithValue("by", status == "Open" ? DBNull.Value : ProgramStore.Who(subject));
            c.Parameters.AddWithValue("at", status == "Open" ? DBNull.Value : now);
            c.Parameters.AddWithValue("id", checklistId);
            c.Parameters.AddWithValue("item", item.Id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return PStep.Missing();
            return PStep.Done(("gate", item.GateNumber), ("status", status));
        }, ct);

    public Task<Outcome<GateDetail>> RemoveChecklistItemAsync(Subject subject, string gateNumber, Guid checklistId, string? corr, CancellationToken ct = default) =>
        MutateGateAsync(subject, gateNumber, "program.gate_checklist_remove", corr, async (head, item, conn, tx, now) =>
        {
            await using var c = ProgramStore.Cmd(conn, tx, "DELETE FROM prg.checklist WHERE id = @id AND item_id = @item");
            c.Parameters.AddWithValue("id", checklistId);
            c.Parameters.AddWithValue("item", item.Id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return PStep.Missing();
            return PStep.Done(("gate", item.GateNumber));
        }, ct);

    /// <summary>
    /// The readable text that a gate review's signatures cover: the gate, its checklist and the readiness of its deliverables as the submitter saw them. Stored with the
    /// submission and hashed into every signature (ADR-0043).
    /// </summary>
    internal async Task<string> BuildGateSnapshotAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject actor, PHead program, IHead item, int submission, CancellationToken ct)
    {
        var detail = (await LoadGateAsync(conn, tx, actor, program, item.GateNumber!, ct))!;
        var sb = new StringBuilder();
        sb.Append(item.GateNumber).Append(" submission ").Append(submission.ToString(CultureInfo.InvariantCulture)).AppendLine();
        sb.Append("Markings: ").AppendLine(item.Markings.BannerText());
        sb.Append("Program: ").Append(program.Code).Append(' ').AppendLine(program.Name);
        sb.Append("Gate: ").Append(detail.GateType).Append(" (").Append(detail.TypeLabel).Append(") ").AppendLine(detail.Title);
        sb.Append("Planned date: ").AppendLine(ScheduleMath.Fmt(detail.PlannedDate));
        sb.Append("Description: ").AppendLine(detail.Description);
        sb.AppendLine("Checklist:");
        foreach (var c in detail.Checklist)
            sb.Append("  ").Append(c.Seq.ToString(CultureInfo.InvariantCulture)).Append(". [").Append(c.Status).Append(c.Required ? ", required" : ", optional").Append("] ").Append(c.Text).AppendLine(c.Note.Length == 0 ? "" : " Note: " + c.Note);
        sb.AppendLine("Deliverables:");
        foreach (var d in detail.Deliverables)
            sb.Append("  ").Append(d.Number).Append(' ').Append(d.Title).Append(" -> ").Append(d.ObjectKind).Append(' ').Append(d.ObjectKey).Append(": ").Append(d.ReadinessLabel).Append(", due ").AppendLine(ScheduleMath.Fmt(d.Due));
        return sb.ToString().TrimEnd();
    }
}
