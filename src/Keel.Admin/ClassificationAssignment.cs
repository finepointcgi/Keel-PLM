using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Admin;

/// <summary>A part's class and class-attribute values: the working copy for the check-out owner, otherwise the latest iteration.</summary>
public sealed record PartClassificationView(
    string? ClassCode, string? ClassName, IReadOnlyList<string> PathNames, JsonObject Attributes, IReadOnlyList<EffectiveAttribute> Effective, bool IsWorkingCopy, string IterationLabel);

public sealed partial class ClassificationService
{
    private const string PartObject = "part";

    private static AuditEntry PartEntry(Subject s, string action, AuditOutcome outcome, string number, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s),
        Action = action,
        Outcome = outcome,
        ObjectType = PartObject,
        ObjectId = number,
        CorrelationId = correlationId,
        Details = AuditDetails.Of(details),
    };

    private sealed record PartHead(Guid MasterId, Markings Markings, Guid RevisionId, string Revision, int Iteration, string? CheckoutUser);

    private static async Task<PartHead?> LoadHeadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string number, bool forUpdate, CancellationToken ct)
    {
        Guid masterId;
        Markings markings;
        await using (var cmd = AdminRunner.Cmd(conn, tx, $"SELECT m.id, {MarkingColumns.Select("m")} FROM plm.part_master m WHERE m.number = @n{(forUpdate ? " FOR UPDATE" : "")}", ("n", number)))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            (masterId, markings) = (r.GetGuid(0), MarkingColumns.Read(r, 1));
        }
        await using var rc = AdminRunner.Cmd(conn, tx,
            "SELECT r.id, r.revision, r.latest_iteration, c.user_id FROM plm.part_revision r LEFT JOIN plm.part_checkout c ON c.revision_id = r.id WHERE r.master_id = @m AND r.is_latest", ("m", masterId));
        await using var rr = await rc.ExecuteReaderAsync(ct);
        await rr.ReadAsync(ct);
        return new PartHead(masterId, markings, rr.GetGuid(0), rr.GetString(1), rr.GetInt32(2), rr.IsDBNull(3) ? null : rr.GetString(3));
    }

    /// <summary>
    /// The classification a user sees for a part. Read access is checked like for the part itself: a part the caller may not read is "not found" and the
    /// attempt is audited.
    /// </summary>
    public async Task<Outcome<PartClassificationView>> GetForPartAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await run.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await LoadHeadAsync(conn, tx, number, false, ct);
        if (head is null) return Outcome<PartClassificationView>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, PartEntry(subject, "part.classification_read", AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<PartClassificationView>.NotFound();
        }
        var tree = await LoadTreeAsync(conn, tx, ct);
        var working = head.CheckoutUser == subject.Id;
        Guid? classId = null;
        string attributes = "{}";
        if (working)
        {
            await using var cmd = AdminRunner.Cmd(conn, tx, "SELECT class_id, attributes::text FROM cfg.part_checkout_classification WHERE revision_id = @r", ("r", head.RevisionId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) (classId, attributes) = (r.GetGuid(0), r.GetString(1));
        }
        else
        {
            await using var cmd = AdminRunner.Cmd(conn, tx,
                "SELECT pc.class_id, pc.attributes::text FROM cfg.part_classification pc JOIN plm.part_iteration i ON i.id = pc.iteration_id WHERE i.revision_id = @r AND i.iteration = @n",
                ("r", head.RevisionId), ("n", head.Iteration));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) (classId, attributes) = (r.GetGuid(0), r.GetString(1));
        }
        await tx.CommitAsync(ct);
        var label = $"{head.Revision}.{head.Iteration}";
        if (classId is not { } id || tree.ById(id) is not { } node)
            return Outcome<PartClassificationView>.Ok(new PartClassificationView(null, null, [], new JsonObject(), [], working, label));
        return Outcome<PartClassificationView>.Ok(new PartClassificationView(node.Code, node.Name, [.. tree.Chain(id).Select(n => n.Name)],
            JsonNode.Parse(attributes)!.AsObject(), tree.Effective(id), working, label));
    }

    /// <summary>
    /// Assigns (or, with a null class code, clears) the class of a part in the check-out owner's working copy. The values are validated against the
    /// class's effective attribute set; nothing becomes part of an iteration until check-in. One class per part (ADM-10).
    /// </summary>
    public async Task<Outcome<PartClassificationView>> SetForPartAsync(Subject subject, string number, string? classCode, JsonObject? attributes, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await run.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await LoadHeadAsync(conn, tx, number, true, ct); // same row lock as every part mutation
        if (head is null) return Outcome<PartClassificationView>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, PartEntry(subject, "part.classify", AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<PartClassificationView>.NotFound();
        }
        var modify = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Modify);
        if (!modify.Permitted)
        {
            await audit.AppendAsync(tx, PartEntry(subject, "part.classify", AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", modify.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<PartClassificationView>.Forbidden(modify.Reasons);
        }
        if (head.CheckoutUser != subject.Id) return Outcome<PartClassificationView>.Conflict("Check the part out before changing its classification.");

        var tree = await LoadTreeAsync(conn, tx, ct);
        string? beforeCode = null;
        string beforeAttributes = "{}";
        await using (var cmd = AdminRunner.Cmd(conn, tx, "SELECT cn.code, w.attributes::text FROM cfg.part_checkout_classification w JOIN cfg.class_node cn ON cn.id = w.class_id WHERE w.revision_id = @r", ("r", head.RevisionId)))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) (beforeCode, beforeAttributes) = (r.GetString(0), r.GetString(1));
        }

        if (classCode is null)
        {
            await using var del = AdminRunner.Cmd(conn, tx, "DELETE FROM cfg.part_checkout_classification WHERE revision_id = @r", ("r", head.RevisionId));
            await del.ExecuteNonQueryAsync(ct);
            await audit.AppendAsync(tx, PartEntry(subject, "part.classify", AuditOutcome.Success, number, correlationId, ("before", beforeCode), ("after", null)), ct);
            await tx.CommitAsync(ct);
            return Outcome<PartClassificationView>.Ok(new PartClassificationView(null, null, [], new JsonObject(), [], true, $"{head.Revision}.{head.Iteration}"));
        }

        var node = tree.ByCode(classCode);
        if (node is null) return Outcome<PartClassificationView>.Invalid([$"Class '{classCode}' does not exist."]);
        if (!node.IsActive && node.Code != beforeCode) return Outcome<PartClassificationView>.Invalid([$"Class {node.Code} is retired."]);
        var (normalized, errors) = AttributeSpecs.Validate(tree.EffectiveSpecs(node.Id), attributes);
        if (errors.Count > 0) return Outcome<PartClassificationView>.Invalid(errors);

        await using (var up = AdminRunner.Cmd(conn, tx,
            """
            INSERT INTO cfg.part_checkout_classification (revision_id, class_id, attributes) VALUES (@r, @c, @a::jsonb)
            ON CONFLICT (revision_id) DO UPDATE SET class_id = @c, attributes = @a::jsonb
            """, ("r", head.RevisionId), ("c", node.Id), ("a", normalized.ToJsonString())))
            await up.ExecuteNonQueryAsync(ct);
        await audit.AppendAsync(tx, PartEntry(subject, "part.classify", AuditOutcome.Success, number, correlationId,
            ("before", beforeCode is null ? null : $"{beforeCode} {beforeAttributes}"), ("after", $"{node.Code} {normalized.ToJsonString()}")), ct);
        await tx.CommitAsync(ct);
        return Outcome<PartClassificationView>.Ok(new PartClassificationView(node.Code, node.Name, [.. tree.Chain(node.Id).Select(n => n.Name)], normalized, tree.Effective(node.Id), true, $"{head.Revision}.{head.Iteration}"));
    }
}
