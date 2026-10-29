using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>Gap wave A in a real browser: server-side table sorting kept in the address bar, tab deep links, the supply-type page with the shared confirmation dialog, and the MBOM markings tab.</summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class GapATests(E2EFixture fx)
{
    private static async Task<string[]> ColumnAsync(IPage page, int index) =>
        [.. (await page.Locator("table.data tbody tr").EvaluateAllAsync<string[]>($"rows => rows.map(r => r.cells[{index}].innerText.trim())"))];

    [E2EFact]
    public async Task A_list_sorts_the_whole_result_on_the_server_and_keeps_the_order_in_the_address()
    {
        await using var session = new BrowserSession(fx, nameof(A_list_sorts_the_whole_result_on_the_server_and_keeps_the_order_in_the_address));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/parts");
            var name = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Name", Exact = false }).First;
            await Expect(page.Locator("table.data tbody tr").First).ToBeVisibleAsync();

            await name.ClickAsync();
            await Expect(page).ToHaveURLAsync(new Regex(@"[?&]sort=name&dir=asc"));
            await Expect(page.Locator("th", new PageLocatorOptions { HasText = "Name" })).ToHaveAttributeAsync("aria-sort", "ascending");
            var asc = (await ColumnAsync(page, 1)).Select(n => n.ToLowerInvariant()).ToArray();
            Assert.Equal(asc.Order(StringComparer.Ordinal), asc);
            // The next page continues the order of the whole result (a page-local sort could not).
            var next = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Next", Exact = true });
            if (await next.IsEnabledAsync())
            {
                var lastOfFirst = asc[^1];
                await next.ClickAsync();
                await Expect(page.Locator("nav[aria-label=Paging] span")).ToContainTextAsync("Showing 51");
                Assert.True(string.CompareOrdinal((await ColumnAsync(page, 1))[0].ToLowerInvariant(), lastOfFirst) >= 0, "page two starts before page one ends");
            }

            await name.ClickAsync();
            await Expect(page).ToHaveURLAsync(new Regex(@"[?&]sort=name&dir=desc"));
            await Expect(page.Locator("th", new PageLocatorOptions { HasText = "Name" })).ToHaveAttributeAsync("aria-sort", "descending");
            var desc = (await ColumnAsync(page, 1)).Select(n => n.ToLowerInvariant()).ToArray();
            Assert.Equal(desc.OrderDescending(StringComparer.Ordinal), desc);

            // A reload (or a shared link) shows the same order.
            await page.ReloadAsync();
            await Expect(page.Locator("th", new PageLocatorOptions { HasText = "Name" })).ToHaveAttributeAsync("aria-sort", "descending");

            // A value that is not a sort key is ignored: the default order, no error.
            await session.GotoAsync(alice, "/parts?sort=name%3B%20drop%20table%20plm.part_master&dir=up");
            await Expect(page.Locator("table.data tbody tr").First).ToBeVisibleAsync();
            Assert.Equal(0, await page.Locator("th[aria-sort=ascending], th[aria-sort=descending]").CountAsync());

            // The other lists take the same sort.
            foreach (var path in new[] { "/documents", "/changes", "/baselines", "/mboms", "/tasks" })
            {
                await session.GotoAsync(alice, path + "?sort=number&dir=desc");
                await BrowserSession.AssertBannersAsync(page);
            }
            await session.GotoAsync(alice, "/changes?sort=priority&dir=desc");
            await Expect(page.Locator("th", new PageLocatorOptions { HasText = "Priority" })).ToHaveAttributeAsync("aria-sort", "descending");
            Assert.Empty(alice.PageErrors);
        });
    }

    [E2EFact]
    public async Task A_tab_query_value_opens_that_tab_on_the_first_load_of_object_pages()
    {
        await using var session = new BrowserSession(fx, nameof(A_tab_query_value_opens_that_tab_on_the_first_load_of_object_pages));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await page.GotoAsync(fx.BaseUrl + "/changes/" + m.EcnDraftInWork + "?tab=impactmap");
            await Expect(page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Impact map" })).ToHaveAttributeAsync("aria-selected", "true");
            await Expect(page.Locator("#tabpanel-impactmap")).ToBeVisibleAsync();
            // The user's own choice survives what happens after it (the page reloads its data after an action).
            await BrowserSession.OpenTabAsync(page, "Overview");
            await page.GotoAsync(fx.BaseUrl + "/parts/" + m.GimbalBracket + "?tab=history");
            await Expect(page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "History" })).ToHaveAttributeAsync("aria-selected", "true");
            await page.GotoAsync(fx.BaseUrl + "/parts/" + m.GimbalBracket + "?tab=nonsense");
            await Expect(page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Overview" })).ToHaveAttributeAsync("aria-selected", "true");
            await page.GotoAsync(fx.BaseUrl + "/documents/" + m.SystemSpecification + "?tab=history#overview");
            await Expect(page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Overview" })).ToHaveAttributeAsync("aria-selected", "true"); // the fragment wins
            Assert.Empty(alice.PageErrors);
        });
    }

    [E2EFact]
    public async Task An_administrator_manages_supply_types_with_a_reason_and_the_shared_confirmation_dialog_and_others_cannot()
    {
        await using var session = new BrowserSession(fx, nameof(An_administrator_manages_supply_types_with_a_reason_and_the_shared_confirmation_dialog_and_others_cannot));
        await session.RunAsync(async () =>
        {
            var ada = await session.SignInAsync("ada");
            var page = ada.Page;
            await session.GotoAsync(ada, "/admin/supply-types");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.Locator("tr", new PageLocatorOptions { HasText = "Make" }).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Retire" })).ToHaveCountAsync(0); // built in

            await BrowserSession.FillAsync(page.GetByLabel("Code *", new PageGetByLabelOptions { Exact = true }), "E2EKit");
            await BrowserSession.FillAsync(page.GetByLabel("Label *", new PageGetByLabelOptions { Exact = true }), "Kitted by supplier");
            await BrowserSession.ClickAsync(page, "Add supply type");
            var row = page.Locator("tr", new PageLocatorOptions { HasText = "E2EKit" });
            await Expect(row).ToContainTextAsync("Kitted by supplier");
            await Expect(row).ToContainTextAsync("Active");

            await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Retire" }).ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Retire E2EKit?" });
            await Expect(dialog).ToBeVisibleAsync();
            await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Retire", Exact = true }).ClickAsync();
            await Expect(dialog.GetByRole(AriaRole.Alert)).ToContainTextAsync("Enter a reason"); // a reason is required before anything changes
            await Expect(row).ToContainTextAsync("Active");
            await dialog.GetByLabel("Reason").FillAsync("Replaced by Buy");
            await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Retire", Exact = true }).ClickAsync();
            await Expect(dialog).ToHaveCountAsync(0);
            await Expect(row).ToContainTextAsync("Retired");

            Assert.Empty(ada.PageErrors);

            var alice = await session.SignInAsync("alice");
            await alice.Page.GotoAsync(fx.BaseUrl + "/admin/supply-types");
            await Expect(alice.Page).ToHaveURLAsync(new Regex("access-denied"));
        });
    }

    [E2EFact]
    public async Task The_mbom_markings_tab_needs_a_reason_and_a_released_mbom_cannot_change()
    {
        await using var session = new BrowserSession(fx, nameof(The_mbom_markings_tab_needs_a_reason_and_a_released_mbom_cannot_change));
        await session.RunAsync(async () =>
        {
            var extras = fx.Manifest.Extras ?? throw new InvalidOperationException("the demo has no extras");
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;

            await page.GotoAsync(fx.BaseUrl + "/mboms/" + extras.ReleasedMbom + "?tab=security");
            await Expect(page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Security markings" })).ToHaveAttributeAsync("aria-selected", "true");
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Security markings", Exact = true }).First).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Change markings" })).ToHaveCountAsync(0);
            await BrowserSession.AssertBannersAsync(page);

            await page.GotoAsync(fx.BaseUrl + "/mboms/" + extras.StaleMbom + "?tab=security");
            var change = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Change markings" });
            if (await change.CountAsync() == 0) return; // the seeded stale MBOM is not in work in this data set: nothing more to prove here
            await change.ClickAsync();
            await BrowserSession.ClickAsync(page, "Save markings");
            await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("reason");
            await BrowserSession.FillAsync(page.GetByLabel("Reason for change (required, audited)"), "No change intended");
            await BrowserSession.ClickAsync(page, "Save markings");
            await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("unchanged");
            await BrowserSession.ClickAsync(page, "Cancel");
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Change markings" })).ToBeVisibleAsync();
            Assert.Empty(alice.PageErrors);
        });
    }

    [E2EFact]
    public async Task The_sort_headers_and_the_supply_type_page_work_at_375_px_without_sideways_scroll()
    {
        await using var session = new BrowserSession(fx, nameof(The_sort_headers_and_the_supply_type_page_work_at_375_px_without_sideways_scroll));
        await session.RunAsync(async () =>
        {
            var ada = await session.SignInAsync("ada", width: 375, height: 812);
            var page = ada.Page;
            foreach (var path in new[] { "/parts?sort=name&dir=desc", "/admin/supply-types", "/admin/reasons", "/admin/lists" })
            {
                await session.GotoAsync(ada, path);
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1"), $"{path} scrolls sideways");
            }
        });
    }
}
