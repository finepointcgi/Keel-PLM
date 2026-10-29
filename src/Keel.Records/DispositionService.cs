using System.Globalization;
using System.Text;
using System.Text.Json;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Records;

/// <summary>
/// The disposition workflow (ADR-0041): an access-filtered queue of objects whose retention period has run, requests approved through the workflow engine (signatures),
/// and execution by a records manager that never breaks the audit chain or referential integrity: objects are TOMBSTONED, not deleted (every row stays, new work is
/// frozen by triggers); vault content is released to the existing garbage collector (grace period, integrity check) instead of being deleted here; and a signed,
/// immutable certificate of destruction is issued. A later confirmation records, signed, that the garbage collector removed the content.
/// <para>
/// Execution evaluates everything again under the gate lock shared with legal holds (classification, hold membership including a fresh re-evaluation of program and
/// query holds, dependents), so what was true when the request was made is never trusted.
/// </para>
/// </summary>
public sealed class DispositionService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, RecordsSigner signer, ArchiveService archives, RecordsOptions options, IContentStore? store = null)
{
    private const string ObjectType = "disposition";
    private const string CertificateType = "certificate";

    private async Task<Outcome<T>> DenyAsync<T>(Subject s, string action, string objectType, string? id, string? corr, string reason, CancellationToken ct)
    {
        await audit.AppendAsync(RecordsStore.Entry(s, action, AuditOutcome.Denied, objectType, id, corr, ("reasons", reason)), ct);
        return Outcome<T>.Forbidden([reason]);
    }

    // ---------------------------------------------------------------- queue

