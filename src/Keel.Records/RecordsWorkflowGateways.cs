using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Records;

/// <summary>
/// Lets the workflow engine (which must not reference this module) review the three decisions records management signs for: publishing a retention schedule, releasing a
/// legal hold and approving a disposition request (ADR-0041, ADR-0018). As for suppliers, the engine's "revision" and "iteration" are the record itself; the signed content is a
/// readable summary plus a digest recomputed from the live rows, so a change after signing makes verification fail. All methods run in the engine's transaction.
/// </summary>
internal abstract class RecordsGatewayBase(IAuditLog audit) : IWorkflowObjectGateway
{
    public abstract string ObjectType { get; }
    protected IAuditLog Audit => audit;

    protected abstract Task<WorkflowObject?> LoadAsync(NpgsqlConnection c, NpgsqlTransaction t, string? key, Guid? id, bool forUpdate, CancellationToken ct);
    public abstract Task<GatewayResult> BeginReviewAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct);
    public abstract Task<GatewayResult> CompleteReleaseAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Guid boundIterationId, Subject actor, DateTimeOffset now, CancellationToken ct);
    public abstract Task<GatewayResult> ReturnToInWorkAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct);
    public abstract Task<IterationContent?> LoadIterationContentAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct);

    public Task<WorkflowObject?> FindByKeyAsync(DbConnection conn, DbTransaction tx, string key, bool forUpdate, CancellationToken ct) => LoadAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, key, null, forUpdate, ct);
    public Task<WorkflowObject?> FindByRevisionAsync(DbConnection conn, DbTransaction tx, Guid revisionId, bool forUpdate, CancellationToken ct) => LoadAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, null, revisionId, forUpdate, ct);

    public async Task<IReadOnlyDictionary<Guid, WorkflowObject>> FindManyByRevisionAsync(DbConnection conn, DbTransaction tx, IReadOnlyCollection<Guid> revisionIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, WorkflowObject>();
        foreach (var id in revisionIds)
            if (await FindByRevisionAsync(conn, tx, id, false, ct) is { } o) result[id] = o;
        return result;
    }

    protected static string Digest(params string?[] parts) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))));

    protected static async Task ExecAsync(NpgsqlConnection c, NpgsqlTransaction t, string sql, CancellationToken ct, params (string, object?)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, c, t);
        foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

// ---------------------------------------------------------------- retention schedule

internal sealed class ScheduleWorkflowGateway(IAuditLog audit) : RecordsGatewayBase(audit)
{
    public const string ContentFormat = "retention-schedule-content/1";
    public override string ObjectType => "retention_schedule";

    protected override async Task<WorkflowObject?> LoadAsync(NpgsqlConnection c, NpgsqlTransaction t, string? key, Guid? id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT id, version, status, title, owning_org FROM rec.schedule WHERE {(key is not null ? "version = @v" : "id = @id")}{(forUpdate ? " FOR UPDATE" : "")}", c, t);
        if (key is not null)
        {
            if (!key.StartsWith('v') || !int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return null;
            cmd.Parameters.AddWithValue("v", v);
        }
        else cmd.Parameters.AddWithValue("id", id!.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var status = r.GetString(2);
        return new WorkflowObject(ObjectType, r.GetGuid(0), $"v{r.GetInt32(1)}", r.GetString(3), r.GetInt32(1).ToString(CultureInfo.InvariantCulture), r.GetGuid(0), r.GetGuid(0), $"v{r.GetInt32(1)}", status,
            ScheduleStatuses.Label(status), status == ScheduleStatuses.InReview, true, null, RecordsStore.Neutral(r.GetString(4)));
    }

    public override async Task<GatewayResult> BeginReviewAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var cur = await LoadAsync(c, t, null, obj.Id, true, ct);
        if (cur is null) return GatewayResult.Fail("The schedule no longer exists.");
        if (cur.State != ScheduleStatuses.Draft) return GatewayResult.Fail($"Version {cur.Revision} is {cur.StateLabel.ToLowerInvariant()}; only a draft can be submitted for publication.");
        var view = await ScheduleService.LoadAsync(c, t, "s.id = @id", cmd => cmd.Parameters.AddWithValue("id", obj.Id), false, ct);
        if (view!.Classes.Count == 0) return GatewayResult.Fail("A schedule needs at least one record class before it can be published.");
        var problems = view.Classes.SelectMany(cl => RetentionRules.Validate(cl.Input).Select(p => $"{cl.Input.Code}: {p}")).ToList();
        if (problems.Count > 0) return GatewayResult.Fail(string.Join(" ", problems));
        await ExecAsync(c, t, "UPDATE rec.schedule SET status = 'in_review', revision = revision + 1, updated_at = @now WHERE id = @id", ct, ("now", now), ("id", obj.Id));
        await Audit.AppendAsync(t, RecordsStore.Entry(actor, "records.schedule_submit", AuditOutcome.Success, ObjectType, obj.Key, null, ("version", obj.Revision)), ct);
        return GatewayResult.Ok;
    }

    public override async Task<GatewayResult> CompleteReleaseAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Guid boundIterationId, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var cur = await LoadAsync(c, t, null, obj.Id, true, ct);
        if (cur is null) return GatewayResult.Fail("The schedule no longer exists.");
        if (cur.State != ScheduleStatuses.InReview) return GatewayResult.Fail($"Version {cur.Revision} is not in review.");
        if (boundIterationId != cur.Id) return GatewayResult.Fail($"The review is not bound to {cur.Key}.");
        await ExecAsync(c, t, "UPDATE rec.schedule SET status = 'superseded', updated_at = @now WHERE status = 'published'", ct, ("now", now));
        await ExecAsync(c, t, "UPDATE rec.schedule SET status = 'published', published_at = @now, published_by = @by, revision = revision + 1, updated_at = @now WHERE id = @id", ct,
            ("now", now), ("by", actor.Username ?? actor.Id), ("id", obj.Id));
        await Audit.AppendAsync(t, RecordsStore.Entry(actor, "records.schedule_publish", AuditOutcome.Success, ObjectType, cur.Key, null, ("version", cur.Revision), ("path", "workflow")), ct);
        return GatewayResult.Ok;
    }

