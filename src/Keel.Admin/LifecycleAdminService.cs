using System.Globalization;
using Keel.Core;
using Npgsql;

namespace Keel.Admin;

/// <summary>
/// Versioned lifecycle administration (ADR-0022). A published version never changes; editing publishes a new version that NEW objects use. An
/// object keeps its version until an administrator runs the audited migration with a state mapping.
/// </summary>
public sealed class LifecycleAdminService(AdminRunner run, IWorkflowTemplateAdmin workflows)
{
    // ---------------------------------------------------------------- read

    public Task<Outcome<IReadOnlyList<LifecycleFamilyInfo>>> ListFamiliesAsync(Subject subject, CancellationToken ct = default) =>
        run.ReadAsync<IReadOnlyList<LifecycleFamilyInfo>>(subject, "admin.lifecycle_list", null, conn => LoadFamiliesAsync(conn, null, ct), ct);

    public async Task<Outcome<LifecycleVersionView>> GetAsync(Subject subject, string family, int? version, CancellationToken ct = default)
    {
        var o = await run.ReadAsync<LifecycleVersionView?>(subject, "admin.lifecycle_get", family, async conn =>
        {
            var info = (await LoadFamiliesAsync(conn, null, ct)).FirstOrDefault(f => f.Family == family);
            if (info is null) return null;
            var v = version ?? info.CurrentVersion;
            var def = await LoadDefinitionAsync(conn, null, family, v, ct);
            if (def is null) return null;
            var usage = await UsageAsync(conn, null, info.ObjectType, def, ct);
            return new LifecycleVersionView(def, usage, usage.Sum(u => u.Revisions), v == info.CurrentVersion, info.Editable);
        }, ct);
        if (!o.IsOk) return Outcome<LifecycleVersionView>.Forbidden(o.Errors);
        return o.Value is null ? Outcome<LifecycleVersionView>.NotFound() : Outcome<LifecycleVersionView>.Ok(o.Value);
    }

    // ---------------------------------------------------------------- validate (no change)

    /// <summary>Validates a draft of the next version and describes its effect against the current version.</summary>
    public async Task<Outcome<LifecycleValidation>> ValidateAsync(Subject subject, string family, LifecycleDefinition draft, CancellationToken ct = default)
    {
        var current = await GetAsync(subject, family, null, ct);
        if (!current.IsOk) return current.Kind == OutcomeKind.Forbidden ? Outcome<LifecycleValidation>.Forbidden(current.Errors) : Outcome<LifecycleValidation>.NotFound();
        var def = current.Value!.Definition;
        var d = draft with { Family = family, Name = def.Name, ObjectType = def.ObjectType, Version = def.Version + 1 };
        var bound = await WorkflowBoundAsync(subject, def.ObjectType, ct);
        var v = LifecycleRules.Validate(d, bound, def);
        var effects = LifecycleRules.Diff(def, d);
        if (current.Value.TotalRevisions > 0)
            effects.Add($"{current.Value.TotalRevisions} existing revision(s) stay on version {def.Version} until you migrate them; new objects use version {d.Version}.");
        return Outcome<LifecycleValidation>.Ok(v with { Effects = effects });
    }

    private async Task<bool> WorkflowBoundAsync(Subject subject, string objectType, CancellationToken ct)
    {
        var templates = await workflows.ListVersionsAsync(subject, false, null, ct);
        return templates.IsOk && templates.Value!.Any(t => t.IsActive && t.ObjectType == objectType);
    }

    // ---------------------------------------------------------------- publish a new version

