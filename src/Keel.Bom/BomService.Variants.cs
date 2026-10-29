using Keel.Audit;
using Keel.Core;
using Keel.Core.Variants;
using Npgsql;
using NpgsqlTypes;

namespace Keel.Bom;

/// <summary>Checks a parsed condition against an option model (the variants module supplies it). Returns messages for each problem; empty means valid.</summary>
public delegate Task<IReadOnlyList<string>> ConditionValidator(OptionExpr expression, CancellationToken ct);

/// <summary>
/// Option usage conditions on EBOM lines (ADR-0039). A condition is set only on the check-out owner's working copy, like every other line edit; stored rows of an
/// iteration are immutable and are locked with their revision, so a condition on a released iteration changes only by revising the part (the new revision's
/// first iteration starts with the conditions of the one it replaces, <see cref="BomParticipant.OnReviseAsync"/>). The service never evaluates text: the condition
/// is parsed here, stored as canonical text plus the parsed tree, and evaluated in the database from the tree.
/// </summary>
public sealed partial class BomService
{
    /// <summary>
    /// Sets (<paramref name="condition"/> text) or clears (null or blank) the usage condition of a working-copy line. The text must parse; when
    /// <paramref name="validate"/> is given it must also pass (unknown options and values are refused). Audited with the old and the new canonical text.
    /// </summary>
    public Task<Outcome<WorkingBomLine>> SetLineConditionAsync(
        Subject subject, string number, Guid usageId, string? condition, ConditionValidator? validate, string? correlationId, CancellationToken ct = default) =>
        EditAsync<WorkingBomLine>(subject, number, "bom.set_condition", correlationId, async (ctx, conn, tx) =>
        {
            OptionExpr? expr = null;
            if (!string.IsNullOrWhiteSpace(condition))
            {
                var parsed = OptionExpressions.Parse(condition);
                if (!parsed.IsOk) return EditStep<WorkingBomLine>.Invalid([.. parsed.Errors.Select(e => $"Condition, position {e.Position + 1}: {e.Message}")]);
                expr = parsed.Expression;
                if (validate is not null && await validate(expr!, ct) is { Count: > 0 } problems) return EditStep<WorkingBomLine>.Invalid([.. problems]);
            }

            string? before;
            int find;
            await using (var read = new NpgsqlCommand("SELECT opt_cond, find_number FROM plm.bom_working_usage WHERE id = @id AND checkout_revision_id = @rid", conn, tx))
            {
                read.Parameters.AddWithValue("id", usageId);
                read.Parameters.AddWithValue("rid", ctx.RevisionId);
                await using var r = await read.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return EditStep<WorkingBomLine>.Missing();
                (before, find) = (r.IsDBNull(0) ? null : r.GetString(0), r.GetInt32(1));
            }
            await using (var upd = new NpgsqlCommand("UPDATE plm.bom_working_usage SET opt_cond = @c, opt_ast = @a WHERE id = @id AND checkout_revision_id = @rid", conn, tx))
            {
                upd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Text) { Value = (object?)expr?.ToCanonical() ?? DBNull.Value });
                upd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Jsonb) { Value = expr is null ? DBNull.Value : expr.ToJson().ToJsonString() });
                upd.Parameters.AddWithValue("id", usageId);
                upd.Parameters.AddWithValue("rid", ctx.RevisionId);
                await upd.ExecuteNonQueryAsync(ct);
            }
            var line = (await LoadWorkingLinesAsync(conn, tx, subject, ctx.RevisionId, usageId, new MarkingCache(), ct)).Single();
            return EditStep<WorkingBomLine>.Done(line, ("usage", usageId.ToString("D")), ("find", Fmt(find)), ("before", before), ("after", expr?.ToCanonical()));
        }, ct);

    /// <summary>
    /// What differs between two variants of one product (both resolved from the same revision, through the caller's read filter): lines used by one and not the other,
    /// and changed quantities. Restricted lines match on the same hidden part under the same parent, like every comparison.
    /// </summary>
    public async Task<Outcome<DeepComparison>> CompareVariantsAsync(
        Subject subject, string number, VariantSelection a, VariantSelection b, BomConfigSpec spec, int? depth, string? correlationId, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (depth is { } d && (d < 1 || d > BomLimits.MaxDepth)) errors.Add($"Depth must be between 1 and {BomLimits.MaxDepth}.");
        if (spec is not (BomConfigSpec.Latest or BomConfigSpec.LatestReleased)) errors.Add("Variants are compared at Latest or LatestReleased.");
        if (errors.Count > 0) return Outcome<DeepComparison>.Invalid(errors);
        return await WithRootAsync<DeepComparison>(subject, new BomQuery(number), Keel.Security.PlmAction.Read, "bom.compare_variants", correlationId, async (conn, tx, root0) =>
        {
            var revision = await CurrentRootRevisionAsync(conn, tx, root0.MasterId, spec, ct);
            var root = revision is null ? root0 : await LoadRootAsync(conn, tx, root0.Number, revision, null, ct) ?? root0;
            if (!Keel.Security.AccessPolicy.Evaluate(subject, root.Markings, Keel.Security.PlmAction.Read).Permitted) return (Outcome<DeepComparison>.NotFound(), []);
            var left = await ExplodeCoreAsync(conn, tx, subject, root, spec, depth, Seed.ForRoot(root), null, ct, a);
            var right = await ExplodeCoreAsync(conn, tx, subject, root, spec, depth, Seed.ForRoot(root), null, ct, b);
            if (left is null || right is null) return (TooLarge<DeepComparison>(), []);
            var result = Compared($"{root0.Number}: variant {a.Label} against variant {b.Label}", $"variant {a.Label}", $"variant {b.Label}", left, right, [root.Markings], out var details);
            return (result, [("part", root0.Number), ("left", a.Label), ("right", b.Label), .. details]);
        }, ct);
    }

    /// <summary>
    /// The effective condition of every line of an explosion that was resolved without a variant: its own condition together with those of its ancestors (a line is
    /// used only when every assembly above it is). Lines of an unreadable assembly never appear in an explosion, so nothing hidden is needed here.
    /// </summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<string>> ConditionChains(IReadOnlyList<BomLine> lines)
    {
        var byId = lines.ToDictionary(l => l.LineId);
        var result = new Dictionary<Guid, IReadOnlyList<string>>(lines.Count);
        foreach (var line in lines)
        {
            var chain = new List<string>();
            var cursor = line;
            var guard = 0;
            while (cursor is not null && guard++ < BomLimits.MaxDepth + 2)
            {
                if (cursor.Condition is { } c) chain.Add(c);
                cursor = cursor.ParentLineId is { } p && byId.TryGetValue(p, out var up) ? up : null;
            }
            result[line.LineId] = chain;
        }
        return result;
    }
}
