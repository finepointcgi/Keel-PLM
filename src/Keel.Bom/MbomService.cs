using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

/// <summary>
/// Manufacturing BOMs (ADR-0024). An MBOM is its own versioned object per (part, plant): header, revisions, iterations, lines, EBOM links and
/// reconciliation runs. Every call evaluates the access policy (ADR-0004) on the MBOM and on its part, writes its audit event in the same
/// transaction as its change (denials included, ADR-0005) and serializes per MBOM on <c>SELECT ... FOR UPDATE</c> of the header row.
/// </summary>
public sealed partial class MbomService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock)
{
    public const string ObjectType = "mbom";

    internal sealed record Step<T>(OutcomeKind Kind, IReadOnlyList<string> Errors, T? Value, (string, string?)[] Details, bool Denied = false)
    {
        public static Step<T> Done(T value, params (string, string?)[] details) => new(OutcomeKind.Ok, [], value, details);
        public static Step<T> Invalid(params string[] errors) => new(OutcomeKind.Invalid, errors, default, []);
        public static Step<T> Conflict(string message) => new(OutcomeKind.Conflict, [message], default, []);
        public static Step<T> Missing() => new(OutcomeKind.NotFound, [], default, []);
        /// <summary>A refusal on access grounds: audited as Denied, reported as invalid input so existence is not disclosed.</summary>
        public static Step<T> Hidden(string message, string reason) => new(OutcomeKind.Invalid, [message], default, [("reasons", reason)], true);
    }

