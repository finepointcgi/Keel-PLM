using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// Export-control enforcement as people experience it: a non-U.S. person without authorization cannot find, open or see inside ITAR items;
/// a licensed partner (TAA-0042 listed on the object) can see exactly that object; every page carries its marking banner.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class ExportControlTests(E2EFixture fx)
{
    [E2EFact]
    public async Task Lena_without_authorization_cannot_find_or_open_ITAR_items_and_sees_restricted_BOM_rows()
    {
        await using var session = new BrowserSession(fx, nameof(Lena_without_authorization_cannot_find_or_open_ITAR_items_and_sees_restricted_BOM_rows));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var lena = await session.SignInAsync("lena");
            var page = lena.Page;

            // Search: the ITAR bracket does not exist for her; the open fastener does.
            await session.GotoAsync(lena, "/parts");
            await BrowserSession.FillAsync(page.GetByLabel("Search parts"), m.GimbalBracket);
            await BrowserSession.ClickAsync(page, "Search");
            await Expect(page.GetByText("0 parts you can access")).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = m.GimbalBracket, Exact = true })).ToHaveCountAsync(0);
            await BrowserSession.FillAsync(page.GetByLabel("Search parts"), m.OpenFastener);
            await BrowserSession.ClickAsync(page, "Search");
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = m.OpenFastener, Exact = true })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);

            // Direct URL: the same "not found" a missing part gets, so existence is not disclosed.
            foreach (var hidden in new[] { m.GimbalBracket, m.TopAssembly, m.KrakenPart, m.PartnerPlate })
            {
                await session.GotoAsync(lena, "/parts/" + hidden);
                await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync($"Part {hidden} was not found, or you are not authorized to see it.");
                await BrowserSession.AssertBannersAsync(page);
            }

            // A document is protected the same way.
            await session.GotoAsync(lena, "/documents/" + m.BracketDrawing);
            await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("not found, or you are not authorized");

            // The open spares kit is hers to read, but its ITAR lines are redacted: quantity and find number only.
            await session.GotoAsync(lena, "/parts/" + m.SparesKit);
            await Expect(page.GetByRole(AriaRole.Treegrid)).ToBeVisibleAsync();
            await BrowserSession.ClickAsync(page, "Expand all");
            await Expect(page.GetByText("3 restricted items you cannot see")).ToBeVisibleAsync();
            await Expect(page.Locator(".bom-grid .restricted-label")).ToHaveCountAsync(3);
            await Expect(page.Locator(".bom-grid").GetByText(m.GimbalBracket)).ToHaveCountAsync(0);
            await Expect(page.Locator(".bom-grid").GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = m.OpenFastener })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
            // Her page banner covers only what she can see.
            Assert.DoesNotContain("ITAR", await BrowserSession.TopBanner(page).InnerTextAsync(), StringComparison.Ordinal);
        });
    }

    [E2EFact]
    public async Task Farid_sees_the_objects_that_list_his_authorization_and_nothing_else_restricted()
    {
        await using var session = new BrowserSession(fx, nameof(Farid_sees_the_objects_that_list_his_authorization_and_nothing_else_restricted));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var farid = await session.SignInAsync("farid");
            var page = farid.Page;

            // The partner plate lists TAA-0042, which he holds.
            await session.GotoAsync(farid, "/parts/" + m.PartnerPlate);
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = m.PartnerPlate, Exact = false, Level = 2 })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
            Assert.Contains("ITAR", await BrowserSession.TopBanner(page).InnerTextAsync(), StringComparison.Ordinal);
            await session.GotoAsync(farid, "/documents/" + m.PartnerDrawing);
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = m.PartnerDrawing, Exact = false, Level = 2 })).ToBeVisibleAsync();

            // The bracket and its drawing do not list it: not found.
            await session.GotoAsync(farid, "/parts/" + m.GimbalBracket);
            await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("not found, or you are not authorized");
            await session.GotoAsync(farid, "/documents/" + m.BracketDrawing);
            await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("not found, or you are not authorized");

            // In the spares kit he sees his plate and two restricted lines.
            await session.GotoAsync(farid, "/parts/" + m.SparesKit);
            await Expect(page.GetByRole(AriaRole.Treegrid)).ToBeVisibleAsync();
            await BrowserSession.ClickAsync(page, "Expand all");
            await Expect(page.GetByText("2 restricted items you cannot see")).ToBeVisibleAsync();
            await Expect(page.Locator(".bom-grid").GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = m.PartnerPlate })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
        });
    }

    [E2EFact]
    public async Task Carmen_without_the_program_and_alice_with_it_see_the_KRAKEN_line_only_as_restricted_or_not_at_all()
    {
        await using var session = new BrowserSession(fx, nameof(Carmen_without_the_program_and_alice_with_it_see_the_KRAKEN_line_only_as_restricted_or_not_at_all));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            // Alice is on ORCA but not on KRAKEN: the KRAKEN part is a restricted line in the payload assembly and unknown by number.
            var alice = await session.SignInAsync("alice");
            await session.GotoAsync(alice, "/parts/" + m.PayloadAssembly);
            await Expect(alice.Page.GetByRole(AriaRole.Treegrid)).ToBeVisibleAsync();
            await Expect(alice.Page.GetByText("1 restricted item you cannot see")).ToBeVisibleAsync();
            await session.GotoAsync(alice, "/parts/" + m.KrakenPart);
            await Expect(alice.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("not found, or you are not authorized");

            // Carmen is a U.S. person without ORCA: every ORCA item is hidden from her, including the pod itself.
            var carmen = await session.SignInAsync("carmen");
            await session.GotoAsync(carmen, "/parts/" + m.TopAssembly);
            await Expect(carmen.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("not found, or you are not authorized");
            await session.GotoAsync(carmen, "/parts/" + m.SparesKit);
            await Expect(carmen.Page.GetByRole(AriaRole.Treegrid)).ToBeVisibleAsync();
            await BrowserSession.ClickAsync(carmen.Page, "Expand all");
            await Expect(carmen.Page.GetByText("3 restricted items you cannot see")).ToBeVisibleAsync();
        });
    }
}
