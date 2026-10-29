using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// Records management as people use it (ADR-0041): a records manager reads the schedule, drafts a class and places a hold; the hold shows on the object's page for records
/// roles and for nobody else; people without a records role cannot open any records page; every page carries its marking banners and holds at 375 px.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class RecordsTests(E2EFixture fx)
{
    [E2EFact]
    public async Task Rex_reads_the_schedule_drafts_a_class_and_every_page_has_its_banners()
    {
        await using var session = new BrowserSession(fx, nameof(Rex_reads_the_schedule_drafts_a_class_and_every_page_has_its_banners));
        await session.RunAsync(async () =>
        {
            await RecordsSeed.EnsureAsync(fx);
            var rex = await session.SignInAsync("rex");
            var page = rex.Page;
            await Expect(page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Records management" })).ToBeVisibleAsync();
            await session.GotoAsync(rex, "/records");
            await Expect(page.Locator("h1")).ToContainTextAsync("Records management");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByText("Version 900: Seeded schedule")).ToBeVisibleAsync();

            await session.GotoAsync(rex, "/records/schedules");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Version 900" })).ToBeVisibleAsync();
            await Expect(page.GetByText("Program ORCA: contract end")).ToBeVisibleAsync();

            // A draft starts as a copy of the published version; a class is added through the form and shows in the table.
            await BrowserSession.FillAsync(page.GetByLabel("Title of the new draft"), "E2E draft");
            await BrowserSession.ClickAsync(page, "Start a draft");
            await Expect(page.Locator("h1")).ToContainTextAsync("E2E draft");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions { Name = "E2E-SPEC" })).ToBeVisibleAsync();
            await BrowserSession.FillAsync(page.GetByLabel("Code *"), "E2E-DRAW");
            await BrowserSession.FillAsync(page.GetByLabel("Name *"), "Drawings");
            await BrowserSession.FillAsync(page.GetByLabel("Types (comma separated, for example Specification)"), "Drawing");
            await BrowserSession.ClickAsync(page, "Save class");
            await Expect(page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions { Name = "E2E-DRAW" })).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Submit for review" })).ToBeVisibleAsync();
            // Publishing needs signatures: there is no button that publishes.
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish" })).ToHaveCountAsync(0);
            await session.GotoAsync(rex, "/records/schedules");
            // Discard the draft so later tests start clean.
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Version 901" }).ClickAsync();
            await BrowserSession.ClickAsync(page, "Discard this draft");
            await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Discard draft" }).ClickAsync();
            await Expect(page.Locator("h1")).ToContainTextAsync("Retention schedules");
        });
    }

    [E2EFact]
    public async Task A_hold_shows_on_the_object_for_records_roles_only_and_its_page_lists_only_what_the_reader_may_see()
    {
        await using var session = new BrowserSession(fx, nameof(A_hold_shows_on_the_object_for_records_roles_only_and_its_page_lists_only_what_the_reader_may_see));
        await session.RunAsync(async () =>
        {
            await RecordsSeed.EnsureAsync(fx);
            var spec = fx.Manifest.SystemSpecification;
            var rex = await session.SignInAsync("rex");
            await session.GotoAsync(rex, $"/documents/{spec}");
            await Expect(rex.Page.Locator("[data-records-chip=hold]")).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(rex.Page);

            await session.GotoAsync(rex, $"/records/holds/{RecordsSeed.Hold}");
            await Expect(rex.Page.Locator("h1")).ToContainTextAsync(RecordsSeed.Hold);
            await BrowserSession.AssertBannersAsync(rex.Page);
            await Expect(rex.Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = spec, Exact = true })).ToBeVisibleAsync();
            await Expect(rex.Page.GetByText("A hold can cover more than that; no count of the rest is shown.")).ToBeVisibleAsync();

            // Alice has no records role: no chip, and no records page opens for her.
            var alice = await session.SignInAsync("alice");
            await session.GotoAsync(alice, $"/documents/{spec}");
            await Expect(alice.Page.Locator("h1")).ToBeVisibleAsync();
            await Expect(alice.Page.Locator("[data-records-chip]")).ToHaveCountAsync(0);
            await Expect(alice.Page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Records management" })).ToHaveCountAsync(0);
            foreach (var path in new[] { "/records", "/records/holds", $"/records/holds/{RecordsSeed.Hold}", "/records/disposition", "/records/archives", "/records/schedules" })
            {
                await alice.Page.GotoAsync(fx.BaseUrl + path);
                await Expect(alice.Page.GetByText("You are not authorized to open this page")).ToBeVisibleAsync();
                await BrowserSession.AssertBannersAsync(alice.Page);
            }
        });
    }

    [E2EFact]
    public async Task Rex_places_a_hold_by_object_number_and_sees_the_disposition_queue_and_requests()
    {
        await using var session = new BrowserSession(fx, nameof(Rex_places_a_hold_by_object_number_and_sees_the_disposition_queue_and_requests));
        await session.RunAsync(async () =>
        {
            await RecordsSeed.EnsureAsync(fx);
            var rex = await session.SignInAsync("rex");
            var page = rex.Page;
            await session.GotoAsync(rex, "/records/holds");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = RecordsSeed.Hold, Exact = true })).ToBeVisibleAsync();
            await BrowserSession.FillAsync(page.GetByLabel("Name *"), "Fastener matter");
            await BrowserSession.FillAsync(page.GetByLabel("Reason *"), "Supplier dispute");
            await BrowserSession.FillAsync(page.GetByLabel("Objects you can see (one per line, for example D-000001 or P-000001)"), fx.Manifest.OpenFastener);
            await BrowserSession.ClickAsync(page, "Place hold");
            await Expect(page.Locator("h1")).ToContainTextAsync("Fastener matter");
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = fx.Manifest.OpenFastener, Exact = true })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
            // The part page now shows the chip to Rex.
            await session.GotoAsync(rex, $"/parts/{fx.Manifest.OpenFastener}");
            await Expect(page.Locator("[data-records-chip=hold]")).ToBeVisibleAsync();

            await session.GotoAsync(rex, "/records/disposition");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.Locator("h1")).ToContainTextAsync("Disposition");
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = RecordsSeed.Request, Exact = true })).ToBeVisibleAsync();
            await session.GotoAsync(rex, $"/records/disposition/{RecordsSeed.Request}");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Submit for review" })).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Carry out the disposal" })).ToHaveCountAsync(0); // not approved: nothing to carry out

            await session.GotoAsync(rex, "/records/archives");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByText("No archives yet")).ToBeVisibleAsync();
            await session.GotoAsync(rex, "/records/certificates");
            await BrowserSession.AssertBannersAsync(page);
        });
    }

    [E2EFact]
    public async Task Records_pages_hold_at_375_px()
    {
        await using var session = new BrowserSession(fx, nameof(Records_pages_hold_at_375_px));
        await session.RunAsync(async () =>
        {
            await RecordsSeed.EnsureAsync(fx);
            var rex = await session.SignInAsync("rex", 375, 812);
            foreach (var path in new[] { "/records", "/records/schedules", $"/records/schedules/{RecordsSeed.ScheduleVersion}", "/records/holds", $"/records/holds/{RecordsSeed.Hold}", "/records/disposition",
                         $"/records/disposition/{RecordsSeed.Request}", "/records/archives", "/records/certificates" })
            {
                await session.GotoAsync(rex, path);
                await BrowserSession.AssertBannersAsync(rex.Page);
                Assert.True(await rex.Page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1"), $"{path} scrolls sideways at 375 px");
            }
        });
    }
}
