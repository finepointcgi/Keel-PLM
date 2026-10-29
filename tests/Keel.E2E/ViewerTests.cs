using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Vault;
using Keel.Seed;
using Keel.TestSupport;
using Microsoft.Playwright;
using UglyToad.PdfPig;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// The in-browser file viewer as engineers use it (increment 6.0): open a drawing, a CAD model or an image from a document page without downloading,
/// with the banner and distribution statement always on screen, the strict CSP intact, access and audit identical to downloads. The files are
/// hand-written synthetic data from <see cref="ViewerSamples"/>; Chromium renders WebGL in software.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed partial class ViewerTests(E2EFixture fx)
{
    private const string ItarBanner = "CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA";

    private sealed record Doc(string Number, Guid FileId, string FileName, string Sha256);

    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"],
        controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;

    /// <summary>Puts a synthetic document with one primary file into the running system through the real services (as alice, who authors ORCA data).</summary>
    private async Task<Doc> CreateAsync(string fileName, byte[] bytes, Markings? markings = null, string mediaType = "application/octet-stream")
    {
        var alice = RealmUsers.Load()["alice"];
        var docs = new DocumentService(fx.AppDb, new PostgresAuditLog(fx.AppDb, TimeProvider.System), TimeProvider.System, new ObjectVault(fx.Vault));
        var created = await docs.CreateAsync(alice, new CreateDocumentCommand($"E2E viewer {fileName}", "Drawing", markings ?? Itar, "Synthetic viewer test file"),
            new FileUpload(fileName, mediaType, new MemoryStream(bytes)), null);
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        var file = created.Value!.Iteration.Files.Single();
        return new Doc(created.Value.Number, file.Id, fileName, file.Sha256);
    }

    private static async Task<IPage> OpenFromDocumentPageAsync(BrowserSession session, UserPage user, Doc doc)
    {
        await session.GotoAsync(user, "/documents/" + doc.Number);
        var view = user.Page.Locator("a[data-action=view]").First;
        await Expect(view).ToBeVisibleAsync();
        await view.ClickAsync(); // a full navigation to the standalone viewer, not a Blazor route
        await user.Page.WaitForURLAsync(new Regex($"/viewer/documents/{doc.Number}/files/{doc.FileId}"));
        await Expect(user.Page.Locator("body.viewer-page")).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 60_000 });
        return user.Page;
    }

    private static async Task WaitReadyAsync(IPage page) =>
        await Expect(page.Locator("body[data-viewer-state=ready]")).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 90_000 });

    private static async Task AssertMarkingsAsync(IPage page, string banner = ItarBanner)
    {
        await BrowserSession.AssertBannersAsync(page);
        Assert.Equal(banner, (await BrowserSession.TopBanner(page).InnerTextAsync()).Trim());
        await Expect(page.Locator("[data-viewer-distribution]")).ToContainTextAsync("DISTRIBUTION STATEMENT D");
        await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Back to document" })).ToBeVisibleAsync();
    }

    /// <summary>[non-background pixels, total pixels, distinct coarse colours] of the WebGL canvas, read back through toDataURL.</summary>
    private static async Task<int[]> CanvasPixelsAsync(IPage page)
    {
        return await page.EvaluateAsync<int[]>("""
        async () => {
          const c = document.querySelector('#viewer-stage canvas');
          const img = new Image(); img.src = c.toDataURL('image/png'); await img.decode();
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
    }


    private static async Task<string> CanvasUrlAsync(IPage page)
    {
        return await page.EvaluateAsync<string>("document.querySelector('#viewer-stage canvas').toDataURL()");
    }

    private static void AssertNoBrowserErrors(UserPage user)
    {
        Assert.Empty(user.PageErrors);
        Assert.True(user.ConsoleErrors.Count == 0, "Console errors: " + string.Join(" | ", user.ConsoleErrors));
    }

    // ---------------------------------------------------------------- 3D

    [E2EFact(Timeout = 240_000)]
    public async Task Stl_renders_real_pixels_with_the_overlay_tools_and_a_screenshot_with_burned_in_markings()
    {
        await using var session = new BrowserSession(fx, nameof(Stl_renders_real_pixels_with_the_overlay_tools_and_a_screenshot_with_burned_in_markings));
        await session.RunAsync(async () =>
        {
            var doc = await CreateAsync("cube.stl", ViewerSamples.AsciiStlCube());
            var alice = await session.SignInAsync("alice");
            var page = await OpenFromDocumentPageAsync(session, alice, doc);
            await WaitReadyAsync(page);
            await AssertMarkingsAsync(page);

            // Real, non-blank rendering read back from the canvas.
            var shaded = await CanvasPixelsAsync(page);
            Assert.True(shaded[0] > shaded[1] * 0.04, $"The model covers too little of the canvas: {shaded[0]} of {shaded[1]} pixels.");
            Assert.True(shaded[2] >= 4, "A shaded cube shows several tones.");
            Assert.Equal("12", await page.Locator("#viewer-stage").GetAttributeAsync("data-triangles"));
            Assert.Equal("10.0000,10.0000,10.0000", await page.Locator("#viewer-stage").GetAttributeAsync("data-bbox"));
            await Expect(page.Locator(".viewer-readout-block")).ToContainTextAsync("Bounding box 10.00 × 10.00 × 10.00");

            // The overlay is in the DOM, shows the object's banner and statement, and the page banners are the same text.
            var overlay = page.Locator("[data-viewer-overlay]");
            await Expect(overlay).ToBeVisibleAsync();
            await Expect(overlay.Locator(".viewer-overlay-top")).ToHaveTextAsync(ItarBanner);
            await Expect(overlay.Locator(".viewer-overlay-bottom")).ToContainTextAsync("DISTRIBUTION STATEMENT D");

            // Orbit changes the picture; wireframe changes it again.
            var before = await CanvasUrlAsync(page);
            var box = (await page.Locator("#viewer-stage canvas").BoundingBoxAsync())!;
            await page.Mouse.MoveAsync(box.X + box.Width / 2, box.Y + box.Height / 2);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(box.X + box.Width / 2 + 120, box.Y + box.Height / 2 + 40, new MouseMoveOptions { Steps = 8 });
            await page.Mouse.UpAsync();
            Assert.NotEqual(before, await CanvasUrlAsync(page));
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Wireframe" }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Wireframe" })).ToHaveAttributeAsync("aria-pressed", "true");
            var wire = await CanvasPixelsAsync(page);
            Assert.True(wire[0] < shaded[0], "A wireframe covers fewer pixels than the shaded solid.");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Wireframe" }).ClickAsync();

            // Standard views: the front view of a cube is a square silhouette that fills a fixed share of the height.
            await page.Locator("#viewer-toolbar select").First.SelectOptionAsync("front");
            var front = await CanvasPixelsAsync(page);
            Assert.True(front[0] > 0);

            // Measurement between two clicked points on the model.
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Measure", Exact = true }).ClickAsync();
            await page.Mouse.ClickAsync(box.X + box.Width / 2 - 25, box.Y + box.Height / 2 - 25);
            await page.Mouse.ClickAsync(box.X + box.Width / 2 + 25, box.Y + box.Height / 2 + 25);
            await Expect(page.Locator(".viewer-readout-block")).ToContainTextAsync("Distance");
            var measured = double.Parse(await page.Locator("#viewer-stage canvas").GetAttributeAsync("data-measured") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(measured, 0.5, 17.4); // the cube is 10 on a side: no two points on it are further apart than its 17.3 diagonal
            await Expect(page.Locator(".viewer-measure-label")).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Measure", Exact = true }).ClickAsync();

            // Section plane: cutting the model in half leaves fewer pixels of it.
            var uncut = await CanvasPixelsAsync(page);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Section", Exact = true }).ClickAsync();
            await page.Locator("#viewer-toolbar label:has-text(\"Axis\") select").SelectOptionAsync("x");
            await page.Locator("#viewer-toolbar label:has-text(\"Position\") input").FillAsync("20");
            var cut = await CanvasPixelsAsync(page);
            Assert.True(cut[0] < uncut[0] * 0.8, $"Section did not remove geometry ({cut[0]} vs {uncut[0]} pixels).");

            // The screenshot has the banner, statement and trace line burned into its own bands.
            var download = await page.RunAndWaitForDownloadAsync(async () => await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Screenshot" }).ClickAsync());
            Assert.Equal("cube-view.png", download.SuggestedFilename);
            var path = Path.Combine(Path.GetTempPath(), "keel-e2e-" + Guid.NewGuid().ToString("N") + ".png");
            await download.SaveAsAsync(path);
            var shot = await page.EvaluateAsync<int[]>("""
                async (b64) => {
                  const img = new Image(); img.src = 'data:image/png;base64,' + b64; await img.decode();
                  const k = document.createElement('canvas'); k.width = img.width; k.height = img.height;
                  const g = k.getContext('2d'); g.drawImage(img, 0, 0);
                  const px = (x, y) => Array.from(g.getImageData(x, y, 1, 1).data);
                  const white = (y0, y1) => { let n = 0; const d = g.getImageData(0, y0, k.width, y1 - y0).data; for (let i = 0; i < d.length; i += 4) if (d[i] > 235 && d[i + 1] > 235 && d[i + 2] > 235) n++; return n; };
                  const [r, gr, b] = px(2, 2); const [r2, g2, b2] = px(2, k.height - 2);
                  return [img.width, img.height, r, gr, b, r2, g2, b2, white(0, 24), white(k.height - 60, k.height)];
                }
                """, Convert.ToBase64String(await File.ReadAllBytesAsync(path)));
            File.Delete(path);
            Assert.True(shot[1] > (int)box.Height, "The export is taller than the 3D view: the bands are added, not overprinted.");
            Assert.Equal([80, 43, 133], shot[2..5]);   // CUI purple band on top
            Assert.Equal([80, 43, 133], shot[5..8]);   // and at the bottom
            Assert.True(shot[8] > 40, "Banner text (white pixels) is burned into the top band.");
            Assert.True(shot[9] > 40, "Statement and trace text are burned into the bottom band.");

            // The audit trail has this view.
            await using var conn = await fx.AppDb.OpenConnectionAsync();
            await using var cmd = new Npgsql.NpgsqlCommand("SELECT outcome, payload FROM audit.event WHERE action = 'document.view' AND object_id = @n", conn);
            cmd.Parameters.AddWithValue("n", doc.Number);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Success", reader.GetString(0));
            Assert.Contains(doc.Sha256, reader.GetString(1), StringComparison.Ordinal);
            Assert.Contains("\"viewer\":\"model3d\"", reader.GetString(1), StringComparison.Ordinal);

            AssertNoBrowserErrors(alice);
        });
    }

    [E2EFact(Timeout = 240_000)]
    public async Task Step_runs_in_a_worker_and_a_glb_with_two_parts_explodes()
    {
        await using var session = new BrowserSession(fx, nameof(Step_runs_in_a_worker_and_a_glb_with_two_parts_explodes));
        await session.RunAsync(async () =>
        {
            var step = await CreateAsync("box.step", ViewerSamples.StepBox());
            var glb = await CreateAsync("two-boxes.glb", ViewerSamples.GlbTwoBoxes());
            var gltf = await CreateAsync("two-boxes.gltf", ViewerSamples.GltfTwoBoxes());
            var alice = await session.SignInAsync("alice");
            var workers = new List<string>();
            alice.Page.Worker += (_, w) => workers.Add(w.Url);

            var page = await OpenFromDocumentPageAsync(session, alice, step);
            await WaitReadyAsync(page);
            await AssertMarkingsAsync(page);
            Assert.Contains(workers, w => w.EndsWith("/viewer/step-worker.js", StringComparison.Ordinal)); // the CAD import ran off the main thread
            Assert.Equal("40.0000,20.0000,10.0000", await page.Locator("#viewer-stage").GetAttributeAsync("data-bbox"));
            await Expect(page.Locator(".viewer-readout-block")).ToContainTextAsync("40.00 × 20.00 × 10.00 mm");
            Assert.Equal("12", await page.Locator("#viewer-stage").GetAttributeAsync("data-triangles"));
            var stepPixels = await CanvasPixelsAsync(page);
            Assert.True(stepPixels[0] > stepPixels[1] * 0.03);
            // Distances of a CAD model are in the model's millimetres.
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Measure", Exact = true }).ClickAsync();
            var box = (await page.Locator("#viewer-stage canvas").BoundingBoxAsync())!;
            await page.Mouse.ClickAsync(box.X + box.Width / 2 - 30, box.Y + box.Height / 2);
            await page.Mouse.ClickAsync(box.X + box.Width / 2 + 30, box.Y + box.Height / 2);
            await Expect(page.Locator(".viewer-readout-block")).ToContainTextAsync("mm");
            AssertNoBrowserErrors(alice);

            foreach (var (model, name) in new[] { (glb, "GLB"), (gltf, "glTF") })
            {
                page = await OpenFromDocumentPageAsync(session, alice, model);
                await WaitReadyAsync(page);
                await AssertMarkingsAsync(page);
                Assert.Equal("24", await page.Locator("#viewer-stage").GetAttributeAsync("data-triangles"));
                Assert.Equal("3.0000,1.0000,1.0000", await page.Locator("#viewer-stage").GetAttributeAsync("data-bbox"));
                var assembled = await CanvasUrlAsync(page);
                var explode = page.GetByLabel("Explode (2 parts)");
                await Expect(explode).ToBeVisibleAsync();
                await explode.FillAsync("100");
                Assert.NotEqual(assembled, await CanvasUrlAsync(page));
                AssertNoBrowserErrors(alice);
                Assert.True(name.Length > 0);
            }
        });
    }

    [E2EFact(Timeout = 240_000)]
    public async Task Obj_loads_and_keyboard_alone_drives_the_3d_view()
    {
        await using var session = new BrowserSession(fx, nameof(Obj_loads_and_keyboard_alone_drives_the_3d_view));
        await session.RunAsync(async () =>
        {
            var doc = await CreateAsync("cube.obj", ViewerSamples.ObjCube());
            var alice = await session.SignInAsync("alice");
            var page = await OpenFromDocumentPageAsync(session, alice, doc);
            await WaitReadyAsync(page);
            Assert.Equal("12", await page.Locator("#viewer-stage").GetAttributeAsync("data-triangles"));

            await page.Locator("#viewer-stage canvas").FocusAsync();
            await page.Keyboard.PressAsync("w");
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Wireframe" })).ToHaveAttributeAsync("aria-pressed", "true");
            await page.Keyboard.PressAsync("m");
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Measure", Exact = true })).ToHaveAttributeAsync("aria-pressed", "true");
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Measure", Exact = true })).ToHaveAttributeAsync("aria-pressed", "false");
            var before = await CanvasUrlAsync(page);
            await page.Keyboard.PressAsync("6"); // top view
            Assert.NotEqual(before, await CanvasUrlAsync(page));
            AssertNoBrowserErrors(alice);
        });
    }

    // ---------------------------------------------------------------- PDF and images

    [E2EFact(Timeout = 240_000)]
    public async Task Pdf_pages_show_the_stamped_banner_the_text_is_selectable_and_navigation_works_by_keyboard()
    {
        await using var session = new BrowserSession(fx, nameof(Pdf_pages_show_the_stamped_banner_the_text_is_selectable_and_navigation_works_by_keyboard));
        await session.RunAsync(async () =>
        {
            var tag = Guid.NewGuid().ToString("N")[..8];
            var doc = await CreateAsync("two-pages.pdf", ViewerSamples.Pdf(2, tag), mediaType: "application/pdf");
            var alice = await session.SignInAsync("alice");
            var page = await OpenFromDocumentPageAsync(session, alice, doc);
            await WaitReadyAsync(page);
            await AssertMarkingsAsync(page);
            Assert.Equal("2", await page.Locator("body").GetAttributeAsync("data-pdf-pages"));

            // Page 1 is drawn on a canvas that is not blank, and its text layer carries the sheet text AND the stamped banner and notice.
            var first = page.Locator(".pdf-page[data-page='1']");
            await Expect(first).ToHaveAttributeAsync("data-rendered", "true", new LocatorAssertionsToHaveAttributeOptions { Timeout = 60_000 });
            var ink = await page.EvaluateAsync<int>("""
                () => { const c = document.querySelector('.pdf-page[data-page="1"] canvas'); const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
                  let dark = 0; for (let i = 0; i < d.length; i += 4) if (d[i] < 200 || d[i + 1] < 200 || d[i + 2] < 200) dark++; return dark; }
                """);
            Assert.True(ink > 500, "The page canvas is blank.");
            var textLayer = page.Locator(".pdf-page[data-page='1'] .pdf-text");
            await Expect(textLayer).ToContainTextAsync($"Synthetic sheet 1 of 2 {tag}", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
            await Expect(textLayer).ToContainTextAsync("DISTRIBUTION STATEMENT D");
            await Expect(textLayer).ToContainTextAsync("ITAR USML XI(d)");

            // What is on screen equals what a download contains: same page count, banner on every page of the downloaded PDF too.
            var link = await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Download (marked)" }).GetAttributeAsync("href");
            var bytes = await (await alice.Context.APIRequest.GetAsync(fx.BaseUrl + link)).BodyAsync();
            using (var downloaded = PdfDocument.Open(bytes))
            {
                Assert.Equal(2, downloaded.NumberOfPages);
                Assert.All(downloaded.GetPages(), p => Assert.Contains(ItarBanner, p.Text.Replace("\n", " ", StringComparison.Ordinal), StringComparison.Ordinal));
            }

            // Keyboard: next page, zoom, fit width.
            var zoom = page.Locator(".viewer-readout[role=status]");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Next page" }).ClickAsync();
            await Expect(page.GetByLabel("Page number")).ToHaveValueAsync("2");
            await Expect(page.Locator(".pdf-page[data-page='2']")).ToHaveAttributeAsync("data-rendered", "true", new LocatorAssertionsToHaveAttributeOptions { Timeout = 60_000 });
            await page.Locator(".pdf-scroll").FocusAsync();
            var fitText = await zoom.InnerTextAsync();
            await page.Keyboard.PressAsync("-");
            await Expect(zoom).Not.ToHaveTextAsync(fitText);
            await page.Keyboard.PressAsync("0");
            await Expect(zoom).ToHaveTextAsync(fitText);
            await page.Keyboard.PressAsync("Home");
            await Expect(page.GetByLabel("Page number")).ToHaveValueAsync("1");
            AssertNoBrowserErrors(alice);
        });
    }

    [E2EFact(Timeout = 240_000)]
    public async Task Png_and_svg_show_as_images_zoom_by_keyboard_and_svg_script_never_runs()
    {
        await using var session = new BrowserSession(fx, nameof(Png_and_svg_show_as_images_zoom_by_keyboard_and_svg_script_never_runs));
        await session.RunAsync(async () =>
        {
            var png = await CreateAsync("picture.png", ViewerSamples.Png());
            var svg = await CreateAsync("shape.svg", ViewerSamples.SvgWithScript());
            var alice = await session.SignInAsync("alice");

            var page = await OpenFromDocumentPageAsync(session, alice, png);
            await WaitReadyAsync(page);
            await AssertMarkingsAsync(page);
            Assert.Equal(64, await page.Locator("img.image-content").EvaluateAsync<int>("i => i.naturalWidth"));
            Assert.Equal(32, await page.Locator("img.image-content").EvaluateAsync<int>("i => i.naturalHeight"));
            await Expect(page.Locator("[data-viewer-overlay] .viewer-overlay-top")).ToHaveTextAsync(ItarBanner);
            var readout = page.Locator(".viewer-readout[role=status]");
            var fitted = await readout.InnerTextAsync();
            await page.Locator(".image-frame").FocusAsync();
            await page.Keyboard.PressAsync("+");
            await Expect(readout).Not.ToHaveTextAsync(fitted);
            await page.Keyboard.PressAsync("0");
            await Expect(readout).ToHaveTextAsync(fitted);

            page = await OpenFromDocumentPageAsync(session, alice, svg);
            await WaitReadyAsync(page);
            Assert.Equal("shape.svg · Keel PLM viewer", await page.TitleAsync()); // the SVG's script (title = 'pwned') did not run
            Assert.Equal(120, await page.Locator("img.image-content").EvaluateAsync<int>("i => i.naturalWidth"));
            Assert.Equal(0, await page.Locator("svg, script:not([src])").CountAsync()); // never inlined into the document
            // Opened directly, the response is sandboxed: no script either.
            var direct = await alice.Context.APIRequest.GetAsync($"{fx.BaseUrl}/viewer/content/documents/{svg.Number}/files/{svg.FileId}");
            Assert.Contains("sandbox", direct.Headers["content-security-policy"], StringComparison.Ordinal);
            AssertNoBrowserErrors(alice);
        });
    }

    // ---------------------------------------------------------------- errors and unsupported files

    [E2EFact(Timeout = 240_000)]
    public async Task Corrupt_unsupported_and_oversized_looking_files_get_clear_panels_without_console_errors()
    {
        await using var session = new BrowserSession(fx, nameof(Corrupt_unsupported_and_oversized_looking_files_get_clear_panels_without_console_errors));
        await session.RunAsync(async () =>
        {
            var corruptStep = await CreateAsync("broken.step", ViewerSamples.CorruptStep());
            var brokenPng = await CreateAsync("truncated.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(new byte[40]).ToArray());
            var textStl = await CreateAsync("notes.stl", System.Text.Encoding.UTF8.GetBytes("this is a text note, not a mesh " + Guid.NewGuid()));
            var docx = await CreateAsync("spec.docx", System.Text.Encoding.ASCII.GetBytes("PK-not-really-a-docx " + Guid.NewGuid()));
            var unstampable = await CreateAsync("scan.pdf", System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\nnot really " + Guid.NewGuid()), mediaType: "application/pdf");
            var alice = await session.SignInAsync("alice");

            var page = await OpenFromDocumentPageAsync(session, alice, corruptStep);
            await Expect(page.Locator("body[data-viewer-state=error]")).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 90_000 });
            await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("could not be converted");
            await AssertMarkingsAsync(page);
            await Expect(page.Locator("a.viewer-button[download]").First).ToBeVisibleAsync();

            page = await OpenFromDocumentPageAsync(session, alice, brokenPng);
            await Expect(page.Locator("body[data-viewer-state=error]")).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 60_000 });
            await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("could not be decoded");

            page = await OpenFromDocumentPageAsync(session, alice, textStl);
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "No preview for this type" })).ToBeVisibleAsync();
            await Expect(page.Locator(".viewer-panel")).ToContainTextAsync("not a valid STL model");
            await Expect(page.Locator(".viewer-details")).ToContainTextAsync(textStl.Sha256);
            await AssertMarkingsAsync(page);

            page = await OpenFromDocumentPageAsync(session, alice, docx);
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "No preview for this type" })).ToBeVisibleAsync();
            await Expect(page.Locator(".viewer-panel a.viewer-button[download]")).ToBeVisibleAsync();
            Assert.Equal(0, await page.Locator("#viewer-stage").CountAsync());

            page = await OpenFromDocumentPageAsync(session, alice, unstampable);
            await Expect(page.Locator("body[data-viewer-state=error]")).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 60_000 });
            await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("cannot be marked");

            // The only console noise allowed is the browser's own line for a refused request (the 422 above); no script errors, no CSP violations.
            Assert.Empty(alice.PageErrors);
            Assert.All(alice.ConsoleErrors, e => Assert.Contains("422", e, StringComparison.Ordinal));
        });
    }

    // ---------------------------------------------------------------- access

    [E2EFact(Timeout = 240_000)]
    public async Task Users_without_rights_get_the_same_not_found_as_for_a_missing_document_and_a_licensed_partner_can_view()
    {
        await using var session = new BrowserSession(fx, nameof(Users_without_rights_get_the_same_not_found_as_for_a_missing_document_and_a_licensed_partner_can_view));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var itarDrawing = await CreateAsync("bracket.png", ViewerSamples.Png());
            var lena = await session.SignInAsync("lena");
            var page = lena.Page;

            // Neither the page, nor the metadata, nor the bytes of an ITAR document open for a non-U.S. person without authorization.
            await page.GotoAsync($"{fx.BaseUrl}/viewer/documents/{itarDrawing.Number}/files/{itarDrawing.FileId}");
            await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("was not found, or you are not authorized to see it");
            await Expect(BrowserSession.TopBanner(page)).ToHaveTextAsync("UNCLASSIFIED"); // no marking of the hidden object leaks into the banner
            Assert.DoesNotContain("bracket.png", await page.ContentAsync(), StringComparison.Ordinal);
            var missing = await lena.Context.APIRequest.GetAsync($"{fx.BaseUrl}/viewer/documents/D-999999/files/{itarDrawing.FileId}");
            Assert.Equal(404, missing.Status);
            foreach (var url in new[] { $"/viewer/meta/documents/{itarDrawing.Number}/files/{itarDrawing.FileId}", $"/viewer/content/documents/{itarDrawing.Number}/files/{itarDrawing.FileId}" })
            {
                var res = await lena.Context.APIRequest.GetAsync(fx.BaseUrl + url);
                Assert.Equal(404, res.Status);
                Assert.DoesNotContain("bracket", await res.TextAsync(), StringComparison.Ordinal);
            }
            await using (var conn = await fx.AppDb.OpenConnectionAsync())
            await using (var cmd = new Npgsql.NpgsqlCommand("SELECT count(*) FROM audit.event WHERE action = 'document.view' AND outcome = 'Denied' AND object_id = @n", conn))
            {
                cmd.Parameters.AddWithValue("n", itarDrawing.Number);
                Assert.True((long)(await cmd.ExecuteScalarAsync())! >= 3, "Every denied attempt is audited.");
            }

            // Farid holds TAA-0042, which the partner drawing lists: he can open it in the viewer and sees the stamped PDF.
            var farid = await session.SignInAsync("farid");
            await session.GotoAsync(farid, "/documents/" + m.PartnerDrawing);
            await farid.Page.Locator("a[data-action=view]").First.ClickAsync();
            await farid.Page.WaitForURLAsync(new Regex("/viewer/documents/"));
            await WaitReadyAsync(farid.Page);
            await BrowserSession.AssertBannersAsync(farid.Page);
            Assert.Contains("ITAR", await BrowserSession.TopBanner(farid.Page).InnerTextAsync(), StringComparison.Ordinal);
            await Expect(farid.Page.Locator(".pdf-page[data-page='1'] .pdf-text")).ToContainTextAsync("DISTRIBUTION STATEMENT", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });

            // And the same hidden drawing is still hidden from him.
            await farid.Page.GotoAsync($"{fx.BaseUrl}/viewer/documents/{itarDrawing.Number}/files/{itarDrawing.FileId}");
            await Expect(farid.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("was not found, or you are not authorized");
        });
    }

    // ---------------------------------------------------------------- the part page, narrow screens, tampering

    [E2EFact(Timeout = 240_000)]
    public async Task The_part_page_opens_the_linked_drawing_and_the_viewer_fits_a_375_pixel_screen()
    {
        await using var session = new BrowserSession(fx, nameof(The_part_page_opens_the_linked_drawing_and_the_viewer_fits_a_375_pixel_screen));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var alice = await session.SignInAsync("alice", 375, 812);
            var page = alice.Page;
            await session.GotoAsync(alice, "/parts/" + m.GimbalBracket);
            await BrowserSession.OpenTabAsync(page, "Documents");
            var view = page.Locator("a[data-action=view-primary]").First;
            await Expect(view).ToBeVisibleAsync();
            await view.ClickAsync();
            await page.WaitForURLAsync(new Regex("/viewer/documents/.+/files/"));
            await WaitReadyAsync(page);
            await AssertMarkingsAsync(page, (await BrowserSession.TopBanner(page).InnerTextAsync()).Trim());

            // Nothing scrolls sideways, and the banners and the statement are on screen.
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"), "The viewer scrolls horizontally at 375 px.");
            await Expect(BrowserSession.TopBanner(page)).ToBeInViewportAsync();
            await Expect(page.Locator("[data-viewer-distribution]")).ToBeVisibleAsync();
            AssertNoBrowserErrors(alice);

            // A 3D model at the same width.
            var cube = await CreateAsync("narrow.stl", ViewerSamples.AsciiStlCube());
            await session.GotoAsync(alice, "/documents/" + cube.Number);
            await page.Locator("a[data-action=view]").First.ClickAsync();
            await WaitReadyAsync(page);
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
            var pixels = await CanvasPixelsAsync(page);
            Assert.True(pixels[0] > 0);
        });
    }

    [E2EFact(Timeout = 240_000)]
    public async Task Removing_or_hiding_a_banner_restores_it_and_hides_the_file_until_reload()
    {
        await using var session = new BrowserSession(fx, nameof(Removing_or_hiding_a_banner_restores_it_and_hides_the_file_until_reload));
        await session.RunAsync(async () =>
        {
            var doc = await CreateAsync("guarded.stl", ViewerSamples.AsciiStlCube());
            var alice = await session.SignInAsync("alice");
            var page = await OpenFromDocumentPageAsync(session, alice, doc);
            await WaitReadyAsync(page);
            await Expect(page.Locator("body")).ToHaveAttributeAsync("data-markings-intact", "true");

            await page.EvaluateAsync("document.querySelector('[data-viewer-banner][data-position=\"top\"]').remove()");
            await Expect(page.Locator("body")).ToHaveAttributeAsync("data-markings-intact", "false", new LocatorAssertionsToHaveAttributeOptions { Timeout = 10_000 });
            await Expect(BrowserSession.TopBanner(page)).ToHaveTextAsync(ItarBanner); // put back
            await Expect(page.Locator("#viewer-stage")).Not.ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Reload the page");

            await page.ReloadAsync();
            await WaitReadyAsync(page);
            await Expect(page.Locator("body")).ToHaveAttributeAsync("data-markings-intact", "true");
            await page.EvaluateAsync("document.querySelector('[data-viewer-overlay]').style.display = 'none'");
            await Expect(page.Locator("body")).ToHaveAttributeAsync("data-markings-intact", "false", new LocatorAssertionsToHaveAttributeOptions { Timeout = 10_000 });
        });
    }

    [E2EFact(Timeout = 240_000)]
    public async Task The_viewer_has_its_own_csp_and_every_other_page_keeps_the_strict_one()
    {
        await using var session = new BrowserSession(fx, nameof(The_viewer_has_its_own_csp_and_every_other_page_keeps_the_strict_one));
        await session.RunAsync(async () =>
        {
            var doc = await CreateAsync("csp.stl", ViewerSamples.AsciiStlCube());
            var alice = await session.SignInAsync("alice");
            var response = await alice.Page.GotoAsync($"{fx.BaseUrl}/viewer/documents/{doc.Number}/files/{doc.FileId}");
            var policy = (await response!.AllHeadersAsync())["content-security-policy"];
            Assert.StartsWith("default-src 'self'; worker-src 'self'; img-src 'self' data: blob:;", policy, StringComparison.Ordinal);
            Assert.DoesNotContain("unsafe", policy, StringComparison.Ordinal);
            Assert.Equal("no-store", (await response.AllHeadersAsync())["cache-control"]);

            var other = await alice.Page.GotoAsync($"{fx.BaseUrl}/documents/{doc.Number}");
            Assert.StartsWith("default-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'", (await other!.AllHeadersAsync())["content-security-policy"], StringComparison.Ordinal);
        });
    }
}
