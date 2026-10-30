using System.Runtime.CompilerServices;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>What the resource and cost E2E tests share: a pool, a role with a rate, a resource, two overlapping assignments (so one is over-allocated), a budget, an actual cost and a material line on program ORCA.</summary>
public static class ProgramResourceRecords
{
    private static readonly ConditionalWeakTable<E2EFixture, Task<bool>> Cache = [];
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task EnsureAsync(E2EFixture fx, BrowserSession session, UserPage alice, ProgramData program)
    {
        await Gate.WaitAsync();
        try
        {
            if (!Cache.TryGetValue(fx, out var task))
            {
                task = CreateAsync(fx, session, alice, program);
                Cache.Add(fx, task);
            }
            await task;
        }
        finally { Gate.Release(); }
    }

    private static async Task<bool> CreateAsync(E2EFixture fx, BrowserSession session, UserPage alice, ProgramData program)
    {
        var page = alice.Page;
        await session.GotoAsync(alice, $"/programs/{program.Code}?tab=resources");
        await BrowserSession.FillAsync(page.GetByLabel("Pool name *"), "Mechanical engineering");
        await BrowserSession.ClickAsync(page, "Add pool");
        await Expect(page.Locator("table[data-table='roles']")).ToContainTextAsync("Mechanical engineering");
        await page.GetByLabel("Pool *").SelectOptionAsync(new SelectOptionValue { Label = "RP-001 Mechanical engineering" });
        await BrowserSession.FillAsync(page.GetByLabel("Role name *"), "Design engineer");
        await BrowserSession.FillAsync(page.GetByLabel("Rate per hour (optional)"), "100");
        await BrowserSession.ClickAsync(page, "Add role");
        await Expect(page.Locator("table[data-table='roles']")).ToContainTextAsync("Design engineer");
        await page.GetByLabel("Role *").SelectOptionAsync(new SelectOptionValue { Label = "RR-001 Design engineer" });
        await BrowserSession.FillAsync(page.GetByLabel("Resource name *"), "Design crew A");
        await BrowserSession.ClickAsync(page, "Add resource");
        await Expect(page.Locator("table[data-table='resources']")).ToContainTextAsync("Design crew A");
        await AssignAsync(page, "2027-03-01", "2027-03-31", "10");
        await AssignAsync(page, "2027-03-15", "2027-03-31", "4");

        await session.GotoAsync(alice, $"/programs/{program.Code}?tab=cost");
        await page.GetByLabel("Month *").FillAsync("2027-03");
        await BrowserSession.FillAsync(page.GetByLabel("Amount (USD) *").First, "30000");
        await BrowserSession.ClickAsync(page, "Set budget");
        await Expect(page.Locator("[data-total='budget']")).ToContainTextAsync("30,000.00");
        await page.GetByLabel("Date *").First.FillAsync("2027-03-02");
        await BrowserSession.FillAsync(page.GetByLabel("Resource *"), "RS-001");
        await BrowserSession.FillAsync(page.GetByLabel("Hours *"), "8");
        await BrowserSession.ClickAsync(page, "Record cost");
        await Expect(page.Locator("table[data-table='cost-actuals']")).ToContainTextAsync("AC-0001");
        await BrowserSession.FillAsync(page.GetByLabel("End item (part number) *"), fx.Manifest.OpenFastener);
        await BrowserSession.FillAsync(page.GetByLabel("Quantity *"), "10");
        await page.GetByLabel("Needed on *").FillAsync("2027-06-15");
        await BrowserSession.ClickAsync(page, "Add material line");
        await Expect(page.Locator("table[data-table='cost-material']")).ToContainTextAsync(fx.Manifest.OpenFastener);
        return true;
    }

    private static async Task AssignAsync(IPage page, string start, string end, string hours)
    {
        await BrowserSession.FillAsync(page.GetByLabel("Task (for example TK-001) *"), "TK-002");
        await page.GetByLabel("Resource *").SelectOptionAsync(new SelectOptionValue { Label = "RS-001 Design crew A" });
        await page.GetByLabel("Start *").FillAsync(start);
        await page.GetByLabel("End *").FillAsync(end);
        await BrowserSession.FillAsync(page.GetByLabel("Hours per day *"), hours);
        var before = await page.Locator("table[data-table='assignments'] tbody tr").CountAsync();
        await BrowserSession.ClickAsync(page, "Assign resource");
        await Expect(page.Locator("table[data-table='assignments'] tbody tr")).ToHaveCountAsync(before + 1);
    }
}

