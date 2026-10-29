using Npgsql;

namespace Keel.E2E;

/// <summary>
/// A published retention schedule with one class, a legal hold on the system specification and a draft disposition request, for the records E2E tests and the accessibility audit,
/// written with plain SQL as the application role (the demo seed has no records data). The schedule goes through its real status changes (draft, in review, published); the
/// signatures of a real publication are covered by the integration tests. Version and numbers start at 900 and 900001 so they never clash with what tests create. Idempotent.
/// </summary>
public static class RecordsSeed
{
    public const int ScheduleVersion = 900;
    public const string Hold = "LH-900001";
    public const string Request = "DR-900001";

    public static async Task EnsureAsync(E2EFixture fx)
    {
        await using var conn = await fx.AppDb.OpenConnectionAsync();
        async Task<long> Scalar(string sql, params (string, object)[] p)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }
        async Task Run(string sql, params (string, object)[] p) => _ = await Scalar(sql + "; SELECT 1", p);
        if (await Scalar("SELECT count(*) FROM rec.schedule WHERE version = @v", ("v", ScheduleVersion)) > 0) return;
        var spec = fx.Manifest.SystemSpecification;
        var id = Guid.NewGuid();
        await Run("INSERT INTO rec.schedule (id, version, status, title, owning_org, created_at, created_by) VALUES (@id, @v, 'draft', 'Seeded schedule', 'ENG', now(), 'e2e')", ("id", id), ("v", ScheduleVersion));
        await Run(
            """
            INSERT INTO rec.record_class (id, schedule_id, code, name, priority, match_kinds, match_types, match_programs, match_jurisdictions, match_cui, match_statements, retention_months, trigger_event, disposition, citation)
            VALUES (gen_random_uuid(), @id, 'E2E-SPEC', 'Specifications', 100, ARRAY['document'], ARRAY['Specification'], ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 600, 'release', 'review', 'Policy 7.1')
            """, ("id", id));
        await Run("UPDATE rec.schedule SET status = 'in_review' WHERE id = @id", ("id", id));
        await Run("UPDATE rec.schedule SET status = 'published', published_at = now(), published_by = 'e2e' WHERE id = @id", ("id", id));
        await Run("INSERT INTO rec.program_event (program, event, occurred_on, recorded_at, recorded_by) VALUES ('ORCA', 'contract_end', CURRENT_DATE - 30, now(), 'e2e')");
        await Run("INSERT INTO rec.hold (id, number, name, reason, scope_kind, scope, owning_org, placed_at, placed_by) VALUES (@id, @n, 'E2E matter', 'Seeded for the browser tests', 'objects', '{\"kind\":\"objects\",\"objects\":[]}', 'ENG', now(), 'e2e')", ("id", Guid.NewGuid()), ("n", Hold));
        await Run("INSERT INTO rec.hold_member (hold_id, object_kind, object_id, added_at, added_by) SELECT h.id, 'document', m.id, now(), 'placement' FROM rec.hold h, plm.document_master m WHERE h.number = @h AND m.number = @d", ("h", Hold), ("d", spec));
        await Run(
            """
            INSERT INTO rec.disposition (id, number, status, note, created_at, created_by, export_jurisdiction, eccn, usml_category, cui_categories, distribution_statement, controlling_office, distribution_reason, program, owning_org, export_authorizations)
            SELECT gen_random_uuid(), @n, 'draft', 'Seeded request', now(), 'e2e', m.export_jurisdiction, m.eccn, m.usml_category, m.cui_categories, m.distribution_statement, m.controlling_office, m.distribution_reason, m.program, m.owning_org, m.export_authorizations
            FROM plm.part_master m WHERE m.number = @p
            """, ("n", Request), ("p", fx.Manifest.OpenFastener));
        await Run(
            """
            INSERT INTO rec.disposition_item (id, disposition_id, object_kind, object_id, object_number, class_code, schedule_version, action, eligible_on)
            SELECT gen_random_uuid(), d.id, 'part', m.id, m.number, 'E2E-SPEC', @v, 'review', CURRENT_DATE FROM rec.disposition d, plm.part_master m WHERE d.number = @n AND m.number = @p
            """, ("v", ScheduleVersion), ("n", Request), ("p", fx.Manifest.OpenFastener));
    }
}
