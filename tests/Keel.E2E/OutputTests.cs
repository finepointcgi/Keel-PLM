using Microsoft.Playwright;
using Npgsql;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>Output and bulk work as people use it (ADR-0044): the report builder, a mass change with preview and undo, the BOM exchange page and release packages, with banners at desktop and phone width.</summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class OutputTests(E2EFixture fx)
{
    private static ILocator Id(IPage page, string id) => page.Locator($"[data-testid='{id}']");

    [E2EFact]
    public async Task Alice_builds_a_report_with_grouping_and_the_page_keeps_its_banners_at_phone_width()
    {
        await using var session = new BrowserSession(fx, nameof(Alice_builds_a_report_with_grouping_and_the_page_keeps_its_banners_at_phone_width));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/output/reports");
            await BrowserSession.AssertBannersAsync(page);
            await Id(page, "report-group").SelectOptionAsync("state");
            await Id(page, "report-run").ClickAsync();
            await Expect(Id(page, "report-result")).ToBeVisibleAsync();
            await Expect(Id(page, "report-groups")).ToBeVisibleAsync();
            await Expect(Id(page, "report-count")).ToContainTextAsync("row");
            await BrowserSession.AssertBannersAsync(page);
            // The banner is the aggregate of the rows shown: the demo parts are ITAR.
            Assert.Contains("ITAR", (await BrowserSession.TopBanner(page).InnerTextAsync()), StringComparison.Ordinal);
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "PDF" })).ToBeVisibleAsync();

            // A filter that matches nothing leaves no marked row, so the banner drops back.
            await Id(page, "report-filter-column").SelectOptionAsync("name");
            await BrowserSession.FillAsync(Id(page, "report-filter-value"), "no such part anywhere");
            await Id(page, "report-run").ClickAsync();
            await Expect(Id(page, "report-count")).ToContainTextAsync("0 rows");
            await Expect(BrowserSession.TopBanner(page)).ToContainTextAsync("UNCLASSIFIED");

            // An unknown dataset column cannot be chosen: nothing selected is refused in words.
            foreach (var box in await page.Locator("[data-testid^='report-col-']").AllAsync()) await box.UncheckAsync();
            await Id(page, "report-run").ClickAsync();
            await Expect(Id(page, "report-error")).ToContainTextAsync("Choose at least one column");

            await page.SetViewportSizeAsync(375, 800);
            await session.GotoAsync(alice, "/output/reports");
            await BrowserSession.AssertBannersAsync(page);
            Assert.False(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth + 1"), "no horizontal page scroll at 375 px");
            await page.SetViewportSizeAsync(1280, 800);
        });
    }

    [E2EFact]
    public async Task Alice_previews_applies_and_undoes_a_mass_change_and_sees_what_was_refused()
    {
        await using var session = new BrowserSession(fx, nameof(Alice_previews_applies_and_undoes_a_mass_change_and_sees_what_was_refused));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            var m = fx.Manifest;
            await session.GotoAsync(alice, "/output/mass");
            await BrowserSession.AssertBannersAsync(page);
            await BrowserSession.FillAsync(Id(page, "mass-numbers"), $"{m.GimbalBracket}\n{m.EndCap}\nP-999999");
            await Id(page, "mass-operation").SelectOptionAsync("favorite");
            await Id(page, "mass-preview").ClickAsync();
            await Expect(Id(page, "mass-result")).ToBeVisibleAsync();
            await Expect(Id(page, "mass-items")).ToContainTextAsync("Will change");
            await Expect(Id(page, "mass-items")).ToContainTextAsync("Not found, or you may not read it.");
            await BrowserSession.AssertBannersAsync(page);
            await Id(page, "mass-apply").ClickAsync();
            await Expect(Id(page, "mass-status")).ToHaveTextAsync("Applied");
            await Expect(Id(page, "mass-items")).ToContainTextAsync("Changed");
            await Id(page, "mass-undo").ClickAsync();
            await Expect(Id(page, "mass-status")).ToHaveTextAsync("Undone");

            // More than the cap is refused before anything is previewed.
            await BrowserSession.FillAsync(Id(page, "mass-numbers"), string.Join("\n", Enumerable.Range(1, 501).Select(i => $"P-{i:D6}")));
            await Id(page, "mass-preview").ClickAsync();
            await Expect(Id(page, "mass-error")).ToContainTextAsync("At most 500");

            await page.SetViewportSizeAsync(375, 800);
            await session.GotoAsync(alice, "/output/mass");
            await BrowserSession.AssertBannersAsync(page);
            Assert.False(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth + 1"), "no horizontal page scroll at 375 px");
            await page.SetViewportSizeAsync(1280, 800);
        });
    }

    [E2EFact]
    public async Task The_bom_exchange_page_offers_export_and_template_and_reports_a_bad_workbook_in_words()
    {
        await using var session = new BrowserSession(fx, nameof(The_bom_exchange_page_offers_export_and_template_and_reports_a_bad_workbook_in_words));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/output/bom");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(Id(page, "bom-template")).ToBeVisibleAsync();
            // The export is a plain download through the cookie session: fetch it as the page does and read the first bytes.
            var url = $"/ui/output/bom-export?kind=ebom&number={Uri.EscapeDataString(fx.Manifest.TopAssembly)}&format=csv&spec=latest";
            var text = await page.EvaluateAsync<string>("async u => { const r = await fetch(u); return r.status + '|' + r.headers.get('content-type') + '|' + (await r.text()).slice(0, 400); }", url);
            Assert.StartsWith("200|text/csv", text, StringComparison.Ordinal);
            Assert.Contains("ITAR", text, StringComparison.Ordinal); // the banner is the first line of the file
            var bad = await page.EvaluateAsync<int>("async () => (await fetch('/ui/output/bom-export?kind=ebom&number=P-999999&format=csv')).status");
            Assert.Equal(404, bad);

            // A file that is not a workbook is refused with a message, nothing is created.
            await Id(page, "bom-import-file").SetInputFilesAsync(new FilePayload { Name = "bad.xlsx", MimeType = "application/octet-stream", Buffer = System.Text.Encoding.UTF8.GetBytes("this is not a workbook") });
            await Id(page, "bom-import-check").ClickAsync();
            await Expect(Id(page, "bom-import-error")).ToContainTextAsync("not an .xlsx workbook");

            await page.SetViewportSizeAsync(375, 800);
            await session.GotoAsync(alice, "/output/bom");
            await BrowserSession.AssertBannersAsync(page);
            Assert.False(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth + 1"), "no horizontal page scroll at 375 px");
            await page.SetViewportSizeAsync(1280, 800);
        });
    }

    [E2EFact]
    public async Task A_release_package_is_refused_for_unreleased_data_and_made_for_a_released_part()
    {
        await using var session = new BrowserSession(fx, nameof(A_release_package_is_refused_for_unreleased_data_and_made_for_a_released_part));
        await session.RunAsync(async () =>
        {
            string? releasedLeaf = null;
            await using (var conn = await fx.AppDb.OpenConnectionAsync())
            await using (var cmd = new NpgsqlCommand(
                "SELECT m.number FROM plm.part_master m JOIN plm.part_revision r ON r.master_id = m.id AND r.is_latest WHERE r.lifecycle_state = 'Released' AND m.part_type <> 'Assembly' AND m.export_jurisdiction = 'None' ORDER BY m.number LIMIT 1", conn))
                releasedLeaf = (string?)await cmd.ExecuteScalarAsync();
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/output/packages");
            await BrowserSession.AssertBannersAsync(page);
            await Id(page, "package-key").FillAsync(fx.Manifest.AvionicsAssemblyUnderReview);
            await Id(page, "package-generate").ClickAsync();
            await Expect(Id(page, "package-error")).ToContainTextAsync("released data");
            if (releasedLeaf is not null)
            {
                await BrowserSession.FillAsync(Id(page, "package-key"), releasedLeaf);
                await Id(page, "package-generate").ClickAsync();
                await Expect(Id(page, "package-list")).ToBeVisibleAsync();
                await Expect(Id(page, "package-download").First).ToBeVisibleAsync();
                await BrowserSession.AssertBannersAsync(page);
                var status = await page.EvaluateAsync<int>("async () => (await fetch(document.querySelector('[data-testid=package-download]').href)).status");
                Assert.Equal(200, status);
            }
            await page.SetViewportSizeAsync(375, 800);
            await session.GotoAsync(alice, "/output/packages");
            await BrowserSession.AssertBannersAsync(page);
            Assert.False(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth + 1"), "no horizontal page scroll at 375 px");
            await page.SetViewportSizeAsync(1280, 800);
        });
    }
}
