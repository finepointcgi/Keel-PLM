using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

public sealed partial class MbomService
{
    // ---------------------------------------------------------------- reading lines

    /// <summary>The lines of a checked-in iteration (default: the latest of the named or latest revision). Parts the caller may not read stay placeholders.</summary>
    public Task<Outcome<MbomLines>> GetLinesAsync(Subject subject, string number, string? revision, int? iteration, string? correlationId, CancellationToken ct = default) =>
        ReadAsync<MbomLines>(subject, number, revision, "mbom.lines", correlationId, async (conn, tx, head) =>
        {
            var iterationId = head.IterationId;
            var label = head.LatestIteration;
            if (iteration is { } it && it != head.LatestIteration)
            {
                await using var cmd = new NpgsqlCommand("SELECT i.id FROM mbom.iteration i WHERE i.revision_id = @r AND i.iteration = @it", conn, tx);
                cmd.Parameters.AddWithValue("r", head.RevisionId);
                cmd.Parameters.AddWithValue("it", it);
                if (await cmd.ExecuteScalarAsync(ct) is not Guid found) return (Outcome<MbomLines>.NotFound(), []);
                (iterationId, label) = (found, it);
            }
            var cache = new MarkingCache();
            var lines = await LoadLineViewsAsync(conn, tx, subject, false, iterationId, null, cache, ct);
            string? derived = null;
            await using (var cmd = new NpgsqlCommand("SELECT derived_part_iteration_id FROM mbom.iteration WHERE id = @i", conn, tx))
            {
                cmd.Parameters.AddWithValue("i", iterationId);
                if (await cmd.ExecuteScalarAsync(ct) is Guid d) derived = await MbomStore.IterationLabelAsync(conn, tx, d, ct);
            }
            var banner = Markings.AggregateBanner(cache.Distinct.Append(head.Markings));
            return (Outcome<MbomLines>.Ok(new MbomLines(head.Number, head.Revision, label, false, derived, lines, banner, MbomLimits.MaxLines)),
                [("revision", $"{head.Revision}.{Fmt(label)}"), ("lines", Fmt(lines.Count)), ("redacted", Fmt(lines.Count(l => l.Redacted))), ("banner", banner)]);
        }, ct);

