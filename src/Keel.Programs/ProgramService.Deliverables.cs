using System.Globalization;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Programs;

public sealed partial class ProgramService
{
    /// <summary>The kinds of Keel object a deliverable may point at.</summary>
    public static readonly string[] DeliverableKinds =
        [AwarenessKinds.Part, AwarenessKinds.Document, AwarenessKinds.Change, AwarenessKinds.Baseline, AwarenessKinds.ReqSpec, AwarenessKinds.Ncr, AwarenessKinds.Capa, AwarenessKinds.Deviation, AwarenessKinds.Mbom];

    /// <summary>
    /// What a deliverable's target looks like to this viewer: the owning module's key lookup (<see cref="IObjectAccess"/>) read through the access policy. A target the viewer
    /// may not read, or that does not exist, is "not visible" and reveals nothing else; no marked data of the target is ever stored in a deliverable.
    /// </summary>
    internal async Task<(string Readiness, string? Name, string? State, Markings? Markings)> ResolveTargetAsync(Subject subject, string kind, string key, CancellationToken ct)
    {
        if (!_objects.TryGetValue(kind, out var access)) return (Readiness.NotVisible, null, null, null);
        var info = await access.FindAsync(key, ct);
        if (info is null || !AccessPolicy.Evaluate(subject, info.Markings, PlmAction.Read).Permitted) return (Readiness.NotVisible, null, null, null);
        return (Readiness.Of(info.StateLabel), info.Name, info.StateLabel, info.Markings);
    }

