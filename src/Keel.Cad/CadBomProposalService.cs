using System.Globalization;
using System.Text.Json;
using Keel.Audit;
using Keel.Cad.Structure;
using Keel.Core;
using Keel.Security;
using Npgsql;
using NpgsqlTypes;

namespace Keel.Cad;

/// <summary>
/// CAD BOM ingestion (ADR-0035): a CAD structure becomes a proposed EBOM that people review against the current one; applying it goes through
/// the normal part and BOM services (<see cref="ICadPartPort"/>), so iteration rules, release locks and audit are the normal ones. Nothing is released.
/// A proposal names parts, so it is marked data: it carries markings, is listed through the read filter, and its audit entries hold numbers and counts only.
/// </summary>
public sealed class CadBomProposalService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, ICadPartPort parts)
{
    public const int MaxListLimit = 200;
    public const int MaxDiffParents = 200;
    private const int MaxAttributeLookups = 2000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Row(Guid Id, string Number, string Title, string Status, string Format, string? System, string RootRef, bool ApplyRemovals, Markings Markings,
        DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset? DecidedAt, string? DecidedBy, string? ResultJson);

    private sealed record PartRow(string Ref, string? CadNumber, string Name, string PartType, string Unit, string Source, IReadOnlyList<MatchRule> MatchOn,
        IReadOnlyDictionary<string, string> Attributes, string MatchKind, Guid? MatchedId, IReadOnlyList<Guid> Candidates, string Decision, Guid? DecisionId, Guid? CreatedId)
    {
        /// <summary>The Keel part this CAD part stands for, if one is chosen or was created.</summary>
        public Guid? Target => Decision switch { "match" => DecisionId ?? MatchedId, "create" => CreatedId, _ => null };
    }

    // ---------------------------------------------------------------- create

