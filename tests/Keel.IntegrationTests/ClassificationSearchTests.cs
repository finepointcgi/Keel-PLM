using System.Diagnostics;
using System.Text.Json.Nodes;
using Keel.Admin;
using Keel.Change;
using Keel.Core;
using Keel.Parts;
using Keel.Security;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Faceted classification search under the access filter (ADR-0022): every total, class count and facet count equals an oracle computed in memory
/// with <see cref="AccessPolicy.Evaluate"/>, so a restricted user's numbers never include a part they may not read.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ClassificationSearchTests(PostgresFixture pg)
{
    private static readonly Subject Ada = AdminEnv.Ada;
    private static readonly Subject Alice = AdminEnv.Alice;
    private static readonly Subject Kim = WorkflowEnv.User("kim", [Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]);
    private static readonly Subject Lena = WorkflowEnv.User("lena", [Roles.Viewer, Roles.Author], us: false);

    private sealed record Seeded(string Number, Markings Markings, string Class, string Grade, decimal Diameter, decimal? Length, string Name);

    private static async Task BuildTreeAsync(AdminEnv env)
    {
        var c = env.Classes;
        AdminEnv.Ok(await c.CreateNodeAsync(Ada, new ClassNodeInput("MECH", "Mechanical", null, null, 10), null));
        AdminEnv.Ok(await c.CreateNodeAsync(Ada, new ClassNodeInput("FAST", "Fasteners", null, "MECH", 10), null));
        AdminEnv.Ok(await c.CreateNodeAsync(Ada, new ClassNodeInput("BOLT", "Bolts", null, "FAST", 10), null));
        AdminEnv.Ok(await c.CreateNodeAsync(Ada, new ClassNodeInput("BRACKET", "Brackets", null, "MECH", 20), null));
        AdminEnv.Ok(await c.SaveAttributeAsync(Ada, "MECH", AdminEnv.Enum("grade", "Material grade", "304", "316", "6061"), true, null));
        AdminEnv.Ok(await c.SaveAttributeAsync(Ada, "FAST", AdminEnv.Number("diameter", "Diameter", "mm", 1, 100), true, null));
        AdminEnv.Ok(await c.SaveAttributeAsync(Ada, "BOLT", AdminEnv.Number("length", "Length", "mm", 1, 500), true, null));
    }

    private static async Task<List<Seeded>> SeedAsync(AdminEnv env, int count)
    {
        var all = new List<Seeded>();
        string[] grades = ["304", "316", "6061"];
        for (var i = 0; i < count; i++)
        {
            var kind = i % 3;
            var cls = kind == 0 ? "BOLT" : kind == 1 ? "FAST" : "BRACKET";
            var markings = (i % 4) switch { 1 => AdminEnv.Itar("ORCA"), 2 => AdminEnv.Itar("KRAKEN"), _ => AdminEnv.Open };
            var author = markings.Program == "KRAKEN" ? Kim : Alice;
            var grade = grades[i % 3 == 2 ? (i / 3) % 3 : i % 3];
            var diameter = 4 + i % 10;
            var name = i % 7 == 0 ? $"Special part {i}" : $"Ordinary part {i}";
            var number = await env.NewPartAsync(name, markings, author);
            var attrs = new JsonObject { ["grade"] = grade };
            decimal? length = null;
            if (cls != "BRACKET") attrs["diameter"] = diameter;
            if (cls == "BOLT") { length = 10 + i; attrs["length"] = length; }
            await env.ClassifyAsync(number, cls, attrs, author);
            all.Add(new Seeded(number, markings, cls, grade, cls == "BRACKET" ? 0 : diameter, length, name));
        }
        return all;
    }

    private static bool Visible(Subject s, Seeded p) => AccessPolicy.Evaluate(s, p.Markings, PlmAction.Read).Permitted;

    private static bool InClass(Seeded p, string code) => code switch
    {
        "MECH" => true,
        "FAST" => p.Class is "FAST" or "BOLT",
        _ => p.Class == code,
    };

    [Fact]
    public async Task Totals_class_counts_and_facets_equal_the_access_oracle_for_every_kind_of_user()
    {
        var env = await AdminEnv.CreateAsync(pg);
        await BuildTreeAsync(env);
        var parts = await SeedAsync(env, 36);
        var users = new[] { Alice, Kim, AdminEnv.Eve, Lena, AdminEnv.Mallory };

        foreach (var user in users)
        {
            var visible = parts.Where(p => Visible(user, p)).ToList();

            // Whole tree.
            var all = AdminEnv.Ok(await env.Classes.SearchAsync(user, new ClassSearchQuery("MECH", Limit: 200), "c-all"));
            Assert.Equal(visible.Count, all.Total);
            Assert.Equal(visible.Select(p => p.Number).Order(), all.Items.Select(i => i.Number).Order());
            Assert.All(new[] { "MECH", "FAST", "BOLT", "BRACKET" }, code =>
                Assert.Equal(visible.Count(p => InClass(p, code)), all.ClassCounts.FirstOrDefault(c => c.Code == code)?.Count ?? 0));

            // Subtree + range + enum filters; counts and facets follow the same filters.
            var query = new ClassSearchQuery("FAST", Filters:
            [
                new FacetFilter("diameter", Min: 6, Max: 11),
                new FacetFilter("grade", AnyOf: ["304", "316"]),
            ], Limit: 200);
            var result = AdminEnv.Ok(await env.Classes.SearchAsync(user, query, null));
            var expected = visible.ToList();
            expected = [.. visible.Where(p => InClass(p, "FAST") && p.Diameter >= 6 && p.Diameter <= 11 && (p.Grade is "304" or "316"))];
            Assert.Equal(expected.Count, result.Total);
            Assert.Equal(expected.Select(p => p.Number).Order(), result.Items.Select(i => i.Number).Order());

            // Facet semantics: a facet ignores its own filter. Grade counts use the diameter filter only; the diameter range uses the grade filter only.
            var gradeFacet = result.Facets.FirstOrDefault(f => f.Attribute == "grade");
            var gradeBase = visible.Where(p => InClass(p, "FAST") && p.Diameter >= 6 && p.Diameter <= 11).ToList();
            foreach (var g in new[] { "304", "316", "6061" })
                Assert.Equal(gradeBase.Count(p => p.Grade == g), gradeFacet?.Values.FirstOrDefault(v => v.Value == g)?.Count ?? 0);
            var diameterFacet = result.Facets.FirstOrDefault(f => f.Attribute == "diameter");
            var diameterBase = visible.Where(p => InClass(p, "FAST") && (p.Grade is "304" or "316")).ToList();
            Assert.Equal(diameterBase.Count == 0 ? null : diameterBase.Min(p => p.Diameter), diameterFacet?.Min);
            Assert.Equal(diameterBase.Count == 0 ? null : diameterBase.Max(p => p.Diameter), diameterFacet?.Max);

            // Free text combined with a class.
            var text = AdminEnv.Ok(await env.Classes.SearchAsync(user, new ClassSearchQuery("MECH", "Special", Limit: 200), null));
            Assert.Equal(visible.Count(p => p.Name.StartsWith("Special", StringComparison.Ordinal)), text.Total);
            // Nothing the user may not read is ever named, in any item.
            Assert.All(all.Items.Concat(result.Items).Concat(text.Items), i => Assert.True(parts.Single(p => p.Number == i.Number) is var p && Visible(user, p)));
        }

        var restricted = AdminEnv.Ok(await env.Classes.SearchAsync(AdminEnv.Eve, new ClassSearchQuery("MECH", Limit: 200), null));
        var privileged = AdminEnv.Ok(await env.Classes.SearchAsync(Kim, new ClassSearchQuery("MECH", Limit: 200), null));
        Assert.True(restricted.Total < privileged.Total);
        Assert.DoesNotContain(restricted.Items, i => i.Markings.Program is not null);
    }

    [Fact]
    public async Task Search_validates_filters_pages_results_and_audits_what_was_returned()
    {
        var env = await AdminEnv.CreateAsync(pg);
        await BuildTreeAsync(env);
        await SeedAsync(env, 12);

        Assert.Equal(OutcomeKind.Invalid, (await env.Classes.SearchAsync(Alice, new ClassSearchQuery(null, Filters: [new FacetFilter("diameter", Min: 1)]), null)).Kind); // no class: nothing to type the filter
        Assert.Equal(OutcomeKind.Invalid, (await env.Classes.SearchAsync(Alice, new ClassSearchQuery("FAST", Filters: [new FacetFilter("nope", Min: 1)]), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Classes.SearchAsync(Alice, new ClassSearchQuery("FAST", Filters: [new FacetFilter("diameter")]), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Classes.SearchAsync(Alice, new ClassSearchQuery("NOPE"), null)).Kind);

        var page1 = AdminEnv.Ok(await env.Classes.SearchAsync(Alice, new ClassSearchQuery("MECH", Limit: 3, Offset: 0), "c-page"));
        var page2 = AdminEnv.Ok(await env.Classes.SearchAsync(Alice, new ClassSearchQuery("MECH", Limit: 3, Offset: 3), null));
        Assert.Equal(3, page1.Items.Count);
        Assert.Empty(page1.Items.Select(i => i.Number).Intersect(page2.Items.Select(i => i.Number)));
        Assert.Equal(page1.Total, page2.Total);

        var audit = (await env.AuditAsync()).First(e => e.Action == "part.classification_search" && e.CorrelationId == "c-page");
        Assert.Contains(page1.Items[0].Number, audit.Payload, StringComparison.Ordinal);
        // The fixed-text filter works for string attributes too and unclassified parts never appear.
        await env.NewPartAsync("Unclassified");
        Assert.Equal(page1.Total, AdminEnv.Ok(await env.Classes.SearchAsync(Alice, new ClassSearchQuery("MECH", Limit: 1), null)).Total);
    }

    [Fact]
    public async Task Classification_is_copied_forward_on_revise_and_covered_by_change_fingerprints_and_review_signatures()
    {
        var env = await AdminEnv.CreateAsync(pg);
        await BuildTreeAsync(env);
        string[] numbers = [await env.NewPartAsync("Twin"), await env.NewPartAsync("Twin"), await env.NewPartAsync("Twin"), await env.NewPartAsync("Twin")];
        await env.ClassifyAsync(numbers[0], "BOLT", new JsonObject { ["grade"] = "316", ["diameter"] = 8, ["length"] = 20 });
        await env.ClassifyAsync(numbers[1], "BOLT", new JsonObject { ["grade"] = "316", ["diameter"] = 8, ["length"] = 22 });
        await env.ClassifyAsync(numbers[3], "BOLT", new JsonObject { ["grade"] = "316", ["diameter"] = 8, ["length"] = 20 });
        // numbers[2] stays unclassified; give it the check-in the others had so that iteration counts match.
        Assert.True((await env.Parts.CheckOutAsync(Alice, numbers[2], null)).IsOk);
        Assert.True((await env.Parts.CheckInAsync(Alice, numbers[2], "classified", null)).IsOk);

        // Change fingerprints (what an ECN's signatures cover) include the classification.
        var participants = new IPartIterationParticipant[] { new ClassificationParticipant() };
        var gateway = new PartChangeGateway(participants);
        async Task<string> FingerprintAsync(string number)
        {
            await using var conn = await env.Ds.OpenConnectionAsync();
            var id = Guid.Parse(await env.TextAsync($"SELECT i.id FROM plm.part_iteration i JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = '{number}' AND i.iteration = r.latest_iteration"));
            await using var tx = await conn.BeginTransactionAsync();
            return (await gateway.ContentFingerprintAsync(conn, tx, id, CancellationToken.None))!;
        }
        var f = new[] { await FingerprintAsync(numbers[0]), await FingerprintAsync(numbers[1]), await FingerprintAsync(numbers[2]), await FingerprintAsync(numbers[3]) };
        Assert.Equal(f[0], f[3]);
        Assert.NotEqual(f[0], f[1]);
        Assert.NotEqual(f[0], f[2]);

        // Part review signatures hash the same content: identical parts differ only by their classification.
        foreach (var n in numbers) await env.ReleaseAsync(n);
        async Task<string> SignatureContentHashAsync(string number) =>
            await env.TextAsync($"SELECT encode(content_hash, 'hex') FROM wf.signature WHERE object_key = '{number}' ORDER BY signed_at LIMIT 1");
        var h = new[] { await SignatureContentHashAsync(numbers[0]), await SignatureContentHashAsync(numbers[1]), await SignatureContentHashAsync(numbers[2]), await SignatureContentHashAsync(numbers[3]) };
        Assert.Equal(h[0], h[3]);
        Assert.NotEqual(h[0], h[1]);
        Assert.NotEqual(h[0], h[2]);

        // Revise the released, classified part in an ECN: the next revision starts with the same class and values.
        var ecn = await env.Change.CreateAsync(Alice, new CreateChangeCommand("ECN", "Revise twin", "d", "Medium", "r", AdminEnv.Itar(), "II"), null);
        Assert.True(ecn.IsOk, AdminEnv.Errors(ecn));
        var added = await env.Change.AddAffectedAsync(Alice, ecn.Value!.Number, new AddAffectedCommand("part", numbers[0], "revise", null, false), null);
        Assert.True(added.IsOk, AdminEnv.Errors(added));
        var b = await env.PartAsync(numbers[0]);
        Assert.Equal(("B", "B.1"), (b.Revision.Revision, b.Iteration.Label));
        var classification = AdminEnv.Ok(await env.Classes.GetForPartAsync(Alice, numbers[0], null));
        Assert.Equal(("BOLT", "B.1", 20m), (classification.ClassCode, classification.IterationLabel, classification.Attributes["length"]!.GetValue<decimal>()));
        Assert.Equal(1, await env.ScalarAsync($"SELECT count(*) FROM cfg.part_class_value v JOIN plm.part_iteration i ON i.id = v.iteration_id JOIN plm.part_revision r ON r.id = i.revision_id WHERE r.revision = 'B' AND v.attr = 'length'"));
        // Revision A keeps its own frozen classification.
        Assert.Equal(1, await env.ScalarAsync($"SELECT count(*) FROM cfg.part_classification pc JOIN plm.part_iteration i ON i.id = pc.iteration_id JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = '{numbers[0]}' AND r.revision = 'A'"));
        // And the search finds the part by its latest (B) iteration.
        Assert.Contains(AdminEnv.Ok(await env.Classes.SearchAsync(Alice, new ClassSearchQuery("BOLT", Limit: 50), null)).Items, i => i.Number == numbers[0] && i.Revision == "B");
    }

    [Fact]
    public async Task Frozen_classification_rows_are_immutable_in_the_database()
    {
        var env = await AdminEnv.CreateAsync(pg);
        await BuildTreeAsync(env);
        var number = await env.NewPartAsync();
        await env.ClassifyAsync(number, "BRACKET", new JsonObject { ["grade"] = "304" });
        await using var conn = await env.Ds.OpenConnectionAsync();
        foreach (var sql in new[]
        {
            "UPDATE cfg.part_classification SET class_code = 'X'",
            "DELETE FROM cfg.part_classification",
            "UPDATE cfg.part_class_value SET txt = 'y'",
            "DELETE FROM cfg.class_node WHERE code = 'BRACKET'",
            "UPDATE cfg.class_node SET code = 'ZZ' WHERE code = 'BRACKET'",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public async Task Five_thousand_classified_parts_search_fast_and_use_the_value_indexes()
    {
        var env = await AdminEnv.CreateAsync(pg);
        await BuildTreeAsync(env);
        await env.Db.ExecuteAsSuperuserAsync(
            """
            CREATE TEMP TABLE bulk AS SELECT g, gen_random_uuid() AS m, gen_random_uuid() AS r, gen_random_uuid() AS i FROM generate_series(1, 5000) g;
            INSERT INTO plm.part_master (id, number, name, part_type, default_unit, source, lifecycle_id, export_jurisdiction, cui_categories, distribution_statement, program, owning_org, export_authorizations, created_at, created_by)
            SELECT m, 'B-' || lpad(g::text, 6, '0'), 'Bulk part ' || g, 'Component', 'EA', 'Make', 'part-default',
                   CASE WHEN g % 5 = 0 THEN 'Itar' ELSE 'None' END, CASE WHEN g % 5 = 0 THEN ARRAY['EXPT'] ELSE ARRAY[]::text[] END,
                   CASE WHEN g % 5 = 0 THEN 'D' ELSE 'A' END, CASE WHEN g % 10 = 0 THEN 'KRAKEN' WHEN g % 5 = 0 THEN 'ORCA' ELSE NULL END, 'ENG', ARRAY[]::text[], now(), 'bulk'
            FROM bulk;
            INSERT INTO plm.part_revision (id, master_id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by)
            SELECT r, m, 'A', 'InWork', true, 1, now(), 'bulk', now(), 'bulk' FROM bulk;
            INSERT INTO plm.part_iteration (id, revision_id, iteration, description, attributes, comment, created_at, created_by) SELECT i, r, 1, '', '{}', 'bulk', now(), 'bulk' FROM bulk;
            INSERT INTO cfg.part_classification (iteration_id, class_id, class_code, attributes)
            SELECT i, (SELECT id FROM cfg.class_node WHERE code = 'BOLT'), 'BOLT',
                   jsonb_build_object('grade', (ARRAY['304','316','6061'])[1 + g % 3], 'diameter', 1 + g % 100, 'length', 10 + g % 400) FROM bulk;
            INSERT INTO cfg.part_class_value (iteration_id, attr, txt) SELECT i, 'grade', (ARRAY['304','316','6061'])[1 + g % 3] FROM bulk;
            INSERT INTO cfg.part_class_value (iteration_id, attr, num) SELECT i, 'diameter', 1 + g % 100 FROM bulk;
            INSERT INTO cfg.part_class_value (iteration_id, attr, num) SELECT i, 'length', 10 + g % 400 FROM bulk;
            ANALYZE;
            """);

        var query = new ClassSearchQuery("FAST", Filters: [new FacetFilter("diameter", Min: 20, Max: 22), new FacetFilter("grade", AnyOf: ["316"])], Limit: 50);
        await env.Classes.SearchAsync(Alice, query, null); // warm up
        var sw = Stopwatch.StartNew();
        var result = AdminEnv.Ok(await env.Classes.SearchAsync(Alice, query, null));
        sw.Stop();
        // 5000 parts: diameter in 20..22 (3% of them), grade 316 (a third), minus the KRAKEN ones Alice cannot see.
        Assert.True(result.Total is > 10 and < 100, $"total {result.Total}");
        Assert.True(sw.ElapsedMilliseconds < 1500, $"search took {sw.ElapsedMilliseconds} ms");

        // The value indexes serve the range filter (EXPLAIN of the same shape of statement).
        var filter = AccessPolicy.BuildReadFilter(Alice, "m");
        await using var conn = await env.Ds.OpenConnectionAsync();
        await using var explain = new NpgsqlCommand(
            $"""
            EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF)
            SELECT count(*) FROM plm.part_master m JOIN plm.part_revision r ON r.master_id = m.id AND r.is_latest
            JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            JOIN cfg.part_classification pc ON pc.iteration_id = i.id JOIN cfg.class_node cn ON cn.id = pc.class_id
            WHERE {filter.Sql} AND cn.path LIKE '/MECH/FAST/%'
              AND EXISTS (SELECT 1 FROM cfg.part_class_value fv0 WHERE fv0.iteration_id = i.id AND fv0.attr = 'diameter' AND fv0.num >= 20 AND fv0.num <= 22)
              AND EXISTS (SELECT 1 FROM cfg.part_class_value fv1 WHERE fv1.iteration_id = i.id AND fv1.attr = 'grade' AND fv1.txt = ANY(ARRAY['316']))
            """, conn);
        foreach (var (k, v) in filter.Parameters) explain.Parameters.AddWithValue(k, v);
        var plan = new List<string>();
        await using (var r = await explain.ExecuteReaderAsync())
            while (await r.ReadAsync()) plan.Add(r.GetString(0));
        var text = string.Join('\n', plan);
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "keel-classification-explain.txt"), $"search ms: {sw.ElapsedMilliseconds}\n{text}");
        Assert.Contains("part_class_value_num_idx", text, StringComparison.Ordinal);
    }
}
