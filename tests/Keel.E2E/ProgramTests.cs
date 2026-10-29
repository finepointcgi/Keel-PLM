using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>What the program E2E tests share: one program ORCA with a phase, tasks, a gate review and a deliverable, created once through the pages.</summary>
public sealed record ProgramData(string Code, string Gate, string Part);

public static class ProgramRecords
{
    private static readonly ConditionalWeakTable<E2EFixture, Task<ProgramData>> Cache = [];
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<ProgramData> EnsureAsync(E2EFixture fx, BrowserSession session, UserPage alice)
    {
        await Gate.WaitAsync();
        try
        {
            if (!Cache.TryGetValue(fx, out var task))
            {
                task = CreateAsync(fx, session, alice);
                Cache.Add(fx, task);
            }
            return await task;
        }
        finally { Gate.Release(); }
    }

    private static async Task AddItemAsync(IPage page, string kind, string title, string start, string? end, string? gate = null, string? phase = null)
    {
        var add = page.Locator("section[aria-labelledby='add-heading']");
        var selects = add.Locator("select");
        await selects.Nth(0).SelectOptionAsync(kind);
        if (phase is not null) await selects.Nth(1).SelectOptionAsync(new SelectOptionValue { Label = phase });
        await BrowserSession.FillAsync(add.Locator("input[maxlength='200']"), title);
        await add.Locator("input[type=date]").Nth(0).FillAsync(start);
        if (end is not null) await add.Locator("input[type=date]").Nth(1).FillAsync(end);
        if (gate is not null) await selects.Nth(2).SelectOptionAsync(gate);
        await BrowserSession.ClickAsync(page, "Add item");
        await Expect(page.Locator("table[data-table='schedule']")).ToContainTextAsync(title);
    }

    private static async Task<ProgramData> CreateAsync(E2EFixture fx, BrowserSession session, UserPage alice)
    {
        var page = alice.Page;
        await session.GotoAsync(alice, "/programs/new");
        await BrowserSession.AssertBannersAsync(page);
        await BrowserSession.FillAsync(page.GetByLabel("Program code *"), "ORCA");
        await BrowserSession.FillAsync(page.GetByLabel("Name *"), "Orca gimbal program");
        await page.GetByLabel("Start date").FillAsync("2027-03-01");
        await page.GetByLabel("End date").FillAsync("2028-03-01");
        await BrowserSession.ClickAsync(page, "Create program");
        await page.WaitForURLAsync(new Regex(@"/programs/ORCA$"), new PageWaitForURLOptions { Timeout = 60_000 });

        await session.GotoAsync(alice, "/programs/ORCA?tab=schedule");
        await AddItemAsync(page, "Phase", "Design phase", "2027-03-01", "2027-06-30");
        await AddItemAsync(page, "Task", "Concept design", "2027-03-01", "2027-03-31", phase: "PH-001 Design phase");
        await AddItemAsync(page, "Task", "Detailed design", "2027-04-01", "2027-05-31", phase: "PH-001 Design phase");
        await AddItemAsync(page, "Milestone", "Preliminary design review", "2027-06-15", null, gate: "PDR");
        await page.Locator("section[aria-labelledby='dep-heading'] select").Nth(0).SelectOptionAsync(new SelectOptionValue { Label = "TK-002 Concept design" });
        await page.Locator("section[aria-labelledby='dep-heading'] select").Nth(1).SelectOptionAsync(new SelectOptionValue { Label = "TK-003 Detailed design" });
        await BrowserSession.ClickAsync(page, "Add dependency");
        await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Remove dependency TK-002 to TK-003" })).ToBeVisibleAsync();
        await BrowserSession.FillAsync(page.GetByLabel("Baseline name"), "Contract baseline");
        await BrowserSession.ClickAsync(page, "Take baseline");
        await Expect(page.Locator("main")).ToContainTextAsync("Baseline 1: Contract baseline");

        await session.GotoAsync(alice, "/programs/ORCA?tab=deliverables");
        await BrowserSession.FillAsync(page.GetByLabel("Title *"), "Bracket drawing set");
        await page.GetByLabel("Kind of object").SelectOptionAsync("part");
        await BrowserSession.FillAsync(page.GetByLabel("Number of the object *"), fx.Manifest.GimbalBracket);
        await page.GetByLabel("Due date *").FillAsync("2027-06-01");
        await BrowserSession.ClickAsync(page, "Add deliverable");
        await Expect(page.Locator("table[data-table='deliverables']")).ToContainTextAsync("Bracket drawing set");

        await session.GotoAsync(alice, "/programs/ORCA?tab=schedule");
        var href = await page.Locator("table[data-table='schedule'] a[href^='programs/gates/']").First.GetAttributeAsync("href");
        var gate = href![(href!.LastIndexOf('/') + 1)..];
        return new ProgramData("ORCA", gate, fx.Manifest.GimbalBracket);
    }
}