    internal async Task<DeliverableList> LoadDeliverablesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, PHead head, Guid? itemId, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "d");
        var raw = new List<(Guid Id, string Number, string Title, string Kind, string Key, DateOnly Due, Guid? ItemId, string? OwnerName, string? OwnerLogin, Markings M)>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT d.id, d.number, d.title, d.object_kind, d.object_key, d.due_date, d.item_id, d.owner_name, d.owner_username, {MarkingColumns.Select("d")}
            FROM prg.deliverable d
            WHERE d.program_id = @p AND d.removed_at IS NULL AND {filter.Sql} AND (@item::uuid IS NULL OR d.item_id = @item)
            ORDER BY d.due_date, d.number
            """, conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("p", head.Id);
            cmd.Parameters.AddWithValue("item", (object?)itemId ?? DBNull.Value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                raw.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), ProgramStore.Date(r, 5), r.IsDBNull(6) ? null : r.GetGuid(6), ProgramStore.Str(r, 7), ProgramStore.Str(r, 8), MarkingColumns.Read(r, 9)));
        }
        var itemKeys = new Dictionary<Guid, string?>();
        foreach (var id in raw.Where(x => x.ItemId is not null).Select(x => x.ItemId!.Value).Distinct())
        {
            var ih = await ProgramStore.LoadItemByIdAsync(conn, tx, id, false, ct);
            itemKeys[id] = ih is not null && AccessPolicy.Evaluate(subject, ih.Markings, PlmAction.Read).Permitted ? ih.Key : null;
        }
        var today = Today;
        var rows = new List<DeliverableRow>();
        var banners = new List<Markings> { head.Markings };
        foreach (var x in raw)
        {
            var (readiness, name, state, tm) = await ResolveTargetAsync(subject, x.Kind, x.Key, ct);
            if (tm is not null) banners.Add(tm);
            banners.Add(x.M);
            rows.Add(new DeliverableRow(x.Id, x.Number, x.Title, x.Kind, x.Key, name, state, readiness, x.Due, x.Due < today && readiness != Readiness.Ready,
                x.ItemId is { } iid ? itemKeys[iid] : null, x.OwnerName, x.OwnerLogin, AccessPolicy.Evaluate(subject, x.M, PlmAction.Modify).Permitted, x.M, tm));
        }
        return new DeliverableList(rows, Markings.AggregateBanner(banners));
    }

    public Task<Outcome<DeliverableList>> ListDeliverablesAsync(Subject subject, string code, string? corr, CancellationToken ct = default) =>
        ReadAsync(subject, code, "program.deliverables", corr, (conn, tx, head) => LoadDeliverablesAsync(conn, tx, subject, head, null, ct), l => [("count", ProgramStore.Int(l.Items.Count))], ct);

    public Task<Outcome<DeliverableRow>> AddDeliverableAsync(Subject subject, string code, AddDeliverableCommand cmd, string? corr, CancellationToken ct = default)
    {
        string? number = null;
        return MutateAsync<DeliverableRow>(subject, code, "program.deliverable_add", corr, PlmAction.Modify, false, async (head, conn, tx, now) =>
        {
            var title = cmd.Title?.Trim() ?? "";
            if (title.Length is 0 or > 200) return PStep.Invalid("Title is required (at most 200 characters).");
            if (!DeliverableKinds.Contains(cmd.ObjectKind)) return PStep.Invalid($"The object kind must be one of: {string.Join(", ", DeliverableKinds)}.");
            var key = cmd.ObjectKey?.Trim() ?? "";
            if (key.Length is 0 or > 64) return PStep.Invalid("The number of the object is required.");
            var target = await ResolveTargetAsync(subject, cmd.ObjectKind, key, ct);
            if (target.Readiness == Readiness.NotVisible) return PStep.Invalid($"{AwarenessKinds.Label(cmd.ObjectKind)} {key} was not found.");
            var markings = cmd.Markings ?? head.Markings;
            var gaps = MarkingDominance.Gaps(markings, head.Markings);
            if (gaps.Count > 0) return PStep.Invalid($"The markings of a deliverable must cover those of the program. {MarkingDominance.Describe(gaps)}");
            var modify = AccessPolicy.Evaluate(subject, markings, PlmAction.Modify);
            if (!modify.Permitted) return PStep.Forbid([.. modify.Reasons]);
            Guid? itemId = null;
            string? itemKey = null;
            if (!string.IsNullOrWhiteSpace(cmd.ItemKey))
            {
                var item = await ProgramStore.LoadItemAsync(conn, tx, head.Id, cmd.ItemKey.Trim(), false, ct);
                if (RequireItem(subject, item, PlmAction.Read) is not null) return PStep.Invalid($"Item '{cmd.ItemKey}' was not found.");
                itemId = item!.Id;
                itemKey = item.Key;
            }
            var (owner, ownerError) = await ResolveOwnerAsync(conn, tx, cmd.OwnerLogin, markings, ct);
            if (ownerError is not null) return PStep.Invalid(ownerError);
            await using (var dup = ProgramStore.Cmd(conn, tx, "SELECT 1 FROM prg.deliverable WHERE program_id = @p AND object_kind = @k AND object_key = @key AND removed_at IS NULL AND item_id IS NOT DISTINCT FROM @item"))
            {
                dup.Parameters.AddWithValue("p", head.Id);
                dup.Parameters.AddWithValue("k", cmd.ObjectKind);
                dup.Parameters.AddWithValue("key", key);
                dup.Parameters.AddWithValue("item", (object?)itemId ?? DBNull.Value);
                if (await dup.ExecuteScalarAsync(ct) is not null) return PStep.Fail("That object is already a deliverable here.");
            }
            long no;
            await using (var n = ProgramStore.Cmd(conn, tx, "UPDATE prg.program SET next_deliverable_no = next_deliverable_no + 1 WHERE id = @id RETURNING next_deliverable_no - 1"))
            {
                n.Parameters.AddWithValue("id", head.Id);
                no = Convert.ToInt64(await n.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }
            number = $"D-{no.ToString("D3", CultureInfo.InvariantCulture)}";
            await using var ins = ProgramStore.Cmd(conn, tx,
                $"""
                INSERT INTO prg.deliverable (id, program_id, number, item_id, title, object_kind, object_key, due_date, owner_sub, owner_username, owner_name, {MarkingColumns.InsertColumns}, created_at, created_by)
                VALUES (@id, @p, @num, @item, @title, @kind, @key, @due, @osub, @ouser, @oname, {MarkingColumns.InsertValues}, @at, @by)
                """);
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("p", head.Id);
            ins.Parameters.AddWithValue("num", number);
            ins.Parameters.AddWithValue("item", (object?)itemId ?? DBNull.Value);
            ins.Parameters.AddWithValue("title", title);
            ins.Parameters.AddWithValue("kind", cmd.ObjectKind);
            ins.Parameters.AddWithValue("key", key);
            ins.Parameters.AddWithValue("due", cmd.Due.ToDateTime(TimeOnly.MinValue));
            ins.Parameters.AddWithValue("osub", (object?)owner?.Sub ?? DBNull.Value);
            ins.Parameters.AddWithValue("ouser", (object?)owner?.Username ?? DBNull.Value);
            ins.Parameters.AddWithValue("oname", (object?)owner?.DisplayName ?? DBNull.Value);
            foreach (var (k, v) in MarkingColumns.Parameters(markings)) ins.Parameters.AddWithValue(k, v);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", ProgramStore.Who(subject));
            await ins.ExecuteNonQueryAsync(ct);
            return PStep.Done(("number", number), ("target", $"{cmd.ObjectKind}:{key}"), ("item", itemKey));
        }, async (conn, tx, head) => (await LoadDeliverablesAsync(conn, tx, subject, head, null, ct)).Items.FirstOrDefault(d => d.Number == number), ct);
    }

    public Task<Outcome<DeliverableList>> RemoveDeliverableAsync(Subject subject, string code, string deliverableNumber, string? corr, CancellationToken ct = default) =>
        MutateAsync<DeliverableList>(subject, code, "program.deliverable_remove", corr, PlmAction.Read, false, async (head, conn, tx, now) =>
        {
            var visible = (await LoadDeliverablesAsync(conn, tx, subject, head, null, ct)).Items.FirstOrDefault(d => string.Equals(d.Number, deliverableNumber, StringComparison.OrdinalIgnoreCase));
            if (visible is null) return PStep.Missing();
            if (!visible.CanModify) return PStep.Forbid([.. AccessPolicy.Evaluate(subject, visible.Markings, PlmAction.Modify).Reasons]);
            await using var up = ProgramStore.Cmd(conn, tx, "UPDATE prg.deliverable SET removed_at = @at, removed_by = @by WHERE id = @id");
            up.Parameters.AddWithValue("at", now);
            up.Parameters.AddWithValue("by", ProgramStore.Who(subject));
            up.Parameters.AddWithValue("id", visible.Id);
            await up.ExecuteNonQueryAsync(ct);
            return PStep.Done(("number", visible.Number));
        }, async (conn, tx, head) => await LoadDeliverablesAsync(conn, tx, subject, head, null, ct), ct);
}
