using System.Diagnostics;
using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.Security;
using Keel.TestSupport;

namespace Keel.IntegrationTests;

/// <summary>
/// The performance gate of ADR-0003/ADR-0013: a 10-level, 50,000-line structure explodes in under 2 s, through the real SQL filter,
/// on a warm run. Timings are logged as diagnostic messages. Generated with bulk SQL by <see cref="SyntheticBomGenerator"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BomPerformanceTests(PostgresFixture pg)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private static void Log(string message)
    {
        TestContext.Current.SendDiagnosticMessage(message);
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        // Optional: KEEL_PERF_LOG=<file> appends the timings to a file, for runs where diagnostic messages are not shown.
        if (Environment.GetEnvironmentVariable("KEEL_PERF_LOG") is { Length: > 0 } path) File.AppendAllText(path, message + Environment.NewLine);
    }

    private static async Task<(T Result, TimeSpan Elapsed)> Time<T>(Func<Task<T>> call)
    {
        var sw = Stopwatch.StartNew();
        var result = await call();
        return (result, sw.Elapsed);
    }

    [Fact]
    public async Task Ten_level_fifty_thousand_line_explosion_meets_the_two_second_budget()
    {
        var db = await pg.CreateDatabaseAsync();
        try
        {
        var ds = db.AppDataSource();
        var load = Stopwatch.StartNew();
        var bom = await SyntheticBomGenerator.InsertAsync(ds, new SyntheticBomOptions { Seed = 7, Levels = 10, TargetLines = 50_000 });
        await db.ExecuteAsSuperuserAsync("ANALYZE");
        Log($"Generated {bom.Parts.Count} parts and {bom.Lines.Count} lines in {load.Elapsed.TotalSeconds:F1} s; lines per level: {string.Join(",", bom.LinesPerLevel)}");
        Assert.Equal(10, bom.LinesPerLevel.Count);
        Assert.InRange(bom.Lines.Count, 49_900, 50_100);

        var svc = new BomService(ds, new PostgresAuditLog(ds, TimeProvider.System), TimeProvider.System);

        // Full access: U.S. person in the program, with every CUI category.
        var full = BomTestKit.Alice;
        var query = new BomQuery(bom.RootNumber);
        await svc.ExplodeAsync(full, query, null); // warm-up: plan cache, buffers, JIT
        var (explosion, fullTime) = await Time(() => svc.ExplodeAsync(full, query, null));
        Assert.True(explosion.IsOk, string.Join(" ", explosion.Errors));
        var lines = explosion.Value!.Lines;
        Log($"Full explosion, full access: {lines.Count} lines in {fullTime.TotalMilliseconds:F0} ms (budget {Budget.TotalMilliseconds:F0} ms)");
        Assert.Equal(bom.Lines.Count, lines.Count);
        Assert.Equal(0, explosion.Value.RedactedCount);
        Assert.Equal(10, lines.Max(l => l.Level));
        Assert.True(fullTime < Budget, $"Full explosion took {fullTime.TotalMilliseconds:F0} ms for {lines.Count} lines; budget is {Budget.TotalMilliseconds:F0} ms.");

        // Restricted user: sees open parts, everything else comes back as placeholders whose subtrees are pruned.
        var restricted = BomTestKit.Lena;
        await svc.ExplodeAsync(restricted, query, null);
        var (partial, partialTime) = await Time(() => svc.ExplodeAsync(restricted, query, null));
        var expected = ExpectedVisible(bom, restricted);
        Log($"Redaction path (non-U.S. person): {partial.Value!.Lines.Count} lines, {partial.Value.RedactedCount} redacted, in {partialTime.TotalMilliseconds:F0} ms; oracle expects {expected.Lines} lines / {expected.Redacted} redacted");
        Assert.Equal(expected.Lines, partial.Value.Lines.Count);
        Assert.Equal(expected.Redacted, partial.Value.RedactedCount);
        Assert.All(partial.Value.Lines.Where(l => l.Redacted), l => Assert.Null(l.Number));
        Assert.True(partialTime < Budget, $"Redacted explosion took {partialTime.TotalMilliseconds:F0} ms.");

        // A U.S. person outside program ORCA sees most of the tree (open, EAR, ITAR without program) but not ORCA subtrees.
        var outsider = BomTestKit.Carmen;
        await svc.ExplodeAsync(outsider, query, null);
        var (outside, outsideTime) = await Time(() => svc.ExplodeAsync(outsider, query, null));
        var expectedOutside = ExpectedVisible(bom, outsider);
        Log($"Redaction path (no program ORCA): {outside.Value!.Lines.Count} lines, {outside.Value.RedactedCount} redacted, in {outsideTime.TotalMilliseconds:F0} ms; oracle expects {expectedOutside.Lines} / {expectedOutside.Redacted}");
        Assert.Equal(expectedOutside.Lines, outside.Value.Lines.Count);
        Assert.Equal(expectedOutside.Redacted, outside.Value.RedactedCount);
        Assert.True(outsideTime < Budget, $"Partially visible explosion took {outsideTime.TotalMilliseconds:F0} ms.");

        // Where-used on the most widely used standard part.
        var hot = bom.Lines.GroupBy(l => l.ChildNumber).Where(g => bom.SharedPartNumbers.Contains(g.Key)).OrderByDescending(g => g.Count()).First();
        await svc.WhereUsedAsync(full, hot.Key, 1, null);
        var (used, usedTime) = await Time(() => svc.WhereUsedAsync(full, hot.Key, 1, null));
        Log($"Where-used {hot.Key} (1 level): {used.Value!.Rows.Count} parents in {usedTime.TotalMilliseconds:F0} ms");
        Assert.Equal(hot.Count(), used.Value.Rows.Count);
        var (usedAll, usedAllTime) = await Time(() => svc.WhereUsedAsync(full, hot.Key, 10, null));
        Log($"Where-used {hot.Key} (10 levels): {usedAll.Value!.Rows.Count} rows in {usedAllTime.TotalMilliseconds:F0} ms");
        Assert.True(usedTime < Budget && usedAllTime < Budget, "Where-used exceeded the budget.");

        // Summary and CSV over the same explosion.
        var (summary, summaryTime) = await Time(() => svc.SummaryAsync(full, query, true, null, ct: default));
        Log($"Summary (leaves): {summary.Value!.Lines.Count} distinct parts in {summaryTime.TotalMilliseconds:F0} ms");
        var (export, exportTime) = await Time(() => svc.ExportCsvAsync(full, query, null));
        Log($"CSV export: {export.Value!.Lines} lines, {export.Value.Csv.Length / 1024} KiB in {exportTime.TotalMilliseconds:F0} ms");
        Assert.True(exportTime < Budget);
        }
        finally
        {
            await db.DropAsync(); // keep the shared container small
        }
    }

    /// <summary>Walks the generated model with the in-memory policy: the oracle the SQL result must equal.</summary>
    private static (int Lines, int Redacted) ExpectedVisible(SyntheticBom bom, Subject subject)
    {
        var children = bom.Lines.GroupBy(l => l.ParentNumber).ToDictionary(g => g.Key, g => g.ToList());
        int lines = 0, redacted = 0;
        var stack = new Stack<string>();
        stack.Push(bom.RootNumber);
        while (stack.Count > 0)
            foreach (var l in children.GetValueOrDefault(stack.Pop(), []))
            {
                lines++;
                if (!AccessPolicy.Evaluate(subject, bom.ByNumber[l.ChildNumber].Markings, PlmAction.Read).Permitted) { redacted++; continue; }
                stack.Push(l.ChildNumber);
            }
        return (lines, redacted);
    }
}
