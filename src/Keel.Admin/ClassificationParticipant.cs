using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Keel.Core;
using Npgsql;

namespace Keel.Admin;

/// <summary>
/// Classification is iteration-owned part data (ADR-0002, ADR-0022): the class and class-attribute values are copied into the check-out's working
/// copy, edited there, frozen into the new iteration at check-in and copied forward when a change notice revises the part. Registered as a
/// <see cref="IPartIterationParticipant"/>, so the part service runs it inside its own transaction and the rows commit or roll back with the part change.
/// </summary>
internal sealed class ClassificationParticipant : IPartIterationParticipant
{
    // The classification joins the content that part review signatures cover; the BOM does not (its hashes stay as they were).
    public bool CoveredByReviewSignature => true;

    public async Task OnCheckOutAsync(DbTransaction tx, Guid revisionId, Guid latestIterationId, CancellationToken ct)
    {
        var (conn, t) = Unwrap(tx);
        await using var cmd = AdminRunner.Cmd(conn, t,
            "INSERT INTO cfg.part_checkout_classification (revision_id, class_id, attributes) SELECT @rid, class_id, attributes FROM cfg.part_classification WHERE iteration_id = @it",
            ("rid", revisionId), ("it", latestIterationId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task OnCheckInAsync(DbTransaction tx, Guid revisionId, Guid newIterationId, CancellationToken ct)
    {
        var (conn, t) = Unwrap(tx);
        Guid classId;
        string attributes;
        await using (var cmd = AdminRunner.Cmd(conn, t, "SELECT class_id, attributes::text FROM cfg.part_checkout_classification WHERE revision_id = @rid", ("rid", revisionId)))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return; // not classified: nothing to freeze
            (classId, attributes) = (r.GetGuid(0), r.GetString(1));
        }
        var tree = await ClassificationService.LoadTreeAsync(conn, t, ct);
        var node = tree.ById(classId);
        if (node is null) throw new IterationVetoException("The part's class no longer exists. Choose another class before checking in.");
        // A class retired while the part was checked out must not be frozen into a new iteration; the user picks an active one.
        var unchanged = await SameAsLatestAsync(conn, t, revisionId, classId, attributes, ct);
        if (!node.IsActive && !unchanged)
            throw new IterationVetoException($"Class {node.Code} has been retired. Choose an active class for the part before checking in.");
        var (normalized, errors) = AttributeSpecs.Validate(tree.EffectiveSpecs(classId), JsonNode.Parse(attributes)!.AsObject());
        if (errors.Count > 0 && !unchanged) throw new IterationVetoException($"Classification attributes of class {node.Code} are invalid: {string.Join(" ", errors)}");
        if (errors.Count > 0) normalized = JsonNode.Parse(attributes)!.AsObject(); // frozen history is carried as stored when the class definition moved on
        await InsertFrozenAsync(conn, t, newIterationId, classId, node.Code, normalized, ct);
    }

    public async Task OnReviseAsync(DbTransaction tx, Guid fromIterationId, Guid newIterationId, CancellationToken ct)
    {
        var (conn, t) = Unwrap(tx);
        Guid classId;
        string code, attributes;
        await using (var cmd = AdminRunner.Cmd(conn, t, "SELECT class_id, class_code, attributes::text FROM cfg.part_classification WHERE iteration_id = @it", ("it", fromIterationId)))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return;
            (classId, code, attributes) = (r.GetGuid(0), r.GetString(1), r.GetString(2));
        }
        await InsertFrozenAsync(conn, t, newIterationId, classId, code, JsonNode.Parse(attributes)!.AsObject(), ct);
    }

    /// <summary>Stable text of the class and its values: this is what part review and change notice signatures cover.</summary>
    public async Task<string?> DescribeContentAsync(DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        var (conn, t) = Unwrap(tx);
        await using var cmd = AdminRunner.Cmd(conn, t, "SELECT class_code, attributes::text FROM cfg.part_classification WHERE iteration_id = @it", ("it", iterationId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? $"class:{r.GetString(0)};attributes:{r.GetString(1)}" : null;
    }

    private static async Task<bool> SameAsLatestAsync(NpgsqlConnection conn, NpgsqlTransaction t, Guid revisionId, Guid classId, string attributes, CancellationToken ct)
    {
        await using var cmd = AdminRunner.Cmd(conn, t,
            """
            SELECT pc.class_id = @c AND pc.attributes = @a::jsonb
            FROM cfg.part_classification pc JOIN plm.part_iteration i ON i.id = pc.iteration_id
            JOIN plm.part_revision r ON r.id = i.revision_id AND i.iteration = r.latest_iteration WHERE r.id = @rid
            """, ("c", classId), ("a", attributes), ("rid", revisionId));
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    internal static async Task InsertFrozenAsync(NpgsqlConnection conn, NpgsqlTransaction t, Guid iterationId, Guid classId, string code, JsonObject attributes, CancellationToken ct)
    {
        await using (var cmd = AdminRunner.Cmd(conn, t, "INSERT INTO cfg.part_classification (iteration_id, class_id, class_code, attributes) VALUES (@it, @c, @code, @a::jsonb)",
            ("it", iterationId), ("c", classId), ("code", code), ("a", attributes.ToJsonString())))
            await cmd.ExecuteNonQueryAsync(ct);
        // One typed row per value for the facet search (ranges over numbers, equality over text and flags).
        foreach (var (name, node) in attributes)
        {
            if (node is null) continue;
            string? txt = null;
            decimal? num = null;
            bool? flag = null;
            switch (node.GetValueKind())
            {
                case JsonValueKind.String: txt = node.GetValue<string>(); break;
                case JsonValueKind.Number: num = decimal.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture); break;
                case JsonValueKind.True or JsonValueKind.False: flag = node.GetValue<bool>(); break;
                default: continue;
            }
            await using var ins = AdminRunner.Cmd(conn, t, "INSERT INTO cfg.part_class_value (iteration_id, attr, txt, num, flag) VALUES (@it, @a, @txt, @num, @flag)",
                ("it", iterationId), ("a", name), ("txt", txt), ("num", num), ("flag", flag));
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    private static (NpgsqlConnection, NpgsqlTransaction) Unwrap(DbTransaction tx) =>
        tx is NpgsqlTransaction t && t.Connection is { } c ? (c, t) : throw new InvalidOperationException("The classification participant requires an Npgsql transaction.");
}
