using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Core;
using Keel.Core.Geometry;
using Keel.Documents;
using Keel.Documents.Vault;
using Keel.Seed;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// Model versions as engineers use them (increment 9.1, ADR-0046): the seeded bracket shows its two versions and what changed, a new version goes in through one
/// dialog (and a released revision is revised in the same dialog), restore adds a version without touching history, and the visual comparison renders real
/// pixels in all three modes at desktop size and at 375 px, under the viewer's strict policy and with both banners and the distribution statement.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class ModelVersionTests(E2EFixture fx)
{
    private const string ItarBanner = "CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA";

    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"],
        controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;

    private DocumentService Docs() => new(fx.AppDb, new PostgresAuditLog(fx.AppDb, TimeProvider.System), TimeProvider.System, new ObjectVault(fx.Vault));

    private async Task<string> CreateModelAsync(string name, byte[] bytes)
    {
        var alice = RealmUsers.Load()["alice"];
        var created = await Docs().CreateAsync(alice, new CreateDocumentCommand(name, "CAD", Itar, "Synthetic model for the E2E tests"),
            new FileUpload("bracket.stl", "model/stl", new MemoryStream(bytes)), null);
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        return created.Value!.Number;
    }

    private static byte[] Shape(double height, double wall = 4) => SyntheticMesh.ToBinaryStl(SyntheticMesh.Bracket(40, height, wall, 20), "e2e " + Guid.NewGuid().ToString("N")[..8]);

    private static async Task OpenVersionsAsync(BrowserSession session, UserPage user, string number)
    {
        await session.GotoAsync(user, $"/documents/{number}?tab=versions");
        await Expect(user.Page.Locator("[data-testid=model-versions]")).ToBeVisibleAsync();
        await Expect(user.Page.Locator("[data-testid=version-table]")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
    }

    private static async Task<int[]> CanvasPixelsAsync(IPage page) => await page.EvaluateAsync<int[]>("""
        async () => {
          const c = document.querySelector('#viewer-stage canvas');
          const img = new Image(); img.src = c.toDataURL('image/png'); await img.decode();
          const k = document.createElement('canvas'); k.width = 320; k.height = 200;
          const g = k.getContext('2d'); g.drawImage(img, 0, 0, k.width, k.height);
          const d = g.getImageData(0, 0, k.width, k.height).data;
          const bg = [d[0], d[1], d[2]]; let non = 0; let blue = 0; let orange = 0; const colors = new Set();
          for (let i = 0; i < d.length; i += 4) {
            if (Math.abs(d[i] - bg[0]) + Math.abs(d[i + 1] - bg[1]) + Math.abs(d[i + 2] - bg[2]) > 30) non++;
            if (d[i + 2] > d[i] + 40 && d[i + 2] > d[i + 1]) blue++;
            if (d[i] > d[i + 2] + 60 && d[i + 1] > d[i + 2]) orange++;
            colors.add(((d[i] >> 4) << 8) | ((d[i + 1] >> 4) << 4) | (d[i + 2] >> 4));
          }
          return [non, k.width * k.height, colors.size, blue, orange];
        }
        """);

    private static async Task WaitReadyAsync(IPage page) =>
        await Expect(page.Locator("body[data-viewer-state=ready]")).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 90_000 });

    private static async Task AssertCompareMarkingsAsync(IPage page)
    {
        await BrowserSession.AssertBannersAsync(page);
        Assert.Equal(ItarBanner, (await BrowserSession.TopBanner(page).InnerTextAsync()).Trim());
        await Expect(page.Locator("[data-viewer-distribution]")).ToContainTextAsync("DISTRIBUTION STATEMENT D");
        await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Back to document" })).ToBeVisibleAsync();
        await Expect(page.Locator("[data-compare-frame-note]")).ToContainTextAsync("no automatic registration");
    }

    private static async Task ExerciseModesAsync(IPage page)
    {
        await WaitReadyAsync(page);
        await AssertCompareMarkingsAsync(page);
        var overlay = await CanvasPixelsAsync(page);
        Assert.True(overlay[0] > overlay[1] * 0.03, $"The overlay covers too little of the canvas: {overlay[0]} of {overlay[1]} pixels.");
        Assert.True(overlay[3] > 0 && overlay[4] > 0, $"The overlay shows both tints (blue {overlay[3]}, orange {overlay[4]}).");

        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Side by side" }).ClickAsync();
        await Expect(page.Locator("#viewer-stage")).ToHaveAttributeAsync("data-mode", "side");
        var side = await CanvasPixelsAsync(page);
        Assert.True(side[0] > side[1] * 0.03, "Side by side is not blank.");
        await Expect(page.Locator(".compare-divider")).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Swipe" }).ClickAsync();
        await Expect(page.Locator("#viewer-stage")).ToHaveAttributeAsync("data-mode", "swipe");
        var before = await page.EvaluateAsync<string>("document.querySelector('#viewer-stage canvas').toDataURL()");
        await page.GetByLabel("Divider").FillAsync("20");
        var after = await page.EvaluateAsync<string>("document.querySelector('#viewer-stage canvas').toDataURL()");
        Assert.NotEqual(before, after);
        var swipe = await CanvasPixelsAsync(page);
        Assert.True(swipe[0] > swipe[1] * 0.03, "Swipe is not blank.");

        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Overlay" }).ClickAsync();
        await Expect(page.Locator("#viewer-stage")).ToHaveAttributeAsync("data-mode", "overlay");
        var solid = await CanvasPixelsAsync(page);
        await page.GetByLabel("Transparency").FillAsync("90");
        var ghost = await CanvasPixelsAsync(page);
        Assert.NotEqual(solid[2], ghost[2]); // the slider changes what is drawn
        // the banners are still the object's own after all the tools
        await AssertCompareMarkingsAsync(page);
    }

    // ---------------------------------------------------------------- the seeded demo

    [E2EFact(Timeout = 300_000)]
    public async Task The_seeded_bracket_shows_two_versions_what_changed_and_a_working_visual_comparison()
    {
        await using var session = new BrowserSession(fx, nameof(The_seeded_bracket_shows_two_versions_what_changed_and_a_working_visual_comparison));
        await session.RunAsync(async () =>
        {
            var models = fx.Manifest.Models ?? throw new InvalidOperationException("The demo models are missing from the seed.");
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await OpenVersionsAsync(session, alice, models.BracketModel);
            await BrowserSession.AssertBannersAsync(page);

            var rows = page.Locator("[data-testid=version-table] tbody tr");
            await Expect(rows).ToHaveCountAsync(2);
            var a2 = page.Locator("tr[data-version='A.2']");
            await Expect(a2).ToContainTextAsync("Current");
            await Expect(a2).ToContainTextAsync("Upright raised by 2.0 mm");
            await Expect(a2).ToContainTextAsync("triangles");
            await Expect(page.Locator("tr[data-version='A.1']")).ToContainTextAsync("Created");

            await page.GetByTestId("compare-go").ClickAsync();
            await Expect(page.GetByTestId("compare-summary")).ToContainTextAsync("Taller by 2.0 units");
            await Expect(page.GetByTestId("compare-summary")).ToContainTextAsync("volume up");
            await Expect(page.GetByTestId("compare-result").GetByRole(AriaRole.Table)).ToContainTextAsync("Size Z");

            await page.GetByTestId("open-visual-compare").ClickAsync(); // a full navigation to the standalone compare page
            await page.WaitForURLAsync(new Regex($"/viewer/compare/documents/{models.BracketModel}"));
            await ExerciseModesAsync(page);
            Assert.Empty(alice.PageErrors);
            Assert.True(alice.ConsoleErrors.Count == 0, "Console errors: " + string.Join(" | ", alice.ConsoleErrors));

            // the plate is open data: its version history is there too, without export-controlled markings
            await OpenVersionsAsync(session, alice, models.PlateModel);
            await Expect(page.Locator("tr[data-version='A.2']")).ToContainTextAsync("thickened");
        });
    }

    // ---------------------------------------------------------------- upload, restore, revise

    [E2EFact(Timeout = 300_000)]
    public async Task A_new_version_goes_in_through_one_dialog_restore_adds_a_version_and_a_released_revision_is_revised_in_the_same_dialog()
    {
        await using var session = new BrowserSession(fx, nameof(A_new_version_goes_in_through_one_dialog_restore_adds_a_version_and_a_released_revision_is_revised_in_the_same_dialog));
        await session.RunAsync(async () =>
        {
            var first = Shape(30);
            var number = await CreateModelAsync("E2E bracket model " + Guid.NewGuid().ToString("N")[..6], first);
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await OpenVersionsAsync(session, alice, number);
            await Expect(page.Locator("[data-testid=version-table] tbody tr")).ToHaveCountAsync(1);

            // --- the dialog: the file and the change note are both required
            await page.GetByTestId("upload-new-version").ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog);
            await Expect(dialog).ToBeVisibleAsync();
            var ok = dialog.Locator("[data-upload-ok]");
            await Expect(ok).ToBeDisabledAsync();
            await dialog.Locator("input[data-testid=upload-file]").SetInputFilesAsync(new FilePayload { Name = "bracket.stl", MimeType = "model/stl", Buffer = Shape(32) });
            await Expect(ok).ToBeDisabledAsync(); // still no note
            await dialog.GetByTestId("upload-note").FillAsync("Raised the upright by 2 mm");
            await Expect(ok).ToBeEnabledAsync();
            await ok.ClickAsync();
            await Expect(dialog).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 60_000 });
            await Expect(page.Locator("tr[data-version='A.2']")).ToContainTextAsync("Raised the upright by 2 mm", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
            await Expect(page.Locator("tr[data-version='A.2']")).ToContainTextAsync("Current");
            await Expect(page.Locator("tr[data-version='A.2']")).ToContainTextAsync("triangles");
            await BrowserSession.AssertBannersAsync(page);

            // --- no stray check-out: the Overview tab offers "Check out" (it would say "Checked out by" otherwise)
            await session.GotoAsync(alice, $"/documents/{number}");
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Check out" })).ToBeVisibleAsync();
            await Expect(page.GetByText("Checked out by")).ToHaveCountAsync(0);

            // --- the same content again is refused with a clear message and creates nothing
            await OpenVersionsAsync(session, alice, number);
            await page.GetByTestId("upload-new-version").ClickAsync();
            dialog = page.GetByRole(AriaRole.Dialog);
            await dialog.Locator("input[data-testid=upload-file]").SetInputFilesAsync(new FilePayload { Name = "bracket.stl", MimeType = "model/stl", Buffer = await LatestBytesAsync(number) });
            await dialog.GetByTestId("upload-note").FillAsync("Same file");
            await dialog.Locator("[data-upload-ok]").ClickAsync();
            await Expect(dialog.GetByTestId("upload-error")).ToContainTextAsync("identical to the current version A.2");
            await dialog.Locator("[data-upload-cancel]").ClickAsync();
            await Expect(page.Locator("[data-testid=version-table] tbody tr")).ToHaveCountAsync(2);

            // --- restore A.1: a NEW version A.3, history untouched
            await page.GetByTestId("restore-A.1").ClickAsync();
            await Expect(page.GetByRole(AriaRole.Dialog)).ToContainTextAsync("Restore version A.1?");
            await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Restore" }).ClickAsync();
            await Expect(page.Locator("tr[data-version='A.3']")).ToContainTextAsync("Restored from A.1", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
            await Expect(page.Locator("[data-testid=version-table] tbody tr")).ToHaveCountAsync(3);
            await Expect(page.Locator("tr[data-version='A.1']")).ToContainTextAsync("Created");
            await Expect(page.Locator("tr[data-version='A.3']")).ToContainTextAsync("triangles");

            // --- release it, then the dialog offers to revise first
            var ada = RealmUsers.Load()["ada"];
            Assert.True((await Docs().SetStateAsync(ada, number, "Released", "E2E release", true, null)).IsOk);
            await OpenVersionsAsync(session, alice, number);
            await page.GetByTestId("upload-new-version").ClickAsync();
            dialog = page.GetByRole(AriaRole.Dialog);
            await Expect(dialog).ToContainTextAsync("Revision A is Released and locked");
            await dialog.Locator("input[data-testid=upload-file]").SetInputFilesAsync(new FilePayload { Name = "bracket.stl", MimeType = "model/stl", Buffer = Shape(36) });
            await dialog.GetByTestId("upload-note").FillAsync("Taller for revision B");
            await Expect(dialog.Locator("[data-upload-ok]")).ToBeDisabledAsync(); // revising is a decision the person makes
            await dialog.GetByTestId("upload-revise").CheckAsync();
            await dialog.Locator("[data-upload-ok]").ClickAsync();
            await Expect(dialog).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 60_000 });
            await Expect(page.Locator("tr[data-version='B.2']")).ToContainTextAsync("Taller for revision B", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
            await Expect(page.Locator("tr[data-version='A.3']")).ToContainTextAsync("Released");

            // --- the ZIP of two versions downloads
            await page.GetByLabel("Select version A.1").CheckAsync();
            await page.GetByLabel("Select version B.2").CheckAsync();
            var download = await page.RunAndWaitForDownloadAsync(async () => await page.GetByTestId("download-zip").ClickAsync());
            Assert.EndsWith("-versions.zip", download.SuggestedFilename);
            Assert.Empty(alice.PageErrors);
        });
    }

    private async Task<byte[]> LatestBytesAsync(string number)
    {
        var alice = RealmUsers.Load()["alice"];
        var docs = Docs();
        var detail = (await docs.GetAsync(alice, number, null, null)).Value!;
        var download = (await docs.DownloadAsync(alice, number, detail.Iteration.Files.Single().Id, null, null, null)).Value!;
        using var ms = new MemoryStream();
        await download.WriteToAsync(ms, CancellationToken.None);
        using var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
        await using var s = zip.GetEntry("bracket.stl")!.Open();
        using var outMs = new MemoryStream();
        await s.CopyToAsync(outMs);
        return outMs.ToArray();
    }

    // ---------------------------------------------------------------- 375 px

    [E2EFact(Timeout = 300_000)]
    public async Task The_versions_tab_the_dialog_and_the_visual_comparison_fit_a_375_pixel_screen()
    {
        await using var session = new BrowserSession(fx, nameof(The_versions_tab_the_dialog_and_the_visual_comparison_fit_a_375_pixel_screen));
        await session.RunAsync(async () =>
        {
            var models = fx.Manifest.Models ?? throw new InvalidOperationException("The demo models are missing from the seed.");
            var alice = await session.SignInAsync("alice", 375, 812);
            var page = alice.Page;
            await OpenVersionsAsync(session, alice, models.BracketModel);
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"), "The versions tab scrolls horizontally at 375 px.");
            await BrowserSession.AssertBannersAsync(page);

            await page.GetByTestId("upload-new-version").ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog);
            await Expect(dialog).ToBeVisibleAsync();
            var box = await dialog.BoundingBoxAsync();
            Assert.NotNull(box);
            Assert.True(box!.X >= 0 && box.X + box.Width <= 375.5, $"The dialog is wider than the screen: {box.X}..{box.X + box.Width}.");
            await dialog.Locator("[data-upload-cancel]").ClickAsync();

            await page.GetByTestId("compare-go").ClickAsync();
            await Expect(page.GetByTestId("compare-summary")).ToContainTextAsync("Taller by 2.0 units");
            await page.GetByTestId("open-visual-compare").ClickAsync();
            await page.WaitForURLAsync(new Regex($"/viewer/compare/documents/{models.BracketModel}"));
            await ExerciseModesAsync(page);
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"), "The comparison scrolls horizontally at 375 px.");
            Assert.Empty(alice.PageErrors);
        });
    }

    // ---------------------------------------------------------------- access and tampering

    [E2EFact(Timeout = 300_000)]
    public async Task A_user_without_access_sees_no_versions_and_the_comparison_banners_cannot_be_removed()
    {
        await using var session = new BrowserSession(fx, nameof(A_user_without_access_sees_no_versions_and_the_comparison_banners_cannot_be_removed));
        await session.RunAsync(async () =>
        {
            var models = fx.Manifest.Models ?? throw new InvalidOperationException("The demo models are missing from the seed.");
            var lena = await session.SignInAsync("lena");
            await session.GotoAsync(lena, $"/documents/{models.BracketModel}?tab=versions");
            await Expect(lena.Page.GetByText("was not found, or you are not authorized")).ToBeVisibleAsync();
            await Expect(lena.Page.Locator("[data-testid=version-table]")).ToHaveCountAsync(0);

            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await OpenVersionsAsync(session, alice, models.BracketModel);
            await page.GetByTestId("compare-go").ClickAsync();
            await page.GetByTestId("open-visual-compare").ClickAsync();
            await page.WaitForURLAsync(new Regex("/viewer/compare/documents/"));
            await WaitReadyAsync(page);
            // removing the banner is noticed: it is restored and the models are hidden until the page is reloaded
            await page.EvaluateAsync("document.querySelector('[data-viewer-banner][data-position=top]').remove()");
            await Expect(page.Locator("body[data-markings-intact=false]")).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 10_000 });
            await Expect(page.Locator("[data-viewer-banner][data-position=top]")).ToHaveCountAsync(1);
            await Expect(page.Locator(".viewer-tamper")).ToBeVisibleAsync();
        });
    }
}
