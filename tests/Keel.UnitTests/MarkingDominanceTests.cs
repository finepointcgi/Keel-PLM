using System.Collections.Immutable;
using Keel.Core;
using Keel.Security;

namespace Keel.UnitTests;

/// <summary>
/// Marking dominance (ADR-0019). The security property is proven over a generated space: whenever a change covers an object, every
/// subject who can read the change can read the object, so a change never discloses an object its reader cannot see.
/// </summary>
public sealed class MarkingDominanceTests
{
    // ---------------------------------------------------------------- generated spaces

    private static readonly Lazy<List<Markings>> AllMarkings = new(GenerateMarkings);
    private static readonly Lazy<List<Subject>> AllSubjects = new(GenerateSubjects);

    private static List<Markings> GenerateMarkings()
    {
        var list = new List<Markings>();
        var jurisdictions = new (ExportJurisdiction J, string? Eccn, string? Usml)[]
        {
            (ExportJurisdiction.None, null, null), (ExportJurisdiction.Ear, "EAR99", null), (ExportJurisdiction.Ear, "9A610.x", null), (ExportJurisdiction.Ear, "3A001", null),
            (ExportJurisdiction.Itar, null, "XI(d)"), (ExportJurisdiction.Itar, null, "VIII(h)"),
        };
        string[][] cuis = [[], ["EXPT"], ["EXPT", "CTI"], ["CTI"], ["EXPT", "CTI", "PROPIN"]];
        DistributionStatement[] dists = [DistributionStatement.A, DistributionStatement.B, DistributionStatement.D, DistributionStatement.E, DistributionStatement.F];
        string?[] programs = [null, "ORCA", "KRAKEN"];
        string[][] auths = [[], ["TAA-1"], ["TAA-1", "TAA-2"], ["TAA-2"]];
        foreach (var (j, eccn, usml) in jurisdictions)
            foreach (var cui in cuis)
                foreach (var dist in dists)
                    foreach (var program in programs)
                        foreach (var auth in auths)
                        {
                            var m = Markings.Create(j, dist, "ENG", eccn, usml, cui, dist == DistributionStatement.A ? null : "ACME", dist == DistributionStatement.A ? null : "Reason", program, auth);
                            if (m.IsSuccess) list.Add(m.Value);
                        }
        return list;
    }

    private static List<Subject> GenerateSubjects()
    {
        var list = new List<Subject>();
        foreach (var us in new[] { true, false })
            foreach (var programs in new[] { Array.Empty<string>(), ["ORCA"], ["KRAKEN"], ["ORCA", "KRAKEN"] })
                foreach (var cui in new[] { Array.Empty<string>(), ["EXPT"], ["EXPT", "CTI"], ["CTI"], ["EXPT", "CTI", "PROPIN"] })
                    foreach (var auths in new[] { Array.Empty<string>(), ["TAA-1"], ["TAA-2"], ["TAA-1", "TAA-2"] })
                        list.Add(new Subject
                        {
                            Id = "s", DisplayName = "s", UsPerson = us, Org = "ENG",
                            Programs = programs.ToImmutableSortedSet(StringComparer.Ordinal),
                            CuiCategories = cui.ToImmutableSortedSet(StringComparer.Ordinal),
                            ExportAuthorizations = auths.ToImmutableSortedSet(StringComparer.Ordinal),
                            Roles = [Roles.Viewer],
                        });
        return list;
    }

    private static bool CanRead(Subject s, Markings m) => AccessPolicy.Evaluate(s, m, PlmAction.Read).Permitted;

    [Fact]
    public void The_generated_space_is_big_enough_to_mean_something()
    {
        Assert.True(AllMarkings.Value.Count > 600, $"only {AllMarkings.Value.Count} valid markings");
        Assert.Equal(160, AllSubjects.Value.Count);
    }

    // ---------------------------------------------------------------- the security property

    [Fact]
    public void Whoever_can_read_a_covering_change_can_read_the_object()
    {
        var markings = AllMarkings.Value;
        var subjects = AllSubjects.Value;
        // Per change, the readers are computed once; per pair, only those readers are tested against the object.
        var covered = 0;
        foreach (var change in markings)
        {
            var readers = subjects.Where(s => CanRead(s, change)).ToList();
            foreach (var obj in markings)
            {
                if (!MarkingDominance.Covers(change, obj)) continue;
                covered++;
                foreach (var reader in readers)
                    Assert.True(CanRead(reader, obj), $"Change {change.BannerText()} covers {obj.BannerText()} but {Describe(reader)} reads only the change.");
            }
        }
        Assert.True(covered > 10_000, $"only {covered} covering pairs were exercised");
    }

