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
    /// <summary>An issued request to both suppliers with a tiered quote from each and its first line awarded to supplier A (9.6).</summary>
    public const string RequestQuoted = "RFQ-900003";
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
                VALUES (gen_random_uuid(), @n, @name, 'USA', ARRAY['vendor', 'manufacturer'], 'approved', false, 'orca-prime-contractor', 'None', ARRAY[]::text[], 'A', 'ENG', ARRAY[]::text[], now(), 'e2e')
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

        // 9.6: a request to both suppliers with tiers and an award, and a published scorecard for supplier A (SQL as the application role, like the rest of this seed).
        await Run(
            """
            INSERT INTO src.request (id, number, kind, title, description, state, due_on, export_jurisdiction, cui_categories, distribution_statement, owning_org, export_authorizations, created_at, created_by, issued_at)
            VALUES (gen_random_uuid(), @r, 'rfq', 'E2E tiered fasteners', 'Please quote 600 pieces with price tiers.', 'Issued', CURRENT_DATE + 14, 'None', ARRAY[]::text[], 'A', 'ENG', ARRAY[]::text[], now(), 'e2e', now())
            ON CONFLICT (number) DO NOTHING
            """, ("r", RequestQuoted));
        await Run("INSERT INTO src.request_part (request_id, part_master_id, quantity, unit, need_by) SELECT r.id, m.id, 600, 'EA', CURRENT_DATE + 60 FROM src.request r, plm.part_master m WHERE r.number = @r AND m.number = @p ON CONFLICT DO NOTHING", ("r", RequestQuoted), ("p", open));
        foreach (var supplier in new[] { SupplierA, SupplierB })
            await Run("INSERT INTO src.request_recipient (request_id, supplier_id, state, added_at) SELECT r.id, s.id, 'Responded', now() FROM src.request r, src.supplier s WHERE r.number = @r AND s.number = @s ON CONFLICT DO NOTHING", ("r", RequestQuoted), ("s", supplier));
        var quoteA = $"[{{\"part\":\"{open}\",\"currency\":\"USD\",\"leadTimeDays\":21,\"moq\":50,\"tiers\":[{{\"quantity\":100,\"unitPrice\":2.5}},{{\"quantity\":500,\"unitPrice\":2.1}}],\"note\":\"Lot pricing\",\"exception\":\"Cannot ship before 1 July\"}}]";
        var quoteB = $"[{{\"part\":\"{open}\",\"unitPrice\":2.2,\"currency\":\"USD\",\"leadTimeDays\":10}}]";
        foreach (var (supplier, sub, name, quote, valid) in new[] { (SupplierA, AlphaSub, "Alpha Supplier", quoteA, "CURRENT_DATE + 30"), (SupplierB, BravoSub, "Bravo Supplier", quoteB, "NULL") })
            await Run(
                $"""
                INSERT INTO src.response (id, request_id, supplier_id, seq, declined, message, quote, submitted_at, submitted_by_sub, submitted_by_name, valid_until)
                SELECT gen_random_uuid(), r.id, s.id, 1, false, 'Pleased to quote.', @q::jsonb, now(), @sub, @name, {valid} FROM src.request r, src.supplier s
                WHERE r.number = @r AND s.number = @s ON CONFLICT (request_id, supplier_id, seq) DO NOTHING
                """, ("q", quote), ("sub", sub), ("name", name), ("r", RequestQuoted), ("s", supplier));
        await Run(
            """
            INSERT INTO src.award (id, request_id, part_master_id, supplier_id, response_id, quantity, unit_price, currency, lead_time_days, entry_id, entry_created, awarded_at, awarded_by)
            SELECT gen_random_uuid(), r.id, m.id, s.id, p.id, 600, 2.1, 'USD', 21, e.id, false, now(), 'e2e'
            FROM src.request r, plm.part_master m, src.supplier s, src.response p, src.source_entry e
            WHERE r.number = @r AND m.number = @p AND s.number = @s AND p.request_id = r.id AND p.supplier_id = s.id AND e.number = @e
            ON CONFLICT DO NOTHING
            """, ("r", RequestQuoted), ("p", open), ("s", SupplierA), ("e", Entry));
        await Run("INSERT INTO src.scorecard_publication (id, supplier_id, as_of, formula_version, payload, published_at, published_by) SELECT gen_random_uuid(), s.id, CURRENT_DATE, 1, @payload::jsonb, now(), 'e2e' FROM src.supplier s WHERE s.number = @s AND NOT EXISTS (SELECT 1 FROM src.scorecard_publication x WHERE x.supplier_id = s.id)",
            ("payload", ScorecardPayload()), ("s", SupplierA));
    }

    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>The scorecard of supplier A as the buyer published it: the same shape as <c>ScorecardView</c> (counts and rates only).</summary>
    private static string ScorecardPayload()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var trend = new List<object>();
        for (var i = 11; i >= 1; i--)
        {
            var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-i);
            var end = new DateOnly(first.Year, first.Month, DateTime.DaysInMonth(first.Year, first.Month));
            trend.Add(new { asOf = end.ToString("yyyy-MM-dd"), score = i > 9 ? (decimal?)null : (decimal?)(i > 4 ? 100m : 92.5m) });
        }
        trend.Add(new { asOf = today.ToString("yyyy-MM-dd"), score = (decimal?)92.5m });
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            asOf = today.ToString("yyyy-MM-dd"),
            score = 92.5m,
            band = "Good",
            components = new object[]
            {
                new { key = "ncr", label = "Nonconformances", weight = 40, available = true, score = 90m, measure = "1 in the last 12 months (0 critical, 0 major, 1 minor), 1 points." },
                new { key = "capa", label = "Corrective actions", weight = 10, available = true, score = 100m, measure = "No corrective action is open." },
                new { key = "rfq", label = "Responsiveness to requests", weight = 20, available = true, score = 100m, measure = "2 of 2 requests answered on time; first answer after 1.5 days on average." },
                new { key = "certs", label = "Certifications", weight = 20, available = true, score = 100m, measure = "1 of 1 certification is valid." },
                new { key = "delivery", label = "On-time delivery", weight = 10, available = false, score = (decimal?)null, measure = "Not measured: Keel records no deliveries yet." },
            },
            trend,
            formula = new[]
            {
                "Score = the weighted average of the measured components (0 to 100). A component without a basis is not measured and its weight is left out.",
                "Nonconformances (weight 40): 100 minus 10 for each point, over the nonconformances opened in the last 12 months and linked to the supplier. A minor one is 1 point, a major one 3, a critical one 8. Not below 0.",
                "On-time delivery (weight 10): not measured. Keel records no deliveries or receipts yet, so this component is shown but never counted.",
                "Bands: 85 and above is good, 65 to under 85 is to watch, under 65 is poor.",
            },
            formulaVersion = 1,
            banner = "UNCLASSIFIED",
        }, WebJson);
    }
}
