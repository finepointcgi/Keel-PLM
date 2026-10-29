using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Compliance;

/// <summary>
/// The reference data of product compliance (ADR-0040): the substance master (CAS numbers), materials with their typical composition, and the regulatory lists in versions.
/// All of it is public regulatory data, readable by every internal role and changed only by administrators; each change is audited in the transaction that makes it.
/// A list version is edited as a draft and published only through the workflow engine (signatures): see <see cref="ComplianceListWorkflowGateway"/>. Published versions are
/// immutable (database triggers), so a change of a threshold is always a new version with its own effective date.
/// </summary>
public sealed class RegulatoryCatalogService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock)
{
    public static readonly SortMap SubstanceSortKeys = new(("number", ["s.number"]), ("cas", ["s.cas_number"]), ("name", ["lower(s.name)"]), ("mineral", ["s.mineral"]));

    private static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string type, string? id, string? corr, params (string, string?)[] details) =>
        ComplianceStore.Entry(s, action, outcome, type, id, corr, details);

    private async Task<Outcome<T>> DeniedAsync<T>(Subject subject, string action, string type, string? id, string? corr, IEnumerable<string> reasons, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, type, id, corr, ("reasons", string.Join(",", reasons))), ct);
        await tx.CommitAsync(ct);
        return Outcome<T>.Forbidden(reasons);
    }

    private Task<Outcome<T>> NeedRead<T>(Subject s, string action, string type, string? id, string? corr, CancellationToken ct) =>
        DeniedAsync<T>(s, action, type, id, corr, [AccessPolicy.RoleMissing], ct);

    private static bool CanRead(Subject s) => AccessPolicy.HasRoleFor(s, PlmAction.Read);
    private static bool IsAdmin(Subject s) => AccessPolicy.HasRoleFor(s, PlmAction.Administer);

    // ================================================================ substances

    private const string SubstanceColumns = "s.id, s.number, s.cas_number, s.name, s.ec_number, s.mineral, s.revision";

    private static SubstanceView ReadSubstance(NpgsqlDataReader r, int o = 0) =>
        new(r.GetGuid(o), r.GetString(o + 1), r.GetString(o + 2), r.GetString(o + 3), ComplianceStore.Str(r, o + 4), ComplianceStore.Str(r, o + 5), r.GetInt32(o + 6));

    public async Task<Outcome<SubstancePage>> SearchSubstancesAsync(Subject subject, SubstanceSearch q, string? correlationId, CancellationToken ct = default)
    {
        if (!CanRead(subject)) return await NeedRead<SubstancePage>(subject, "compliance.substance_search", "substance", null, correlationId, ct);
        var where = new List<string>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand { Connection = conn };
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            where.Add("(s.number ILIKE @like OR s.cas_number ILIKE @like OR s.name ILIKE @like)");
            cmd.Parameters.AddWithValue("like", ComplianceStore.Like(q.Text));
        }
        if (q.Mineral is not null)
        {
            where.Add("s.mineral = @mineral");
            cmd.Parameters.AddWithValue("mineral", q.Mineral);
        }
        var clause = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
        var order = SubstanceSortKeys.OrderBy(q.Sort, "s.number", "s.number");
        cmd.CommandText = $"SELECT {SubstanceColumns}, count(*) OVER () FROM cmp.substance s {clause} ORDER BY {order} LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("limit", Math.Clamp(q.Limit, 1, 200));
        cmd.Parameters.AddWithValue("offset", Math.Max(0, q.Offset));
        var items = new List<SubstanceView>();
        var total = 0;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) { items.Add(ReadSubstance(r)); total = (int)r.GetInt64(7); }
        if (items.Count == 0 && q.Offset > 0)
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM cmp.substance s {clause}", conn);
            foreach (NpgsqlParameter p in cmd.Parameters) if (p.ParameterName is "like" or "mineral") count.Parameters.AddWithValue(p.ParameterName, p.Value!);
            total = (int)(long)(await count.ExecuteScalarAsync(ct))!;
        }
        return Outcome<SubstancePage>.Ok(new SubstancePage(items, total));
    }

    public async Task<Outcome<SubstanceDetail>> GetSubstanceAsync(Subject subject, string numberOrCas, string? correlationId, CancellationToken ct = default)
    {
        if (!CanRead(subject)) return await NeedRead<SubstanceDetail>(subject, "compliance.substance_read", "substance", numberOrCas, correlationId, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        SubstanceView? s;
        await using (var cmd = new NpgsqlCommand($"SELECT {SubstanceColumns} FROM cmp.substance s WHERE s.number = @k OR s.cas_number = @k", conn))
        {
            cmd.Parameters.AddWithValue("k", numberOrCas.Trim());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            s = await r.ReadAsync(ct) ? ReadSubstance(r) : null;
        }
        if (s is null) return Outcome<SubstanceDetail>.NotFound();
        var versions = await ComplianceStore.LoadVersionsAsync(conn, null, ComplianceStore.Today(clock), null, ct);
        var lists = versions.Where(v => v.Entries.ContainsKey(s.Id)).Select(v => new SubstanceListUse(v.Code, v.Name, v.Version, v.Entries[s.Id].ThresholdPct)).ToList();
        var materials = new List<string>();
        await using (var cmd = new NpgsqlCommand("SELECT m.code FROM cmp.material m JOIN cmp.material_substance ms ON ms.material_id = m.id WHERE ms.substance_id = @s ORDER BY m.code", conn))
        {
            cmd.Parameters.AddWithValue("s", s.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) materials.Add(r.GetString(0));
        }
        return Outcome<SubstanceDetail>.Ok(new SubstanceDetail(s, lists, materials));
    }

    public async Task<Outcome<SubstanceView>> CreateSubstanceAsync(Subject subject, SubstanceInput input, string? correlationId, CancellationToken ct = default)
    {
        if (!IsAdmin(subject)) return await DeniedAsync<SubstanceView>(subject, "compliance.substance_create", "substance", null, correlationId, [AccessPolicy.RoleMissing], ct);
        var cas = ComplianceRules.Clean(input.CasNumber);
        var name = ComplianceRules.Clean(input.Name);
        var ec = ComplianceRules.Clean(input.EcNumber);
        var mineral = ComplianceRules.Clean(input.Mineral)?.ToLowerInvariant();
        var errors = ComplianceRules.ValidateSubstance(cas, name, ec, mineral);
        if (errors.Count > 0) return Outcome<SubstanceView>.Invalid(errors);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var dup = new NpgsqlCommand("SELECT 1 FROM cmp.substance WHERE cas_number = @c", conn, tx))
        {
            dup.Parameters.AddWithValue("c", cas!);
            if (await dup.ExecuteScalarAsync(ct) is not null) return Outcome<SubstanceView>.Conflict($"The substance {cas} exists already.");
        }
        var number = ComplianceStore.NextNumber("SUB-", await ComplianceStore.NextValAsync(conn, tx, "cmp.substance_seq", ct));
        var id = Guid.CreateVersion7();
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO cmp.substance (id, number, cas_number, name, ec_number, mineral, created_at, created_by) VALUES (@id, @n, @cas, @name, @ec, @min, @now, @by)", conn, tx))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("n", number);
            ins.Parameters.AddWithValue("cas", cas!);
            ins.Parameters.AddWithValue("name", name!);
            ins.Parameters.AddWithValue("ec", (object?)ec ?? DBNull.Value);
            ins.Parameters.AddWithValue("min", (object?)mineral ?? DBNull.Value);
            ins.Parameters.AddWithValue("now", clock.GetUtcNow());
            ins.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "compliance.substance_create", AuditOutcome.Success, "substance", number, correlationId, ("cas", cas), ("mineral", mineral)), ct);
        await tx.CommitAsync(ct);
        return Outcome<SubstanceView>.Ok(new SubstanceView(id, number, cas!, name!, ec, mineral, 1));
    }

    public async Task<Outcome<SubstanceView>> UpdateSubstanceAsync(Subject subject, string number, SubstanceInput input, int revision, string? correlationId, CancellationToken ct = default)
    {
        if (!IsAdmin(subject)) return await DeniedAsync<SubstanceView>(subject, "compliance.substance_update", "substance", number, correlationId, [AccessPolicy.RoleMissing], ct);
        var cas = ComplianceRules.Clean(input.CasNumber);
        var name = ComplianceRules.Clean(input.Name);
        var ec = ComplianceRules.Clean(input.EcNumber);
        var mineral = ComplianceRules.Clean(input.Mineral)?.ToLowerInvariant();
        var errors = ComplianceRules.ValidateSubstance(cas, name, ec, mineral);
        if (errors.Count > 0) return Outcome<SubstanceView>.Invalid(errors);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        SubstanceView? current;
        await using (var cmd = new NpgsqlCommand($"SELECT {SubstanceColumns} FROM cmp.substance s WHERE s.number = @n FOR UPDATE", conn, tx))
        {
            cmd.Parameters.AddWithValue("n", number);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            current = await r.ReadAsync(ct) ? ReadSubstance(r) : null;
        }
        if (current is null) return Outcome<SubstanceView>.NotFound();
        if (current.Revision != revision) return Outcome<SubstanceView>.Conflict("The substance was changed by someone else; reload and try again.");
        await using (var dup = new NpgsqlCommand("SELECT 1 FROM cmp.substance WHERE cas_number = @c AND id <> @id", conn, tx))
        {
            dup.Parameters.AddWithValue("c", cas!);
            dup.Parameters.AddWithValue("id", current.Id);
            if (await dup.ExecuteScalarAsync(ct) is not null) return Outcome<SubstanceView>.Conflict($"The substance {cas} exists already.");
        }
        await using (var upd = new NpgsqlCommand(
            "UPDATE cmp.substance SET cas_number = @cas, name = @name, ec_number = @ec, mineral = @min, revision = revision + 1, updated_at = @now, updated_by = @by WHERE id = @id", conn, tx))
        {
            upd.Parameters.AddWithValue("cas", cas!);
            upd.Parameters.AddWithValue("name", name!);
            upd.Parameters.AddWithValue("ec", (object?)ec ?? DBNull.Value);
            upd.Parameters.AddWithValue("min", (object?)mineral ?? DBNull.Value);
            upd.Parameters.AddWithValue("now", clock.GetUtcNow());
            upd.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            upd.Parameters.AddWithValue("id", current.Id);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "compliance.substance_update", AuditOutcome.Success, "substance", number, correlationId, ("cas", cas), ("mineral", mineral)), ct);
        await tx.CommitAsync(ct);
        return Outcome<SubstanceView>.Ok(new SubstanceView(current.Id, number, cas!, name!, ec, mineral, revision + 1));
    }

    // ================================================================ materials

    public async Task<Outcome<IReadOnlyList<MaterialView>>> ListMaterialsAsync(Subject subject, string? text, string? correlationId, CancellationToken ct = default)
    {
        if (!CanRead(subject)) return await NeedRead<IReadOnlyList<MaterialView>>(subject, "compliance.material_read", "material", null, correlationId, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var materials = new List<MaterialView>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT m.id, m.code, m.name, m.description, m.active, m.revision FROM cmp.material m WHERE (@t::text IS NULL OR m.code ILIKE @t OR m.name ILIKE @t) ORDER BY m.code", conn))
        {
            cmd.Parameters.AddWithValue("t", string.IsNullOrWhiteSpace(text) ? DBNull.Value : ComplianceStore.Like(text));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) materials.Add(new MaterialView(r.GetGuid(0), r.GetString(1), r.GetString(2), ComplianceStore.Str(r, 3), r.GetBoolean(4), r.GetInt32(5), []));
        }
        var compositions = await LoadCompositionsAsync(conn, null, ct);
        return Outcome<IReadOnlyList<MaterialView>>.Ok([.. materials.Select(m => m with { Composition = compositions.GetValueOrDefault(m.Id) ?? [] })]);
    }

    private static async Task<Dictionary<Guid, List<MaterialComponent>>> LoadCompositionsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct, Guid? only = null)
    {
        var result = new Dictionary<Guid, List<MaterialComponent>>();
        await using var cmd = new NpgsqlCommand(
            "SELECT ms.material_id, s.id, s.number, s.cas_number, s.name, ms.fraction_pct FROM cmp.material_substance ms JOIN cmp.substance s ON s.id = ms.substance_id WHERE (@m::uuid IS NULL OR ms.material_id = @m) ORDER BY ms.material_id, s.cas_number", conn, tx);
        cmd.Parameters.AddWithValue("m", only is null ? DBNull.Value : only.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (!result.TryGetValue(r.GetGuid(0), out var list)) result[r.GetGuid(0)] = list = [];
            list.Add(new MaterialComponent(r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetDecimal(5)));
        }
        return result;
    }

    public async Task<Outcome<MaterialView>> GetMaterialAsync(Subject subject, string code, string? correlationId, CancellationToken ct = default)
    {
        var all = await ListMaterialsAsync(subject, null, correlationId, ct);
        if (!all.IsOk) return Outcome<MaterialView>.Forbidden(all.Errors);
        var m = all.Value!.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.Ordinal));
        return m is null ? Outcome<MaterialView>.NotFound() : Outcome<MaterialView>.Ok(m);
    }

    public Task<Outcome<MaterialView>> CreateMaterialAsync(Subject subject, MaterialInput input, string? correlationId, CancellationToken ct = default) =>
        SaveMaterialAsync(subject, null, input, 0, correlationId, ct);

    public Task<Outcome<MaterialView>> UpdateMaterialAsync(Subject subject, string code, MaterialInput input, int revision, string? correlationId, CancellationToken ct = default) =>
        SaveMaterialAsync(subject, code, input, revision, correlationId, ct);

    private async Task<Outcome<MaterialView>> SaveMaterialAsync(Subject subject, string? existingCode, MaterialInput input, int revision, string? correlationId, CancellationToken ct)
    {
        var action = existingCode is null ? "compliance.material_create" : "compliance.material_update";
        if (!IsAdmin(subject)) return await DeniedAsync<MaterialView>(subject, action, "material", existingCode, correlationId, [AccessPolicy.RoleMissing], ct);
        var code = ComplianceRules.Clean(input.Code)?.ToUpperInvariant();
        var name = ComplianceRules.Clean(input.Name);
        var composition = input.Composition.Select(c => (Cas: ComplianceRules.Clean(c.CasNumber) ?? "", c.FractionPct)).ToList();
        var errors = ComplianceRules.ValidateMaterial(code, name, composition).ToList();
        if (existingCode is not null && !string.Equals(existingCode, code, StringComparison.Ordinal)) errors.Add("The code of a material cannot change.");
        if (input.Description is { Length: > 1000 }) errors.Add("The description is longer than 1000 characters.");
        if (errors.Count > 0) return Outcome<MaterialView>.Invalid(errors);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var substances = await ResolveSubstancesAsync(conn, tx, composition.Select(c => c.Cas), ct);
        if (substances.Missing.Count > 0) return Outcome<MaterialView>.Invalid(substances.Missing.Select(c => $"There is no substance {c} in the substance master."));
        Guid id;
        var now = clock.GetUtcNow();
        var by = subject.Username ?? subject.Id;
        if (existingCode is null)
        {
            await using (var dup = new NpgsqlCommand("SELECT 1 FROM cmp.material WHERE code = @c", conn, tx))
            {
                dup.Parameters.AddWithValue("c", code!);
                if (await dup.ExecuteScalarAsync(ct) is not null) return Outcome<MaterialView>.Conflict($"The material {code} exists already.");
            }
            id = Guid.CreateVersion7();
            await using var ins = new NpgsqlCommand("INSERT INTO cmp.material (id, code, name, description, active, created_at, created_by) VALUES (@id, @c, @n, @d, @a, @now, @by)", conn, tx);
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("c", code!);
            ins.Parameters.AddWithValue("n", name!);
            ins.Parameters.AddWithValue("d", (object?)ComplianceRules.Clean(input.Description) ?? DBNull.Value);
            ins.Parameters.AddWithValue("a", input.Active);
            ins.Parameters.AddWithValue("now", now);
            ins.Parameters.AddWithValue("by", by);
            await ins.ExecuteNonQueryAsync(ct);
        }
        else
        {
            Guid? found = null;
            int current = 0;
            await using (var sel = new NpgsqlCommand("SELECT id, revision FROM cmp.material WHERE code = @c FOR UPDATE", conn, tx))
            {
                sel.Parameters.AddWithValue("c", existingCode);
                await using var r = await sel.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) (found, current) = (r.GetGuid(0), r.GetInt32(1));
            }
            if (found is null) return Outcome<MaterialView>.NotFound();
            if (current != revision) return Outcome<MaterialView>.Conflict("The material was changed by someone else; reload and try again.");
            id = found.Value;
            await using var upd = new NpgsqlCommand("UPDATE cmp.material SET name = @n, description = @d, active = @a, revision = revision + 1, updated_at = @now, updated_by = @by WHERE id = @id", conn, tx);
            upd.Parameters.AddWithValue("n", name!);
            upd.Parameters.AddWithValue("d", (object?)ComplianceRules.Clean(input.Description) ?? DBNull.Value);
            upd.Parameters.AddWithValue("a", input.Active);
            upd.Parameters.AddWithValue("now", now);
            upd.Parameters.AddWithValue("by", by);
            upd.Parameters.AddWithValue("id", id);
            await upd.ExecuteNonQueryAsync(ct);
            await using var del = new NpgsqlCommand("DELETE FROM cmp.material_substance WHERE material_id = @id", conn, tx);
            del.Parameters.AddWithValue("id", id);
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var (cas, fraction) in composition)
        {
            await using var ins = new NpgsqlCommand("INSERT INTO cmp.material_substance (material_id, substance_id, fraction_pct) VALUES (@m, @s, @f)", conn, tx);
            ins.Parameters.AddWithValue("m", id);
            ins.Parameters.AddWithValue("s", substances.Ids[cas]);
            ins.Parameters.AddWithValue("f", fraction);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Success, "material", code, correlationId, ("components", composition.Count.ToString(CultureInfo.InvariantCulture))), ct);
        var view = new MaterialView(id, code!, name!, ComplianceRules.Clean(input.Description), input.Active, existingCode is null ? 1 : revision + 1,
            (await LoadCompositionsAsync(conn, tx, ct, id)).GetValueOrDefault(id) ?? []);
        await tx.CommitAsync(ct);
        return Outcome<MaterialView>.Ok(view);
    }

    private static async Task<(Dictionary<string, Guid> Ids, List<string> Missing)> ResolveSubstancesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, IEnumerable<string> cas, CancellationToken ct)
    {
        var wanted = cas.Distinct(StringComparer.Ordinal).ToArray();
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (wanted.Length > 0)
        {
            await using var cmd = new NpgsqlCommand("SELECT cas_number, id FROM cmp.substance WHERE cas_number = ANY(@c)", conn, tx);
            cmd.Parameters.AddWithValue("c", wanted);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids[r.GetString(0)] = r.GetGuid(1);
        }
        return (ids, [.. wanted.Where(c => !ids.ContainsKey(c))]);
    }

    // ================================================================ regulatory lists

    public async Task<Outcome<IReadOnlyList<RegListSummary>>> ListsAsync(Subject subject, string? correlationId, CancellationToken ct = default)
    {
        if (!CanRead(subject)) return await NeedRead<IReadOnlyList<RegListSummary>>(subject, "compliance.list_read", "reg_list", null, correlationId, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return Outcome<IReadOnlyList<RegListSummary>>.Ok(await LoadListSummariesAsync(conn, null, null, ct));
    }

    private async Task<IReadOnlyList<RegListSummary>> LoadListSummariesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string? code, CancellationToken ct)
    {
        var today = ComplianceStore.Today(clock);
        var result = new List<RegListSummary>();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT l.code, l.name, l.kind, l.description, l.active,
                   a.version, a.effective_from, o.version, o.status,
                   (SELECT count(*) FROM cmp.reg_list_entry e WHERE e.version_id = COALESCE(a.id, o.id))
            FROM cmp.reg_list l
            LEFT JOIN LATERAL (SELECT x.id, x.version, x.effective_from FROM cmp.reg_list_version x WHERE x.list_id = l.id AND x.status IN ('published', 'superseded') AND x.effective_from <= @today ORDER BY x.version DESC LIMIT 1) a ON true
            LEFT JOIN LATERAL (SELECT x.id, x.version, x.status FROM cmp.reg_list_version x WHERE x.list_id = l.id AND x.status IN ('draft', 'in_review') LIMIT 1) o ON true
            WHERE (@code::text IS NULL OR l.code = @code)
            ORDER BY l.name
            """, conn, tx);
        cmd.Parameters.AddWithValue("today", today);
        cmd.Parameters.AddWithValue("code", (object?)code ?? DBNull.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result.Add(new RegListSummary(r.GetString(0), r.GetString(1), r.GetString(2), ComplianceStore.Str(r, 3), r.GetBoolean(4), r.IsDBNull(5) ? null : r.GetInt32(5),
                r.IsDBNull(6) ? null : r.GetFieldValue<DateOnly>(6), r.IsDBNull(7) ? null : r.GetInt32(7), ComplianceStore.Str(r, 8), (int)r.GetInt64(9)));
        return result;
    }

    public async Task<Outcome<RegListDetail>> GetListAsync(Subject subject, string code, string? correlationId, CancellationToken ct = default)
    {
        if (!CanRead(subject)) return await NeedRead<RegListDetail>(subject, "compliance.list_read", "reg_list", code, correlationId, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var summaries = await LoadListSummariesAsync(conn, null, code, ct);
        var summary = summaries.Count > 0 ? summaries[0] : null;
        if (summary is null) return Outcome<RegListDetail>.NotFound();
        var versions = new List<RegVersionSummary>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT v.id, v.version, v.status, v.effective_from, v.source_note, (SELECT count(*) FROM cmp.reg_list_entry e WHERE e.version_id = v.id),
                   (SELECT count(*) FROM cmp.reg_list_exemption x WHERE x.version_id = v.id), v.published_at, v.published_by, v.revision, v.owning_org
            FROM cmp.reg_list_version v JOIN cmp.reg_list l ON l.id = v.list_id WHERE l.code = @c ORDER BY v.version DESC
            """, conn))
        {
            cmd.Parameters.AddWithValue("c", code);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                versions.Add(new RegVersionSummary(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetFieldValue<DateOnly>(3), ComplianceStore.Str(r, 4), (int)r.GetInt64(5), (int)r.GetInt64(6),
                    r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), ComplianceStore.Str(r, 8), r.GetInt32(9), r.GetString(10)));
        }
        return Outcome<RegListDetail>.Ok(new RegListDetail(summary, versions));
    }

    internal static async Task<(RegVersionSummary Summary, string ListCode, string ListName, string Kind)?> LoadVersionHeadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string code, int version, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT v.id, v.version, v.status, v.effective_from, v.source_note, (SELECT count(*) FROM cmp.reg_list_entry e WHERE e.version_id = v.id),
                   (SELECT count(*) FROM cmp.reg_list_exemption x WHERE x.version_id = v.id), v.published_at, v.published_by, v.revision, l.code, l.name, l.kind, v.owning_org
            FROM cmp.reg_list_version v JOIN cmp.reg_list l ON l.id = v.list_id WHERE l.code = @c AND v.version = @v{(forUpdate ? " FOR UPDATE OF v" : "")}
            """, conn, tx);
        cmd.Parameters.AddWithValue("c", code);
        cmd.Parameters.AddWithValue("v", version);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (new RegVersionSummary(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetFieldValue<DateOnly>(3), ComplianceStore.Str(r, 4), (int)r.GetInt64(5), (int)r.GetInt64(6),
            r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), ComplianceStore.Str(r, 8), r.GetInt32(9), r.GetString(13)), r.GetString(10), r.GetString(11), r.GetString(12));
    }

    internal static async Task<(IReadOnlyList<RegEntryView> Entries, IReadOnlyList<RegExemptionView> Exemptions)> LoadVersionContentAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid versionId, CancellationToken ct)
    {
        var entries = new List<RegEntryView>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT s.id, s.number, s.cas_number, s.name, s.mineral, e.threshold_pct, e.note FROM cmp.reg_list_entry e JOIN cmp.substance s ON s.id = e.substance_id WHERE e.version_id = @v ORDER BY s.cas_number", conn, tx))
        {
            cmd.Parameters.AddWithValue("v", versionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) entries.Add(new RegEntryView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), ComplianceStore.Str(r, 4), r.GetDecimal(5), ComplianceStore.Str(r, 6)));
        }
        var exemptions = new List<RegExemptionView>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT x.code, x.description, s.number, s.cas_number, x.expires_on FROM cmp.reg_list_exemption x LEFT JOIN cmp.substance s ON s.id = x.substance_id WHERE x.version_id = @v ORDER BY x.code", conn, tx))
        {
            cmd.Parameters.AddWithValue("v", versionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) exemptions.Add(new RegExemptionView(r.GetString(0), r.GetString(1), ComplianceStore.Str(r, 2), ComplianceStore.Str(r, 3), r.IsDBNull(4) ? null : r.GetFieldValue<DateOnly>(4)));
        }
        return (entries, exemptions);
    }

    public async Task<Outcome<RegVersionDetail>> GetVersionAsync(Subject subject, string code, int version, string? correlationId, CancellationToken ct = default)
    {
        if (!CanRead(subject)) return await NeedRead<RegVersionDetail>(subject, "compliance.list_read", "reg_list_version", $"{code}-{version}", correlationId, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var head = await LoadVersionHeadAsync(conn, null, code, version, false, ct);
        if (head is null) return Outcome<RegVersionDetail>.NotFound();
        var (entries, exemptions) = await LoadVersionContentAsync(conn, null, head.Value.Summary.Id, ct);
        RegVersionDiff? diff = null;
        await using (var prev = new NpgsqlCommand(
            "SELECT v.id FROM cmp.reg_list_version v JOIN cmp.reg_list l ON l.id = v.list_id WHERE l.code = @c AND v.version < @v AND v.status IN ('published', 'superseded') ORDER BY v.version DESC LIMIT 1", conn))
        {
            prev.Parameters.AddWithValue("c", code);
            prev.Parameters.AddWithValue("v", version);
            if (await prev.ExecuteScalarAsync(ct) is Guid previousId)
                diff = Diff((await LoadVersionContentAsync(conn, null, previousId, ct)).Entries, entries);
        }
        return Outcome<RegVersionDetail>.Ok(new RegVersionDetail(head.Value.ListCode, head.Value.ListName, head.Value.Kind, head.Value.Summary, entries, exemptions, diff));
    }

    internal static RegVersionDiff Diff(IReadOnlyList<RegEntryView> before, IReadOnlyList<RegEntryView> after)
    {
        var b = before.ToDictionary(e => e.CasNumber, StringComparer.Ordinal);
        var a = after.ToDictionary(e => e.CasNumber, StringComparer.Ordinal);
        string Line(RegEntryView e) => $"{e.CasNumber} {e.SubstanceName} (threshold {ComplianceRules.Num(e.ThresholdPct)} %)";
        return new RegVersionDiff(
            [.. a.Values.Where(e => !b.ContainsKey(e.CasNumber)).OrderBy(e => e.CasNumber, StringComparer.Ordinal).Select(Line)],
            [.. b.Values.Where(e => !a.ContainsKey(e.CasNumber)).OrderBy(e => e.CasNumber, StringComparer.Ordinal).Select(Line)],
            [.. a.Values.Where(e => b.TryGetValue(e.CasNumber, out var old) && old.ThresholdPct != e.ThresholdPct).OrderBy(e => e.CasNumber, StringComparer.Ordinal)
                .Select(e => $"{e.CasNumber} {e.SubstanceName}: threshold {ComplianceRules.Num(b[e.CasNumber].ThresholdPct)} % to {ComplianceRules.Num(e.ThresholdPct)} %")]);
    }

    public async Task<Outcome<RegListSummary>> CreateListAsync(Subject subject, CreateListCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        if (!IsAdmin(subject)) return await DeniedAsync<RegListSummary>(subject, "compliance.list_create", "reg_list", null, correlationId, [AccessPolicy.RoleMissing], ct);
        var code = ComplianceRules.Clean(cmd.Code)?.ToLowerInvariant();
        var errors = new List<string>();
        if (!ComplianceRules.IsValidListCode(code)) errors.Add("The list code uses lower-case letters, digits and '_' (2 to 31 characters, starting with a letter).");
        if (string.IsNullOrWhiteSpace(cmd.Name) || cmd.Name.Trim().Length > 120) errors.Add("A name of 1 to 120 characters is required.");
        if (!ListKinds.All.Contains(cmd.Kind)) errors.Add("The kind is restricted, svhc or conflict.");
        if (cmd.Description is { Length: > 1000 }) errors.Add("The description is longer than 1000 characters.");
        if (errors.Count > 0) return Outcome<RegListSummary>.Invalid(errors);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var dup = new NpgsqlCommand("SELECT 1 FROM cmp.reg_list WHERE code = @c", conn, tx))
        {
            dup.Parameters.AddWithValue("c", code!);
            if (await dup.ExecuteScalarAsync(ct) is not null) return Outcome<RegListSummary>.Conflict($"The list {code} exists already.");
        }
        await using (var ins = new NpgsqlCommand("INSERT INTO cmp.reg_list (id, code, name, kind, description, created_at, created_by) VALUES (@id, @c, @n, @k, @d, @now, @by)", conn, tx))
        {
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("c", code!);
            ins.Parameters.AddWithValue("n", cmd.Name.Trim());
            ins.Parameters.AddWithValue("k", cmd.Kind);
            ins.Parameters.AddWithValue("d", (object?)ComplianceRules.Clean(cmd.Description) ?? DBNull.Value);
            ins.Parameters.AddWithValue("now", clock.GetUtcNow());
            ins.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "compliance.list_create", AuditOutcome.Success, "reg_list", code, correlationId, ("kind", cmd.Kind)), ct);
        var summary = (await LoadListSummariesAsync(conn, tx, code, ct)).Single();
        await tx.CommitAsync(ct);
        return Outcome<RegListSummary>.Ok(summary);
    }

    /// <summary>Starts the next version of a list as a draft that holds a copy of the newest version's entries and exemptions. One version is in preparation at a time.</summary>
    public async Task<Outcome<RegVersionSummary>> CreateDraftAsync(Subject subject, string code, DateOnly effectiveFrom, string? sourceNote, string? correlationId, CancellationToken ct = default)
    {
        if (!IsAdmin(subject)) return await DeniedAsync<RegVersionSummary>(subject, "compliance.list_draft_create", "reg_list", code, correlationId, [AccessPolicy.RoleMissing], ct);
        if (sourceNote is { Length: > 500 }) return Outcome<RegVersionSummary>.Invalid(["The source note is longer than 500 characters."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid listId;
        await using (var sel = new NpgsqlCommand("SELECT id FROM cmp.reg_list WHERE code = @c FOR UPDATE", conn, tx))
        {
            sel.Parameters.AddWithValue("c", code);
            if (await sel.ExecuteScalarAsync(ct) is not Guid id) return Outcome<RegVersionSummary>.NotFound();
            listId = id;
        }
        int next; Guid? source = null; DateOnly? lastFrom = null; bool open;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT COALESCE(max(version), 0) + 1,
                   (SELECT x.id FROM cmp.reg_list_version x WHERE x.list_id = @l ORDER BY x.version DESC LIMIT 1),
                   (SELECT max(x.effective_from) FROM cmp.reg_list_version x WHERE x.list_id = @l AND x.status IN ('published', 'superseded')),
                   EXISTS (SELECT 1 FROM cmp.reg_list_version x WHERE x.list_id = @l AND x.status IN ('draft', 'in_review'))
            FROM cmp.reg_list_version WHERE list_id = @l
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("l", listId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            next = r.GetInt32(0);
            source = r.IsDBNull(1) ? null : r.GetGuid(1);
            lastFrom = r.IsDBNull(2) ? null : r.GetFieldValue<DateOnly>(2);
            open = r.GetBoolean(3);
        }
        if (open) return Outcome<RegVersionSummary>.Conflict("A version of this list is already in preparation or in review.");
        if (lastFrom is { } lf && effectiveFrom < lf) return Outcome<RegVersionSummary>.Invalid([$"The effective date cannot be before that of the current version ({lf:yyyy-MM-dd})."]);
        var versionId = Guid.CreateVersion7();
        var now = clock.GetUtcNow();
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO cmp.reg_list_version (id, list_id, version, status, effective_from, source_note, owning_org, created_at, created_by) VALUES (@id, @l, @v, 'draft', @from, @note, @org, @now, @by)", conn, tx))
        {
            ins.Parameters.AddWithValue("id", versionId);
            ins.Parameters.AddWithValue("l", listId);
            ins.Parameters.AddWithValue("v", next);
            ins.Parameters.AddWithValue("from", effectiveFrom);
            ins.Parameters.AddWithValue("note", (object?)ComplianceRules.Clean(sourceNote) ?? DBNull.Value);
            ins.Parameters.AddWithValue("org", string.IsNullOrWhiteSpace(subject.Org) ? "UNKNOWN" : subject.Org);
            ins.Parameters.AddWithValue("now", now);
            ins.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await ins.ExecuteNonQueryAsync(ct);
        }
        if (source is { } src)
        {
            await using var copy = new NpgsqlCommand(
                """
                INSERT INTO cmp.reg_list_entry (id, version_id, substance_id, threshold_pct, note) SELECT gen_random_uuid(), @n, substance_id, threshold_pct, note FROM cmp.reg_list_entry WHERE version_id = @s;
                INSERT INTO cmp.reg_list_exemption (id, version_id, code, description, substance_id, expires_on) SELECT gen_random_uuid(), @n, code, description, substance_id, expires_on FROM cmp.reg_list_exemption WHERE version_id = @s;
                """, conn, tx);
            copy.Parameters.AddWithValue("n", versionId);
            copy.Parameters.AddWithValue("s", src);
            await copy.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "compliance.list_draft_create", AuditOutcome.Success, ComplianceStore.ListVersionObjectType, $"{code}-{next}", correlationId,
            ("effectiveFrom", effectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))), ct);
        var head = (await LoadVersionHeadAsync(conn, tx, code, next, false, ct))!.Value.Summary;
        await tx.CommitAsync(ct);
        return Outcome<RegVersionSummary>.Ok(head);
    }

    /// <summary>Replaces the entries and exemptions of a draft version (and its effective date and source note). Only a draft can change; a version in review is frozen.</summary>
    public async Task<Outcome<RegVersionSummary>> SaveDraftAsync(
        Subject subject, string code, int version, DateOnly effectiveFrom, string? sourceNote, IReadOnlyList<RegEntryInput> entries, IReadOnlyList<RegExemptionInput> exemptions, int revision, string? correlationId, CancellationToken ct = default)
    {
        var objectId = $"{code}-{version}";
        if (!IsAdmin(subject)) return await DeniedAsync<RegVersionSummary>(subject, "compliance.list_draft_save", ComplianceStore.ListVersionObjectType, objectId, correlationId, [AccessPolicy.RoleMissing], ct);
        if (sourceNote is { Length: > 500 }) return Outcome<RegVersionSummary>.Invalid(["The source note is longer than 500 characters."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await LoadVersionHeadAsync(conn, tx, code, version, true, ct);
        if (head is null) return Outcome<RegVersionSummary>.NotFound();
        if (head.Value.Summary.Status != VersionStates.Draft) return Outcome<RegVersionSummary>.Conflict($"Version {version} is {VersionStates.Label(head.Value.Summary.Status).ToLowerInvariant()} and cannot be edited.");
        if (head.Value.Summary.Revision != revision) return Outcome<RegVersionSummary>.Conflict("The draft was changed by someone else; reload and try again.");
        var cleaned = entries.Select(e => e with { CasNumber = ComplianceRules.Clean(e.CasNumber) ?? "" }).ToList();
        var cleanedEx = exemptions.Select(x => x with { Code = ComplianceRules.Clean(x.Code) ?? "", CasNumber = ComplianceRules.Clean(x.CasNumber), Description = ComplianceRules.Clean(x.Description) ?? "" }).ToList();
        var errors = ComplianceRules.ValidateListVersion(cleaned, cleanedEx, head.Value.Kind).ToList();
        await using (var last = new NpgsqlCommand(
            "SELECT max(v.effective_from) FROM cmp.reg_list_version v JOIN cmp.reg_list l ON l.id = v.list_id WHERE l.code = @c AND v.status IN ('published', 'superseded')", conn, tx))
        {
            last.Parameters.AddWithValue("c", code);
            var lastValue = await last.ExecuteScalarAsync(ct);
            DateOnly? lastFrom = lastValue switch { DateTime d => DateOnly.FromDateTime(d), DateOnly d => d, _ => null };
            if (lastFrom is { } lf && effectiveFrom < lf) errors.Add("The effective date cannot be before that of the current version.");
        }
        var wanted = cleaned.Select(e => e.CasNumber).Concat(cleanedEx.Where(x => x.CasNumber is not null).Select(x => x.CasNumber!));
        var substances = await ResolveSubstancesAsync(conn, tx, wanted, ct);
        errors.AddRange(substances.Missing.Select(c => $"There is no substance {c} in the substance master; add it first."));
        if (errors.Count > 0) return Outcome<RegVersionSummary>.Invalid(errors);
        foreach (var sql in new[] { "DELETE FROM cmp.reg_list_exemption WHERE version_id = @v", "DELETE FROM cmp.reg_list_entry WHERE version_id = @v" })
        {
            await using var del = new NpgsqlCommand(sql, conn, tx);
            del.Parameters.AddWithValue("v", head.Value.Summary.Id);
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var e in cleaned)
        {
            await using var ins = new NpgsqlCommand("INSERT INTO cmp.reg_list_entry (id, version_id, substance_id, threshold_pct, note) VALUES (@id, @v, @s, @t, @n)", conn, tx);
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("v", head.Value.Summary.Id);
            ins.Parameters.AddWithValue("s", substances.Ids[e.CasNumber]);
            ins.Parameters.AddWithValue("t", e.ThresholdPct);
            ins.Parameters.AddWithValue("n", (object?)ComplianceRules.Clean(e.Note) ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync(ct);
        }
        foreach (var x in cleanedEx)
        {
            await using var ins = new NpgsqlCommand("INSERT INTO cmp.reg_list_exemption (id, version_id, code, description, substance_id, expires_on) VALUES (@id, @v, @c, @d, @s, @e)", conn, tx);
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("v", head.Value.Summary.Id);
            ins.Parameters.AddWithValue("c", x.Code);
            ins.Parameters.AddWithValue("d", x.Description);
            ins.Parameters.AddWithValue("s", x.CasNumber is null ? DBNull.Value : substances.Ids[x.CasNumber]);
            ins.Parameters.AddWithValue("e", (object?)x.ExpiresOn ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await using (var upd = new NpgsqlCommand(
            "UPDATE cmp.reg_list_version SET effective_from = @from, source_note = @note, revision = revision + 1, updated_at = @now, updated_by = @by WHERE id = @id", conn, tx))
        {
            upd.Parameters.AddWithValue("from", effectiveFrom);
            upd.Parameters.AddWithValue("note", (object?)ComplianceRules.Clean(sourceNote) ?? DBNull.Value);
            upd.Parameters.AddWithValue("now", clock.GetUtcNow());
            upd.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            upd.Parameters.AddWithValue("id", head.Value.Summary.Id);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "compliance.list_draft_save", AuditOutcome.Success, ComplianceStore.ListVersionObjectType, objectId, correlationId,
            ("entries", cleaned.Count.ToString(CultureInfo.InvariantCulture)), ("exemptions", cleanedEx.Count.ToString(CultureInfo.InvariantCulture))), ct);
        var saved = (await LoadVersionHeadAsync(conn, tx, code, version, false, ct))!.Value.Summary;
        await tx.CommitAsync(ct);
        return Outcome<RegVersionSummary>.Ok(saved);
    }

    public async Task<Outcome<bool>> DiscardDraftAsync(Subject subject, string code, int version, string? correlationId, CancellationToken ct = default)
    {
        var objectId = $"{code}-{version}";
        if (!IsAdmin(subject)) return await DeniedAsync<bool>(subject, "compliance.list_draft_discard", ComplianceStore.ListVersionObjectType, objectId, correlationId, [AccessPolicy.RoleMissing], ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await LoadVersionHeadAsync(conn, tx, code, version, true, ct);
        if (head is null) return Outcome<bool>.NotFound();
        if (head.Value.Summary.Status != VersionStates.Draft) return Outcome<bool>.Conflict("Only a draft can be discarded.");
        foreach (var sql in new[] { "DELETE FROM cmp.reg_list_exemption WHERE version_id = @v", "DELETE FROM cmp.reg_list_entry WHERE version_id = @v", "DELETE FROM cmp.reg_list_version WHERE id = @v" })
        {
            await using var del = new NpgsqlCommand(sql, conn, tx);
            del.Parameters.AddWithValue("v", head.Value.Summary.Id);
            await del.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "compliance.list_draft_discard", AuditOutcome.Success, ComplianceStore.ListVersionObjectType, objectId, correlationId), ct);
        await tx.CommitAsync(ct);
        return Outcome<bool>.Ok(true);
    }

    /// <summary>The regulations in force on a date (default today) as reports name them.</summary>
    public async Task<Outcome<IReadOnlyList<ReportRegulation>>> ActiveRegulationsAsync(Subject subject, DateOnly? asOf, string? correlationId, CancellationToken ct = default)
    {
        if (!CanRead(subject)) return await NeedRead<IReadOnlyList<ReportRegulation>>(subject, "compliance.list_read", "reg_list", null, correlationId, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var versions = await ComplianceStore.LoadVersionsAsync(conn, null, asOf ?? ComplianceStore.Today(clock), null, ct);
        return Outcome<IReadOnlyList<ReportRegulation>>.Ok([.. versions.Select(v => new ReportRegulation(v.Code, v.Name, v.Kind, v.Version, v.EffectiveFrom))]);
    }
}
