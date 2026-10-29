using Npgsql;

namespace Keel.E2E;

/// <summary>
/// Synthetic suppliers, list entries and an issued request for the sourcing E2E tests and the accessibility audit, written with plain SQL as the application role
/// (the demo seed has no suppliers). Numbers start at 900001 so they never clash with numbers the tests create through the UI. Idempotent.
/// The portal users are the synthetic Keycloak users sup-alpha and sup-bravo, linked by their fixed subject ids.
/// </summary>
public static class SourcingSeed
{
    public const string SupplierA = "SUP-900001";
    public const string SupplierB = "SUP-900002";
    public const string Request = "RFQ-900001";
    public const string RequestForB = "RFQ-900002";
    public const string Entry = "SRC-900001";
    public const string AlphaSub = "3c0f2b9e-6a4d-5d7e-9a10-7e1a5b0c8d01";
    public const string BravoSub = "3c0f2b9e-6a4d-5d7e-9a10-7e1a5b0c8d02";

    public static async Task EnsureAsync(E2EFixture fx)
    {
        var open = fx.Manifest.OpenFastener;
        await using var conn = await fx.AppDb.OpenConnectionAsync();
        async Task Run(string sql, params (string, object)[] p)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v);
            await cmd.ExecuteNonQueryAsync();
        }
        foreach (var (number, name) in new[] { (SupplierA, "E2E Alpha Works"), (SupplierB, "E2E Bravo Works") })
            await Run(
                """
                INSERT INTO src.supplier (id, number, name, country, roles, status, is_foreign, clearance_profile, export_jurisdiction, cui_categories, distribution_statement, owning_org, export_authorizations, created_at, created_by)
                VALUES (gen_random_uuid(), @n, @name, 'USA', ARRAY['vendor', 'manufacturer'], 'approved', false, 'public-release', 'None', ARRAY[]::text[], 'A', 'ENG', ARRAY[]::text[], now(), 'e2e')
                ON CONFLICT (number) DO NOTHING
                """, ("n", number), ("name", name));
        await Run(
            """
            INSERT INTO src.supplier_certification (id, supplier_id, cert_type, certificate_number, issuer, issued_on, expires_on, created_at, created_by)
            SELECT gen_random_uuid(), s.id, 'AS9100', 'E2E-1', 'Registrar', CURRENT_DATE - 100, CURRENT_DATE + 300, now(), 'e2e' FROM src.supplier s
            WHERE s.number = @n AND NOT EXISTS (SELECT 1 FROM src.supplier_certification c WHERE c.supplier_id = s.id)
            """, ("n", SupplierA));
        await Run("INSERT INTO src.portal_user (user_sub, supplier_id, username, display_name, created_at, created_by) SELECT @sub, s.id, 'sup-alpha', 'Alpha Supplier', now(), 'e2e' FROM src.supplier s WHERE s.number = @n ON CONFLICT (user_sub) DO NOTHING", ("sub", AlphaSub), ("n", SupplierA));
        await Run("INSERT INTO src.portal_user (user_sub, supplier_id, username, display_name, created_at, created_by) SELECT @sub, s.id, 'sup-bravo', 'Bravo Supplier', now(), 'e2e' FROM src.supplier s WHERE s.number = @n ON CONFLICT (user_sub) DO NOTHING", ("sub", BravoSub), ("n", SupplierB));
        await Run(
            """
            INSERT INTO src.source_entry (id, number, kind, part_master_id, supplier_id, status, approval_state, vendor_part_number, lead_time_days, created_at, created_by)
            SELECT gen_random_uuid(), @e, 'avl', m.id, s.id, 'preferred', 'Active', 'ALPHA-5X16', 14, now(), 'e2e' FROM plm.part_master m, src.supplier s
            WHERE m.number = @p AND s.number = @s ON CONFLICT (number) DO NOTHING
            """, ("e", Entry), ("p", open), ("s", SupplierA));
        foreach (var (request, supplier, title) in new[] { (Request, SupplierA, "E2E fasteners for Alpha"), (RequestForB, SupplierB, "E2E fasteners for Bravo") })
        {
            await Run(
                """
                INSERT INTO src.request (id, number, kind, title, description, state, export_jurisdiction, cui_categories, distribution_statement, owning_org, export_authorizations, created_at, created_by, issued_at)
                VALUES (gen_random_uuid(), @r, 'rfq', @title, 'Please quote the listed part.', 'Issued', 'None', ARRAY[]::text[], 'A', 'ENG', ARRAY[]::text[], now(), 'e2e', now())
                ON CONFLICT (number) DO NOTHING
                """, ("r", request), ("title", title));
            await Run("INSERT INTO src.request_part (request_id, part_master_id, quantity, unit) SELECT r.id, m.id, 10, 'EA' FROM src.request r, plm.part_master m WHERE r.number = @r AND m.number = @p ON CONFLICT DO NOTHING", ("r", request), ("p", open));
            await Run("INSERT INTO src.request_recipient (request_id, supplier_id, state, added_at) SELECT r.id, s.id, 'Sent', now() FROM src.request r, src.supplier s WHERE r.number = @r AND s.number = @s ON CONFLICT DO NOTHING", ("r", request), ("s", supplier));
        }
    }
}
