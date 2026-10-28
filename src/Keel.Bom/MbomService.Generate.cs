using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

public sealed partial class MbomService
{
    /// <summary>One EBOM line with the columns a copy needs.</summary>
    internal sealed record EbomCopyLine(Guid Id, Guid Child, decimal Quantity, string Unit, int Find, int Alt, string[] Designators, string? Notes, EffectivityRange? Eff);

    internal static async Task<List<EbomCopyLine>> LoadEbomCopyLinesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid partIteration, EffectivityRequest? unit, CancellationToken ct)
    {
        var list = new List<EbomCopyLine>();
        await using var cmd = new NpgsqlCommand(
            $"SELECT u.id, u.child_master_id, u.quantity, u.unit, u.find_number, u.alt, u.reference_designators, u.notes, u.eff_kind, u.eff_context, u.eff_from, u.eff_to FROM plm.bom_usage u WHERE u.parent_iteration_id = @it{(unit is null ? "" : " AND " + BomSql.UsageApplies("u"))} ORDER BY u.find_number, u.alt", conn, tx);
        cmd.Parameters.AddWithValue("it", partIteration);
        if (unit is not null) BomService.BindEffectivity(cmd, unit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new EbomCopyLine(r.GetGuid(0), r.GetGuid(1), r.GetDecimal(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5), r.GetFieldValue<string[]>(6), r.IsDBNull(7) ? null : r.GetString(7), EffectivityStore.ReadRange(r, 8)));
        return list;
    }

    /// <summary>The parts among <paramref name="ids"/> the subject may read (number, name, type, source, default unit); the rest are hidden and only counted.</summary>
    internal static async Task<Dictionary<Guid, PartRef>> ReadableAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var map = new Dictionary<Guid, PartRef>();
        if (ids.Count == 0) return map;
        var filter = AccessPolicy.BuildReadFilter(subject, "m");
        await using var cmd = new NpgsqlCommand(
            $"SELECT m.id, m.number, m.name, m.part_type, m.source, m.default_unit, {MarkingColumns.Select("m")} FROM plm.part_master m WHERE {filter.Sql} AND m.id = ANY(@ids)", conn, tx);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetGuid(0)] = new PartRef(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), MarkingColumns.Read(r, 6));
        return map;
    }

    // ---------------------------------------------------------------- generate from EBOM

    /// <summary>
    /// Generates the MBOM of a part for a plant from its EBOM: every EBOM line becomes an MBOM line with a matched 1:1 link, the supply type follows the child
    /// part. With <see cref="GenerateMbomCommand.MultiLevel"/> an MBOM is also generated for every Make sub-assembly (an existing MBOM of the plant is kept).
    /// All in one transaction; refused when the EBOM holds parts the caller cannot read.
    /// </summary>
    public async Task<Outcome<GenerateResult>> GenerateAsync(Subject subject, GenerateMbomCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        var spec = cmd.Spec ?? EbomSpec.Released;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Step<GenerateResult> step;
        try { step = await GenerateCoreAsync(conn, tx, subject, cmd, spec, correlationId, ct); }
        catch (PostgresException e) when (e.SqlState is "23505" or "23514")
        {
            step = Step<GenerateResult>.Conflict($"The MBOM could not be generated: {e.MessageText}");
        }
        if (step.Kind != OutcomeKind.Ok)
        {
            await tx.RollbackAsync(ct);
            await audit.AppendAsync(step.Denied
                ? Entry(subject, "mbom.generate", AuditOutcome.Denied, null, correlationId, [("part", cmd.PartNumber), ("plant", cmd.Plant), .. step.Details])
                : Entry(subject, "mbom.generate", AuditOutcome.Failure, null, correlationId, ("part", cmd.PartNumber), ("plant", cmd.Plant), ("error", string.Join(" ", step.Errors))), ct);
            return step.Kind == OutcomeKind.Conflict ? Outcome<GenerateResult>.Conflict(step.Errors[0]) : Outcome<GenerateResult>.Invalid(step.Errors);
        }
        await tx.CommitAsync(ct);
        return Outcome<GenerateResult>.Ok(step.Value!);
    }

    private async Task<Step<GenerateResult>> GenerateCoreAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, GenerateMbomCommand cmd, EbomSpec spec, string? correlationId, CancellationToken ct)
    {
        var specErrors = spec.Validate();
        if (specErrors.Count > 0) return Step<GenerateResult>.Invalid([.. specErrors]);
        var rootPart = string.IsNullOrWhiteSpace(cmd.PartNumber) ? null : await MbomStore.LoadPartByNumberAsync(conn, tx, cmd.PartNumber.Trim(), ct);
        if (rootPart is null || !AccessPolicy.Evaluate(subject, rootPart.Markings, PlmAction.Read).Permitted)
            return Step<GenerateResult>.Hidden($"Part '{cmd.PartNumber}' was not found, or you are not authorized to see it.", "part.not_readable");

        var items = new List<GeneratedMbom>();
        var notices = new List<string>();
        var queue = new Queue<(PartRef Part, EbomSpec Spec, bool IsRoot)>();
        var seen = new HashSet<Guid> { rootPart.Id };
        queue.Enqueue((rootPart, spec, true));
        var created = 0;
        while (queue.Count > 0)
        {
            var (part, partSpec, isRoot) = queue.Dequeue();
            if (part.PartType != "Assembly")
            {
                if (isRoot) return Step<GenerateResult>.Invalid($"{part.Number} is a {part.PartType}; only Assembly parts have a manufacturing BOM.");
                continue;
            }
            var existing = await MbomStore.LoadByPartAsync(conn, tx, part.Id, cmd.Plant?.Trim().ToUpperInvariant() ?? "", false, ct);
            if (existing is not null)
            {
                if (isRoot) return Step<GenerateResult>.Conflict($"{part.Number} already has the MBOM {existing.Number} for plant {existing.Plant}; regenerate it from the EBOM instead.");
                items.Add(new GeneratedMbom(existing.Number, part.Number, existing.Plant, "", 0, false, "An MBOM of this plant already exists and was kept; regenerate it from its own page."));
                continue;
            }
            var (resolved, error) = await MbomStore.ResolveEbomAsync(conn, tx, subject, part, partSpec, ct);
            if (resolved is null)
            {
                if (isRoot) return Step<GenerateResult>.Invalid(error!);
                notices.Add($"{part.Number}: {error} No MBOM was generated for it.");
                continue;
            }
            var ebom = await LoadEbomCopyLinesAsync(conn, tx, resolved.IterationId, partSpec.Kind == EbomSpecKind.Effectivity ? partSpec.Unit : null, ct);
            if (ebom.Count > MbomLimits.MaxLines) return Step<GenerateResult>.Conflict($"{part.Number} has more than {MbomLimits.MaxLines} EBOM lines.");
            var children = await ReadableAsync(conn, tx, subject, [.. ebom.Select(e => e.Child).Distinct()], ct);
            var hidden = ebom.Select(e => e.Child).Distinct().Count() - children.Count;
            if (hidden > 0)
                return Step<GenerateResult>.Hidden($"{part.Number} contains {hidden} item(s) you are not authorized to see; its MBOM cannot be generated.", $"ebom.contains_restricted:{hidden}");

            var now = clock.GetUtcNow();
            var who = BomService.DisplayId(subject);
            var lines = new List<NewMbomLine>();
            foreach (var e in ebom)
            {
                var c = children[e.Child];
                lines.Add(new NewMbomLine(Guid.CreateVersion7(), e.Child, e.Quantity, e.Unit, e.Find, e.Designators, null, false, MbomRules.DefaultSupplyType(c.PartType, c.Source), null, false,
                    e.Notes, e.Eff, now, who, [new LinkInput(e.Id, LinkStatuses.Matched, null)]));
                if (cmd.MultiLevel && c.PartType == "Assembly" && c.Source == "Make" && seen.Add(c.Id))
                    queue.Enqueue((c, partSpec.Kind is EbomSpecKind.Iteration ? EbomSpec.Released : partSpec, false));
            }
            var markings = isRoot ? cmd.Markings : null;
            var step = await CreateCoreAsync(conn, tx, subject, part.Number, cmd.Plant ?? "", markings, cmd.ConfirmRaiseMarkings, null, resolved.IterationId, resolved.Description, lines, correlationId, ct);
            if (step.Kind != OutcomeKind.Ok) return new Step<GenerateResult>(step.Kind, step.Errors, default, step.Details, step.Denied);
            var head = step.Value!;
            await audit.AppendAsync(tx, Entry(subject, "mbom.generate", AuditOutcome.Success, head.Number, correlationId,
                ("part", part.Number), ("plant", head.Plant), ("derivedFrom", resolved.Description), ("lines", Fmt(lines.Count)), ("multiLevel", cmd.MultiLevel.ToString()),
                ("banner", head.Markings.BannerText())), ct);
            items.Add(new GeneratedMbom(head.Number, part.Number, head.Plant, resolved.Description, lines.Count, true, null));
            if (++created > MbomLimits.MaxGenerated) return Step<GenerateResult>.Conflict($"More than {MbomLimits.MaxGenerated} MBOMs would be generated in one request; generate the lower levels separately.");
        }
        return Step<GenerateResult>.Done(new GenerateResult(items, notices), ("created", Fmt(created)));
    }

    // ---------------------------------------------------------------- regeneration: proposal and apply

    private sealed record Plan(
        RegenerationProposal Proposal, ResolvedEbom Target, List<NewMbomLine> Old, Guid? OldDerived, Dictionary<Guid, Guid> NewByOldEbom,
        Dictionary<string, Func<List<NewMbomLine>, NewMbomLine?>> Actions, IReadOnlyDictionary<Guid, PartRef> Parts);

    /// <summary>
    /// Compares the latest MBOM iteration with the EBOM a spec selects and proposes what to change. Manufacturing edits are never overwritten silently: a quantity,
    /// unit or effectivity the engineer declared or changed is only reported (review items); lines without an EBOM source (MBOM-only, manual) are always kept.
    /// Refused when a line involves a part the caller cannot read, so the proposal never reveals one.
    /// </summary>
    public Task<Outcome<RegenerationProposal>> ProposeRegenerationAsync(Subject subject, string number, EbomSpec? spec, string? correlationId, CancellationToken ct = default) =>
        ReadAsync<RegenerationProposal>(subject, number, null, "mbom.regenerate_propose", correlationId, async (conn, tx, head) =>
        {
            var (plan, error, hiddenReason) = await BuildPlanAsync(conn, tx, subject, head, spec ?? EbomSpec.Released, ct);
            if (plan is null) return (Outcome<RegenerationProposal>.Invalid([error!]), hiddenReason is null ? [] : [("reasons", hiddenReason)]);
            return (Outcome<RegenerationProposal>.Ok(plan.Proposal), [("from", plan.Proposal.FromDerived), ("to", plan.Proposal.ToEbom), ("items", Fmt(plan.Proposal.Items.Count))]);
        }, ct);

    private static async Task<(Plan? Plan, string? Error, string? HiddenReason)> BuildPlanAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, MbomHead head, EbomSpec spec, CancellationToken ct)
    {
        Guid? oldDerived;
        await using (var cmd = new NpgsqlCommand("SELECT derived_part_iteration_id FROM mbom.iteration WHERE id = @i", conn, tx))
        {
            cmd.Parameters.AddWithValue("i", head.IterationId);
            oldDerived = await cmd.ExecuteScalarAsync(ct) as Guid?;
        }
        if (oldDerived is null) return (null, "This MBOM is not derived from an EBOM; choose an EBOM source while editing it first.", null);
        var part = (await MbomStore.LoadPartAsync(conn, tx, head.PartMasterId, ct))!;
        var (target, error) = await MbomStore.ResolveEbomAsync(conn, tx, subject, part, spec, ct);
        if (target is null) return (null, error, null);

        var oldLines = await LoadEbomCopyLinesAsync(conn, tx, oldDerived.Value, null, ct);
        var newLines = await LoadEbomCopyLinesAsync(conn, tx, target.IterationId, spec.Kind == EbomSpecKind.Effectivity ? spec.Unit : null, ct);
        var content = await MbomStore.ReadContentAsync(conn, tx, false, head.IterationId, ct);
        var ids = oldLines.Select(l => l.Child).Concat(newLines.Select(l => l.Child)).Concat(content.Select(l => l.Child)).Distinct().ToList();
        var parts = await ReadableAsync(conn, tx, subject, ids, ct);
        if (parts.Count < ids.Count)
            return (null, $"The comparison involves {ids.Count - parts.Count} item(s) you are not authorized to see; the proposal cannot be shown.", $"proposal.contains_restricted:{ids.Count - parts.Count}");

        // Carry each link to the line of the new EBOM iteration that is the same line (part, find number, alternate), else same part and find number, else the only line of that part.
        var byIdentity = new Dictionary<(Guid, int, int), EbomCopyLine>();
        var byFind = new Dictionary<(Guid, int), EbomCopyLine>();
        foreach (var n in newLines) { byIdentity.TryAdd((n.Child, n.Find, n.Alt), n); byFind.TryAdd((n.Child, n.Find), n); }
        var byChild = newLines.GroupBy(n => n.Child).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var newByOld = new Dictionary<Guid, Guid>();
        foreach (var o in oldLines)
        {
            if (byIdentity.TryGetValue((o.Child, o.Find, o.Alt), out var a) || byFind.TryGetValue((o.Child, o.Find), out a) || byChild.TryGetValue(o.Child, out a)) newByOld[o.Id] = a.Id;
        }
        var oldById = oldLines.ToDictionary(l => l.Id);
        var newById = newLines.ToDictionary(l => l.Id);

        var items = new List<ProposalItem>();
        var actions = new Dictionary<string, Func<List<NewMbomLine>, NewMbomLine?>>(StringComparer.Ordinal);
        var coveredNew = new HashSet<Guid>();
        var relinked = 0;
        var linkedBy = content.SelectMany(l => l.Links.Select(k => (Line: l, k.EbomUsageId))).GroupBy(x => x.EbomUsageId).ToDictionary(g => g.Key, g => g.Select(x => x.Line).ToList());
        var preserved = content.Count(l => l.Links.Count == 0);
        foreach (var m in content.OrderBy(l => l.Find))
        {
            if (m.Links.Count == 0) continue;
            var mapped = m.Links.Where(k => newByOld.ContainsKey(k.EbomUsageId)).ToList();
            relinked += mapped.Count;
            foreach (var k in mapped) coveredNew.Add(newByOld[k.EbomUsageId]);
            var name = parts[m.Child];
            if (mapped.Count == 0)
            {
                if (m.Links.Any(k => k.Status == LinkStatuses.Substituted))
                    items.Add(new ProposalItem($"review:{m.Id:N}", ProposalKinds.ReviewSubstituted, m.Find, null, name.Number, name.Name, "The EBOM line this substitution was declared for no longer exists; decide whether to keep the line.", false));
                else
                {
                    var id = $"remove:{m.Id:N}";
                    items.Add(new ProposalItem(id, ProposalKinds.RemoveLine, m.Find, null, name.Number, name.Name, "The EBOM line this line implemented no longer exists; remove the line.", true));
                    var mid = m.Id;
                    actions[id] = lines => { lines.RemoveAll(l => l.Id == mid); return null; };
                }
                continue;
            }
            if (m.Links.Count != 1 || linkedBy[m.Links[0].EbomUsageId].Count != 1) continue; // splits and merges are compared as a whole by reconciliation
            var link = m.Links[0];
            var oldE = oldById[link.EbomUsageId];
            var newE = newById[newByOld[link.EbomUsageId]];
            var untouched = link.Status == LinkStatuses.Matched;
            void Change(string kind, string key, string summary, Func<NewMbomLine, NewMbomLine> apply)
            {
                var id = $"{key}:{m.Id:N}";
                if (untouched)
                {
                    items.Add(new ProposalItem(id, kind, m.Find, newE.Find, name.Number, name.Name, summary, true));
                    var mid = m.Id;
                    actions[id] = lines => { var at = lines.FindIndex(l => l.Id == mid); if (at >= 0) lines[at] = apply(lines[at]); return null; };
                }
                else items.Add(new ProposalItem($"review:{key}:{m.Id:N}", ProposalKinds.ReviewAdjusted, m.Find, newE.Find, name.Number, name.Name, $"{summary} The line has a declared adjustment and is left as it is.", false));
            }
            if (oldE.Quantity != newE.Quantity)
            {
                if (m.Quantity == oldE.Quantity) Change(ProposalKinds.ChangeQuantity, "qty", $"EBOM quantity {MbomText.Qty(oldE.Quantity)} to {MbomText.Qty(newE.Quantity)} {newE.Unit}.", l => l with { Quantity = newE.Quantity });
                else items.Add(new ProposalItem($"review:qty:{m.Id:N}", ProposalKinds.ReviewAdjusted, m.Find, newE.Find, name.Number, name.Name,
                    $"EBOM quantity {MbomText.Qty(oldE.Quantity)} to {MbomText.Qty(newE.Quantity)}; the MBOM line has {MbomText.Qty(m.Quantity)} and is left as it is.", false));
            }
            if (oldE.Unit != newE.Unit)
            {
                if (m.Unit == oldE.Unit) Change(ProposalKinds.ChangeUnit, "unit", $"EBOM unit {oldE.Unit} to {newE.Unit}.", l => l with { Unit = newE.Unit });
                else items.Add(new ProposalItem($"review:unit:{m.Id:N}", ProposalKinds.ReviewAdjusted, m.Find, newE.Find, name.Number, name.Name, $"EBOM unit {oldE.Unit} to {newE.Unit}; the MBOM line uses {m.Unit} and is left as it is.", false));
            }
            if (!Equals(oldE.Eff, newE.Eff))
            {
                if (Equals(m.Eff, oldE.Eff)) Change(ProposalKinds.ChangeEffectivity, "eff", $"EBOM effectivity {oldE.Eff?.Describe() ?? "none"} to {newE.Eff?.Describe() ?? "none"}.", l => l with { Eff = newE.Eff });
                else items.Add(new ProposalItem($"review:eff:{m.Id:N}", ProposalKinds.ReviewAdjusted, m.Find, newE.Find, name.Number, name.Name,
                    $"EBOM effectivity {oldE.Eff?.Describe() ?? "none"} to {newE.Eff?.Describe() ?? "none"}; the MBOM line has {m.Eff?.Describe() ?? "none"} and is left as it is.", false));
            }
        }
        // New EBOM lines no MBOM line implements yet.
        foreach (var n in newLines.Where(n => !coveredNew.Contains(n.Id)))
        {
            var c = parts[n.Child];
            var id = $"add:{n.Id:N}";
            items.Add(new ProposalItem(id, ProposalKinds.AddLine, null, n.Find, c.Number, c.Name, $"New EBOM line: {MbomText.Qty(n.Quantity)} {n.Unit}.", true));
            var line = n;
            actions[id] = lines =>
            {
                var find = line.Find;
                if (lines.Any(l => l.Find == find && EffectivityRules.Conflict(l.Eff, line.Eff))) find = BomRules.NextFindNumber(lines.Select(l => l.Find));
                return new NewMbomLine(Guid.CreateVersion7(), line.Child, line.Quantity, line.Unit, find, line.Designators, null, false, MbomRules.DefaultSupplyType(c.PartType, c.Source), null, false,
                    line.Notes, line.Eff, DateTimeOffset.UtcNow, "", [new LinkInput(line.Id, LinkStatuses.Matched, null)]);
            };
        }
        var fromLabel = await MbomStore.IterationLabelAsync(conn, tx, oldDerived.Value, ct) ?? "";
        var proposal = new RegenerationProposal(head.Number, $"{head.PartNumber} {fromLabel}", $"{head.PartNumber} {target.Label}", oldDerived != target.IterationId,
            items.OrderBy(i => i.MbomFind ?? i.EbomFind ?? int.MaxValue).ThenBy(i => i.Kind, StringComparer.Ordinal).ToList(), preserved, relinked,
            Markings.AggregateBanner([head.Markings, .. parts.Values.Select(p => p.Markings)]), spec.Describe());
        return (new Plan(proposal, target, content, oldDerived, newByOld, actions, parts), null, null);
    }

    /// <summary>
    /// Applies the accepted proposal items (all actionable ones when <paramref name="acceptedIds"/> is null) into a NEW iteration of the latest revision.
    /// Links are carried over to the new EBOM lines, MBOM-only and manual lines stay, review items are left for the engineer. Does nothing (no new iteration) when
    /// the EBOM iteration is unchanged and nothing is accepted: regenerating twice is idempotent.
    /// </summary>
    public Task<Outcome<RegenerationResult>> ApplyRegenerationAsync(Subject subject, string number, EbomSpec? spec, IReadOnlySet<string>? acceptedIds, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<RegenerationResult>(subject, number, "mbom.regenerate_apply", correlationId, async (head, conn, tx) =>
        {
            if (head.State != MbomStates.InWork) return Step<RegenerationResult>.Conflict($"Revision {head.Revision} is {head.State}; revise a Released MBOM before regenerating it.");
            if (head.CheckoutUser is not null) return Step<RegenerationResult>.Conflict($"Checked out by {head.CheckoutUserName ?? head.CheckoutUser}; check in or undo the check-out first.");
            var (plan, error, hiddenReason) = await BuildPlanAsync(conn, tx, subject, head, spec ?? EbomSpec.Released, ct);
            if (plan is null) return hiddenReason is null ? Step<RegenerationResult>.Invalid(error!) : Step<RegenerationResult>.Hidden(error!, hiddenReason);
            var actionable = plan.Proposal.Items.Where(i => i.Actionable).Select(i => i.Id).ToList();
            var accepted = acceptedIds is null ? actionable : [.. actionable.Where(acceptedIds.Contains)];
            var unknown = acceptedIds?.Except(actionable).ToList() ?? [];
            if (unknown.Count > 0) return Step<RegenerationResult>.Invalid($"These proposal items do not exist (any more): {string.Join(", ", unknown)}. Request the proposal again.");
            if (accepted.Count == 0 && !plan.Proposal.EbomChanged)
                return Step<RegenerationResult>.Done(new RegenerationResult(head.Number, false, head.Label, 0, 0, ["The MBOM is already derived from this EBOM and nothing changed."]), ("created", "false"));

            // The new iteration: the latest content, links carried over, accepted changes applied.
            var lines = plan.Old.Select(l => l with
            {
                Links = [.. l.Links.Where(k => plan.NewByOldEbom.ContainsKey(k.EbomUsageId)).Select(k => k with { EbomUsageId = plan.NewByOldEbom[k.EbomUsageId] })],
            }).ToList();
            var now = clock.GetUtcNow();
            var who = BomService.DisplayId(subject);
            // The prepared actions address lines by their current ids; every stored row is immutable, so fresh ids are assigned afterwards.
            foreach (var id in accepted)
                if (plan.Actions[id](lines) is { } added) lines.Add(added with { CreatedAt = now, CreatedBy = who });
            lines = [.. lines.Select(l => l with { Id = Guid.CreateVersion7() })];
            if (lines.Count > MbomLimits.MaxLines) return Step<RegenerationResult>.Conflict($"An MBOM can have at most {MbomLimits.MaxLines} lines.");
            var problems = await MbomStore.ValidateLinksAsync(conn, tx, plan.Target.IterationId, lines, ct);
            if (problems.Count > 0) return Step<RegenerationResult>.Invalid([.. problems]);
            var next = head.LatestIteration + 1;
            string description;
            await using (var cmd = new NpgsqlCommand("SELECT description FROM mbom.iteration WHERE id = @i", conn, tx))
            {
                cmd.Parameters.AddWithValue("i", head.IterationId);
                description = (string)(await cmd.ExecuteScalarAsync(ct))!;
            }
            await MbomStore.WriteIterationAsync(conn, tx, head.RevisionId, next, description, $"Regenerated from EBOM {plan.Target.Label}: {accepted.Count} of {actionable.Count} proposed changes applied", plan.Target.IterationId,
                plan.Target.Description, lines, now, who, ct);
            var notices = plan.Proposal.Items.Where(i => !i.Actionable).Select(i => $"Review line {i.MbomFind?.ToString(CultureInfo.InvariantCulture) ?? "-"}: {i.Summary}").ToList();
            return Step<RegenerationResult>.Done(new RegenerationResult(head.Number, true, $"{head.Revision}.{Fmt(next)}", accepted.Count, actionable.Count - accepted.Count, notices),
                ("derivedFrom", plan.Target.Description), ("applied", Fmt(accepted.Count)), ("skipped", Fmt(actionable.Count - accepted.Count)), ("review", Fmt(notices.Count)), ("revision", $"{head.Revision}.{Fmt(next)}"));
        }, ct);
}