    public override async Task<GatewayResult> ReturnToInWorkAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var cur = await LoadAsync(c, t, null, obj.Id, true, ct);
        if (cur is null) return GatewayResult.Fail("The schedule no longer exists.");
        if (cur.State != ScheduleStatuses.InReview) return GatewayResult.Fail($"Version {cur.Revision} is not in review.");
        await ExecAsync(c, t, "UPDATE rec.schedule SET status = 'draft', revision = revision + 1, updated_at = @now WHERE id = @id", ct, ("now", now), ("id", obj.Id));
        await Audit.AppendAsync(t, RecordsStore.Entry(actor, "records.schedule_review_cancel", AuditOutcome.Success, ObjectType, cur.Key, null), ct);
        return GatewayResult.Ok;
    }

    public override async Task<IterationContent?> LoadIterationContentAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var view = await ScheduleService.LoadAsync(c, t, "s.id = @id", cmd => cmd.Parameters.AddWithValue("id", iterationId), false, ct);
        if (view is null) return null;
        var lines = view.Classes.Select(cl => RetentionRules.Describe(cl.Input) + $" (priority {cl.Input.Priority}; kinds {Join(cl.Input.Kinds)}; types {Join(cl.Input.Types)}; programs {Join(cl.Input.Programs)}; jurisdictions {Join(cl.Input.Jurisdictions)}; CUI {Join(cl.Input.Cui)}; statements {Join(cl.Input.Statements)}; citation {cl.Input.Citation ?? "none"})").ToList();
        var digest = Digest(view.Version.ToString(CultureInfo.InvariantCulture), view.Title, view.Notes, string.Join('\n', lines));
        var description = new StringBuilder().AppendLine($"Retention schedule version {view.Version}: {view.Title}").AppendLine(view.Notes ?? "No notes").Append(lines.Count == 0 ? "No classes" : "Classes:\n" + string.Join('\n', lines)).ToString();
        var attributes = new JsonObject { ["format"] = ContentFormat, ["version"] = view.Version, ["classes"] = view.Classes.Count, ["contentSha256"] = digest };
        return new IterationContent(iterationId, ObjectType, $"v{view.Version}", view.Version.ToString(CultureInfo.InvariantCulture), $"v{view.Version}", description, attributes.ToJsonString());
    }

    private static string Join(string[] values) => values.Length == 0 ? "any" : string.Join("/", values);
}

// ---------------------------------------------------------------- legal hold release

internal sealed class HoldWorkflowGateway(IAuditLog audit) : RecordsGatewayBase(audit)
{
    public const string ContentFormat = "legal-hold-content/1";
    public override string ObjectType => "legal_hold";

    private sealed record Row(Guid Id, string Number, string Name, string ScopeKind, string ScopeJson, bool InRelease, bool Released, string Org);

