using System.Collections.Immutable;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// ADR-0004 release gate: the in-memory evaluator and the SQL read filter must agree for every combination of
/// subject attributes and markings in the generated space.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AccessFilterAgreementTests(PostgresFixture pg)
{
    private static IEnumerable<Markings> AllMarkings()
    {
        (ExportJurisdiction J, string? Eccn, string? Usml)[] export =
            [(ExportJurisdiction.None, null, null), (ExportJurisdiction.Ear, "EAR99", null), (ExportJurisdiction.Ear, "9E610", null), (ExportJurisdiction.Itar, null, "XI(d)")];
        string?[] programs = [null, "ORCA", "KRAKEN"];
        string[][] cui = [[], ["CTI"], ["EXPT"], ["CTI", "EXPT"]];
        string[][] auths = [[], ["TAA-1"], ["TAA-1", "DSP5-7"]];
        DistributionStatement[] dist = [DistributionStatement.A, DistributionStatement.C, DistributionStatement.F];

        foreach (var e in export)
        foreach (var p in programs)
        foreach (var c in cui)
        foreach (var a in auths)
        foreach (var d in dist)
        {
            var r = Markings.Create(e.J, d, "ENG", e.Eccn, e.Usml, c, "ACME", "Test", p, a);
            if (r.IsSuccess) yield return r.Value;
        }
    }

    private static IEnumerable<Subject> AllSubjects()
    {
        string[][] programs = [[], ["ORCA"], ["ORCA", "KRAKEN"]];
        string[][] cui = [[], ["CTI"], ["EXPT"], ["CTI", "EXPT"]];
        string[][] auths = [[], ["TAA-1"], ["DSP5-7"], ["OTHER"]];
        string[][] roles = [[], [Roles.Viewer], [Roles.Admin]];
        var n = 0;
        foreach (var us in new[] { true, false })
        foreach (var p in programs)
        foreach (var c in cui)
        foreach (var a in auths)
        foreach (var r in roles)
            yield return new Subject
            {
                Id = $"s{n++}",
                DisplayName = "generated",
                UsPerson = us,
                Programs = p.ToImmutableSortedSet(StringComparer.Ordinal),
                CuiCategories = c.ToImmutableSortedSet(StringComparer.Ordinal),
                ExportAuthorizations = a.ToImmutableSortedSet(StringComparer.Ordinal),
                Roles = r.ToImmutableSortedSet(StringComparer.Ordinal),
            };
    }

    [Fact]
    public async Task Sql_filter_matches_in_memory_policy_for_every_combination()
    {
        var db = await pg.CreateDatabaseAsync();
        var markings = AllMarkings().ToList();
        Assert.True(markings.Count > 100, $"only {markings.Count} valid marking combinations generated");

        // Same marking column contract every controlled table will use (ADR-0004).
        await db.ExecuteAsSuperuserAsync("""
            CREATE TABLE public.marked (
                id int PRIMARY KEY,
                export_jurisdiction text NOT NULL,
                eccn text,
                program text,
                cui_categories text[] NOT NULL,
                export_authorizations text[] NOT NULL);
            GRANT SELECT ON public.marked TO keel_app;
            """);
        await using (var su = NpgsqlDataSource.Create(db.SuperuserConnectionString))
        {
            for (var i = 0; i < markings.Count; i++)
            {
                var m = markings[i];
                await using var ins = su.CreateCommand("INSERT INTO public.marked VALUES (@i, @j, @e, @p, @c, @a)");
                ins.Parameters.AddWithValue("i", i);
                ins.Parameters.AddWithValue("j", m.ExportJurisdiction.ToString());
                ins.Parameters.AddWithValue("e", (object?)m.Eccn ?? DBNull.Value);
                ins.Parameters.AddWithValue("p", (object?)m.Program ?? DBNull.Value);
                ins.Parameters.AddWithValue("c", m.CuiCategories.ToArray());
                ins.Parameters.AddWithValue("a", m.ExportAuthorizations.ToArray());
                await ins.ExecuteNonQueryAsync();
            }
        }

        await using var app = db.AppDataSource();
        var subjects = AllSubjects().ToList();
        var mismatches = new List<string>();
        foreach (var s in subjects)
        {
            var filter = AccessPolicy.BuildReadFilter(s, "m");
            await using var cmd = app.CreateCommand($"SELECT id FROM public.marked m WHERE {filter.Sql} ORDER BY id");
            foreach (var (name, value) in filter.Parameters) cmd.Parameters.AddWithValue(name, value);
            var sqlVisible = new HashSet<int>();
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync()) sqlVisible.Add(r.GetInt32(0));

            for (var i = 0; i < markings.Count; i++)
            {
                var allowed = AccessPolicy.Evaluate(s, markings[i], PlmAction.Read).Permitted;
                if (allowed != sqlVisible.Contains(i))
                    mismatches.Add($"{s.Id} vs {markings[i].BannerText()} auths=[{string.Join(',', markings[i].ExportAuthorizations)}]: memory={allowed} sql={!allowed}");
            }
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} disagreements, first: {string.Join("\n", mismatches.Take(10))}");
        TestContext.Current.SendDiagnosticMessage($"Compared {subjects.Count} subjects x {markings.Count} markings.");
    }
}
