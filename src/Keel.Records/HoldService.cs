using System.Globalization;
using System.Text.Json;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Records;

/// <summary>
/// Legal holds (ADR-0041). A hold names a scope (objects, a program, or a query); its members are materialized when it is placed and again at every
/// re-evaluation (a hold only grows until it is released). A hold overrides disposition everywhere: the queue leaves held objects out, execution skips them
/// under the same lock holds are placed with, a database trigger refuses a tombstone for them, and the vault garbage collector keeps their content.
/// <para>
/// Disclosure rules: only records roles see holds at all; program and query scopes are evaluated over ALL objects (a hold must protect what its author cannot
/// see) but every list and count shown to a person includes only objects that person may read, so a hold never reveals that a hidden object exists or is held.
/// A person who names an object they cannot read gets the same answer as for one that does not exist.
/// </para>
/// </summary>
public sealed class HoldService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock)
{
    private const string ObjectType = "legal_hold";
    private const int MaxNamedObjects = 500;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private async Task<Outcome<T>> DenyAsync<T>(Subject s, string action, string? id, string? corr, CancellationToken ct)
    {
        await audit.AppendAsync(RecordsStore.Entry(s, action, AuditOutcome.Denied, ObjectType, id, corr, ("reasons", AccessPolicyReasons.Role)), ct);
        return Outcome<T>.Forbidden([AccessPolicyReasons.Role]);
    }

    // ---------------------------------------------------------------- place