    [Fact]
    public void The_minimal_cover_covers_everything_it_was_asked_to_and_keeps_the_security_property()
    {
        var markings = AllMarkings.Value;
        var subjects = AllSubjects.Value;
        var stride = 7; // every 7th change keeps the run fast while still covering every kind of difference
        var raised = 0;
        for (var i = 0; i < markings.Count; i += stride)
        {
            var change = markings[i];
            foreach (var obj in markings)
            {
                var cover = MarkingDominance.MinimalCover(change, [obj]);
                if (!cover.IsSuccess)
                {
                    // The only reason a cover does not exist is two different programs.
                    Assert.NotNull(change.Program);
                    Assert.NotNull(obj.Program);
                    Assert.NotEqual(change.Program, obj.Program);
                    continue;
                }
                var result = cover.Value;
                Assert.True(MarkingDominance.Covers(result, obj), $"{result.BannerText()} does not cover {obj.BannerText()}");
                // It never loosens the change: the result covers the original markings (CUI, jurisdiction, program, distribution)...
                Assert.True(result.CuiCategories.IsSupersetOf(change.CuiCategories));
                Assert.True(MarkingDominance.JurisdictionRank(result) >= MarkingDominance.JurisdictionRank(change));
                Assert.True(result.DistributionStatement >= change.DistributionStatement);
                Assert.True(change.Program is null || change.Program == result.Program);
                // ...and is minimal where it can be: the CUI categories are exactly the union, nothing is invented.
                Assert.Equal(change.CuiCategories.Union(obj.CuiCategories), result.CuiCategories);
                if (MarkingDominance.Covers(change, obj)) Assert.Same(change, result);
                else raised++;
                // Idempotent, and the raised change still discloses nothing to people who cannot read the object.
                Assert.Same(result, MarkingDominance.MinimalCover(result, [obj]).Value);
                foreach (var reader in subjects.Where(s => CanRead(s, result)))
                    Assert.True(CanRead(reader, obj));
            }
        }
        Assert.True(raised > 1_000, $"only {raised} raising cases were exercised");
    }

    [Fact]
    public void A_cover_of_several_objects_covers_each_of_them()
    {
        var markings = AllMarkings.Value;
        var random = new Random(20261027);
        var attempts = 0;
        for (var n = 0; n < 4000; n++)
        {
            var change = markings[random.Next(markings.Count)];
            var set = Enumerable.Range(0, 1 + random.Next(4)).Select(_ => markings[random.Next(markings.Count)]).ToList();
            var cover = MarkingDominance.MinimalCover(change, set);
            var programs = set.Select(o => o.Program).Append(change.Program).Where(p => p is not null).Distinct().Count();
            if (!cover.IsSuccess)
            {
                Assert.True(programs > 1, "A cover failed although at most one program is involved: " + string.Join("; ", cover.Errors));
                continue;
            }
            attempts++;
            Assert.True(MarkingDominance.CoversAll(cover.Value, set));
            Assert.True(MarkingDominance.Covers(cover.Value, change) || change.ExportAuthorizations.Count > 0, "The cover must not be less restrictive than the change except by narrowing authorizations.");
        }
        Assert.True(attempts > 300, $"only {attempts} sets had a cover");
    }

    // ---------------------------------------------------------------- the individual rules

    private static Markings M(ExportJurisdiction j = ExportJurisdiction.None, DistributionStatement d = DistributionStatement.A, string[]? cui = null, string? program = null,
        string? eccn = null, string? usml = null, string[]? auths = null) =>
        Markings.Create(j, d, "ENG", eccn, usml, cui, d == DistributionStatement.A ? null : "ACME", d == DistributionStatement.A ? null : "Reason", program, auths).Value;

    private static Markings Itar(string[]? cui = null, string? program = null, DistributionStatement d = DistributionStatement.D, string[]? auths = null) =>
        M(ExportJurisdiction.Itar, d, cui ?? ["EXPT"], program, usml: "XI(d)", auths: auths);

    [Fact]
    public void Cui_categories_of_the_object_must_be_in_the_change()
    {
        Assert.True(MarkingDominance.Covers(Itar(["EXPT", "CTI"]), Itar(["EXPT"])));
        Assert.Equal([MarkingDominance.CuiNotCovered], MarkingDominance.Gaps(Itar(["EXPT"]), Itar(["EXPT", "CTI"])));
    }

    [Fact]
    public void Jurisdiction_ranks_none_below_controlled_ear_below_itar_and_ear99_counts_as_none()
    {
        var none = M();
        var ear99 = M(ExportJurisdiction.Ear, DistributionStatement.D, eccn: "EAR99");
        var ear = M(ExportJurisdiction.Ear, DistributionStatement.D, ["EXPT"], eccn: "9A610.x");
        var itar = Itar();
        Assert.Equal([0, 0, 1, 2], new[] { none, ear99, ear, itar }.Select(MarkingDominance.JurisdictionRank));
        Assert.True(MarkingDominance.Covers(itar, ear));
        Assert.True(MarkingDominance.Covers(ear, ear99));
        Assert.False(MarkingDominance.Covers(ear, itar));
        Assert.False(MarkingDominance.Covers(ear99, ear));
        Assert.Contains(MarkingDominance.JurisdictionNotCovered, MarkingDominance.Gaps(none, ear));
        Assert.True(MarkingDominance.Covers(M(d: DistributionStatement.D), ear99)); // EAR99 has no nationality gate, so a change that is not export controlled may list it
        // Raising takes the ECCN or USML category from the object that forced it.
        var raised = MarkingDominance.MinimalCover(none, [ear]).Value;
        Assert.Equal((ExportJurisdiction.Ear, "9A610.X", null), (raised.ExportJurisdiction, raised.Eccn, raised.UsmlCategory)); // ECCNs are normalized to upper case
        var toItar = MarkingDominance.MinimalCover(raised, [itar]).Value;
        Assert.Equal((ExportJurisdiction.Itar, null, "XI(d)"), (toItar.ExportJurisdiction, toItar.Eccn, toItar.UsmlCategory));
    }

