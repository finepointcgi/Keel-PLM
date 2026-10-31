using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Vault;
using Keel.Seed;
using Microsoft.Playwright;
using Npgsql;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// The Viewer tab of document and part pages as engineers use it (increment 9.5, ADR-0052): a STEP assembly drawn from the placements in the real Open CASCADE
/// fixture, the structure list as the text alternative, keyboard operation, the markings on screen, the strict policy intact, nothing for a reader the markings
/// keep out, and the same page in all three themes with axe. Chromium renders WebGL in software.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class ModelViewerTests(E2EFixture fx)
{
    private const string ItarBanner = "CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA";

    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"],
        controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;

    internal static byte[] FrameStep() => File.ReadAllBytes(Path.Combine(FindRoot(), "tests", "Keel.UnitTests", "Fixtures", "cad", "frame-asm-ap214.step"));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    internal async Task<string> CreateStepDocumentAsync(string name = "E2E frame model")
    {
        var alice = RealmUsers.Load()["alice"];
        var docs = new DocumentService(fx.AppDb, new PostgresAuditLog(fx.AppDb, TimeProvider.System), TimeProvider.System, new ObjectVault(fx.Vault));
        var created = await docs.CreateAsync(alice, new CreateDocumentCommand(name, "CAD", Itar, "Real STEP assembly written by Open CASCADE"),
            new FileUpload("frame.step", "application/step", new MemoryStream(FrameStep())), null);
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        return created.Value!.Number;
    }

    internal async Task AssociateAsync(string documentNumber, string partNumber)
    {
        await using var conn = await fx.AppDb.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO cad.association (id, kind, document_master_id, part_master_id, created_at, created_by)
            VALUES (@id, 'model-of', (SELECT id FROM plm.document_master WHERE number = @d), (SELECT id FROM plm.part_master WHERE number = @p), now(), 'alice')
            ON CONFLICT DO NOTHING
            """, conn);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("d", documentNumber);
        cmd.Parameters.AddWithValue("p", partNumber);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task OpenViewerTabAsync(BrowserSession session, UserPage user, string path)
    {
        await session.GotoAsync(user, path);
        await Expect(user.Page.GetByTestId("viewer-canvas")).ToHaveAttributeAsync("data-viewer-state", "ready", new LocatorAssertionsToHaveAttributeOptions { Timeout = 60_000 });
    }

    private static async Task<int[]> CanvasPixelsAsync(IPage page) => await page.EvaluateAsync<int[]>("""
        async () => {
          const c = document.querySelector('[data-testid=viewer-canvas] canvas');
          // The page's strict policy has no img-src data:, so the pixels are read through a Blob, not a data URL.
          const blob = await new Promise(resolve => c.toBlob(resolve, 'image/png'));
          const img = await createImageBitmap(blob);
          const k = document.createElement('canvas'); k.width = 320; k.height = 200;
          const g = k.getContext('2d'); g.drawImage(img, 0, 0, k.width, k.height);
          const d = g.getImageData(0, 0, k.width, k.height).data;
          const bg = [d[0], d[1], d[2]]; let non = 0; const colors = new Set();
          for (let i = 0; i < d.length; i += 4) {
            if (Math.abs(d[i] - bg[0]) + Math.abs(d[i + 1] - bg[1]) + Math.abs(d[i + 2] - bg[2]) > 30) non++;
            colors.add(((d[i] >> 4) << 8) | ((d[i + 1] >> 4) << 4) | (d[i + 2] >> 4));
          }
          return [non, k.width * k.height, colors.size];
        }
        """);

    private static async Task<double[]> CameraAsync(IPage page) =>
        [.. (await page.GetByTestId("viewer-canvas").GetAttributeAsync("data-camera"))!.Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture))];

    /// <summary>Waits until the condition holds (the picture redraws on the next animation frame, so a key press shows in <c>data-camera</c> a moment later).</summary>
    private static async Task UntilAsync(Func<Task<bool>> condition, string what)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail("Timed out waiting for: " + what);
    }

    private static void AssertNoBrowserErrors(UserPage user)
    {
        Assert.Empty(user.PageErrors);
        Assert.True(user.ConsoleErrors.Count == 0, "Console errors: " + string.Join(" | ", user.ConsoleErrors));
    }

    [E2EFact(Timeout = 240_000)]
    public async Task A_step_assembly_is_drawn_from_its_placements_with_a_text_alternative_and_keyboard_control()
    {
        await using var session = new BrowserSession(fx, nameof(A_step_assembly_is_drawn_from_its_placements_with_a_text_alternative_and_keyboard_control));
        await session.RunAsync(async () =>
        {
            var number = await CreateStepDocumentAsync();
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await OpenViewerTabAsync(session, alice, $"/documents/{number}?tab=viewer");
            await Expect(page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Viewer" })).ToHaveAttributeAsync("aria-selected", "true");

            // markings: the page banner is the document's, top and bottom, and the strict policy stayed intact (no console or page errors)
            await BrowserSession.AssertBannersAsync(page);
            Assert.Equal(ItarBanner, (await BrowserSession.TopBanner(page).InnerTextAsync()).Trim());

            // the picture: 24 instances, 21 of them parts drawn as boxes (the frame and two bracket assemblies are assemblies)
            var host = page.GetByTestId("viewer-canvas");
            await Expect(host).ToHaveAttributeAsync("data-boxes", "21");
            var pixels = await CanvasPixelsAsync(page);
            Assert.True(pixels[0] > pixels[1] * 0.03, $"The assembly covers too little of the canvas: {pixels[0]} of {pixels[1]} pixels.");
            Assert.True(pixels[2] >= 4, "A shaded box scene shows several tones.");

            // the text alternative carries the same structure
            var nodes = page.GetByTestId("viewer-node");
            await Expect(nodes).ToHaveCountAsync(24);
            await Expect(page.GetByTestId("viewer-summary")).ToContainTextAsync("24 instances of 6 parts");
            await Expect(page.GetByTestId("viewer-limits")).ToContainTextAsync("bounding box");
            await Expect(page.GetByTestId("viewer-status")).ToContainTextAsync("21 parts drawn as bounding boxes");

            // keyboard: arrows orbit, + and - zoom, Home resets
            await host.FocusAsync();
            var start = await CameraAsync(page);
            await page.Keyboard.PressAsync("ArrowRight");
            await page.Keyboard.PressAsync("ArrowDown");
            await UntilAsync(async () => { var c = await CameraAsync(page); return c[0] != start[0] && c[1] != start[1]; }, "the view to orbit in both directions");
            await page.Keyboard.PressAsync("+");
            await UntilAsync(async () => (await CameraAsync(page))[2] < start[2], "zoom in");
            await page.Keyboard.PressAsync("-");
            await page.Keyboard.PressAsync("-");
            await UntilAsync(async () => (await CameraAsync(page))[2] > start[2], "zoom out");
            await page.Keyboard.PressAsync("Home");
            await UntilAsync(async () => (await CameraAsync(page)).SequenceEqual(start), "the view to reset");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Orbit left" }).ClickAsync();
            await UntilAsync(async () => (await CameraAsync(page))[0] != start[0], "the view to orbit");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Reset view" }).ClickAsync();
            await UntilAsync(async () => (await CameraAsync(page)).SequenceEqual(start), "the view to reset");

            // selecting a part in the list shows what the file says about it
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "KP-1002" }).ClickAsync();
            var selection = page.GetByTestId("viewer-selection");
            await Expect(selection).ToContainTextAsync("KP-1002");
            await Expect(selection).ToContainTextAsync("120 × 80 × 4 mm");
            await Expect(selection).ToContainTextAsync("As given by the file");
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "KP-1002" })).ToHaveAttributeAsync("aria-pressed", "true");

            await Expect(page.GetByTestId("open-full-viewer")).ToHaveAttributeAsync("href", new Regex("^/viewer/documents/" + number + "/files/"));
            AssertNoBrowserErrors(alice);
        });
    }

    [E2EFact(Timeout = 240_000)]
    public async Task An_stl_model_shows_its_triangles_and_the_part_page_offers_the_tab_only_where_a_model_is_attached()
    {
        await using var session = new BrowserSession(fx, nameof(An_stl_model_shows_its_triangles_and_the_part_page_offers_the_tab_only_where_a_model_is_attached));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            var models = fx.Manifest.Models ?? throw new InvalidOperationException("The demo models are missing.");
            await OpenViewerTabAsync(session, alice, $"/documents/{models.BracketModel}?tab=viewer");
            await Expect(page.GetByTestId("viewer-canvas")).ToHaveAttributeAsync("data-kind", "mesh");
            await Expect(page.GetByTestId("viewer-canvas")).ToHaveAttributeAsync("data-triangles", new Regex("^[1-9][0-9]*$"));
            var pixels = await CanvasPixelsAsync(page);
            Assert.True(pixels[0] > pixels[1] * 0.02, "The mesh covers too little of the canvas.");

            // part pages: no tab without a model, a tab with one
            await session.GotoAsync(alice, "/parts/" + fx.Manifest.TopAssembly);
            await Expect(page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Structure" })).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Viewer" })).ToHaveCountAsync(0);

            var step = await CreateStepDocumentAsync("E2E gimbal model");
            await AssociateAsync(step, fx.Manifest.GimbalAssembly);
            await session.GotoAsync(alice, "/parts/" + fx.Manifest.GimbalAssembly);
            await BrowserSession.OpenTabAsync(page, "Viewer");
            await Expect(page.GetByTestId("viewer-canvas")).ToHaveAttributeAsync("data-viewer-state", "ready", new LocatorAssertionsToHaveAttributeOptions { Timeout = 60_000 });
            await Expect(page.GetByTestId("viewer-node")).ToHaveCountAsync(24);
            await BrowserSession.AssertBannersAsync(page);
            Assert.Equal(ItarBanner, (await BrowserSession.TopBanner(page).InnerTextAsync()).Trim());
            AssertNoBrowserErrors(alice);
        });
    }

    [E2EFact(Timeout = 240_000)]
    public async Task A_reader_the_markings_keep_out_gets_no_tab_content_and_no_part_name()
    {
        await using var session = new BrowserSession(fx, nameof(A_reader_the_markings_keep_out_gets_no_tab_content_and_no_part_name));
        await session.RunAsync(async () =>
        {
            var number = await CreateStepDocumentAsync("E2E restricted frame model");
            await AssociateAsync(number, fx.Manifest.PayloadAssembly);
            var lena = await session.SignInAsync("lena");
            var page = lena.Page;
            await page.GotoAsync(fx.BaseUrl + $"/documents/{number}?tab=viewer");
            await Expect(page.GetByText("was not found, or you are not authorized")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
            var text = await page.Locator("body").InnerTextAsync();
            foreach (var secret in new[] { "KA-3000", "Frame assembly", "KF-2001", "Mounting plate", "E2E restricted frame model" })
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            await Expect(page.GetByTestId("viewer-canvas")).ToHaveCountAsync(0);

            // the part page of a part Lena may read lists the model only as a placeholder
            await page.GotoAsync(fx.BaseUrl + $"/parts/{fx.Manifest.PayloadAssembly}?tab=viewer");
            await Expect(page.Locator("h1")).ToBeVisibleAsync();
            var partText = await page.Locator("body").InnerTextAsync();
            Assert.DoesNotContain("KA-3000", partText, StringComparison.Ordinal);
            Assert.DoesNotContain(number, partText, StringComparison.Ordinal);
            await Expect(page.GetByTestId("viewer-canvas")).ToHaveCountAsync(0);
        });
    }

    private async Task AuditThemeAsync(string theme, string testName)
    {
        await using var session = new BrowserSession(fx, testName);
        await session.RunAsync(async () =>
        {
            var step = await CreateStepDocumentAsync("E2E accessibility frame model");
            var alice = await session.SignInAsync("alice", 1280, 900);
            var page = alice.Page;
            var original = await page.EvaluateAsync<string>("document.documentElement.getAttribute('data-theme')");
            await AccessibilityTests.ChooseThemeAsync(page, "alice", theme);
            var axe = await File.ReadAllTextAsync(AccessibilityTests.AxePath());
            var findings = new List<string>();
            try
            {
                var models = fx.Manifest.Models ?? throw new InvalidOperationException("The demo models are missing.");
                foreach (var (path, label) in new[] { ($"/documents/{step}?tab=viewer", "STEP viewer tab"), ($"/documents/{models.BracketModel}?tab=viewer", "STL viewer tab") })
                {
                    await OpenViewerTabAsync(session, alice, path);
                    await BrowserSession.AssertBannersAsync(page);
                    await AccessibilityTests.RunAxeAsync(page, $"{theme} {label}", axe, findings);
                    if (label.StartsWith("STEP", StringComparison.Ordinal))
                    {
                        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "KP-1002" }).ClickAsync();
                        await Expect(page.GetByTestId("viewer-selection")).ToBeVisibleAsync();
                        await AccessibilityTests.RunAxeAsync(page, $"{theme} {label} with a selection", axe, findings);
                    }
                }
                // The canvas takes its colours from the theme: the background it clears to differs between the light and the dark/high-contrast choices.
                var pixels = await CanvasPixelsAsync(page);
                Assert.True(pixels[0] > 0);
            }
            finally
            {
                try { await AccessibilityTests.ChooseThemeAsync(page, "alice", original); } catch (PlaywrightException) { /* best effort */ }
            }
            Assert.True(findings.Count == 0, "Accessibility findings:\n" + string.Join("\n", findings));
        });
    }

    [E2EFact(Timeout = 300_000)]
    public Task The_viewer_tab_passes_axe_in_the_light_theme() => AuditThemeAsync("light", nameof(The_viewer_tab_passes_axe_in_the_light_theme));

    [E2EFact(Timeout = 300_000)]
    public Task The_viewer_tab_passes_axe_in_the_dark_theme() => AuditThemeAsync("dark", nameof(The_viewer_tab_passes_axe_in_the_dark_theme));

    [E2EFact(Timeout = 300_000)]
    public Task The_viewer_tab_passes_axe_in_the_high_contrast_theme() => AuditThemeAsync("high-contrast", nameof(The_viewer_tab_passes_axe_in_the_high_contrast_theme));
}