    /// <summary>The caller's private working copy.</summary>
    public async Task<Outcome<MbomLines>> GetWorkingAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await MbomStore.LoadByNumberAsync(conn, tx, number, null, false, ct);
        if (head is null) return Outcome<MbomLines>.NotFound();
        if (!CanRead(subject, head).Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "mbom.working_read", AuditOutcome.Denied, number, correlationId, ("reasons", "read")), ct);
            await tx.CommitAsync(ct);
            return Outcome<MbomLines>.NotFound();
        }
        if (head.CheckoutUser is null || head.CheckoutUser != subject.Id)
        {
            await tx.RollbackAsync(ct);
            return Outcome<MbomLines>.Conflict("The working copy is private to the user who checked the MBOM out.");
        }
        var cache = new MarkingCache();
        var lines = await LoadLineViewsAsync(conn, tx, subject, true, head.RevisionId, null, cache, ct);
        var co = await LoadCheckoutAsync(conn, tx, head.RevisionId, ct);
        var derived = co.DerivedFrom is { } d ? await MbomStore.IterationLabelAsync(conn, tx, d, ct) : null;
        var banner = Markings.AggregateBanner(cache.Distinct.Append(head.Markings));
        await audit.AppendAsync(tx, Entry(subject, "mbom.working_read", AuditOutcome.Success, number, correlationId, ("lines", Fmt(lines.Count)), ("banner", banner)), ct);
        await tx.CommitAsync(ct);
        return Outcome<MbomLines>.Ok(new MbomLines(number, head.Revision, head.LatestIteration + 1, true, derived, lines, banner, MbomLimits.MaxLines));
    }

    internal static async Task<List<MbomLineView>> LoadLineViewsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, bool working, Guid id, Guid? only, MarkingCache cache, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "cm");
        var table = working ? "mbom.working_usage" : "mbom.usage";
        var key = working ? "u.checkout_revision_id" : "u.iteration_id";
        var altExpr = working ? "(row_number() OVER (PARTITION BY u.find_number ORDER BY u.eff_from NULLS FIRST, u.created_at, u.id) - 1)::int" : "u.alt";
        var lines = new List<MbomLineView>();
        var index = new Dictionary<Guid, int>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT * FROM (
              SELECT u.id, u.find_number, {altExpr} AS alt, u.child_master_id, u.quantity, u.unit, u.scrap_factor, u.phantom, u.supply_type, u.operation_ref, u.mbom_only,
                     u.reference_designators, u.notes, u.eff_kind, u.eff_context, u.eff_from, u.eff_to,
                     cm.id AS cmid, cm.number, cm.name, cm.part_type, cm.source, {MarkingColumns.Select("cm")}
              FROM {table} u
              LEFT JOIN plm.part_master cm ON cm.id = u.child_master_id AND {filter.Sql}
              WHERE {key} = @id
            ) x WHERE (@only::uuid IS NULL OR x.id = @only) ORDER BY x.find_number, x.alt
            """, conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.Add(new NpgsqlParameter("only", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)only ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var readable = !r.IsDBNull(17);
                Markings? m = null;
                string? banner = null;
                if (readable) (m, banner) = cache.Get(r, r.GetGuid(17), 22);
                index[r.GetGuid(0)] = lines.Count;
                lines.Add(new MbomLineView(r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), !readable, readable ? r.GetGuid(3) : null, readable ? r.GetString(18) : null,
                    readable ? r.GetString(19) : null, readable ? r.GetString(20) : null, readable ? r.GetString(21) : null,
                    r.GetDecimal(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetDecimal(6), r.GetBoolean(7), readable ? r.GetString(8) : null,
                    readable && !r.IsDBNull(9) ? r.GetString(9) : null, r.GetBoolean(10), readable ? r.GetFieldValue<string[]>(11) : [],
                    readable && !r.IsDBNull(12) ? r.GetString(12) : null, EffectivityStore.ReadRange(r, 13), [], banner, m));
            }
        }
        if (lines.Count == 0) return lines;
        // Links, with the EBOM line's part when the caller may read it.
        var efilter = AccessPolicy.BuildReadFilter(subject, "em");
        var linkTable = working ? "mbom.working_link" : "mbom.usage_link";
        var linkKey = working ? "working_usage_id" : "mbom_usage_id";
        var perLine = new Dictionary<Guid, List<LinkView>>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT l.{linkKey}, l.ebom_usage_id, l.status, l.reason, eu.find_number, em.id, em.number
            FROM {linkTable} l
            JOIN {table} u ON u.id = l.{linkKey}
            JOIN plm.bom_usage eu ON eu.id = l.ebom_usage_id
            LEFT JOIN plm.part_master em ON em.id = eu.child_master_id AND {efilter.Sql}
            WHERE {key} = @id
            """, conn, tx))
        {
            foreach (var (k, v) in efilter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var line = r.GetGuid(0);
                if (!index.TryGetValue(line, out var at)) continue;
                var visible = !lines[at].Redacted;
                if (!perLine.TryGetValue(line, out var list)) perLine[line] = list = [];
                list.Add(new LinkView(r.GetGuid(1), r.GetInt32(4), r.GetString(2), visible && !r.IsDBNull(3) ? r.GetString(3) : null, visible && !r.IsDBNull(5) ? r.GetString(6) : null));
            }
        }
        for (var i = 0; i < lines.Count; i++)
            if (perLine.TryGetValue(lines[i].Id, out var links)) lines[i] = lines[i] with { Links = [.. links.OrderBy(l => l.EbomFindNumber)] };
        return lines;
    }

    // ---------------------------------------------------------------- editing the working copy

    private Task<Outcome<T>> EditAsync<T>(Subject subject, string number, string action, string? correlationId,
        Func<MbomHead, NpgsqlConnection, NpgsqlTransaction, Task<Step<T>>> body, CancellationToken ct) =>
        MutateAsync<T>(subject, number, action, correlationId, async (head, conn, tx) =>
        {
            if (head.CheckoutUser is null) return Step<T>.Conflict("Check the MBOM out before editing its lines.");
            if (head.CheckoutUser != subject.Id) return Step<T>.Conflict($"Checked out by {head.CheckoutUserName ?? head.CheckoutUser}; only the user who checked it out can edit its lines.");
            return await body(head, conn, tx);
        }, ct);

    private sealed record Existing(Guid Id, int Find, EffectivityRange? Range, bool MbomOnly);

    private static async Task<List<Existing>> LoadExistingAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid revisionId, CancellationToken ct)
    {
        var list = new List<Existing>();
        await using var cmd = new NpgsqlCommand("SELECT id, find_number, eff_kind, eff_context, eff_from, eff_to, mbom_only FROM mbom.working_usage WHERE checkout_revision_id = @r", conn, tx);
        cmd.Parameters.AddWithValue("r", revisionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new Existing(r.GetGuid(0), r.GetInt32(1), EffectivityStore.ReadRange(r, 2), r.GetBoolean(6)));
        return list;
    }

    public Task<Outcome<MbomLineView>> AddLineAsync(Subject subject, string number, AddMbomLineCommand cmd, string? correlationId, CancellationToken ct = default) =>
        EditAsync<MbomLineView>(subject, number, "mbom.add_line", correlationId, async (head, conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(cmd.ChildNumber)) return Step<MbomLineView>.Invalid("Child part number is required.");
            var child = await MbomStore.LoadPartByNumberAsync(conn, tx, cmd.ChildNumber.Trim(), ct);
            if (child is null || !AccessPolicy.Evaluate(subject, child.Markings, PlmAction.Read).Permitted)
                return Step<MbomLineView>.Hidden($"Part '{cmd.ChildNumber.Trim()}' was not found, or you are not authorized to see it.", "child.not_readable");
            if (child.Id == head.PartMasterId) return Step<MbomLineView>.Invalid("An MBOM cannot contain its own part.");

            var unit = cmd.Unit ?? child.DefaultUnit;
            var supply = cmd.SupplyType ?? MbomRules.DefaultSupplyType(child.PartType, child.Source);
            var (errors, designators) = MbomRules.ValidateLine(cmd.Quantity, unit, cmd.ReferenceDesignators, cmd.Notes, cmd.FindNumber, cmd.ScrapFactor, cmd.OperationReference, supply,
                await ReferenceChecks.ActiveCodesAsync(conn, tx, RefLists.Unit, ct), await MbomStore.ActiveSupplyTypesAsync(conn, tx, ct));
            if (errors.Count > 0) return Step<MbomLineView>.Invalid([.. errors]);
            EffectivityRange? range = null;
            if (cmd.Effectivity is { } input)
            {
                var parsed = await EffectivityStore.ParseAsync(conn, tx, subject, input, ct);
                if (parsed.Error is not null) return Step<MbomLineView>.Invalid(parsed.Error);
                range = parsed.Range;
            }
            var existing = await LoadExistingAsync(conn, tx, head.RevisionId, ct);
            if (existing.Count >= MbomLimits.MaxLines) return Step<MbomLineView>.Conflict($"An MBOM can have at most {MbomLimits.MaxLines} lines.");
            var find = cmd.FindNumber ?? BomRules.NextFindNumber(existing.Select(e => e.Find));
            if (find > BomLimits.MaxFindNumber) return Step<MbomLineView>.Conflict("No find numbers left above the highest in use.");
            var overlap = existing.FirstOrDefault(e => e.Find == find && EffectivityRules.Conflict(e.Range, range));
            if (overlap is not null) return Step<MbomLineView>.Conflict(EffectivityRules.ConflictMessage(find, overlap.Range, range));

            var id = Guid.CreateVersion7();
            await InsertWorkingLineAsync(conn, tx, head.RevisionId, id, child.Id, cmd.Quantity, unit, find, designators, cmd.ScrapFactor, cmd.Phantom, supply, cmd.OperationReference?.Trim(), cmd.MbomOnly, cmd.Notes, range, BomService.DisplayId(subject), ct);
            var line = (await LoadLineViewsAsync(conn, tx, subject, true, head.RevisionId, id, new MarkingCache(), ct)).Single();
            return Step<MbomLineView>.Done(line, ("line", id.ToString("D")), ("child", child.Number), ("find", Fmt(find)), ("quantity", $"{MbomText.Qty(cmd.Quantity)} {unit}"),
                ("supply", supply), ("mbomOnly", cmd.MbomOnly.ToString()), ("effectivity", range?.Describe()));
        }, ct);

    private async Task InsertWorkingLineAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid revisionId, Guid id, Guid child, decimal qty, string unit, int find, string[] designators,
        decimal? scrap, bool phantom, string supply, string? op, bool mbomOnly, string? notes, EffectivityRange? range, string by, CancellationToken ct)
    {
        await using var ins = new NpgsqlCommand(
            """
            INSERT INTO mbom.working_usage (id, checkout_revision_id, child_master_id, quantity, unit, find_number, reference_designators, scrap_factor, phantom, supply_type, operation_ref,
                                            mbom_only, notes, eff_kind, eff_context, eff_from, eff_to, created_at, created_by)
            VALUES (@id, @rid, @child, @qty, @unit, @find, @refs, @scrap, @phantom, @supply, @op, @only, @notes, @ek, @ec, @ef, @et, @at, @by)
            """, conn, tx);
        EffectivityStore.Bind(ins, range);
        ins.Parameters.AddWithValue("id", id);
        ins.Parameters.AddWithValue("rid", revisionId);
        ins.Parameters.AddWithValue("child", child);
        ins.Parameters.AddWithValue("qty", qty);
        ins.Parameters.AddWithValue("unit", unit);
        ins.Parameters.AddWithValue("find", find);
        ins.Parameters.AddWithValue("refs", designators);
        ins.Parameters.Add(new NpgsqlParameter("scrap", NpgsqlTypes.NpgsqlDbType.Numeric) { Value = (object?)scrap ?? DBNull.Value });
        ins.Parameters.AddWithValue("phantom", phantom);
        ins.Parameters.AddWithValue("supply", supply);
        ins.Parameters.Add(new NpgsqlParameter("op", NpgsqlTypes.NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(op) ? DBNull.Value : op });
        ins.Parameters.AddWithValue("only", mbomOnly);
        ins.Parameters.Add(new NpgsqlParameter("notes", NpgsqlTypes.NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(notes) ? DBNull.Value : notes.Trim() });
        ins.Parameters.AddWithValue("at", clock.GetUtcNow());
        ins.Parameters.AddWithValue("by", by);
        await ins.ExecuteNonQueryAsync(ct);
    }

    public Task<Outcome<MbomLineView>> UpdateLineAsync(Subject subject, string number, Guid lineId, UpdateMbomLineCommand cmd, string? correlationId, CancellationToken ct = default) =>
        EditAsync<MbomLineView>(subject, number, "mbom.update_line", correlationId, async (head, conn, tx) =>
        {
            var existing = await LoadExistingAsync(conn, tx, head.RevisionId, ct);
            var current = existing.FirstOrDefault(e => e.Id == lineId);
            if (current is null) return Step<MbomLineView>.Missing();
            var supply = cmd.SupplyType ?? "";
            var units = await ReferenceChecks.ActiveCodesAsync(conn, tx, RefLists.Unit, ct);
            var (errors, designators) = MbomRules.ValidateLine(cmd.Quantity, cmd.Unit, cmd.ReferenceDesignators, cmd.Notes, cmd.FindNumber, cmd.ScrapFactor, cmd.OperationReference, supply,
                [.. units, cmd.Unit], await MbomStore.ActiveSupplyTypesAsync(conn, tx, ct));
            if (errors.Count > 0) return Step<MbomLineView>.Invalid([.. errors]);
            var range = current.Range;
            if (cmd.ClearEffectivity) range = null;
            else if (cmd.Effectivity is { } input)
            {
                var parsed = await EffectivityStore.ParseAsync(conn, tx, subject, input, ct);
                if (parsed.Error is not null) return Step<MbomLineView>.Invalid(parsed.Error);
                range = parsed.Range;
            }
            var overlap = existing.FirstOrDefault(e => e.Id != lineId && e.Find == cmd.FindNumber && EffectivityRules.Conflict(e.Range, range));
            if (overlap is not null) return Step<MbomLineView>.Conflict(EffectivityRules.ConflictMessage(cmd.FindNumber, overlap.Range, range));

            await using (var upd = new NpgsqlCommand(
                """
                UPDATE mbom.working_usage SET quantity = @qty, unit = @unit, find_number = @find, reference_designators = @refs, scrap_factor = @scrap, phantom = @phantom,
                       supply_type = @supply, operation_ref = @op, mbom_only = @only, notes = @notes, eff_kind = @ek, eff_context = @ec, eff_from = @ef, eff_to = @et
                WHERE id = @id AND checkout_revision_id = @rid
                """, conn, tx))
            {
                EffectivityStore.Bind(upd, range);
                upd.Parameters.AddWithValue("qty", cmd.Quantity);
                upd.Parameters.AddWithValue("unit", cmd.Unit);
                upd.Parameters.AddWithValue("find", cmd.FindNumber);
                upd.Parameters.AddWithValue("refs", designators);
                upd.Parameters.Add(new NpgsqlParameter("scrap", NpgsqlTypes.NpgsqlDbType.Numeric) { Value = (object?)cmd.ScrapFactor ?? DBNull.Value });
                upd.Parameters.AddWithValue("phantom", cmd.Phantom);
                upd.Parameters.AddWithValue("supply", supply);
                upd.Parameters.Add(new NpgsqlParameter("op", NpgsqlTypes.NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(cmd.OperationReference) ? DBNull.Value : cmd.OperationReference.Trim() });
                upd.Parameters.AddWithValue("only", cmd.MbomOnly);
                upd.Parameters.Add(new NpgsqlParameter("notes", NpgsqlTypes.NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(cmd.Notes) ? DBNull.Value : cmd.Notes.Trim() });
                upd.Parameters.AddWithValue("id", lineId);
                upd.Parameters.AddWithValue("rid", head.RevisionId);
                await upd.ExecuteNonQueryAsync(ct);
            }
            var dropped = 0;
            if (cmd.MbomOnly)
            {
                await using var del = new NpgsqlCommand("DELETE FROM mbom.working_link WHERE working_usage_id = @id", conn, tx);
                del.Parameters.AddWithValue("id", lineId);
                dropped = await del.ExecuteNonQueryAsync(ct);
            }
            var line = (await LoadLineViewsAsync(conn, tx, subject, true, head.RevisionId, lineId, new MarkingCache(), ct)).Single();
            return Step<MbomLineView>.Done(line, ("line", lineId.ToString("D")), ("find", Fmt(cmd.FindNumber)), ("quantity", $"{MbomText.Qty(cmd.Quantity)} {cmd.Unit}"),
                ("scrap", cmd.ScrapFactor is { } s ? MbomText.Qty(s) : null), ("supply", supply), ("mbomOnly", cmd.MbomOnly.ToString()), ("linksDropped", Fmt(dropped)),
                ("effectivity", $"{current.Range?.Describe() ?? "none"} to {range?.Describe() ?? "none"}"));
        }, ct);

    public Task<Outcome<RemovedMbomLine>> RemoveLineAsync(Subject subject, string number, Guid lineId, string? correlationId, CancellationToken ct = default) =>
        EditAsync<RemovedMbomLine>(subject, number, "mbom.remove_line", correlationId, async (head, conn, tx) =>
        {
            var existing = await LoadExistingAsync(conn, tx, head.RevisionId, ct);
            var current = existing.FirstOrDefault(e => e.Id == lineId);
            if (current is null) return Step<RemovedMbomLine>.Missing();
            await using var del = new NpgsqlCommand("DELETE FROM mbom.working_usage WHERE id = @id AND checkout_revision_id = @rid", conn, tx);
            del.Parameters.AddWithValue("id", lineId);
            del.Parameters.AddWithValue("rid", head.RevisionId);
            await del.ExecuteNonQueryAsync(ct);
            return Step<RemovedMbomLine>.Done(new RemovedMbomLine(lineId, current.Find, existing.Count - 1), ("line", lineId.ToString("D")), ("find", Fmt(current.Find)));
        }, ct);

    /// <summary>Replaces the EBOM links of one working line. An empty list makes it a manufacturing-only candidate (flag it MBOM-only or reconciliation reports it).</summary>
    public Task<Outcome<MbomLineView>> SetLinksAsync(Subject subject, string number, Guid lineId, IReadOnlyList<LinkInput> links, string? correlationId, CancellationToken ct = default) =>
        EditAsync<MbomLineView>(subject, number, "mbom.set_links", correlationId, async (head, conn, tx) =>
        {
            var existing = await LoadExistingAsync(conn, tx, head.RevisionId, ct);
            var current = existing.FirstOrDefault(e => e.Id == lineId);
            if (current is null) return Step<MbomLineView>.Missing();
            if (links.Select(l => l.EbomUsageId).Distinct().Count() != links.Count) return Step<MbomLineView>.Invalid("An EBOM line can be linked only once per MBOM line.");
            var co = await LoadCheckoutAsync(conn, tx, head.RevisionId, ct);
            // The part of the line, to check matched links; the check names only find numbers.
            Guid child;
            await using (var cmd = new NpgsqlCommand("SELECT child_master_id FROM mbom.working_usage WHERE id = @id", conn, tx))
            {
                cmd.Parameters.AddWithValue("id", lineId);
                child = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
            }
            var probe = new NewMbomLine(lineId, child, 1, "EA", current.Find, [], null, false, "Make", null, current.MbomOnly, null, null, DateTimeOffset.UtcNow, "", links);
            var errors = await MbomStore.ValidateLinksAsync(conn, tx, co.DerivedFrom, [probe], ct);
            if (errors.Count > 0) return Step<MbomLineView>.Invalid([.. errors]);
            await using (var del = new NpgsqlCommand("DELETE FROM mbom.working_link WHERE working_usage_id = @id", conn, tx))
            {
                del.Parameters.AddWithValue("id", lineId);
                await del.ExecuteNonQueryAsync(ct);
            }
            foreach (var l in links)
            {
                await using var ins = new NpgsqlCommand("INSERT INTO mbom.working_link (working_usage_id, ebom_usage_id, status, reason) VALUES (@w, @e, @s, @r)", conn, tx);
                ins.Parameters.AddWithValue("w", lineId);
                ins.Parameters.AddWithValue("e", l.EbomUsageId);
                ins.Parameters.AddWithValue("s", l.Status);
                ins.Parameters.Add(new NpgsqlParameter("r", NpgsqlTypes.NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(l.Reason) ? DBNull.Value : l.Reason.Trim() });
                await ins.ExecuteNonQueryAsync(ct);
            }
            var line = (await LoadLineViewsAsync(conn, tx, subject, true, head.RevisionId, lineId, new MarkingCache(), ct)).Single();
            return Step<MbomLineView>.Done(line, ("line", lineId.ToString("D")), ("find", Fmt(current.Find)), ("links", string.Join(",", links.Select(l => l.Status))));
        }, ct);

    /// <summary>
    /// Chooses the EBOM iteration the working copy is derived from (for a manual MBOM, or to move to a newer EBOM by hand). Existing links are carried over to the
    /// lines of the new iteration by identity (part, find number, alternate); links that have no counterpart are dropped and counted in the audit event.
    /// </summary>
    public Task<Outcome<MbomLineView?>> SetDerivationAsync(Subject subject, string number, EbomSpec spec, string? correlationId, CancellationToken ct = default) =>
        EditAsync<MbomLineView?>(subject, number, "mbom.set_derivation", correlationId, async (head, conn, tx) =>
        {
            var part = await MbomStore.LoadPartAsync(conn, tx, head.PartMasterId, ct);
            var (resolved, error) = await MbomStore.ResolveEbomAsync(conn, tx, subject, part!, spec, ct);
            if (resolved is null) return Step<MbomLineView?>.Invalid(error!);
            await using (var upd = new NpgsqlCommand("UPDATE mbom.checkout SET derived_part_iteration_id = @i, derived_spec = @s WHERE revision_id = @r", conn, tx))
            {
                upd.Parameters.AddWithValue("i", resolved.IterationId);
                upd.Parameters.AddWithValue("s", resolved.Description);
                upd.Parameters.AddWithValue("r", head.RevisionId);
                await upd.ExecuteNonQueryAsync(ct);
            }
            var (moved, dropped) = await RelinkWorkingAsync(conn, tx, head.RevisionId, resolved.IterationId, ct);
            return Step<MbomLineView?>.Done(null, ("derivedFrom", resolved.Description), ("linksMoved", Fmt(moved)), ("linksDropped", Fmt(dropped)));
        }, ct);

    internal static async Task<(int Moved, int Dropped)> RelinkWorkingAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid revisionId, Guid newIteration, CancellationToken ct)
    {
        int moved, dropped;
        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE mbom.working_link wl SET ebom_usage_id = n.id
            FROM mbom.working_usage w, plm.bom_usage o, plm.bom_usage n
            WHERE w.id = wl.working_usage_id AND w.checkout_revision_id = @r AND o.id = wl.ebom_usage_id AND o.parent_iteration_id <> @new
              AND n.parent_iteration_id = @new AND n.child_master_id = o.child_master_id AND n.find_number = o.find_number AND n.alt = o.alt
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("r", revisionId);
            cmd.Parameters.AddWithValue("new", newIteration);
            moved = await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand(
            """
            DELETE FROM mbom.working_link wl USING mbom.working_usage w
            WHERE w.id = wl.working_usage_id AND w.checkout_revision_id = @r
              AND NOT EXISTS (SELECT 1 FROM plm.bom_usage n WHERE n.id = wl.ebom_usage_id AND n.parent_iteration_id = @new)
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("r", revisionId);
            cmd.Parameters.AddWithValue("new", newIteration);
            dropped = await cmd.ExecuteNonQueryAsync(ct);
        }
        return (moved, dropped);
    }

    /// <summary>The EBOM lines of the derivation source (the working copy's, or the latest iteration's), for choosing links, with how many MBOM lines already implement each.</summary>
    public async Task<Outcome<EbomLineChoices>> GetEbomLineChoicesAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        await ReadAsync<EbomLineChoices>(subject, number, null, "mbom.ebom_lines", correlationId, async (conn, tx, head) =>
        {
            Guid? derived;
            var working = head.CheckoutUser == subject.Id;
            if (working) derived = (await LoadCheckoutAsync(conn, tx, head.RevisionId, ct)).DerivedFrom;
            else
            {
                await using var cmd = new NpgsqlCommand("SELECT derived_part_iteration_id FROM mbom.iteration WHERE id = @i", conn, tx);
                cmd.Parameters.AddWithValue("i", head.IterationId);
                derived = await cmd.ExecuteScalarAsync(ct) as Guid?;
            }
            if (derived is null) return (Outcome<EbomLineChoices>.Invalid(["This MBOM has no EBOM source yet; choose one first."]), []);
            var filter = AccessPolicy.BuildReadFilter(subject, "cm");
            var cache = new MarkingCache();
            var lines = new List<EbomLineChoice>();
            await using (var cmd = new NpgsqlCommand(
                $"""
                SELECT u.id, u.find_number, u.alt, u.quantity, u.unit, u.eff_kind, u.eff_context, u.eff_from, u.eff_to, cm.id, cm.number, cm.name, {MarkingColumns.Select("cm")},
                       (SELECT count(*) FROM {(working ? "mbom.working_link l JOIN mbom.working_usage w ON w.id = l.working_usage_id WHERE w.checkout_revision_id" : "mbom.usage_link l JOIN mbom.usage w ON w.id = l.mbom_usage_id WHERE w.iteration_id")} = @owner AND l.ebom_usage_id = u.id)
                FROM plm.bom_usage u
                LEFT JOIN plm.part_master cm ON cm.id = u.child_master_id AND {filter.Sql}
                WHERE u.parent_iteration_id = @derived ORDER BY u.find_number, u.alt
                """, conn, tx))
            {
                foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
                cmd.Parameters.AddWithValue("owner", working ? head.RevisionId : head.IterationId);
                cmd.Parameters.AddWithValue("derived", derived.Value);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var readable = !r.IsDBNull(9);
                    if (readable) cache.Get(r, r.GetGuid(9), 12);
                    lines.Add(new EbomLineChoice(r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), !readable, readable ? r.GetString(10) : null, readable ? r.GetString(11) : null,
                        r.GetDecimal(3), r.GetString(4), EffectivityStore.ReadRange(r, 5), (int)r.GetInt64(12 + MarkingColumns.Count)));
                }
            }
            var label = await MbomStore.IterationLabelAsync(conn, tx, derived.Value, ct) ?? "";
            var banner = Markings.AggregateBanner(cache.Distinct.Append(head.Markings));
            return (Outcome<EbomLineChoices>.Ok(new EbomLineChoices($"{head.PartNumber} {label}", lines, banner)), [("source", label), ("lines", Fmt(lines.Count))]);
        }, ct);
}
