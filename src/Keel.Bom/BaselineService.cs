using System.Globalization;
using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;
using NpgsqlTypes;

namespace Keel.Bom;

/// <summary>
/// Baselines (ADR-0021): named, numbered, immutable snapshots of an exploded structure. Every call evaluates the access policy (ADR-0004) and
/// writes its audit event (ADR-0005) in the same transaction, denials included. Creating needs Read on every item and the roles that may modify;
/// the baseline's markings must dominate every item and pinned document. Mutations serialize on <c>SELECT ... FOR UPDATE</c> of the baseline row.
/// </summary>
public sealed partial class BaselineService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, IDocumentPinProvider? documents = null, IDomainEventSink? events = null)
{
    public const string ObjectType = "baseline";
    private const string DraftState = BaselineStates.Draft;

    private sealed record Step<T>(OutcomeKind Kind, IReadOnlyList<string> Errors, T? Value, (string, string?)[] Details, bool Denied = false)
    {
        public static Step<T> Done(T value, params (string, string?)[] details) => new(OutcomeKind.Ok, [], value, details);
        public static Step<T> Invalid(params string[] errors) => new(OutcomeKind.Invalid, errors, default, []);
        public static Step<T> Conflict(string message) => new(OutcomeKind.Conflict, [message], default, []);
        public static Step<T> Refused(string message, string reason) => new(OutcomeKind.Invalid, [message], default, [("reasons", reason)], true);
    }

    // ---------------------------------------------------------------- types