    private static async Task<Row?> ReadAsync(NpgsqlConnection c, NpgsqlTransaction t, string? key, Guid? id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT id, number, name, scope_kind, scope::text, in_release, released_at IS NOT NULL, owning_org FROM rec.hold WHERE {(key is not null ? "number = @k" : "id = @id")}{(forUpdate ? " FOR UPDATE" : "")}", c, t);
        if (key is not null) cmd.Parameters.AddWithValue("k", key); else cmd.Parameters.AddWithValue("id", id!.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new Row(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetBoolean(5), r.GetBoolean(6), r.GetString(7)) : null;
    }

    protected override async Task<WorkflowObject?> LoadAsync(NpgsqlConnection c, NpgsqlTransaction t, string? key, Guid? id, bool forUpdate, CancellationToken ct)
    {
        var h = await ReadAsync(c, t, key, id, forUpdate, ct);
        if (h is null) return null;
        var label = h.Released ? "Released" : h.InRelease ? "Release requested" : "Active";
        return new WorkflowObject(ObjectType, h.Id, h.Number, h.Name, "1", h.Id, h.Id, h.Number, h.Released ? "released" : h.InRelease ? "in_release" : "active", label, h.InRelease, true, null, RecordsStore.Neutral(h.Org));
    }

    public override async Task<GatewayResult> BeginReviewAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var h = await ReadAsync(c, t, null, obj.Id, true, ct);
        if (h is null) return GatewayResult.Fail("The hold no longer exists.");
        if (h.Released) return GatewayResult.Fail($"{h.Number} is already released.");
        if (h.InRelease) return GatewayResult.Fail($"{h.Number} already has a release request.");
        await ExecAsync(c, t, "UPDATE rec.hold SET in_release = true, revision = revision + 1 WHERE id = @id", ct, ("id", h.Id));
        await Audit.AppendAsync(t, RecordsStore.Entry(actor, "records.hold_release_request", AuditOutcome.Success, ObjectType, h.Number, null), ct);
        return GatewayResult.Ok;
    }

    public override async Task<GatewayResult> CompleteReleaseAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Guid boundIterationId, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        await RecordsStore.TakeGateAsync(c, t, ct); // releasing a hold serializes with disposition like placing one does
        var h = await ReadAsync(c, t, null, obj.Id, true, ct);
        if (h is null) return GatewayResult.Fail("The hold no longer exists.");
        if (h.Released) return GatewayResult.Fail($"{h.Number} is already released.");
        if (!h.InRelease) return GatewayResult.Fail($"{h.Number} has no release request.");
        if (boundIterationId != h.Id) return GatewayResult.Fail($"The review is not bound to {h.Number}.");
        await ExecAsync(c, t, "UPDATE rec.hold SET released_at = @now, released_by = @by, in_release = false, revision = revision + 1 WHERE id = @id", ct, ("now", now), ("by", actor.Username ?? actor.Id), ("id", h.Id));
        await Audit.AppendAsync(t, RecordsStore.Entry(actor, "records.hold_release", AuditOutcome.Success, ObjectType, h.Number, null, ("path", "workflow")), ct);
        return GatewayResult.Ok;
    }

    public override async Task<GatewayResult> ReturnToInWorkAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var h = await ReadAsync(c, t, null, obj.Id, true, ct);
        if (h is null) return GatewayResult.Fail("The hold no longer exists.");
        if (!h.InRelease) return GatewayResult.Fail($"{h.Number} has no release request.");
        await ExecAsync(c, t, "UPDATE rec.hold SET in_release = false, revision = revision + 1 WHERE id = @id", ct, ("id", h.Id));
        await Audit.AppendAsync(t, RecordsStore.Entry(actor, "records.hold_release_cancel", AuditOutcome.Success, ObjectType, h.Number, null), ct);
        return GatewayResult.Ok;
    }

    public override async Task<IterationContent?> LoadIterationContentAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var h = await ReadAsync(c, t, null, iterationId, false, ct);
        if (h is null) return null;
        string membership;
        await using (var cmd = new NpgsqlCommand("SELECT coalesce(encode(sha256(convert_to(string_agg(object_kind || ':' || object_id::text, ',' ORDER BY object_kind, object_id), 'UTF8')), 'hex'), 'empty') FROM rec.hold_member WHERE hold_id = @h", c, t))
        {
            cmd.Parameters.AddWithValue("h", h.Id);
            membership = (string)(await cmd.ExecuteScalarAsync(ct))!;
        }
        // The description never says how many objects the hold covers: the approver may not be able to read them (ADR-0041).
        var description = $"Release of legal hold {h.Number}: {h.Name}\nScope: {HoldService.ScopeText(h.ScopeJson)}\nThe signature covers the hold and a digest of everything it covers.";
        var attributes = new JsonObject { ["format"] = ContentFormat, ["scopeKind"] = h.ScopeKind, ["membershipSha256"] = membership, ["contentSha256"] = Digest(h.Number, h.Name, h.ScopeJson, membership) };
        return new IterationContent(iterationId, ObjectType, h.Number, "1", h.Number, description, attributes.ToJsonString());
    }
}