    internal static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string? number, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s),
        Action = action,
        Outcome = outcome,
        ObjectType = ObjectType,
        ObjectId = number,
        CorrelationId = correlationId,
        Details = AuditDetails.Of(details),
    };

    private static string Fmt(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>The MBOM and its part must both be readable: the MBOM markings dominate the part's, and this keeps the guarantee even if data were edited outside the application.</summary>
    internal static AccessDecision CanRead(Subject s, MbomHead h)
    {
        var d = AccessPolicy.Evaluate(s, h.Markings, PlmAction.Read);
        return d.Permitted ? AccessPolicy.Evaluate(s, h.PartMarkings, PlmAction.Read) : d;
    }

    // ---------------------------------------------------------------- scaffolding

    /// <summary>Loads the MBOM, enforces read access with auditing, runs a read body and audits its outcome.</summary>
    private async Task<Outcome<T>> ReadAsync<T>(
        Subject subject, string number, string? revision, string action, string? correlationId,
        Func<NpgsqlConnection, NpgsqlTransaction, MbomHead, Task<(Outcome<T> Result, (string, string?)[] Details)>> body, CancellationToken ct, PlmAction permission = PlmAction.Read)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await MbomStore.LoadByNumberAsync(conn, tx, number, revision, false, ct);
        if (head is null) return Outcome<T>.NotFound();
        var read = CanRead(subject, head);
        var decision = permission == PlmAction.Read ? read : AccessPolicy.Evaluate(subject, head.Markings, permission);
        if (!read.Permitted || !decision.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", (read.Permitted ? decision : read).Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.NotFound();
        }
        var (result, details) = await body(conn, tx, head);
        await audit.AppendAsync(tx, Entry(subject, action, result.IsOk ? AuditOutcome.Success : AuditOutcome.Failure, number, correlationId,
            result.IsOk ? details : [("error", string.Join(" ", result.Errors)), ("kind", result.Kind.ToString())]), ct);
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>Locks the MBOM row, enforces read and modify access (denials audited) and runs a mutation; its audit event commits with it.</summary>
    internal async Task<Outcome<T>> MutateAsync<T>(
        Subject subject, string number, string action, string? correlationId,
        Func<MbomHead, NpgsqlConnection, NpgsqlTransaction, Task<Step<T>>> body, CancellationToken ct, bool allowAdminWithoutModify = false)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await MbomStore.LoadByNumberAsync(conn, tx, number, null, true, ct);
        if (head is null) return Outcome<T>.NotFound();
        var read = CanRead(subject, head);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.NotFound();
        }
        var modify = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Modify);
        if (!modify.Permitted && allowAdminWithoutModify && AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Administer).Permitted) modify = AccessDecision.Permit;
        if (!modify.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", modify.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.Forbidden(modify.Reasons);
        }
        var step = await body(head, conn, tx);
        if (step.Kind != OutcomeKind.Ok)
        {
            await tx.RollbackAsync(ct);
            await audit.AppendAsync(step.Denied
                ? Entry(subject, action, AuditOutcome.Denied, number, correlationId, step.Details)
                : Entry(subject, action, AuditOutcome.Failure, number, correlationId, ("error", string.Join(" ", step.Errors)), ("kind", step.Kind.ToString())), ct);
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

    // ---------------------------------------------------------------- plants and lists

    public async Task<IReadOnlyList<PlantInfo>> ListPlantsAsync(bool includeRetired = false, CancellationToken ct = default)
    {
        var list = new List<PlantInfo>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT code, name, is_active, release_allows_warnings, sort_order FROM mbom.plant WHERE is_active OR @all ORDER BY sort_order, code", conn);
        cmd.Parameters.AddWithValue("all", includeRetired);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new PlantInfo(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.GetInt32(4)));
        return list;
    }

    public async Task<IReadOnlyList<SupplyTypeInfo>> ListSupplyTypesAsync(bool includeRetired = false, CancellationToken ct = default)
    {
        var list = new List<SupplyTypeInfo>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT code, label, is_active, sort_order, is_system FROM mbom.supply_type WHERE is_active OR @all ORDER BY sort_order, code", conn);
        cmd.Parameters.AddWithValue("all", includeRetired);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new SupplyTypeInfo(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetInt32(3), r.GetBoolean(4)));
        return list;
    }

    /// <summary>Creates or updates a plant (administrators). A plant is retired, never deleted, so existing MBOMs keep a valid plant.</summary>
    public async Task<Outcome<PlantInfo>> SavePlantAsync(Subject subject, SavePlantCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        var code = cmd.Code?.Trim().ToUpperInvariant() ?? "";
        var errors = new List<string>();
        if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z][A-Z0-9-]{1,19}$")) errors.Add("A plant code starts with a letter and has 2 to 20 letters, digits or '-'.");
        if (string.IsNullOrWhiteSpace(cmd.Name) || cmd.Name.Trim().Length > 120) errors.Add("The plant name is required (at most 120 characters).");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Administer))
        {
            await audit.AppendAsync(tx, Entry(subject, "mbom.plant_save", AuditOutcome.Denied, null, correlationId, ("plant", code), ("reasons", AccessPolicy.RoleMissing)), ct);
            await tx.CommitAsync(ct);
            return Outcome<PlantInfo>.Forbidden([AccessPolicy.RoleMissing]);
        }
        if (errors.Count > 0) return Outcome<PlantInfo>.Invalid(errors);
        var before = await MbomStore.LoadPlantAsync(conn, tx, code, ct);
        await using (var up = new NpgsqlCommand(
            """
            INSERT INTO mbom.plant (code, name, is_active, release_allows_warnings, sort_order, updated_at, updated_by) VALUES (@c, @n, @a, @w, @o, @at, @by)
            ON CONFLICT (code) DO UPDATE SET name = @n, is_active = @a, release_allows_warnings = @w, sort_order = @o, updated_at = @at, updated_by = @by
            """, conn, tx))
        {
            up.Parameters.AddWithValue("c", code);
            up.Parameters.AddWithValue("n", cmd.Name.Trim());
            up.Parameters.AddWithValue("a", cmd.IsActive);
            up.Parameters.AddWithValue("w", cmd.ReleaseAllowsWarnings);
            up.Parameters.AddWithValue("o", cmd.SortOrder);
            up.Parameters.AddWithValue("at", clock.GetUtcNow());
            up.Parameters.AddWithValue("by", BomService.DisplayId(subject));
            await up.ExecuteNonQueryAsync(ct);
        }
        var saved = (await MbomStore.LoadPlantAsync(conn, tx, code, ct))!;
        await audit.AppendAsync(tx, Entry(subject, "mbom.plant_save", AuditOutcome.Success, null, correlationId, ("plant", code), ("new", before is null ? "true" : "false"),
            ("active", saved.IsActive.ToString()), ("releaseAllowsWarnings", saved.ReleaseAllowsWarnings.ToString())), ct);
        await tx.CommitAsync(ct);
        return Outcome<PlantInfo>.Ok(saved);
    }

    /// <summary>
    /// Creates or updates a supply type (administrators). The system types Make, Buy and Consume cannot be retired. Retiring a type, or restoring a retired one, needs a
    /// reason; the audit event records the label and state before and after. Nothing changes when the reason is missing (GAP-A-2).
    /// </summary>
    public async Task<Outcome<SupplyTypeInfo>> SaveSupplyTypeAsync(Subject subject, SaveSupplyTypeCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        var code = cmd.Code?.Trim() ?? "";
        var errors = new List<string>();
        if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Za-z][A-Za-z0-9-]{0,19}$")) errors.Add("A supply type code starts with a letter and has at most 20 letters, digits or '-'.");
        if (string.IsNullOrWhiteSpace(cmd.Label) || cmd.Label.Trim().Length > 60) errors.Add("The label is required (at most 60 characters).");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Administer))
        {
            await audit.AppendAsync(tx, Entry(subject, "mbom.supply_type_save", AuditOutcome.Denied, null, correlationId, ("code", code), ("reasons", AccessPolicy.RoleMissing)), ct);
            await tx.CommitAsync(ct);
            return Outcome<SupplyTypeInfo>.Forbidden([AccessPolicy.RoleMissing]);
        }
        if (errors.Count > 0) return Outcome<SupplyTypeInfo>.Invalid(errors);
        if (!cmd.IsActive && code is "Make" or "Buy" or "Consume") return Outcome<SupplyTypeInfo>.Invalid([$"The system supply type {code} cannot be retired."]);
        (string Label, bool Active, int Order)? before = null;
        await using (var prior = new NpgsqlCommand("SELECT label, is_active, sort_order FROM mbom.supply_type WHERE code = @c", conn, tx))
        {
            prior.Parameters.AddWithValue("c", code);
            await using var pr = await prior.ExecuteReaderAsync(ct);
            if (await pr.ReadAsync(ct)) before = (pr.GetString(0), pr.GetBoolean(1), pr.GetInt32(2));
        }
        var reason = cmd.Reason?.Trim();
        if (before is not null && before.Value.Active != cmd.IsActive && string.IsNullOrEmpty(reason))
            return Outcome<SupplyTypeInfo>.Invalid([$"A reason is required to {(cmd.IsActive ? "restore" : "retire")} a supply type."]);
        if (reason is { Length: > 1000 }) return Outcome<SupplyTypeInfo>.Invalid(["The reason is at most 1000 characters."]);
        await using (var up = new NpgsqlCommand(
            """
            INSERT INTO mbom.supply_type (code, label, sort_order, is_active, updated_at, updated_by) VALUES (@c, @l, @o, @a, @at, @by)
            ON CONFLICT (code) DO UPDATE SET label = @l, sort_order = @o, is_active = @a, updated_at = @at, updated_by = @by
            """, conn, tx))
        {
            up.Parameters.AddWithValue("c", code);
            up.Parameters.AddWithValue("l", cmd.Label.Trim());
            up.Parameters.AddWithValue("o", cmd.SortOrder);
            up.Parameters.AddWithValue("a", cmd.IsActive);
            up.Parameters.AddWithValue("at", clock.GetUtcNow());
            up.Parameters.AddWithValue("by", BomService.DisplayId(subject));
            await up.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "mbom.supply_type_save", AuditOutcome.Success, null, correlationId, ("code", code), ("new", before is null ? "true" : "false"),
            ("labelBefore", before?.Label), ("labelAfter", cmd.Label.Trim()), ("activeBefore", before?.Active.ToString()), ("active", cmd.IsActive.ToString()), ("reason", reason)), ct);
        await tx.CommitAsync(ct);
        return Outcome<SupplyTypeInfo>.Ok((await ListSupplyTypesAsync(true, ct)).Single(s => s.Code == code));
    }

    // ---------------------------------------------------------------- create (manual)

    public async Task<Outcome<MbomDetail>> CreateAsync(Subject subject, CreateMbomCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var step = await CreateCoreAsync(conn, tx, subject, cmd.PartNumber, cmd.Plant, cmd.Markings, cmd.ConfirmRaiseMarkings, cmd.Description, null, null, [], correlationId, ct);
        if (step.Kind != OutcomeKind.Ok)
        {
            await tx.RollbackAsync(ct);
            await audit.AppendAsync(step.Denied
                ? Entry(subject, "mbom.create", AuditOutcome.Denied, null, correlationId, [("part", cmd.PartNumber), ("plant", cmd.Plant), .. step.Details])
                : Entry(subject, "mbom.create", AuditOutcome.Failure, null, correlationId, ("part", cmd.PartNumber), ("plant", cmd.Plant), ("error", string.Join(" ", step.Errors))), ct);
            return step.Kind == OutcomeKind.NotFound ? Outcome<MbomDetail>.NotFound()
                : step.Kind == OutcomeKind.Conflict ? Outcome<MbomDetail>.Conflict(step.Errors[0]) : Outcome<MbomDetail>.Invalid(step.Errors);
        }
        var head = step.Value!;
        await audit.AppendAsync(tx, Entry(subject, "mbom.create", AuditOutcome.Success, head.Number, correlationId, [("part", head.PartNumber), ("plant", head.Plant), ("banner", head.Markings.BannerText()), .. step.Details]), ct);
        var detail = await LoadDetailAsync(conn, tx, subject, head, ct);
        await tx.CommitAsync(ct);
        return Outcome<MbomDetail>.Ok(detail);
    }

    /// <summary>
    /// Creates the header, revision A and iteration 1 (with the given lines). The markings must dominate the part's; a gap is refused unless the caller
    /// confirms the minimal cover. Used by create and by generate.
    /// </summary>
    internal async Task<Step<MbomHead>> CreateCoreAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string partNumber, string plantCode, Markings? requested, bool confirmRaise, string? description,
        Guid? derivedFrom, string? derivedSpec, IReadOnlyList<NewMbomLine> lines, string? correlationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(partNumber)) return Step<MbomHead>.Invalid("Name the part the MBOM is for.");
        if ((description?.Length ?? 0) > 4000) return Step<MbomHead>.Invalid("The description must be at most 4000 characters.");
        var part = await MbomStore.LoadPartByNumberAsync(conn, tx, partNumber.Trim(), ct);
        if (part is null || !AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Read).Permitted)
            return Step<MbomHead>.Hidden($"Part '{partNumber}' was not found, or you are not authorized to see it.", "part.not_readable");
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Modify)) return Step<MbomHead>.Hidden("Your roles do not permit creating an MBOM.", AccessPolicy.RoleMissing);
        if (part.PartType != "Assembly") return Step<MbomHead>.Invalid($"{part.Number} is a {part.PartType}; only Assembly parts have a manufacturing BOM.");
        var plant = await MbomStore.LoadPlantAsync(conn, tx, plantCode?.Trim().ToUpperInvariant() ?? "", ct);
        if (plant is null || !plant.IsActive) return Step<MbomHead>.Invalid($"'{plantCode}' is not an active plant.");
        var existing = await MbomStore.LoadByPartAsync(conn, tx, part.Id, plant.Code, false, ct);
        if (existing is not null) return Step<MbomHead>.Conflict($"{part.Number} already has the MBOM {existing.Number} for plant {plant.Code}.");

        var markings = requested ?? part.Markings;
        var gaps = MarkingDominance.Gaps(markings, part.Markings);
        if (gaps.Count > 0)
        {
            var cover = MarkingDominance.MinimalCover(markings, [part.Markings]);
            if (!cover.IsSuccess) return Step<MbomHead>.Invalid([.. cover.Errors]);
            if (!confirmRaise)
                return Step<MbomHead>.Invalid($"The markings {markings.BannerText()} do not cover the part {part.Number}. An MBOM must be at least as restrictive as its part; use {cover.Value.BannerText()}.");
            markings = cover.Value;
        }
        if (!AccessPolicy.Evaluate(subject, markings, PlmAction.Modify).Permitted)
            return Step<MbomHead>.Invalid($"Markings {markings.BannerText()} would lock you out of the MBOM you are creating.");

        var lifecycle = await Lifecycle.LoadAsync(conn, tx, await Lifecycle.CurrentIdAsync(conn, tx, "mbom-default", ct), ct) ?? throw new InvalidOperationException("The MBOM lifecycle is missing.");
        var number = await Numbering.NextAsync(conn, tx, ObjectType, ct);
        var now = clock.GetUtcNow();
        var who = BomService.DisplayId(subject);
        var id = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        await using (var ins = new NpgsqlCommand(
            $"""
            INSERT INTO mbom.mbom (id, number, part_master_id, plant, lifecycle_id, {MarkingColumns.InsertColumns}, created_at, created_by)
            VALUES (@id, @number, @part, @plant, @lc, {MarkingColumns.InsertValues}, @at, @by);
            INSERT INTO mbom.revision (id, mbom_id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by)
            VALUES (@rid, @id, @rev, @state, true, 1, @at, @by, @at, @by)
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("number", number);
            ins.Parameters.AddWithValue("part", part.Id);
            ins.Parameters.AddWithValue("plant", plant.Code);
            ins.Parameters.AddWithValue("lc", lifecycle.Id);
            foreach (var (k, v) in MarkingColumns.Parameters(markings)) ins.Parameters.AddWithValue(k, v);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", who);
            ins.Parameters.AddWithValue("rid", revisionId);
            ins.Parameters.AddWithValue("rev", RevisionScheme.First);
            ins.Parameters.AddWithValue("state", lifecycle.Initial.State);
            await ins.ExecuteNonQueryAsync(ct);
        }
        var validation = await MbomStore.ValidateLinksAsync(conn, tx, derivedFrom, lines, ct);
        if (validation.Count > 0) return Step<MbomHead>.Invalid([.. validation]);
        // The first iteration is written with revision latest_iteration = 1 already set above.
        await MbomStore.WriteIterationAsync(conn, tx, revisionId, 1, description?.Trim() ?? "", "Created", derivedFrom, derivedSpec, lines, now, who, ct);
        var head = (await MbomStore.LoadByNumberAsync(conn, tx, number, null, false, ct))!;
        return Step<MbomHead>.Done(head, ("lines", Fmt(lines.Count)), ("derivedSpec", derivedSpec));
    }

    // ---------------------------------------------------------------- read

    public Task<Outcome<MbomDetail>> GetAsync(Subject subject, string number, string? revision, string? correlationId, CancellationToken ct = default) =>
        ReadAsync<MbomDetail>(subject, number, revision, "mbom.read", correlationId, async (conn, tx, head) =>
        {
            var detail = await LoadDetailAsync(conn, tx, subject, head, ct);
            return (Outcome<MbomDetail>.Ok(detail), [("revision", head.Label), ("banner", detail.Summary.Banner)]);
        }, ct);

    internal static async Task<MbomDetail> LoadDetailAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, MbomHead head, CancellationToken ct)
    {
        var lifecycle = await Lifecycle.LoadAsync(conn, tx, head.LifecycleId, ct) ?? throw new InvalidOperationException("The MBOM lifecycle is missing.");
        var plant = await MbomStore.LoadPlantAsync(conn, tx, head.Plant, ct) ?? throw new InvalidOperationException("The plant of an MBOM is missing.");
        var revisions = new List<MbomRevisionInfo>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT id, revision, lifecycle_state, is_latest, latest_iteration, state_changed_at, state_changed_by FROM mbom.revision WHERE mbom_id = @m ORDER BY length(revision) DESC, revision DESC", conn, tx))
        {
            cmd.Parameters.AddWithValue("m", head.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                revisions.Add(new MbomRevisionInfo(r.GetGuid(0), r.GetString(1), r.GetString(2), lifecycle.State(r.GetString(2))?.Label ?? r.GetString(2), r.GetBoolean(3), r.GetInt32(4),
                    r.GetFieldValue<DateTimeOffset>(5), r.GetString(6)));
        }
        var displayed = revisions.Single(r => r.Id == head.RevisionId);
        var iterations = new List<MbomIterationInfo>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT i.id, i.iteration, i.comment, i.derived_part_iteration_id, i.derived_spec, i.line_count, i.link_count, i.content_sha256, i.created_at, i.created_by,
                   dr.revision || '.' || di.iteration
            FROM mbom.iteration i
            LEFT JOIN plm.part_iteration di ON di.id = i.derived_part_iteration_id
            LEFT JOIN plm.part_revision dr ON dr.id = di.revision_id
            WHERE i.revision_id = @r ORDER BY i.iteration DESC
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("r", head.RevisionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                iterations.Add(new MbomIterationInfo(r.GetGuid(0), r.GetInt32(1), $"{head.Revision}.{Fmt(r.GetInt32(1))}", r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5), r.GetInt32(6), r.GetString(7), r.GetFieldValue<DateTimeOffset>(8), r.GetString(9)));
        }
        var iteration = iterations.First(i => i.Id == head.IterationId);

        string? checkoutDerived = null, checkoutSpec = null;
        if (head.CheckoutUser is not null)
        {
            await using var cmd = new NpgsqlCommand("SELECT derived_part_iteration_id, derived_spec FROM mbom.checkout WHERE revision_id = @r", conn, tx);
            cmd.Parameters.AddWithValue("r", head.RevisionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct) && !r.IsDBNull(0)) { checkoutSpec = r.GetString(1); checkoutDerived = r.GetGuid(0).ToString("D"); }
        }
        if (checkoutDerived is not null) checkoutDerived = await MbomStore.IterationLabelAsync(conn, tx, Guid.Parse(checkoutDerived), ct);
        var checkout = head.CheckoutUser is null ? null
            : new MbomCheckoutInfo(head.CheckoutUser, head.CheckoutUserName, head.CheckoutAt!.Value, head.CheckoutUser == subject.Id, checkoutDerived, checkoutSpec);

        var modify = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Modify);
        var isLatest = displayed.IsLatest;
        var transitions = new List<MbomTransitionInfo>();
        if (isLatest)
            foreach (var t in lifecycle.Transitions.Where(t => t.From == head.State).OrderBy(t => lifecycle.State(t.To)?.SortOrder))
            {
                var label = lifecycle.State(t.To)?.Label ?? t.To;
                var (allowed, reason) = t.Via != "manual" ? (false, t.To == MbomStates.UnderReview || t.To == MbomStates.Released || t.To == MbomStates.InWork ? "Taken by submitting the MBOM for review." : "Only through the workflow.")
                    : !modify.Permitted ? (false, "You may not modify this MBOM.")
                    : head.CheckoutUser is not null ? (false, "Check in or undo the check-out first.") : (true, null);
                transitions.Add(new MbomTransitionInfo(t.To, label, allowed, reason));
            }
        var last = await LastRunBriefAsync(conn, tx, head, plant, ct);
        var blocker = isLatest && head.State == MbomStates.InWork ? await ReleaseBlockerAsync(conn, tx, head, plant, ct) : null;
        var banner = head.Markings.BannerText();
        var summary = new MbomSummary(head.Id, head.Number, head.PartNumber, head.PartName, head.Plant, head.Revision, head.LatestIteration, head.State,
            lifecycle.State(head.State)?.Label ?? head.State, iteration.DerivedFrom, last?.Status, head.CheckoutUserName ?? head.CheckoutUser, banner, head.Markings);
        string description;
        await using (var cmd = new NpgsqlCommand("SELECT description FROM mbom.iteration WHERE id = @i", conn, tx))
        {
            cmd.Parameters.AddWithValue("i", head.IterationId);
            description = (string)(await cmd.ExecuteScalarAsync(ct))!;
        }
        return new MbomDetail(summary, description, plant, head.PartName is null ? "" : await PartStateAsync(conn, tx, head, ct), revisions, displayed, iterations, iteration, checkout,
            transitions, new MbomPermissions(modify.Permitted, AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Administer).Permitted, [.. modify.Reasons]), last, blocker);
    }

    private static async Task<string> PartStateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, MbomHead head, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT r.revision || ' (' || r.lifecycle_state || ')' FROM plm.part_revision r WHERE r.master_id = @m AND r.is_latest", conn, tx);
        cmd.Parameters.AddWithValue("m", head.PartMasterId);
        return (string?)await cmd.ExecuteScalarAsync(ct) ?? "";
    }

    /// <summary>The sort keys the MBOM list accepts (GAP-A-1); the part is inner-joined with the read filter, so only readable parts take part in the order.</summary>
    public static readonly SortMap SortKeys = new(
        ("mbom", ["mm.number"]), ("part", ["pm.number"]), ("plant", ["mm.plant"]), ("state", ["lower(COALESCE(ls.label, r.lifecycle_state))"]));

    public async Task<Outcome<MbomSearchResult>> SearchAsync(Subject subject, MbomSearch search, string? correlationId, CancellationToken ct = default)
    {
        var limit = Math.Clamp(search.Limit, 1, 200);
        var offset = Math.Max(0, search.Offset);
        var q = (search.Query ?? "").Trim();
        var like = "%" + q.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%";
        var mbomFilter = AccessPolicy.BuildReadFilter(subject, "mm");
        var partFilter = AccessPolicy.BuildReadFilter(subject, "pm");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var items = new List<MbomSummary>();
        long total = 0;
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT mm.id, mm.number, pm.number, pm.name, mm.plant, r.revision, r.latest_iteration, r.lifecycle_state, COALESCE(ls.label, r.lifecycle_state),
                   COALESCE(c.user_name, c.user_id), {MarkingColumns.Select("mm")}, count(*) OVER ()
            FROM mbom.mbom mm
            JOIN plm.part_master pm ON pm.id = mm.part_master_id AND {partFilter.Sql}
            JOIN mbom.revision r ON r.mbom_id = mm.id AND r.is_latest
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = mm.lifecycle_id AND ls.state = r.lifecycle_state
            LEFT JOIN mbom.checkout c ON c.revision_id = r.id
            WHERE {mbomFilter.Sql}
              AND (@q = '' OR mm.number ILIKE @like OR pm.number ILIKE @like OR pm.name ILIKE @like)
              AND (@plant::text IS NULL OR mm.plant = @plant) AND (@state::text IS NULL OR r.lifecycle_state = @state)
              AND (@part::text IS NULL OR pm.number = @part)
            ORDER BY {SortKeys.OrderBy(search.Sort, "mm.number", "mm.number")}
            LIMIT @limit OFFSET @offset
            """, conn, tx))
        {
            foreach (var (k, v) in mbomFilter.Parameters.Concat(partFilter.Parameters)) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("q", q);
            cmd.Parameters.AddWithValue("like", like);
            cmd.Parameters.Add(new NpgsqlParameter("plant", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)search.Plant ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("state", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)search.State ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("part", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)search.PartNumber ?? DBNull.Value });
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var m = MarkingColumns.Read(r, 10);
                items.Add(new MbomSummary(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6), r.GetString(7), r.GetString(8), null, null,
                    r.IsDBNull(9) ? null : r.GetString(9), m.BannerText(), m));
                total = r.GetInt64(10 + MarkingColumns.Count);
            }
        }
        // The reconciliation status of each listed MBOM (its latest iteration's most recent run).
        for (var i = 0; i < items.Count; i++)
        {
            await using var cmd = new NpgsqlCommand(
                """
                SELECT rr.status FROM mbom.reconciliation_run rr JOIN mbom.revision r ON r.id = rr.revision_id AND r.is_latest
                JOIN mbom.iteration it ON it.id = rr.iteration_id AND it.iteration = r.latest_iteration
                WHERE rr.mbom_id = @m ORDER BY rr.run_at DESC LIMIT 1
                """, conn, tx);
            cmd.Parameters.AddWithValue("m", items[i].Id);
            items[i] = items[i] with { ReconciliationStatus = (string?)await cmd.ExecuteScalarAsync(ct) };
        }
        await audit.AppendAsync(tx, Entry(subject, "mbom.search", AuditOutcome.Success, null, correlationId, ("query", q), ("plant", search.Plant), ("state", search.State), ("sort", search.Sort is null ? null : $"{search.Sort.Key} {search.Sort.DirectionText}"),
            ("returned", Fmt(items.Count)), ("numbers", string.Join(",", items.Select(i => i.Number)))), ct);
        await tx.CommitAsync(ct);
        return Outcome<MbomSearchResult>.Ok(new MbomSearchResult(items, total, Markings.AggregateBanner(items.Select(i => i.Markings))));
    }

    // ---------------------------------------------------------------- check-out, working copy, check-in

    public Task<Outcome<MbomDetail>> CheckOutAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<MbomDetail>(subject, number, "mbom.checkout", correlationId, async (head, conn, tx) =>
        {
            var lifecycle = await Lifecycle.LoadAsync(conn, tx, head.LifecycleId, ct) ?? throw new InvalidOperationException("Lifecycle missing.");
            if (lifecycle.IsLocked(head.State)) return Step<MbomDetail>.Conflict($"Revision {head.Revision} is {lifecycle.State(head.State)?.Label} and locked; it cannot be checked out.");
            if (head.CheckoutUser is not null) return Step<MbomDetail>.Conflict($"Already checked out by {head.CheckoutUserName ?? head.CheckoutUser}.");
            await using (var cmd = new NpgsqlCommand(
                """
                INSERT INTO mbom.checkout (revision_id, user_id, user_name, checked_out_at, description, derived_part_iteration_id, derived_spec)
                SELECT @rid, @user, @name, @at, i.description, i.derived_part_iteration_id, i.derived_spec FROM mbom.iteration i WHERE i.id = @iid
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("rid", head.RevisionId);
                cmd.Parameters.AddWithValue("iid", head.IterationId);
                cmd.Parameters.AddWithValue("user", subject.Id);
                cmd.Parameters.AddWithValue("name", (object?)subject.DisplayName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var copy = new NpgsqlCommand(
                $"""
                INSERT INTO mbom.working_usage (id, checkout_revision_id, child_master_id, quantity, unit, find_number, reference_designators, scrap_factor, phantom, supply_type, operation_ref,
                                                mbom_only, notes, eff_kind, eff_context, eff_from, eff_to, created_at, created_by)
                SELECT id, @rid, child_master_id, quantity, unit, find_number, reference_designators, scrap_factor, phantom, supply_type, operation_ref,
                       mbom_only, notes, eff_kind, eff_context, eff_from, eff_to, created_at, created_by
                FROM mbom.usage WHERE iteration_id = @iid;
                INSERT INTO mbom.working_link (working_usage_id, ebom_usage_id, status, reason)
                SELECT l.mbom_usage_id, l.ebom_usage_id, l.status, l.reason FROM mbom.usage_link l JOIN mbom.usage u ON u.id = l.mbom_usage_id WHERE u.iteration_id = @iid;
                """, conn, tx))
            {
                copy.Parameters.AddWithValue("rid", head.RevisionId);
                copy.Parameters.AddWithValue("iid", head.IterationId);
                await copy.ExecuteNonQueryAsync(ct);
            }
            var fresh = (await MbomStore.LoadByNumberAsync(conn, tx, number, null, false, ct))!;
            return Step<MbomDetail>.Done(await LoadDetailAsync(conn, tx, subject, fresh, ct), ("revision", head.Label));
        }, ct);

    public Task<Outcome<MbomDetail>> UndoCheckOutAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<MbomDetail>(subject, number, "mbom.undo_checkout", correlationId, async (head, conn, tx) =>
        {
            if (head.CheckoutUser is null) return Step<MbomDetail>.Conflict("The MBOM is not checked out.");
            var isAdmin = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Administer).Permitted;
            if (head.CheckoutUser != subject.Id && !isAdmin) return new Step<MbomDetail>(OutcomeKind.Invalid, ["Only the user who checked it out (or an administrator) can undo the check-out."], default, [("reasons", "checkout.not_owner")], true);
            await using (var del = new NpgsqlCommand("DELETE FROM mbom.checkout WHERE revision_id = @rid", conn, tx))
            {
                del.Parameters.AddWithValue("rid", head.RevisionId);
                await del.ExecuteNonQueryAsync(ct); // the working lines and links cascade
            }
            var fresh = (await MbomStore.LoadByNumberAsync(conn, tx, number, null, false, ct))!;
            return Step<MbomDetail>.Done(await LoadDetailAsync(conn, tx, subject, fresh, ct), ("checkedOutBy", head.CheckoutUserName ?? head.CheckoutUser), ("override", (head.CheckoutUser != subject.Id).ToString()));
        }, ct, allowAdminWithoutModify: true);

    public Task<Outcome<MbomDetail>> CheckInAsync(Subject subject, string number, string? comment, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<MbomDetail>(subject, number, "mbom.checkin", correlationId, async (head, conn, tx) =>
        {
            if (head.CheckoutUser != subject.Id) return Step<MbomDetail>.Conflict("Only the user who checked the MBOM out can check it in.");
            if ((comment?.Length ?? 0) > 1000) return Step<MbomDetail>.Invalid("The check-in comment must be at most 1000 characters.");
            var co = await LoadCheckoutAsync(conn, tx, head.RevisionId, ct);
            var content = await MbomStore.ReadContentAsync(conn, tx, true, head.RevisionId, ct);
            if (content.Count > MbomLimits.MaxLines) return Step<MbomDetail>.Conflict($"An MBOM can have at most {MbomLimits.MaxLines} lines.");
            var problems = await MbomStore.ValidateLinksAsync(conn, tx, co.DerivedFrom, content, ct);
            if (problems.Count > 0) return Step<MbomDetail>.Invalid([.. problems]);
            // Every stored row is immutable, so the iteration gets fresh row ids; the lines keep their find numbers and links.
            var fresh = content.Select(l => l with { Id = Guid.CreateVersion7() }).ToList();
            var next = head.LatestIteration + 1;
            await MbomStore.WriteIterationAsync(conn, tx, head.RevisionId, next, co.Description, comment, co.DerivedFrom, co.DerivedSpec, fresh, clock.GetUtcNow(), BomService.DisplayId(subject), ct);
            await using (var del = new NpgsqlCommand("DELETE FROM mbom.checkout WHERE revision_id = @rid", conn, tx))
            {
                del.Parameters.AddWithValue("rid", head.RevisionId);
                await del.ExecuteNonQueryAsync(ct);
            }
            var updated = (await MbomStore.LoadByNumberAsync(conn, tx, number, null, false, ct))!;
            return Step<MbomDetail>.Done(await LoadDetailAsync(conn, tx, subject, updated, ct), ("revision", updated.Label), ("lines", Fmt(content.Count)), ("comment", comment));
        }, ct);

    internal sealed record CheckoutRow(Guid? DerivedFrom, string? DerivedSpec, string Description);

    internal static async Task<CheckoutRow> LoadCheckoutAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid revisionId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT derived_part_iteration_id, derived_spec, description FROM mbom.checkout WHERE revision_id = @r", conn, tx);
        cmd.Parameters.AddWithValue("r", revisionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new CheckoutRow(r.IsDBNull(0) ? null : r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2));
    }

    // ---------------------------------------------------------------- lifecycle, markings, revise

    /// <summary>Takes a manual lifecycle transition (Obsolete). Review states are taken only through the workflow.</summary>
    public Task<Outcome<MbomDetail>> TransitionAsync(Subject subject, string number, string toState, string? reason, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<MbomDetail>(subject, number, "mbom.transition", correlationId, async (head, conn, tx) =>
        {
            var lifecycle = await Lifecycle.LoadAsync(conn, tx, head.LifecycleId, ct) ?? throw new InvalidOperationException("Lifecycle missing.");
            var target = lifecycle.State(toState);
            if (target is null) return Step<MbomDetail>.Invalid($"'{toState}' is not a state of the MBOM lifecycle.");
            var t = lifecycle.Transitions.FirstOrDefault(x => x.From == head.State && x.To == toState);
            if (t is null) return Step<MbomDetail>.Conflict($"No transition from {lifecycle.State(head.State)?.Label} to {target.Label}.");
            if (t.Via != "manual") return Step<MbomDetail>.Conflict($"{target.Label} is reached by submitting the MBOM for review, not by a manual transition.");
            if (head.CheckoutUser is not null) return Step<MbomDetail>.Conflict($"Checked out by {head.CheckoutUserName ?? head.CheckoutUser}; check in or undo the check-out first.");
            if (string.IsNullOrWhiteSpace(reason)) return Step<MbomDetail>.Invalid("A reason is required.");
            await using (var cmd = new NpgsqlCommand(
                "UPDATE mbom.revision SET lifecycle_state = @s, state_changed_at = @at, state_changed_by = @by, state_reason = @reason WHERE id = @rid", conn, tx))
            {
                cmd.Parameters.AddWithValue("s", toState);
                cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
                cmd.Parameters.AddWithValue("by", BomService.DisplayId(subject));
                cmd.Parameters.AddWithValue("reason", reason.Trim());
                cmd.Parameters.AddWithValue("rid", head.RevisionId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            var updated = (await MbomStore.LoadByNumberAsync(conn, tx, number, null, false, ct))!;
            return Step<MbomDetail>.Done(await LoadDetailAsync(conn, tx, subject, updated, ct), ("revision", head.Revision), ("from", head.State), ("to", toState), ("reason", reason.Trim()));
        }, ct);

    public Task<Outcome<MbomDetail>> ChangeMarkingsAsync(Subject subject, string number, Markings markings, string? reason, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<MbomDetail>(subject, number, "mbom.markings_change", correlationId, async (head, conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) return Step<MbomDetail>.Invalid("A reason is required to change markings.");
            var lifecycle = await Lifecycle.LoadAsync(conn, tx, head.LifecycleId, ct) ?? throw new InvalidOperationException("Lifecycle missing.");
            if (lifecycle.IsLocked(head.State)) return Step<MbomDetail>.Conflict($"Revision {head.Revision} is {lifecycle.State(head.State)?.Label}; the markings of a locked MBOM cannot change.");
            if (markings == head.Markings) return Step<MbomDetail>.Conflict("The markings are unchanged.");
            var gaps = MarkingDominance.Gaps(markings, head.PartMarkings);
            if (gaps.Count > 0) return Step<MbomDetail>.Invalid($"The markings must be at least as restrictive as the part {head.PartNumber} ({string.Join(", ", gaps)}).");
            var after = AccessPolicy.Evaluate(subject, markings, PlmAction.Modify);
            if (!after.Permitted) return new Step<MbomDetail>(OutcomeKind.Invalid, ["With the new markings you could no longer modify this MBOM."], default, [("reasons", string.Join(",", after.Reasons.Select(r => "new_markings:" + r)))], true);
            await using (var cmd = new NpgsqlCommand($"UPDATE mbom.mbom SET {MarkingColumns.UpdateSet} WHERE id = @id", conn, tx))
            {
                foreach (var (k, v) in MarkingColumns.Parameters(markings)) cmd.Parameters.AddWithValue(k, v);
                cmd.Parameters.AddWithValue("id", head.Id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            var updated = (await MbomStore.LoadByNumberAsync(conn, tx, number, null, false, ct))!;
            return Step<MbomDetail>.Done(await LoadDetailAsync(conn, tx, subject, updated, ct), ("before", head.Markings.BannerText()), ("after", markings.BannerText()), ("reason", reason.Trim()));
        }, ct);

    /// <summary>
    /// Starts the next revision of a Released MBOM, independent of the design revision: manufacturing engineering revises its own structure.
    /// The new revision starts as In Work with the lines and links of the released one; the released revision is superseded when the new one releases.
    /// </summary>
    public Task<Outcome<MbomDetail>> ReviseAsync(Subject subject, string number, string? reason, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<MbomDetail>(subject, number, "mbom.revise", correlationId, async (head, conn, tx) =>
        {
            if (head.State != MbomStates.Released) return Step<MbomDetail>.Conflict($"Revision {head.Revision} is not Released; only a Released MBOM is revised (others are edited in place).");
            if (head.CheckoutUser is not null) return Step<MbomDetail>.Conflict("The MBOM is checked out.");
            if (string.IsNullOrWhiteSpace(reason)) return Step<MbomDetail>.Invalid("A reason for the new revision is required.");
            var next = RevisionScheme.Next(head.Revision);
            var lifecycle = await Lifecycle.LoadAsync(conn, tx, head.LifecycleId, ct) ?? throw new InvalidOperationException("Lifecycle missing.");
            var now = clock.GetUtcNow();
            var who = BomService.DisplayId(subject);
            var content = (await MbomStore.ReadContentAsync(conn, tx, false, head.IterationId, ct)).Select(l => l with { Id = Guid.CreateVersion7() }).ToList();
            Guid? derived; string? spec; string description;
            await using (var cmd = new NpgsqlCommand("SELECT derived_part_iteration_id, derived_spec, description FROM mbom.iteration WHERE id = @i", conn, tx))
            {
                cmd.Parameters.AddWithValue("i", head.IterationId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                (derived, spec, description) = (r.IsDBNull(0) ? null : r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2));
            }
            var rid = Guid.CreateVersion7();
            await using (var cmd = new NpgsqlCommand(
                """
                UPDATE mbom.revision SET is_latest = false WHERE id = @old;
                INSERT INTO mbom.revision (id, mbom_id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by, state_reason)
                VALUES (@rid, @m, @rev, @state, true, 1, @at, @by, @at, @by, @reason)
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("old", head.RevisionId);
                cmd.Parameters.AddWithValue("rid", rid);
                cmd.Parameters.AddWithValue("m", head.Id);
                cmd.Parameters.AddWithValue("rev", next);
                cmd.Parameters.AddWithValue("state", lifecycle.Initial.State);
                cmd.Parameters.AddWithValue("at", now);
                cmd.Parameters.AddWithValue("by", who);
                cmd.Parameters.AddWithValue("reason", reason.Trim());
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await MbomStore.WriteIterationAsync(conn, tx, rid, 1, description, $"Revised from {head.Revision}: {reason.Trim()}", derived, spec, content, now, who, ct);
            var updated = (await MbomStore.LoadByNumberAsync(conn, tx, number, null, false, ct))!;
            return Step<MbomDetail>.Done(await LoadDetailAsync(conn, tx, subject, updated, ct), ("from", head.Revision), ("to", next), ("reason", reason.Trim()), ("lines", Fmt(content.Count)));
        }, ct);
}