/// <summary>Resources and cost of a program as people use them (ADR-0049): build the resource plan, see the load picture and the over-allocation, read budget against actual against estimate to complete, export, and hold at 375 px; a member without the cost permission sees hours only.</summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class ProgramResourceTests(E2EFixture fx)
{
    [E2EFact]
    public async Task Alice_plans_resources_sees_the_over_allocation_and_reads_the_cost_with_a_table_and_an_export()
    {
        await using var session = new BrowserSession(fx, nameof(Alice_plans_resources_sees_the_over_allocation_and_reads_the_cost_with_a_table_and_an_export));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            var data = await ProgramRecords.EnsureAsync(fx, session, alice);
            await ProgramResourceRecords.EnsureAsync(fx, session, alice, data);

            await session.GotoAsync(alice, $"/programs/{data.Code}?tab=resources");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.Locator("[data-over-allocation='true']")).ToContainTextAsync("1 resource is over-allocated");
            await Expect(page.Locator("table[data-table='over-allocation']")).ToContainTextAsync("RS-001");
            await Expect(page.Locator("table[data-table='roles']")).ToContainTextAsync("100.00");

            // the load picture: one keyboard stop per resource, the table holds the same facts
            await Expect(page.Locator(".load-heat .vis-viewport")).ToBeVisibleAsync();
            await page.Locator("g.load-row[tabindex='0']").FocusAsync();
            Assert.Contains("over capacity", await page.EvaluateAsync<string>("() => document.activeElement?.getAttribute('aria-label') ?? ''"), StringComparison.Ordinal);
            await BrowserSession.ClickAsync(page, "Table");
            await Expect(page.Locator("table[data-table='load']")).ToContainTextAsync("Design crew A");
            await BrowserSession.AssertBannersAsync(page);

            // cost: totals, the curves with a table, the by-type table, the export
            await session.GotoAsync(alice, $"/programs/{data.Code}?tab=cost");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.Locator("[data-total='budget']")).ToContainTextAsync("30,000.00");
            await Expect(page.Locator("[data-total='actual']")).ToContainTextAsync("800.00");
            await Expect(page.Locator("svg.cost-chart")).ToBeVisibleAsync();
            Assert.Contains("Cumulative cost by month", (await page.Locator("svg.cost-chart desc").TextContentAsync())!, StringComparison.Ordinal);
            await BrowserSession.ClickAsync(page, "Table");
            await Expect(page.Locator("table[data-table='cost-months']")).ToContainTextAsync("Labor");
            await Expect(page.Locator("table[data-table='cost-types']")).ToContainTextAsync("Material");
            var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Export to Excel" }).ClickAsync());
            Assert.StartsWith("program-ORCA-resources-cost-", download.SuggestedFilename, StringComparison.Ordinal);
            Assert.EndsWith(".xlsx", download.SuggestedFilename, StringComparison.Ordinal);
        });
    }

    [E2EFact]
    public async Task A_member_without_the_cost_permission_sees_hours_and_over_allocation_but_no_rate_and_no_cost_tab()
    {
        await using var session = new BrowserSession(fx, nameof(A_member_without_the_cost_permission_sees_hours_and_over_allocation_but_no_rate_and_no_cost_tab));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var data = await ProgramRecords.EnsureAsync(fx, session, alice);
            await ProgramResourceRecords.EnsureAsync(fx, session, alice, data);

            var bob = await session.SignInAsync("bob");
            var page = bob.Page;
            await session.GotoAsync(bob, $"/programs/{data.Code}?tab=resources");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.Locator("table[data-table='resources']")).ToContainTextAsync("Design crew A");
            await Expect(page.Locator("[data-over-allocation='true']")).ToBeVisibleAsync();
            await Expect(page.Locator("main")).ToContainTextAsync("Rates are shown to holders of the program cost permission");
            Assert.DoesNotContain("100.00", await page.Locator("main").InnerTextAsync(), StringComparison.Ordinal);
            await Expect(page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Cost" })).ToHaveCountAsync(0);
            // asking for the cost tab by address does not show a cost either
            await session.GotoAsync(bob, $"/programs/{data.Code}?tab=cost");
            Assert.DoesNotContain("30,000.00", await page.Locator("main").InnerTextAsync(), StringComparison.Ordinal);
            var export = await page.Context.APIRequest.GetAsync($"{fx.BaseUrl}/ui/programs/{data.Code}/resources-export?format=csv&table=months");
            Assert.Equal(403, export.Status);
        });
    }

    [E2EFact]
    public async Task The_resource_and_cost_pages_hold_at_375_px()
    {
        await using var session = new BrowserSession(fx, nameof(The_resource_and_cost_pages_hold_at_375_px));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var data = await ProgramRecords.EnsureAsync(fx, session, alice);
            await ProgramResourceRecords.EnsureAsync(fx, session, alice, data);
            var phone = await session.SignInAsync("alice", width: 375, height: 812);
            var page = phone.Page;
            foreach (var tab in new[] { "resources", "cost" })
            {
                await session.GotoAsync(phone, $"/programs/{data.Code}?tab={tab}");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                var (scrollWidth, innerWidth) = await page.EvaluateAsync<int[]>("() => [document.documentElement.scrollWidth, window.innerWidth]") is [var a, var b] ? (a, b) : default;
                Assert.True(scrollWidth <= innerWidth, $"{tab}: the page scrolls sideways at 375 px (document {scrollWidth}, window {innerWidth}).");
                await BrowserSession.AssertBannersAsync(page);
            }
        });
    }
}