    public async Task<Outcome<HoldView>> PlaceAsync(Subject s, PlaceHoldCommand cmd, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanManage(s)) return await DenyAsync<HoldView>(s, "records.hold_place", null, corr, ct);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(cmd.Name) || cmd.Name.Length > 200) errors.Add("The hold needs a name of at most 200 characters.");
        if (string.IsNullOrWhiteSpace(cmd.Reason) || cmd.Reason.Length > 2000) errors.Add("The hold needs a reason of at most 2000 characters.");
        errors.AddRange(ValidateScope(cmd.Scope));
        if (errors.Count > 0) return Outcome<HoldView>.Invalid(errors);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        List<(string Kind, Guid Id)> named = [];
        if (cmd.Scope.Kind == HoldScopeKinds.Objects)
        {
            var missing = new List<string>();
            foreach (var o in cmd.Scope.Objects!.DistinctBy(o => (o.Kind, o.Number)))
            {
                var f = await RecordsStore.LoadFactsAsync(conn, tx, o.Kind, o.Number, ct);
                if (f is null || !AccessPolicy.Evaluate(s, f.Markings, PlmAction.Read).Permitted) missing.Add($"{RecordKinds.Label(o.Kind)} {o.Number} was not found.");
                else named.Add((o.Kind, f.Id));
            }
            if (missing.Count > 0)
            {
                await audit.AppendAsync(tx, RecordsStore.Entry(s, "records.hold_place", AuditOutcome.Denied, ObjectType, null, corr, ("reasons", "object.not_found")), ct);
                await tx.CommitAsync(ct);
                return Outcome<HoldView>.Invalid(missing);
            }
        }

        await RecordsStore.TakeGateAsync(conn, tx, ct);
        var id = Guid.CreateVersion7();
        var number = RecordsStore.NextNumber("LH-", await RecordsStore.NextValAsync(conn, tx, "rec.hold_seq", ct));
        var now = clock.GetUtcNow();
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO rec.hold (id, number, name, reason, scope_kind, scope, owning_org, placed_at, placed_by) VALUES (@id, @n, @name, @reason, @sk, @scope::jsonb, @org, @now, @by)", conn, tx))
        {
            ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("n", number);
            ins.Parameters.AddWithValue("name", cmd.Name.Trim()); ins.Parameters.AddWithValue("reason", cmd.Reason.Trim());
            ins.Parameters.AddWithValue("sk", cmd.Scope.Kind); ins.Parameters.AddWithValue("scope", JsonSerializer.Serialize(cmd.Scope, Json));
            ins.Parameters.AddWithValue("org", RecordsStore.OrgOf(s)); ins.Parameters.AddWithValue("now", now); ins.Parameters.AddWithValue("by", s.Username ?? s.Id);
            await ins.ExecuteNonQueryAsync(ct);
        }
        var members = await InsertMembersAsync(conn, tx, id, cmd.Scope, named, now, "placement", ct);
        await audit.AppendAsync(tx, RecordsStore.Entry(s, "records.hold_place", AuditOutcome.Success, ObjectType, number, corr,
            ("scope", cmd.Scope.Kind), ("members", members.ToString(CultureInfo.InvariantCulture))), ct);
        var view = await LoadViewAsync(conn, tx, s, number, ct);
        await tx.CommitAsync(ct);
        return Outcome<HoldView>.Ok(view!);
    }

    internal static IEnumerable<string> ValidateScope(HoldScope scope)
    {
        switch (scope.Kind)
        {
            case HoldScopeKinds.Objects:
                if (scope.Objects is null || scope.Objects.Count == 0) yield return "Name at least one object.";
                else if (scope.Objects.Count > MaxNamedObjects) yield return $"Name at most {MaxNamedObjects} objects; use a program or a query for more.";
                else if (scope.Objects.Any(o => !RecordKinds.IsKnown(o.Kind) || string.IsNullOrWhiteSpace(o.Number))) yield return "Every named object needs a kind (document or part) and a number.";
                break;
            case HoldScopeKinds.Program:
                if (string.IsNullOrWhiteSpace(scope.Program) || scope.Program.Length > 64) yield return "Enter the program code.";
                break;
            case HoldScopeKinds.Query:
                var q = scope.Query;
                if (q is null || (q.Program is null && q.ObjectType is null && q.NumberPrefix is null && q.Jurisdiction is null && q.CuiCategory is null && q.CreatedBefore is null && q.CreatedAfter is null))
                    yield return "A query hold needs at least one criterion.";
                else
                {
                    if (q.Kind is not null && !RecordKinds.IsKnown(q.Kind)) yield return "The object kind must be document or part.";
                    if (q.Jurisdiction is not null and not ("None" or "Ear" or "Itar")) yield return "The export jurisdiction must be None, Ear or Itar.";
                }
                break;
            default:
                yield return "Choose a scope: named objects, a program or a query.";
                break;
        }
    }

    /// <summary>Inserts members for the scope; returns how many were new. Program and query scopes see every object (no read filter), by design.</summary>
    private static async Task<int> InsertMembersAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid holdId, HoldScope scope, IReadOnlyList<(string Kind, Guid Id)> named, DateTimeOffset now, string how, CancellationToken ct)
    {
        var inserted = 0;
        if (scope.Kind == HoldScopeKinds.Objects)
        {
            foreach (var (kind, id) in named)
            {
                await using var cmd = new NpgsqlCommand("INSERT INTO rec.hold_member (hold_id, object_kind, object_id, added_at, added_by) VALUES (@h, @k, @id, @now, @how) ON CONFLICT DO NOTHING", conn, tx);
                cmd.Parameters.AddWithValue("h", holdId); cmd.Parameters.AddWithValue("k", kind); cmd.Parameters.AddWithValue("id", id);
                cmd.Parameters.AddWithValue("now", now); cmd.Parameters.AddWithValue("how", how);
                inserted += await cmd.ExecuteNonQueryAsync(ct);
            }
            return inserted;
        }
        var q = scope.Kind == HoldScopeKinds.Program ? new HoldQuery(Program: scope.Program!.Trim()) : scope.Query!;
        foreach (var kind in q.Kind is null ? RecordKinds.All : [q.Kind])
        {
            var (table, typeColumn) = kind == RecordKinds.Document ? ("plm.document_master", "document_type") : ("plm.part_master", "part_type");
            var where = new List<string>();
            await using var cmd = new NpgsqlCommand { Connection = conn, Transaction = tx };
            if (q.Program is not null) { where.Add("x.program = @program"); cmd.Parameters.AddWithValue("program", q.Program); }
            if (q.ObjectType is not null) { where.Add($"lower(x.{typeColumn}) = lower(@type)"); cmd.Parameters.AddWithValue("type", q.ObjectType); }
            if (q.NumberPrefix is not null) { where.Add("x.number LIKE @prefix ESCAPE '\\'"); cmd.Parameters.AddWithValue("prefix", EscapeLike(q.NumberPrefix) + "%"); }
            if (q.Jurisdiction is not null) { where.Add("x.export_jurisdiction = @jur"); cmd.Parameters.AddWithValue("jur", q.Jurisdiction); }
            if (q.CuiCategory is not null) { where.Add("@cui = ANY (x.cui_categories)"); cmd.Parameters.AddWithValue("cui", q.CuiCategory); }
            if (q.CreatedBefore is { } before) { where.Add("x.created_at < @before"); cmd.Parameters.AddWithValue("before", new DateTimeOffset(before.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)); }
            if (q.CreatedAfter is { } afterDate) { where.Add("x.created_at >= @after"); cmd.Parameters.AddWithValue("after", new DateTimeOffset(afterDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)); }
            cmd.CommandText = $"INSERT INTO rec.hold_member (hold_id, object_kind, object_id, added_at, added_by) SELECT @h, '{kind}', x.id, @now, @how FROM {table} x WHERE {string.Join(" AND ", where)} ON CONFLICT DO NOTHING";
            cmd.Parameters.AddWithValue("h", holdId); cmd.Parameters.AddWithValue("now", now); cmd.Parameters.AddWithValue("how", how);
            inserted += await cmd.ExecuteNonQueryAsync(ct);
        }
        return inserted;
    }

    private static string EscapeLike(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    // ---------------------------------------------------------------- re-evaluate

    /// <summary>Re-evaluates one hold's program or query scope and adds objects that match now. Returns how many members were added (visible only to records managers).</summary>
    public async Task<Outcome<int>> ReevaluateAsync(Subject s, string number, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanManage(s)) return await DenyAsync<int>(s, "records.hold_reevaluate", number, corr, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RecordsStore.TakeGateAsync(conn, tx, ct);
        Guid id; string scopeJson; DateTimeOffset? released;
        await using (var cmd = new NpgsqlCommand("SELECT id, scope::text, released_at FROM rec.hold WHERE number = @n", conn, tx))
        {
            cmd.Parameters.AddWithValue("n", number);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Outcome<int>.NotFound();
            id = r.GetGuid(0); scopeJson = r.GetString(1); released = r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2);
        }
        if (released is not null) return Outcome<int>.Conflict($"{number} is released.");
        var added = await ReevaluateOneAsync(conn, tx, id, scopeJson, clock.GetUtcNow(), ct);
        await audit.AppendAsync(tx, RecordsStore.Entry(s, "records.hold_reevaluate", AuditOutcome.Success, ObjectType, number, corr, ("added", added.ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return Outcome<int>.Ok(added);
    }

    private static async Task<int> ReevaluateOneAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, string scopeJson, DateTimeOffset now, CancellationToken ct)
    {
        var scope = JsonSerializer.Deserialize<HoldScope>(scopeJson, Json)!;
        return scope.Kind == HoldScopeKinds.Objects ? 0 : await InsertMembersAsync(conn, tx, id, scope, [], now, "reevaluation", ct);
    }

    /// <summary>Re-evaluates every active program and query hold, inside the caller's transaction (execution does this under the gate lock before it disposes of anything).</summary>
    internal static async Task ReevaluateAllActiveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, DateTimeOffset now, CancellationToken ct)
    {
        var holds = new List<(Guid Id, string Scope)>();
        await using (var cmd = new NpgsqlCommand("SELECT id, scope::text FROM rec.hold WHERE released_at IS NULL AND scope_kind <> 'objects'", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) holds.Add((r.GetGuid(0), r.GetString(1)));
        foreach (var (id, scope) in holds) await ReevaluateOneAsync(conn, tx, id, scope, now, ct);
    }

    /// <summary>For the background job: re-evaluate all active holds as the system.</summary>
    public async Task ReevaluateAllAsSystemAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RecordsStore.TakeGateAsync(conn, tx, ct);
        await ReevaluateAllActiveAsync(conn, tx, clock.GetUtcNow(), ct);
        await audit.AppendAsync(tx, new AuditEntry { Actor = ActorSnapshot.System, Action = "records.hold_reevaluate", Outcome = AuditOutcome.Success, ObjectType = ObjectType, ObjectId = "all" }, ct);
        await tx.CommitAsync(ct);
    }

    // ---------------------------------------------------------------- read


    private static async Task<HoldView?> LoadViewAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Subject s, string number, CancellationToken ct)
    {
        Guid id; HoldView? view = null;
        await using (var cmd = new NpgsqlCommand(
            "SELECT h.id, h.number, h.name, h.reason, h.scope_kind, h.scope::text, h.placed_at, h.placed_by, h.released_at, h.released_by, h.in_release, h.revision FROM rec.hold h WHERE h.number = @n", conn, tx))
        {
            cmd.Parameters.AddWithValue("n", number);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            id = r.GetGuid(0);
            var released = r.IsDBNull(8) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(8);
            view = new HoldView(r.GetString(1), r.GetString(2), r.GetString(3), released is null ? "active" : "released", r.GetString(4), ScopeText(r.GetString(5)),
                r.GetFieldValue<DateTimeOffset>(6), r.GetString(7), released, RecordsStore.Str(r, 9), r.GetBoolean(10), r.GetInt32(11), 0);
        }
        return view with { ReadableMembers = await CountReadableAsync(conn, tx, s, id, ct) };
    }

    internal static string ScopeText(string scopeJson)
    {
        var scope = JsonSerializer.Deserialize<HoldScope>(scopeJson, Json)!;
        switch (scope.Kind)
        {
            case HoldScopeKinds.Objects: return $"{scope.Objects!.Count.ToString(CultureInfo.InvariantCulture)} named object(s) when placed";
            case HoldScopeKinds.Program: return $"Every document and part of program {scope.Program}";
            default:
                var q = scope.Query!;
                var parts = new List<string>();
                if (q.Kind is not null) parts.Add($"kind {q.Kind}");
                if (q.Program is not null) parts.Add($"program {q.Program}");
                if (q.ObjectType is not null) parts.Add($"type {q.ObjectType}");
                if (q.NumberPrefix is not null) parts.Add($"number starts with {q.NumberPrefix}");
                if (q.Jurisdiction is not null) parts.Add($"jurisdiction {q.Jurisdiction}");
                if (q.CuiCategory is not null) parts.Add($"CUI {q.CuiCategory}");
                if (q.CreatedBefore is { } b) parts.Add($"created before {b:yyyy-MM-dd}");
                if (q.CreatedAfter is { } a) parts.Add($"created on or after {a:yyyy-MM-dd}");
                return "Query: " + string.Join(", ", parts);
        }
    }

    private static async Task<long> CountReadableAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Subject s, Guid holdId, CancellationToken ct)
    {
        long total = 0;
        foreach (var kind in RecordKinds.All)
        {
            var table = kind == RecordKinds.Document ? "plm.document_master" : "plm.part_master";
            var f = AccessPolicy.BuildReadFilter(s, "x");
            await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM rec.hold_member m JOIN {table} x ON x.id = m.object_id WHERE m.hold_id = @h AND m.object_kind = '{kind}' AND {f.Sql}", conn, tx);
            foreach (var (k, v) in f.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("h", holdId);
            total += (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        return total;
    }

    public async Task<Outcome<HoldView>> GetAsync(Subject s, string number, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanView(s)) return await DenyAsync<HoldView>(s, "records.hold_read", number, corr, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var v = await LoadViewAsync(conn, null, s, number, ct);
        return v is null ? Outcome<HoldView>.NotFound() : Outcome<HoldView>.Ok(v);
    }

    public async Task<Outcome<IReadOnlyList<HoldSummary>>> ListAsync(Subject s, bool includeReleased, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanView(s)) return await DenyAsync<IReadOnlyList<HoldSummary>>(s, "records.hold_list", null, corr, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var list = new List<HoldSummary>();
        await using var cmd = new NpgsqlCommand(
            $"SELECT number, name, released_at, scope_kind, placed_at, placed_by, in_release FROM rec.hold {(includeReleased ? "" : "WHERE released_at IS NULL")} ORDER BY placed_at DESC LIMIT 500", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new HoldSummary(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? "active" : "released", r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), r.GetString(5), r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2), r.GetBoolean(6)));
        return Outcome<IReadOnlyList<HoldSummary>>.Ok(list);
    }

    /// <summary>The members of a hold that the caller may read, newest first. Members the caller cannot read are not listed, counted or hinted at.</summary>
    public async Task<Outcome<IReadOnlyList<HoldMemberView>>> MembersAsync(Subject s, string number, int limit, int offset, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanView(s)) return await DenyAsync<IReadOnlyList<HoldMemberView>>(s, "records.hold_members", number, corr, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        Guid id;
        await using (var cmd = new NpgsqlCommand("SELECT id FROM rec.hold WHERE number = @n", conn))
        {
            cmd.Parameters.AddWithValue("n", number);
            if (await cmd.ExecuteScalarAsync(ct) is not Guid g) return Outcome<IReadOnlyList<HoldMemberView>>.NotFound();
            id = g;
        }
        var rows = new List<(string Kind, string Number, string Name, Markings M, DateTimeOffset At)>();
        foreach (var kind in RecordKinds.All)
        {
            var table = kind == RecordKinds.Document ? "plm.document_master" : "plm.part_master";
            var f = AccessPolicy.BuildReadFilter(s, "x");
            await using var cmd = new NpgsqlCommand($"SELECT x.number, x.name, {MarkingColumns.Select("x")}, m.added_at FROM rec.hold_member m JOIN {table} x ON x.id = m.object_id WHERE m.hold_id = @h AND m.object_kind = '{kind}' AND {f.Sql}", conn);
            foreach (var (k, v) in f.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("h", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add((kind, r.GetString(0), r.GetString(1), MarkingColumns.Read(r, 2), r.GetFieldValue<DateTimeOffset>(2 + MarkingColumns.Count)));
        }
        var page = rows.OrderByDescending(x => x.At).ThenBy(x => x.Number, StringComparer.Ordinal).Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 500))
            .Select(x => new HoldMemberView(x.Kind, x.Number, x.Name, x.M.BannerText(), x.M)).ToList();
        return Outcome<IReadOnlyList<HoldMemberView>>.Ok(page);
    }

    /// <summary>
    /// What an object's page may say about records management: shown only when the caller holds a records role AND can read the object (so it adds nothing
    /// about hidden objects), else null. Not audited per call: it is a derived view of holds that are audited when placed and released.
    /// </summary>
    public async Task<ObjectRecordsStatus?> StatusOfAsync(Subject s, string kind, string number, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanView(s) || !RecordKinds.IsKnown(kind)) return null;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var f = await RecordsStore.LoadFactsAsync(conn, null, kind, number, ct);
        if (f is null || !AccessPolicy.Evaluate(s, f.Markings, PlmAction.Read).Permitted) return null;
        var holds = new List<string>();
        await using (var cmd = new NpgsqlCommand("SELECT h.number FROM rec.hold_member m JOIN rec.hold h ON h.id = m.hold_id WHERE m.object_kind = @k AND m.object_id = @id AND h.released_at IS NULL ORDER BY h.number", conn))
        {
            cmd.Parameters.AddWithValue("k", kind); cmd.Parameters.AddWithValue("id", f.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) holds.Add(r.GetString(0));
        }
        string? cert = null;
        await using (var cmd = new NpgsqlCommand("SELECT c.number FROM rec.tombstone t JOIN rec.certificate c ON c.id = t.certificate_id WHERE t.object_kind = @k AND t.object_id = @id", conn))
        {
            cmd.Parameters.AddWithValue("k", kind); cmd.Parameters.AddWithValue("id", f.Id);
            cert = (string?)await cmd.ExecuteScalarAsync(ct);
        }
        string? code = null, name = null, disp = null; DateOnly? due = null;
        await using (var cmd = new NpgsqlCommand("SELECT c.class_code, (SELECT rc.name FROM rec.record_class rc WHERE rc.schedule_id = c.schedule_id AND rc.code = c.class_code), c.eligible_on, c.disposition FROM rec.classification c WHERE c.object_kind = @k AND c.object_id = @id", conn))
        {
            cmd.Parameters.AddWithValue("k", kind); cmd.Parameters.AddWithValue("id", f.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { code = r.GetString(0); name = RecordsStore.Str(r, 1); due = r.GetFieldValue<DateOnly>(2); disp = r.GetString(3); }
        }
        return new ObjectRecordsStatus(holds.Count > 0, holds, cert is not null, cert, code, name, due, disp);
    }
}
