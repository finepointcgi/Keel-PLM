using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Change;

/// <summary>
/// Change management commands and queries (PR, ECR, ECN, change tasks; ADR-0019). Like every module service: each call evaluates the
/// access policy (ADR-0004), writes its audit event in the same transaction as its change (ADR-0005), including denials, and serializes
/// per object on <c>SELECT … FOR UPDATE</c> (the change row first, then the object masters in id order, then other change rows).
/// </summary>
public sealed partial class ChangeService(NpgsqlDataSource dataSource, IAuditLog audit, IEnumerable<IChangeObjectGateway> gateways, TimeProvider clock, IRevisionEffectivity? effectivity = null)
{
    public const int MaxSearchLimit = 200;
    public const int MaxTitleLength = 200;

    private IChangeObjectGateway? GatewayFor(string objectType) => gateways.FirstOrDefault(g => g.ObjectType == objectType);

    private sealed record Ctx(ChangeRow Head, Lifecycle Lifecycle, DateTimeOffset Now)
    {
        public string State => Head.State;
        public string StateLabel => Lifecycle.State(Head.State)?.Label ?? Head.State;
        public bool Editable => !Lifecycle.IsLocked(Head.State);
        public string Label => ChangeTypes.Label(Head.Type);
    }

    private sealed record Step(OutcomeKind Kind, IReadOnlyList<string> Errors, (string, string?)[] Details)
    {
        public List<string> Notices { get; } = [];

        /// <summary>When set, the refusal is also an access denial: audited after the transaction rolls back (a rolled-back audit row would be lost).</summary>
        public (string, string?)[]? DenialDetails { get; init; }
    }

    private static Step Done(params (string, string?)[] details) => new(OutcomeKind.Ok, [], details);
    private static Step Fail(string message) => new(OutcomeKind.Conflict, [message], []);
    private static Step Invalid(params string[] errors) => new(OutcomeKind.Invalid, errors, []);
    private static Step Forbid(params string[] reasons) => new(OutcomeKind.Forbidden, reasons, []);

    private static Step Frozen(Ctx c) => Fail($"{c.Label} {c.Head.Number} is {c.StateLabel} and can no longer be edited.");

    // ---------------------------------------------------------------- shared mutation scaffold

