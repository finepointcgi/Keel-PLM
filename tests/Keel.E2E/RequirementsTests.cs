using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// Requirements and traceability as people use them (ADR-0038): create a specification, add and change requirements, allocate to a part, see the link go suspect and review it,
/// verify, read coverage, find the requirement on the part's Trace tab, and the pages hold at 375 px. A reader without the program sees no restricted specification.
/// Every page carries its marking banners.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class RequirementsTests(E2EFixture fx)
{
    private static bool IsKnownNotice(string message) =>
        (message.Contains("Refused to apply inline style", StringComparison.Ordinal) || message.Contains("Applying inline style violates", StringComparison.Ordinal))
        && message.Contains("Content Security Policy", StringComparison.Ordinal);

    [E2EFact]
    public async Task Alice_builds_a_specification_allocates_it_watches_a_link_go_suspect_and_verifies()
    {
        await using var session = new BrowserSession(fx, nameof(Alice_builds_a_specification_allocates_it_watches_a_link_go_suspect_and_verifies));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/requirements/new");
            await BrowserSession.AssertBannersAsync(page);
            await BrowserSession.FillAsync(page.GetByLabel("Name *"), "E2E housing requirements");
            await BrowserSession.ClickAsync(page, "Create specification");
            await Expect(page.Locator("h1")).ToContainTextAsync("E2E housing requirements");
            await BrowserSession.AssertBannersAsync(page);
            var number = (await page.Locator("h1").InnerTextAsync()).Split('·')[0].Trim();

            // Add a requirement and a child.
            await BrowserSession.OpenTabAsync(page, "Requirements");
            await BrowserSession.ClickAsync(page, "Add requirement");
            await BrowserSession.FillAsync(page.GetByLabel("Title *"), "Housing depth rating");
            await BrowserSession.FillAsync(page.GetByLabel("Requirement text *"), "The housing shall be rated to 6000 m.");
            await page.Locator("label", new PageLocatorOptions { HasText = "Status" }).Locator("select").SelectOptionAsync("approved");
            await BrowserSession.ClickAsync(page, "Save");
            await Expect(page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions { Name = "R-0001", Exact = true })).ToBeVisibleAsync();
            await Expect(page.GetByText("Housing depth rating").First).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
            await BrowserSession.ClickAsync(page, "Add child of R-0001", exact: false);
            await BrowserSession.FillAsync(page.GetByLabel("Title *"), "Seal rating");
            await BrowserSession.FillAsync(page.GetByLabel("Requirement text *"), "The seals shall hold 600 bar.");
            await BrowserSession.ClickAsync(page, "Save");
            await Expect(page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions { Name = "1.1", Exact = true })).ToBeVisibleAsync();

            // Allocate R-0001 to a part, then reword it: the link is suspect until reviewed.
            await BrowserSession.ClickAsync(page, "Details of R-0001", exact: false);
            await page.GetByLabel("Target number").FillAsync(fx.Manifest.OpenFastener);
            await BrowserSession.ClickAsync(page, "Add link");
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = fx.Manifest.OpenFastener, Exact = true }).First).ToBeVisibleAsync();
            await Expect(page.GetByText("Current").First).ToBeVisibleAsync();
            await BrowserSession.ClickAsync(page, "Edit R-0001", exact: false);
            await BrowserSession.FillAsync(page.GetByLabel("Requirement text *"), "The housing shall be rated to 7000 m.");
            await BrowserSession.ClickAsync(page, "Save");
            await Expect(page.GetByText("Suspect").First).ToBeVisibleAsync();
            await BrowserSession.ClickAsync(page, "Mark reviewed", exact: false);
            await Expect(page.GetByText("Current").First).ToBeVisibleAsync();

            // The traceability matrix, the graph and its table alternative.
            await BrowserSession.OpenTabAsync(page, "Trace");
            await Expect(page.Locator("table.matrix")).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
            await BrowserSession.ClickAsync(page, "Graph");
            await Expect(page.Locator(".vis-node").First).ToBeVisibleAsync();
            await BrowserSession.ClickAsync(page, "Graph as a table");
            await Expect(page.GetByRole(AriaRole.Table, new PageGetByRoleOptions { Name = "Impact map as a table" })).ToBeVisibleAsync();

            // Verification and coverage.
            await BrowserSession.OpenTabAsync(page, "Verification");
            await page.GetByLabel("Requirement to verify").SelectOptionAsync("R-0001");
            await page.GetByLabel("Result").SelectOptionAsync("pass");
            await BrowserSession.ClickAsync(page, "Record verification");
            await Expect(page.GetByText("Verified").First).ToBeVisibleAsync();
            await BrowserSession.OpenTabAsync(page, "Coverage");
            await Expect(page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Coverage summary" })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);

            // The part's Trace tab lists the requirement.
            await session.GotoAsync(alice, $"/parts/{fx.Manifest.OpenFastener}?tab=trace");
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = number, Exact = true })).ToBeVisibleAsync();
            await Expect(page.GetByText("Housing depth rating")).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);

            // The spec is found by the global search prefix.
            await session.GotoAsync(alice, "/requirements");
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = number, Exact = true })).ToBeVisibleAsync();
            Assert.DoesNotContain(alice.PageErrors, e => true);
            Assert.DoesNotContain(alice.ConsoleErrors.Where(e => !IsKnownNotice(e)), e => true);
        });
    }

    [E2EFact]
    public async Task A_reader_without_the_program_never_sees_the_restricted_specification()
    {
        await using var session = new BrowserSession(fx, nameof(A_reader_without_the_program_never_sees_the_restricted_specification));
        await session.RunAsync(async () =>
        {
            var spec = fx.Manifest.Extras!.RequirementSpec!;
            var lena = await session.SignInAsync("lena");
            var page = lena.Page;
            await session.GotoAsync(lena, "/requirements");
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = spec, Exact = true })).ToHaveCountAsync(0);
            await BrowserSession.AssertBannersAsync(page);
            await session.GotoAsync(lena, "/requirements/" + spec);
            await Expect(page.GetByText("was not found, or you are not authorized")).ToBeVisibleAsync();
            await session.GotoAsync(lena, $"/parts/{fx.Manifest.OpenFastener}?tab=trace");
            await Expect(page.GetByText(spec)).ToHaveCountAsync(0);
        });
    }

    [E2EFact]
    public async Task The_demo_specification_shows_coverage_and_a_suspect_link_and_the_pages_hold_at_375_px()
    {
        await using var session = new BrowserSession(fx, nameof(The_demo_specification_shows_coverage_and_a_suspect_link_and_the_pages_hold_at_375_px));
        await session.RunAsync(async () =>
        {
            var spec = fx.Manifest.Extras!.RequirementSpec!;
            var alice = await session.SignInAsync("alice", width: 375, height: 812);
            var page = alice.Page;
            foreach (var path in new[] { "/requirements", "/requirements/new", $"/requirements/{spec}?tab=requirements", $"/requirements/{spec}?tab=trace", $"/requirements/{spec}?tab=verification",
                         $"/requirements/{spec}?tab=coverage", $"/requirements/{spec}?tab=exchange", $"/parts/{fx.Manifest.GimbalAssembly}?tab=trace" })
            {
                await session.GotoAsync(alice, path);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                var (scrollWidth, innerWidth) = await page.EvaluateAsync<int[]>("() => [document.documentElement.scrollWidth, window.innerWidth]") is [var a, var b] ? (a, b) : default;
                Assert.True(scrollWidth <= innerWidth, $"{path}: the page scrolls sideways at 375 px (document {scrollWidth}, window {innerWidth}).");
                await BrowserSession.AssertBannersAsync(page);
            }
            await session.GotoAsync(alice, $"/requirements/{spec}?tab=trace");
            await BrowserSession.ClickAsync(page, "Suspect links");
            await Expect(page.GetByText("The requirement changed after the link was reviewed").First).ToBeVisibleAsync();
            await session.GotoAsync(alice, $"/requirements/{spec}?tab=coverage");
            await Expect(page.GetByText("requirements in scope")).ToBeVisibleAsync();
        });
    }
}
