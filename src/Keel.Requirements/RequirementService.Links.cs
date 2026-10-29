using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;
using static Keel.Requirements.RequirementStore;

namespace Keel.Requirements;

/// <summary>A link row with its other end resolved under the caller's access. <see cref="Visible"/> is false when the other end is not readable: then nothing about it is set.</summary>
internal sealed record RawLink(
    Guid Id, Guid SourceId, string TypeCode, string Label, string ReverseLabel, string Role, string TargetKind, string? TargetNumber, Guid? TargetReqId, string Status, string? SuspectReason,
    string? ReviewedRevision, string ReviewedSourceSha, string? ReviewedTargetSha, DateTimeOffset CreatedAt, string CreatedBy, string? ReviewedBy, DateTimeOffset? ReviewedAt,
    bool Visible, string? OtherNumber, string? OtherKey, string? OtherName, string? OtherRevision, string? OtherSha, Markings? OtherMarkings, string? OtherSpec = null)
{
    /// <summary>The reason this link is suspect now: the stored one, else drift visible from here (the other end moved on, or the source differs from what was reviewed).</summary>
    public string? EffectiveSuspect(string? sourceSha)
    {
        if (Status == "suspect") return SuspectReason;
        if (sourceSha is not null && sourceSha != ReviewedSourceSha) return SuspectReasons.SourceChanged;
        if (!Visible) return null;
        if (TargetKind == LinkTargetKinds.Requirement && OtherSha is not null && OtherSha != ReviewedTargetSha) return SuspectReasons.TargetChanged;
        if (TargetKind is LinkTargetKinds.Part or LinkTargetKinds.Document && OtherRevision is not null && OtherRevision != ReviewedRevision) return SuspectReasons.TargetRevised;
        return null;
    }
}

