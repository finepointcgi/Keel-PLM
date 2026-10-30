using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Workflow;

/// <summary>
/// The workflow module's side of the admin template API (<see cref="IWorkflowTemplateAdmin"/>, increment 2.2). Validation is the engine's own
/// parser, publishing is the audited upload that has existed since 1.5, and pins are stored in <c>wf.template_pin</c>.
/// </summary>
public sealed class WorkflowTemplateAdmin(NpgsqlDataSource dataSource, IAuditLog audit, WorkflowService workflow, IEnumerable<IWorkflowObjectGateway> gateways, TimeProvider clock)
    : IWorkflowTemplateAdmin
{
    private const string ObjectType = "workflow_template";

    public IReadOnlyList<string> ReviewableObjectTypes => [.. gateways.Select(g => g.ObjectType).Distinct().Order(StringComparer.Ordinal)];

    private async Task<Outcome<T>?> DenyAsync<T>(Subject subject, string action, string? correlationId, CancellationToken ct)
    {
        if (AccessPolicy.HasRoleFor(subject, PlmAction.Administer)) return null;
        await audit.AppendAsync(WorkflowService.Entry(subject, action, AuditOutcome.Denied, ObjectType, null, correlationId, ("reasons", AccessPolicy.RoleMissing)), ct);
        return Outcome<T>.Forbidden([AccessPolicy.RoleMissing]);
    }

    private static TemplateVersionInfo ToInfo(TemplateRow r, IReadOnlyDictionary<(string, int), int> running) =>
        new(r.Id, r.Version, r.Name, r.Description, r.ObjectType, r.IsActive, r.CreatedAt, r.CreatedBy, JsonNode.Parse(r.Definition)!, running.GetValueOrDefault((r.Id, r.Version)));

    public async Task<Outcome<IReadOnlyList<TemplateVersionInfo>>> ListVersionsAsync(Subject subject, bool allVersions, string? correlationId, CancellationToken ct = default)
    {
        if (await DenyAsync<IReadOnlyList<TemplateVersionInfo>>(subject, "admin.workflow_templates_list", correlationId, ct) is { } denied) return denied;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await WorkflowStore.LoadTemplatesAsync(conn, null, activeOnly: !allVersions, ct);
        var running = new Dictionary<(string, int), int>();
        // Running reviews are marked data (their instance rows carry the item's markings): the count covers only the reviews this administrator may read.
        var filter = AccessPolicy.BuildReadFilter(subject, "i");
        await using (var cmd = new NpgsqlCommand($"SELECT i.template_id, i.template_version, count(*) FROM wf.instance i WHERE {filter.Sql} AND i.status = 'Active' GROUP BY 1, 2", conn))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) running[(rd.GetString(0), rd.GetInt32(1))] = (int)rd.GetInt64(2);
        }
        return Outcome<IReadOnlyList<TemplateVersionInfo>>.Ok([.. rows.Select(r => ToInfo(r, running))]);
    }

    public async Task<Outcome<TemplateVersionInfo>> ValidateAsync(Subject subject, string definitionJson, CancellationToken ct = default)
    {
        if (await DenyAsync<TemplateVersionInfo>(subject, "admin.workflow_template_validate", null, ct) is { } denied) return denied;
        var parsed = TemplateParser.Parse(definitionJson);
        if (!parsed.IsSuccess) return Outcome<TemplateVersionInfo>.Invalid(parsed.Errors);
        var t = parsed.Value;
        var errors = new List<string>();
        if (!ReviewableObjectTypes.Contains(t.ObjectType)) errors.Add($"No workflow can run on objects of type '{t.ObjectType}'.");
        if (t.Name.Length > 200) errors.Add("The template name must be at most 200 characters.");
        if (errors.Count > 0) return Outcome<TemplateVersionInfo>.Invalid(errors);
        return Outcome<TemplateVersionInfo>.Ok(new TemplateVersionInfo(t.Id, 0, t.Name, t.Description, t.ObjectType, false, clock.GetUtcNow(), subject.Username ?? subject.Id,
            JsonNode.Parse(definitionJson)!));
    }

    public async Task<Outcome<TemplateVersionInfo>> PublishAsync(Subject subject, string definitionJson, string? correlationId, CancellationToken ct = default)
    {
        // The upload checks the role, validates, versions and audits (workflow.template_upload) in one transaction.
        var o = await workflow.UploadTemplateAsync(subject, definitionJson, correlationId, ct);
        return o.Kind switch
        {
            OutcomeKind.Ok => Outcome<TemplateVersionInfo>.Ok(new TemplateVersionInfo(o.Value!.Id, o.Value.Version, o.Value.Name, o.Value.Description, o.Value.ObjectType,
                o.Value.IsActive, o.Value.CreatedAt, o.Value.CreatedBy, o.Value.Definition)),
            OutcomeKind.Invalid => Outcome<TemplateVersionInfo>.Invalid(o.Errors),
            OutcomeKind.Forbidden => Outcome<TemplateVersionInfo>.Forbidden(o.Errors),
            _ => Outcome<TemplateVersionInfo>.Conflict(o.Errors.Count > 0 ? o.Errors[0] : "The template could not be published."),
        };
    }

    public async Task<Outcome<IReadOnlyList<TemplatePin>>> ListPinsAsync(Subject subject, CancellationToken ct = default)
    {
        if (await DenyAsync<IReadOnlyList<TemplatePin>>(subject, "admin.workflow_pins_list", null, ct) is { } denied) return denied;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return Outcome<IReadOnlyList<TemplatePin>>.Ok(await LoadPinsAsync(conn, null, ct));
    }

    private static async Task<List<TemplatePin>> LoadPinsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var list = new List<TemplatePin>();
        await using var cmd = new NpgsqlCommand("SELECT object_type, template_id, pinned_at, pinned_by FROM wf.template_pin ORDER BY object_type", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new TemplatePin(r.GetString(0), r.GetString(1), new DateTimeOffset(r.GetDateTime(2), TimeSpan.Zero), r.GetString(3)));
        return list;
    }

    public async Task<Outcome<TemplatePin?>> PinAsync(Subject subject, string objectType, string? templateId, string? correlationId, CancellationToken ct = default)
    {
        if (await DenyAsync<TemplatePin?>(subject, "admin.workflow_pin", correlationId, ct) is { } denied) return denied;
        if (!ReviewableObjectTypes.Contains(objectType)) return Outcome<TemplatePin?>.Invalid([$"No workflow can run on objects of type '{objectType}'."]);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (templateId is not null)
        {
            var active = (await WorkflowStore.LoadTemplatesAsync(conn, tx, activeOnly: true, ct)).FirstOrDefault(t => t.Id == templateId);
            if (active is null) return Outcome<TemplatePin?>.Invalid([$"There is no active template '{templateId}'."]);
            if (active.ObjectType != objectType) return Outcome<TemplatePin?>.Invalid([$"Template '{templateId}' is for {active.ObjectType} objects, not {objectType}."]);
        }
        var before = (await LoadPinsAsync(conn, tx, ct)).FirstOrDefault(p => p.ObjectType == objectType);
        var now = clock.GetUtcNow();
        if (templateId is null)
        {
            await using var del = new NpgsqlCommand("DELETE FROM wf.template_pin WHERE object_type = @t", conn, tx);
            del.Parameters.AddWithValue("t", objectType);
            await del.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var up = new NpgsqlCommand(
                """
                INSERT INTO wf.template_pin (object_type, template_id, pinned_at, pinned_by) VALUES (@t, @id, @at, @by)
                ON CONFLICT (object_type) DO UPDATE SET template_id = @id, pinned_at = @at, pinned_by = @by
                """, conn, tx);
            up.Parameters.AddWithValue("t", objectType);
            up.Parameters.AddWithValue("id", templateId);
            up.Parameters.AddWithValue("at", now);
            up.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await up.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, WorkflowService.Entry(subject, "admin.workflow_pin", AuditOutcome.Success, ObjectType, objectType, correlationId,
            ("before", before?.TemplateId), ("after", templateId)), ct);
        await tx.CommitAsync(ct);
        return Outcome<TemplatePin?>.Ok(templateId is null ? null : new TemplatePin(objectType, templateId, now, subject.Username ?? subject.Id));
    }

    // ---------------------------------------------------------------- drafts (migration 0035)

    private static TemplateDraftInfo ReadDraft(NpgsqlDataReader r) =>
        new(r.GetString(0), r.GetInt32(1), r.GetString(2), new DateTimeOffset(r.GetDateTime(3), TimeSpan.Zero), r.GetString(4));

    // ASSUMPTION(A83): one unvalidated draft per template id; publishing deletes it in the publish transaction (WorkflowService.UploadTemplateAsync).
    public async Task<Outcome<IReadOnlyList<TemplateDraftInfo>>> ListDraftsAsync(Subject subject, CancellationToken ct = default)
    {
        if (await DenyAsync<IReadOnlyList<TemplateDraftInfo>>(subject, "admin.workflow_drafts_list", null, ct) is { } denied) return denied;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var list = new List<TemplateDraftInfo>();
        await using var cmd = new NpgsqlCommand("SELECT id, base_version, definition, updated_at, updated_by FROM wf.template_draft ORDER BY id", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadDraft(r));
        return Outcome<IReadOnlyList<TemplateDraftInfo>>.Ok(list);
    }

    public async Task<Outcome<TemplateDraftInfo>> SaveDraftAsync(Subject subject, string templateId, int baseVersion, string definitionJson, string? correlationId, CancellationToken ct = default)
    {
        if (await DenyAsync<TemplateDraftInfo>(subject, "admin.workflow_draft_save", correlationId, ct) is { } denied) return denied;
        var errors = new List<string>();
        if (!System.Text.RegularExpressions.Regex.IsMatch(templateId ?? "", "^[a-z][a-z0-9-]{0,62}$")) errors.Add("The template id must be lowercase letters, digits and '-', starting with a letter (max 63).");
        if (baseVersion < 0) errors.Add("The base version cannot be negative.");
        if (string.IsNullOrWhiteSpace(definitionJson) || definitionJson.Length > 1_000_000) errors.Add("The draft must be between 1 and 1,000,000 characters.");
        else
        {
            try { _ = JsonNode.Parse(definitionJson, documentOptions: new System.Text.Json.JsonDocumentOptions { MaxDepth = 32 }); }
            catch (System.Text.Json.JsonException e) { errors.Add($"The draft is not valid JSON: {e.Message}"); }
        }
        if (errors.Count > 0) return Outcome<TemplateDraftInfo>.Invalid(errors);

        var now = clock.GetUtcNow();
        var by = subject.Username ?? subject.Id;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var up = new NpgsqlCommand(
            """
            INSERT INTO wf.template_draft (id, base_version, definition, updated_at, updated_by) VALUES (@id, @bv, @def, @at, @by)
            ON CONFLICT (id) DO UPDATE SET base_version = @bv, definition = @def, updated_at = @at, updated_by = @by
            """, conn, tx))
        {
            up.Parameters.AddWithValue("id", templateId!);
            up.Parameters.AddWithValue("bv", baseVersion);
            up.Parameters.AddWithValue("def", definitionJson);
            up.Parameters.AddWithValue("at", now);
            up.Parameters.AddWithValue("by", by);
            await up.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, WorkflowService.Entry(subject, "admin.workflow_draft_save", AuditOutcome.Success, ObjectType, templateId, correlationId,
            ("baseVersion", baseVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return Outcome<TemplateDraftInfo>.Ok(new TemplateDraftInfo(templateId!, baseVersion, definitionJson, now, by));
    }

    public async Task<Outcome<bool>> DiscardDraftAsync(Subject subject, string templateId, string? correlationId, CancellationToken ct = default)
    {
        if (await DenyAsync<bool>(subject, "admin.workflow_draft_discard", correlationId, ct) is { } denied) return denied;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int removed;
        await using (var del = new NpgsqlCommand("DELETE FROM wf.template_draft WHERE id = @id", conn, tx))
        {
            del.Parameters.AddWithValue("id", templateId);
            removed = await del.ExecuteNonQueryAsync(ct);
        }
        if (removed == 0) return Outcome<bool>.NotFound();
        await audit.AppendAsync(tx, WorkflowService.Entry(subject, "admin.workflow_draft_discard", AuditOutcome.Success, ObjectType, templateId, correlationId), ct);
        await tx.CommitAsync(ct);
        return Outcome<bool>.Ok(true);
    }

    public async Task<Outcome<IReadOnlyList<SignatureMeaning>>> ListSignatureMeaningsAsync(Subject subject, CancellationToken ct = default)
    {
        if (await DenyAsync<IReadOnlyList<SignatureMeaning>>(subject, "admin.workflow_meanings_list", null, ct) is { } denied) return denied;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var codes = await WorkflowStore.LoadReasonCodesAsync(conn, null, ct);
        return Outcome<IReadOnlyList<SignatureMeaning>>.Ok([.. codes.Select(c => new SignatureMeaning(c.Code, c.Label, c.Decision, c.RequiresComment, c.AppliesTo))]);
    }
}
