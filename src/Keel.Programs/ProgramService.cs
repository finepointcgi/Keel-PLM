using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Programs;

internal sealed record PStep(OutcomeKind Kind, IReadOnlyList<string> Errors, (string, string?)[] Details)
{
    public static PStep Done(params (string, string?)[] details) => new(OutcomeKind.Ok, [], details);
    public static PStep Fail(string message) => new(OutcomeKind.Conflict, [message], []);
    public static PStep Invalid(params string[] errors) => new(OutcomeKind.Invalid, errors, []);
    public static PStep Forbid(params string[] reasons) => new(OutcomeKind.Forbidden, reasons, []);
    public static PStep Missing() => new(OutcomeKind.NotFound, [], []);
}

/// <summary>
/// Programs (ADR-0043): the master record of a program marking code, its work breakdown, schedule, baselines, deliverables, gate reviews and views. Like every module
/// service each call evaluates the access policy (ADR-0004) and writes its audit event in the transaction of its change, including denials. The program page is marked with
/// the program's own code, so only members of the program read it; nothing in this module reads or writes membership. Audit events carry keys, numbers, states and counts,
/// never titles or descriptions.
/// </summary>
public sealed partial class ProgramService
{
    public const int MaxSearchLimit = 200;
    public const int UpcomingDays = 30;
    private readonly NpgsqlDataSource _db;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, IObjectAccess> _objects;
    private readonly INotificationSink? _notifications;
    private readonly IDomainEventSink? _events;

    internal ProgramService(NpgsqlDataSource db, IAuditLog audit, TimeProvider clock, IEnumerable<IObjectAccess> objects, INotificationSink? notifications, IDomainEventSink? events)
    {
        _db = db;
        _audit = audit;
        _clock = clock;
        _objects = objects.GroupBy(o => o.Kind).ToDictionary(g => g.Key, g => g.First());
        _notifications = notifications;
        _events = events;
    }

    internal IAuditLog Audit => _audit;
    private DateOnly Today => ProgramStore.Today(_clock);

    // ---------------------------------------------------------------- scaffolds