    public Task<Outcome<LifecycleVersionView>> PublishVersionAsync(Subject subject, string family, LifecycleDefinition draft, string? changeNote, string? correlationId, CancellationToken ct = default) =>
        run.ChangeAsync<LifecycleVersionView>(subject, "admin.lifecycle_publish", family, correlationId, async (conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(changeNote)) return Step<LifecycleVersionView>.Invalid("A change note is required.");
            var info = (await LoadFamiliesAsync(conn, tx, ct)).FirstOrDefault(f => f.Family == family);
            if (info is null) return Step<LifecycleVersionView>.NotFound();
            if (!info.Editable)
                throw new NotSupportedException($"The {info.Name.ToLowerInvariant()} is tied to change-process rules in code and database triggers (ADR-0019); its editor is planned for Phase 5 (see docs/increments/2.2-admin-classification.md).");
            await using (var l = AdminRunner.Cmd(conn, tx, "SELECT pg_advisory_xact_lock(hashtext(@k))", ("k", "plm.lifecycle:" + family))) await l.ExecuteNonQueryAsync(ct);
            // Re-read under the lock so two concurrent publishes cannot both build on the same version.
            info = (await LoadFamiliesAsync(conn, tx, ct)).First(f => f.Family == family);
            var current = (await LoadDefinitionAsync(conn, tx, family, info.CurrentVersion, ct))!;
            var next = draft with { Family = family, ObjectType = info.ObjectType, Name = info.Name, Version = info.CurrentVersion + 1, Status = "published" };
            var validation = LifecycleRules.Validate(next, await WorkflowBoundAsync(subject, info.ObjectType, ct), current);
            if (validation.Errors.Count > 0) return Step<LifecycleVersionView>.Invalid(validation.Errors);
            if (LifecycleRules.Diff(current, next).Count == 0) return Step<LifecycleVersionView>.Invalid("Nothing changed: the draft equals the current version.");

            await InsertVersionAsync(conn, tx, next, AdminRunner.Who(subject), run.Clock.GetUtcNow(), changeNote.Trim(), ct);
            await using (var bump = AdminRunner.Cmd(conn, tx, "UPDATE plm.lifecycle_family SET current_version = @v WHERE family = @f", ("v", next.Version), ("f", family))) await bump.ExecuteNonQueryAsync(ct);
            var stored = (await LoadDefinitionAsync(conn, tx, family, next.Version, ct))!;
            var view = new LifecycleVersionView(stored, await UsageAsync(conn, tx, info.ObjectType, stored, ct), 0, true, true);
            return Step<LifecycleVersionView>.Ok(view, ("before", AdminRunner.ToJson(current)), ("after", AdminRunner.ToJson(stored)), ("changeNote", changeNote.Trim()),
                ("version", next.Version.ToString(CultureInfo.InvariantCulture)));
        }, ct);

    /// <summary>Creates a new lifecycle family (version 1) that part or document types can be bound to.</summary>
    public Task<Outcome<LifecycleVersionView>> CreateFamilyAsync(Subject subject, string family, string name, string objectType, LifecycleDefinition draft, string? correlationId, CancellationToken ct = default) =>
        run.ChangeAsync<LifecycleVersionView>(subject, "admin.lifecycle_create", family, correlationId, async (conn, tx) =>
        {
            if (family is null || !System.Text.RegularExpressions.Regex.IsMatch(family, "^[a-z][a-z0-9-]{0,40}$")) return Step<LifecycleVersionView>.Invalid("The family id must start with a lower-case letter and use a-z, 0-9 and '-' (at most 41 characters).");
            if (!LifecycleRules.VersionedObjectTypes.Contains(objectType)) return Step<LifecycleVersionView>.Invalid($"Lifecycles can be created for: {string.Join(", ", LifecycleRules.VersionedObjectTypes)}.");
            if ((await LoadFamiliesAsync(conn, tx, ct)).Any(f => f.Family == family)) return Step<LifecycleVersionView>.Conflict($"Lifecycle '{family}' already exists.");
            var def = draft with { Family = family, Name = name?.Trim() ?? "", ObjectType = objectType, Version = 1, Status = "published" };
            var validation = LifecycleRules.Validate(def, await WorkflowBoundAsync(subject, objectType, ct));
            if (validation.Errors.Count > 0) return Step<LifecycleVersionView>.Invalid(validation.Errors);
            await using (var f = AdminRunner.Cmd(conn, tx, "INSERT INTO plm.lifecycle_family (family, object_type, name, current_version, is_system, editable) VALUES (@f, @t, @n, 1, false, true)",
                ("f", family), ("t", objectType), ("n", def.Name))) await f.ExecuteNonQueryAsync(ct);
            await InsertVersionAsync(conn, tx, def, AdminRunner.Who(subject), run.Clock.GetUtcNow(), "Created", ct);
            var stored = (await LoadDefinitionAsync(conn, tx, family, 1, ct))!;
            return Step<LifecycleVersionView>.Ok(new LifecycleVersionView(stored, [], 0, true, true), ("after", AdminRunner.ToJson(stored)));
        }, ct);

    // ---------------------------------------------------------------- migrate objects to another version

    public async Task<Outcome<MigrationPlan>> PreviewMigrationAsync(Subject subject, string family, int fromVersion, int toVersion, CancellationToken ct = default)
    {
        var o = await run.ReadAsync<MigrationPlan?>(subject, "admin.lifecycle_migrate_preview", family, conn => PlanAsync(conn, null, family, fromVersion, toVersion, ct), ct);
        if (!o.IsOk) return Outcome<MigrationPlan>.Forbidden(o.Errors);
        return o.Value is null ? Outcome<MigrationPlan>.NotFound() : Outcome<MigrationPlan>.Ok(o.Value);
    }

    public Task<Outcome<MigrationResult>> MigrateObjectsAsync(Subject subject, string family, int fromVersion, int toVersion, IReadOnlyDictionary<string, string> mapping, string? reason,
        string? correlationId, CancellationToken ct = default) =>
        run.ChangeAsync<MigrationResult>(subject, "admin.lifecycle_migrate", family, correlationId, async (conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) return Step<MigrationResult>.Invalid("A reason is required.");
            await using (var l = AdminRunner.Cmd(conn, tx, "SELECT pg_advisory_xact_lock(hashtext(@k))", ("k", "plm.lifecycle:" + family))) await l.ExecuteNonQueryAsync(ct);
            var plan = await PlanAsync(conn, tx, family, fromVersion, toVersion, ct);
            if (plan is null) return Step<MigrationResult>.NotFound();
            if (toVersion <= fromVersion) return Step<MigrationResult>.Invalid("Objects can only be migrated to a newer version.");
            var info = (await LoadFamiliesAsync(conn, tx, ct)).First(f => f.Family == family);
            if (!LifecycleRules.VersionedObjectTypes.Contains(info.ObjectType)) return Step<MigrationResult>.Invalid($"Objects of type {info.ObjectType} cannot be migrated.");
            if (plan.ActiveReviews > 0) return Step<MigrationResult>.Conflict($"{plan.ActiveReviews} object(s) are under an active review. Finish or cancel those reviews first.");
            var from = (await LoadDefinitionAsync(conn, tx, family, fromVersion, ct))!;
            var to = (await LoadDefinitionAsync(conn, tx, family, toVersion, ct))!;
            var errors = LifecycleRules.ValidateMapping(from, to, mapping, plan.StatesInUse);
            if (errors.Count > 0) return Step<MigrationResult>.Invalid(errors);

            // Only states in use need a pair; the function refuses any state in use that is missing from the mapping.
            var keys = mapping.Keys.ToArray();
            await using var cmd = AdminRunner.Cmd(conn, tx, "SELECT cfg.migrate_lifecycle_objects(@t, @from, @to, @mf, @mt)",
                ("t", info.ObjectType), ("from", from.Version == 1 ? family : $"{family}~v{from.Version}"), ("to", $"{family}~v{to.Version}"), ("mf", keys), ("mt", keys.Select(k => mapping[k]).ToArray()));
            var moved = (long)(await cmd.ExecuteScalarAsync(ct))!;
            var result = new MigrationResult(family, fromVersion, toVersion, moved, plan.Revisions);
            return Step<MigrationResult>.Ok(result,
                ("before", AdminRunner.ToJson(new { version = fromVersion, objects = plan.Revisions, states = plan.StatesInUse })),
                ("after", AdminRunner.ToJson(new { version = toVersion, mapping, statesChanged = moved })), ("reason", reason.Trim()));
        }, ct);

    // ---------------------------------------------------------------- storage

    private static async Task<IReadOnlyList<LifecycleFamilyInfo>> LoadFamiliesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var versions = new Dictionary<string, List<LifecycleVersionInfo>>();
        await using (var cmd = AdminRunner.Cmd(conn, tx,
            "SELECT l.family, l.version, l.status, l.created_at, l.created_by, l.change_note, l.version = f.current_version FROM plm.lifecycle l JOIN plm.lifecycle_family f ON f.family = l.family ORDER BY l.family, l.version DESC"))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!versions.TryGetValue(r.GetString(0), out var list)) versions[r.GetString(0)] = list = [];
                list.Add(new LifecycleVersionInfo(r.GetInt32(1), r.GetString(2), new DateTimeOffset(r.GetDateTime(3), TimeSpan.Zero), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetBoolean(6)));
            }
        }
        var result = new List<LifecycleFamilyInfo>();
        await using var fc = AdminRunner.Cmd(conn, tx, "SELECT family, name, object_type, current_version, is_system, editable FROM plm.lifecycle_family ORDER BY object_type, family");
        await using var fr = await fc.ExecuteReaderAsync(ct);
        while (await fr.ReadAsync(ct))
            result.Add(new LifecycleFamilyInfo(fr.GetString(0), fr.GetString(1), fr.GetString(2), fr.GetInt32(3), fr.GetBoolean(4), fr.GetBoolean(5), versions.GetValueOrDefault(fr.GetString(0)) ?? []));
        return result;
    }

    internal static string IdOf(string family, int version) => version == 1 ? family : $"{family}~v{version}";

    internal static async Task<LifecycleDefinition?> LoadDefinitionAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string family, int version, CancellationToken ct)
    {
        var id = IdOf(family, version);
        string name, objectType, status;
        string? note;
        await using (var c = AdminRunner.Cmd(conn, tx, "SELECT l.name, f.object_type, l.status, l.change_note FROM plm.lifecycle l JOIN plm.lifecycle_family f ON f.family = l.family WHERE l.id = @id", ("id", id)))
        {
            await using var r = await c.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            (name, objectType, status, note) = (r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));
        }
        var states = new List<LifecycleStateDef>();
        await using (var c = AdminRunner.Cmd(conn, tx, "SELECT state, label, sort_order, is_initial, is_locked, is_final FROM plm.lifecycle_state WHERE lifecycle_id = @id ORDER BY sort_order, state", ("id", id)))
        {
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) states.Add(new LifecycleStateDef(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetBoolean(3), r.GetBoolean(4), r.GetBoolean(5)));
        }
        var transitions = new List<LifecycleTransitionDef>();
        await using (var c = AdminRunner.Cmd(conn, tx, "SELECT from_state, to_state, via FROM plm.lifecycle_transition WHERE lifecycle_id = @id ORDER BY from_state, to_state", ("id", id)))
        {
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) transitions.Add(new LifecycleTransitionDef(r.GetString(0), r.GetString(1), r.GetString(2)));
        }
        return new LifecycleDefinition(family, name, objectType, version, status, states, transitions, note);
    }

    private static async Task InsertVersionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, LifecycleDefinition d, string by, DateTimeOffset at, string note, CancellationToken ct)
    {
        var id = IdOf(d.Family, d.Version);
        // Rows go in under a draft header and the header is published last: the database refuses changes to published versions.
        await using (var c = AdminRunner.Cmd(conn, tx,
            "INSERT INTO plm.lifecycle (id, name, family, version, status, created_at, created_by, change_note) VALUES (@id, @n, @f, @v, 'draft', @at, @by, @note)",
            ("id", id), ("n", d.Name), ("f", d.Family), ("v", d.Version), ("at", at), ("by", by), ("note", note))) await c.ExecuteNonQueryAsync(ct);
        foreach (var s in d.States)
            await using (var c = AdminRunner.Cmd(conn, tx,
                "INSERT INTO plm.lifecycle_state (lifecycle_id, state, label, sort_order, is_initial, is_locked, is_final) VALUES (@id, @s, @l, @o, @i, @k, @fin)",
                ("id", id), ("s", s.State), ("l", s.Label.Trim()), ("o", s.SortOrder), ("i", s.IsInitial), ("k", s.IsLocked), ("fin", s.IsFinal))) await c.ExecuteNonQueryAsync(ct);
        foreach (var t in d.Transitions)
            await using (var c = AdminRunner.Cmd(conn, tx,
                "INSERT INTO plm.lifecycle_transition (lifecycle_id, from_state, to_state, via) VALUES (@id, @a, @b, @via)", ("id", id), ("a", t.From), ("b", t.To), ("via", t.Via))) await c.ExecuteNonQueryAsync(ct);
        await using (var c = AdminRunner.Cmd(conn, tx, "UPDATE plm.lifecycle SET status = 'published' WHERE id = @id", ("id", id))) await c.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<StateUsage>> UsageAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string objectType, LifecycleDefinition def, CancellationToken ct)
    {
        var id = IdOf(def.Family, def.Version);
        var counts = new Dictionary<string, StateUsage>(StringComparer.Ordinal);
        if (LifecycleRules.VersionedObjectTypes.Contains(objectType))
        {
            await using var c = AdminRunner.Cmd(conn, tx, "SELECT state, revisions, checked_out FROM cfg.lifecycle_usage(@t) WHERE lifecycle_id = @id", ("t", objectType), ("id", id));
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) counts[r.GetString(0)] = new StateUsage(r.GetString(0), r.GetInt64(1), r.GetInt64(2));
        }
        var list = def.States.Select(s => counts.GetValueOrDefault(s.State) ?? new StateUsage(s.State, 0, 0)).ToList();
        // Rows in a state the version does not define (data repaired by an override) are still shown.
        list.AddRange(counts.Values.Where(u => def.States.All(s => s.State != u.State)));
        return list;
    }

    private static async Task<MigrationPlan?> PlanAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string family, int fromVersion, int toVersion, CancellationToken ct)
    {
        var info = (await LoadFamiliesAsync(conn, tx, ct)).FirstOrDefault(f => f.Family == family);
        if (info is null) return null;
        var from = await LoadDefinitionAsync(conn, tx, family, fromVersion, ct);
        var to = await LoadDefinitionAsync(conn, tx, family, toVersion, ct);
        if (from is null || to is null) return null;
        var usage = await UsageAsync(conn, tx, info.ObjectType, from, ct);
        var suggested = usage.Where(u => u.Revisions > 0 && to.States.Any(s => s.State == u.State)).ToDictionary(u => u.State, u => u.State, StringComparer.Ordinal);
        long reviews = 0;
        if (LifecycleRules.VersionedObjectTypes.Contains(info.ObjectType))
        {
            await using var c = AdminRunner.Cmd(conn, tx, "SELECT cfg.lifecycle_active_reviews(@t, @id)", ("t", info.ObjectType), ("id", IdOf(family, fromVersion)));
            reviews = (long)(await c.ExecuteScalarAsync(ct))!;
        }
        return new MigrationPlan(family, fromVersion, toVersion, usage, suggested, usage.Sum(u => u.Revisions), reviews);
    }
}
