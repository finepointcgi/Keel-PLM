using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Npgsql;

namespace Keel.Compliance;

/// <summary>
/// Lets the workflow engine (Keel.Workflow, which must not reference this module) review and publish a version of a regulatory list like any other object (ADR-0018,
/// ADR-0040): publishing is granted by signatures. The "revision" and "iteration" of the engine are the version row; the signed content is a readable summary of the
/// entries, thresholds, exemptions and effective date plus a digest recomputed from the live rows, so a change after signing makes verification fail. The object key is
/// <c>{list code}-{version}</c>. Regulatory lists are public reference data: the markings the engine checks readers and signers against are the unmarked ones.
/// Releasing makes the version the current one, supersedes the previous version and re-evaluates every declaration in the same transaction, telling the watchers of
/// the parts whose declaration became non-compliant (pointers only).
/// </summary>
internal sealed class ComplianceListWorkflowGateway(IAuditLog audit, TimeProvider clock, INotificationSink? notifications = null) : IWorkflowObjectGateway
{
    public const string ContentFormat = "reg-list-version-content/1";
    public string ObjectType => ComplianceStore.ListVersionObjectType;

    /// <summary>Regulations are public: no export control, no CUI, distribution A. The owning organization is the one that prepared the version (the Modify rule needs it).</summary>
    internal static Markings PublicMarkings(string owningOrg) => Markings.Create(ExportJurisdiction.None, DistributionStatement.A, string.IsNullOrWhiteSpace(owningOrg) ? "UNKNOWN" : owningOrg).Value;

    public static string KeyOf(string code, int version) => $"{code}-{version.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryParseKey(string key, out string code, out int version)
    {
        code = "";
        version = 0;
        var dash = key.LastIndexOf('-');
        if (dash <= 0 || !int.TryParse(key.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out version) || version < 1) return false;
        code = key[..dash];
        return true;
    }

    private static WorkflowObject Map(RegVersionSummary v, string code, string name) =>
        new(ComplianceStore.ListVersionObjectType, v.Id, KeyOf(code, v.Version), $"{name} version {v.Version}", v.Version.ToString(CultureInfo.InvariantCulture), v.Id, v.Id, KeyOf(code, v.Version),
            v.Status, VersionStates.Label(v.Status), v.Status == VersionStates.InReview, true, null, PublicMarkings(v.OwningOrg));