    public async Task<IReadOnlyList<BaselineTypeInfo>> ListTypesAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await TypesAsync(conn, null, ct);
    }

    private static async Task<IReadOnlyList<BaselineTypeInfo>> TypesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var list = new List<BaselineTypeInfo>();
        await using var cmd = new NpgsqlCommand("SELECT type, label, requires_signoff FROM cfg.baseline_type ORDER BY sort_order", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new BaselineTypeInfo(r.GetString(0), r.GetString(1), r.GetBoolean(2)));
        return list;
    }

    // ---------------------------------------------------------------- create

    public async Task<Outcome<BaselineDetail>> CreateAsync(Subject subject, CreateBaselineCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        var errors = ValidateCommand(cmd);
        if (errors.Count > 0) return Outcome<BaselineDetail>.Invalid(errors);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var number = cmd.RootNumber.Trim();

        var root0 = await BomService.LoadRootAsync(conn, tx, number, cmd.RootRevision, null, ct);
        if (root0 is null) return Outcome<BaselineDetail>.NotFound();
        var read = AccessPolicy.Evaluate(subject, root0.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "baseline.create", AuditOutcome.Denied, null, correlationId, ("root", number), ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<BaselineDetail>.NotFound();
        }
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Modify))
        {
            await audit.AppendAsync(tx, Entry(subject, "baseline.create", AuditOutcome.Denied, null, correlationId, ("root", number), ("reasons", "role.modify")), ct);
            await tx.CommitAsync(ct);
            return Outcome<BaselineDetail>.Forbidden(["role.modify"]);
        }

        var step = await CreateCoreAsync(conn, tx, subject, cmd, root0, correlationId, ct);
        if (step.Kind != OutcomeKind.Ok)
        {
            await tx.RollbackAsync(ct);
            await audit.AppendAsync(step.Denied
                ? Entry(subject, "baseline.create", AuditOutcome.Denied, null, correlationId, [("root", number), .. step.Details])
                : Entry(subject, "baseline.create", AuditOutcome.Failure, null, correlationId, ("root", number), ("error", string.Join(" ", step.Errors)), ("kind", step.Kind.ToString())), ct);
            return step.Kind == OutcomeKind.Conflict ? Outcome<BaselineDetail>.Conflict(step.Errors[0]) : Outcome<BaselineDetail>.Invalid(step.Errors);
        }
        var detail = step.Value!;
        await audit.AppendAsync(tx, Entry(subject, "baseline.create", AuditOutcome.Success, detail.Summary.Number, correlationId, step.Details), ct);
        await tx.CommitAsync(ct);
        return Outcome<BaselineDetail>.Ok(detail);
    }

    private static List<string> ValidateCommand(CreateBaselineCommand cmd)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(cmd.RootNumber)) errors.Add("Name the part to baseline.");
        if (string.IsNullOrWhiteSpace(cmd.Name) || cmd.Name.Trim().Length > 200) errors.Add("The name is required and at most 200 characters.");
        if ((cmd.Purpose?.Length ?? 0) > BaselineLimits.MaxPurposeLength) errors.Add($"The purpose must be at most {BaselineLimits.MaxPurposeLength} characters.");
        if (cmd.Milestone is { } m && (m.Trim().Length is < 1 or > 40)) errors.Add("The milestone must be 1 to 40 characters (for example PDR, CDR, FCA, PCA).");
        if (cmd.Spec is not (BomConfigSpec.Latest or BomConfigSpec.LatestReleased or BomConfigSpec.Effectivity))
            errors.Add("A baseline is created from Latest, LatestReleased or Effectivity.");
        if (cmd.Spec == BomConfigSpec.Effectivity)
        {
            if (cmd.Effectivity is null) errors.Add("Give the serial number, lot or date to baseline.");
            else errors.AddRange(cmd.Effectivity.Validate());
        }
        return errors;
    }

    private async Task<Step<BaselineDetail>> CreateCoreAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, CreateBaselineCommand cmd, BomService.Root root0, string? correlationId, CancellationToken ct)
    {
        var types = await TypesAsync(conn, tx, ct);
        var type = types.FirstOrDefault(t => t.Type == cmd.Type);
        if (type is null) return Step<BaselineDetail>.Invalid($"Baseline type must be one of: {string.Join(", ", types.Select(t => t.Type))}.");
        if (cmd.RequireSignoff == false && type.RequiresSignoff)
            return Step<BaselineDetail>.Invalid($"A {type.Label.ToLowerInvariant()} must be established through the review workflow with e-signatures; it cannot be created without sign-off.");
        var signoff = type.RequiresSignoff || cmd.RequireSignoff == true;

        // The root revision follows the spec unless one was named.
        var root = root0;
        if (cmd.RootRevision is null)
        {
            string? revision = cmd.Spec switch
            {
                BomConfigSpec.LatestReleased => await ReleasedRevisionAsync(conn, tx, root0.MasterId, ct),
                BomConfigSpec.Effectivity => await BomService.ResolveRootRevisionAsync(conn, tx, root0.MasterId, cmd.Effectivity!, ct),
                _ => root0.Revision,
            };
            if (revision is null) return Step<BaselineDetail>.Invalid($"{root0.Number} has no released revision to baseline.");
            if (revision != root0.Revision) root = await BomService.LoadRootAsync(conn, tx, root0.Number, revision, null, ct) ?? root0;
        }
        if (root.PartType != "Assembly") return Step<BaselineDetail>.Invalid($"{root.Number} is a {root.PartType}; baseline an assembly (its structure is what a baseline freezes).");
        if (cmd.ReleasedOnly && root.State != "Released")
            return Step<BaselineDetail>.Invalid($"Released-only: {root.Number} revision {root.Revision} is not Released.");

        var data = await BomService.ExplodeCoreAsync(conn, tx, subject, root, cmd.Spec, null, BomService.Seed.ForRoot(root), cmd.Effectivity, ct);
        if (data is null) return Step<BaselineDetail>.Invalid($"The structure has more than {BomLimits.MaxExplosionLines:N0} lines; a baseline of that size is not supported.");
        if (data.Lines.Count == 0) return Step<BaselineDetail>.Invalid($"{root.Number} has no structure under this configuration; there is nothing to baseline.");

        // A baseline needs Read on every item. Items the creator cannot read are only counted, never named (A4).
        if (data.Redacted > 0)
            return Step<BaselineDetail>.Refused(
                $"{data.Redacted} item{(data.Redacted == 1 ? "" : "s")} in this structure {(data.Redacted == 1 ? "is" : "are")} not accessible to you. A baseline needs read access to every item, so it cannot be created.",
                $"items.unreadable:{data.Redacted}");
        var unresolved = data.Lines.Where(l => l.Unresolved).Select(l => l.Number ?? l.Path).Distinct().ToList();
        if (unresolved.Count > 0)
            return Step<BaselineDetail>.Invalid($"No released revision exists for: {Join(unresolved)}. Release them or baseline the latest structure.");
        var circular = data.Lines.Where(l => l.Circular).Select(l => l.Number ?? l.Path).Distinct().ToList();
        if (circular.Count > 0) return Step<BaselineDetail>.Invalid($"The structure contains a circular reference at: {Join(circular)}.");
        if (cmd.ReleasedOnly)
        {
            var notReleased = data.Lines.Where(l => l.State != "Released").Select(l => $"{l.Number} ({l.Revision}.{l.Iteration}, {l.StateLabel})").Distinct().ToList();
            if (notReleased.Count > 0) return Step<BaselineDetail>.Invalid($"Released-only: these items are not Released: {Join(notReleased)}.");
        }

        // Pin linked documents (readable ones only; unreadable ones refuse like items do).
        var pinned = new List<PinnedDocument>();
        if (cmd.IncludeDocuments)
        {
            if (documents is null) throw new NotSupportedException("Pinning documents needs the document provider (IDocumentPinProvider), which this host did not register.");
            var iterations = new List<Guid> { root.IterationId };
            iterations.AddRange(data.Identities.Where(i => i is not null).Select(i => i!.IterationId));
            pinned.AddRange(await documents.ResolveAsync(conn, tx, subject, [.. iterations.Distinct()], ct));
            var hiddenDocs = pinned.Where(p => !p.Readable).Select(p => p.DocumentRevisionId).Distinct().Count();
            if (hiddenDocs > 0)
                return Step<BaselineDetail>.Refused(
                    $"{hiddenDocs} linked document{(hiddenDocs == 1 ? " is" : "s are")} not accessible to you. A baseline that pins documents needs read access to every one of them.", $"documents.unreadable:{hiddenDocs}");
        }

        // Markings dominate every item and pinned document, and the creator confirms what the cover is.
        var itemMarkings = data.Lines.Where(l => l.Markings is not null).Select(l => l.Markings!).Append(root.Markings).Concat(pinned.Where(p => p.Markings is not null).Select(p => p.Markings!)).Distinct().ToList();
        var requested = cmd.Markings ?? root.Markings;
        var notices = new List<string>();
        var final = requested;
        var gaps = itemMarkings.SelectMany(m => MarkingDominance.Gaps(requested, m)).Distinct().ToList();
        if (gaps.Count > 0)
        {
            var cover = MarkingDominance.MinimalCover(requested, itemMarkings);
            if (!cover.IsSuccess) return Step<BaselineDetail>.Invalid([.. cover.Errors]);
            if (!cmd.ConfirmRaiseMarkings)
                return Step<BaselineDetail>.Invalid($"The markings {requested.BannerText()} do not cover every item of the baseline. The baseline must be at least as restrictive as what it lists. Confirm to use the minimal cover {cover.Value.BannerText()}.");
            final = cover.Value;
            notices.Add($"The markings of the baseline were raised from {requested.BannerText()} to {final.BannerText()} because its items are more restrictive.");
        }
        if (!AccessPolicy.Evaluate(subject, final, PlmAction.Modify).Permitted)
            return Step<BaselineDetail>.Invalid($"Markings {final.BannerText()} would lock you out of the baseline you are creating.");

        // ---- persist
        var now = SignatureTime(clock.GetUtcNow());
        var id = Guid.CreateVersion7();
        string numberText;
        await using (var seq = new NpgsqlCommand("SELECT 'BL-' || lpad(nextval('cfg.baseline_seq')::text, 6, '0')", conn, tx))
            numberText = (string)(await seq.ExecuteScalarAsync(ct))!;

        var items = new List<StoredItem> { new(0, null, 0, "", null, 1m, "EA", 1m, root.MasterId, root.RevisionId, root.IterationId, "", null, null) };
        var seqByPath = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<(StoredItem Item, string[] Designators)> { (items[0], []) };
        for (var i = 0; i < data.Lines.Count; i++)
        {
            var l = data.Lines[i];
            var identity = data.Identities[i] ?? throw new InvalidOperationException("A readable line without identity.");
            var seq = i + 1;
            seqByPath[l.Path] = seq;
            var cut = l.Path.LastIndexOf('.');
            var parent = cut < 0 ? 0 : seqByPath[l.Path[..cut]];
            var item = new StoredItem(seq, parent, l.Level, l.Path, l.FindNumber, l.Quantity, l.Unit, l.ExtendedQuantity, identity.MasterId, identity.RevisionId, identity.IterationId,
                string.Join(',', l.ReferenceDesignators), l.Notes, BaselineStore.EffectivityText(l.Effectivity) is { Length: > 0 } e ? e : null);
            rows.Add((item, [.. l.ReferenceDesignators]));
            items.Add(item);
        }
        // Items of one iteration share its documents: pin them per distinct (item iteration, link), attributed to the first item that uses it.
        var docs = new List<StoredDocument>();
        var firstItemOf = new Dictionary<Guid, int>();
        foreach (var it in items) firstItemOf.TryAdd(it.IterationId, it.Seq);
        foreach (var p in pinned.OrderBy(p => firstItemOf.GetValueOrDefault(p.PartIterationId, int.MaxValue)).ThenBy(p => p.LinkType, StringComparer.Ordinal).ThenBy(p => p.DocumentMasterId))
            if (firstItemOf.TryGetValue(p.PartIterationId, out var itemSeq))
                docs.Add(new StoredDocument(docs.Count + 1, itemSeq, p.LinkType, p.DocumentMasterId, p.DocumentRevisionId));
        var hash = BaselineStore.ContentHash(root.MasterId, root.RevisionId, root.IterationId, cmd.Spec.ToString(), items, docs);

        await using (var ins = new NpgsqlCommand(
            $"""
            INSERT INTO cfg.baseline (id, number, name, purpose, milestone, baseline_type, signoff, root_master_id, root_revision_id, root_iteration_id, spec, released_only,
                                      include_documents, spec_context, spec_serial, spec_lot, spec_day, item_count, document_count, content_sha256, lifecycle_state,
                                      {MarkingColumns.InsertColumns}, created_at, created_by, state_changed_at, state_changed_by)
            VALUES (@id, @number, @name, @purpose, @milestone, @type, @signoff, @rm, @rr, @ri, @spec, @ro, @docs, @sctx, @sserial, @slot, @sday, @items, @doccount, @hash, 'Draft',
                    {MarkingColumns.InsertValues}, @at, @by, @at, @by)
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("number", numberText);
            ins.Parameters.AddWithValue("name", cmd.Name.Trim());
            ins.Parameters.AddWithValue("purpose", cmd.Purpose?.Trim() ?? "");
            ins.Parameters.Add(new NpgsqlParameter("milestone", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(cmd.Milestone) ? DBNull.Value : cmd.Milestone.Trim() });
            ins.Parameters.AddWithValue("type", type.Type);
            ins.Parameters.AddWithValue("signoff", signoff);
            ins.Parameters.AddWithValue("rm", root.MasterId);
            ins.Parameters.AddWithValue("rr", root.RevisionId);
            ins.Parameters.AddWithValue("ri", root.IterationId);
            ins.Parameters.AddWithValue("spec", cmd.Spec.ToString());
            ins.Parameters.AddWithValue("ro", cmd.ReleasedOnly);
            ins.Parameters.AddWithValue("docs", cmd.IncludeDocuments);
            ins.Parameters.Add(new NpgsqlParameter("sctx", NpgsqlDbType.Text) { Value = (object?)cmd.Effectivity?.Context ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("sserial", NpgsqlDbType.Bigint) { Value = (object?)cmd.Effectivity?.Serial ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("slot", NpgsqlDbType.Bigint) { Value = (object?)cmd.Effectivity?.Lot ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("sday", NpgsqlDbType.Bigint) { Value = (object?)cmd.Effectivity?.Day ?? DBNull.Value });
            ins.Parameters.AddWithValue("items", items.Count);
            ins.Parameters.AddWithValue("doccount", docs.Count);
            ins.Parameters.AddWithValue("hash", hash);
            foreach (var (k, v) in MarkingColumns.Parameters(final)) ins.Parameters.AddWithValue(k, v);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", BomService.DisplayId(subject));
            await ins.ExecuteNonQueryAsync(ct);
        }

        await using (var copy = await conn.BeginBinaryImportAsync(
            "COPY cfg.baseline_item (baseline_id, seq, parent_seq, level, path, find_number, quantity, unit, ext_quantity, master_id, revision_id, iteration_id, reference_designators, notes, eff_kind, eff_context, eff_from, eff_to) FROM STDIN (FORMAT BINARY)", ct))
        {
            var byseq = data.Lines.Select((l, i) => (l, i)).ToDictionary(x => x.i + 1, x => x.l);
            foreach (var (item, designators) in rows)
            {
                await copy.StartRowAsync(ct);
                var eff = item.Seq == 0 ? null : byseq[item.Seq].Effectivity;
                await copy.WriteAsync(id, NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(item.Seq, NpgsqlDbType.Integer, ct);
                if (item.ParentSeq is { } ps) await copy.WriteAsync(ps, NpgsqlDbType.Integer, ct); else await copy.WriteNullAsync(ct);
                await copy.WriteAsync(item.Level, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(item.Path, NpgsqlDbType.Text, ct);
                if (item.Find is { } f) await copy.WriteAsync(f, NpgsqlDbType.Integer, ct); else await copy.WriteNullAsync(ct);
                await copy.WriteAsync(item.Quantity, NpgsqlDbType.Numeric, ct);
                await copy.WriteAsync(item.Unit, NpgsqlDbType.Text, ct);
                await copy.WriteAsync(item.Extended, NpgsqlDbType.Numeric, ct);
                await copy.WriteAsync(item.MasterId, NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(item.RevisionId, NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(item.IterationId, NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(designators, NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
                if (item.Notes is { } n) await copy.WriteAsync(n, NpgsqlDbType.Text, ct); else await copy.WriteNullAsync(ct);
                if (eff is null) { await copy.WriteNullAsync(ct); await copy.WriteNullAsync(ct); await copy.WriteNullAsync(ct); await copy.WriteNullAsync(ct); }
                else
                {
                    await copy.WriteAsync(eff.Kind, NpgsqlDbType.Text, ct);
                    if (eff.Context is { } c) await copy.WriteAsync(c, NpgsqlDbType.Text, ct); else await copy.WriteNullAsync(ct);
                    if (eff.From is { } from) await copy.WriteAsync(from, NpgsqlDbType.Bigint, ct); else await copy.WriteNullAsync(ct);
                    if (eff.To is { } to) await copy.WriteAsync(to, NpgsqlDbType.Bigint, ct); else await copy.WriteNullAsync(ct);
                }
            }
            await copy.CompleteAsync(ct);
        }
        foreach (var d in docs)
        {
            await using var dc = new NpgsqlCommand(
                "INSERT INTO cfg.baseline_document (baseline_id, seq, item_seq, link_type, document_master_id, document_revision_id) VALUES (@b, @s, @i, @t, @dm, @dr)", conn, tx);
            dc.Parameters.AddWithValue("b", id);
            dc.Parameters.AddWithValue("s", d.Seq);
            dc.Parameters.AddWithValue("i", d.ItemSeq);
            dc.Parameters.AddWithValue("t", d.LinkType);
            dc.Parameters.AddWithValue("dm", d.MasterId);
            dc.Parameters.AddWithValue("dr", d.RevisionId);
            await dc.ExecuteNonQueryAsync(ct);
        }

        var head = (await BaselineStore.LoadByIdAsync(conn, tx, id, false, ct))!;
        var detail = (await LoadDetailAsync(conn, tx, subject, head, notices, ct))!;
        return Step<BaselineDetail>.Done(detail,
            ("root", $"{root.Number} {root.Revision}.{root.Iteration}"), ("spec", head.SpecText), ("type", head.Type), ("signoff", signoff.ToString()),
            ("items", items.Count.ToString(CultureInfo.InvariantCulture)), ("documents", docs.Count.ToString(CultureInfo.InvariantCulture)),
            ("banner", final.BannerText()), ("markingsRaised", (gaps.Count > 0).ToString()), ("contentSha256", hash));
    }

    private static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 10 ? string.Join(", ", list) : string.Join(", ", list.Take(10)) + $" and {list.Count - 10} more";
    }

    private static async Task<string?> ReleasedRevisionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid master, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT revision FROM plm.part_revision WHERE master_id = @m AND lifecycle_state = 'Released' ORDER BY length(revision) DESC, revision DESC LIMIT 1", conn, tx);
        cmd.Parameters.AddWithValue("m", master);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    /// <summary>Timestamps are stored with microsecond precision.</summary>
    private static DateTimeOffset SignatureTime(DateTimeOffset t) => new(t.UtcTicks - t.UtcTicks % 10, TimeSpan.Zero);

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
}
