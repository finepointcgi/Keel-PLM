using System.Text.Json;
using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.Security;
using Keel.TestSupport;
using static Keel.IntegrationTests.BomTestKit;

namespace Keel.IntegrationTests;

/// <summary>Redaction (A4), SQL/policy agreement for structural queries, and "hidden means hidden" on every BOM surface.</summary>
[Collection(PostgresCollection.Name)]
public sealed class BomSecurityTests(PostgresFixture pg)
{
    private sealed record Scene(Env Env, string Top, string HiddenSub, string SecretLeaf, string OpenSub, string OpenLeaf)
    {
        public string[] Forbidden => [HiddenSub, SecretLeaf, "Classified gearbox", "Stealth rotor", "ITAR", "XI(d)", "ORCA", "EXPT", "CUI//"];
    }

    /// <summary>
    /// top: 2 x hidden ITAR gearbox (which holds an open part), 4 x open sub (3 x the same open part), 1 x hidden ITAR rotor.
    /// A.1 of top is built first so a later iteration can change a hidden line's quantity (compare).
    /// </summary>
    private async Task<Scene> BuildScene()
    {
        var env = await Setup(pg);
        var openLeaf = await env.CreatePart(Alice, "Open bracket");
        var secretLeaf = await env.CreatePart(Alice, "Stealth rotor", markings: Itar());
        var hiddenSub = await env.Assembly(Alice, "Classified gearbox", Itar(), (openLeaf, 2));
        var openSub = await env.Assembly(Alice, "Open sub", null, (openLeaf, 3));
        var top = await env.Assembly(Alice, "Top", null, (hiddenSub, 2), (openSub, 4), (secretLeaf, 1));
        return new Scene(env, top, hiddenSub, secretLeaf, openSub, openLeaf);
    }

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    [Fact]
    public async Task Hidden_children_are_placeholders_with_quantities_only_and_their_subtrees_are_not_traversed()
    {
        var s = await BuildScene();
        var lena = (await s.Env.Bom.ExplodeAsync(Lena, new BomQuery(s.Top), null)).Value!;

        Assert.Equal(["10", "20", "20.10", "30"], lena.Lines.Select(l => l.Path)); // nothing below the hidden gearbox
        Assert.Equal([true, false, false, true], lena.Lines.Select(l => l.Redacted));
        Assert.Equal(2, lena.RedactedCount);
        var hidden = lena.Lines[0];
        Assert.Equal((2m, "EA", 10, 2m), (hidden.Quantity, hidden.Unit, hidden.FindNumber, hidden.ExtendedQuantity)); // qty visible: rollups stay correct
        Assert.Null(hidden.Number); Assert.Null(hidden.Name); Assert.Null(hidden.PartType); Assert.Null(hidden.Revision); Assert.Null(hidden.State);
        Assert.Null(hidden.Banner); Assert.Null(hidden.Markings); Assert.Null(hidden.MasterId); Assert.Empty(hidden.ReferenceDesignators);
        Assert.False(hidden.HasChildren);
        Assert.Equal(12m, lena.Lines[2].ExtendedQuantity); // 4 x 3 below the open sub
        Assert.Equal("UNCLASSIFIED · DISTRIBUTION A", lena.Banner); // the banner reveals nothing about what is hidden
        foreach (var f in s.Forbidden) Assert.DoesNotContain(f, Json(lena), StringComparison.Ordinal);

        // A cannot-read line cannot be expanded (it looks like a line that does not exist).
        Assert.Equal(OutcomeKind.NotFound, (await s.Env.Bom.ExplodeAsync(Lena, new BomQuery(s.Top, ParentLine: "10"), null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await s.Env.Bom.ExplodeAsync(Lena, new BomQuery(s.Top, ParentLine: "10.10"), null)).Kind);

        // A user who may read everything sees it all, including the controlled banner.
        var alice = (await s.Env.Bom.ExplodeAsync(Alice, new BomQuery(s.Top), null)).Value!;
        Assert.Equal(["10", "10.10", "20", "20.10", "30"], alice.Lines.Select(l => l.Path));
        Assert.Equal(0, alice.RedactedCount);
        Assert.Equal(s.HiddenSub, alice.Lines[0].Number);
        Assert.Equal("CUI//EXPT · ITAR · DISTRIBUTION D", alice.Banner);
    }

    [Fact]
    public async Task No_BOM_surface_discloses_a_hidden_part()
    {
        var s = await BuildScene();
        var bom = s.Env.Bom;

        // Change a hidden line (quantity 1 -> 5) in A.3 so the comparison has something hidden to show.
        await s.Env.Checkout(Alice, s.Top);
        var secretLine = (await bom.GetWorkingAsync(Alice, s.Top, null)).Value!.Lines.Single(l => l.ChildNumber == s.SecretLeaf);
        await bom.UpdateUsageAsync(Alice, s.Top, secretLine.Id, new UpdateUsageCommand(5, "EA", 30), null);
        await s.Env.CheckIn(Alice, s.Top);

        var outputs = new List<(string Surface, string Content)>
        {
            ("explode", Json((await bom.ExplodeAsync(Lena, new BomQuery(s.Top), null)).Value)),
            ("summary", Json((await bom.SummaryAsync(Lena, new BomQuery(s.Top), false, null)).Value)),
            ("summary leaves", Json((await bom.SummaryAsync(Lena, new BomQuery(s.Top), true, null)).Value)),
            ("csv", (await bom.ExportCsvAsync(Lena, new BomQuery(s.Top), null)).Value!.Csv),
            ("where-used leaf", Json((await bom.WhereUsedAsync(Lena, s.OpenLeaf, 3, null)).Value)),
            ("compare", Json((await bom.CompareAsync(Lena, s.Top, "A.2", "A.3", null)).Value)),
        };
        foreach (var (surface, content) in outputs)
            foreach (var f in s.Forbidden)
                Assert.False(content.Contains(f, StringComparison.Ordinal), $"{surface} disclosed '{f}'");

        // The surfaces still work and show the placeholders.
        var used = (await bom.WhereUsedAsync(Lena, s.OpenLeaf, 3, null)).Value!;
        Assert.Contains(used.Rows, r => r.Redacted && r.Level == 1 && r.Quantity == 2m && r.ParentNumber is null);
        Assert.Contains(used.Rows, r => !r.Redacted && r.ParentNumber == s.OpenSub);
        var diff = (await bom.CompareAsync(Lena, s.Top, "A.2", "A.3", null)).Value!;
        var changed = Assert.Single(diff.Lines);
        Assert.True(changed.Redacted);
        Assert.Equal(["Quantity 1 to 5"], changed.Changes);
        var csv = (await bom.ExportCsvAsync(Lena, new BomQuery(s.Top), null)).Value!.Csv;
        Assert.Contains("Restricted item", csv, StringComparison.Ordinal);
        var summary = (await bom.SummaryAsync(Lena, new BomQuery(s.Top), false, null)).Value!;
        Assert.Equal(2, summary.Lines.Count(l => l.Redacted)); // never merged: that would reveal they are the same hidden part
        Assert.All(summary.Lines.Where(l => l.Redacted), l => Assert.Null(l.Number));

        // Asking about a hidden part directly looks like asking about a part that does not exist.
        Assert.Equal(OutcomeKind.NotFound, (await bom.ExplodeAsync(Lena, new BomQuery(s.HiddenSub), null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await bom.WhereUsedAsync(Lena, s.SecretLeaf, 1, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await bom.CompareAsync(Lena, s.HiddenSub, "A.1", "A.2", null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await bom.ExportCsvAsync(Lena, new BomQuery(s.HiddenSub), null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await bom.GetWorkingAsync(Lena, s.HiddenSub, null)).Kind);
        var audit = await s.Env.Audit();
        Assert.Contains(audit, e => e.Action == "bom.explode" && e.Outcome == AuditOutcome.Denied && e.ObjectId == s.HiddenSub);
        Assert.Contains(audit, e => e.Action == "bom.export" && e.Outcome == AuditOutcome.Denied);
        Assert.Contains(audit, e => e.Action == "bom.where_used" && e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task The_working_copy_shows_children_that_became_unreadable_as_placeholders()
    {
        var s = await BuildScene();
        var asm = await s.Env.CreatePart(Lena, "Lena's assembly", "Assembly");
        await s.Env.Checkout(Lena, asm);
        await s.Env.Add(Lena, asm, s.OpenLeaf, 2);
        var open2 = await s.Env.CreatePart(Alice, "Soon controlled");
        await s.Env.Add(Lena, asm, open2, 1);
        // The part is later marked ITAR by its owner; Lena's working copy must not keep showing it.
        await s.Env.Parts.ChangeMarkingsAsync(Alice, open2, Itar(), "export review", null);

        var working = (await s.Env.Bom.GetWorkingAsync(Lena, asm, null)).Value!;
        Assert.Equal([false, true], working.Lines.Select(l => l.Redacted));
        Assert.Null(working.Lines[1].ChildNumber);
        Assert.DoesNotContain(open2, Json(working), StringComparison.Ordinal);
        Assert.Equal("UNCLASSIFIED · DISTRIBUTION A", working.Banner);
    }

    [Fact]
    public async Task A_user_without_a_read_role_gets_nothing_and_the_denial_is_audited()
    {
        var s = await BuildScene();
        var nobody = User("nobody", roles: []);
        Assert.Equal(OutcomeKind.NotFound, (await s.Env.Bom.ExplodeAsync(nobody, new BomQuery(s.Top), "corr-9")).Kind);
        var denial = Assert.Single(await s.Env.Audit(), e => e.Action == "bom.explode" && e.Outcome == AuditOutcome.Denied);
        Assert.Equal(("corr-9", s.Top), (denial.CorrelationId, denial.ObjectId));
        Assert.Contains("role.missing", denial.Payload, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- SQL filter vs in-memory policy

    private static readonly Subject NoCui = User("nocui") with { CuiCategories = [] };
    private static readonly Subject[] Subjects = [Alice, Lena, Carmen, NoCui, User("farid", usPerson: false, auths: ["TAA-0042"])];

    /// <summary>The in-memory oracle: walk the generated model applying <see cref="AccessPolicy.Evaluate"/> exactly as the SQL must.</summary>
    private static List<(string Path, string? Number, bool Redacted, bool Unresolved)> Oracle(SyntheticBom bom, Subject subject, BomConfigSpec spec)
    {
        var children = bom.Lines.GroupBy(l => l.ParentNumber).ToDictionary(g => g.Key, g => g.OrderBy(l => l.FindNumber).ToList());
        var result = new List<(string, string?, bool, bool)>();
        void Walk(string parent, string path)
        {
            foreach (var l in children.GetValueOrDefault(parent, []))
            {
                var p = path.Length == 0 ? l.FindNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{path}.{l.FindNumber}";
                var child = bom.ByNumber[l.ChildNumber];
                if (!AccessPolicy.Evaluate(subject, child.Markings, PlmAction.Read).Permitted) { result.Add((p, null, true, false)); continue; }
                var unresolved = spec == BomConfigSpec.LatestReleased && !child.Released;
                result.Add((p, child.Number, false, unresolved));
                if (!unresolved) Walk(child.Number, p);
            }
        }
        Walk(bom.RootNumber, "");
        return [.. result.OrderBy(r => r.Item1, Comparer<string>.Create(ComparePaths))];
    }

    private static int ComparePaths(string a, string b)
    {
        var x = a.Split('.').Select(int.Parse).ToArray();
        var y = b.Split('.').Select(int.Parse).ToArray();
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return x.Length.CompareTo(y.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Explosion_and_where_used_agree_with_the_in_memory_policy_for_every_subject(int seed)
    {
        var db = await pg.CreateDatabaseAsync();
        await using var cleanup = new AsyncDisposable(db.DropAsync);
        var ds = db.AppDataSource();
        var bom = await SyntheticBomGenerator.InsertAsync(ds, new SyntheticBomOptions
        {
            Seed = seed, Levels = 5, TargetLines = 600, RootChildren = 8, SharedPartCount = 6, ReleasedFraction = 0.5, NumberPrefix = $"G{seed}",
        });
        var svc = new BomService(ds, new PostgresAuditLog(ds, TimeProvider.System), TimeProvider.System);

        foreach (var spec in new[] { BomConfigSpec.Latest, BomConfigSpec.LatestReleased })
            foreach (var subject in Subjects)
            {
                var actual = (await svc.ExplodeAsync(subject, new BomQuery(bom.RootNumber, Spec: spec), null)).Value!;
                var expected = Oracle(bom, subject, spec);
                Assert.Equal(expected, actual.Lines.Select(l => (l.Path, l.Number, l.Redacted, l.Unresolved)).ToList());
                Assert.All(actual.Lines.Where(l => l.Redacted), l => { Assert.Null(l.Name); Assert.Null(l.Markings); Assert.False(l.HasChildren); });
                Assert.All(actual.Lines.Where(l => l.Markings is not null), l => Assert.Equal(bom.ByNumber[l.Number!].Markings.BannerText(), l.Banner));
            }

        foreach (var shared in bom.SharedPartNumbers.Take(3))
            foreach (var subject in Subjects)
            {
                var actual = (await svc.WhereUsedAsync(subject, shared, 1, null)).Value!;
                var parents = bom.Lines.Where(l => l.ChildNumber == shared).Select(l => l.ParentNumber).ToList();
                // Level 1 lists every parent usage; each parent is a placeholder exactly when the policy denies it.
                Assert.Equal(parents.Count, actual.Rows.Count);
                Assert.Equal(parents.Count(p => !AccessPolicy.Evaluate(subject, bom.ByNumber[p].Markings, PlmAction.Read).Permitted), actual.Rows.Count(r => r.Redacted));
                Assert.Equal(parents.Where(p => AccessPolicy.Evaluate(subject, bom.ByNumber[p].Markings, PlmAction.Read).Permitted).Order(StringComparer.Ordinal),
                    actual.Rows.Where(r => !r.Redacted).Select(r => r.ParentNumber!).Order(StringComparer.Ordinal));
            }
    }

    private sealed class AsyncDisposable(Func<Task> action) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await action();
    }
}
