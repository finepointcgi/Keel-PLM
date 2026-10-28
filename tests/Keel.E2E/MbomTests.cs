using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>The manufacturing BOM in a real browser (increment 2.3): generate from the EBOM, read the lines, reconcile, compare side by side, and the phone layout.</summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class MbomTests(E2EFixture fx)
{
    private static bool IsKnownNotice(string message) =>
        (message.Contains("Refused to apply inline style", StringComparison.Ordinal) || message.Contains("Applying inline style violates", StringComparison.Ordinal))
        && message.Contains("Content Security Policy", StringComparison.Ordinal);

    [E2EFact]
    public async Task An_mbom_is_generated_from_the_ebom_reconciled_and_read_side_by_side()
    {
        await using var session = new BrowserSession(fx, nameof(An_mbom_is_generated_from_the_ebom_reconciled_and_read_side_by_side));
        await session.RunAsync(async () =>
        {
            // The seed already holds MBOMs of the pod and of the drive module (PLANT-A); the gimbal assembly has none, so the wizard has something to generate.
            var top = fx.Manifest.GimbalAssembly;
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;

            // The wizard: part, plant, which EBOM.
            await session.GotoAsync(alice, "/mboms/generate?part=" + top);
            await page.GetByLabel("EBOM selection").SelectOptionAsync("Latest");
            await BrowserSession.ClickAsync(page, "Generate");
            var result = page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Result" });
            await Expect(result).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
            var link = page.Locator("table.data a[href^='mboms/MB-']").First;
            var mbom = (await link.InnerTextAsync()).Trim();
            await link.ClickAsync();

            // The MBOM page: header, virtualized lines with EBOM link indicators, banners.
            await Expect(page.Locator("h2")).ToContainTextAsync(mbom);
            await Expect(page.Locator(".mbom-grid [role=row]").Nth(2)).ToBeVisibleAsync();
            await Expect(page.Locator(".mbom-grid .cell-ebom").First).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);

            // Reconcile: the findings appear grouped by severity and the coverage is stated.
            await page.GetByLabel("Reconcile against: EBOM selection").SelectOptionAsync("Latest");
            await BrowserSession.ClickAsync(page, "Reconcile");
            var panel = page.Locator("section[aria-labelledby='recon-heading']");
            await Expect(panel).ToContainTextAsync("Coverage:");
            await Expect(panel.Locator(".badge").First).ToBeVisibleAsync();

            // The structure, expanded, shows more than one level.
            await page.GetByLabel("Levels shown").SelectOptionAsync("0");
            await Expect(page.Locator("section[aria-labelledby='mbom-structure-heading'] .bom-grid .bom-row").Nth(3)).ToBeVisibleAsync();

            // Side by side: EBOM lines and the MBOM lines that implement them.
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "EBOM and MBOM side by side" }).First.ClickAsync();
            await Expect(page.Locator("table.side-by-side tbody tr").First).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);

            // The part page lists the MBOM per plant.
            await session.GotoAsync(alice, "/parts/" + top);
            var section = page.Locator("section[aria-labelledby='mbom-section-heading']");
            await Expect(section).ToContainTextAsync(mbom);
            await Expect(section).ToContainTextAsync("PLANT-A");

            // Keyboard: Tab reaches the lines grid's first row.
            await session.GotoAsync(alice, "/mboms/" + mbom);
            await Expect(page.Locator(".mbom-grid [role=row][tabindex='0']").First).ToBeVisibleAsync();

            Assert.DoesNotContain(alice.PageErrors, e => true);
            Assert.DoesNotContain(alice.ConsoleErrors.Where(e => !IsKnownNotice(e)), e => true);
        });
    }

    [E2EFact]
    public async Task The_mbom_pages_do_not_scroll_sideways_on_a_phone()
    {
        await using var session = new BrowserSession(fx, nameof(The_mbom_pages_do_not_scroll_sideways_on_a_phone));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice", width: 375, height: 812);
            var page = alice.Page;
            foreach (var path in new[] { "/mboms", "/mboms/generate", "/parts/" + fx.Manifest.TopAssembly })
            {
                await session.GotoAsync(alice, path);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                var (scrollWidth, innerWidth) = await page.EvaluateAsync<int[]>("() => [document.documentElement.scrollWidth, window.innerWidth]") is [var a, var b] ? (a, b) : default;
                Assert.True(scrollWidth <= innerWidth, $"{path}: the page scrolls sideways at 375 px (document {scrollWidth}, window {innerWidth}).");
            }
        });
    }
}
