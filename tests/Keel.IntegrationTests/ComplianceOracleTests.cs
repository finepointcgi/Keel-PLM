using Keel.Compliance;
using Keel.Core;
using Keel.Security;

namespace Keel.IntegrationTests;

/// <summary>
/// The SQL read filters of the compliance module agree with the in-memory access policy for every subject and every marking variant (the SQL oracle of ADR-0004): which
/// declarations a search returns, what the dashboard counts, which parts a roll-up evaluates and what its totals say are exactly what <see cref="AccessPolicy.Evaluate"/>
/// permits for the PART, and a hidden part changes nothing a subject sees.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ComplianceOracleTests(PostgresFixture pg)
{
    private static readonly Subject Lena = WorkflowEnv.User("lena", [Roles.Viewer], us: false);
    private static readonly Subject Farid = WorkflowEnv.User("farid", [Roles.Viewer], us: false, auths: ["TAA-0042"]);
    private static readonly Subject Erp = WorkflowEnv.User("erp", [Roles.Integration]);
    private static readonly Subject Kim = WorkflowEnv.User("kim", [Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]);

    private static Markings Make(ExportJurisdiction j, DistributionStatement d, string? usml = null, string? eccn = null, string[]? cui = null, string? program = null, string[]? auths = null) =>
        Markings.Create(j, d, "ENG", eccn, usml, cui, d == DistributionStatement.A ? null : "ACME", d == DistributionStatement.A ? null : "Reason", program, auths).Value;

    private static readonly Markings[] Variants =
    [
        ComplianceEnv.Open,
        SourcingEnv.Cui(),
        ComplianceEnv.Itar(),
        Make(ExportJurisdiction.Itar, DistributionStatement.D, usml: "XI(d)", cui: ["EXPT"], program: "ORCA", auths: ["TAA-0042"]),
        Make(ExportJurisdiction.Ear, DistributionStatement.C, eccn: "9A610", cui: ["EXPT"]),
        Make(ExportJurisdiction.Ear, DistributionStatement.C, eccn: "EAR99"),
        Make(ExportJurisdiction.None, DistributionStatement.B, program: "ORCA"),
        Make(ExportJurisdiction.None, DistributionStatement.B, cui: ["CTI", "EXPT"]),
    ];

    private static readonly Subject[] Subjects =
    [
        ComplianceEnv.Alice, ComplianceEnv.Bob, ComplianceEnv.Ada, ComplianceEnv.Mallory, ComplianceEnv.Audrey, ComplianceEnv.Eve, ComplianceEnv.Vic, Lena, Farid, Erp, Kim,
    ];

    [Fact]
    public async Task Declaration_lists_counts_part_views_and_rollups_agree_with_the_policy_for_every_subject()
    {
        var env = await ComplianceEnv.CreateAsync(pg);
        var parts = new Dictionary<string, Markings>();
        var declarations = new Dictionary<string, (string Part, Markings M)>();
        foreach (var m in Variants)
        {
            var part = await env.NewPartAsync($"Part {parts.Count}", m);
            parts[part] = m;
            // Every part carries a violating declaration: a hidden part would change any total it leaked into.
            var d = await env.DeclareAsync(part, [ComplianceEnv.Sub(ComplianceEnv.Lead, 0.5m)]);
            declarations[d.Summary.Number] = (part, m);
        }
        var lines = parts.Keys.Select((n, i) => ComplianceEnv.Line($"1.{i + 1}", n, false, level: 2)).ToList();

        foreach (var subject in Subjects)
        {
            bool Readable(Markings m) => AccessPolicy.Evaluate(subject, m, PlmAction.Read).Permitted;
            var expectedParts = parts.Where(kv => Readable(kv.Value)).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
            var expectedDeclarations = declarations.Where(kv => Readable(kv.Value.M)).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();

            var page = (await env.Declarations.SearchAsync(subject, new DeclarationSearch(Limit: 200), null)).Value!;
            Assert.True(expectedDeclarations.SequenceEqual(page.Items.Select(i => i.Number).Order(StringComparer.Ordinal)), $"declarations of {subject.Username}");
            Assert.Equal(expectedDeclarations.Count, page.Total);

            var counts = await env.Declarations.CountsAsync(subject);
            Assert.Equal(expectedDeclarations.Count, counts.NonCompliant);
            Assert.Equal(expectedDeclarations.Count, counts.Parts);
            Assert.Equal(0, counts.Declared + counts.Verified + counts.Expired);

            foreach (var (number, m) in parts)
            {
                var view = await env.Declarations.GetForPartAsync(subject, number, null);
                Assert.Equal(Readable(m), view.IsOk);
                if (!view.IsOk) Assert.Equal(OutcomeKind.NotFound, view.Kind); // a part that cannot be read looks like one that does not exist
            }
            foreach (var (number, (_, m)) in declarations)
            {
                var detail = await env.Declarations.GetAsync(subject, number, null);
                Assert.Equal(Readable(m), detail.IsOk);
                if (!detail.IsOk) Assert.Equal(OutcomeKind.NotFound, detail.Kind);
            }

            if (!AccessPolicy.HasRoleFor(subject, PlmAction.Read)) continue;
            var report = (await env.Reports.BuildAsync(subject, ComplianceEnv.Request("P-ROOT", lines), null)).Value!;
            Assert.True(expectedParts.SequenceEqual(report.Lines.Select(l => l.Number).Order(StringComparer.Ordinal)), $"roll-up lines of {subject.Username}");
            var rohs = report.Summaries.Single(s => s.Code == "rohs");
            Assert.Equal(expectedParts.Count, rohs.NonCompliant);
            Assert.Equal(expectedParts.Count, rohs.Required);
            Assert.Equal(0, rohs.Gaps);
            Assert.Equal(expectedParts.Count, rohs.Causes.Count);
            Assert.All(rohs.Causes, c => Assert.Contains(c.Number, expectedParts));
            Assert.Equal(expectedParts.Count == 0 ? Standings.AssemblyNone : Standings.AssemblyNonCompliant, rohs.Standing);
        }
    }

    [Fact]
    public async Task A_hidden_part_changes_no_total_status_or_cause_of_the_roll_up()
    {
        var env = await ComplianceEnv.CreateAsync(pg);
        var open = await env.NewPartAsync("Open");
        var hidden = await env.NewPartAsync("Hidden", ComplianceEnv.Itar());
        await env.DeclareAsync(open, [ComplianceEnv.Sub(ComplianceEnv.Tin, 1, null, true)]);
        await env.DeclareAsync(hidden, [ComplianceEnv.Sub(ComplianceEnv.Lead, 5)]); // non-compliant
        var withHidden = new[] { ComplianceEnv.Line("1.1", open, false, level: 2), ComplianceEnv.Line("1.2", hidden, false, level: 2) };
        var without = new[] { ComplianceEnv.Line("1.1", open, false, level: 2) };

        // Eve is not on ORCA: the ITAR part is invisible to her. Her roll-up of the structure that contains it equals the roll-up of the structure without it.
        var a = (await env.Reports.BuildAsync(ComplianceEnv.Eve, ComplianceEnv.Request("P-ROOT", withHidden), null)).Value!;
        var b = (await env.Reports.BuildAsync(ComplianceEnv.Eve, ComplianceEnv.Request("P-ROOT", without), null)).Value!;
        Assert.Equal(b.Lines.Count, a.Lines.Count);
        foreach (var (x, y) in a.Summaries.Zip(b.Summaries))
        {
            Assert.Equal((y.Standing, y.Compliant, y.Exempt, y.NonCompliant, y.Unverified, y.Gaps, y.Expired, y.NotRequired, y.Causes.Count), (x.Standing, x.Compliant, x.Exempt, x.NonCompliant, x.Unverified, x.Gaps, x.Expired, x.NotRequired, x.Causes.Count));
        }
        Assert.DoesNotContain(a.Lines, l => l.Number == hidden);
        Assert.DoesNotContain(a.Summaries.SelectMany(s => s.Causes), c => c.Number == hidden);
        // Alice sees both and the assembly is non-compliant.
        Assert.Equal(Standings.AssemblyNonCompliant, (await env.Reports.BuildAsync(ComplianceEnv.Alice, ComplianceEnv.Request("P-ROOT", withHidden), null)).Value!.Summaries.Single(s => s.Code == "rohs").Standing);
        // The dashboard counts and search say nothing about the hidden declaration either.
        Assert.Equal(0, (await env.Declarations.CountsAsync(ComplianceEnv.Eve)).NonCompliant);
        Assert.Equal(1, (await env.Declarations.CountsAsync(ComplianceEnv.Alice)).NonCompliant);
        // And a supplier-role user reads nothing at all.
        Assert.Equal(0, (await env.Declarations.SearchAsync(SourcingEnv.Sup1, new DeclarationSearch(), null)).Value!.Total);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Reports.BuildAsync(SourcingEnv.Sup1, ComplianceEnv.Request("P-ROOT", withHidden), null)).Kind);
    }

    [Fact]
    public async Task Changing_the_markings_of_a_part_applies_to_its_declarations_at_once()
    {
        var env = await ComplianceEnv.CreateAsync(pg);
        var part = await env.NewPartAsync("Switching");
        var d = await env.DeclareAsync(part, [ComplianceEnv.Sub(ComplianceEnv.Tin, 1)]);
        Assert.True((await env.Declarations.GetAsync(ComplianceEnv.Eve, d.Summary.Number, null)).IsOk);
        await env.Db.ExecuteAsSuperuserAsync($"UPDATE plm.part_master SET export_jurisdiction = 'Itar', usml_category = 'XI(d)', cui_categories = ARRAY['EXPT'], distribution_statement = 'D', controlling_office = 'ACME', distribution_reason = 'Reason', program = 'ORCA' WHERE number = '{part}'");
        Assert.Equal(OutcomeKind.NotFound, (await env.Declarations.GetAsync(ComplianceEnv.Eve, d.Summary.Number, null)).Kind);
        Assert.Equal(0, (await env.Declarations.SearchAsync(ComplianceEnv.Eve, new DeclarationSearch(), null)).Value!.Total);
        Assert.True((await env.Declarations.GetAsync(ComplianceEnv.Alice, d.Summary.Number, null)).IsOk);
    }
}