    /// <summary>Objects past their retention period, that the caller can read, that no hold keeps and no open request already lists.</summary>
    public async Task<Outcome<EligiblePage>> EligibleAsync(Subject s, EligibleQuery q, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanView(s)) return await DenyAsync<EligiblePage>(s, "records.queue_read", ObjectType, null, corr, AccessPolicyReasons.Role, ct);
        if (q.Kind is not null && !RecordKinds.IsKnown(q.Kind)) return Outcome<EligiblePage>.Invalid(["The object kind must be document or part."]);
        var limit = Math.Clamp(q.Limit, 1, 200);
        var offset = Math.Max(0, q.Offset);
        var today = RecordsStore.Today(clock);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = new List<EligibleRow>();
        long total = 0, held = 0;
        foreach (var kind in q.Kind is null ? RecordKinds.All : [q.Kind])
        {
            var table = kind == RecordKinds.Document ? "plm.document_master" : "plm.part_master";
            var f = AccessPolicy.BuildReadFilter(s, "x");
            var where = new StringBuilder($"c.object_kind = '{kind}' AND c.eligible_on <= @today AND {f.Sql}");
            if (q.ClassCode is not null) where.Append(" AND c.class_code = @cls");
            if (!string.IsNullOrWhiteSpace(q.Search)) where.Append(" AND (x.number ILIKE @q ESCAPE '\\' OR x.name ILIKE @q ESCAPE '\\')");
            void Bind(NpgsqlCommand cmd)
            {
                foreach (var (k, v) in f.Parameters) cmd.Parameters.AddWithValue(k, v);
                cmd.Parameters.AddWithValue("today", today);
                if (q.ClassCode is not null) cmd.Parameters.AddWithValue("cls", q.ClassCode);
                if (!string.IsNullOrWhiteSpace(q.Search)) cmd.Parameters.AddWithValue("q", "%" + q.Search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%");
            }
            const string open = "NOT rec.is_tombstoned(c.object_kind, c.object_id) AND NOT EXISTS (SELECT 1 FROM rec.disposition_item i JOIN rec.disposition d ON d.id = i.disposition_id WHERE i.object_kind = c.object_kind AND i.object_id = c.object_id AND d.status IN ('draft', 'in_review', 'approved'))";
            await using (var cmd = new NpgsqlCommand($"SELECT count(*) FROM rec.classification c JOIN {table} x ON x.id = c.object_id WHERE {where} AND NOT rec.is_held(c.object_kind, c.object_id) AND {open}", conn))
            {
                Bind(cmd);
                total += (long)(await cmd.ExecuteScalarAsync(ct))!;
            }
            await using (var cmd = new NpgsqlCommand($"SELECT count(*) FROM rec.classification c JOIN {table} x ON x.id = c.object_id WHERE {where} AND rec.is_held(c.object_kind, c.object_id) AND NOT rec.is_tombstoned(c.object_kind, c.object_id)", conn))
            {
                Bind(cmd);
                held += (long)(await cmd.ExecuteScalarAsync(ct))!;
            }
            await using (var cmd = new NpgsqlCommand(
                $"SELECT x.number, x.name, c.class_code, c.trigger_event, c.trigger_date, c.eligible_on, c.disposition, {MarkingColumns.Select("x")} FROM rec.classification c JOIN {table} x ON x.id = c.object_id WHERE {where} AND NOT rec.is_held(c.object_kind, c.object_id) AND {open} ORDER BY c.eligible_on, x.number LIMIT @lim", conn))
            {
                Bind(cmd);
                cmd.Parameters.AddWithValue("lim", offset + limit);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var m = MarkingColumns.Read(r, 7);
                    rows.Add(new EligibleRow(kind, r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateOnly>(4), r.GetFieldValue<DateOnly>(5), r.GetString(6), m.BannerText(), m));
                }
            }
        }
        var page = rows.OrderBy(r => r.EligibleOn).ThenBy(r => r.Number, StringComparer.Ordinal).Skip(offset).Take(limit).ToList();
        return Outcome<EligiblePage>.Ok(new EligiblePage(page, total, held, Markings.AggregateBanner(page.Select(r => r.Markings))));
    }

    // ---------------------------------------------------------------- create / read / cancel

    public async Task<Outcome<DispositionView>> CreateAsync(Subject s, CreateDispositionCommand cmd, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanManage(s)) return await DenyAsync<DispositionView>(s, "records.disposition_create", ObjectType, null, corr, AccessPolicyReasons.Role, ct);
        if (cmd.Items.Count == 0) return Outcome<DispositionView>.Invalid(["Choose at least one object."]);
        if (cmd.Items.Count > options.MaxItemsPerRequest) return Outcome<DispositionView>.Invalid([$"A request lists at most {options.MaxItemsPerRequest} objects."]);
        if ((cmd.Note?.Length ?? 0) > 1000) return Outcome<DispositionView>.Invalid(["The note must be at most 1000 characters."]);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var resolved = await ArchiveService.ResolveAsync(conn, tx, s, cmd.Items, ct);
        if (resolved.Errors.Count > 0) return Outcome<DispositionView>.Invalid(resolved.Errors);
        await RecordsStore.TakeGateAsync(conn, tx, ct);
        await HoldService.ReevaluateAllActiveAsync(conn, tx, clock.GetUtcNow(), ct);
        var published = await RecordsStore.LoadPublishedAsync(conn, tx, ct);
        if (published is null) return Outcome<DispositionView>.Conflict("No retention schedule is published yet.");
        var events = await RecordsStore.LoadProgramEventsAsync(conn, tx, ct);
        var today = RecordsStore.Today(clock);

        var problems = new List<string>();
        var items = new List<(ObjectFacts F, ClassAssignment A)>();
        foreach (var f in resolved.Objects)
        {
            var label = $"{RecordKinds.Label(f.Kind)} {f.Number}";
            if (await RecordsStore.IsTombstonedAsync(conn, tx, f.Kind, f.Id, ct)) { problems.Add($"{label} was already disposed of."); continue; }
            if (await RecordsStore.IsHeldAsync(conn, tx, f.Kind, f.Id, ct)) { problems.Add($"{label} is under legal hold."); continue; }
            var a = RetentionRules.Assign(published.Rules, f, events);
            if (a is null || a.EligibleOn > today) { problems.Add($"{label} is not due for disposition."); continue; }
            string? other;
            await using (var chk = new NpgsqlCommand("SELECT d.number FROM rec.disposition_item i JOIN rec.disposition d ON d.id = i.disposition_id WHERE i.object_kind = @k AND i.object_id = @id AND d.status IN ('draft', 'in_review', 'approved') LIMIT 1", conn, tx))
            {
                chk.Parameters.AddWithValue("k", f.Kind); chk.Parameters.AddWithValue("id", f.Id);
                other = (string?)await chk.ExecuteScalarAsync(ct);
            }
            if (other is not null) { problems.Add($"{label} is already in request {other}."); continue; }
            items.Add((f, a));
        }
        if (problems.Count > 0) return Outcome<DispositionView>.Invalid(problems);

        var id = Guid.CreateVersion7();
        var number = RecordsStore.NextNumber("DR-", await RecordsStore.NextValAsync(conn, tx, "rec.disposition_seq", ct));
        var now = clock.GetUtcNow();
        await using (var ins = new NpgsqlCommand($"INSERT INTO rec.disposition (id, number, status, note, created_at, created_by, {MarkingColumns.InsertColumns}) VALUES (@id, @n, 'draft', @note, @now, @by, {MarkingColumns.InsertValues})", conn, tx))
        {
            ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("n", number); ins.Parameters.AddWithValue("note", (object?)cmd.Note?.Trim() ?? DBNull.Value);
            ins.Parameters.AddWithValue("now", now); ins.Parameters.AddWithValue("by", s.Username ?? s.Id);
            RecordsStore.AddMarkings(ins, resolved.Cover!);
            await ins.ExecuteNonQueryAsync(ct);
        }
        foreach (var (f, a) in items)
        {
            await using var ins = new NpgsqlCommand(
                "INSERT INTO rec.disposition_item (id, disposition_id, object_kind, object_id, object_number, class_code, schedule_version, action, eligible_on) VALUES (@id, @d, @k, @o, @n, @c, @v, @a, @e)", conn, tx);
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7()); ins.Parameters.AddWithValue("d", id); ins.Parameters.AddWithValue("k", f.Kind); ins.Parameters.AddWithValue("o", f.Id);
            ins.Parameters.AddWithValue("n", f.Number); ins.Parameters.AddWithValue("c", a.Code); ins.Parameters.AddWithValue("v", published.Version); ins.Parameters.AddWithValue("a", a.Disposition); ins.Parameters.AddWithValue("e", a.EligibleOn);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, RecordsStore.Entry(s, "records.disposition_create", AuditOutcome.Success, ObjectType, number, corr, ("items", items.Count.ToString(CultureInfo.InvariantCulture)), ("scheduleVersion", published.Version.ToString(CultureInfo.InvariantCulture))), ct);
        var view = await LoadViewAsync(conn, tx, number, ct);
        await tx.CommitAsync(ct);
        return Outcome<DispositionView>.Ok(view!);
    }

    private static readonly string Select = $"d.id, d.number, d.status, d.note, d.revision, d.created_at, d.created_by, d.executed_at, d.executed_by, {MarkingColumns.Select("d")}";

    internal static async Task<DispositionView?> LoadViewAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string number, CancellationToken ct)
    {
        DispositionView? view; Guid id;
        await using (var cmd = new NpgsqlCommand($"SELECT {Select}, (SELECT c.number FROM rec.certificate c WHERE c.disposition_id = d.id) FROM rec.disposition d WHERE d.number = @n", conn, tx))
        {
            cmd.Parameters.AddWithValue("n", number);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            id = r.GetGuid(0);
            var m = MarkingColumns.Read(r, 9);
            view = new DispositionView(r.GetString(1), r.GetString(2), RecordsStore.Str(r, 3), r.GetInt32(4), r.GetFieldValue<DateTimeOffset>(5), r.GetString(6),
                r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), RecordsStore.Str(r, 8), m.BannerText(), m, [], RecordsStore.Str(r, 9 + MarkingColumns.Count));
        }
        var items = new List<DispositionItemView>();
        await using (var cmd = new NpgsqlCommand("SELECT object_kind, object_number, class_code, action, schedule_version, eligible_on, result, result_code FROM rec.disposition_item WHERE disposition_id = @d ORDER BY object_kind, object_number", conn, tx))
        {
            cmd.Parameters.AddWithValue("d", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(new DispositionItemView(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetFieldValue<DateOnly>(5), RecordsStore.Str(r, 6), RecordsStore.Str(r, 7)));
        }
        return view with { Items = items };
    }

    public async Task<Outcome<DispositionView>> GetAsync(Subject s, string number, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanView(s)) return await DenyAsync<DispositionView>(s, "records.disposition_read", ObjectType, number, corr, AccessPolicyReasons.Role, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var v = await LoadViewAsync(conn, null, number, ct);
        if (v is null) return Outcome<DispositionView>.NotFound();
        var read = AccessPolicy.Evaluate(s, v.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(RecordsStore.Entry(s, "records.disposition_read", AuditOutcome.Denied, ObjectType, number, corr, ("reasons", string.Join(",", read.Reasons))), ct);
            return Outcome<DispositionView>.NotFound();
        }
        return Outcome<DispositionView>.Ok(v);
    }

    public async Task<Outcome<IReadOnlyList<DispositionSummary>>> ListAsync(Subject s, string? status, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanView(s)) return await DenyAsync<IReadOnlyList<DispositionSummary>>(s, "records.disposition_list", ObjectType, null, corr, AccessPolicyReasons.Role, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var f = AccessPolicy.BuildReadFilter(s, "d");
        var list = new List<DispositionSummary>();
        await using var cmd = new NpgsqlCommand($"SELECT d.number, d.status, (SELECT count(*) FROM rec.disposition_item i WHERE i.disposition_id = d.id), d.created_at, d.created_by, {MarkingColumns.Select("d")} FROM rec.disposition d WHERE {f.Sql}{(status is null ? "" : " AND d.status = @st")} ORDER BY d.created_at DESC LIMIT 500", conn);
        foreach (var (k, v) in f.Parameters) cmd.Parameters.AddWithValue(k, v);
        if (status is not null) cmd.Parameters.AddWithValue("st", status);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new DispositionSummary(r.GetString(0), r.GetString(1), (int)r.GetInt64(2), r.GetFieldValue<DateTimeOffset>(3), r.GetString(4), MarkingColumns.Read(r, 5).BannerText()));
        return Outcome<IReadOnlyList<DispositionSummary>>.Ok(list);
    }

    public async Task<Outcome<DispositionView>> CancelAsync(Subject s, string number, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanManage(s)) return await DenyAsync<DispositionView>(s, "records.disposition_cancel", ObjectType, number, corr, AccessPolicyReasons.Role, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var v = await LoadViewAsync(conn, tx, number, ct);
        if (v is null || !AccessPolicy.Evaluate(s, v.Markings, PlmAction.Read).Permitted) return Outcome<DispositionView>.NotFound();
        if (v.Status is not (DispositionStatuses.Draft or DispositionStatuses.Approved)) return Outcome<DispositionView>.Conflict($"{number} is {DispositionStatuses.Label(v.Status).ToLowerInvariant()} and cannot be cancelled here (cancel a running review from the review).");
        await using (var upd = new NpgsqlCommand("UPDATE rec.disposition SET status = 'cancelled', revision = revision + 1, updated_at = @now WHERE number = @n AND status = @st", conn, tx))
        {
            upd.Parameters.AddWithValue("now", clock.GetUtcNow()); upd.Parameters.AddWithValue("n", number); upd.Parameters.AddWithValue("st", v.Status);
            if (await upd.ExecuteNonQueryAsync(ct) == 0) return Outcome<DispositionView>.Conflict("The request changed; reload it.");
        }
        await audit.AppendAsync(tx, RecordsStore.Entry(s, "records.disposition_cancel", AuditOutcome.Success, ObjectType, number, corr), ct);
        var after = await LoadViewAsync(conn, tx, number, ct);
        await tx.CommitAsync(ct);
        return Outcome<DispositionView>.Ok(after!);
    }

    // ---------------------------------------------------------------- execute

    private sealed record Item(Guid Id, string Kind, Guid ObjectId, string Number, string ClassCode, int ScheduleVersion, string Action);

    private sealed record Disposed(Item Item, ObjectFacts Facts, Guid TombstoneId, IReadOnlyList<(string Sha, long Size)> Content);

    public async Task<Outcome<ExecutionResult>> ExecuteAsync(Subject s, string number, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanExecute(s)) return await DenyAsync<ExecutionResult>(s, "records.disposition_execute", ObjectType, number, corr, AccessPolicyReasons.Role, ct);
        if (!signer.Available) return Outcome<ExecutionResult>.Unavailable("No checkpoint signing key is configured, so a certificate of destruction cannot be signed. Nothing was disposed of.");

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var peek = await LoadViewAsync(conn, null, number, ct);
        if (peek is null) return Outcome<ExecutionResult>.NotFound();
        var read = AccessPolicy.Evaluate(s, peek.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(RecordsStore.Entry(s, "records.disposition_execute", AuditOutcome.Denied, ObjectType, number, corr, ("reasons", string.Join(",", read.Reasons))), ct);
            return Outcome<ExecutionResult>.NotFound();
        }
        if (peek.Status != DispositionStatuses.Approved) return Outcome<ExecutionResult>.Conflict($"{number} is {DispositionStatuses.Label(peek.Status).ToLowerInvariant()}: only an approved request can be executed.");
        // Marked content (CUI or export controlled): the person who asked for the disposal cannot also carry it out (two-person rule, FEAT-REC-9).
        if ((peek.Markings.IsCui || peek.Markings.IsExportControlled) && string.Equals(peek.CreatedBy, s.Username ?? s.Id, StringComparison.Ordinal))
            return await DenyAsync<ExecutionResult>(s, "records.disposition_execute", ObjectType, number, corr, "sod.executor_is_requester", ct);

        var itemIds = new List<Item>();
        await using (var cmd = new NpgsqlCommand("SELECT i.id, i.object_kind, i.object_id, i.object_number, i.class_code, i.schedule_version, i.action FROM rec.disposition_item i JOIN rec.disposition d ON d.id = i.disposition_id WHERE d.number = @n ORDER BY i.object_kind, i.object_number", conn))
        {
            cmd.Parameters.AddWithValue("n", number);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) itemIds.Add(new Item(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetString(6)));
        }

        // Objects moving to the archive are packaged first (the vault is not transactional); the package is registered only if the transaction commits it.
        PackagedArchive? package = null;
        var transferFacts = new List<ObjectFacts>();
        foreach (var it in itemIds.Where(i => i.Action == DispositionActions.TransferArchive))
            if (await RecordsStore.LoadFactsByIdAsync(conn, null, it.Kind, it.ObjectId, ct) is { } f) transferFacts.Add(f);
        if (transferFacts.Count > 0)
        {
            var cover = MarkingDominance.MinimalCover(transferFacts[0].Markings, transferFacts.Skip(1).Select(f => f.Markings));
            if (!cover.IsSuccess) return Outcome<ExecutionResult>.Invalid(cover.Errors);
            var packaged = await archives.PackageAsync(s.Username ?? s.Id, $"Transfer under {number}", transferFacts, cover.Value, corr, ct);
            if (!packaged.IsOk) return new Outcome<ExecutionResult>(packaged.Kind, null, packaged.Errors);
            package = packaged.Value;
        }

        await using var tx = await conn.BeginTransactionAsync(ct);
        await RecordsStore.TakeGateAsync(conn, tx, ct);
        Guid dispositionId; string status;
        await using (var lockCmd = new NpgsqlCommand("SELECT id, status FROM rec.disposition WHERE number = @n FOR UPDATE", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("n", number);
            await using var r = await lockCmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Outcome<ExecutionResult>.NotFound();
            dispositionId = r.GetGuid(0); status = r.GetString(1);
        }
        if (status != DispositionStatuses.Approved) return Outcome<ExecutionResult>.Conflict($"{number} is no longer approved (another run may have executed it).");

        var now = AuditChain.Truncate(clock.GetUtcNow());
        var today = RecordsStore.Today(clock);
        await HoldService.ReevaluateAllActiveAsync(conn, tx, now, ct);
        var published = await RecordsStore.LoadPublishedAsync(conn, tx, ct);
        if (published is null) return Outcome<ExecutionResult>.Conflict("No retention schedule is published.");
        var events = await RecordsStore.LoadProgramEventsAsync(conn, tx, ct);

        var results = new Dictionary<Guid, (string Result, string? Code)>();
        var disposed = new List<Disposed>();
        var packageNumbers = package?.Objects.Select(o => o.Number).ToHashSet(StringComparer.Ordinal) ?? [];
        var pending = itemIds.ToList();
        bool progress;
        do
        {
            progress = false;
            foreach (var it in pending.ToList())
            {
                var facts = await RecordsStore.LoadFactsByIdAsync(conn, tx, it.Kind, it.ObjectId, ct);
                string? skip = null;
                ClassAssignment? a = null;
                if (facts is null) skip = SkipCodes.Gone;
                else if (await RecordsStore.IsTombstonedAsync(conn, tx, it.Kind, it.ObjectId, ct)) skip = SkipCodes.AlreadyDisposed;
                else if (await RecordsStore.IsHeldAsync(conn, tx, it.Kind, it.ObjectId, ct)) skip = SkipCodes.Held;
                else
                {
                    a = RetentionRules.Assign(published.Rules, facts!, events);
                    if (a is null || a.EligibleOn > today || a.Code != it.ClassCode || a.Disposition != it.Action) skip = SkipCodes.NotEligible;
                    else if (it.Action == DispositionActions.TransferArchive && !packageNumbers.Contains(it.Number)) skip = SkipCodes.NotEligible;
                }
                if (skip is null && it.Action != DispositionActions.Review && await IsReferencedAsync(conn, tx, it, [.. disposed.Select(d => d.Item.ObjectId)], ct)) { results[it.Id] = ("skipped", SkipCodes.Referenced); continue; } // may be freed by another item of this request
                pending.Remove(it);
                progress = true;
                if (skip is not null) { results[it.Id] = ("skipped", skip); continue; }
                if (it.Action == DispositionActions.Review)
                {
                    var months = published.Rules.First(r => r.Code == it.ClassCode).RetentionMonths;
                    await using var def = new NpgsqlCommand("INSERT INTO rec.review_deferral (id, object_kind, object_id, retain_until, decided_at, decided_by) VALUES (@id, @k, @o, @u, @now, @by)", conn, tx);
                    def.Parameters.AddWithValue("id", Guid.CreateVersion7()); def.Parameters.AddWithValue("k", it.Kind); def.Parameters.AddWithValue("o", it.ObjectId);
                    def.Parameters.AddWithValue("u", today.AddMonths(months > 0 ? months : 12)); def.Parameters.AddWithValue("now", now); def.Parameters.AddWithValue("by", s.Username ?? s.Id);
                    await def.ExecuteNonQueryAsync(ct);
                    results[it.Id] = ("retained", null);
                    continue;
                }
                disposed.Add(new Disposed(it, facts!, Guid.CreateVersion7(), await ContentOfAsync(conn, tx, it, ct)));
                results[it.Id] = (it.Action == DispositionActions.Destroy ? "destroyed" : "transferred", null);
            }
        } while (progress && pending.Count > 0);
        foreach (var it in pending) results[it.Id] = ("skipped", SkipCodes.Referenced);

        string? certificateNumber = null; string? archiveNumber = null;
        if (disposed.Count > 0)
        {
            var certId = Guid.CreateVersion7();
            certificateNumber = RecordsStore.NextNumber("COD-", await RecordsStore.NextValAsync(conn, tx, "rec.certificate_seq", ct));
            var body = await BuildCertificateBodyAsync(conn, tx, certificateNumber, number, s, now, published.Version, disposed, results, ct);
            var bodySha = RecordsSigner.Sha256Hex(Encoding.UTF8.GetBytes(body));
            var signature = signer.Sign(RecordsSigner.CertificateDomain, bodySha);
            await using (var ins = new NpgsqlCommand(
                $"""
                INSERT INTO rec.certificate (id, number, disposition_id, body, body_sha256, key_id, signature, issued_at, issued_by, object_count, {MarkingColumns.InsertColumns})
                VALUES (@id, @n, @d, @body, @hash, @kid, @sig, @now, @by, @oc, {MarkingColumns.InsertValues})
                """, conn, tx))
            {
                ins.Parameters.AddWithValue("id", certId); ins.Parameters.AddWithValue("n", certificateNumber); ins.Parameters.AddWithValue("d", dispositionId);
                ins.Parameters.AddWithValue("body", body); ins.Parameters.AddWithValue("hash", Convert.FromHexString(bodySha)); ins.Parameters.AddWithValue("kid", signer.KeyId); ins.Parameters.AddWithValue("sig", signature);
                ins.Parameters.AddWithValue("now", now); ins.Parameters.AddWithValue("by", s.Username ?? s.Id); ins.Parameters.AddWithValue("oc", disposed.Count);
                RecordsStore.AddMarkings(ins, peek.Markings);
                await ins.ExecuteNonQueryAsync(ct);
            }
            foreach (var d in disposed)
            {
                await using var ins = new NpgsqlCommand(
                    $"INSERT INTO rec.tombstone (id, object_kind, object_id, object_number, disposition_id, certificate_id, action, tombstoned_at, tombstoned_by, {MarkingColumns.InsertColumns}) VALUES (@id, @k, @o, @n, @d, @c, @a, @now, @by, {MarkingColumns.InsertValues})", conn, tx);
                ins.Parameters.AddWithValue("id", d.TombstoneId); ins.Parameters.AddWithValue("k", d.Item.Kind); ins.Parameters.AddWithValue("o", d.Item.ObjectId); ins.Parameters.AddWithValue("n", d.Item.Number);
                ins.Parameters.AddWithValue("d", dispositionId); ins.Parameters.AddWithValue("c", certId); ins.Parameters.AddWithValue("a", d.Item.Action);
                ins.Parameters.AddWithValue("now", now); ins.Parameters.AddWithValue("by", s.Username ?? s.Id);
                RecordsStore.AddMarkings(ins, d.Facts.Markings);
                await ins.ExecuteNonQueryAsync(ct);
                await audit.AppendAsync(tx, RecordsStore.Entry(s, "records.dispose", AuditOutcome.Success, d.Item.Kind, d.Item.Number, corr,
                    ("action", d.Item.Action), ("class", d.Item.ClassCode), ("certificate", certificateNumber), ("disposition", number)), ct);
            }
            if (package is not null && disposed.Any(d => d.Item.Action == DispositionActions.TransferArchive))
            {
                await archives.RegisterAsync(conn, tx, package, s.Username ?? s.Id, dispositionId, ct);
                archiveNumber = package.Number;
            }
        }
        foreach (var (itemId, (result, code)) in results)
        {
            await using var upd = new NpgsqlCommand("UPDATE rec.disposition_item SET result = @r, result_code = @c WHERE id = @id", conn, tx);
            upd.Parameters.AddWithValue("r", result); upd.Parameters.AddWithValue("c", (object?)code ?? DBNull.Value); upd.Parameters.AddWithValue("id", itemId);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await using (var upd = new NpgsqlCommand("UPDATE rec.disposition SET status = 'executed', executed_at = @now, executed_by = @by, revision = revision + 1, updated_at = @now WHERE id = @id", conn, tx))
        {
            upd.Parameters.AddWithValue("now", now); upd.Parameters.AddWithValue("by", s.Username ?? s.Id); upd.Parameters.AddWithValue("id", dispositionId);
            await upd.ExecuteNonQueryAsync(ct);
        }
        int Count(string r) => results.Values.Count(v => v.Result == r);
        var result2 = new ExecutionResult(number, certificateNumber, Count("destroyed"), Count("transferred"), Count("retained"), Count("skipped"), archiveNumber);
        await audit.AppendAsync(tx, RecordsStore.Entry(s, "records.disposition_execute", AuditOutcome.Success, ObjectType, number, corr,
            ("destroyed", result2.Destroyed.ToString(CultureInfo.InvariantCulture)), ("transferred", result2.Transferred.ToString(CultureInfo.InvariantCulture)), ("retained", result2.Retained.ToString(CultureInfo.InvariantCulture)),
            ("skipped", result2.Skipped.ToString(CultureInfo.InvariantCulture)), ("certificate", certificateNumber)), ct);
        await tx.CommitAsync(ct);
        return Outcome<ExecutionResult>.Ok(result2);
    }

    /// <summary>Whether another live object still depends on this one (FEAT-REC-8): a document linked from a part that is not disposed of, a part used in the structure of a part that is not disposed of.</summary>
    private static async Task<bool> IsReferencedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Item it, Guid[] disposedNow, CancellationToken ct)
    {
        var sql = it.Kind == RecordKinds.Document
            ? """
              SELECT EXISTS (SELECT 1 FROM plm.part_document_link l JOIN plm.part_iteration pi ON pi.id = l.part_iteration_id JOIN plm.part_revision pr ON pr.id = pi.revision_id
                             WHERE l.document_master_id = @id AND NOT rec.is_tombstoned('part', pr.master_id) AND pr.master_id <> ALL (@gone))
              """
            : """
              SELECT EXISTS (SELECT 1 FROM plm.bom_usage u JOIN plm.part_iteration pi ON pi.id = u.parent_iteration_id JOIN plm.part_revision pr ON pr.id = pi.revision_id
                             WHERE u.child_master_id = @id AND pr.master_id <> @id AND NOT rec.is_tombstoned('part', pr.master_id) AND pr.master_id <> ALL (@gone))
              """;
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("id", it.ObjectId);
        cmd.Parameters.AddWithValue("gone", disposedNow);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<IReadOnlyList<(string Sha, long Size)>> ContentOfAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Item it, CancellationToken ct)
    {
        var list = new List<(string, long)>();
        if (it.Kind != RecordKinds.Document) return list;
        await using var cmd = new NpgsqlCommand(
            "SELECT DISTINCT f.sha256, f.size_bytes FROM plm.document_file f JOIN plm.document_iteration i ON i.id = f.iteration_id JOIN plm.document_revision rv ON rv.id = i.revision_id WHERE rv.master_id = @m ORDER BY f.sha256", conn, tx);
        cmd.Parameters.AddWithValue("m", it.ObjectId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetString(0), r.GetInt64(1)));
        return list;
    }

    private static async Task<string> BuildCertificateBodyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string certificate, string disposition, Subject s, DateTimeOffset now, int scheduleVersion, IReadOnlyList<Disposed> disposed,
        IReadOnlyDictionary<Guid, (string Result, string? Code)> results, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("format", "keel-records-certificate/1");
            w.WriteString("certificate", certificate);
            w.WriteString("disposition", disposition);
            w.WriteString("issuedAt", AuditChain.FormatTimestamp(now));
            w.WriteString("issuedBy", s.Username ?? s.Id);
            w.WriteNumber("scheduleVersion", scheduleVersion);
            w.WriteStartObject("approval");
            await using (var inst = new NpgsqlCommand("SELECT id FROM wf.instance WHERE object_key = @k AND object_type = 'disposition' AND status = 'Approved' ORDER BY completed_at DESC LIMIT 1", conn, tx))
            {
                inst.Parameters.AddWithValue("k", disposition);
                if (await inst.ExecuteScalarAsync(ct) is Guid instanceId)
                {
                    w.WriteString("instance", instanceId.ToString("D"));
                    w.WriteStartArray("signatures");
                    await using var sig = new NpgsqlCommand("SELECT signer_username, meaning_label, decision, signed_at, signature_hash FROM wf.signature WHERE instance_id = @i ORDER BY signed_at, id", conn, tx);
                    sig.Parameters.AddWithValue("i", instanceId);
                    await using var r = await sig.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        w.WriteStartObject();
                        w.WriteString("signer", r.GetString(0)); w.WriteString("meaning", r.GetString(1)); w.WriteString("decision", r.GetString(2));
                        w.WriteString("signedAt", AuditChain.FormatTimestamp(r.GetFieldValue<DateTimeOffset>(3))); w.WriteString("signatureHash", Convert.ToHexStringLower((byte[])r[4]));
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
            }
            w.WriteEndObject();
            w.WriteStartArray("objects");
            foreach (var d in disposed.OrderBy(d => d.Item.Kind, StringComparer.Ordinal).ThenBy(d => d.Item.Number, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("kind", d.Item.Kind); w.WriteString("number", d.Item.Number); w.WriteString("class", d.Item.ClassCode); w.WriteString("action", d.Item.Action);
                w.WriteString("tombstone", d.TombstoneId.ToString("D"));
                w.WriteStartArray("content");
                foreach (var (sha, size) in d.Content) { w.WriteStartObject(); w.WriteString("sha256", sha); w.WriteNumber("size", size); w.WriteEndObject(); }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("retained", results.Values.Count(v => v.Result == "retained"));
            w.WriteNumber("skipped", results.Values.Count(v => v.Result == "skipped"));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // ---------------------------------------------------------------- certificates

    public async Task<Outcome<IReadOnlyList<CertificateSummary>>> ListCertificatesAsync(Subject s, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanView(s)) return await DenyAsync<IReadOnlyList<CertificateSummary>>(s, "records.certificate_list", CertificateType, null, corr, AccessPolicyReasons.Role, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var f = AccessPolicy.BuildReadFilter(s, "c");
        var list = new List<CertificateSummary>();
        await using var cmd = new NpgsqlCommand($"SELECT c.number, d.number, c.issued_at, c.issued_by, c.object_count, {MarkingColumns.Select("c")} FROM rec.certificate c JOIN rec.disposition d ON d.id = c.disposition_id WHERE {f.Sql} ORDER BY c.issued_at DESC LIMIT 500", conn);
        foreach (var (k, v) in f.Parameters) cmd.Parameters.AddWithValue(k, v);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new CertificateSummary(r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetString(3), r.GetInt32(4), MarkingColumns.Read(r, 5).BannerText()));
        return Outcome<IReadOnlyList<CertificateSummary>>.Ok(list);
    }

    public async Task<Outcome<CertificateView>> GetCertificateAsync(Subject s, string number, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanView(s)) return await DenyAsync<CertificateView>(s, "records.certificate_read", CertificateType, number, corr, AccessPolicyReasons.Role, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        CertificateView? view = null; Guid id; byte[] hash; byte[] signature;
        await using (var cmd = new NpgsqlCommand($"SELECT c.id, c.number, d.number, c.issued_at, c.issued_by, c.body, c.body_sha256, c.key_id, c.signature, c.object_count, {MarkingColumns.Select("c")} FROM rec.certificate c JOIN rec.disposition d ON d.id = c.disposition_id WHERE c.number = @n", conn))
        {
            cmd.Parameters.AddWithValue("n", number);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Outcome<CertificateView>.NotFound();
            id = r.GetGuid(0); hash = (byte[])r[6]; signature = (byte[])r[8];
            var m = MarkingColumns.Read(r, 10);
            var body = r.GetString(5);
            var actual = RecordsSigner.Sha256Hex(Encoding.UTF8.GetBytes(body));
            var matches = string.Equals(actual, Convert.ToHexStringLower(hash), StringComparison.Ordinal);
            var ok = signer.Verify(RecordsSigner.CertificateDomain, actual, r.GetString(7), signature, out var known);
            view = new CertificateView(r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.GetString(4), body, Convert.ToHexStringLower(hash), r.GetString(7), r.GetInt32(9), m.BannerText(), m,
                new CertificateVerification(matches, known, ok, r.GetString(7)), []);
        }
        if (!AccessPolicy.Evaluate(s, view.Markings, PlmAction.Read).Permitted)
        {
            await audit.AppendAsync(RecordsStore.Entry(s, "records.certificate_read", AuditOutcome.Denied, CertificateType, number, corr, ("reasons", "markings")), ct);
            return Outcome<CertificateView>.NotFound();
        }
        var content = new List<ContentConfirmation>();
        foreach (var (sha, size) in ParseContent(view.Body))
        {
            await using var cmd = new NpgsqlCommand("SELECT vault_absent, still_referenced, confirmed_at FROM rec.destruction_confirmation WHERE certificate_id = @c AND sha256 = @h ORDER BY confirmed_at DESC LIMIT 1", conn);
            cmd.Parameters.AddWithValue("c", id); cmd.Parameters.AddWithValue("h", sha);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) content.Add(new ContentConfirmation(sha, size, r.GetBoolean(0) ? "destroyed" : "retained_shared", r.GetFieldValue<DateTimeOffset>(2)));
            else content.Add(new ContentConfirmation(sha, size, "pending", null));
        }
        return Outcome<CertificateView>.Ok(view with { Content = content });
    }

    internal static IReadOnlyList<(string Sha, long Size)> ParseContent(string body)
    {
        var list = new List<(string, long)>();
        using var doc = JsonDocument.Parse(body);
        foreach (var o in doc.RootElement.GetProperty("objects").EnumerateArray())
            foreach (var c in o.GetProperty("content").EnumerateArray())
                list.Add((c.GetProperty("sha256").GetString()!, c.GetProperty("size").GetInt64()));
        return [.. list.Distinct()];
    }

    /// <summary>
    /// Checks, for each content hash a certificate names, whether the vault no longer has it (the garbage collector ran after the grace period) and records a signed
    /// confirmation; content that another live object still references is recorded as retained (shared). Content still present and unreferenced stays pending.
    /// </summary>
    public async Task<Outcome<ConfirmReport>> ConfirmDestructionAsync(Subject s, string number, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanManage(s)) return await DenyAsync<ConfirmReport>(s, "records.destruction_confirm", CertificateType, number, corr, AccessPolicyReasons.Role, ct);
        if (!signer.Available) return Outcome<ConfirmReport>.Unavailable("No checkpoint signing key is configured.");
        if (store is null) return Outcome<ConfirmReport>.Unavailable("The vault is not available to this host.");
        var cert = await GetCertificateAsync(s, number, corr, ct);
        if (!cert.IsOk) return new Outcome<ConfirmReport>(cert.Kind, null, cert.Errors);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        Guid id;
        await using (var cmd = new NpgsqlCommand("SELECT id FROM rec.certificate WHERE number = @n", conn)) { cmd.Parameters.AddWithValue("n", number); id = (Guid)(await cmd.ExecuteScalarAsync(ct))!; }
        int confirmed = 0, shared = 0, pending = 0;
        foreach (var c in cert.Value!.Content.Where(c => c.State == "pending"))
        {
            bool absent;
            try { absent = !await store.ExistsAsync(c.Sha256, c.SizeBytes, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { pending++; continue; }
            var referenced = !absent && await StillReferencedAsync(conn, c.Sha256, ct);
            if (!absent && !referenced) { pending++; continue; }
            var at = AuditChain.Truncate(clock.GetUtcNow());
            var statement = string.Create(CultureInfo.InvariantCulture, $"{number}|{c.Sha256}|{(absent ? "absent" : "referenced")}|{AuditChain.FormatTimestamp(at)}");
            var hash = RecordsSigner.Sha256Hex(Encoding.UTF8.GetBytes(statement));
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var ins = new NpgsqlCommand("INSERT INTO rec.destruction_confirmation (id, certificate_id, sha256, vault_absent, still_referenced, confirmed_at, confirmed_by, key_id, signature) VALUES (@id, @c, @h, @a, @r, @at, @by, @kid, @sig)", conn, tx))
            {
                ins.Parameters.AddWithValue("id", Guid.CreateVersion7()); ins.Parameters.AddWithValue("c", id); ins.Parameters.AddWithValue("h", c.Sha256); ins.Parameters.AddWithValue("a", absent); ins.Parameters.AddWithValue("r", referenced);
                ins.Parameters.AddWithValue("at", at); ins.Parameters.AddWithValue("by", s.Username ?? s.Id); ins.Parameters.AddWithValue("kid", signer.KeyId); ins.Parameters.AddWithValue("sig", signer.Sign(RecordsSigner.ConfirmationDomain, hash));
                await ins.ExecuteNonQueryAsync(ct);
            }
            await audit.AppendAsync(tx, RecordsStore.Entry(s, "records.destruction_confirm", AuditOutcome.Success, CertificateType, number, corr, ("state", absent ? "destroyed" : "retained_shared")), ct);
            await tx.CommitAsync(ct);
            if (absent) confirmed++; else shared++;
        }
        return Outcome<ConfirmReport>.Ok(new ConfirmReport(number, confirmed, shared, pending));
    }

    /// <summary>Mirrors the vault registry's live references (VaultReferences.Sources); a test keeps the two in step.</summary>
    private static async Task<bool> StillReferencedAsync(NpgsqlConnection conn, string sha, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT EXISTS (SELECT 1 FROM plm.document_file f WHERE f.sha256 = @h AND NOT rec.iteration_content_released(f.iteration_id))
                OR EXISTS (SELECT 1 FROM plm.document_checkout_file f WHERE f.sha256 = @h)
                OR EXISTS (SELECT 1 FROM src.response_file f WHERE f.sha256 = @h)
                OR EXISTS (SELECT 1 FROM rec.archive a WHERE a.package_sha256 = @h)
            """, conn);
        cmd.Parameters.AddWithValue("h", sha);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
