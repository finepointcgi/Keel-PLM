using Npgsql;

namespace Keel.E2E;

/// <summary>
/// Synthetic declarations for the compliance E2E tests and the accessibility audit, written with plain SQL as the application role (the demo seed has none). The open
/// fastener gets a verified declaration with a conflict-free tin line; the gimbal bracket (an ITAR part) gets one with lead above the RoHS threshold. Idempotent.
/// </summary>
public static class ComplianceSeed
{
    public const string FastenerDeclaration = "CD-900001";
    public const string BracketDeclaration = "CD-900002";

    public static async Task EnsureAsync(E2EFixture fx)
    {
        await using var conn = await fx.AppDb.OpenConnectionAsync();
        async Task Run(string sql, params (string, object)[] p)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v);
            await cmd.ExecuteNonQueryAsync();
        }
        async Task Declare(string number, string part, string status, string cas, decimal percent, bool? conflictFree, bool verified)
        {
            await Run(
                """
                INSERT INTO cmp.declaration (id, number, part_master_id, source, declared_on, expires_on, status, verified_at, verified_by, created_at, created_by)
                SELECT gen_random_uuid(), @n, m.id, 'internal', CURRENT_DATE - 10, CURRENT_DATE + 355, @status, CASE WHEN @verified THEN now() END, CASE WHEN @verified THEN 'bob' END, now(), 'e2e'
                FROM plm.part_master m WHERE m.number = @p ON CONFLICT (number) DO NOTHING
                """, ("n", number), ("p", part), ("status", status), ("verified", verified));
            await Run(
                """
                INSERT INTO cmp.declaration_line (id, declaration_id, ordinal, substance_id, percent, conflict_free)
                SELECT gen_random_uuid(), d.id, 1, s.id, @pct, @cf FROM cmp.declaration d, cmp.substance s
                WHERE d.number = @n AND s.cas_number = @cas AND NOT EXISTS (SELECT 1 FROM cmp.declaration_line l WHERE l.declaration_id = d.id)
                """, ("n", number), ("cas", cas), ("pct", percent), ("cf", (object?)conflictFree ?? DBNull.Value));
        }
        await Declare(FastenerDeclaration, fx.Manifest.OpenFastener, "verified", "7440-31-5", 1m, true, true);
        await Declare(BracketDeclaration, fx.Manifest.GimbalBracket, "non_compliant", "7439-92-1", 0.5m, null, false);
    }
}