    private static async Task<(RegVersionSummary V, string Code, string Name, string Kind)?> ByIdAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT l.code, v.version FROM cmp.reg_list_version v JOIN cmp.reg_list l ON l.id = v.list_id WHERE v.id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var (code, version) = (r.GetString(0), r.GetInt32(1));
        await r.CloseAsync();
        var head = await RegulatoryCatalogService.LoadVersionHeadAsync(conn, tx, code, version, forUpdate, ct);
        return head is null ? null : (head.Value.Summary, head.Value.ListCode, head.Value.ListName, head.Value.Kind);
    }

    public async Task<WorkflowObject?> FindByKeyAsync(DbConnection conn, DbTransaction tx, string key, bool forUpdate, CancellationToken ct)
    {
        if (!TryParseKey(key, out var code, out var version)) return null;
        var head = await RegulatoryCatalogService.LoadVersionHeadAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, code, version, forUpdate, ct);
        return head is null ? null : Map(head.Value.Summary, head.Value.ListCode, head.Value.ListName);
    }

    public async Task<WorkflowObject?> FindByRevisionAsync(DbConnection conn, DbTransaction tx, Guid revisionId, bool forUpdate, CancellationToken ct)
    {
        var v = await ByIdAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, revisionId, forUpdate, ct);
        return v is null ? null : Map(v.Value.V, v.Value.Code, v.Value.Name);
    }

    public async Task<IReadOnlyDictionary<Guid, WorkflowObject>> FindManyByRevisionAsync(DbConnection conn, DbTransaction tx, IReadOnlyCollection<Guid> revisionIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, WorkflowObject>();
        foreach (var id in revisionIds)
            if (await FindByRevisionAsync(conn, tx, id, false, ct) is { } o) result[id] = o;
        return result;
    }

    private static async Task SetStatusAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid id, string status, Subject actor, DateTimeOffset now, bool publish, CancellationToken ct)
    {
        await using var upd = new NpgsqlCommand(
            $"UPDATE cmp.reg_list_version SET status = @s, revision = revision + 1, updated_at = @now, updated_by = @by{(publish ? ", published_at = @now, published_by = @by" : "")} WHERE id = @id", c, t);
        upd.Parameters.AddWithValue("s", status);
        upd.Parameters.AddWithValue("now", now);
        upd.Parameters.AddWithValue("by", actor.Username ?? actor.Id);
        upd.Parameters.AddWithValue("id", id);
        await upd.ExecuteNonQueryAsync(ct);
    }

    public async Task<GatewayResult> BeginReviewAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var v = await ByIdAsync(c, t, obj.Id, true, ct);
        if (v is null) return GatewayResult.Fail("The list version no longer exists.");
        var key = KeyOf(v.Value.Code, v.Value.V.Version);
        if (v.Value.V.Status != VersionStates.Draft) return GatewayResult.Fail($"{key} is {VersionStates.Label(v.Value.V.Status).ToLowerInvariant()}; only a draft can be submitted.");
        if (v.Value.V.Entries == 0) return GatewayResult.Fail("A list version without any substance cannot be published.");
        await SetStatusAsync(c, t, v.Value.V.Id, VersionStates.InReview, actor, now, false, ct);
        await audit.AppendAsync(t, ComplianceStore.Entry(actor, "compliance.list_submit", AuditOutcome.Success, ComplianceStore.ListVersionObjectType, key, null,
            ("entries", v.Value.V.Entries.ToString(CultureInfo.InvariantCulture))), ct);
        return GatewayResult.Ok;
    }

    public async Task<GatewayResult> CompleteReleaseAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Guid boundIterationId, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var v = await ByIdAsync(c, t, obj.Id, true, ct);
        if (v is null) return GatewayResult.Fail("The list version no longer exists.");
        var key = KeyOf(v.Value.Code, v.Value.V.Version);
        if (v.Value.V.Status != VersionStates.InReview) return GatewayResult.Fail($"{key} is not in a review.");
        if (boundIterationId != v.Value.V.Id) return GatewayResult.Fail($"The review is not bound to {key}.");
        await using (var sup = new NpgsqlCommand(
            "UPDATE cmp.reg_list_version SET status = 'superseded' WHERE status = 'published' AND list_id = (SELECT list_id FROM cmp.reg_list_version WHERE id = @id)", c, t))
        {
            sup.Parameters.AddWithValue("id", v.Value.V.Id);
            await sup.ExecuteNonQueryAsync(ct);
        }
        await SetStatusAsync(c, t, v.Value.V.Id, VersionStates.Published, actor, now, true, ct);

        // Re-evaluate every declaration against the regulations now in force.
        var ctx = await ComplianceEvaluator.ContextAsync(c, t, clock, null, null, ct);
        var changes = await ComplianceEvaluator.RefreshAllAsync(c, t, ctx, ct);
        var nonCompliant = 0;
        foreach (var (row, from, to) in changes)
        {
            await audit.AppendAsync(t, ComplianceStore.Entry(actor, "compliance.status_change", AuditOutcome.Success, ComplianceStore.PartObjectType, row.PartNumber, null,
                ("declaration", row.Number), ("from", from), ("to", to), ("cause", "list_published")), ct);
            if (to == DeclarationStatuses.NonCompliant)
            {
                nonCompliant++;
                if (notifications is not null)
                    await notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.ComplianceNonCompliant, AwarenessKinds.Part, row.PartNumber, row.PartMarkings, actor.Id), ct);
            }
        }
        await audit.AppendAsync(t, ComplianceStore.Entry(actor, "compliance.list_publish", AuditOutcome.Success, ComplianceStore.ListVersionObjectType, key, null,
            ("effectiveFrom", v.Value.V.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("changed", changes.Count.ToString(CultureInfo.InvariantCulture)),
            ("nonCompliant", nonCompliant.ToString(CultureInfo.InvariantCulture)), ("path", "workflow")), ct);
        return GatewayResult.Ok;
    }

    public async Task<GatewayResult> ReturnToInWorkAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var v = await ByIdAsync(c, t, obj.Id, true, ct);
        if (v is null) return GatewayResult.Fail("The list version no longer exists.");
        var key = KeyOf(v.Value.Code, v.Value.V.Version);
        if (v.Value.V.Status != VersionStates.InReview) return GatewayResult.Fail($"{key} is not in a review.");
        await SetStatusAsync(c, t, v.Value.V.Id, VersionStates.Draft, actor, now, false, ct);
        await audit.AppendAsync(t, ComplianceStore.Entry(actor, "compliance.list_review_cancel", AuditOutcome.Success, ComplianceStore.ListVersionObjectType, key, null), ct);
        return GatewayResult.Ok;
    }

    public async Task<IterationContent?> LoadIterationContentAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var v = await ByIdAsync(c, t, iterationId, false, ct);
        if (v is null) return null;
        var (entries, exemptions) = await RegulatoryCatalogService.LoadVersionContentAsync(c, t, v.Value.V.Id, ct);
        var lines = entries.Select(e => $"{e.CasNumber}|{ComplianceRules.Num(e.ThresholdPct)}|{e.Mineral}").Concat(
            exemptions.Select(x => $"exemption|{x.Code}|{x.CasNumber}|{x.ExpiresOn:yyyy-MM-dd}|{x.Description}")).ToList();
        // The live digest: editing a row after signing changes what verification computes.
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            v.Value.Code, v.Value.V.Version.ToString(CultureInfo.InvariantCulture), v.Value.V.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), string.Join('\n', lines)))));
        var description = new StringBuilder()
            .AppendLine($"{v.Value.Name} (list {v.Value.Code}), version {v.Value.V.Version}, {ListKinds.Label(v.Value.Kind).ToLowerInvariant()}")
            .AppendLine($"Effective from {v.Value.V.EffectiveFrom:yyyy-MM-dd}")
            .AppendLine($"{entries.Count} substance{(entries.Count == 1 ? "" : "s")}, {exemptions.Count} exemption{(exemptions.Count == 1 ? "" : "s")}")
            .Append(string.Join('\n', entries.Select(e => $"{e.CasNumber} {e.SubstanceName}: {ComplianceRules.Num(e.ThresholdPct)} %")))
            .ToString();
        var attributes = new JsonObject { ["format"] = ContentFormat, ["version"] = v.Value.V.Version, ["entries"] = entries.Count, ["exemptions"] = exemptions.Count, ["contentSha256"] = digest };
        return new IterationContent(iterationId, ComplianceStore.ListVersionObjectType, KeyOf(v.Value.Code, v.Value.V.Version), v.Value.V.Version.ToString(CultureInfo.InvariantCulture),
            KeyOf(v.Value.Code, v.Value.V.Version), description, attributes.ToJsonString());
    }
}
