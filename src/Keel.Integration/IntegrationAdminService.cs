using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Integration;

public sealed record DeliveryView(
    Guid Id, Guid EventId, long Seq, string EventType, string SinkName, string Status, int Attempts, DateTimeOffset NextAttemptAt,
    DateTimeOffset? LastAttemptAt, string? LastError, DateTimeOffset? DeliveredAt, IReadOnlyList<string> WithheldReasons, DateTimeOffset CreatedAt);

/// <summary>Administration of sinks, clearance profiles and deliveries (plm-admin only). Every change is audited in its own transaction with before/after values.</summary>
public sealed class IntegrationAdminService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, SinkRegistry sinks)
{
    private static readonly string[] Jurisdictions = ["None", "Ear", "Itar"];

    private async Task<Outcome<T>?> RequireAdminAsync<T>(Subject s, string action, string? corr, CancellationToken ct)
    {
        if (AccessPolicy.HasRoleFor(s, PlmAction.Administer)) return null;
        await audit.AppendAsync(IntegrationAudit.Entry(s, action, AuditOutcome.Denied, null, corr, ("reasons", AccessPolicy.RoleMissing)), ct);
        return Outcome<T>.Forbidden([AccessPolicy.RoleMissing]);
    }

    // ---------------------------------------------------------------- profiles

    public async Task<Outcome<IReadOnlyList<ProfileRecord>>> ListProfilesAsync(Subject s, CancellationToken ct = default)
    {
        if (await RequireAdminAsync<IReadOnlyList<ProfileRecord>>(s, "integration.profile.list", null, ct) is { } denied) return denied;
        return Outcome<IReadOnlyList<ProfileRecord>>.Ok(await LoadProfilesAsync(ct));
    }