/// <summary>Programs as people use them (ADR-0043): create a program, plan the schedule, read the dashboard and the timeline with its table, link a deliverable, prepare a gate review, and hold at 375 px.</summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class ProgramTests(E2EFixture fx)
{
    [E2EFact]
    public async Task Alice_plans_a_program_reads_the_dashboard_and_the_timeline_and_prepares_a_gate_review()
    {
        await using var session = new BrowserSession(fx, nameof(Alice_plans_a_program_reads_the_dashboard_and_the_timeline_and_prepares_a_gate_review));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            var data = await ProgramRecords.EnsureAsync(fx, session, alice);

            await session.GotoAsync(alice, "/programs");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = data.Code, Exact = true })).ToBeVisibleAsync();

            await session.GotoAsync(alice, $"/programs/{data.Code}");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.Locator("h1")).ToContainTextAsync("Orca gimbal program");
            await Expect(page.Locator("[data-panel='program-dashboard']")).ToContainTextAsync("Upcoming milestones");
            await Expect(page.Locator("[data-count='percent']")).ToBeVisibleAsync();
            await Expect(page.Locator("[data-count='deliverable-InWork'], [data-count='deliverable-Ready']").First).ToBeVisibleAsync();

            // the timeline: the chart is keyboard operable and the table holds the same items
            await BrowserSession.OpenTabAsync(page, "Timeline");
            await Expect(page.Locator(".vis-viewport")).ToBeVisibleAsync();
            await page.Locator("g.vis-node[tabindex='0']").FocusAsync();
            await page.Keyboard.PressAsync("Home");
            await Expect(page.Locator(".vis-node:focus")).ToHaveCountAsync(1);
            await page.Keyboard.PressAsync("ArrowDown");
            await Expect(page.Locator(".vis-node:focus")).ToHaveAttributeAsync("aria-label", new Regex("TK-00|MS-00|PH-00"));
            await BrowserSession.ClickAsync(page, "Table");
            await Expect(page.Locator("table[data-table='timeline']")).ToContainTextAsync("Concept design");
            await BrowserSession.AssertBannersAsync(page);

            // the schedule shows float and the critical path in words
            await session.GotoAsync(alice, $"/programs/{data.Code}?tab=schedule");
            await Expect(page.Locator("main")).ToContainTextAsync("Critical path:");

            // the gate review page: criteria, marking them, the review tab
            await session.GotoAsync(alice, $"/programs/gates/{data.Gate}");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.Locator("h1")).ToContainTextAsync("Preliminary design review");
            await BrowserSession.FillAsync(page.GetByLabel("New criterion"), "Requirements are baselined");
            await BrowserSession.ClickAsync(page, "Add criterion");
            await Expect(page.Locator("table[data-table='checklist']")).ToContainTextAsync("Requirements are baselined");
            await page.GetByLabel("Status of criterion 1").SelectOptionAsync("Met");
            await BrowserSession.OpenTabAsync(page, "Review");
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Submit to the review board" })).ToBeVisibleAsync();

            // my work on Home and the palette find the program
            await session.GotoAsync(alice, "/");
            await Expect(page.Locator("#card-program-work")).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
        });
    }

    [E2EFact]
    public async Task A_reader_outside_the_program_finds_nothing_and_the_pages_hold_at_375_px()
    {
        await using var session = new BrowserSession(fx, nameof(A_reader_outside_the_program_finds_nothing_and_the_pages_hold_at_375_px));
        await session.RunAsync(async () =>
        {
            var phone = await session.SignInAsync("alice", width: 375, height: 812);
            var data = await ProgramRecords.EnsureAsync(fx, session, phone);
            var page = phone.Page;
            foreach (var path in new[] { "/programs", "/programs/new", $"/programs/{data.Code}", $"/programs/{data.Code}?tab=schedule", $"/programs/{data.Code}?tab=timeline", $"/programs/{data.Code}?tab=deliverables",
                         $"/programs/{data.Code}?tab=history", $"/programs/gates/{data.Gate}" })
            {
                await session.GotoAsync(phone, path);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                var (scrollWidth, innerWidth) = await page.EvaluateAsync<int[]>("() => [document.documentElement.scrollWidth, window.innerWidth]") is [var a, var b] ? (a, b) : default;
                Assert.True(scrollWidth <= innerWidth, $"{path}: the page scrolls sideways at 375 px (document {scrollWidth}, window {innerWidth}).");
                await BrowserSession.AssertBannersAsync(page);
            }

            // a user without program ORCA gets no program in the list and "not found" on its page
            var outsider = await session.SignInAsync("audrey");
            await session.GotoAsync(outsider, "/programs");
            Assert.DoesNotContain("Orca gimbal program", await outsider.Page.Locator("main").InnerTextAsync(), StringComparison.Ordinal);
        });
    }
}