    public async Task<Outcome<ProposalDetail>> CreateAsync(Subject subject, CreateProposalRequest request, ReadOnlyMemory<byte> content, string? correlationId, CancellationToken ct = default)
    {
        var title = request.Title?.Trim() ?? "";
        var errors = new List<string>();
        if (title.Length is 0 or > 200) errors.Add("Title is required (at most 200 characters).");
        if (!StructureFormats.All.Contains(request.Format)) errors.Add($"Format must be one of: {string.Join(", ", StructureFormats.All)}.");
        if (errors.Count > 0) return Outcome<ProposalDetail>.Invalid(errors);

        var decision = AccessPolicy.Evaluate(subject, request.Markings, PlmAction.Modify);
        if (!decision.Permitted)
        {
            await audit.AppendAsync(CadGraphService.Entry(subject, "cad.bom_proposal.create", AuditOutcome.Denied, null, correlationId,
                ("reasons", string.Join(",", decision.Reasons)), ("markings", request.Markings.BannerText())), ct);
            return Outcome<ProposalDetail>.Forbidden(decision.Reasons);
        }
        StructureModel model;
        try { model = StructureReader.Read(request.Format, content, new StructureReadOptions(request.RootNumber)); }
        catch (StructureFormatException e) { return Outcome<ProposalDetail>.Invalid([$"{CadErrors.Proposal}: {e.Message}"]); }

        // Match against existing parts: by number first, then by attribute rules. Only parts the caller can read are found.
        var numbers = model.Parts.Where(p => !string.IsNullOrWhiteSpace(p.Number)).Select(p => p.Number!.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var byNumber = (await parts.FindManyAsync(subject, numbers, ct)).ToDictionary(p => p.Number, StringComparer.Ordinal);
        var parentRefs = model.Lines.Select(l => l.ParentRef).ToHashSet(StringComparer.Ordinal);
        var lookups = 0;
        var rows = new List<PartRow>();
        foreach (var p in model.Parts)
        {
            var partType = p.PartType ?? (parentRefs.Contains(p.Ref) ? "Assembly" : "Component");
            var unit = p.Unit ?? "EA";
            var source = p.Source ?? (parentRefs.Contains(p.Ref) ? "Make" : "Buy");
            string kind = "new"; Guid? matched = null; var candidates = new List<Guid>();
            if (p.Number is { } n && byNumber.TryGetValue(n.Trim(), out var hit)) { kind = "number"; matched = hit.MasterId; }
            else
            {
                foreach (var rule in p.MatchOn)
                {
                    if (++lookups > MaxAttributeLookups) return Outcome<ProposalDetail>.Invalid([$"{CadErrors.Proposal}: more than {MaxAttributeLookups} attribute matches were requested."]);
                    var found = await parts.FindByAttributeAsync(subject, rule.Attribute, rule.Value, 5, ct);
                    if (found.Count == 0) continue;
                    if (found.Count == 1) { kind = "attribute"; matched = found[0].MasterId; }
                    else { kind = "ambiguous"; candidates = [.. found.Select(f => f.MasterId)]; }
                    break;
                }
            }
            var dec = kind switch { "number" or "attribute" => "match", "ambiguous" => "undecided", _ => "create" };
            rows.Add(new PartRow(p.Ref, p.Number?.Trim(), p.Name.Trim(), partType, unit, source, p.MatchOn, p.Attributes, kind, matched, candidates, dec, null, null));
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        long seq;
        await using (var cmd = new NpgsqlCommand("SELECT nextval('cad.proposal_number_seq')", conn, tx)) seq = (long)(await cmd.ExecuteScalarAsync(ct))!;
        var number = "CBP-" + seq.ToString("D6", CultureInfo.InvariantCulture);
        var id = Guid.CreateVersion7();
        var now = clock.GetUtcNow();
        await using (var ins = new NpgsqlCommand(
            $"""
            INSERT INTO cad.bom_proposal (id, number, title, source_format, source_system, root_ref, status, apply_removals, {MarkingColumns.InsertColumns}, created_at, created_by)
            VALUES (@id, @number, @title, @format, @system, @root, 'draft', @removals, {MarkingColumns.InsertValues}, @at, @by)
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("number", number);
            ins.Parameters.AddWithValue("title", title);
            ins.Parameters.AddWithValue("format", request.Format);
            ins.Parameters.AddWithValue("system", (object?)model.SourceSystem ?? DBNull.Value);
            ins.Parameters.AddWithValue("root", model.RootRef);
            ins.Parameters.AddWithValue("removals", request.ApplyRemovals);
            foreach (var (k, v) in MarkingColumns.Parameters(request.Markings)) ins.Parameters.AddWithValue(k, v);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await using (var batch = new NpgsqlBatch(conn, tx))
        {
            foreach (var p in rows)
            {
                var cmd = new NpgsqlBatchCommand(
                    """
                    INSERT INTO cad.bom_proposal_part (proposal_id, ref, cad_number, name, part_type, unit, source, attributes, match_on, match_kind, matched_master_id, candidate_ids, decision)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
                    """);
                cmd.Parameters.AddWithValue(id);
                cmd.Parameters.AddWithValue(p.Ref);
                cmd.Parameters.AddWithValue((object?)p.CadNumber ?? DBNull.Value);
                cmd.Parameters.AddWithValue(p.Name);
                cmd.Parameters.AddWithValue(p.PartType);
                cmd.Parameters.AddWithValue(p.Unit);
                cmd.Parameters.AddWithValue(p.Source);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(p.Attributes, Json) });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(p.MatchOn, Json) });
                cmd.Parameters.AddWithValue(p.MatchKind);
                cmd.Parameters.AddWithValue((object?)p.MatchedId ?? DBNull.Value);
                cmd.Parameters.AddWithValue(p.Candidates.ToArray());
                cmd.Parameters.AddWithValue(p.Decision);
                batch.BatchCommands.Add(cmd);
            }
            if (batch.BatchCommands.Count > 0) await batch.ExecuteNonQueryAsync(ct);
        }
        await using (var batch = new NpgsqlBatch(conn, tx))
        {
            var seqNo = 0;
            foreach (var l in model.Lines)
            {
                var cmd = new NpgsqlBatchCommand(
                    """
                    INSERT INTO cad.bom_proposal_line (id, proposal_id, seq, parent_ref, child_ref, find_number, quantity, unit, reference_designators, notes)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                    """);
                cmd.Parameters.AddWithValue(Guid.CreateVersion7());
                cmd.Parameters.AddWithValue(id);
                cmd.Parameters.AddWithValue(++seqNo);
                cmd.Parameters.AddWithValue(l.ParentRef);
                cmd.Parameters.AddWithValue(l.ChildRef);
                cmd.Parameters.AddWithValue((object?)l.FindNumber ?? DBNull.Value);
                cmd.Parameters.AddWithValue(l.Quantity);
                cmd.Parameters.AddWithValue((object?)l.Unit ?? DBNull.Value);
                cmd.Parameters.AddWithValue(l.ReferenceDesignators.ToArray());
                cmd.Parameters.AddWithValue((object?)l.Notes ?? DBNull.Value);
                batch.BatchCommands.Add(cmd);
            }
            if (batch.BatchCommands.Count > 0) await batch.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, CadGraphService.Entry(subject, "cad.bom_proposal.create", AuditOutcome.Success, number, correlationId,
            ("format", request.Format), ("source", model.SourceSystem), ("parts", rows.Count.ToString(CultureInfo.InvariantCulture)), ("lines", model.Lines.Count.ToString(CultureInfo.InvariantCulture)),
            ("matched", rows.Count(r => r.Decision == "match").ToString(CultureInfo.InvariantCulture)), ("new", rows.Count(r => r.MatchKind == "new").ToString(CultureInfo.InvariantCulture)),
            ("ambiguous", rows.Count(r => r.MatchKind == "ambiguous").ToString(CultureInfo.InvariantCulture)), ("markings", request.Markings.BannerText())), ct);
        await tx.CommitAsync(ct);
        return await GetCoreAsync(subject, number, null, audited: false, ct);
    }

    // ---------------------------------------------------------------- list

    public async Task<ProposalList> ListAsync(Subject subject, string? status, int limit, int offset, string? correlationId, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxListLimit);
        offset = Math.Max(0, offset);
        var filter = AccessPolicy.BuildReadFilter(subject, "p");
        var items = new List<ProposalSummary>();
        long total = 0;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT p.id, p.number, p.title, p.status, p.source_format, p.source_system, p.created_at, p.created_by, {MarkingColumns.Select("p")},
                   (SELECT count(*) FROM cad.bom_proposal_part x WHERE x.proposal_id = p.id), (SELECT count(*) FROM cad.bom_proposal_line y WHERE y.proposal_id = p.id),
                   count(*) OVER ()
            FROM cad.bom_proposal p
            WHERE {filter.Sql} AND (@status::text IS NULL OR p.status = @status)
            ORDER BY p.created_at DESC, p.number DESC
            LIMIT @limit OFFSET @offset
            """, conn))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("status", (object?)status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var m = MarkingColumns.Read(r, 8);
                var o = 8 + MarkingColumns.Count;
                items.Add(new ProposalSummary(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                    (int)r.GetInt64(o), (int)r.GetInt64(o + 1), new DateTimeOffset(r.GetDateTime(6), TimeSpan.Zero), r.GetString(7), m.BannerText(), m));
                total = r.GetInt64(o + 2);
            }
        }
        await audit.AppendAsync(CadGraphService.Entry(subject, "cad.bom_proposal.list", AuditOutcome.Success, null, correlationId,
            ("status", status), ("returned", items.Count.ToString(CultureInfo.InvariantCulture)), ("numbers", string.Join(",", items.Select(i => i.Number)))), ct);
        return new ProposalList(items, total, Markings.AggregateBanner(items.Select(i => i.Markings)));
    }

    // ---------------------------------------------------------------- read with the diff

    public Task<Outcome<ProposalDetail>> GetAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) => GetCoreAsync(subject, number, correlationId, audited: true, ct);