    internal async Task<IReadOnlyList<ProfileRecord>> LoadProfilesAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {ProfileRecord.Columns} FROM integration.clearance_profile p ORDER BY p.name");
        var list = new List<ProfileRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ProfileRecord.Read(r));
        return list;
    }

    /// <summary>Profiles an exporter may pick as the recipient of a TDP (active ones; the profile data is configuration, not controlled data).</summary>
    public async Task<IReadOnlyList<ProfileRecord>> ActiveProfilesAsync(CancellationToken ct = default) => [.. (await LoadProfilesAsync(ct)).Where(p => p.Active)];

    private static List<string> Codes(IEnumerable<string>? values, bool upper) =>
        [.. (values ?? []).Select(v => v.Trim()).Where(v => v.Length > 0).Select(v => upper ? v.ToUpperInvariant() : v).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public async Task<Outcome<ProfileRecord>> SaveProfileAsync(Subject s, ProfileInput input, string? corr, CancellationToken ct = default)
    {
        if (await RequireAdminAsync<ProfileRecord>(s, "integration.profile.save", corr, ct) is { } denied) return denied;
        var errors = new List<string>();
        var name = input.Name.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z][a-z0-9-]{1,40}$")) errors.Add("The name must be 2 to 41 lower-case letters, digits or '-', starting with a letter.");
        if (string.IsNullOrWhiteSpace(input.Label) || input.Label.Trim().Length > 120) errors.Add("The label is required (at most 120 characters).");
        if (!Enum.TryParse<RecipientCategory>(input.RecipientCategory, out _)) errors.Add($"Unknown recipient category '{input.RecipientCategory}'.");
        var statements = Codes(input.AllowedStatements, true);
        foreach (var st in statements)
            if (!Enum.TryParse<DistributionStatement>(st, out _)) errors.Add($"Unknown distribution statement '{st}'.");
        var jurisdictions = Codes(input.AllowedJurisdictions, false);
        foreach (var j in jurisdictions)
            if (!Jurisdictions.Contains(j)) errors.Add($"Unknown export jurisdiction '{j}'.");
        if (errors.Count == 0)
        {
            var probe = new ProfileRecord(Guid.Empty, name, input.Label, "", input.RecipientCategory, input.UsPersonsOnly, jurisdictions, Codes(input.AllowedEccns, true),
                Codes(input.AllowedUsml, false), Codes(input.AllowedCui, true), Codes(input.AllowedPrograms, false), statements, Codes(input.ExportAuthorizations, true), input.Active, 1).ToProfile();
            errors.AddRange(probe.Validate());
        }
        if (errors.Count > 0) return Outcome<ProfileRecord>.Invalid(errors);

        var now = clock.GetUtcNow();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid? existing = null;
        await using (var find = new NpgsqlCommand("SELECT id FROM integration.clearance_profile WHERE name = @n FOR UPDATE", conn, tx))
        {
            find.Parameters.AddWithValue("n", name);
            existing = (Guid?)await find.ExecuteScalarAsync(ct);
        }
        var id = existing ?? Guid.CreateVersion7();
        await using (var cmd = new NpgsqlCommand(
            existing is null
                ? """
                  INSERT INTO integration.clearance_profile (id, name, label, description, recipient_category, us_persons_only, allowed_jurisdictions, allowed_eccns, allowed_usml,
                      allowed_cui, allowed_programs, allowed_statements, export_authorizations, active, created_at, created_by)
                  VALUES (@id, @n, @label, @desc, @cat, @us, @jur, @eccn, @usml, @cui, @prog, @stm, @auth, @active, @now, @by)
                  """
                : """
                  UPDATE integration.clearance_profile SET label = @label, description = @desc, recipient_category = @cat, us_persons_only = @us, allowed_jurisdictions = @jur,
                      allowed_eccns = @eccn, allowed_usml = @usml, allowed_cui = @cui, allowed_programs = @prog, allowed_statements = @stm, export_authorizations = @auth,
                      active = @active, revision = revision + 1, updated_at = @now, updated_by = @by WHERE id = @id
                  """, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("label", input.Label.Trim());
            cmd.Parameters.AddWithValue("desc", input.Description?.Trim() ?? "");
            cmd.Parameters.AddWithValue("cat", input.RecipientCategory);
            cmd.Parameters.AddWithValue("us", input.UsPersonsOnly);
            cmd.Parameters.AddWithValue("jur", jurisdictions.ToArray());
            cmd.Parameters.AddWithValue("eccn", Codes(input.AllowedEccns, true).ToArray());
            cmd.Parameters.AddWithValue("usml", Codes(input.AllowedUsml, false).ToArray());
            cmd.Parameters.AddWithValue("cui", Codes(input.AllowedCui, true).ToArray());
            cmd.Parameters.AddWithValue("prog", Codes(input.AllowedPrograms, false).ToArray());
            cmd.Parameters.AddWithValue("stm", statements.ToArray());
            cmd.Parameters.AddWithValue("auth", Codes(input.ExportAuthorizations, true).ToArray());
            cmd.Parameters.AddWithValue("active", input.Active);
            cmd.Parameters.AddWithValue("now", now);
            cmd.Parameters.AddWithValue("by", s.Username ?? s.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, IntegrationAudit.Entry(s, existing is null ? "integration.profile.create" : "integration.profile.update", AuditOutcome.Success, name, corr,
            ("category", input.RecipientCategory), ("usPersonsOnly", input.UsPersonsOnly.ToString()), ("jurisdictions", string.Join(",", jurisdictions)),
            ("statements", string.Join(",", statements)), ("cui", string.Join(",", Codes(input.AllowedCui, true))), ("programs", string.Join(",", Codes(input.AllowedPrograms, false))),
            ("authorizations", string.Join(",", Codes(input.ExportAuthorizations, true))), ("active", input.Active.ToString())), ct);
        ProfileRecord saved;
        await using (var load = new NpgsqlCommand($"SELECT {ProfileRecord.Columns} FROM integration.clearance_profile p WHERE p.id = @id", conn, tx))
        {
            load.Parameters.AddWithValue("id", id);
            await using var r = await load.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            saved = ProfileRecord.Read(r);
        }
        await tx.CommitAsync(ct);
        return Outcome<ProfileRecord>.Ok(saved);
    }

    // ---------------------------------------------------------------- sinks

    public async Task<Outcome<IReadOnlyList<SinkRecord>>> ListSinksAsync(Subject s, CancellationToken ct = default)
    {
        if (await RequireAdminAsync<IReadOnlyList<SinkRecord>>(s, "integration.sink.list", null, ct) is { } denied) return denied;
        await using var cmd = dataSource.CreateCommand($"SELECT {SinkRecord.Columns} FROM integration.sink s JOIN integration.clearance_profile p ON p.id = s.profile_id ORDER BY s.name");
        var list = new List<SinkRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(SinkRecord.Read(r));
        return Outcome<IReadOnlyList<SinkRecord>>.Ok(list);
    }

    /// <summary>Creates or updates a sink by name. The configuration is validated (URL allow-list, secret names, directory) and the messages returned. A broker sink throws NotSupportedException (Phase 4b).</summary>
    public async Task<Outcome<SinkRecord>> SaveSinkAsync(Subject s, SinkInput input, string? corr, CancellationToken ct = default)
    {
        if (await RequireAdminAsync<SinkRecord>(s, "integration.sink.save", corr, ct) is { } denied) return denied;
        var errors = new List<string>();
        var name = input.Name.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z][a-z0-9-]{1,40}$")) errors.Add("The name must be 2 to 41 lower-case letters, digits or '-', starting with a letter.");
        if (string.IsNullOrWhiteSpace(input.Label) || input.Label.Trim().Length > 120) errors.Add("The label is required (at most 120 characters).");
        if (!SinkKinds.All.Contains(input.Kind)) errors.Add($"Unknown sink kind '{input.Kind}'.");
        var types = Codes(input.EventTypes, false);
        foreach (var t in types)
            if (!EventTypes.Default.Contains(t)) errors.Add($"'{t}' is not an event type a sink can subscribe to; the full structure is the separate option.");
        if (errors.Count == 0) errors.AddRange(sinks.For(input.Kind).ValidateConfig(input.Config, input.Enabled)); // throws NotSupportedException for broker
        if (errors.Count > 0) return Outcome<SinkRecord>.Invalid(errors);

        var now = clock.GetUtcNow();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid? profileId;
        await using (var pc = new NpgsqlCommand("SELECT id FROM integration.clearance_profile WHERE name = @n", conn, tx))
        {
            pc.Parameters.AddWithValue("n", input.ProfileName);
            profileId = (Guid?)await pc.ExecuteScalarAsync(ct);
        }
        if (profileId is null) return Outcome<SinkRecord>.Invalid([$"Clearance profile '{input.ProfileName}' does not exist."]);
        Guid? existing;
        await using (var find = new NpgsqlCommand("SELECT id FROM integration.sink WHERE name = @n FOR UPDATE", conn, tx))
        {
            find.Parameters.AddWithValue("n", name);
            existing = (Guid?)await find.ExecuteScalarAsync(ct);
        }
        var id = existing ?? Guid.CreateVersion7();
        await using (var cmd = new NpgsqlCommand(
            existing is null
                ? "INSERT INTO integration.sink (id, name, label, kind, enabled, profile_id, config, event_types, full_structure, created_at, created_by) VALUES (@id, @n, @label, @kind, @en, @pid, @cfg, @types, @full, @now, @by)"
                : "UPDATE integration.sink SET label = @label, enabled = @en, profile_id = @pid, config = @cfg, event_types = @types, full_structure = @full, revision = revision + 1, updated_at = @now, updated_by = @by WHERE id = @id",
            conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("label", input.Label.Trim());
            cmd.Parameters.AddWithValue("kind", input.Kind);
            cmd.Parameters.AddWithValue("en", input.Enabled);
            cmd.Parameters.AddWithValue("pid", profileId.Value);
            cmd.Parameters.Add(new NpgsqlParameter("cfg", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = input.Config.ToJsonString() });
            cmd.Parameters.AddWithValue("types", types.ToArray());
            cmd.Parameters.AddWithValue("full", input.FullStructure);
            cmd.Parameters.AddWithValue("now", now);
            cmd.Parameters.AddWithValue("by", s.Username ?? s.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, IntegrationAudit.Entry(s, existing is null ? "integration.sink.create" : "integration.sink.update", AuditOutcome.Success, name, corr,
            ("kind", input.Kind), ("enabled", input.Enabled.ToString()), ("profile", input.ProfileName), ("config", input.Config.ToJsonString()),
            ("eventTypes", string.Join(",", types)), ("fullStructure", input.FullStructure.ToString())), ct);
        SinkRecord saved;
        await using (var load = new NpgsqlCommand($"SELECT {SinkRecord.Columns} FROM integration.sink s JOIN integration.clearance_profile p ON p.id = s.profile_id WHERE s.id = @id", conn, tx))
        {
            load.Parameters.AddWithValue("id", id);
            await using var r = await load.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            saved = SinkRecord.Read(r);
        }
        await tx.CommitAsync(ct);
        return Outcome<SinkRecord>.Ok(saved);
    }

    // ---------------------------------------------------------------- deliveries

    public async Task<Outcome<IReadOnlyList<DeliveryView>>> ListDeliveriesAsync(Subject s, string? status, string? sinkName, int limit, CancellationToken ct = default)
    {
        if (await RequireAdminAsync<IReadOnlyList<DeliveryView>>(s, "integration.delivery.list", null, ct) is { } denied) return denied;
        await using var cmd = dataSource.CreateCommand(
            """
            SELECT d.id, d.event_id, o.seq, o.event_type, s.name, d.status, d.attempts, d.next_attempt_at, d.last_attempt_at, d.last_error, d.delivered_at, d.withheld_reason, d.created_at
            FROM integration.delivery d JOIN integration.sink s ON s.id = d.sink_id
            JOIN integration.outbox o ON o.id = d.event_id
            WHERE (@status::text IS NULL OR d.status = @status) AND (@sink::text IS NULL OR s.name = @sink)
            ORDER BY o.seq DESC LIMIT @limit
            """);
        cmd.Parameters.Add(new NpgsqlParameter("status", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("sink", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)sinkName ?? DBNull.Value });
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        var list = new List<DeliveryView>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new DeliveryView(r.GetGuid(0), r.GetGuid(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6), r.GetFieldValue<DateTimeOffset>(7),
                r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10),
                r.IsDBNull(11) ? [] : (string[])r.GetValue(11), r.GetFieldValue<DateTimeOffset>(12)));
        return Outcome<IReadOnlyList<DeliveryView>>.Ok(list);
    }

    /// <summary>Puts dead letters back in the queue (attempts reset). One delivery, or every dead letter of a sink.</summary>
    public Task<Outcome<int>> RequeueDeadAsync(Subject s, Guid? deliveryId, string? sinkName, string? corr, CancellationToken ct = default) =>
        MoveAsync(s, "integration.delivery.requeue", DeliveryStatus.Dead, "status = 'pending', attempts = 0, last_error = NULL", deliveryId, sinkName, corr, ct);

    /// <summary>
    /// Sends withheld deliveries back to the queue after a profile changed. The dispatcher evaluates them again before anything is sent, so an event
    /// the profile still does not clear is withheld again. Nothing leaves a withheld state except through this action.
    /// </summary>
    public Task<Outcome<int>> ReevaluateWithheldAsync(Subject s, Guid? deliveryId, string? sinkName, string? corr, CancellationToken ct = default) =>
        MoveAsync(s, "integration.delivery.reevaluate", DeliveryStatus.Withheld, "status = 'pending', withheld_reason = NULL, attempts = 0", deliveryId, sinkName, corr, ct);

    private async Task<Outcome<int>> MoveAsync(Subject s, string action, string from, string set, Guid? deliveryId, string? sinkName, string? corr, CancellationToken ct)
    {
        if (await RequireAdminAsync<int>(s, action, corr, ct) is { } denied) return denied;
        if (deliveryId is null && sinkName is null) return Outcome<int>.Invalid(["Name a delivery or a sink."]);
        var now = clock.GetUtcNow();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int count;
        await using (var cmd = new NpgsqlCommand(
            $"""
            UPDATE integration.delivery d SET {set}, next_attempt_at = @now, updated_at = @now, claimed_by = NULL, claimed_until = NULL
            FROM integration.sink s
            WHERE s.id = d.sink_id AND d.status = @from AND (@id::uuid IS NULL OR d.id = @id) AND (@sink::text IS NULL OR s.name = @sink)
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("now", now);
            cmd.Parameters.AddWithValue("from", from);
            cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)deliveryId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("sink", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)sinkName ?? DBNull.Value });
            count = await cmd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, IntegrationAudit.Entry(s, action, AuditOutcome.Success, deliveryId?.ToString("D") ?? sinkName, corr,
            ("count", count.ToString(CultureInfo.InvariantCulture)), ("sink", sinkName)), ct);
        await tx.CommitAsync(ct);
        return Outcome<int>.Ok(count);
    }

    // ---------------------------------------------------------------- TDP category mapping

    public async Task<Outcome<IReadOnlyList<TdpCategoryRule>>> ListCategoryRulesAsync(Subject s, CancellationToken ct = default)
    {
        if (await RequireAdminAsync<IReadOnlyList<TdpCategoryRule>>(s, "integration.tdp_category.list", null, ct) is { } denied) return denied;
        return Outcome<IReadOnlyList<TdpCategoryRule>>.Ok(await TdpCategories.LoadAsync(dataSource, ct));
    }

    public async Task<Outcome<TdpCategoryRule>> SaveCategoryRuleAsync(Subject s, string documentType, string? linkType, string category, string? corr, CancellationToken ct = default)
    {
        if (await RequireAdminAsync<TdpCategoryRule>(s, "integration.tdp_category.save", corr, ct) is { } denied) return denied;
        var rule = new TdpCategoryRule(documentType.Trim(), string.IsNullOrWhiteSpace(linkType) ? "*" : linkType.Trim(), category.Trim().ToUpperInvariant());
        var errors = new List<string>();
        if (rule.DocumentType.Length is < 1 or > 60) errors.Add("The document type is required.");
        if (!TdpCategories.All.Contains(rule.Category)) errors.Add($"Unknown TDP element category '{rule.Category}'.");
        if (errors.Count > 0) return Outcome<TdpCategoryRule>.Invalid(errors);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO integration.tdp_category (document_type, link_type, category, updated_at, updated_by) VALUES (@t, @l, @c, @now, @by)
            ON CONFLICT (document_type, link_type) DO UPDATE SET category = @c, updated_at = @now, updated_by = @by
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("t", rule.DocumentType);
            cmd.Parameters.AddWithValue("l", rule.LinkType);
            cmd.Parameters.AddWithValue("c", rule.Category);
            cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
            cmd.Parameters.AddWithValue("by", s.Username ?? s.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, IntegrationAudit.Entry(s, "integration.tdp_category.save", AuditOutcome.Success, rule.DocumentType, corr, ("linkType", rule.LinkType), ("category", rule.Category)), ct);
        await tx.CommitAsync(ct);
        return Outcome<TdpCategoryRule>.Ok(rule);
    }

    public async Task<Outcome<bool>> DeleteCategoryRuleAsync(Subject s, string documentType, string linkType, string? corr, CancellationToken ct = default)
    {
        if (await RequireAdminAsync<bool>(s, "integration.tdp_category.delete", corr, ct) is { } denied) return denied;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("DELETE FROM integration.tdp_category WHERE document_type = @t AND link_type = @l", conn, tx))
        {
            cmd.Parameters.AddWithValue("t", documentType);
            cmd.Parameters.AddWithValue("l", linkType);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, IntegrationAudit.Entry(s, "integration.tdp_category.delete", AuditOutcome.Success, documentType, corr, ("linkType", linkType)), ct);
        await tx.CommitAsync(ct);
        return Outcome<bool>.Ok(true);
    }
}
