using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// RFQs beyond the portal and supplier scorecards as people use them (ADR-0054): a buyer starts a request from a part, a supplier quotes with price tiers and an exception,
/// the buyer compares and awards, the supplier sees what was awarded to it; a buyer publishes a scorecard and the supplier reads it, with the chart's table alternative.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class RfqTests(E2EFixture fx)
{
    private static readonly System.Text.RegularExpressions.Regex RequestUrl = new(@"/requests/(RFQ-\d+)$");

    [E2EFact]
    public async Task A_buyer_starts_a_request_from_a_part_a_supplier_quotes_with_tiers_and_the_buyer_compares_and_awards()
    {
        await using var session = new BrowserSession(fx, nameof(A_buyer_starts_a_request_from_a_part_a_supplier_quotes_with_tiers_and_the_buyer_compares_and_awards));
        await session.RunAsync(async () =>
        {
            await SourcingSeed.EnsureAsync(fx);
            var part = fx.Manifest.OpenFastener;
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;

            // From the part's Sourcing tab: one click to a request that already lists the part.
            await session.GotoAsync(alice, $"/parts/{part}?tab=sourcing");
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Request a quote for this part" }).ClickAsync();
            await Expect(page.Locator("h1")).ToContainTextAsync("New supplier request");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByLabel("Lines *", new PageGetByLabelOptions { Exact = false })).ToHaveValueAsync($"{part} 1");
            await BrowserSession.FillAsync(page.GetByLabel("Title *"), "E2E tiered quote");
            await BrowserSession.FillAsync(page.GetByLabel("Lines *", new PageGetByLabelOptions { Exact = false }), $"{part} 600 2027-03-01");
            await BrowserSession.FillAsync(page.GetByLabel("Suppliers *", new PageGetByLabelOptions { Exact = false }), SourcingSeed.SupplierA);
            await BrowserSession.ClickAsync(page, "Create draft request");
            await Expect(page).ToHaveURLAsync(RequestUrl);
            var number = RequestUrl.Match(page.Url).Groups[1].Value;
            await Expect(page.Locator("h1")).ToContainTextAsync("E2E tiered quote");
            await Expect(page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions { Name = "2027-03-01" })).ToBeVisibleAsync();
            await BrowserSession.ClickAsync(page, "Issue to suppliers");
            await Expect(page.Locator(".badge", new PageLocatorOptions { HasText = "Issued" }).First).ToBeVisibleAsync();

            // The supplier quotes: price tiers, lead time, an exception and how long the quote holds.
            var alpha = await session.SignInAsync("sup-alpha");
            await session.GotoAsync(alpha, $"/portal/requests/{number}");
            var ap = alpha.Page;
            await Expect(ap.GetByRole(AriaRole.Rowheader, new PageGetByRoleOptions { Name = part }).First).ToBeVisibleAsync();
            await BrowserSession.FillAsync(ap.GetByLabel("Price tiers for " + part), "100:2.50, 500:2.10");
            await BrowserSession.FillAsync(ap.GetByLabel("Lead time in days for " + part), "21");
            await BrowserSession.FillAsync(ap.GetByLabel("Exception for " + part), "Cannot ship before 1 July");
            await BrowserSession.FillAsync(ap.GetByLabel("Quote valid until"), DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd"));
            await BrowserSession.ClickAsync(ap, "Send response");
            await Expect(ap.GetByText("Your response was sent.")).ToBeVisibleAsync();
            await Expect(ap.GetByText("price tiers", new PageGetByTextOptions { Exact = false }).First).ToBeVisibleAsync();

            // The buyer compares the quotes side by side and awards the line.
            await session.GotoAsync(alice, $"/requests/{number}");
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Compare quotes" })).ToBeVisibleAsync();
            await Expect(page.GetByText("2.10 USD each")).ToBeVisibleAsync();
            await Expect(page.GetByText("Cannot ship before 1 July").First).ToBeVisibleAsync();
            await BrowserSession.ClickAsync(page, $"Award {part} to E2E Alpha Works");
            await Expect(page.GetByText("vendor list entry", new PageGetByTextOptions { Exact = false }).First).ToBeVisibleAsync(); // the seeded supplier already has an open entry for this part: it is linked, not duplicated
            await Expect(page.Locator(".badge", new PageLocatorOptions { HasText = "Closed" }).First).ToBeVisibleAsync(); // every line has an award
            await BrowserSession.AssertBannersAsync(page);

            // The supplier sees the award, and only its own.
            await session.GotoAsync(alpha, $"/portal/requests/{number}");
            await Expect(ap.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Awarded to you" })).ToBeVisibleAsync();
            await Expect(ap.GetByText("at 2.10 USD each", new PageGetByTextOptions { Exact = false })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(ap);
        });
    }

    [E2EFact]
    public async Task A_buyer_publishes_the_scorecard_the_supplier_reads_it_and_the_chart_has_a_table_alternative()
    {
        await using var session = new BrowserSession(fx, nameof(A_buyer_publishes_the_scorecard_the_supplier_reads_it_and_the_chart_has_a_table_alternative));
        await session.RunAsync(async () =>
        {
            await SourcingSeed.EnsureAsync(fx);
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, $"/suppliers/{SourcingSeed.SupplierA}?tab=scorecard");
            await Expect(page.Locator("#scorecard-heading")).ToBeVisibleAsync();
            await Expect(page.GetByText("out of 100").First).ToBeVisibleAsync();
            await Expect(page.GetByText("Not measured: Keel records no deliveries yet.")).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
            // The formula is on the page, in words.
            await page.GetByText("How the score is calculated").ClickAsync();
            await Expect(page.GetByText("Bands: 85 and above is good", new PageGetByTextOptions { Exact = false })).ToBeVisibleAsync();

            // Withdraw, then publish: the supplier sees nothing, then the scorecard.
            await BrowserSession.ClickAsync(page, "Withdraw from supplier");
            await Expect(page.GetByText("The supplier sees no scorecard in the portal.")).ToBeVisibleAsync();
            var alpha = await session.SignInAsync("sup-alpha");
            await session.GotoAsync(alpha, "/portal/scorecard");
            await Expect(alpha.Page.GetByText("Your buyer has not shared a scorecard with you.")).ToBeVisibleAsync();
            await BrowserSession.ClickAsync(page, "Publish to supplier");
            await Expect(page.GetByText("The supplier sees the scorecard published on")).ToBeVisibleAsync();

            await session.GotoAsync(alpha, "/portal/scorecard");
            var ap = alpha.Page;
            await Expect(ap.GetByText("Your buyer published this scorecard")).ToBeVisibleAsync();
            await Expect(ap.GetByRole(AriaRole.Navigation, new PageGetByRoleOptions { Name = "Main" }).GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "My scorecard", Exact = true })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(ap);
            // The chart is an image with a description; the same numbers are in the table behind the switch.
            await Expect(ap.Locator("svg.vis-trend")).ToBeVisibleAsync();
            await ap.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Table", Exact = true }).ClickAsync();
            await Expect(ap.GetByRole(AriaRole.Table, new PageGetByRoleOptions { Name = "Score over the last 12 months as a table" })).ToBeVisibleAsync();
            await Expect(ap.Locator("table[aria-label='Score over the last 12 months as a table'] tbody tr")).ToHaveCountAsync(12);
            // The supplier sees neither the buyer's quality records nor another supplier.
            await session.GotoAsync(alpha, $"/suppliers/{SourcingSeed.SupplierA}?tab=scorecard");
            await Expect(ap.GetByText("You are not authorized to open this page")).ToBeVisibleAsync();
        });
    }

    [E2EFact]
    public async Task The_new_pages_hold_at_375_px()
    {
        await using var session = new BrowserSession(fx, nameof(The_new_pages_hold_at_375_px));
        await session.RunAsync(async () =>
        {
            await SourcingSeed.EnsureAsync(fx);
            var alice = await session.SignInAsync("alice", 375, 812);
            foreach (var path in new[] { $"/requests/{SourcingSeed.RequestQuoted}", $"/suppliers/{SourcingSeed.SupplierA}?tab=scorecard", $"/requests/new?part={fx.Manifest.OpenFastener}" })
            {
                await session.GotoAsync(alice, path);
                await BrowserSession.AssertBannersAsync(alice.Page);
                Assert.True(await alice.Page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1"), $"{path} scrolls sideways at 375 px");
            }
            var alpha = await session.SignInAsync("sup-alpha", 375, 812);
            foreach (var path in new[] { "/portal/scorecard", $"/portal/requests/{SourcingSeed.RequestQuoted}" })
            {
                await session.GotoAsync(alpha, path);
                await BrowserSession.AssertBannersAsync(alpha.Page);
                Assert.True(await alpha.Page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1"), $"{path} scrolls sideways at 375 px");
            }
        });
    }
}