    private async Task<Outcome<ProposalDetail>> GetCoreAsync(Subject subject, string number, string? correlationId, bool audited, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var row = await LoadRowAsync(conn, number, ct);
        if (row is null) return Outcome<ProposalDetail>.NotFound();
        var read = AccessPolicy.Evaluate(subject, row.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(CadGraphService.Entry(subject, "cad.bom_proposal.read", AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            return Outcome<ProposalDetail>.NotFound();
        }
        var partRows = await LoadPartsAsync(conn, row.Id, ct);
        var lines = await LoadLinesAsync(conn, row.Id, ct);
        var context = await ResolveAsync(subject, partRows, lines, row.ApplyRemovals, correlationId, includeSnapshots: true, ct);

        var modify = AccessPolicy.Evaluate(subject, row.Markings, PlmAction.Modify).Permitted;
        var blockers = context.Parents.Where(p => p.Blocker is not null && context.EditsByParent.GetValueOrDefault(p.ParentRef)?.Count > 0).Select(p => $"{p.Label}: {p.Blocker}").ToList();
        var undecided = partRows.Count(p => p.Decision == "undecided");
        var parentRefs = lines.Select(l => l.ParentRef).ToHashSet(StringComparer.Ordinal);
        var views = partRows.Select(p =>
        {
            var info = p.Target is { } t && context.Infos.TryGetValue(t, out var i) ? i : null;
            var matchedId = p.DecisionId ?? p.MatchedId;
            var matchedInfo = matchedId is { } mid && context.Infos.TryGetValue(mid, out var mi) ? mi : null;
            return new ProposalPartView(p.Ref, p.CadNumber, p.Name, p.PartType, p.Unit, p.Source, p.MatchKind, p.Decision,
                matchedInfo?.Number, matchedInfo?.Name, [.. p.Candidates.Where(c => context.Infos.ContainsKey(c)).Select(c => context.Infos[c].Number)],
                p.CreatedId is { } cid && context.Infos.TryGetValue(cid, out var ci) ? ci.Number : null, parentRefs.Contains(p.Ref))
            { MatchHidden = matchedId is not null && matchedInfo is null };
        }).ToList();
        var any = context.EditsByParent.Values.Any(e => e.Count > 0) || partRows.Any(p => p.Decision == "create" && p.CreatedId is null && (parentRefs.Contains(p.Ref) || lines.Any(l => l.ChildRef == p.Ref)));
        var canApply = row.Status == "draft" && undecided == 0 && any && blockers.Count == 0 && modify;
        ApplyReport? last = row.ResultJson is null ? null : JsonSerializer.Deserialize<ApplyReport>(row.ResultJson, Json);
        var visibleMarkings = new List<Markings> { row.Markings };
        visibleMarkings.AddRange(context.Infos.Values.Select(i => i.Markings));
        var banner = Markings.AggregateBanner(visibleMarkings);
        if (audited)
            await audit.AppendAsync(CadGraphService.Entry(subject, "cad.bom_proposal.read", AuditOutcome.Success, number, correlationId,
                ("parts", partRows.Count.ToString(CultureInfo.InvariantCulture)), ("lines", lines.Count.ToString(CultureInfo.InvariantCulture)), ("banner", banner)), ct);
        return Outcome<ProposalDetail>.Ok(new ProposalDetail(row.Id, row.Number, row.Title, row.Status, row.Format, row.System, row.RootRef, row.ApplyRemovals, banner, row.Markings,
            row.CreatedAt, row.CreatedBy, row.DecidedAt, row.DecidedBy, views, [.. lines.Select((l, i) => new ProposalLineView(i + 1, l.ParentRef, l.ChildRef, l.FindNumber, l.Quantity, l.Unit, l.ReferenceDesignators, l.Notes))],
            context.Parents, context.Rows, context.Truncated, undecided, canApply, modify && row.Status == "draft", blockers, last));
    }

    // ---------------------------------------------------------------- decisions

    public async Task<Outcome<ProposalDetail>> DecideAsync(Subject subject, string number, IReadOnlyList<ProposalPartDecision> decisions, bool? applyRemovals, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var (row, denied) = await AuthorizeAsync<ProposalDetail>(conn, subject, number, "cad.bom_proposal.decide", correlationId, ct);
        if (denied is not null) return denied;
        if (row!.Status != "draft") return Outcome<ProposalDetail>.Conflict($"{CadErrors.Proposal}: {number} is {row.Status} and can no longer change.");
        var partRows = (await LoadPartsAsync(conn, row.Id, ct)).ToDictionary(p => p.Ref, StringComparer.Ordinal);
        var matchNumbers = decisions.Where(d => d.Decision == "match" && !string.IsNullOrWhiteSpace(d.MatchNumber)).Select(d => d.MatchNumber!.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var found = (await parts.FindManyAsync(subject, matchNumbers, ct)).ToDictionary(p => p.Number, StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var d in decisions)
        {
            if (!partRows.TryGetValue(d.Ref, out var part)) { errors.Add($"The proposal has no part '{d.Ref}'."); continue; }
            if (d.Decision is not ("match" or "create" or "skip")) errors.Add($"Decision for '{d.Ref}' must be match, create or skip.");
            else if (d.Decision == "match")
            {
                var target = !string.IsNullOrWhiteSpace(d.MatchNumber) ? d.MatchNumber.Trim() : null;
                if (target is null && part.MatchedId is null) errors.Add($"'{d.Ref}' has no matched part: give the number of the part to use.");
                else if (target is not null && !found.ContainsKey(target)) errors.Add($"Part '{target}' was not found, or you are not authorized to see it.");
            }
        }
        if (errors.Count > 0) return Outcome<ProposalDetail>.Invalid(errors);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var d in decisions)
        {
            Guid? decisionId = d.Decision == "match" && !string.IsNullOrWhiteSpace(d.MatchNumber) ? found[d.MatchNumber.Trim()].MasterId : null;
            await using var cmd = new NpgsqlCommand("UPDATE cad.bom_proposal_part SET decision = @d, decision_master_id = @m WHERE proposal_id = @p AND ref = @r", conn, tx);
            cmd.Parameters.AddWithValue("d", d.Decision);
            cmd.Parameters.AddWithValue("m", (object?)decisionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("p", row.Id);
            cmd.Parameters.AddWithValue("r", d.Ref);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (applyRemovals is { } ar && ar != row.ApplyRemovals)
        {
            await using var cmd = new NpgsqlCommand("UPDATE cad.bom_proposal SET apply_removals = @a WHERE id = @p", conn, tx);
            cmd.Parameters.AddWithValue("a", ar);
            cmd.Parameters.AddWithValue("p", row.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, CadGraphService.Entry(subject, "cad.bom_proposal.decide", AuditOutcome.Success, number, correlationId,
            ("decisions", decisions.Count.ToString(CultureInfo.InvariantCulture)), ("applyRemovals", applyRemovals?.ToString())), ct);
        await tx.CommitAsync(ct);
        return await GetCoreAsync(subject, number, correlationId, audited: false, ct);
    }

    public async Task<Outcome<ProposalDetail>> DiscardAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var (row, denied) = await AuthorizeAsync<ProposalDetail>(conn, subject, number, "cad.bom_proposal.discard", correlationId, ct);
        if (denied is not null) return denied;
        if (row!.Status != "draft") return Outcome<ProposalDetail>.Conflict($"{CadErrors.Proposal}: {number} is {row.Status} and can no longer change.");
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("UPDATE cad.bom_proposal SET status = 'discarded', decided_at = @at, decided_by = @by WHERE id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
            cmd.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            cmd.Parameters.AddWithValue("id", row.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, CadGraphService.Entry(subject, "cad.bom_proposal.discard", AuditOutcome.Success, number, correlationId), ct);
        await tx.CommitAsync(ct);
        return await GetCoreAsync(subject, number, correlationId, audited: false, ct);
    }

    // ---------------------------------------------------------------- apply

    public async Task<Outcome<ApplyReport>> ApplyAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var (row, denied) = await AuthorizeAsync<ApplyReport>(conn, subject, number, "cad.bom_proposal.apply", correlationId, ct);
        if (denied is not null) return denied;
        if (row!.Status != "draft") return Outcome<ApplyReport>.Conflict($"{CadErrors.Proposal}: {number} is {row.Status} and can no longer change.");
        var partRows = await LoadPartsAsync(conn, row.Id, ct);
        var lines = await LoadLinesAsync(conn, row.Id, ct);
        var undecided = partRows.Count(p => p.Decision == "undecided");
        if (undecided > 0) return Outcome<ApplyReport>.Conflict($"{CadErrors.Proposal}: {undecided} part(s) still need a decision (several Keel parts match).");

        // Nothing is changed unless every parent that will change can be changed.
        var context = await ResolveAsync(subject, partRows, lines, row.ApplyRemovals, correlationId, includeSnapshots: true, ct, forApply: true);
        var blocked = context.Parents.Where(p => p.Blocker is not null && context.EditsByParent.GetValueOrDefault(p.ParentRef)?.Count > 0).Select(p => $"{p.Label} ({p.Blocker})").ToList();
        if (blocked.Count > 0)
        {
            await audit.AppendAsync(CadGraphService.Entry(subject, "cad.bom_proposal.apply", AuditOutcome.Failure, number, correlationId, ("reason", "blocked"), ("blocked", blocked.Count.ToString(CultureInfo.InvariantCulture))), ct);
            return Outcome<ApplyReport>.Conflict($"{CadErrors.Proposal}: nothing was changed; these parts cannot be edited now: {string.Join("; ", blocked)}.");
        }
        if (context.HiddenDecisions > 0) return Outcome<ApplyReport>.Conflict($"{CadErrors.Proposal}: {context.HiddenDecisions} chosen part(s) are not visible to you; decide again.");

        // Create the missing parts the structure needs (recorded one by one, so a retry never creates a part twice).
        var created = new List<string>();
        var used = lines.SelectMany(l => new[] { l.ParentRef, l.ChildRef }).ToHashSet(StringComparer.Ordinal);
        var numberOf = new Dictionary<string, string>(StringComparer.Ordinal); // part ref -> Keel number
        foreach (var p in partRows)
            if (p.Target is { } t && context.Infos.TryGetValue(t, out var info)) numberOf[p.Ref] = info.Number;
        foreach (var p in partRows.Where(p => p is { Decision: "create", CreatedId: null } && used.Contains(p.Ref)))
        {
            var attrs = p.Attributes.Count == 0 ? null : p.Attributes;
            var made = await parts.CreateAsync(subject, new CadPartCreate(p.Name, p.PartType, p.Unit, p.Source, row.Markings,
                p.CadNumber is null ? "Created from a CAD structure." : $"Created from a CAD structure (CAD number {p.CadNumber}).", attrs), correlationId, ct);
            if (!made.IsOk)
            {
                var report = new ApplyReport(created, [], false);
                await SaveResultAsync(conn, row.Id, report, ct);
                await audit.AppendAsync(CadGraphService.Entry(subject, "cad.bom_proposal.apply", AuditOutcome.Failure, number, correlationId, ("reason", "part.create"), ("created", created.Count.ToString(CultureInfo.InvariantCulture))), ct);
                return Outcome<ApplyReport>.Conflict($"{CadErrors.Proposal}: part '{Trunc(p.Name)}' could not be created ({string.Join(" ", made.Errors)}). {created.Count} part(s) were created before this; run apply again after fixing it.");
            }
            await using (var upd = new NpgsqlCommand("UPDATE cad.bom_proposal_part SET created_master_id = @m WHERE proposal_id = @p AND ref = @r", conn))
            {
                upd.Parameters.AddWithValue("m", made.Value!.MasterId);
                upd.Parameters.AddWithValue("p", row.Id);
                upd.Parameters.AddWithValue("r", p.Ref);
                await upd.ExecuteNonQueryAsync(ct);
            }
            numberOf[p.Ref] = made.Value.Number;
            created.Add(made.Value.Number);
        }

        // Edit each parent through the part check-out, the BOM working-copy rules and a check-in.
        var results = new List<ApplyNodeResult>();
        var anyFailed = false;
        foreach (var parentRef in context.ParentOrder)
        {
            if (!numberOf.TryGetValue(parentRef, out var parentNumber)) continue;
            var pairs = ProposalDiff.Compute([.. ResolvedFor(parentRef, lines, partRows, numberOf)], context.Snapshots.GetValueOrDefault(parentRef)?.Lines ?? []);
            var edits = ProposalDiff.Edits(pairs, row.ApplyRemovals);
            if (edits.Count == 0) continue;
            var applied = await parts.ApplyBomAsync(subject, parentNumber, edits, $"CAD structure proposal {number}", correlationId, ct);
            if (applied.IsOk) results.Add(new ApplyNodeResult(parentNumber, true, applied.Value, null, edits.Count));
            else { anyFailed = true; results.Add(new ApplyNodeResult(parentNumber, false, null, string.Join(" ", applied.Errors), edits.Count)); }
        }
        var final = new ApplyReport(created, results, !anyFailed);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!anyFailed)
            await using (var cmd = new NpgsqlCommand("UPDATE cad.bom_proposal SET status = 'applied', decided_at = @at, decided_by = @by, apply_result = @res WHERE id = @id", conn, tx))
            {
                cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
                cmd.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
                cmd.Parameters.Add(new NpgsqlParameter("res", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(final, Json) });
                cmd.Parameters.AddWithValue("id", row.Id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        else
            await using (var cmd = new NpgsqlCommand("UPDATE cad.bom_proposal SET apply_result = @res WHERE id = @id", conn, tx))
            {
                cmd.Parameters.Add(new NpgsqlParameter("res", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(final, Json) });
                cmd.Parameters.AddWithValue("id", row.Id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        await audit.AppendAsync(tx, CadGraphService.Entry(subject, "cad.bom_proposal.apply", anyFailed ? AuditOutcome.Failure : AuditOutcome.Success, number, correlationId,
            ("created", string.Join(",", created)), ("parents", string.Join(",", results.Where(r => r.Ok).Select(r => $"{r.Parent}:{r.Iteration}"))),
            ("failed", string.Join(",", results.Where(r => !r.Ok).Select(r => r.Parent))), ("edits", results.Sum(r => r.Edits).ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return anyFailed
            ? Outcome<ApplyReport>.Conflict($"{CadErrors.Proposal}: {results.Count(r => !r.Ok)} parent part(s) could not be changed ({string.Join("; ", results.Where(r => !r.Ok).Select(r => r.Parent + ": " + r.Error))}). The others were changed; run apply again after fixing the cause.")
            : Outcome<ApplyReport>.Ok(final);
    }

    // ---------------------------------------------------------------- shared resolution

    private sealed record Context(
        IReadOnlyDictionary<Guid, CadPartInfo> Infos, IReadOnlyDictionary<string, CadBomSnapshot> Snapshots, IReadOnlyList<DiffParent> Parents, IReadOnlyList<DiffRow> Rows,
        IReadOnlyDictionary<string, IReadOnlyList<CadBomEdit>> EditsByParent, IReadOnlyList<string> ParentOrder, bool Truncated, int HiddenDecisions);

    private static IEnumerable<ResolvedLine> ResolvedFor(string parentRef, IReadOnlyList<LineRow> lines, IReadOnlyList<PartRow> partRows, IReadOnlyDictionary<string, string> numberOf)
    {
        var byRef = partRows.ToDictionary(p => p.Ref, StringComparer.Ordinal);
        foreach (var l in lines.Where(l => l.ParentRef == parentRef))
        {
            var child = byRef[l.ChildRef];
            if (child.Decision == "skip") continue;
            var childNumber = numberOf.GetValueOrDefault(l.ChildRef);
            yield return new ResolvedLine(l.ChildRef, childNumber, childNumber ?? child.Name, l.Quantity, l.Unit, l.FindNumber, l.ReferenceDesignators, l.Notes,
                Resolvable: child.Decision is "match" or "create" && (childNumber is not null || child.Decision == "create"));
        }
    }

    /// <summary>Loads the Keel parts the proposal points at (readable ones only), the current BOM of every parent, and computes the diff rows and edits.</summary>
    private async Task<Context> ResolveAsync(Subject subject, IReadOnlyList<PartRow> partRows, IReadOnlyList<LineRow> lines, bool applyRemovals, string? correlationId, bool includeSnapshots, CancellationToken ct, bool forApply = false)
    {
        var ids = partRows.SelectMany(p => new[] { p.MatchedId, p.DecisionId, p.CreatedId }.Concat(p.Candidates.Cast<Guid?>())).Where(g => g is not null).Select(g => g!.Value).Distinct().ToArray();
        var infos = (await parts.FindByIdsAsync(subject, ids, ct)).ToDictionary(i => i.MasterId);
        var numberOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var hidden = 0;
        foreach (var p in partRows)
        {
            if (p.Target is { } t) { if (infos.TryGetValue(t, out var i)) numberOf[p.Ref] = i.Number; else if (p.Decision == "match") hidden++; }
        }
        var byRef = partRows.ToDictionary(p => p.Ref, StringComparer.Ordinal);
        string Label(string r) => numberOf.TryGetValue(r, out var n) ? n : byRef[r].CadNumber ?? Trunc(byRef[r].Name);
        var parentOrder = lines.Select(l => l.ParentRef).Distinct(StringComparer.Ordinal).Where(r => byRef[r].Decision is "match" or "create").ToList();
        var snapshots = new Dictionary<string, CadBomSnapshot>(StringComparer.Ordinal);
        var parents = new List<DiffParent>();
        var rows = new List<DiffRow>();
        var edits = new Dictionary<string, IReadOnlyList<CadBomEdit>>(StringComparer.Ordinal);
        var truncated = false;
        var inspected = 0;
        foreach (var parentRef in parentOrder)
        {
            var part = byRef[parentRef];
            string? blocker = null;
            CadBomSnapshot? snapshot = null;
            if (part.Decision == "match")
            {
                if (!numberOf.TryGetValue(parentRef, out var parentNumber)) blocker = "the matched part is not visible to you";
                else if (!forApply && inspected >= MaxDiffParents) { truncated = true; continue; }
                else
                {
                    inspected++;
                    var snap = await parts.GetBomAsync(subject, parentNumber, correlationId, ct);
                    if (!snap.IsOk) blocker = "its structure cannot be read";
                    else
                    {
                        snapshot = snap.Value!;
                        snapshots[parentRef] = snapshot;
                        if (snapshot.IsLocked) blocker = $"revision {snapshot.Revision} is released; change it through a change notice";
                        else if (snapshot.CheckedOutBy is not null) blocker = $"checked out by {snapshot.CheckedOutBy}";
                    }
                }
            }
            var resolved = ResolvedFor(parentRef, lines, partRows, numberOf).ToList();
            var pairs = ProposalDiff.Compute(resolved, snapshot?.Lines ?? []);
            foreach (var pair in pairs)
            {
                var childLabel = pair.Proposed?.ChildLabel ?? pair.Current!.ChildNumber;
                var applies = blocker is null && pair.Kind switch
                {
                    DiffKind.Added or DiffKind.Changed => pair.Proposed!.Resolvable,
                    DiffKind.Removed => applyRemovals,
                    _ => false,
                };
                string? note = pair.Kind == DiffKind.Removed && !applyRemovals ? "kept: removals are off" : pair.Proposed is { Resolvable: false } ? "child needs a decision" : pair.Proposed?.ChildNumber is null && pair.Proposed is not null ? "new part" : null;
                rows.Add(new DiffRow(parentRef, Label(parentRef), childLabel, pair.Proposed?.ChildNumber ?? pair.Current?.ChildNumber, pair.Kind, ProposalDiff.Side(pair.Current), ProposalDiff.Side(pair.Proposed), applies, note));
            }
            parents.Add(new DiffParent(parentRef, Label(parentRef), part.Decision == "create" ? "new part" : "existing part", blocker,
                pairs.Count(p => p.Kind == DiffKind.Added), pairs.Count(p => p.Kind == DiffKind.Removed), pairs.Count(p => p.Kind == DiffKind.Changed), pairs.Count(p => p.Kind == DiffKind.Unchanged),
                snapshot?.RedactedCount ?? 0));
            edits[parentRef] = ProposalDiff.Edits(pairs, applyRemovals); // a blocked parent keeps its edits so the blocker is reported, never silently dropped
        }
        return new Context(infos, snapshots, parents, rows, edits, parentOrder, truncated, hidden);
    }

    // ---------------------------------------------------------------- storage

    private sealed record LineRow(string ParentRef, string ChildRef, int? FindNumber, decimal Quantity, string? Unit, IReadOnlyList<string> ReferenceDesignators, string? Notes);

    private static string Trunc(string s) => s.Length <= 40 ? s : s[..40] + "...";

    private static async Task<Row?> LoadRowAsync(NpgsqlConnection conn, string number, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT p.id, p.number, p.title, p.status, p.source_format, p.source_system, p.root_ref, p.apply_removals, p.created_at, p.created_by, p.decided_at, p.decided_by, p.apply_result::text,
                   {MarkingColumns.Select("p")}
            FROM cad.bom_proposal p WHERE p.number = @n
            """, conn);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Row(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetBoolean(7), MarkingColumns.Read(r, 13),
            new DateTimeOffset(r.GetDateTime(8), TimeSpan.Zero), r.GetString(9), r.IsDBNull(10) ? null : new DateTimeOffset(r.GetDateTime(10), TimeSpan.Zero), r.IsDBNull(11) ? null : r.GetString(11),
            r.IsDBNull(12) ? null : r.GetString(12));
    }

    private static async Task<List<PartRow>> LoadPartsAsync(NpgsqlConnection conn, Guid proposalId, CancellationToken ct)
    {
        var list = new List<PartRow>();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT ref, cad_number, name, part_type, unit, source, attributes::text, match_on::text, match_kind, matched_master_id, candidate_ids, decision, decision_master_id, created_master_id
            FROM cad.bom_proposal_part WHERE proposal_id = @p ORDER BY ref
            """, conn);
        cmd.Parameters.AddWithValue("p", proposalId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new PartRow(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                JsonSerializer.Deserialize<List<MatchRule>>(r.GetString(7), Json) ?? [], JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(6), Json) ?? [],
                r.GetString(8), r.IsDBNull(9) ? null : r.GetGuid(9), (Guid[])r.GetValue(10), r.GetString(11), r.IsDBNull(12) ? null : r.GetGuid(12), r.IsDBNull(13) ? null : r.GetGuid(13)));
        return list;
    }

    private static async Task<List<LineRow>> LoadLinesAsync(NpgsqlConnection conn, Guid proposalId, CancellationToken ct)
    {
        var list = new List<LineRow>();
        await using var cmd = new NpgsqlCommand(
            "SELECT parent_ref, child_ref, find_number, quantity, unit, reference_designators, notes FROM cad.bom_proposal_line WHERE proposal_id = @p ORDER BY seq", conn);
        cmd.Parameters.AddWithValue("p", proposalId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new LineRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetString(4), (string[])r.GetValue(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }

    private static async Task SaveResultAsync(NpgsqlConnection conn, Guid id, ApplyReport report, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("UPDATE cad.bom_proposal SET apply_result = @res WHERE id = @id", conn);
        cmd.Parameters.Add(new NpgsqlParameter("res", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(report, Json) });
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<(Row? Row, Outcome<T>? Denied)> AuthorizeAsync<T>(NpgsqlConnection conn, Subject subject, string number, string action, string? correlationId, CancellationToken ct)
    {
        var row = await LoadRowAsync(conn, number, ct);
        if (row is null) return (null, Outcome<T>.NotFound());
        var read = AccessPolicy.Evaluate(subject, row.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(CadGraphService.Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            return (null, Outcome<T>.NotFound());
        }
        var modify = AccessPolicy.Evaluate(subject, row.Markings, PlmAction.Modify);
        if (!modify.Permitted)
        {
            await audit.AppendAsync(CadGraphService.Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", modify.Reasons))), ct);
            return (null, Outcome<T>.Forbidden(modify.Reasons));
        }
        return (row, null);
    }
}
