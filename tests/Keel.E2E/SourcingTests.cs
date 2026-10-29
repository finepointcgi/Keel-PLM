using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// Suppliers, approved lists and the supplier portal as people use them (ADR-0037): a buyer maintains a supplier, a supplier user sees only requests addressed
/// to its own company and nothing internal, responds with a file, and the pages hold at 375 px. Every page carries its marking banners.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class SourcingTests(E2EFixture fx)
{
    [E2EFact]
    public async Task Alice_creates_a_supplier_with_a_certification_and_a_contact_and_every_page_has_its_banners()
    {
        await using var session = new BrowserSession(fx, nameof(Alice_creates_a_supplier_with_a_certification_and_a_contact_and_every_page_has_its_banners));
        await session.RunAsync(async () =>
        {
            await SourcingSeed.EnsureAsync(fx);
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/suppliers/new");
            await BrowserSession.AssertBannersAsync(page);
            await BrowserSession.FillAsync(page.GetByLabel("Name *"), "Granite Precision Machining");
            await BrowserSession.FillAsync(page.GetByLabel("CAGE code"), "7Z9Q1");
            await BrowserSession.ClickAsync(page, "Create supplier");
            await Expect(page.Locator("h1")).ToContainTextAsync("Granite Precision Machining");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.Locator(".badge", new PageLocatorOptions { HasText = "Prospective" }).First).ToBeVisibleAsync();

            await BrowserSession.OpenTabAsync(page, "Certifications");
            await BrowserSession.FillAsync(page.GetByLabel("Certificate number"), "AS-2026-7");
            await BrowserSession.FillAsync(page.GetByLabel("Issuer"), "Example Registrar");
            await BrowserSession.ClickAsync(page, "Add certification");
            await Expect(page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions { Name = "AS-2026-7" })).ToBeVisibleAsync();
            await Expect(page.GetByText("Valid", new PageGetByTextOptions { Exact = true }).First).ToBeVisibleAsync();

            await BrowserSession.OpenTabAsync(page, "Contacts");
            await BrowserSession.FillAsync(page.GetByLabel("Name *"), "Quincy Quality");
            await BrowserSession.FillAsync(page.GetByLabel("E-mail"), "quincy@granite.example");
            await BrowserSession.ClickAsync(page, "Add contact");
            await Expect(page.GetByRole(AriaRole.Rowheader, new PageGetByRoleOptions { Name = "Quincy Quality" })).ToBeVisibleAsync();

            // Approval needs a review: the page offers the proposal and the review panel, not a button that approves.
            await BrowserSession.OpenTabAsync(page, "Approval");
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Approve" })).ToHaveCountAsync(0);
            await page.GetByLabel("Status the review should grant").SelectOptionAsync("approved");
            await BrowserSession.ClickAsync(page, "Set proposed status");
            await Expect(page.GetByText("The review will grant: Approved.")).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Submit for review" })).ToBeVisibleAsync();

            // The list pages and the part's Sourcing tab.
            await session.GotoAsync(alice, "/suppliers");
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = SourcingSeed.SupplierA, Exact = true })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
            await session.GotoAsync(alice, $"/parts/{fx.Manifest.OpenFastener}?tab=sourcing");
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = SourcingSeed.Entry, Exact = true })).ToBeVisibleAsync();
            await Expect(page.GetByText("Approved source")).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
            await session.GotoAsync(alice, $"/sourcing/where-sourced?supplier={SourcingSeed.SupplierA}");
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = fx.Manifest.OpenFastener, Exact = true })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
        });
    }

    [E2EFact]
    public async Task A_supplier_user_sees_only_its_own_requests_responds_with_a_file_and_reaches_nothing_internal()
    {
        await using var session = new BrowserSession(fx, nameof(A_supplier_user_sees_only_its_own_requests_responds_with_a_file_and_reaches_nothing_internal));
        await session.RunAsync(async () =>
        {
            await SourcingSeed.EnsureAsync(fx);
            var alpha = await session.SignInAsync("sup-alpha");
            var page = alpha.Page;
            // Home leads to the portal; the navigation is the portal's only, and there is no search or notification bell.
            await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/portal$"));
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.Locator("h1")).ToContainTextAsync("E2E Alpha Works");
            await Expect(page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link)).ToHaveCountAsync(1);
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Search or jump to" })).ToHaveCountAsync(0);
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = SourcingSeed.Request, Exact = true })).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = SourcingSeed.RequestForB, Exact = true })).ToHaveCountAsync(0);

            // Another supplier's request is "not found", exactly like an unknown one; internal pages are refused.
            await session.GotoAsync(alpha, $"/portal/requests/{SourcingSeed.RequestForB}");
            await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("This request was not found");
            foreach (var internalPage in new[] { "/parts", "/suppliers", $"/suppliers/{SourcingSeed.SupplierA}", "/requests", "/documents", "/audit", "/admin" })
            {
                await page.GotoAsync(fx.BaseUrl + internalPage);
                await Expect(page.GetByText("You are not authorized to open this page")).ToBeVisibleAsync();
                await BrowserSession.AssertBannersAsync(page);
            }

            // Respond with a quote and a file.
            await session.GotoAsync(alpha, $"/portal/requests/{SourcingSeed.Request}");
            await Expect(page.GetByRole(AriaRole.Rowheader, new PageGetByRoleOptions { Name = fx.Manifest.OpenFastener })).ToBeVisibleAsync();
            await BrowserSession.FillAsync(page.GetByLabel("Message"), "We can supply from stock.");
            await BrowserSession.FillAsync(page.GetByLabel($"Unit price for {fx.Manifest.OpenFastener}"), "0.42");
            await page.GetByLabel("Response files", new PageGetByLabelOptions { Exact = false }).SetInputFilesAsync(new FilePayload { Name = "quote.txt", MimeType = "text/plain", Buffer = System.Text.Encoding.UTF8.GetBytes("synthetic quote") });
            await BrowserSession.ClickAsync(page, "Send response");
            await Expect(page.GetByText("Your response was sent.")).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "quote.txt" })).ToBeVisibleAsync();

            // Supplier bravo sees only its own request.
            var bravo = await session.SignInAsync("sup-bravo");
            await Expect(bravo.Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = SourcingSeed.RequestForB, Exact = true })).ToBeVisibleAsync();
            await Expect(bravo.Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = SourcingSeed.Request, Exact = true })).ToHaveCountAsync(0);
            await session.GotoAsync(bravo, $"/portal/requests/{SourcingSeed.Request}");
            await Expect(bravo.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("This request was not found");

            // The buyer sees the response and the file.
            var alice = await session.SignInAsync("alice");
            await session.GotoAsync(alice, $"/requests/{SourcingSeed.Request}");
            await Expect(alice.Page.GetByText("We can supply from stock.")).ToBeVisibleAsync();
            await Expect(alice.Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "quote.txt" })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(alice.Page);
        });
    }

    [E2EFact]
    public async Task Sourcing_and_portal_pages_hold_at_375_px()
    {
        await using var session = new BrowserSession(fx, nameof(Sourcing_and_portal_pages_hold_at_375_px));
        await session.RunAsync(async () =>
        {
            await SourcingSeed.EnsureAsync(fx);
            var alice = await session.SignInAsync("alice", 375, 812);
            foreach (var path in new[] { "/suppliers", $"/suppliers/{SourcingSeed.SupplierA}", $"/suppliers/{SourcingSeed.SupplierA}?tab=certifications", "/requests", $"/requests/{SourcingSeed.Request}",
                         $"/parts/{fx.Manifest.OpenFastener}?tab=sourcing", "/sourcing/where-sourced", "/sourcing/coverage", "/suppliers/new", "/requests/new" })
            {
                await session.GotoAsync(alice, path);
                await BrowserSession.AssertBannersAsync(alice.Page);
                Assert.True(await alice.Page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1"), $"{path} scrolls sideways at 375 px");
            }
            var alpha = await session.SignInAsync("sup-alpha", 375, 812);
            foreach (var path in new[] { "/portal", $"/portal/requests/{SourcingSeed.Request}" })
            {
                await session.GotoAsync(alpha, path);
                await BrowserSession.AssertBannersAsync(alpha.Page);
                Assert.True(await alpha.Page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1"), $"{path} scrolls sideways at 375 px");
            }
        });
    }

    [E2EFact]
    public async Task Lena_cannot_open_the_sourcing_of_an_ITAR_part()
    {
        await using var session = new BrowserSession(fx, nameof(Lena_cannot_open_the_sourcing_of_an_ITAR_part));
        await session.RunAsync(async () =>
        {
            await SourcingSeed.EnsureAsync(fx);
            var lena = await session.SignInAsync("lena");
            await session.GotoAsync(lena, "/parts/" + fx.Manifest.GimbalBracket + "?tab=sourcing");
            await Expect(lena.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("not found, or you are not authorized");
            await BrowserSession.AssertBannersAsync(lena.Page);
            // The open part's sourcing is hers to read.
            await session.GotoAsync(lena, "/parts/" + fx.Manifest.OpenFastener + "?tab=sourcing");
            await Expect(lena.Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = SourcingSeed.Entry, Exact = true })).ToBeVisibleAsync();
        });
    }
}