    /// <summary>
    /// Loads and locks the change row, requires read access (404 otherwise, denial audited) and <paramref name="permission"/> (403,
    /// audited), runs <paramref name="body"/> and audits its result in the same transaction. Returns the refreshed detail.
    /// </summary>
    private async Task<Outcome<ChangeDetail>> MutateAsync(
        Subject subject, string number, string action, string? correlationId, PlmAction permission,
        Func<Ctx, NpgsqlConnection, NpgsqlTransaction, Task<Step>> body, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await ChangeStore.LoadByNumberAsync(conn, tx, number, forUpdate: true, ct);
        if (head is null) return Outcome<ChangeDetail>.NotFound();
        var objectType = ChangeTypes.ObjectTypeOf(head.Type);

        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, ChangeStore.Entry(subject, action, AuditOutcome.Denied, objectType, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<ChangeDetail>.NotFound();
        }
        var decision = permission == PlmAction.Read ? read : AccessPolicy.Evaluate(subject, head.Markings, permission);
        if (!decision.Permitted)
        {
            await audit.AppendAsync(tx, ChangeStore.Entry(subject, action, AuditOutcome.Denied, objectType, number, correlationId, ("reasons", string.Join(",", decision.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<ChangeDetail>.Forbidden(decision.Reasons);
        }

        var lifecycle = await Lifecycle.LoadAsync(conn, tx, head.LifecycleId, ct) ?? throw new InvalidOperationException($"Lifecycle {head.LifecycleId} missing.");
        var step = await body(new Ctx(head, lifecycle, clock.GetUtcNow()), conn, tx);
        if (step.Kind != OutcomeKind.Ok)
        {
            await tx.RollbackAsync(ct);
            if (step.Kind == OutcomeKind.Forbidden || step.DenialDetails is not null)
                await audit.AppendAsync(ChangeStore.Entry(subject, action, AuditOutcome.Denied, objectType, number, correlationId, step.DenialDetails ?? [("reasons", string.Join(",", step.Errors))]), ct);
            if (step.Kind == OutcomeKind.Forbidden) return Outcome<ChangeDetail>.Forbidden(step.Errors);
            return step.Kind == OutcomeKind.Invalid ? Outcome<ChangeDetail>.Invalid(step.Errors) : Outcome<ChangeDetail>.Conflict(step.Errors[0]);
        }
        await audit.AppendAsync(tx, ChangeStore.Entry(subject, action, AuditOutcome.Success, objectType, number, correlationId, step.Details), ct);
        var detail = await LoadDetailAsync(conn, tx, subject, head.Id, step.Notices, ct);
        await tx.CommitAsync(ct);
        return Outcome<ChangeDetail>.Ok(detail!);
    }

    // ---------------------------------------------------------------- create

    /// <summary>Creates a PR, an ECR or an ECN. A Class I change notice must name an ECR approved by the CCB (A8).</summary>
    public async Task<Outcome<ChangeDetail>> CreateAsync(Subject subject, CreateChangeCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        ChangeRow? source = null;
        if (cmd.SourceNumber is not null)
        {
            source = await ChangeStore.LoadByNumberAsync(conn, tx, cmd.SourceNumber, forUpdate: true, ct);
            if (source is null || !AccessPolicy.Evaluate(subject, source.Markings, PlmAction.Read).Permitted)
            {
                if (source is not null)
                    await audit.AppendAsync(tx, ChangeStore.Entry(subject, "change.create", AuditOutcome.Denied, ChangeTypes.ObjectTypeOf(cmd.Type), cmd.SourceNumber, correlationId,
                        ("reasons", string.Join(",", AccessPolicy.Evaluate(subject, source.Markings, PlmAction.Read).Reasons)), ("via", "source")), ct);
                await tx.CommitAsync(ct);
                return Outcome<ChangeDetail>.Invalid([$"{cmd.SourceNumber} was not found."]);
            }
        }
        return await CreateInTransactionAsync(conn, tx, subject, cmd, source, copyAffected: false, correlationId, ct);
    }

    /// <summary>
    /// Creates an ECR from a PR, or an ECN from an ECR, copying what the caller does not override: title, description, priority, reason,
    /// markings, and the listed objects (the link to the source is stored). The new object's markings must cover the source's.
    /// </summary>
    public async Task<Outcome<ChangeDetail>> CreateFromAsync(Subject subject, string sourceNumber, string targetType, CreateFromCommand over, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var source = await ChangeStore.LoadByNumberAsync(conn, tx, sourceNumber, forUpdate: true, ct);
        if (source is null) return Outcome<ChangeDetail>.NotFound();
        var read = AccessPolicy.Evaluate(subject, source.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, ChangeStore.Entry(subject, "change.create", AuditOutcome.Denied, ChangeTypes.ObjectTypeOf(source.Type), sourceNumber, correlationId,
                ("reasons", string.Join(",", read.Reasons)), ("via", "create_from")), ct);
            await tx.CommitAsync(ct);
            return Outcome<ChangeDetail>.NotFound();
        }
        var expected = source.Type == ChangeTypes.Pr ? ChangeTypes.Ecr : source.Type == ChangeTypes.Ecr ? ChangeTypes.Ecn : null;
        if (expected is null || targetType != expected)
            return Outcome<ChangeDetail>.Invalid([$"A {ChangeTypes.Label(source.Type).ToLowerInvariant()} can only be turned into {(expected is null ? "nothing" : "a " + ChangeTypes.Label(expected).ToLowerInvariant())}."]);

        var cmd = new CreateChangeCommand(targetType, over.Title ?? source.Title, over.Description ?? source.Description, over.Priority ?? source.Priority,
            over.Reason ?? source.Reason, over.Markings ?? source.Markings,
            // A notice inherits the request's class; the request is classified at the CCB (ECN) or by the requester (ECR, optional).
            targetType == ChangeTypes.Ecn ? source.ChangeClass : over.ChangeClass, sourceNumber);
        if (targetType == ChangeTypes.Ecn && over.ChangeClass is not null && over.ChangeClass != source.ChangeClass)
            return Outcome<ChangeDetail>.Invalid([$"A change notice has the class of its change request ({source.ChangeClass ?? "not yet classified"}); it cannot be changed here."]);
        return await CreateInTransactionAsync(conn, tx, subject, cmd, source, copyAffected: true, correlationId, ct);
    }

    private async Task<Outcome<ChangeDetail>> CreateInTransactionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, CreateChangeCommand cmd,
        ChangeRow? source, bool copyAffected, string? correlationId, CancellationToken ct)
    {
        var objectType = ChangeTypes.ObjectTypeOf(cmd.Type);
        var errors = new List<string>();
        if (!ChangeTypes.All.Contains(cmd.Type)) errors.Add($"Type must be one of: {string.Join(", ", ChangeTypes.All)}.");
        var title = cmd.Title?.Trim() ?? "";
        if (title.Length is 0 or > MaxTitleLength) errors.Add($"Title is required (at most {MaxTitleLength} characters).");
        if ((cmd.Description?.Length ?? 0) > 8000) errors.Add("Description must be at most 8000 characters.");
        if ((cmd.Reason?.Length ?? 0) > 4000) errors.Add("Reason must be at most 4000 characters.");
        if (!ChangePriorities.All.Contains(cmd.Priority)) errors.Add($"Priority must be one of: {string.Join(", ", ChangePriorities.All)}.");
        if (cmd.ChangeClass is not null && !ChangeClasses.All.Contains(cmd.ChangeClass)) errors.Add("Class must be I or II.");
        if (errors.Count > 0) return Outcome<ChangeDetail>.Invalid(errors);

        // ---- process rules (A8)
        switch (cmd.Type)
        {
            case ChangeTypes.Pr:
                if (cmd.ChangeClass is not null) errors.Add("A problem report has no class.");
                if (source is not null) errors.Add("A problem report is not created from another change.");
                break;
            case ChangeTypes.Ecr:
                if (source is not null && source.Type != ChangeTypes.Pr) errors.Add("A change request is created from a problem report, or from nothing.");
                if (source is not null && source.LifecycleId != "pr-default") errors.Add("Unexpected source lifecycle.");
                break;
            case ChangeTypes.Ecn:
                if (cmd.ChangeClass is null && source is null) errors.Add("A change notice needs a class (I or II).");
                if (source is not null && source.Type != ChangeTypes.Ecr) errors.Add("A change notice is created from a change request, or from nothing (Class II only).");
                if (source is null && cmd.ChangeClass == ChangeClasses.I)
                    errors.Add("A Class I change needs a change request approved by the CCB before a change notice can be opened. Create the ECR first.");
                break;
        }
        if (errors.Count > 0) return Outcome<ChangeDetail>.Invalid(errors);
        if (source is not null)
        {
            var lifecycle = (await Lifecycle.LoadAsync(conn, tx, source.LifecycleId, ct))!;
            var sourceLabel = lifecycle.State(source.State)?.Label ?? source.State;
            if (cmd.Type == ChangeTypes.Ecr && lifecycle.IsLocked(source.State))
                return Outcome<ChangeDetail>.Conflict($"{source.Number} is {sourceLabel}; a change request cannot be created from it.");
            if (cmd.Type == ChangeTypes.Ecn)
            {
                if (source.ChangeClass is null)
                    return Outcome<ChangeDetail>.Conflict($"{source.Number} is not classified yet. Classify it (Class I or II) before opening a change notice.");
                if (source.ChangeClass == ChangeClasses.I && source.State != "Approved")
                    return Outcome<ChangeDetail>.Conflict($"{source.Number} is Class I and is {sourceLabel}. A Class I change notice can only be opened after the CCB approved the change request.");
                if (source.ChangeClass == ChangeClasses.II && source.State is not ("Draft" or "Approved"))
                    return Outcome<ChangeDetail>.Conflict($"{source.Number} is {sourceLabel}; a change notice cannot be opened from it.");
            }
            // The copy must not be less restrictive than what it copies (title and description are copied).
            var gaps = MarkingDominance.Gaps(cmd.Markings, source.Markings);
            if (gaps.Count > 0)
                return Outcome<ChangeDetail>.Invalid([$"The markings of the new {ChangeTypes.Label(cmd.Type).ToLowerInvariant()} must cover those of {source.Number}. {MarkingDominance.Describe(gaps)}"]);
        }

        var decision = AccessPolicy.Evaluate(subject, cmd.Markings, PlmAction.Modify);
        if (!decision.Permitted)
        {
            await audit.AppendAsync(tx, ChangeStore.Entry(subject, "change.create", AuditOutcome.Denied, objectType, null, correlationId,
                ("reasons", string.Join(",", decision.Reasons)), ("markings", cmd.Markings.BannerText())), ct);
            await tx.CommitAsync(ct);
            return Outcome<ChangeDetail>.Forbidden(decision.Reasons);
        }

        var lifecycleId = ChangeTypes.LifecycleOf(cmd.Type);
        var lc = await Lifecycle.LoadAsync(conn, tx, lifecycleId, ct) ?? throw new InvalidOperationException($"Lifecycle {lifecycleId} missing.");
        long seq;
        await using (var nv = new NpgsqlCommand($"SELECT nextval('chg.{objectType}_seq')", conn, tx)) seq = (long)(await nv.ExecuteScalarAsync(ct))!;
        var number = $"{cmd.Type}-{seq.ToString("D6", CultureInfo.InvariantCulture)}";
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7();
        await using (var ins = new NpgsqlCommand(
            $"""
            INSERT INTO chg.change (id, change_type, number, title, description, priority, change_class, reason, requester_sub, requester_username, requester_name,
                                    source_id, lifecycle_id, lifecycle_state, {MarkingColumns.InsertColumns}, created_at, created_by, state_changed_at, state_changed_by)
            VALUES (@id, @type, @number, @title, @desc, @prio, @class, @reason, @sub, @user, @name, @source, @lc, @state, {MarkingColumns.InsertValues}, @at, @by, @at, @by)
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("type", cmd.Type);
            ins.Parameters.AddWithValue("number", number);
            ins.Parameters.AddWithValue("title", title);
            ins.Parameters.AddWithValue("desc", cmd.Description?.Trim() ?? "");
            ins.Parameters.AddWithValue("prio", cmd.Priority);
            ins.Parameters.AddWithValue("class", (object?)cmd.ChangeClass ?? DBNull.Value);
            ins.Parameters.AddWithValue("reason", cmd.Reason?.Trim() ?? "");
            ins.Parameters.AddWithValue("sub", subject.Id);
            ins.Parameters.AddWithValue("user", ChangeStore.Who(subject));
            ins.Parameters.AddWithValue("name", subject.DisplayName);
            ins.Parameters.AddWithValue("source", (object?)source?.Id ?? DBNull.Value);
            ins.Parameters.AddWithValue("lc", lifecycleId);
            ins.Parameters.AddWithValue("state", lc.Initial.State);
            foreach (var (k, v) in MarkingColumns.Parameters(cmd.Markings)) ins.Parameters.AddWithValue(k, v);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", ChangeStore.Who(subject));
            await ins.ExecuteNonQueryAsync(ct);
        }
        await ChangeStore.AppendHistoryAsync(conn, tx, id, null, subject, "Created", source is null ? null : $"Created from {source.Number}.", now, ct);

        var notices = new List<string>();
        var copied = 0;
        if (copyAffected && source is not null)
        {
            var created = (await ChangeStore.LoadByIdAsync(conn, tx, id, forUpdate: false, ct))!;
            foreach (var row in await ChangeStore.LoadAffectedAsync(conn, tx, source.Id, ct))
            {
                // A request to revise an object that is not released any more keeps it as a reference; a notice must name the exact revision.
                var action = cmd.Type == ChangeTypes.Ecr && row.Action == ChangeActions.None ? ChangeActions.Revise : row.Action;
                var step = await AddAffectedCoreAsync(conn, tx, subject, created, new AddAffectedCommand(row.ObjectType, row.Key, action, row.Revision), now, notices, ct);
                if (step.Kind != OutcomeKind.Ok && cmd.Type == ChangeTypes.Ecr && action == ChangeActions.Revise)
                    step = await AddAffectedCoreAsync(conn, tx, subject, created, new AddAffectedCommand(row.ObjectType, row.Key, ChangeActions.None, null), now, notices, ct);
                if (step.Kind != OutcomeKind.Ok)
                {
                    await tx.RollbackAsync(ct);
                    var message = $"{row.Key}: {string.Join(" ", step.Errors)}";
                    if (step.Kind == OutcomeKind.Forbidden || step.DenialDetails is not null)
                        await audit.AppendAsync(ChangeStore.Entry(subject, "change.create", AuditOutcome.Denied, objectType, null, correlationId,
                            [.. (step.DenialDetails ?? [("reasons", string.Join(",", step.Errors))]), ("via", "copy_affected")]), ct);
                    if (step.Kind == OutcomeKind.Forbidden) return Outcome<ChangeDetail>.Forbidden(step.Errors);
                    return step.Kind == OutcomeKind.Invalid ? Outcome<ChangeDetail>.Invalid([message]) : Outcome<ChangeDetail>.Conflict(message);
                }
                copied++;
            }
        }
        await audit.AppendAsync(tx, ChangeStore.Entry(subject, "change.create", AuditOutcome.Success, objectType, number, correlationId,
            ("type", cmd.Type), ("class", cmd.ChangeClass), ("source", source?.Number), ("copiedObjects", copied.ToString(CultureInfo.InvariantCulture)), ("markings", cmd.Markings.BannerText())), ct);
        var detail = await LoadDetailAsync(conn, tx, subject, id, notices, ct);
        await tx.CommitAsync(ct);
        return Outcome<ChangeDetail>.Ok(detail!);
    }

    // ---------------------------------------------------------------- edit

    public Task<Outcome<ChangeDetail>> UpdateAsync(Subject subject, string number, UpdateChangeCommand cmd, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, "change.update", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (!c.Editable) return Frozen(c);
            var title = cmd.Title?.Trim() ?? "";
            var errors = new List<string>();
            if (title.Length is 0 or > MaxTitleLength) errors.Add($"Title is required (at most {MaxTitleLength} characters).");
            if ((cmd.Description?.Length ?? 0) > 8000) errors.Add("Description must be at most 8000 characters.");
            if ((cmd.Reason?.Length ?? 0) > 4000) errors.Add("Reason must be at most 4000 characters.");
            if (!ChangePriorities.All.Contains(cmd.Priority)) errors.Add($"Priority must be one of: {string.Join(", ", ChangePriorities.All)}.");
            if (errors.Count > 0) return Invalid([.. errors]);
            await using var up = new NpgsqlCommand("UPDATE chg.change SET title = @t, description = @d, priority = @p, reason = @r WHERE id = @id", conn, tx);
            up.Parameters.AddWithValue("t", title);
            up.Parameters.AddWithValue("d", cmd.Description?.Trim() ?? "");
            up.Parameters.AddWithValue("p", cmd.Priority);
            up.Parameters.AddWithValue("r", cmd.Reason?.Trim() ?? "");
            up.Parameters.AddWithValue("id", c.Head.Id);
            await up.ExecuteNonQueryAsync(ct);
            await ChangeStore.AppendHistoryAsync(conn, tx, c.Head.Id, null, subject, "Edited", null, c.Now, ct);
            return Done(("title", title), ("priority", cmd.Priority));
        }, ct);

    /// <summary>Sets Class I or II on an ECR while it is a draft; the CCB reviews it with that class (A8).</summary>
    public Task<Outcome<ChangeDetail>> ClassifyAsync(Subject subject, string number, string changeClass, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, "change.classify", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (c.Head.Type != ChangeTypes.Ecr) return Invalid("Only a change request is classified here; a change notice has the class it was created with.");
            if (!ChangeClasses.All.Contains(changeClass)) return Invalid("Class must be I or II.");
            if (!c.Editable) return Frozen(c);
            if (c.Head.ChangeClass == changeClass) return Fail($"{c.Head.Number} is already Class {changeClass}.");
            await using var up = new NpgsqlCommand("UPDATE chg.change SET change_class = @c WHERE id = @id", conn, tx);
            up.Parameters.AddWithValue("c", changeClass);
            up.Parameters.AddWithValue("id", c.Head.Id);
            await up.ExecuteNonQueryAsync(ct);
            await ChangeStore.AppendHistoryAsync(conn, tx, c.Head.Id, null, subject, "Classified", $"Class {changeClass} (was {c.Head.ChangeClass ?? "unclassified"}).", c.Now, ct);
            return Done(("from", c.Head.ChangeClass), ("to", changeClass));
        }, ct);

    /// <summary>
    /// Changes the markings of a change that can still be edited. They must cover every listed object as it is now, and the actor and
    /// every assignee of an open task must still be able to read the change afterwards.
    /// </summary>
    public Task<Outcome<ChangeDetail>> ChangeMarkingsAsync(Subject subject, string number, Markings markings, string? reason, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, "change.markings_change", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) return Invalid("A reason is required to change markings.");
            if (!c.Editable) return Frozen(c);
            if (markings == c.Head.Markings) return Fail("The markings are unchanged.");
            var after = AccessPolicy.Evaluate(subject, markings, PlmAction.Modify);
            if (!after.Permitted) return Forbid([.. after.Reasons.Select(r => "new_markings:" + r)]);
            var problem = await CheckCoverageAsync(conn, tx, c.Head, markings, subject, ct);
            if (problem is not null) return Invalid(problem);
            await SetMarkingsAsync(conn, tx, c.Head.Id, markings, ct);
            await ChangeStore.AppendHistoryAsync(conn, tx, c.Head.Id, null, subject, "Markings changed", $"{c.Head.Markings.BannerText()} → {markings.BannerText()}. Reason: {reason.Trim()}", c.Now, ct);
            return Done(("before", c.Head.Markings.BannerText()), ("after", markings.BannerText()), ("reason", reason.Trim()));
        }, ct);

    private static async Task SetMarkingsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid changeId, Markings markings, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"UPDATE chg.change SET {MarkingColumns.UpdateSet} WHERE id = @id", conn, tx);
        foreach (var (k, v) in MarkingColumns.Parameters(markings)) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("id", changeId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Null when <paramref name="markings"/> are acceptable for the change; otherwise why not (without naming objects the actor cannot read).</summary>
    private async Task<string?> CheckCoverageAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ChangeRow change, Markings markings, Subject actor, CancellationToken ct)
    {
        foreach (var row in await ChangeStore.LoadAffectedAsync(conn, tx, change.Id, ct))
        {
            var gateway = GatewayFor(row.ObjectType);
            var live = gateway is null ? null : await gateway.FindByRevisionAsync(conn, tx, row.RevisionId, forUpdate: false, ct);
            if (live is null) continue;
            var gaps = MarkingDominance.Gaps(markings, live.Markings);
            if (gaps.Count == 0) continue;
            var name = AccessPolicy.Evaluate(actor, live.Markings, PlmAction.Read).Permitted ? live.Key : "a listed object";
            return $"The new markings do not cover {name}. {MarkingDominance.Describe(gaps)}";
        }
        if (change.Type == ChangeTypes.Ecn)
            foreach (var user in await ChangeStore.TaskAssigneesAsync(conn, tx, change.Id, ct))
                if (!AccessPolicy.Evaluate(user.Subject, markings, PlmAction.Read).Permitted)
                    return $"{user.Username} has an open change task on this notice and could no longer read it under the new markings. Cancel or complete the task first.";
        return null;
    }

    // ---------------------------------------------------------------- Phase 2 (explicitly not built)

    /// <summary>Redline markup (annotated drawings and models carried by a change) needs the Phase 2 viewer and markup store.</summary>
    public Task<Outcome<ChangeDetail>> AttachRedlineAsync(Subject subject, string number, Guid documentId, string? correlationId, CancellationToken ct = default) =>
        throw new NotSupportedException("Redline markup on affected documents is delivered in Phase 2.");

    /// <summary>Baselines (frozen sets of versions for audits and milestones) are Phase 2 configuration management.</summary>
    public Task<Outcome<ChangeDetail>> CaptureBaselineAsync(Subject subject, string number, string name, string? correlationId, CancellationToken ct = default) =>
        throw new NotSupportedException("Baselines are delivered in Phase 2 (configuration management).");

    /// <summary>Serial-number, lot or date effectivity of a change is Phase 2; in Phase 1 a released change applies to everything from release on.</summary>
    public Task<Outcome<ChangeDetail>> SetEffectivityAsync(Subject subject, string number, string effectivity, string? correlationId, CancellationToken ct = default) =>
        throw new NotSupportedException("Effectivity is delivered in Phase 2 (configuration management, A19).");

    /// <summary>Manufacturing BOM changes are Phase 2 (A18).</summary>
    public Task<Outcome<ChangeDetail>> AddMbomChangeAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        throw new NotSupportedException("Manufacturing BOM (MBOM) changes are delivered in Phase 2.");
}