// ---------------------------------------------------------------- disposition approval

internal sealed class DispositionWorkflowGateway(IAuditLog audit, TimeProvider clock) : RecordsGatewayBase(audit), IWorkflowObjectGateway
{
    public const string ContentFormat = "disposition-content/1";
    public override string ObjectType => "disposition";

    private sealed record Row(Guid Id, string Number, string Status, Markings Markings);

    private static async Task<Row?> ReadAsync(NpgsqlConnection c, NpgsqlTransaction t, string? key, Guid? id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT d.id, d.number, d.status, {MarkingColumns.Select("d")} FROM rec.disposition d WHERE {(key is not null ? "d.number = @k" : "d.id = @id")}{(forUpdate ? " FOR UPDATE OF d" : "")}", c, t);
        if (key is not null) cmd.Parameters.AddWithValue("k", key); else cmd.Parameters.AddWithValue("id", id!.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new Row(r.GetGuid(0), r.GetString(1), r.GetString(2), MarkingColumns.Read(r, 3)) : null;
    }

    protected override async Task<WorkflowObject?> LoadAsync(NpgsqlConnection c, NpgsqlTransaction t, string? key, Guid? id, bool forUpdate, CancellationToken ct)
    {
        var d = await ReadAsync(c, t, key, id, forUpdate, ct);
        return d is null ? null : new WorkflowObject(ObjectType, d.Id, d.Number, $"Disposition request {d.Number}", "1", d.Id, d.Id, d.Number, d.Status, DispositionStatuses.Label(d.Status), d.Status == DispositionStatuses.InReview, true, null, d.Markings);
    }

    private sealed record ItemRow(string Kind, Guid ObjectId, string Number, string Class, int Version, string Action, DateOnly EligibleOn);

    private static async Task<List<ItemRow>> ItemsAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid id, CancellationToken ct)
    {
        var items = new List<ItemRow>();
        await using var cmd = new NpgsqlCommand("SELECT object_kind, object_id, object_number, class_code, schedule_version, action, eligible_on FROM rec.disposition_item WHERE disposition_id = @d ORDER BY object_kind, object_number", c, t);
        cmd.Parameters.AddWithValue("d", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) items.Add(new ItemRow(r.GetString(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetFieldValue<DateOnly>(6)));
        return items;
    }

    public override async Task<GatewayResult> BeginReviewAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var d = await ReadAsync(c, t, null, obj.Id, true, ct);
        if (d is null) return GatewayResult.Fail("The request no longer exists.");
        if (d.Status != DispositionStatuses.Draft) return GatewayResult.Fail($"{d.Number} is {DispositionStatuses.Label(d.Status).ToLowerInvariant()}; only a draft can be submitted.");
        await RecordsStore.TakeGateAsync(c, t, ct);
        await HoldService.ReevaluateAllActiveAsync(c, t, now, ct);
        var items = await ItemsAsync(c, t, d.Id, ct);
        if (items.Count == 0) return GatewayResult.Fail("The request lists no objects.");
        var published = await RecordsStore.LoadPublishedAsync(c, t, ct);
        if (published is null) return GatewayResult.Fail("No retention schedule is published.");
        var events = await RecordsStore.LoadProgramEventsAsync(c, t, ct);
        var today = RecordsStore.Today(clock);
        var problems = new List<string>();
        foreach (var i in items)
        {
            var facts = await RecordsStore.LoadFactsByIdAsync(c, t, i.Kind, i.ObjectId, ct);
            if (facts is null) problems.Add($"{i.Number} no longer exists.");
            else if (await RecordsStore.IsTombstonedAsync(c, t, i.Kind, i.ObjectId, ct)) problems.Add($"{i.Number} was already disposed of.");
            else if (await RecordsStore.IsHeldAsync(c, t, i.Kind, i.ObjectId, ct)) problems.Add($"{i.Number} is under legal hold.");
            else if (RetentionRules.Assign(published.Rules, facts, events) is not { } a || a.EligibleOn > today || a.Code != i.Class || a.Disposition != i.Action) problems.Add($"{i.Number} is no longer due for {DispositionActions.Label(i.Action).ToLowerInvariant()}.");
        }
        if (problems.Count > 0) return GatewayResult.Fail(string.Join(" ", problems));
        await ExecAsync(c, t, "UPDATE rec.disposition SET status = 'in_review', revision = revision + 1, updated_at = @now WHERE id = @id", ct, ("now", now), ("id", d.Id));
        await Audit.AppendAsync(t, RecordsStore.Entry(actor, "records.disposition_submit", AuditOutcome.Success, ObjectType, d.Number, null, ("items", items.Count.ToString(CultureInfo.InvariantCulture))), ct);
        return GatewayResult.Ok;
    }

