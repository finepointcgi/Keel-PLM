using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// Product compliance as people use it (ADR-0040): an author declares the substances of a part and sees its standing, anyone rolls an assembly up and drills into the treemap,
/// an administrator prepares a new version of a regulatory list for review, a person who may not read a part cannot read its declarations, and the pages hold at 375 px.
/// Every page carries its marking banners.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class ComplianceTests(E2EFixture fx)
{
    [E2EFact]
    public async Task Alice_declares_a_part_and_sees_its_standing_for_each_regulation()
    {
        await using var session = new BrowserSession(fx, nameof(Alice_declares_a_part_and_sees_its_standing_for_each_regulation));
        await session.RunAsync(async () =>
        {
            await ComplianceSeed.EnsureAsync(fx);
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, $"/parts/{fx.Manifest.OpenFastener}?tab=compliance");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = ComplianceSeed.FastenerDeclaration, Exact = true }).First).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Rowheader, new PageGetByRoleOptions { Name = "RoHS" })).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add the declaration" })).ToBeVisibleAsync();

            // A line of lead above the RoHS threshold makes the part non-compliant for RoHS: the standing says so in words, with what was found.
            await BrowserSession.FillAsync(page.GetByLabel("CAS number").First, "7439-92-1");
            await BrowserSession.FillAsync(page.GetByLabel("Amount", new PageGetByLabelOptions { Exact = true }), "0.5");
            await BrowserSession.ClickAsync(page, "Add the declaration");
            await Expect(page.GetByText("Above the threshold").First).ToBeVisibleAsync();
            await Expect(page.GetByText("Non-compliant").First).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);

            // A mistake is explained and nothing is stored.
            await BrowserSession.FillAsync(page.GetByLabel("CAS number").First, "7439-92-2");
            await BrowserSession.FillAsync(page.GetByLabel("Amount", new PageGetByLabelOptions { Exact = true }), "1");
            await BrowserSession.ClickAsync(page, "Add the declaration");
            await Expect(page.GetByRole(AriaRole.Alert).First).ToContainTextAsync("not a valid CAS number");

            // The declaration page: composition, results per regulation, documents, actions.
            await session.GotoAsync(alice, $"/compliance/declarations/{ComplianceSeed.FastenerDeclaration}");
            await Expect(page.Locator("h1")).ToContainTextAsync(ComplianceSeed.FastenerDeclaration);
            await Expect(page.GetByText("Against the regulations in force")).ToBeVisibleAsync();
            await Expect(page.GetByText("Tin (7440-31-5)")).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
            await session.GotoAsync(alice, "/compliance/declarations");
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = ComplianceSeed.FastenerDeclaration, Exact = true })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
        });
    }

    [E2EFact]
    public async Task An_assembly_is_rolled_up_with_the_causes_a_treemap_and_two_exports()
    {
        await using var session = new BrowserSession(fx, nameof(An_assembly_is_rolled_up_with_the_causes_a_treemap_and_two_exports));
        await session.RunAsync(async () =>
        {
            await ComplianceSeed.EnsureAsync(fx);
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, $"/compliance?mode=ebom&number={fx.Manifest.TopAssembly}");
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = fx.Manifest.TopAssembly }).First).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "RoHS" }).First).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Conflict minerals (CSV)" })).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Material declaration (XML)" })).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Table).First).ToBeVisibleAsync();

            // The same data as a treemap; a tile is a button or a link, the table stays available.
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Treemap", Exact = true }).ClickAsync();
            await Expect(page.Locator("svg.vis-svg")).ToBeVisibleAsync();
            await Expect(page.Locator("g.vis-tile").First).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
            await page.GetByLabel("Compliance picture").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Table", Exact = true }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Table).First).ToBeVisibleAsync();

            // The export is a file download, not a page.
            var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Conflict minerals (CSV)" }).ClickAsync());
            Assert.EndsWith("-cmrt.csv", download.SuggestedFilename, StringComparison.Ordinal);
        });
    }

    [E2EFact]
    public async Task Ada_prepares_the_next_version_of_a_list_and_it_waits_for_review()
    {
        await using var session = new BrowserSession(fx, nameof(Ada_prepares_the_next_version_of_a_list_and_it_waits_for_review));
        await session.RunAsync(async () =>
        {
            var ada = await session.SignInAsync("ada");
            var page = ada.Page;
            await session.GotoAsync(ada, "/compliance/lists");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "RoHS", Exact = true })).ToBeVisibleAsync();
            await session.GotoAsync(ada, "/compliance/lists/rohs");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Version 1" })).ToBeVisibleAsync();
            await BrowserSession.ClickAsync(page, "Create the draft");
            await Expect(page.Locator("h1")).ToContainTextAsync("version 2");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByText("Edit the draft")).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Submit for review" })).ToBeVisibleAsync();
            await Expect(page.GetByText("Changes compared with the version before it")).ToBeVisibleAsync();
            // Published versions never offer an editor.
            await session.GotoAsync(ada, "/compliance/lists/rohs/versions/1");
            await Expect(page.GetByText("A published version never changes")).ToBeVisibleAsync();
            await Expect(page.GetByText("Edit the draft")).ToHaveCountAsync(0);
            // Tidy up so the next run starts from the same state.
            await session.GotoAsync(ada, "/compliance/lists/rohs/versions/2");
            await BrowserSession.ClickAsync(page, "Discard the draft");
            await Expect(page.Locator("h1")).ToContainTextAsync("RoHS");
        });
    }

    [E2EFact]
    public async Task Lena_cannot_open_the_compliance_of_an_ITAR_part_and_the_roll_up_leaves_it_out()
    {
        await using var session = new BrowserSession(fx, nameof(Lena_cannot_open_the_compliance_of_an_ITAR_part_and_the_roll_up_leaves_it_out));
        await session.RunAsync(async () =>
        {
            await ComplianceSeed.EnsureAsync(fx);
            var lena = await session.SignInAsync("lena");
            await session.GotoAsync(lena, $"/parts/{fx.Manifest.GimbalBracket}?tab=compliance");
            await Expect(lena.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("not found, or you are not authorized");
            await BrowserSession.AssertBannersAsync(lena.Page);
            Assert.DoesNotContain(ComplianceSeed.BracketDeclaration, await lena.Page.ContentAsync(), StringComparison.Ordinal);
            await session.GotoAsync(lena, $"/compliance/declarations/{ComplianceSeed.BracketDeclaration}");
            await Expect(lena.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Not found, or you are not authorized");
            await session.GotoAsync(lena, "/compliance/declarations");
            await Expect(lena.Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = ComplianceSeed.BracketDeclaration, Exact = true })).ToHaveCountAsync(0);
            await Expect(lena.Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = ComplianceSeed.FastenerDeclaration, Exact = true })).ToBeVisibleAsync();
        });
    }

    [E2EFact]
    public async Task Compliance_pages_hold_at_375_px()
    {
        await using var session = new BrowserSession(fx, nameof(Compliance_pages_hold_at_375_px));
        await session.RunAsync(async () =>
        {
            await ComplianceSeed.EnsureAsync(fx);
            var alice = await session.SignInAsync("alice", 375, 812);
            foreach (var path in new[] { "/compliance", $"/compliance?mode=ebom&number={fx.Manifest.TopAssembly}", "/compliance/declarations", $"/compliance/declarations/{ComplianceSeed.FastenerDeclaration}",
                         "/compliance/lists", "/compliance/lists/rohs", "/compliance/lists/rohs/versions/1", "/compliance/substances", "/compliance/materials", $"/parts/{fx.Manifest.OpenFastener}?tab=compliance", "/" })
            {
                await session.GotoAsync(alice, path);
                await Expect(alice.Page.Locator("h1")).ToBeVisibleAsync();
                await alice.Page.WaitForTimeoutAsync(800); // pages that load a list set their banner when the list arrives
                await BrowserSession.AssertBannersAsync(alice.Page);
                var wide = await alice.Page.EvaluateAsync<string>("[...document.querySelectorAll('body *')].filter(e => e.getBoundingClientRect().right > document.documentElement.clientWidth + 1 || e.scrollWidth > e.clientWidth + 1).slice(0, 8).map(e => e.tagName + '.' + e.className).join(' | ')");
                Assert.True(await alice.Page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1"), $"{path} scrolls sideways at 375 px: {wide}");
            }
        });
    }
}
