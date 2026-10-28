using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

public sealed record RemovedUsage(Guid Id, int FindNumber, int Remaining);

/// <summary>
/// Working-copy edits (ADR-0003): BOM lines belong to the parent iteration and change only through the check-out owner's
/// private working copy. Parent row locking (SELECT ... FOR UPDATE) serializes every edit, check-in and check-out of the part.
/// </summary>
public sealed partial class BomService
{
    private sealed record EditContext(Guid MasterId, string Number, string PartType, Markings Markings, Guid RevisionId, string Revision);

    private sealed record EditStep<T>(OutcomeKind Kind, IReadOnlyList<string> Errors, T? Value, (string, string?)[] Details, bool Denied = false)
    {
        public static EditStep<T> Done(T value, params (string, string?)[] details) => new(OutcomeKind.Ok, [], value, details);
        public static EditStep<T> Invalid(params string[] errors) => new(OutcomeKind.Invalid, errors, default, []);
        public static EditStep<T> Conflict(string message) => new(OutcomeKind.Conflict, [message], default, []);
        public static EditStep<T> Missing() => new(OutcomeKind.NotFound, [], default, []);
        /// <summary>A refusal on access grounds: audited as Denied, reported as invalid input so existence is not disclosed.</summary>
        public static EditStep<T> Hidden(string message, string reason) => new(OutcomeKind.Invalid, [message], default, [("reasons", reason)], true);
    }

    // ---------------------------------------------------------------- read the working copy