    [Fact]
    public void The_program_must_equal_the_objects_program_when_it_has_one()
    {
        Assert.True(MarkingDominance.Covers(Itar(program: "ORCA"), Itar(program: "ORCA")));
        Assert.True(MarkingDominance.Covers(Itar(program: "ORCA"), Itar()));          // an object without a program may be listed by a program change
        Assert.False(MarkingDominance.Covers(Itar(), Itar(program: "ORCA")));         // a change without program would disclose it
        Assert.False(MarkingDominance.Covers(Itar(program: "KRAKEN"), Itar(program: "ORCA")));
        Assert.Equal("ORCA", MarkingDominance.MinimalCover(Itar(), [Itar(program: "ORCA")]).Value.Program);
        var clash = MarkingDominance.MinimalCover(Itar(program: "KRAKEN"), [Itar(program: "ORCA")]);
        Assert.False(clash.IsSuccess);
        Assert.Contains("separate change", clash.Errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Distribution_statements_order_a_below_f_by_restrictiveness()
    {
        var order = new[] { DistributionStatement.A, DistributionStatement.B, DistributionStatement.C, DistributionStatement.D, DistributionStatement.E, DistributionStatement.F };
        for (var i = 0; i < order.Length; i++)
            for (var j = 0; j < order.Length; j++)
            {
                var change = M(d: order[i], program: order[i] == DistributionStatement.F ? "ORCA" : null);
                var obj = M(d: order[j], program: order[j] == DistributionStatement.F ? "ORCA" : null);
                var expected = i >= j && (order[j] != DistributionStatement.F || change.Program == "ORCA");
                Assert.Equal(expected, MarkingDominance.Covers(change, obj));
            }
        var raised = MarkingDominance.MinimalCover(M(d: DistributionStatement.B), [M(d: DistributionStatement.E)]).Value;
        Assert.Equal((DistributionStatement.E, "ACME", "Reason"), (raised.DistributionStatement, raised.ControllingOffice, raised.DistributionReason));
    }

    [Fact]
    public void Export_authorizations_of_the_change_must_be_a_subset_of_the_objects()
    {
        // A non-U.S. reader admitted to the change by TAA-1 must be admitted to the object by TAA-1 as well.
        Assert.True(MarkingDominance.Covers(Itar(auths: ["TAA-1"]), Itar(auths: ["TAA-1", "TAA-2"])));
        Assert.False(MarkingDominance.Covers(Itar(auths: ["TAA-1", "TAA-3"]), Itar(auths: ["TAA-1"])));
        Assert.True(MarkingDominance.Covers(Itar(), Itar(auths: ["TAA-1"])));
        var narrowed = MarkingDominance.MinimalCover(Itar(auths: ["TAA-1", "TAA-3"]), [Itar(auths: ["TAA-1"])]).Value;
        Assert.Equal(["TAA-1"], narrowed.ExportAuthorizations);
        var empty = MarkingDominance.MinimalCover(Itar(auths: ["TAA-3"]), [Itar(auths: ["TAA-1"])]).Value;
        Assert.Empty(empty.ExportAuthorizations); // only U.S. persons read this change afterwards: never wider than the object
        // Authorizations of an object that is not export controlled do not matter.
        Assert.True(MarkingDominance.Covers(Itar(auths: ["TAA-9"]), M(program: null)));
    }

    [Fact]
    public void Describe_names_each_gap_without_naming_objects()
    {
        var text = MarkingDominance.Describe(MarkingDominance.Gaps(M(), Itar(["EXPT", "CTI"], "ORCA", DistributionStatement.E)));
        Assert.Contains("CUI", text, StringComparison.Ordinal);
        Assert.Contains("jurisdiction", text, StringComparison.Ordinal);
        Assert.Contains("program", text, StringComparison.Ordinal);
        Assert.Contains("distribution", text, StringComparison.Ordinal);
        Assert.True(MarkingDominance.CoversAll(Itar(), []));
    }

    private static string Describe(Subject s) =>
        $"us={s.UsPerson} programs=[{string.Join(',', s.Programs)}] cui=[{string.Join(',', s.CuiCategories)}] auths=[{string.Join(',', s.ExportAuthorizations)}]";
}