    /// <summary>Loads and locks the program, 404 when it cannot be read (denial audited), 403 when <paramref name="permission"/> is not permitted (audited), runs the body, audits, then loads the result.</summary>
    private async Task<Outcome<T>> MutateAsync<T>(Subject subject, string code, string action, string? corr, PlmAction permission, bool allowClosed,
        Func<PHead, NpgsqlConnection, NpgsqlTransaction, DateTimeOffset, Task<PStep>> body,
        Func<NpgsqlConnection, NpgsqlTransaction, PHead, Task<T?>> load, CancellationToken ct) where T : class
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await ProgramStore.LoadProgramAsync(conn, tx, code, forUpdate: true, ct);
        if (head is null) return Outcome<T>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await _audit.AppendAsync(tx, ProgramStore.Entry(subject, action, AuditOutcome.Denied, ProgramStore.ObjectType, head.Code, corr, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.NotFound();
        }
        var decision = permission == PlmAction.Read ? read : AccessPolicy.Evaluate(subject, head.Markings, permission);
        if (!decision.Permitted)
        {
            await _audit.AppendAsync(tx, ProgramStore.Entry(subject, action, AuditOutcome.Denied, ProgramStore.ObjectType, head.Code, corr, ("reasons", string.Join(",", decision.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.Forbidden(decision.Reasons);
        }
        if (head.Closed && !allowClosed) return Outcome<T>.Conflict($"The program is {ProgramStatuses.Label(head.Status).ToLowerInvariant()} and takes no further changes. Change its status first.");
        PStep step;
        try { step = await body(head, conn, tx, _clock.GetUtcNow()); }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.CheckViolation or PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.InsufficientPrivilege)
        {
            await tx.RollbackAsync(ct);
            return Outcome<T>.Conflict(e.MessageText);
        }
        if (step.Kind != OutcomeKind.Ok)
        {
            await tx.RollbackAsync(ct);
            if (step.Kind == OutcomeKind.Forbidden)
                await _audit.AppendAsync(ProgramStore.Entry(subject, action, AuditOutcome.Denied, ProgramStore.ObjectType, head.Code, corr, ("reasons", string.Join(",", step.Errors))), ct);
            return step.Kind switch
            {
                OutcomeKind.Forbidden => Outcome<T>.Forbidden(step.Errors),
                OutcomeKind.Invalid => Outcome<T>.Invalid(step.Errors),
                OutcomeKind.NotFound => Outcome<T>.NotFound(),
                _ => Outcome<T>.Conflict(step.Errors[0]),
            };
        }
        await _audit.AppendAsync(tx, ProgramStore.Entry(subject, action, AuditOutcome.Success, ProgramStore.ObjectType, head.Code, corr, step.Details), ct);
        var fresh = await ProgramStore.LoadProgramAsync(conn, tx, head.Code, false, ct) ?? head;
        var detail = await load(conn, tx, fresh);
        await tx.CommitAsync(ct);
        return Outcome<T>.Ok(detail!);
    }

    /// <summary>Single-program read with the same 404 rule; <paramref name="load"/> runs only for a reader.</summary>
    private async Task<Outcome<T>> ReadAsync<T>(Subject subject, string code, string action, string? corr, Func<NpgsqlConnection, NpgsqlTransaction, PHead, Task<T>> load,
        Func<T, (string, string?)[]> details, CancellationToken ct) where T : class
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await ProgramStore.LoadProgramAsync(conn, tx, code, false, ct);
        if (head is null) return Outcome<T>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await _audit.AppendAsync(tx, ProgramStore.Entry(subject, action, AuditOutcome.Denied, ProgramStore.ObjectType, head.Code, corr, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.NotFound();
        }
        var result = await load(conn, tx, head);
        await _audit.AppendAsync(tx, ProgramStore.Entry(subject, action, AuditOutcome.Success, ProgramStore.ObjectType, head.Code, corr, details(result)), ct);
        await tx.CommitAsync(ct);
        return Outcome<T>.Ok(result);
    }

    private static ProgramPermissions Permissions(Subject s, PHead h)
    {
        var d = AccessPolicy.Evaluate(s, h.Markings, PlmAction.Modify);
        return new ProgramPermissions(d.Permitted, d.Reasons);
    }

    // ---------------------------------------------------------------- program master

    private const string DetailSql =
        "SELECT p.id, p.code, p.name, p.description, p.customer_ref, p.contract_ref, p.status, p.manager_username, p.manager_name, p.start_date, p.end_date, p.actual_end_date, p.created_at, p.updated_at, ";

    private static async Task<ProgramDetail?> LoadDetailAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, PHead head, CancellationToken ct)
    {
        await using var cmd = ProgramStore.Cmd(conn, tx, $"{DetailSql}{MarkingColumns.Select("p")} FROM prg.program p WHERE p.id = @id");
        cmd.Parameters.AddWithValue("id", head.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new ProgramDetail(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
            ProgramStore.DateAt(r, 9), ProgramStore.DateAt(r, 10), ProgramStore.DateAt(r, 11), MarkingColumns.Read(r, 14), ProgramStore.Utc(r, 12), ProgramStore.Utc(r, 13), Permissions(subject, head));
    }

    public async Task<Outcome<ProgramDetail>> CreateAsync(Subject subject, CreateProgramCommand cmd, string? corr, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var code = cmd.Code?.Trim() ?? "";
        var name = cmd.Name?.Trim() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(code, @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")) errors.Add("The program code may contain letters, digits, '-', '_' and '.', and must start with a letter or digit.");
        if (name.Length is 0 or > 200) errors.Add("Name is required (at most 200 characters).");
        errors.AddRange(TextLimits(cmd.Description, cmd.CustomerRef, cmd.ContractRef));
        if (cmd.Start is { } s && cmd.End is { } e && s > e) errors.Add("The start date must not be after the end date.");
        if (cmd.Markings.Program is null || !string.Equals(cmd.Markings.Program, code, StringComparison.Ordinal))
            errors.Add("The markings of a program must name the program's own code as their program, so that only its members can read it.");
        if (errors.Count > 0) return Outcome<ProgramDetail>.Invalid(errors);

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // The caller must be allowed to modify an object with these markings: that needs membership of the program, which this module neither grants nor checks beyond the policy.
        var decision = AccessPolicy.Evaluate(subject, cmd.Markings, PlmAction.Modify);
        if (!decision.Permitted)
        {
            await _audit.AppendAsync(tx, ProgramStore.Entry(subject, "program.create", AuditOutcome.Denied, ProgramStore.ObjectType, code, corr, ("reasons", string.Join(",", decision.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<ProgramDetail>.Forbidden(decision.Reasons);
        }
        var manager = await ProgramStore.FindUserAsync(conn, tx, cmd.ManagerLogin?.Trim() ?? "", ct);
        if (manager is null) return Outcome<ProgramDetail>.Invalid([$"The user '{cmd.ManagerLogin}' was not found. A person appears in the directory after their first sign-in."]);
        if (!AccessPolicy.Evaluate(manager.Subject, cmd.Markings, PlmAction.Read).Permitted)
            return Outcome<ProgramDetail>.Invalid([$"{manager.DisplayName} is not a member of program {code} and could not read it. Membership comes from the identity provider; it is not granted here."]);
        if (await ProgramStore.LoadProgramAsync(conn, tx, code, false, ct) is not null) return Outcome<ProgramDetail>.Conflict($"A program with code {code} already exists.");

        var id = Guid.CreateVersion7();
        var now = _clock.GetUtcNow();
        await using (var ins = ProgramStore.Cmd(conn, tx,
            $"""
            INSERT INTO prg.program (id, code, name, description, customer_ref, contract_ref, status, manager_sub, manager_username, manager_name, start_date, end_date, {MarkingColumns.InsertColumns},
                                     created_at, created_by, updated_at, updated_by)
            VALUES (@id, @code, @name, @desc, @cust, @contract, 'Planning', @msub, @muser, @mname, @start, @end, {MarkingColumns.InsertValues}, @at, @by, @at, @by)
            """))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("code", code);
            ins.Parameters.AddWithValue("name", name);
            ins.Parameters.AddWithValue("desc", cmd.Description?.Trim() ?? "");
            ins.Parameters.AddWithValue("cust", cmd.CustomerRef?.Trim() ?? "");
            ins.Parameters.AddWithValue("contract", cmd.ContractRef?.Trim() ?? "");
            ins.Parameters.AddWithValue("msub", manager.Sub);
            ins.Parameters.AddWithValue("muser", manager.Username);
            ins.Parameters.AddWithValue("mname", manager.DisplayName);
            ins.Parameters.AddWithValue("start", ProgramStore.OptDate(cmd.Start));
            ins.Parameters.AddWithValue("end", ProgramStore.OptDate(cmd.End));
            foreach (var (k, v) in MarkingColumns.Parameters(cmd.Markings)) ins.Parameters.AddWithValue(k, v);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", ProgramStore.Who(subject));
            await ins.ExecuteNonQueryAsync(ct);
        }
        if (_notifications is not null) await _notifications.AutoWatchAsync(conn, tx, subject.Id, ProgramStore.ObjectType, code, ct);
        await _audit.AppendAsync(tx, ProgramStore.Entry(subject, "program.create", AuditOutcome.Success, ProgramStore.ObjectType, code, corr, ("markings", cmd.Markings.BannerText()), ("manager", manager.Username)), ct);
        var head = (await ProgramStore.LoadProgramAsync(conn, tx, code, false, ct))!;
        var detail = await LoadDetailAsync(conn, tx, subject, head, ct);
        await tx.CommitAsync(ct);
        return Outcome<ProgramDetail>.Ok(detail!);
    }

    private static IEnumerable<string> TextLimits(string? description, string? customer, string? contract)
    {
        if ((description?.Length ?? 0) > 8000) yield return "The description must be at most 8000 characters.";
        if ((customer?.Trim().Length ?? 0) > 200) yield return "The customer reference must be at most 200 characters.";
        if ((contract?.Trim().Length ?? 0) > 200) yield return "The contract reference must be at most 200 characters.";
    }

    public Task<Outcome<ProgramDetail>> GetAsync(Subject subject, string code, string? corr, CancellationToken ct = default) =>
        ReadAsync(subject, code, "program.read", corr, async (conn, tx, head) => (await LoadDetailAsync(conn, tx, subject, head, ct))!, _ => [], ct);

    public Task<Outcome<ProgramDetail>> UpdateAsync(Subject subject, string code, UpdateProgramCommand cmd, string? corr, CancellationToken ct = default) =>
        MutateAsync<ProgramDetail>(subject, code, "program.update", corr, PlmAction.Modify, allowClosed: true, async (head, conn, tx, now) =>
        {
            var errors = new List<string>();
            var name = cmd.Name?.Trim() ?? "";
            if (name.Length is 0 or > 200) errors.Add("Name is required (at most 200 characters).");
            errors.AddRange(TextLimits(cmd.Description, cmd.CustomerRef, cmd.ContractRef));
            if (!ProgramStatuses.All.Contains(cmd.Status)) errors.Add($"Status must be one of: {string.Join(", ", ProgramStatuses.All)}.");
            if (cmd.Start is { } s && cmd.End is { } e && s > e) errors.Add("The start date must not be after the end date.");
            if (errors.Count > 0) return PStep.Invalid([.. errors]);
            var manager = await ProgramStore.FindUserAsync(conn, tx, cmd.ManagerLogin?.Trim() ?? "", ct);
            if (manager is null) return PStep.Invalid($"The user '{cmd.ManagerLogin}' was not found.");
            if (!AccessPolicy.Evaluate(manager.Subject, head.Markings, PlmAction.Read).Permitted)
                return PStep.Invalid($"{manager.DisplayName} is not a member of program {head.Code}. Membership comes from the identity provider; it is not granted here.");
            await using var up = ProgramStore.Cmd(conn, tx,
                """
                UPDATE prg.program SET name = @name, description = @desc, customer_ref = @cust, contract_ref = @contract, status = @status, manager_sub = @msub, manager_username = @muser,
                       manager_name = @mname, start_date = @start, end_date = @end, actual_end_date = @aend, updated_at = @at, updated_by = @by WHERE id = @id
                """);
            up.Parameters.AddWithValue("name", name);
            up.Parameters.AddWithValue("desc", cmd.Description?.Trim() ?? "");
            up.Parameters.AddWithValue("cust", cmd.CustomerRef?.Trim() ?? "");
            up.Parameters.AddWithValue("contract", cmd.ContractRef?.Trim() ?? "");
            up.Parameters.AddWithValue("status", cmd.Status);
            up.Parameters.AddWithValue("msub", manager.Sub);
            up.Parameters.AddWithValue("muser", manager.Username);
            up.Parameters.AddWithValue("mname", manager.DisplayName);
            up.Parameters.AddWithValue("start", ProgramStore.OptDate(cmd.Start));
            up.Parameters.AddWithValue("end", ProgramStore.OptDate(cmd.End));
            up.Parameters.AddWithValue("aend", ProgramStore.OptDate(cmd.ActualEnd));
            up.Parameters.AddWithValue("at", now);
            up.Parameters.AddWithValue("by", ProgramStore.Who(subject));
            up.Parameters.AddWithValue("id", head.Id);
            await up.ExecuteNonQueryAsync(ct);
            return PStep.Done(("from", head.Status), ("to", cmd.Status), ("manager", manager.Username));
        }, (conn, tx, head) => LoadDetailAsync(conn, tx, subject, head, ct), ct);

    public static SortMap SortKeys { get; } = new(("code", ["p.code"]), ("name", ["lower(p.name)"]), ("status", ["p.status"]), ("manager", ["lower(p.manager_name)"]), ("start", ["p.start_date"]), ("end", ["p.end_date"]));

    public async Task<ProgramSearchResult> SearchAsync(Subject subject, ProgramSearchQuery query, string? corr, CancellationToken ct = default)
    {
        var limit = Math.Clamp(query.Limit, 1, MaxSearchLimit);
        var offset = Math.Max(0, query.Offset);
        var q = (query.Text ?? "").Trim();
        var like = "%" + q.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%";
        var filter = AccessPolicy.BuildReadFilter(subject, "p");
        var items = new List<ProgramSummary>();
        var total = 0;
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT p.code, p.name, p.status, p.manager_name, p.start_date, p.end_date, {MarkingColumns.Select("p")}, count(*) OVER ()
            FROM prg.program p
            WHERE {filter.Sql} AND (@status::text IS NULL OR p.status = @status) AND (@q = '' OR p.code ILIKE @like OR p.name ILIKE @like)
            ORDER BY {SortKeys.OrderBy(query.Sort, "p.code", "p.code")}
            LIMIT @limit OFFSET @offset
            """, conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("status", (object?)query.Status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("q", q);
            cmd.Parameters.AddWithValue("like", like);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                items.Add(new ProgramSummary(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), ProgramStore.DateAt(r, 4), ProgramStore.DateAt(r, 5), MarkingColumns.Read(r, 6)));
                total = (int)r.GetInt64(6 + MarkingColumns.Count);
            }
        }
        await _audit.AppendAsync(tx, ProgramStore.Entry(subject, "program.search", AuditOutcome.Success, ProgramStore.ObjectType, null, corr,
            ("status", query.Status), ("returned", ProgramStore.Int(items.Count)), ("codes", string.Join(",", items.Select(i => i.Code)))), ct);
        await tx.CommitAsync(ct);
        return new ProgramSearchResult(items, total, Markings.AggregateBanner(items.Select(i => i.Markings)));
    }
}
