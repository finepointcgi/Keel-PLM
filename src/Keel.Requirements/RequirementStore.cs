using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Requirements;

/// <summary>A specification with its displayed revision and that revision's latest iteration, with markings. Internal: callers outside the module get public views after an access check.</summary>
internal sealed record SpecHead(
    Guid Id, string Number, string Name, string? Product, string Description, string LifecycleId, int NextKey, Markings Markings, DateTimeOffset CreatedAt, string CreatedBy,
    Guid RevisionId, string Revision, bool IsLatest, string State, int Iteration, Guid IterationId, string IterationSha, int RequirementCount, string? IterationComment,
    DateTimeOffset IterationAt, string IterationBy)
{
    public string Label => $"{Revision}.{Iteration.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>SQL shared by the services of the module. Every statement here is a key lookup, a system view used inside an already authorized transaction, or takes an access filter from its caller.</summary>
internal static class RequirementStore
{
    public const string ObjectType = "reqspec";

    public static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string? number, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s),
        Action = action,
        Outcome = outcome,
        ObjectType = ObjectType,
        ObjectId = number,
        CorrelationId = correlationId,
        Details = AuditDetails.Of(details),
    };

    public static AuditEntry SystemEntry(string action, string? number, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.System,
        Action = action,
        Outcome = AuditOutcome.Success,
        ObjectType = ObjectType,
        ObjectId = number,
        Details = AuditDetails.Of(details),
    };

    public static string Who(Subject s) => s.Username ?? s.Id;
    public static string Fmt(int n) => n.ToString(CultureInfo.InvariantCulture);

    private const string HeadSelect =
        "s.id, s.number, s.name, s.product, s.description, s.lifecycle_id, s.next_key, " +
        "s.export_jurisdiction, s.eccn, s.usml_category, s.cui_categories, s.distribution_statement, s.controlling_office, s.distribution_reason, s.program, s.owning_org, s.export_authorizations, " +
        "s.created_at, s.created_by, r.id, r.revision, r.is_latest, r.lifecycle_state, r.latest_iteration, i.id, i.content_sha256, i.requirement_count, i.comment, i.created_at, i.created_by";

    private static SpecHead ReadHead(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6), MarkingColumns.Read(r, 7),
        r.GetFieldValue<DateTimeOffset>(17), r.GetString(18), r.GetGuid(19), r.GetString(20), r.GetBoolean(21), r.GetString(22), r.GetInt32(23), r.GetGuid(24), r.GetString(25), r.GetInt32(26),
        r.IsDBNull(27) ? null : r.GetString(27), r.GetFieldValue<DateTimeOffset>(28), r.GetString(29));

    /// <summary>The specification with this number at the named revision (the latest when <paramref name="revision"/> is null). A key lookup: the caller evaluates the access policy.</summary>
    public static async Task<SpecHead?> LoadSpecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string number, string? revision, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT {HeadSelect}
            FROM req.spec s
            JOIN req.spec_revision r ON r.spec_id = s.id AND ((@rev::text IS NULL AND r.is_latest) OR r.revision = @rev)
            JOIN req.spec_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            WHERE s.number = @n{(forUpdate ? " FOR UPDATE OF s" : "")}
            """, conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        cmd.Parameters.Add(new NpgsqlParameter("rev", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)revision ?? DBNull.Value });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadHead(r) : null;
    }

    public static async Task<SpecHead?> LoadSpecByRevisionAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid revisionId, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT {HeadSelect}
            FROM req.spec_revision r
            JOIN req.spec s ON s.id = r.spec_id
            JOIN req.spec_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            WHERE r.id = @id{(forUpdate ? " FOR UPDATE OF s" : "")}
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", revisionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadHead(r) : null;
    }

    public static async Task<SpecHead?> LoadSpecByIterationAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid iterationId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT r.id FROM req.spec_iteration i JOIN req.spec_revision r ON r.id = i.revision_id WHERE i.id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", iterationId);
        return await cmd.ExecuteScalarAsync(ct) is Guid rid ? await LoadSpecByRevisionAsync(conn, tx, rid, false, ct) : null;
    }

    /// <summary>Every requirement of an iteration, readable or not: the system's own view, used only inside an authorized transaction (copying forward, hashing, verifying a signed digest).</summary>
    public static async Task<List<WorkReq>> LoadWorkAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid iterationId, CancellationToken ct)
    {
        var list = new List<WorkReq>();
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT v.requirement_id, q.key, v.parent_requirement_id, v.ordinal, v.title, v.body, v.req_type, v.priority, v.verification_method, v.rationale, v.status, {MarkingColumns.Select("v")}
            FROM req.requirement_version v JOIN req.requirement q ON q.id = v.requirement_id
            WHERE v.iteration_id = @i ORDER BY q.key
            """, conn, tx);
        cmd.Parameters.AddWithValue("i", iterationId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new WorkReq(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetInt32(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
                r.GetString(9), r.GetString(10), MarkingColumns.Read(r, 11)));
        return list;
    }

    /// <summary>The live digest of an iteration, recomputed from its rows (what the review signs).</summary>
    public static string HashWork(IReadOnlyList<WorkReq> work)
    {
        var byId = work.ToDictionary(w => w.Id);
        return RequirementRules.IterationSha(work.Select(w => (w.Key, w.ParentId is { } p && byId.TryGetValue(p, out var pw) ? pw.Key : null, w.Ordinal, w.Sha, w.Markings.BannerText())));
    }

    /// <summary>Writes one iteration with its requirement versions (new identities first). Returns the content digest.</summary>
    public static async Task<string> WriteIterationAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid specId, Guid revisionId, int iteration, string? comment, IReadOnlyList<WorkReq> work, DateTimeOffset now, string who, bool bumpRevision, CancellationToken ct)
    {
        var sha = HashWork(work);
        var iterationId = Guid.CreateVersion7();
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO req.spec_iteration (id, revision_id, iteration, comment, content_sha256, requirement_count, created_at, created_by) VALUES (@id, @r, @n, @c, @sha, @cnt, @at, @by)", conn, tx))
        {
            ins.Parameters.AddWithValue("id", iterationId);
            ins.Parameters.AddWithValue("r", revisionId);
            ins.Parameters.AddWithValue("n", iteration);
            ins.Parameters.Add(new NpgsqlParameter("c", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)comment ?? DBNull.Value });
            ins.Parameters.AddWithValue("sha", sha);
            ins.Parameters.AddWithValue("cnt", work.Count);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", who);
            await ins.ExecuteNonQueryAsync(ct);
        }
        foreach (var w in work.Where(w => w.IsNew))
        {
            await using var q = new NpgsqlCommand("INSERT INTO req.requirement (id, spec_id, key, created_at, created_by) VALUES (@id, @s, @k, @at, @by)", conn, tx);
            q.Parameters.AddWithValue("id", w.Id);
            q.Parameters.AddWithValue("s", specId);
            q.Parameters.AddWithValue("k", w.Key);
            q.Parameters.AddWithValue("at", now);
            q.Parameters.AddWithValue("by", who);
            await q.ExecuteNonQueryAsync(ct);
        }
        foreach (var w in work)
        {
            await using var v = new NpgsqlCommand(
                $"""
                INSERT INTO req.requirement_version (id, iteration_id, requirement_id, parent_requirement_id, ordinal, title, body, req_type, priority, verification_method, rationale, status, content_sha256,
                                                     {MarkingColumns.InsertColumns}, created_at, created_by)
                VALUES (@id, @i, @q, @p, @o, @title, @body, @type, @prio, @method, @rat, @status, @sha, {MarkingColumns.InsertValues}, @at, @by)
                """, conn, tx);
            v.Parameters.AddWithValue("id", Guid.CreateVersion7());
            v.Parameters.AddWithValue("i", iterationId);
            v.Parameters.AddWithValue("q", w.Id);
            v.Parameters.Add(new NpgsqlParameter("p", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)w.ParentId ?? DBNull.Value });
            v.Parameters.AddWithValue("o", w.Ordinal);
            v.Parameters.AddWithValue("title", w.Title);
            v.Parameters.AddWithValue("body", w.Text);
            v.Parameters.AddWithValue("type", w.Type);
            v.Parameters.AddWithValue("prio", w.Priority);
            v.Parameters.AddWithValue("method", w.Method);
            v.Parameters.AddWithValue("rat", w.Rationale);
            v.Parameters.AddWithValue("status", w.Status);
            v.Parameters.AddWithValue("sha", w.Sha);
            foreach (var (k, val) in MarkingColumns.Parameters(w.Markings)) v.Parameters.AddWithValue(k, val);
            v.Parameters.AddWithValue("at", now);
            v.Parameters.AddWithValue("by", who);
            await v.ExecuteNonQueryAsync(ct);
        }
        if (bumpRevision)
        {
            await using var up = new NpgsqlCommand("UPDATE req.spec_revision SET latest_iteration = @n WHERE id = @r", conn, tx);
            up.Parameters.AddWithValue("n", iteration);
            up.Parameters.AddWithValue("r", revisionId);
            await up.ExecuteNonQueryAsync(ct);
        }
        return sha;
    }

    public static async Task<Lifecycle> LifecycleAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string id, CancellationToken ct) =>
        await Lifecycle.LoadAsync(conn, tx, id, ct) ?? throw new InvalidOperationException("The specification lifecycle is missing.");

    // ---------------------------------------------------------------- objects a requirement links to (key lookups; the caller evaluates the access policy)

    /// <summary>A part, document or change by number: its markings and current revision label (null for a change).</summary>
    internal sealed record TargetRef(string Kind, string Number, string Name, string? Revision, Markings Markings);

    public static async Task<TargetRef?> LoadTargetAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string kind, string number, CancellationToken ct)
    {
        string sql = kind switch
        {
            LinkTargetKinds.Part => $"SELECT m.number, m.name, (SELECT r.revision FROM plm.part_revision r WHERE r.master_id = m.id AND r.is_latest), {MarkingColumns.Select("m")} FROM plm.part_master m WHERE m.number = @n",
            LinkTargetKinds.Document => $"SELECT m.number, m.name, (SELECT r.revision FROM plm.document_revision r WHERE r.master_id = m.id AND r.is_latest), {MarkingColumns.Select("m")} FROM plm.document_master m WHERE m.number = @n",
            LinkTargetKinds.Change => $"SELECT m.number, m.title, NULL::text, {MarkingColumns.Select("m")} FROM chg.change m WHERE m.number = @n",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new TargetRef(kind, r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), MarkingColumns.Read(r, 3)) : null;
    }

    public static async Task<LinkTypeInfo?> LoadLinkTypeAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string code, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT code, label, reverse_label, target_kind, coverage_role, is_system, is_active, sort_order FROM req.link_type WHERE code = @c", conn, tx);
        cmd.Parameters.AddWithValue("c", code);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new LinkTypeInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetBoolean(5), r.GetBoolean(6), r.GetInt32(7)) : null;
    }
}