public sealed partial class RequirementService
{
    private static readonly Regex CodePattern = new("^[a-z][a-z0-9-]{1,39}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ---------------------------------------------------------------- link types (administrator data)

    public async Task<IReadOnlyList<LinkTypeInfo>> ListLinkTypesAsync(bool includeRetired = false, CancellationToken ct = default)
    {
        var list = new List<LinkTypeInfo>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT code, label, reverse_label, target_kind, coverage_role, is_system, is_active, sort_order FROM req.link_type WHERE is_active OR @all ORDER BY sort_order, code", conn);
        cmd.Parameters.AddWithValue("all", includeRetired);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new LinkTypeInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetBoolean(5), r.GetBoolean(6), r.GetInt32(7)));
        return list;
    }

    /// <summary>
    /// Creates or updates a link type (administrators). The five system types cannot be retired or redefined; a type keeps its code and target kind; a type is retired, never
    /// deleted, so existing links stay valid. Retiring or restoring needs a reason. An allocation role needs a part target and a verification role a document target.
    /// </summary>
    public async Task<Outcome<LinkTypeInfo>> SaveLinkTypeAsync(Subject subject, SaveLinkTypeCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        var code = cmd.Code?.Trim() ?? "";
        var errors = new List<string>();
        if (!CodePattern.IsMatch(code)) errors.Add("A link type code starts with a lowercase letter and has 2 to 40 lowercase letters, digits or '-'.");
        if (string.IsNullOrWhiteSpace(cmd.Label) || cmd.Label.Trim().Length > 60) errors.Add("The label is required (at most 60 characters).");
        if (string.IsNullOrWhiteSpace(cmd.ReverseLabel) || cmd.ReverseLabel.Trim().Length > 60) errors.Add("The reverse label is required (at most 60 characters).");
        if (!LinkTargetKinds.All.Contains(cmd.TargetKind)) errors.Add($"The target kind must be one of: {string.Join(", ", LinkTargetKinds.All)}.");
        if (!CoverageRoles.All.Contains(cmd.CoverageRole)) errors.Add($"The coverage role must be one of: {string.Join(", ", CoverageRoles.All)}.");
        if (cmd.CoverageRole == CoverageRoles.Allocation && cmd.TargetKind != LinkTargetKinds.Part) errors.Add("An allocation link points at a part.");
        if (cmd.CoverageRole == CoverageRoles.Verification && cmd.TargetKind != LinkTargetKinds.Document) errors.Add("A verification link points at a document.");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Administer))
        {
            await audit.AppendAsync(tx, Entry(subject, "requirement.link_type_save", AuditOutcome.Denied, null, correlationId, ("code", code), ("reasons", AccessPolicy.RoleMissing)), ct);
            await tx.CommitAsync(ct);
            return Outcome<LinkTypeInfo>.Forbidden([AccessPolicy.RoleMissing]);
        }
        if (errors.Count > 0) return Outcome<LinkTypeInfo>.Invalid(errors);
        var before = await LoadLinkTypeAsync(conn, tx, code, ct);
        if (before is not null)
        {
            if (before.IsSystem && (!cmd.IsActive || before.TargetKind != cmd.TargetKind || before.CoverageRole != cmd.CoverageRole)) return Outcome<LinkTypeInfo>.Invalid([$"The system link type {code} cannot be retired or redefined."]);
            if (before.TargetKind != cmd.TargetKind) return Outcome<LinkTypeInfo>.Invalid(["A link type keeps its target kind."]);
            if (before.IsActive != cmd.IsActive && string.IsNullOrWhiteSpace(cmd.Reason)) return Outcome<LinkTypeInfo>.Invalid([$"A reason is required to {(cmd.IsActive ? "restore" : "retire")} a link type."]);
        }
        if ((cmd.Reason?.Length ?? 0) > 1000) return Outcome<LinkTypeInfo>.Invalid(["The reason is at most 1000 characters."]);
        await using (var up = new NpgsqlCommand(
            """
            INSERT INTO req.link_type (code, label, reverse_label, target_kind, coverage_role, is_active, sort_order, updated_at, updated_by) VALUES (@c, @l, @rl, @tk, @role, @a, @o, @at, @by)
            ON CONFLICT (code) DO UPDATE SET label = @l, reverse_label = @rl, coverage_role = @role, is_active = @a, sort_order = @o, updated_at = @at, updated_by = @by
            """, conn, tx))
        {
            up.Parameters.AddWithValue("c", code);
            up.Parameters.AddWithValue("l", cmd.Label.Trim());
            up.Parameters.AddWithValue("rl", cmd.ReverseLabel.Trim());
            up.Parameters.AddWithValue("tk", cmd.TargetKind);
            up.Parameters.AddWithValue("role", cmd.CoverageRole);
            up.Parameters.AddWithValue("a", cmd.IsActive);
            up.Parameters.AddWithValue("o", cmd.SortOrder);
            up.Parameters.AddWithValue("at", clock.GetUtcNow());
            up.Parameters.AddWithValue("by", Who(subject));
            await up.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "requirement.link_type_save", AuditOutcome.Success, null, correlationId, ("code", code), ("new", before is null ? "true" : "false"),
            ("activeBefore", before?.IsActive.ToString()), ("active", cmd.IsActive.ToString()), ("role", cmd.CoverageRole), ("targetKind", cmd.TargetKind), ("reason", cmd.Reason?.Trim())), ct);
        var saved = (await LoadLinkTypeAsync(conn, tx, code, ct))!;
        await tx.CommitAsync(ct);
        return Outcome<LinkTypeInfo>.Ok(saved);
    }

    // ---------------------------------------------------------------- add, remove, review

    /// <summary>The requirement of a specification's latest iteration as the caller may change it; null when it does not exist or is not readable.</summary>
    private static async Task<WorkReq?> FindReadableAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, SpecHead head, string key, CancellationToken ct)
    {
        var w = (await LoadWorkAsync(conn, tx, head.IterationId, ct)).FirstOrDefault(x => x.Key == key);
        return w is not null && AccessPolicy.Evaluate(subject, w.Markings, PlmAction.Read).Permitted ? w : null;
    }

    public Task<Outcome<LinkView>> AddLinkAsync(Subject subject, string number, string key, AddLinkCommand cmd, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<LinkView>(subject, number, "requirement.link_add", correlationId, async (head, conn, tx) =>
        {
            var source = await FindReadableAsync(conn, tx, subject, head, key, ct);
            if (source is null) return Step<LinkView>.Invalid($"Requirement {key} was not found.");
            if (!AccessPolicy.Evaluate(subject, source.Markings, PlmAction.Modify).Permitted) return Step<LinkView>.Hidden($"You are not authorized to change {key}.", "requirement.modify");
            var type = await LoadLinkTypeAsync(conn, tx, cmd.LinkType ?? "", ct);
            if (type is null || !type.IsActive) return Step<LinkView>.Invalid($"'{cmd.LinkType}' is not an active link type.");
            var now = clock.GetUtcNow();
            Guid? targetReq = null;
            string? targetNumber = null, revision = null, targetSha = null;
            string name, banner, otherNumber;
            Markings otherMarkings;
            string? otherKey = null;
            if (type.TargetKind == LinkTargetKinds.Requirement)
            {
                var specNumber = string.IsNullOrWhiteSpace(cmd.TargetSpec) ? head.Number : cmd.TargetSpec.Trim();
                var targetHead = specNumber == head.Number ? head : await LoadSpecAsync(conn, tx, specNumber, null, false, ct);
                if (targetHead is null || !AccessPolicy.Evaluate(subject, targetHead.Markings, PlmAction.Read).Permitted) return Step<LinkView>.Hidden($"Requirement {cmd.TargetKey} of {specNumber} was not found, or you are not authorized to see it.", "target.not_readable");
                var target = await FindReadableAsync(conn, tx, subject, targetHead, cmd.TargetKey ?? "", ct);
                if (target is null) return Step<LinkView>.Hidden($"Requirement {cmd.TargetKey} of {specNumber} was not found, or you are not authorized to see it.", "target.not_readable");
                if (target.Id == source.Id) return Step<LinkView>.Invalid("A requirement cannot be linked to itself.");
                if (await ReachesAsync(conn, tx, type.Code, target.Id, source.Id, ct)) return Step<LinkView>.Invalid($"This link would make {key} depend on itself through '{type.Label}' links.");
                targetReq = target.Id; targetSha = target.Sha; name = target.Title; otherMarkings = target.Markings; banner = target.Markings.BannerText(); otherNumber = specNumber; otherKey = target.Key;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(cmd.TargetNumber)) return Step<LinkView>.Invalid($"Name the {type.TargetKind} to link to.");
                var target = await LoadTargetAsync(conn, tx, type.TargetKind, cmd.TargetNumber.Trim(), ct);
                if (target is null || !AccessPolicy.Evaluate(subject, target.Markings, PlmAction.Read).Permitted)
                    return Step<LinkView>.Hidden($"The {type.TargetKind} '{cmd.TargetNumber}' was not found, or you are not authorized to see it.", "target.not_readable");
                targetNumber = target.Number; revision = target.Revision; name = target.Name; otherMarkings = target.Markings; banner = target.Markings.BannerText(); otherNumber = target.Number;
            }
            var duplicate = new NpgsqlCommand(
                "SELECT 1 FROM req.link l WHERE l.source_requirement_id = @s AND l.link_type = @t AND l.removed_at IS NULL AND l.target_requirement_id IS NOT DISTINCT FROM @tr AND l.target_number IS NOT DISTINCT FROM @tn", conn, tx);
            duplicate.Parameters.AddWithValue("s", source.Id);
            duplicate.Parameters.AddWithValue("t", type.Code);
            duplicate.Parameters.Add(new NpgsqlParameter("tr", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)targetReq ?? DBNull.Value });
            duplicate.Parameters.Add(new NpgsqlParameter("tn", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)targetNumber ?? DBNull.Value });
            await using (duplicate)
                if (await duplicate.ExecuteScalarAsync(ct) is not null) return Step<LinkView>.Conflict($"{key} already has this '{type.Label}' link.");
            var id = Guid.CreateVersion7();
            await using (var ins = new NpgsqlCommand(
                """
                INSERT INTO req.link (id, link_type, source_requirement_id, target_kind, target_requirement_id, target_number, reviewed_target_revision, reviewed_source_sha, reviewed_target_sha, created_at, created_by)
                VALUES (@id, @t, @s, @tk, @tr, @tn, @rev, @ssha, @tsha, @at, @by)
                """, conn, tx))
            {
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("t", type.Code);
                ins.Parameters.AddWithValue("s", source.Id);
                ins.Parameters.AddWithValue("tk", type.TargetKind);
                ins.Parameters.Add(new NpgsqlParameter("tr", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)targetReq ?? DBNull.Value });
                ins.Parameters.Add(new NpgsqlParameter("tn", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)targetNumber ?? DBNull.Value });
                ins.Parameters.Add(new NpgsqlParameter("rev", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)revision ?? DBNull.Value });
                ins.Parameters.AddWithValue("ssha", source.Sha);
                ins.Parameters.Add(new NpgsqlParameter("tsha", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)targetSha ?? DBNull.Value });
                ins.Parameters.AddWithValue("at", now);
                ins.Parameters.AddWithValue("by", Who(subject));
                await ins.ExecuteNonQueryAsync(ct);
            }
            var view = new LinkView(id, false, type.Code, type.Label, "outgoing", type.TargetKind, otherNumber, otherKey, name, revision, false, null, banner, otherMarkings, now, Who(subject), null, null);
            return Step<LinkView>.Done(view, ("key", key), ("linkType", type.Code), ("targetKind", type.TargetKind), ("target", otherKey is null ? otherNumber : $"{otherNumber}/{otherKey}"));
        }, ct);

    /// <summary>True when <paramref name="from"/> already reaches <paramref name="to"/> through links of this type (system view; used only to refuse a cycle).</summary>
    private static async Task<bool> ReachesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string linkType, Guid from, Guid to, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            WITH RECURSIVE walk(id) AS (
                SELECT @from::uuid
                UNION
                SELECT l.target_requirement_id FROM req.link l JOIN walk w ON l.source_requirement_id = w.id
                WHERE l.link_type = @t AND l.removed_at IS NULL AND l.target_requirement_id IS NOT NULL
            )
            SELECT 1 FROM walk WHERE id = @to LIMIT 1
            """, conn, tx);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("t", linkType);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<(Guid Source, string Key)?> LinkSourceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SpecHead head, Guid linkId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT q.id, q.key FROM req.link l JOIN req.requirement q ON q.id = l.source_requirement_id WHERE l.id = @id AND l.removed_at IS NULL AND q.spec_id = @s", conn, tx);
        cmd.Parameters.AddWithValue("id", linkId);
        cmd.Parameters.AddWithValue("s", head.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetGuid(0), r.GetString(1)) : null;
    }

    public Task<Outcome<bool>> RemoveLinkAsync(Subject subject, string number, Guid linkId, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<bool>(subject, number, "requirement.link_remove", correlationId, async (head, conn, tx) =>
        {
            var src = await LinkSourceAsync(conn, tx, head, linkId, ct);
            if (src is null) return Step<bool>.Missing();
            var w = await FindReadableAsync(conn, tx, subject, head, src.Value.Key, ct);
            if (w is null) return Step<bool>.Missing();
            if (!AccessPolicy.Evaluate(subject, w.Markings, PlmAction.Modify).Permitted) return Step<bool>.Hidden($"You are not authorized to change {w.Key}.", "requirement.modify");
            await using var up = new NpgsqlCommand("UPDATE req.link SET removed_at = @at, removed_by = @by WHERE id = @id AND removed_at IS NULL", conn, tx);
            up.Parameters.AddWithValue("at", clock.GetUtcNow());
            up.Parameters.AddWithValue("by", Who(subject));
            up.Parameters.AddWithValue("id", linkId);
            await up.ExecuteNonQueryAsync(ct);
            return Step<bool>.Done(true, ("key", w.Key), ("link", linkId.ToString("D")));
        }, ct);

    /// <summary>Marks a suspect link as reviewed: it is current again (the requirement's content and the other end's revision are recorded as reviewed). Audited with who and what.</summary>
    public Task<Outcome<LinkView>> ReviewLinkAsync(Subject subject, string number, Guid linkId, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<LinkView>(subject, number, "requirement.link_review", correlationId, async (head, conn, tx) =>
        {
            var src = await LinkSourceAsync(conn, tx, head, linkId, ct);
            if (src is null) return Step<LinkView>.Missing();
            var w = await FindReadableAsync(conn, tx, subject, head, src.Value.Key, ct);
            if (w is null) return Step<LinkView>.Missing();
            if (!AccessPolicy.Evaluate(subject, w.Markings, PlmAction.Modify).Permitted) return Step<LinkView>.Hidden($"You are not authorized to change {w.Key}.", "requirement.modify");
            var links = await LoadLinksAsync(conn, tx, subject, [w.Id], false, ct);
            var link = links.FirstOrDefault(l => l.Id == linkId);
            if (link is null || !link.Visible) return Step<LinkView>.Hidden("The other end of this link is not available to you, so you cannot review it.", "target.not_readable");
            var reason = link.EffectiveSuspect(w.Sha);
            if (reason is null) return Step<LinkView>.Conflict("This link is not suspect.");
            var now = clock.GetUtcNow();
            await using (var up = new NpgsqlCommand(
                """
                UPDATE req.link SET status = 'ok', suspect_reason = NULL, suspect_at = NULL, reviewed_source_sha = @ssha, reviewed_target_sha = @tsha, reviewed_target_revision = @rev, reviewed_at = @at, reviewed_by = @by
                WHERE id = @id AND removed_at IS NULL
                """, conn, tx))
            {
                up.Parameters.AddWithValue("ssha", w.Sha);
                up.Parameters.Add(new NpgsqlParameter("tsha", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)(link.TargetKind == LinkTargetKinds.Requirement ? link.OtherSha : null) ?? DBNull.Value });
                up.Parameters.Add(new NpgsqlParameter("rev", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)(link.TargetKind is LinkTargetKinds.Part or LinkTargetKinds.Document ? link.OtherRevision : null) ?? DBNull.Value });
                up.Parameters.AddWithValue("at", now);
                up.Parameters.AddWithValue("by", Who(subject));
                up.Parameters.AddWithValue("id", linkId);
                await up.ExecuteNonQueryAsync(ct);
            }
            var view = new LinkView(link.Id, false, link.TypeCode, link.Label, "outgoing", link.TargetKind, link.OtherNumber, link.OtherKey, link.OtherName, link.OtherRevision, false, null,
                link.OtherMarkings?.BannerText(), link.OtherMarkings, link.CreatedAt, link.CreatedBy, Who(subject), now);
            return Step<LinkView>.Done(view, ("key", w.Key), ("link", linkId.ToString("D")), ("linkType", link.TypeCode), ("wasSuspect", reason));
        }, ct);

    // ---------------------------------------------------------------- reading links

    /// <summary>Outgoing links of the given requirements with the other end resolved through the caller's read filters (an unreadable end is a placeholder).</summary>
    internal static async Task<List<RawLink>> LoadLinksAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, IReadOnlyCollection<Guid> sourceIds, bool incoming, CancellationToken ct)
    {
        var result = new List<RawLink>();
        const string baseCols =
            "l.id, l.source_requirement_id, l.link_type, lt.label, lt.reverse_label, lt.coverage_role, l.target_kind, l.target_number, l.target_requirement_id, l.status, l.suspect_reason, " +
            "l.reviewed_target_revision, l.reviewed_source_sha, l.reviewed_target_sha, l.created_at, l.created_by, l.reviewed_by, l.reviewed_at";
        RawLink Base(NpgsqlDataReader r, bool visible, string? number, string? key, string? name, string? revision, string? sha, Markings? m, string? spec = null) => new(
            r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetGuid(8),
            r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13),
            r.GetFieldValue<DateTimeOffset>(14), r.GetString(15), r.IsDBNull(16) ? null : r.GetString(16), r.IsDBNull(17) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(17),
            visible, number, key, name, revision, sha, m, spec);

        if (incoming)
        {
            // Links from other requirements to the one given: the other end is the source requirement.
            var srFilter = AccessPolicy.BuildReadFilter(subject, "sr");
            await using var cmd = new NpgsqlCommand(
                $"""
                SELECT {baseCols}, sq.key, sp.number, sr.title, sr.content_sha256, {MarkingColumns.Select("sr")}
                FROM req.link l
                JOIN req.link_type lt ON lt.code = l.link_type
                JOIN req.requirement sq ON sq.id = l.source_requirement_id
                JOIN req.spec sp ON sp.id = sq.spec_id
                LEFT JOIN req.current_requirement sr ON sr.requirement_id = l.source_requirement_id AND {srFilter.Sql}
                WHERE l.target_requirement_id = ANY(@ids) AND l.removed_at IS NULL
                ORDER BY l.created_at, l.id
                """, conn, tx);
            cmd.Parameters.AddWithValue("ids", sourceIds.ToArray());
            foreach (var p in srFilter.Parameters) cmd.Parameters.AddWithValue(p.Key, p.Value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var ok = !r.IsDBNull(20);
                // For an incoming link "Id"/"SourceId" keep the link's own columns; the requirement it points at is TargetReqId.
                result.Add(Base(r, ok, ok ? r.GetString(19) : null, ok ? r.GetString(18) : null, ok ? r.GetString(20) : null, null, ok ? r.GetString(21) : null, ok ? MarkingColumns.Read(r, 22) : null, ok ? r.GetString(19) : null));
            }
            return result;
        }

        var tmFilter = AccessPolicy.BuildReadFilter(subject, "tm");
        var trFilter = AccessPolicy.BuildReadFilter(subject, "tr");
        async Task Run(string sql, IEnumerable<KeyValuePair<string, object>> filterParams, Func<NpgsqlDataReader, RawLink> read)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("ids", sourceIds.ToArray());
            foreach (var p in filterParams) cmd.Parameters.AddWithValue(p.Key, p.Value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) result.Add(read(r));
        }
        await Run(
            $"""
            SELECT {baseCols}, tm.number, tm.name, (SELECT x.revision FROM plm.part_revision x WHERE x.master_id = tm.id AND x.is_latest), {MarkingColumns.Select("tm")}
            FROM req.link l JOIN req.link_type lt ON lt.code = l.link_type
            LEFT JOIN plm.part_master tm ON tm.number = l.target_number AND {tmFilter.Sql}
            WHERE l.source_requirement_id = ANY(@ids) AND l.removed_at IS NULL AND l.target_kind = 'part'
            """,
            tmFilter.Parameters,
            r => r.IsDBNull(18) ? Base(r, false, null, null, null, null, null, null) : Base(r, true, r.GetString(18), null, r.GetString(19), r.IsDBNull(20) ? null : r.GetString(20), null, MarkingColumns.Read(r, 21)));
        await Run(
            $"""
            SELECT {baseCols}, tm.number, tm.name, (SELECT x.revision FROM plm.document_revision x WHERE x.master_id = tm.id AND x.is_latest), {MarkingColumns.Select("tm")}
            FROM req.link l JOIN req.link_type lt ON lt.code = l.link_type
            LEFT JOIN plm.document_master tm ON tm.number = l.target_number AND {tmFilter.Sql}
            WHERE l.source_requirement_id = ANY(@ids) AND l.removed_at IS NULL AND l.target_kind = 'document'
            """,
            tmFilter.Parameters,
            r => r.IsDBNull(18) ? Base(r, false, null, null, null, null, null, null) : Base(r, true, r.GetString(18), null, r.GetString(19), r.IsDBNull(20) ? null : r.GetString(20), null, MarkingColumns.Read(r, 21)));
        await Run(
            $"""
            SELECT {baseCols}, tm.number, tm.title, {MarkingColumns.Select("tm")}
            FROM req.link l JOIN req.link_type lt ON lt.code = l.link_type
            LEFT JOIN chg.change tm ON tm.number = l.target_number AND {tmFilter.Sql}
            WHERE l.source_requirement_id = ANY(@ids) AND l.removed_at IS NULL AND l.target_kind = 'change'
            """,
            tmFilter.Parameters,
            r => r.IsDBNull(18) ? Base(r, false, null, null, null, null, null, null) : Base(r, true, r.GetString(18), null, r.GetString(19), null, null, MarkingColumns.Read(r, 20)));
        await Run(
            $"""
            SELECT {baseCols}, tq.key, ts.number, tr.title, tr.content_sha256, {MarkingColumns.Select("tr")}
            FROM req.link l JOIN req.link_type lt ON lt.code = l.link_type
            LEFT JOIN req.current_requirement tr ON tr.requirement_id = l.target_requirement_id AND {trFilter.Sql}
            LEFT JOIN req.requirement tq ON tq.id = tr.requirement_id
            LEFT JOIN req.spec ts ON ts.id = tr.spec_id
            WHERE l.source_requirement_id = ANY(@ids) AND l.removed_at IS NULL AND l.target_kind = 'requirement'
            """,
            trFilter.Parameters,
            r => r.IsDBNull(18) ? Base(r, false, null, null, null, null, null, null) : Base(r, true, r.GetString(19), r.GetString(18), r.GetString(20), null, r.GetString(21), MarkingColumns.Read(r, 22), r.GetString(19)));
        return result;
    }

    /// <summary>The links of one requirement, both directions, with restricted ends as placeholders. Needs the requirement to be readable.</summary>
    public Task<Outcome<RequirementLinks>> GetLinksAsync(Subject subject, string number, string key, string? correlationId, CancellationToken ct = default) =>
        ReadAsync<RequirementLinks>(subject, number, null, "requirement.links", correlationId, async (conn, tx, head) =>
        {
            var w = await FindReadableAsync(conn, tx, subject, head, key, ct);
            if (w is null) return (Outcome<RequirementLinks>.NotFound(), []);
            var views = new List<LinkView>();
            foreach (var l in await LoadLinksAsync(conn, tx, subject, [w.Id], false, ct))
                views.Add(ToView(l, "outgoing", l.Label, w.Sha));
            foreach (var l in await LoadLinksAsync(conn, tx, subject, [w.Id], true, ct))
                views.Add(ToView(l, "incoming", l.ReverseLabel, null));
            var shown = views.Where(v => v.Markings is not null).Select(v => v.Markings!).Append(w.Markings).Append(head.Markings);
            var set = new RequirementLinks(head.Number, key, views.OrderBy(v => v.Direction, StringComparer.Ordinal).ThenBy(v => v.Label, StringComparer.Ordinal).ToList(), Markings.AggregateBanner(shown));
            return (Outcome<RequirementLinks>.Ok(set), [("key", key), ("links", Fmt(views.Count)), ("restricted", Fmt(views.Count(v => v.Redacted)))]);
        }, ct);

    internal static LinkView ToView(RawLink l, string direction, string label, string? sourceSha)
    {
        if (!l.Visible) return new LinkView(l.Id, true, null, null, direction, null, null, null, null, null, false, null, null, null, l.CreatedAt, null, null, null);
        var reason = l.EffectiveSuspect(sourceSha);
        var kind = direction == "incoming" ? LinkTargetKinds.Requirement : l.TargetKind;
        return new LinkView(l.Id, false, l.TypeCode, label, direction, kind, l.OtherNumber, l.OtherKey, l.OtherName, l.OtherRevision, reason is not null, reason, l.OtherMarkings?.BannerText(), l.OtherMarkings,
            l.CreatedAt, l.CreatedBy, l.ReviewedBy, l.ReviewedAt);
    }

    // ---------------------------------------------------------------- suspect marking

    /// <summary>
    /// Marks the links of requirements whose content just changed as suspect, in the transaction of the edit: links FROM them (reason: the requirement changed) and links TO them
    /// (reason: the linked requirement changed). One audit event per link; the event names the link type and the ends by number and key, never any text.
    /// </summary>
    private async Task<int> MarkChangedRequirementsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, SpecHead head, IReadOnlyList<WorkReq> changed, DateTimeOffset now, CancellationToken ct)
    {
        var count = 0;
        foreach (var w in changed)
        {
            await using (var cmd = new NpgsqlCommand(
                """
                UPDATE req.link l SET status = 'suspect', suspect_reason = 'source_changed', suspect_at = @at
                WHERE l.source_requirement_id = @id AND l.removed_at IS NULL AND l.status = 'ok' AND l.reviewed_source_sha <> @sha
                RETURNING l.id, l.link_type, l.target_kind
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("at", now);
                cmd.Parameters.AddWithValue("id", w.Id);
                cmd.Parameters.AddWithValue("sha", w.Sha);
                var hit = new List<(Guid, string, string)>();
                await using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) hit.Add((r.GetGuid(0), r.GetString(1), r.GetString(2)));
                foreach (var (id, type, kind) in hit)
                    await audit.AppendAsync(tx, Entry(subject, "requirement.link_suspect", AuditOutcome.Success, head.Number, null, ("key", w.Key), ("link", id.ToString("D")), ("linkType", type), ("targetKind", kind), ("reason", SuspectReasons.SourceChanged)), ct);
                count += hit.Count;
            }
            await using (var cmd = new NpgsqlCommand(
                """
                UPDATE req.link l SET status = 'suspect', suspect_reason = 'target_changed', suspect_at = @at
                WHERE l.target_requirement_id = @id AND l.removed_at IS NULL AND l.status = 'ok' AND l.reviewed_target_sha IS DISTINCT FROM @sha
                RETURNING l.id, l.link_type, (SELECT q.key FROM req.requirement q WHERE q.id = l.source_requirement_id), (SELECT s.number FROM req.requirement q JOIN req.spec s ON s.id = q.spec_id WHERE q.id = l.source_requirement_id)
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("at", now);
                cmd.Parameters.AddWithValue("id", w.Id);
                cmd.Parameters.AddWithValue("sha", w.Sha);
                var hit = new List<(Guid, string, string, string)>();
                await using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) hit.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3)));
                foreach (var (id, type, srcKey, srcSpec) in hit)
                    await audit.AppendAsync(tx, Entry(subject, "requirement.link_suspect", AuditOutcome.Success, srcSpec, null, ("key", srcKey), ("link", id.ToString("D")), ("linkType", type), ("targetKind", LinkTargetKinds.Requirement),
                        ("target", $"{head.Number}/{w.Key}"), ("reason", SuspectReasons.TargetChanged)), ct);
                count += hit.Count;
            }
        }
        return count;
    }
}
