using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Npgsql;

namespace Keel.Records;

/// <summary>
/// Evaluates the published retention schedule over the documents and parts and stores the result in <c>rec.classification</c> (a cache for the disposition queue).
/// Nothing destructive trusts the cache: execution evaluates each object again from its facts (<see cref="RecordsStore.LoadFactsByIdAsync"/>).
/// </summary>
public sealed class RetentionClassificationService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock)
{
    private const int PageSize = 1000;

    public async Task<Outcome<ClassifyReport>> ClassifyAsync(Subject s, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanManage(s))
        {
            await audit.AppendAsync(RecordsStore.Entry(s, "records.classify", AuditOutcome.Denied, "retention_schedule", null, corr, ("reasons", AccessPolicyReasons.Role)), ct);
            return Outcome<ClassifyReport>.Forbidden([AccessPolicyReasons.Role]);
        }
        return await RunAsync(ActorSnapshot.From(s), corr, ct);
    }

    /// <summary>The same run for the background job (no person; the system actor).</summary>
    public async Task<Outcome<ClassifyReport>> ClassifyAsSystemAsync(CancellationToken ct = default) => await RunAsync(ActorSnapshot.System, null, ct);

    private async Task<Outcome<ClassifyReport>> RunAsync(ActorSnapshot actor, string? corr, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var published = await RecordsStore.LoadPublishedAsync(conn, null, ct);
        if (published is null) return Outcome<ClassifyReport>.Conflict("No retention schedule is published yet.");
        var events = await RecordsStore.LoadProgramEventsAsync(conn, null, ct);
        var started = clock.GetUtcNow();
        int examined = 0, classified = 0, removed = 0;
        foreach (var kind in RecordKinds.All)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            var after = Guid.Empty;
            while (true)
            {
                var page = new List<ObjectFacts>(PageSize);
                await using (var cmd = new NpgsqlCommand(RecordsStore.FactsSelect(kind) + " WHERE x.id > @after ORDER BY x.id LIMIT @n", conn, tx))
                {
                    cmd.Parameters.AddWithValue("after", after);
                    cmd.Parameters.AddWithValue("n", PageSize);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) page.Add(RecordsStore.ReadFacts(r, kind));
                }
                if (page.Count == 0) break;
                after = page[^1].Id;
                examined += page.Count;
                var hits = page.Select(f => (f, a: RetentionRules.Assign(published.Rules, f, events))).Where(x => x.a is not null).ToList();
                classified += hits.Count;
                if (hits.Count > 0) await UpsertAsync(conn, tx, kind, published.Id, hits.Select(x => (x.f.Id, x.a!)).ToList(), started, ct);
            }
            await using (var del = new NpgsqlCommand("DELETE FROM rec.classification WHERE object_kind = @k AND evaluated_at < @t", conn, tx))
            {
                del.Parameters.AddWithValue("k", kind);
                del.Parameters.AddWithValue("t", started);
                removed += await del.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        var report = new ClassifyReport(published.Version, examined, classified, examined - classified, removed);
        await audit.AppendAsync(new AuditEntry
        {
            Actor = actor, Action = "records.classify", Outcome = AuditOutcome.Success, ObjectType = "retention_schedule", ObjectId = $"v{published.Version}", CorrelationId = corr,
            Details = AuditDetails.Of(("examined", examined.ToString(CultureInfo.InvariantCulture)), ("classified", classified.ToString(CultureInfo.InvariantCulture))),
        }, ct);
        return Outcome<ClassifyReport>.Ok(report);
    }

    private static async Task UpsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string kind, Guid scheduleId, IReadOnlyList<(Guid Id, ClassAssignment A)> rows, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO rec.classification (object_kind, object_id, schedule_id, class_code, trigger_event, trigger_date, eligible_on, disposition, evaluated_at)
            SELECT @k, u.id, @s, u.code, u.trig, u.tdate, u.due, u.disp, @at
            FROM unnest(@ids, @codes, @trigs, @tdates, @dues, @disps) AS u(id, code, trig, tdate, due, disp)
            ON CONFLICT (object_kind, object_id) DO UPDATE SET schedule_id = EXCLUDED.schedule_id, class_code = EXCLUDED.class_code, trigger_event = EXCLUDED.trigger_event,
                trigger_date = EXCLUDED.trigger_date, eligible_on = EXCLUDED.eligible_on, disposition = EXCLUDED.disposition, evaluated_at = EXCLUDED.evaluated_at
            """, conn, tx);
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("s", scheduleId);
        cmd.Parameters.AddWithValue("at", at);
        cmd.Parameters.AddWithValue("ids", rows.Select(r => r.Id).ToArray());
        cmd.Parameters.AddWithValue("codes", rows.Select(r => r.A.Code).ToArray());
        cmd.Parameters.AddWithValue("trigs", rows.Select(r => r.A.Trigger).ToArray());
        cmd.Parameters.AddWithValue("tdates", rows.Select(r => r.A.TriggerDate).ToArray());
        cmd.Parameters.AddWithValue("dues", rows.Select(r => r.A.EligibleOn).ToArray());
        cmd.Parameters.AddWithValue("disps", rows.Select(r => r.A.Disposition).ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
