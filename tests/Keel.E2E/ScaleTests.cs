using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// The UI at scale (gap wave D, ADR-0034): a ten-level, 50,000-line structure in its own throwaway database behind its own host (<see cref="E2EFixture.EnsureScaleHostAsync"/>).
/// However many lines the service returns, the browser must hold only the rows in view: the structure grid is virtualized, the purchasing summary, where-used and comparison
/// tables are paged, the treemap folds small tiles. The marking banners stay on every page, and the audit finds no accessibility violation with the big grid on the page.
/// The time budgets are generous (several times the measured values in docs/ops/performance.md); the DOM budgets are the point.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class ScaleTests(E2EFixture fx)
{
    private const int MaxDomNodes = 4000;
    private const int MaxGridRows = 120;

    private static Task<int> DomNodesAsync(IPage page) => page.EvaluateAsync<int>("document.querySelectorAll('*').length");

    [E2EFact(Timeout = 600_000)]
    public async Task A_fifty_thousand_line_structure_keeps_the_dom_small_in_every_view_and_stays_accessible()
    {
        await fx.EnsureScaleHostAsync();
        await using var session = new BrowserSession(fx, nameof(A_fifty_thousand_line_structure_keeps_the_dom_small_in_every_view_and_stays_accessible));
        await session.RunAsync(async () =>
        {
            var bom = fx.ScaleBom!;
            Assert.InRange(bom.Lines.Count, 49_900, 50_100);
            var alice = await session.SignInAsync("alice", baseUrl: fx.ScaleHostUrl);
            var page = alice.Page;
            var axe = await File.ReadAllTextAsync(Path.Combine(fx.RepoRoot, "tests", "Keel.E2E", "vendor", "axe-core", "axe.min.js"));
            var findings = new List<string>();

            // The structure tab opens one level (what is needed first), instantly.
            var sw = Stopwatch.StartNew();
            await page.GotoAsync($"{fx.ScaleHostUrl}/parts/{bom.RootNumber}?tab=structure");
            var grid = page.Locator("[role=treegrid].bom-grid");
            await Expect(grid).ToBeVisibleAsync();
            await Expect(page.Locator(".bom-row[data-path]").First).ToBeVisibleAsync();
            var firstLevelMs = sw.ElapsedMilliseconds;
            await BrowserSession.AssertBannersAsync(page);
            Assert.InRange(await page.Locator(".bom-row[data-path]").CountAsync(), 1, MaxGridRows);
            Assert.True(firstLevelMs < 20_000, $"The first level took {firstLevelMs} ms.");

            // Every level at once: 50,000 lines go to the circuit, a few dozen rows to the browser.
            sw.Restart();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Expand all", Exact = true }).ClickAsync();
            await Expect(page.GetByText(new Regex(@"\b(49\d{3}|50\d{3}) lines shown"))).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 120_000 });
            var expandMs = sw.ElapsedMilliseconds;
            var rows = await page.Locator(".bom-row[data-path]").CountAsync();
            var nodes = await DomNodesAsync(page);
            var rowCount = int.Parse(await grid.GetAttributeAsync("aria-rowcount") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(rowCount > 49_000, $"aria-rowcount is {rowCount}.");
            Assert.True(rows <= MaxGridRows, $"{rows} grid rows are in the DOM for {rowCount} lines.");
            Assert.True(nodes < MaxDomNodes, $"The page has {nodes} DOM nodes with the full structure loaded.");
            Assert.True(expandMs < 90_000, $"Expanding all 50,000 lines took {expandMs} ms.");
            await BrowserSession.AssertBannersAsync(page);

            // Scroll to the end: the window moves, the DOM stays small, and the last line is reachable.
            await page.Locator(".bom-body").EvaluateAsync("el => { el.scrollTop = el.scrollHeight; }");
            await Expect(page.Locator($".bom-row[aria-rowindex='{rowCount}']")).ToBeAttachedAsync(new LocatorAssertionsToBeAttachedOptions { Timeout = 20_000 });
            Assert.True(await page.Locator(".bom-row[data-path]").CountAsync() <= MaxGridRows);
            Assert.True(await DomNodesAsync(page) < MaxDomNodes);
            // And a scroll to the middle.
            await page.Locator(".bom-body").EvaluateAsync("el => { el.scrollTop = el.scrollHeight / 2; }");
            await page.WaitForTimeoutAsync(400);
            Assert.True(await page.Locator(".bom-row[data-path]").CountAsync() <= MaxGridRows);

            // Keyboard: End moves to the last row without growing the DOM.
            await page.Locator(".bom-row[data-path]").First.FocusAsync();
            await page.Keyboard.PressAsync("End");
            await page.WaitForTimeoutAsync(500);
            Assert.True(await page.Locator(".bom-row[data-path]").CountAsync() <= MaxGridRows);

            await AccessibilityTests.RunAxeAsync(page, "scale structure grid", axe, findings);

            // The purchasing summary is paged: one row per distinct part would be tens of thousands.
            await page.GetByLabel("Purchasing summary").CheckAsync();
            await Expect(page.Locator("table.data tbody tr").First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
            var summaryRows = await page.Locator("table.data tbody tr").CountAsync();
            Assert.InRange(summaryRows, 1, 100);
            await Expect(page.GetByRole(AriaRole.Navigation, new PageGetByRoleOptions { Name = "Paging" })).ToContainTextAsync("Showing 1–100 of");
            Assert.True(await DomNodesAsync(page) < MaxDomNodes);
            await BrowserSession.AssertBannersAsync(page);
            await AccessibilityTests.RunAxeAsync(page, "scale purchasing summary", axe, findings);
            var nextPage = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Next", Exact = true });
            await nextPage.ClickAsync();
            await Expect(page.GetByRole(AriaRole.Navigation, new PageGetByRoleOptions { Name = "Paging" })).ToContainTextAsync("Showing 101–200 of");

            // The treemap folds small tiles and keeps a bounded number of elements.
            await page.GetByLabel("Treemap", new PageGetByLabelOptions { Exact = true }).CheckAsync();
            await Expect(page.Locator("svg.vis-svg")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
            Assert.True(await DomNodesAsync(page) < 6000, "The treemap of 50,000 lines drew too many elements.");
            await BrowserSession.AssertBannersAsync(page);
            await AccessibilityTests.RunAxeAsync(page, "scale treemap", axe, findings);

            // Where-used of a standard part used all over the structure: a paged table, all levels.
            var shared = bom.SharedPartNumbers[0];
            sw.Restart();
            await page.GotoAsync($"{fx.ScaleHostUrl}/parts/{shared}/where-used");
            await Expect(page.Locator("h1")).ToBeVisibleAsync();
            await page.GetByLabel("Levels up").SelectOptionAsync(new SelectOptionValue { Label = "All (10)" });
            await Expect(page.Locator("table.data tbody tr").First).ToBeVisibleAsync();
            var whereUsedRows = await page.Locator("table.data tbody tr").CountAsync();
            Assert.InRange(whereUsedRows, 1, 100);
            Assert.True(await DomNodesAsync(page) < MaxDomNodes);
            Assert.True(sw.ElapsedMilliseconds < 30_000, $"Where-used took {sw.ElapsedMilliseconds} ms.");
            await BrowserSession.AssertBannersAsync(page);
            await AccessibilityTests.RunAxeAsync(page, "scale where-used", axe, findings);

            // The part list and a re-sort at 35,000+ parts, by the server.
            sw.Restart();
            await page.GotoAsync($"{fx.ScaleHostUrl}/parts");
            await Expect(page.Locator("table.data tbody tr").First).ToBeVisibleAsync();
            Assert.True(await page.Locator("table.data tbody tr").CountAsync() <= 50);
            await page.GotoAsync($"{fx.ScaleHostUrl}/parts?sort=name&dir=desc");
            await Expect(page.Locator("table.data tbody tr").First).ToBeVisibleAsync();
            Assert.True(sw.ElapsedMilliseconds < 30_000, $"The part list and a re-sort took {sw.ElapsedMilliseconds} ms.");
            await BrowserSession.AssertBannersAsync(page);
            await AccessibilityTests.RunAxeAsync(page, "scale part list", axe, findings);

            Assert.True(findings.Count == 0, $"{findings.Count} accessibility violation(s) at scale:\n" + string.Join("\n", findings));
        });
    }
}