    public async Task<Outcome<WorkingBom>> GetWorkingAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await LoadHeadAsync(conn, tx, number, forUpdate: false, ct);
        if (head is null) return Outcome<WorkingBom>.NotFound();
        if (!AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read).Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "bom.working_read", AuditOutcome.Denied, number, correlationId, ("reasons", "read")), ct);
            await tx.CommitAsync(ct);
            return Outcome<WorkingBom>.NotFound();
        }
        var co = await LoadCheckoutAsync(conn, tx, head.MasterId, ct);
        if (co is null || co.UserId != subject.Id)
        {
            await tx.RollbackAsync(ct);
            return Outcome<WorkingBom>.Conflict("The working copy is private to the user who checked the part out.");
        }
        var cache = new MarkingCache();
        var lines = await LoadWorkingLinesAsync(conn, tx, subject, co.RevisionId, null, cache, ct);
        var banner = Markings.AggregateBanner(cache.Distinct.Append(head.Markings));
        await audit.AppendAsync(tx, Entry(subject, "bom.working_read", AuditOutcome.Success, number, correlationId,
            ("lines", lines.Count.ToString(CultureInfo.InvariantCulture)), ("banner", banner)), ct);
        await tx.CommitAsync(ct);
        return Outcome<WorkingBom>.Ok(new WorkingBom(number, co.Revision, lines, banner, BomLimits.MaxChildrenPerParent));
    }

    // ---------------------------------------------------------------- add / update / remove

    public Task<Outcome<WorkingBomLine>> AddUsageAsync(Subject subject, string number, AddUsageCommand cmd, string? correlationId, CancellationToken ct = default) =>
        EditAsync<WorkingBomLine>(subject, number, "bom.add_usage", correlationId, async (ctx, conn, tx) =>
        {
            var child = string.IsNullOrWhiteSpace(cmd.ChildNumber) ? null : await LoadHeadAsync(conn, tx, cmd.ChildNumber.Trim(), forUpdate: false, ct);
            if (string.IsNullOrWhiteSpace(cmd.ChildNumber)) return EditStep<WorkingBomLine>.Invalid("Child part number is required.");
            if (child is null || !AccessPolicy.Evaluate(subject, child.Markings, PlmAction.Read).Permitted)
                return EditStep<WorkingBomLine>.Hidden($"Part '{cmd.ChildNumber.Trim()}' was not found, or you are not authorized to see it.", "child.not_readable");

            var unit = cmd.Unit ?? child.DefaultUnit;
            var (errors, designators) = BomRules.ValidateLine(cmd.Quantity, unit, cmd.ReferenceDesignators, cmd.Notes, cmd.FindNumber,
                await ReferenceChecks.ActiveCodesAsync(conn, tx, RefLists.Unit, ct));
            if (errors.Count > 0) return EditStep<WorkingBomLine>.Invalid([.. errors]);

            var existing = await LoadExistingAsync(conn, tx, ctx.RevisionId, null, ct);
            if (existing.Count >= BomLimits.MaxChildrenPerParent)
                return EditStep<WorkingBomLine>.Conflict($"A parent can have at most {BomLimits.MaxChildrenPerParent} direct children.");
            var find = cmd.FindNumber ?? BomRules.NextFindNumber(existing.Select(e => e.Find));
            if (find > BomLimits.MaxFindNumber) return EditStep<WorkingBomLine>.Conflict("No find numbers left above the highest in use; renumber the structure.");
            if (existing.Any(e => e.Find == find)) return EditStep<WorkingBomLine>.Conflict($"Find number {find} is already used on this parent.");
            var clash = BomRules.DesignatorClashes(designators, existing.SelectMany(e => e.Designators));
            if (clash.Count > 0) return EditStep<WorkingBomLine>.Invalid($"Reference designators already used elsewhere on this parent: {string.Join(", ", clash)}.");
            var cycle = await CycleErrorAsync(conn, tx, subject, ctx, [child.MasterId], ct);
            if (cycle is not null) return EditStep<WorkingBomLine>.Conflict(cycle);

            var id = Guid.CreateVersion7();
            await using (var ins = new NpgsqlCommand(
                """
                INSERT INTO plm.bom_working_usage (id, checkout_revision_id, child_master_id, quantity, unit, find_number, reference_designators, notes, created_at, created_by)
                VALUES (@id, @rid, @child, @qty, @unit, @find, @refs, @notes, @at, @by)
                """, conn, tx))
            {
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("rid", ctx.RevisionId);
                ins.Parameters.AddWithValue("child", child.MasterId);
                ins.Parameters.AddWithValue("qty", cmd.Quantity);
                ins.Parameters.AddWithValue("unit", unit);
                ins.Parameters.AddWithValue("find", find);
                ins.Parameters.AddWithValue("refs", designators);
                ins.Parameters.AddWithValue("notes", string.IsNullOrWhiteSpace(cmd.Notes) ? DBNull.Value : cmd.Notes.Trim());
                ins.Parameters.AddWithValue("at", clock.GetUtcNow());
                ins.Parameters.AddWithValue("by", DisplayId(subject));
                await ins.ExecuteNonQueryAsync(ct);
            }
            var line = (await LoadWorkingLinesAsync(conn, tx, subject, ctx.RevisionId, id, new MarkingCache(), ct)).Single();
            return EditStep<WorkingBomLine>.Done(line, ("usage", id.ToString("D")), ("child", child.Number), ("find", Fmt(find)),
                ("quantity", $"{cmd.Quantity.ToString(CultureInfo.InvariantCulture)} {unit}"));
        }, ct);

    public Task<Outcome<WorkingBomLine>> UpdateUsageAsync(Subject subject, string number, Guid usageId, UpdateUsageCommand cmd, string? correlationId, CancellationToken ct = default) =>
        EditAsync<WorkingBomLine>(subject, number, "bom.update_usage", correlationId, async (ctx, conn, tx) =>
        {
            var existing = await LoadExistingAsync(conn, tx, ctx.RevisionId, null, ct);
            var current = existing.FirstOrDefault(e => e.Id == usageId);
            if (current is null) return EditStep<WorkingBomLine>.Missing();
            var (errors, designators) = BomRules.ValidateLine(cmd.Quantity, cmd.Unit, cmd.ReferenceDesignators, cmd.Notes, cmd.FindNumber,
                [.. await ReferenceChecks.ActiveCodesAsync(conn, tx, RefLists.Unit, ct), current.Unit]);
            if (errors.Count > 0) return EditStep<WorkingBomLine>.Invalid([.. errors]);
            if (existing.Any(e => e.Id != usageId && e.Find == cmd.FindNumber)) return EditStep<WorkingBomLine>.Conflict($"Find number {cmd.FindNumber} is already used on this parent.");
            var clash = BomRules.DesignatorClashes(designators, existing.Where(e => e.Id != usageId).SelectMany(e => e.Designators));
            if (clash.Count > 0) return EditStep<WorkingBomLine>.Invalid($"Reference designators already used elsewhere on this parent: {string.Join(", ", clash)}.");

            await using (var upd = new NpgsqlCommand(
                "UPDATE plm.bom_working_usage SET quantity = @qty, unit = @unit, find_number = @find, reference_designators = @refs, notes = @notes WHERE id = @id AND checkout_revision_id = @rid", conn, tx))
            {
                upd.Parameters.AddWithValue("qty", cmd.Quantity);
                upd.Parameters.AddWithValue("unit", cmd.Unit);
                upd.Parameters.AddWithValue("find", cmd.FindNumber);
                upd.Parameters.AddWithValue("refs", designators);
                upd.Parameters.AddWithValue("notes", string.IsNullOrWhiteSpace(cmd.Notes) ? DBNull.Value : cmd.Notes.Trim());
                upd.Parameters.AddWithValue("id", usageId);
                upd.Parameters.AddWithValue("rid", ctx.RevisionId);
                await upd.ExecuteNonQueryAsync(ct);
            }
            var line = (await LoadWorkingLinesAsync(conn, tx, subject, ctx.RevisionId, usageId, new MarkingCache(), ct)).Single();
            return EditStep<WorkingBomLine>.Done(line, ("usage", usageId.ToString("D")), ("find", $"{Fmt(current.Find)} to {Fmt(cmd.FindNumber)}"),
                ("quantity", $"{current.Quantity.ToString(CultureInfo.InvariantCulture)} {current.Unit} to {cmd.Quantity.ToString(CultureInfo.InvariantCulture)} {cmd.Unit}"));
        }, ct);

    public Task<Outcome<RemovedUsage>> RemoveUsageAsync(Subject subject, string number, Guid usageId, string? correlationId, CancellationToken ct = default) =>
        EditAsync<RemovedUsage>(subject, number, "bom.remove_usage", correlationId, async (ctx, conn, tx) =>
        {
            var existing = await LoadExistingAsync(conn, tx, ctx.RevisionId, null, ct);
            var current = existing.FirstOrDefault(e => e.Id == usageId);
            if (current is null) return EditStep<RemovedUsage>.Missing();
            await using var del = new NpgsqlCommand("DELETE FROM plm.bom_working_usage WHERE id = @id AND checkout_revision_id = @rid", conn, tx);
            del.Parameters.AddWithValue("id", usageId);
            del.Parameters.AddWithValue("rid", ctx.RevisionId);
            await del.ExecuteNonQueryAsync(ct);
            return EditStep<RemovedUsage>.Done(new RemovedUsage(usageId, current.Find, existing.Count - 1),
                ("usage", usageId.ToString("D")), ("find", Fmt(current.Find)));
        }, ct);

    /// <summary>Renumbers all lines in their current find-number order to start, start+step, ... (default 10, 20, 30, ...).</summary>
    public Task<Outcome<WorkingBom>> RenumberAsync(Subject subject, string number, int start, int step, string? correlationId, CancellationToken ct = default) =>
        EditAsync<WorkingBom>(subject, number, "bom.renumber", correlationId, async (ctx, conn, tx) =>
        {
            if (start < 1 || step < 1) return EditStep<WorkingBom>.Invalid("Start and step must be 1 or greater.");
            var count = (await LoadExistingAsync(conn, tx, ctx.RevisionId, null, ct)).Count;
            if ((long)start + (long)step * count > BomLimits.MaxFindNumber) return EditStep<WorkingBom>.Invalid($"Find numbers would exceed {BomLimits.MaxFindNumber}.");
            // The uniqueness constraint is deferrable, so lines can swap numbers inside one statement.
            await using (var defer = new NpgsqlCommand("SET CONSTRAINTS ALL DEFERRED", conn, tx)) await defer.ExecuteNonQueryAsync(ct);
            await using (var upd = new NpgsqlCommand(
                """
                UPDATE plm.bom_working_usage w SET find_number = n.new_find
                FROM (SELECT id, @start + (row_number() OVER (ORDER BY find_number) - 1) * @step AS new_find
                      FROM plm.bom_working_usage WHERE checkout_revision_id = @rid) n
                WHERE w.id = n.id
                """, conn, tx))
            {
                upd.Parameters.AddWithValue("start", start);
                upd.Parameters.AddWithValue("step", step);
                upd.Parameters.AddWithValue("rid", ctx.RevisionId);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await using (var immediate = new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE", conn, tx)) await immediate.ExecuteNonQueryAsync(ct);
            return EditStep<WorkingBom>.Done(await WorkingViewAsync(conn, tx, subject, ctx, ct), ("start", Fmt(start)), ("step", Fmt(step)), ("lines", Fmt(count)));
        }, ct);

    /// <summary>
    /// Copies the latest structure (latest revision, latest iteration) of another assembly into this working copy. Refuses when
    /// the source holds parts the caller cannot read, rather than silently producing an incomplete structure.
    /// </summary>
    public Task<Outcome<WorkingBom>> CopyStructureAsync(Subject subject, string number, string sourceNumber, bool replaceExisting, string? correlationId, CancellationToken ct = default) =>
        EditAsync<WorkingBom>(subject, number, "bom.copy_structure", correlationId, async (ctx, conn, tx) =>
        {
            var src = string.IsNullOrWhiteSpace(sourceNumber) ? null : await LoadRootAsync(conn, tx, sourceNumber.Trim(), null, null, ct);
            if (src is null || !AccessPolicy.Evaluate(subject, src.Markings, PlmAction.Read).Permitted)
                return EditStep<WorkingBom>.Hidden($"Part '{sourceNumber}' was not found, or you are not authorized to see it.", "source.not_readable");
            if (src.MasterId == ctx.MasterId) return EditStep<WorkingBom>.Invalid("Choose a different assembly to copy from.");

            var lines = await LoadIterationLinesAsync(conn, tx, subject, src.IterationId, new MarkingCache(), ct);
            if (lines.Count == 0) return EditStep<WorkingBom>.Invalid($"{src.Number} has no structure to copy.");
            var hidden = lines.Count(l => !l.Readable);
            if (hidden > 0) return EditStep<WorkingBom>.Hidden($"{src.Number} contains {hidden} item(s) you are not authorized to see; the structure cannot be copied.", "source.contains_restricted");

            var existing = await LoadExistingAsync(conn, tx, ctx.RevisionId, null, ct);
            if (existing.Count > 0 && !replaceExisting) return EditStep<WorkingBom>.Conflict("This working copy already has lines; choose to replace them to copy another structure.");
            if (lines.Count > BomLimits.MaxChildrenPerParent) return EditStep<WorkingBom>.Conflict($"A parent can have at most {BomLimits.MaxChildrenPerParent} direct children.");
            var cycle = await CycleErrorAsync(conn, tx, subject, ctx, [.. lines.Select(l => l.MasterId).Distinct()], ct);
            if (cycle is not null) return EditStep<WorkingBom>.Conflict(cycle);

            if (existing.Count > 0)
            {
                await using var del = new NpgsqlCommand("DELETE FROM plm.bom_working_usage WHERE checkout_revision_id = @rid", conn, tx);
                del.Parameters.AddWithValue("rid", ctx.RevisionId);
                await del.ExecuteNonQueryAsync(ct);
            }
            await using (var ins = new NpgsqlCommand(
                """
                INSERT INTO plm.bom_working_usage (id, checkout_revision_id, child_master_id, quantity, unit, find_number, reference_designators, notes, created_at, created_by)
                SELECT gen_random_uuid(), @rid, u.child_master_id, u.quantity, u.unit, u.find_number, u.reference_designators, u.notes, @at, @by
                FROM plm.bom_usage u WHERE u.parent_iteration_id = @iter
                """, conn, tx))
            {
                ins.Parameters.AddWithValue("rid", ctx.RevisionId);
                ins.Parameters.AddWithValue("iter", src.IterationId);
                ins.Parameters.AddWithValue("at", clock.GetUtcNow());
                ins.Parameters.AddWithValue("by", DisplayId(subject));
                await ins.ExecuteNonQueryAsync(ct);
            }
            return EditStep<WorkingBom>.Done(await WorkingViewAsync(conn, tx, subject, ctx, ct),
                ("source", $"{src.Number} {src.Revision}.{src.Iteration}"), ("lines", Fmt(lines.Count)), ("replaced", replaceExisting.ToString()));
        }, ct);

    // ---------------------------------------------------------------- machinery

    private sealed record Head(Guid MasterId, string Number, string PartType, string DefaultUnit, Markings Markings);
    private sealed record CheckoutInfo(Guid RevisionId, string Revision, string State, string UserId, string? UserName);
    private sealed record ExistingLine(Guid Id, int Find, decimal Quantity, string Unit, string[] Designators);

    private async Task<Outcome<T>> EditAsync<T>(
        Subject subject, string number, string action, string? correlationId,
        Func<EditContext, NpgsqlConnection, NpgsqlTransaction, Task<EditStep<T>>> body, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await LoadHeadAsync(conn, tx, number, forUpdate: true, ct);
        if (head is null) return Outcome<T>.NotFound();

        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.NotFound();
        }
        var modify = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Modify);
        if (!modify.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", modify.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.Forbidden(modify.Reasons);
        }

        EditStep<T> step;
        var co = await LoadCheckoutAsync(conn, tx, head.MasterId, ct);
        if (head.PartType != "Assembly")
            step = EditStep<T>.Conflict($"{head.Number} is a {head.PartType}; only Assembly parts can own BOM lines.");
        else if (co is null)
            step = EditStep<T>.Conflict("Check the part out before editing its BOM.");
        else if (co.UserId != subject.Id)
            step = EditStep<T>.Conflict($"Checked out by {co.UserName ?? co.UserId}; only the user who checked it out can edit its BOM.");
        else
            step = await body(new EditContext(head.MasterId, head.Number, head.PartType, head.Markings, co.RevisionId, co.Revision), conn, tx);

        if (step.Kind != OutcomeKind.Ok)
        {
            await tx.RollbackAsync(ct);
            var failure = step.Denied
                ? Entry(subject, action, AuditOutcome.Denied, number, correlationId, step.Details)
                : Entry(subject, action, AuditOutcome.Failure, number, correlationId, ("error", string.Join(" ", step.Errors)), ("kind", step.Kind.ToString()));
            await audit.AppendAsync(failure, ct);
            return step.Kind switch
            {
                OutcomeKind.Invalid => Outcome<T>.Invalid(step.Errors),
                OutcomeKind.NotFound => Outcome<T>.NotFound(),
                _ => Outcome<T>.Conflict(step.Errors[0]),
            };
        }
        await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Success, number, correlationId, step.Details), ct);
        await tx.CommitAsync(ct);
        return Outcome<T>.Ok(step.Value!);
    }

    private static async Task<Head?> LoadHeadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string number, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT m.id, m.number, m.part_type, m.default_unit, {MarkingColumns.Select("m")} FROM plm.part_master m WHERE m.number = @n{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Head(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), MarkingColumns.Read(r, 4));
    }

    private static async Task<CheckoutInfo?> LoadCheckoutAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid master, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT r.id, r.revision, r.lifecycle_state, c.user_id, c.user_name
            FROM plm.part_revision r JOIN plm.part_checkout c ON c.revision_id = r.id
            WHERE r.master_id = @m AND r.is_latest
            """, conn, tx);
        cmd.Parameters.AddWithValue("m", master);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new CheckoutInfo(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)) : null;
    }

    private static async Task<List<ExistingLine>> LoadExistingAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid revisionId, Guid? except, CancellationToken ct)
    {
        var list = new List<ExistingLine>();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, find_number, quantity, unit, reference_designators FROM plm.bom_working_usage WHERE checkout_revision_id = @rid AND (@except::uuid IS NULL OR id <> @except)", conn, tx);
        cmd.Parameters.AddWithValue("rid", revisionId);
        cmd.Parameters.AddWithValue("except", (object?)except ?? DBNull.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ExistingLine(r.GetGuid(0), r.GetInt32(1), r.GetDecimal(2), r.GetString(3), r.GetFieldValue<string[]>(4)));
        return list;
    }

    private static async Task<List<WorkingBomLine>> LoadWorkingLinesAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, Guid revisionId, Guid? only, MarkingCache cache, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "cm");
        var lines = new List<WorkingBomLine>();
        await using var cmd = new NpgsqlCommand(BomSql.WorkingLines(filter, only is not null), conn, tx);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("rid", revisionId);
        if (only is { } id) cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var readable = !r.IsDBNull(7);
            Markings? m = null;
            string? banner = null;
            if (readable) (m, banner) = cache.Get(r, r.GetGuid(7), 11);
            lines.Add(new WorkingBomLine(r.GetGuid(0), r.GetInt32(1), r.GetDecimal(2), r.GetString(3), !readable,
                readable ? r.GetGuid(6) : null, readable ? r.GetString(8) : null, readable ? r.GetString(9) : null, readable ? r.GetString(10) : null,
                readable ? r.GetFieldValue<string[]>(4) : [], readable && !r.IsDBNull(5) ? r.GetString(5) : null, banner, m));
        }
        return lines;
    }

    private static async Task<WorkingBom> WorkingViewAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, EditContext ctx, CancellationToken ct)
    {
        var cache = new MarkingCache();
        var lines = await LoadWorkingLinesAsync(conn, tx, subject, ctx.RevisionId, null, cache, ct);
        return new WorkingBom(ctx.Number, ctx.Revision, lines, Markings.AggregateBanner(cache.Distinct.Append(ctx.Markings)), BomLimits.MaxChildrenPerParent);
    }

    private static async Task<string?> CycleErrorAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, EditContext ctx, IReadOnlyCollection<Guid> children, CancellationToken ct)
    {
        var cycle = await BomStructure.FindCycleAsync(conn, tx, ctx.MasterId, children, ct);
        if (cycle is null) return null;
        var names = await BomStructure.NamesAsync(conn, tx, subject, [.. cycle.Value.Chain, cycle.Value.Child], ct);
        var child = names[0];
        return BomRules.CycleMessage(ctx.Number, child, names);
    }

    private static string Fmt(int n) => n.ToString(CultureInfo.InvariantCulture);
}
