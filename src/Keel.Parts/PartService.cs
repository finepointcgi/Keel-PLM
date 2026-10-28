using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Parts;

/// <summary>
/// Part commands and queries. Every call evaluates the access policy (ADR-0004) and writes an audit event in the same
/// transaction as its change (ADR-0005), including denials. Per-part operations serialize on the master row.
/// </summary>
public sealed partial class PartService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock)
{
    public const string ObjectType = "part";
    public const string DefaultLifecycle = "part-default";
    public const int MaxSearchLimit = 200;

    // ---------------------------------------------------------------- create

    public async Task<Outcome<PartDetail>> CreateAsync(Subject subject, CreatePartCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var name = cmd.Name?.Trim() ?? "";
        if (name.Length is 0 or > 200) errors.Add("Name is required (at most 200 characters).");
        if (!PartTypes.All.Contains(cmd.PartType)) errors.Add($"Part type must be one of: {string.Join(", ", PartTypes.All)}.");
        if (!PartSources.All.Contains(cmd.Source)) errors.Add("Source must be Make or Buy.");
        if (!UnitsOfMeasure.IsValid(cmd.DefaultUnit)) errors.Add($"Unit must be one of: {string.Join(", ", UnitsOfMeasure.All)}.");
        if (cmd.Number is not null && !NumberPattern().IsMatch(cmd.Number)) errors.Add("Explicit part numbers may contain only A-Z, 0-9, '-', '.', '_' (max 40).");
        if ((cmd.Description?.Length ?? 0) > 4000) errors.Add("Description must be at most 4000 characters.");

        var decision = AccessPolicy.Evaluate(subject, cmd.Markings, PlmAction.Modify);
        if (cmd.Number is not null && !AccessPolicy.HasRoleFor(subject, PlmAction.Administer))
            decision = decision with { Permitted = false, Reasons = decision.Reasons.Add("number.explicit_requires_admin") };

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var defs = await AttributeValidator.LoadAsync(conn, tx, ObjectType, ct);
        var (attributes, attrErrors) = AttributeValidator.Validate(defs, cmd.Attributes);
        errors.AddRange(attrErrors);
        if (errors.Count > 0) return Outcome<PartDetail>.Invalid(errors);

        if (!decision.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "part.create", AuditOutcome.Denied, null, correlationId,
                ("reasons", string.Join(",", decision.Reasons)), ("markings", cmd.Markings.BannerText())), ct);
            await tx.CommitAsync(ct);
            return Outcome<PartDetail>.Forbidden(decision.Reasons);
        }

        var lifecycle = await Lifecycle.LoadAsync(conn, tx, DefaultLifecycle, ct) ?? throw new InvalidOperationException("Default part lifecycle is missing.");
        var number = cmd.Number;
        if (number is null)
        {
            await using var seq = new NpgsqlCommand("SELECT nextval('plm.part_number_seq')", conn, tx);
            number = "P-" + ((long)(await seq.ExecuteScalarAsync(ct))!).ToString("D6", CultureInfo.InvariantCulture);
        }
        else
        {
            await using var exists = new NpgsqlCommand("SELECT 1 FROM plm.part_master WHERE number = @n", conn, tx);
            exists.Parameters.AddWithValue("n", number);
            if (await exists.ExecuteScalarAsync(ct) is not null) return Outcome<PartDetail>.Conflict($"Part number {number} already exists.");
        }

        var now = clock.GetUtcNow();
        var who = DisplayId(subject);
        var masterId = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        await using (var ins = new NpgsqlCommand(
            $"""
            INSERT INTO plm.part_master (id, number, name, part_type, default_unit, source, lifecycle_id, {MarkingColumns.InsertColumns}, created_at, created_by)
            VALUES (@id, @number, @name, @type, @unit, @source, @lc, {MarkingColumns.InsertValues}, @at, @by);
            INSERT INTO plm.part_revision (id, master_id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by)
            VALUES (@rid, @id, @rev, @state, true, 1, @at, @by, @at, @by);
            INSERT INTO plm.part_iteration (id, revision_id, iteration, description, attributes, comment, created_at, created_by)
            VALUES (@iid, @rid, 1, @desc, @attrs::jsonb, 'Created', @at, @by);
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("id", masterId);
            ins.Parameters.AddWithValue("number", number);
            ins.Parameters.AddWithValue("name", name);
            ins.Parameters.AddWithValue("type", cmd.PartType);
            ins.Parameters.AddWithValue("unit", cmd.DefaultUnit);
            ins.Parameters.AddWithValue("source", cmd.Source);
            ins.Parameters.AddWithValue("lc", lifecycle.Id);
            foreach (var (k, v) in MarkingColumns.Parameters(cmd.Markings)) ins.Parameters.AddWithValue(k, v);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", who);
            ins.Parameters.AddWithValue("rid", revisionId);
            ins.Parameters.AddWithValue("rev", RevisionScheme.First);
            ins.Parameters.AddWithValue("state", lifecycle.Initial.State);
            ins.Parameters.AddWithValue("iid", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("desc", cmd.Description?.Trim() ?? "");
            ins.Parameters.AddWithValue("attrs", attributes.ToJsonString());
            await ins.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "part.create", AuditOutcome.Success, number, correlationId,
            ("revision", "A.1"), ("markings", cmd.Markings.BannerText()), ("name", name)), ct);
        var detail = await LoadDetailAsync(conn, tx, subject, number, null, ct);
        await tx.CommitAsync(ct);
        return Outcome<PartDetail>.Ok(detail!);
    }

    // ---------------------------------------------------------------- read

    public async Task<Outcome<PartDetail>> GetAsync(Subject subject, string number, string? revision, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await LoadHeadAsync(conn, tx, number, forUpdate: false, ct);
        if (head is null) return Outcome<PartDetail>.NotFound();
        var decision = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!decision.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "part.read", AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", decision.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<PartDetail>.NotFound();
        }
        var detail = await LoadDetailAsync(conn, tx, subject, number, revision, ct);
        if (detail is null) return Outcome<PartDetail>.NotFound();
        await audit.AppendAsync(tx, Entry(subject, "part.read", AuditOutcome.Success, number, correlationId,
            ("revision", $"{detail.Iteration.Label}")), ct);
        await tx.CommitAsync(ct);
        return Outcome<PartDetail>.Ok(detail);
    }

    public async Task<PartSearchResult> SearchAsync(Subject subject, string? query, string? state, int limit, int offset, string? correlationId, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxSearchLimit);
        offset = Math.Max(0, offset);
        var q = (query ?? "").Trim();
        var like = "%" + EscapeLike(q) + "%";
        var filter = AccessPolicy.BuildReadFilter(subject, "m");

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var items = new List<PartSummary>();
        long total = 0;
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT m.id, m.number, m.name, m.part_type, r.revision, r.latest_iteration, r.lifecycle_state, {MarkingColumns.Select("m")},
                   count(*) OVER ()
            FROM plm.part_master m
            JOIN plm.part_revision r ON r.master_id = m.id AND r.is_latest
            WHERE {filter.Sql}
              AND (@q = '' OR m.search @@ websearch_to_tsquery('english', @q) OR m.number ILIKE @like OR m.name ILIKE @like)
              AND (@state::text IS NULL OR r.lifecycle_state = @state)
            ORDER BY CASE WHEN m.number ILIKE @prefix THEN 0 ELSE 1 END, m.number
            LIMIT @limit OFFSET @offset
            """, conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("q", q);
            cmd.Parameters.AddWithValue("like", like);
            cmd.Parameters.AddWithValue("prefix", EscapeLike(q) + "%");
            cmd.Parameters.AddWithValue("state", (object?)state ?? DBNull.Value);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var m = MarkingColumns.Read(r, 7);
                items.Add(new PartSummary(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5),
                    r.GetString(6), m.BannerText(), m));
                total = r.GetInt64(7 + MarkingColumns.Count);
            }
        }
        await audit.AppendAsync(tx, Entry(subject, "part.search", AuditOutcome.Success, null, correlationId,
            ("query", q), ("state", state), ("offset", offset.ToString(CultureInfo.InvariantCulture)),
            ("returned", items.Count.ToString(CultureInfo.InvariantCulture)), ("numbers", string.Join(",", items.Select(i => i.Number)))), ct);
        await tx.CommitAsync(ct);
        return new PartSearchResult(items, total, Markings.AggregateBanner(items.Select(i => i.Markings)));
    }

    // ---------------------------------------------------------------- check-out / working copy / check-in

    public Task<Outcome<PartDetail>> CheckOutAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, "part.checkout", correlationId, PlmAction.Modify, async (ctx, conn, tx) =>
        {
            if (ctx.Lifecycle.IsLocked(ctx.State)) return Fail($"Revision {ctx.Revision} is {ctx.StateLabel} and locked; it cannot be checked out.");
            if (ctx.CheckoutUser is not null) return Fail($"Already checked out by {ctx.CheckoutUserName ?? ctx.CheckoutUser}.");
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO plm.part_checkout (revision_id, user_id, user_name, checked_out_at, description, attributes)
                SELECT r.id, @user, @name, @at, i.description, i.attributes
                FROM plm.part_revision r JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
                WHERE r.id = @rid
                """, conn, tx);
            cmd.Parameters.AddWithValue("user", subject.Id);
            cmd.Parameters.AddWithValue("name", DisplayId(subject));
            cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
            cmd.Parameters.AddWithValue("rid", ctx.RevisionId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Done(("revision", $"{ctx.Revision}.{ctx.LatestIteration}"));
        }, ct);

    public Task<Outcome<PartDetail>> UpdateWorkingCopyAsync(Subject subject, string number, string? description, JsonObject? attributes, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, "part.edit", correlationId, PlmAction.Modify, async (ctx, conn, tx) =>
        {
            if (ctx.CheckoutUser != subject.Id) return Fail("Check the part out before editing it.");
            if ((description?.Length ?? 0) > 4000) return Invalid(["Description must be at most 4000 characters."]);
            var defs = await AttributeValidator.LoadAsync(conn, tx, ObjectType, ct);
            var (normalized, errors) = AttributeValidator.Validate(defs, attributes);
            if (errors.Count > 0) return Invalid(errors);
            await using var cmd = new NpgsqlCommand("UPDATE plm.part_checkout SET description = @d, attributes = @a::jsonb WHERE revision_id = @rid", conn, tx);
            cmd.Parameters.AddWithValue("d", description?.Trim() ?? "");
            cmd.Parameters.AddWithValue("a", normalized.ToJsonString());
            cmd.Parameters.AddWithValue("rid", ctx.RevisionId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Done(("attributes", normalized.ToJsonString()));
        }, ct);

    public Task<Outcome<PartDetail>> CheckInAsync(Subject subject, string number, string? comment, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, "part.checkin", correlationId, PlmAction.Modify, async (ctx, conn, tx) =>
        {
            if (ctx.CheckoutUser != subject.Id) return Fail("Only the user who checked the part out can check it in.");
            if ((comment?.Length ?? 0) > 1000) return Invalid(["Check-in comment must be at most 1000 characters."]);
            var next = ctx.LatestIteration + 1;
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO plm.part_iteration (id, revision_id, iteration, description, attributes, comment, created_at, created_by)
                SELECT @iid, revision_id, @next, description, attributes, @comment, @at, @by FROM plm.part_checkout WHERE revision_id = @rid;
                UPDATE plm.part_revision SET latest_iteration = @next WHERE id = @rid;
                DELETE FROM plm.part_checkout WHERE revision_id = @rid;
                """, conn, tx);
            cmd.Parameters.AddWithValue("iid", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("next", next);
            cmd.Parameters.AddWithValue("comment", string.IsNullOrWhiteSpace(comment) ? DBNull.Value : comment.Trim());
            cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
            cmd.Parameters.AddWithValue("by", DisplayId(subject));
            cmd.Parameters.AddWithValue("rid", ctx.RevisionId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Done(("revision", $"{ctx.Revision}.{next}"), ("comment", comment));
        }, ct);

    public Task<Outcome<PartDetail>> UndoCheckOutAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, "part.undo_checkout", correlationId, PlmAction.Modify, async (ctx, conn, tx) =>
        {
            if (ctx.CheckoutUser is null) return Fail("The part is not checked out.");
            var isAdmin = AccessPolicy.Evaluate(subject, ctx.Markings, PlmAction.Administer).Permitted;
            if (ctx.CheckoutUser != subject.Id && !isAdmin) return Forbid("checkout.not_owner");
            await using var cmd = new NpgsqlCommand("DELETE FROM plm.part_checkout WHERE revision_id = @rid", conn, tx);
            cmd.Parameters.AddWithValue("rid", ctx.RevisionId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Done(("checkedOutBy", ctx.CheckoutUserName ?? ctx.CheckoutUser), ("override", (ctx.CheckoutUser != subject.Id).ToString()));
        }, ct, allowAdminWithoutModify: true);

    // ---------------------------------------------------------------- lifecycle

    /// <summary>
    /// Takes a manual lifecycle transition, or (administrators, with a reason) forces any state: the
    /// Windchill "Set State" override, used for corrections and migration. Overrides are audited as such.
    /// </summary>
    public Task<Outcome<PartDetail>> SetStateAsync(Subject subject, string number, string toState, string? reason, bool adminOverride, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, adminOverride ? "part.set_state" : "part.transition", correlationId,
            adminOverride ? PlmAction.Administer : PlmAction.Modify, async (ctx, conn, tx) =>
        {
            var target = ctx.Lifecycle.State(toState);
            if (target is null) return Invalid([$"'{toState}' is not a state of lifecycle {ctx.Lifecycle.Id}."]);
            if (toState == ctx.State) return Fail($"Revision {ctx.Revision} is already {target.Label}.");
            if (ctx.CheckoutUser is not null) return Fail($"Checked out by {ctx.CheckoutUserName ?? ctx.CheckoutUser}; check in or undo the check-out first.");
            if (adminOverride)
            {
                if (string.IsNullOrWhiteSpace(reason)) return Invalid(["A reason is required when overriding the lifecycle state."]);
            }
            else
            {
                var t = ctx.Lifecycle.Transitions.FirstOrDefault(t => t.From == ctx.State && t.To == toState);
                if (t is null) return Fail($"No transition from {ctx.StateLabel} to {target.Label}.");
                if (t.Via != "manual") return Fail(ViaMessage(t.Via, target.Label));
            }
            await using var cmd = new NpgsqlCommand(
                "UPDATE plm.part_revision SET lifecycle_state = @s, state_changed_at = @at, state_changed_by = @by WHERE id = @rid", conn, tx);
            cmd.Parameters.AddWithValue("s", toState);
            cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
            cmd.Parameters.AddWithValue("by", DisplayId(subject));
            cmd.Parameters.AddWithValue("rid", ctx.RevisionId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Done(("revision", ctx.Revision), ("from", ctx.State), ("to", toState), ("reason", reason));
        }, ct);

    // ---------------------------------------------------------------- markings

    public Task<Outcome<PartDetail>> ChangeMarkingsAsync(Subject subject, string number, Markings markings, string? reason, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, "part.markings_change", correlationId, PlmAction.Modify, async (ctx, conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) return Invalid(["A reason is required to change markings."]);
            if (ctx.Lifecycle.IsLocked(ctx.State)) return Fail($"Revision {ctx.Revision} is {ctx.StateLabel}; markings of locked data change through change control.");
            if (markings == ctx.Markings) return Fail("The markings are unchanged.");
            // The actor must also be permitted under the new markings, so nobody marks data away from themselves by accident.
            var after = AccessPolicy.Evaluate(subject, markings, PlmAction.Modify);
            if (!after.Permitted) return Forbid([.. after.Reasons.Select(r => "new_markings:" + r)]);
            await using var cmd = new NpgsqlCommand($"UPDATE plm.part_master SET {MarkingColumns.UpdateSet} WHERE id = @id", conn, tx);
            foreach (var (k, v) in MarkingColumns.Parameters(markings)) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("id", ctx.MasterId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Done(("before", Describe(ctx.Markings)), ("after", Describe(markings)), ("reason", reason));
        }, ct);

    // ---------------------------------------------------------------- shared machinery

    private sealed record Head(Guid MasterId, string Number, string LifecycleId, Markings Markings);

    private sealed record MutationContext(
        Guid MasterId, Guid RevisionId, string Revision, string State, string StateLabel, int LatestIteration,
        Markings Markings, Lifecycle Lifecycle, string? CheckoutUser, string? CheckoutUserName);

    private sealed record Step(OutcomeKind Kind, IReadOnlyList<string> Errors, (string, string?)[] Details);

    private static Step Done(params (string, string?)[] details) => new(OutcomeKind.Ok, [], details);
    private static Step Fail(string message) => new(OutcomeKind.Conflict, [message], []);
    private static Step Invalid(IEnumerable<string> errors) => new(OutcomeKind.Invalid, errors.ToList(), []);
    private static Step Forbid(params string[] reasons) => new(OutcomeKind.Forbidden, reasons, []);

    private async Task<Outcome<PartDetail>> MutateAsync(
        Subject subject, string number, string action, string? correlationId, PlmAction permission,
        Func<MutationContext, NpgsqlConnection, NpgsqlTransaction, Task<Step>> body, CancellationToken ct, bool allowAdminWithoutModify = false)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await LoadHeadAsync(conn, tx, number, forUpdate: true, ct);
        if (head is null) return Outcome<PartDetail>.NotFound();

        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<PartDetail>.NotFound();
        }
        var decision = AccessPolicy.Evaluate(subject, head.Markings, permission);
        if (!decision.Permitted && allowAdminWithoutModify && AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Administer).Permitted)
            decision = AccessDecision.Permit;
        if (!decision.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", decision.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<PartDetail>.Forbidden(decision.Reasons);
        }

        var lifecycle = await Lifecycle.LoadAsync(conn, tx, head.LifecycleId, ct) ?? throw new InvalidOperationException($"Lifecycle {head.LifecycleId} missing.");
        MutationContext ctx;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT r.id, r.revision, r.lifecycle_state, r.latest_iteration, c.user_id, c.user_name
            FROM plm.part_revision r LEFT JOIN plm.part_checkout c ON c.revision_id = r.id
            WHERE r.master_id = @m AND r.is_latest
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("m", head.MasterId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var state = r.GetString(2);
            ctx = new MutationContext(head.MasterId, r.GetGuid(0), r.GetString(1), state, lifecycle.State(state)?.Label ?? state, r.GetInt32(3),
                head.Markings, lifecycle, r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5));
        }

        var step = await body(ctx, conn, tx);
        if (step.Kind != OutcomeKind.Ok)
        {
            await tx.RollbackAsync(ct);
            if (step.Kind == OutcomeKind.Forbidden)
            {
                await audit.AppendAsync(Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", step.Errors))), ct);
                return Outcome<PartDetail>.Forbidden(step.Errors);
            }
            return step.Kind == OutcomeKind.Invalid ? Outcome<PartDetail>.Invalid(step.Errors) : Outcome<PartDetail>.Conflict(step.Errors[0]);
        }
        await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Success, number, correlationId, step.Details), ct);
        var detail = await LoadDetailAsync(conn, tx, subject, number, null, ct);
        await tx.CommitAsync(ct);
        return Outcome<PartDetail>.Ok(detail!);
    }

    private static async Task<Head?> LoadHeadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string number, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT m.id, m.number, m.lifecycle_id, {MarkingColumns.Select("m")} FROM plm.part_master m WHERE m.number = @n{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Head(r.GetGuid(0), r.GetString(1), r.GetString(2), MarkingColumns.Read(r, 3));
    }

    /// <summary>Loads the full view. Callers must already have established read access.</summary>
    private static async Task<PartDetail?> LoadDetailAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string number, string? revision, CancellationToken ct)
    {
        Guid id;
        string name, type, unit, source, lifecycleId, createdBy;
        DateTimeOffset createdAt;
        Markings markings;
        await using (var cmd = new NpgsqlCommand(
            $"SELECT m.id, m.name, m.part_type, m.default_unit, m.source, m.lifecycle_id, m.created_at, m.created_by, {MarkingColumns.Select("m")} FROM plm.part_master m WHERE m.number = @n",
            conn, tx))
        {
            cmd.Parameters.AddWithValue("n", number);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            (id, name, type, unit, source, lifecycleId) = (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5));
            createdAt = new DateTimeOffset(r.GetDateTime(6), TimeSpan.Zero);
            createdBy = r.GetString(7);
            markings = MarkingColumns.Read(r, 8);
        }
        var lifecycle = (await Lifecycle.LoadAsync(conn, tx, lifecycleId, ct))!;

        var revisions = new List<RevisionView>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by FROM plm.part_revision WHERE master_id = @m",
            conn, tx))
        {
            cmd.Parameters.AddWithValue("m", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var st = r.GetString(2);
                revisions.Add(new RevisionView(r.GetGuid(0), r.GetString(1), st, lifecycle.State(st)?.Label ?? st, r.GetBoolean(3), lifecycle.IsLocked(st),
                    r.GetInt32(4), new DateTimeOffset(r.GetDateTime(5), TimeSpan.Zero), r.GetString(6), new DateTimeOffset(r.GetDateTime(7), TimeSpan.Zero), r.GetString(8)));
            }
        }
        revisions.Sort((a, b) => RevisionScheme.ToIndex(a.Revision).CompareTo(RevisionScheme.ToIndex(b.Revision)));
        var selected = revision is null ? revisions.Single(r => r.IsLatest) : revisions.FirstOrDefault(r => r.Revision == revision);
        if (selected is null) return null;

        var history = new List<IterationView>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT i.id, r.revision, i.iteration, i.description, i.attributes::text, i.comment, i.created_at, i.created_by
            FROM plm.part_iteration i JOIN plm.part_revision r ON r.id = i.revision_id
            WHERE r.master_id = @m
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("m", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                history.Add(new IterationView(r.GetGuid(0), r.GetString(1), r.GetInt32(2), $"{r.GetString(1)}.{r.GetInt32(2)}", r.GetString(3),
                    JsonNode.Parse(r.GetString(4))!.AsObject(), r.IsDBNull(5) ? null : r.GetString(5), new DateTimeOffset(r.GetDateTime(6), TimeSpan.Zero), r.GetString(7)));
        }
        history = [.. history.OrderByDescending(h => RevisionScheme.ToIndex(h.Revision)).ThenByDescending(h => h.Iteration)];
        var iteration = history.First(h => h.Revision == selected.Revision && h.Iteration == selected.LatestIteration);

        CheckoutView? checkout = null;
        await using (var cmd = new NpgsqlCommand("SELECT user_id, user_name, checked_out_at, description, attributes::text FROM plm.part_checkout WHERE revision_id = @rid", conn, tx))
        {
            cmd.Parameters.AddWithValue("rid", selected.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                var mine = r.GetString(0) == subject.Id;
                // The working copy is private to its owner until check-in.
                checkout = new CheckoutView(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), new DateTimeOffset(r.GetDateTime(2), TimeSpan.Zero), mine,
                    mine ? r.GetString(3) : null, mine ? JsonNode.Parse(r.GetString(4))!.AsObject() : null);
            }
        }

        var modify = AccessPolicy.Evaluate(subject, markings, PlmAction.Modify);
        var admin = AccessPolicy.Evaluate(subject, markings, PlmAction.Administer);
        var transitions = selected.IsLatest
            ? lifecycle.Transitions.Where(t => t.From == selected.State).Select(t =>
            {
                var label = lifecycle.State(t.To)!.Label;
                string? why = t.Via != "manual" ? ViaMessage(t.Via, label)
                    : !modify.Permitted ? "You may not modify this part."
                    : checkout is not null ? "Check in or undo the check-out first."
                    : null;
                return new TransitionView(t.To, label, t.Via, why is null, why);
            }).ToList()
            : [];

        return new PartDetail(id, number, name, type, unit, source, lifecycleId, markings, markings.BannerText(), selected, iteration, revisions, history,
            checkout, transitions, new PartPermissions(modify.Permitted, admin.Permitted, modify.Reasons), createdAt, createdBy);
    }

    private static string ViaMessage(string via, string targetLabel) => via switch
    {
        "workflow" => $"{targetLabel} is reached only through an approved review workflow (increment 1.5).",
        "change" => $"{targetLabel} is reached only through a released change notice (increment 1.6).",
        _ => $"Transition to {targetLabel} is not available.",
    };

    private static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string? number, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s),
        Action = action,
        Outcome = outcome,
        ObjectType = ObjectType,
        ObjectId = number,
        CorrelationId = correlationId,
        Details = AuditDetails.Of(details),
    };

    private static string Describe(Markings m) => JsonSerializer.Serialize(new
    {
        jurisdiction = m.ExportJurisdiction.ToString(), m.Eccn, m.UsmlCategory, cui = m.CuiCategories, distribution = m.DistributionStatement.ToString(),
        m.ControllingOffice, m.DistributionReason, m.Program, m.OwningOrg, authorizations = m.ExportAuthorizations,
    });

    // ASSUMPTION: created_by/state_changed_by store the login name for display; the audit log keeps the stable IdP subject.
    private static string DisplayId(Subject s) => s.Username ?? s.Id;

    private static string EscapeLike(string s) => s.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9._\-]{0,39}$")]
    private static partial Regex NumberPattern();
}