    public override async Task<GatewayResult> CompleteReleaseAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Guid boundIterationId, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var d = await ReadAsync(c, t, null, obj.Id, true, ct);
        if (d is null) return GatewayResult.Fail("The request no longer exists.");
        if (d.Status != DispositionStatuses.InReview) return GatewayResult.Fail($"{d.Number} is not in review.");
        if (boundIterationId != d.Id) return GatewayResult.Fail($"The review is not bound to {d.Number}.");
        await ExecAsync(c, t, "UPDATE rec.disposition SET status = 'approved', revision = revision + 1, updated_at = @now WHERE id = @id", ct, ("now", now), ("id", d.Id));
        await Audit.AppendAsync(t, RecordsStore.Entry(actor, "records.disposition_approve", AuditOutcome.Success, ObjectType, d.Number, null, ("path", "workflow")), ct);
        return GatewayResult.Ok;
    }

    public override async Task<GatewayResult> ReturnToInWorkAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var d = await ReadAsync(c, t, null, obj.Id, true, ct);
        if (d is null) return GatewayResult.Fail("The request no longer exists.");
        if (d.Status != DispositionStatuses.InReview) return GatewayResult.Fail($"{d.Number} is not in review.");
        await ExecAsync(c, t, "UPDATE rec.disposition SET status = 'draft', revision = revision + 1, updated_at = @now WHERE id = @id", ct, ("now", now), ("id", d.Id));
        await Audit.AppendAsync(t, RecordsStore.Entry(actor, "records.disposition_review_cancel", AuditOutcome.Success, ObjectType, d.Number, null), ct);
        return GatewayResult.Ok;
    }

    /// <summary>A rejection ends the request (rejected is final): the objects return to the queue.</summary>
    public async Task<GatewayResult> CompleteRejectionAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var d = await ReadAsync(c, t, null, obj.Id, true, ct);
        if (d is null) return GatewayResult.Fail("The request no longer exists.");
        if (d.Status != DispositionStatuses.InReview) return GatewayResult.Fail($"{d.Number} is not in review.");
        await ExecAsync(c, t, "UPDATE rec.disposition SET status = 'rejected', revision = revision + 1, updated_at = @now WHERE id = @id", ct, ("now", now), ("id", d.Id));
        await Audit.AppendAsync(t, RecordsStore.Entry(actor, "records.disposition_reject", AuditOutcome.Success, ObjectType, d.Number, null), ct);
        return GatewayResult.Ok;
    }


    public override async Task<IterationContent?> LoadIterationContentAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var d = await ReadAsync(c, t, null, iterationId, false, ct);
        if (d is null) return null;
        var items = await ItemsAsync(c, t, d.Id, ct);
        var lines = items.Select(i => $"{RecordKinds.Label(i.Kind)} {i.Number}: class {i.Class}, {DispositionActions.Label(i.Action).ToLowerInvariant()}, due {i.EligibleOn:yyyy-MM-dd} (schedule version {i.Version})").ToList();
        var digest = Digest(d.Number, d.Markings.BannerText(), string.Join('\n', lines));
        var description = new StringBuilder().AppendLine($"Disposition request {d.Number}").AppendLine($"Markings: {d.Markings.BannerText()}").Append("Objects:\n" + string.Join('\n', lines)).ToString();
        var attributes = new JsonObject { ["format"] = ContentFormat, ["items"] = items.Count, ["contentSha256"] = digest };
        return new IterationContent(iterationId, ObjectType, d.Number, "1", d.Number, description, attributes.ToJsonString());
    }
}
