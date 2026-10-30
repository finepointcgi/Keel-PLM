using Microsoft.Playwright;
using UglyToad.PdfPig;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>Marked downloads, an accessibility smoke test (console, keyboard) and the phone-width layout.</summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class QualityTests(E2EFixture fx)
{
    /// <summary>
    /// The one console error the product knowingly produces: Blazor's Virtualize writes inline styles that the content security policy refuses
    /// (docs/increments/1.4-bom.md; the BOM grid ships a script shim and the policy is deliberately not relaxed). Anything else fails the test.
    /// </summary>
    private static bool IsKnownNotice(string message) =>
        (message.Contains("Refused to apply inline style", StringComparison.Ordinal) || message.Contains("Applying inline style violates", StringComparison.Ordinal))
        && message.Contains("Content Security Policy", StringComparison.Ordinal);

    [E2EFact]
    public async Task A_PDF_downloaded_from_the_vault_carries_its_marking_text()
    {
        await using var session = new BrowserSession(fx, nameof(A_PDF_downloaded_from_the_vault_carries_its_marking_text));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            await session.GotoAsync(alice, "/documents/" + fx.Manifest.BracketDrawing);
            var link = alice.Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Download (marked)" });
            var download = await alice.Page.RunAndWaitForDownloadAsync(() => link.ClickAsync());
            Assert.EndsWith(".pdf", download.SuggestedFilename, StringComparison.OrdinalIgnoreCase);
            var path = Path.Combine(Path.GetTempPath(), $"e2e-download-{Guid.NewGuid():N}.pdf");
            await download.SaveAsAsync(path);
            try
            {
                using var pdf = PdfDocument.Open(path);
                Assert.True(pdf.NumberOfPages >= 1);
                var text = string.Join(' ', pdf.GetPages().Select(p => p.Text));
                // The product's stamp (banner on every page) plus the original synthetic content survive.
                Assert.Contains("ITAR USML XI(d)", text, StringComparison.Ordinal);
                Assert.Contains("DISTRIBUTION D", text, StringComparison.Ordinal);
                Assert.Contains("PROGRAM ORCA", text, StringComparison.Ordinal);
                Assert.Contains("CUI", text, StringComparison.Ordinal);
                Assert.Contains("Gimbal bracket drawing", text, StringComparison.Ordinal);
            }
            finally { File.Delete(path); }
        });
    }

    [E2EFact]
    public async Task Pages_load_without_console_errors_and_the_keyboard_reaches_navigation_and_primary_buttons()
    {
        await using var session = new BrowserSession(fx, nameof(Pages_load_without_console_errors_and_the_keyboard_reaches_navigation_and_primary_buttons));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            foreach (var path in new[]
            {
                "/", "/parts", "/parts/" + m.TopAssembly, "/parts/" + m.GimbalBracket, "/parts/new", "/parts/" + m.GimbalBracket + "/where-used",
                "/documents", "/documents/" + m.SystemSpecification, "/documents/new", "/changes", "/changes/" + m.EcnDraftInWork, "/changes/" + m.EcrAwaitingCcb,
                "/tasks", "/profile", "/access",
            })
            {
                await session.GotoAsync(alice, path);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await BrowserSession.AssertBannersAsync(page);
            }
            // The BOM tree really rendered (expand all), and the impact panel ran, so the heaviest components are covered.
            await session.GotoAsync(alice, "/parts/" + m.TopAssembly);
            await BrowserSession.OpenTabAsync(page, "Structure");
            await BrowserSession.ClickAsync(page, "Expand all");
            await Expect(page.Locator(".bom-grid .bom-row").Nth(5)).ToBeVisibleAsync();
            await session.GotoAsync(alice, "/changes/" + m.EcnDraftInWork);
            await BrowserSession.OpenTabAsync(page, "Impact analysis");
            await BrowserSession.ClickAsync(page, "Run impact analysis");
            await Expect(page.Locator("section[aria-labelledby='impact-heading']")).ToContainTextAsync("place");

            var audrey = await session.SignInAsync("audrey");
            await session.GotoAsync(audrey, "/audit");
            await Expect(audrey.Page.Locator("table.data tbody tr").First).ToBeVisibleAsync();

            var unexpected = alice.ConsoleErrors.Concat(audrey.ConsoleErrors).Where(e => !IsKnownNotice(e)).ToList();
            Assert.True(unexpected.Count == 0, "Console errors: " + string.Join(" | ", unexpected));
            Assert.True(alice.PageErrors.Count == 0 && audrey.PageErrors.Count == 0, "Page errors: " + string.Join(" | ", alice.PageErrors.Concat(audrey.PageErrors)));

            // ---- keyboard: Tab from the top of a page reaches every main navigation link, then the page's primary button
            await session.GotoAsync(alice, "/parts/new");
            // Start from the top of the page: Blazor moves focus to the heading on navigation, which is past the navigation bar.
            await page.EvaluateAsync("() => { const b = document.querySelector('.brand'); b.setAttribute('tabindex', '-1'); b.focus(); }");
            var reached = new List<string>();
            // The rail groups are disclosure buttons: a closed group holds no links, so the keyboard user opens it with Enter on its header and Tab then continues into it.
            for (var i = 0; i < 160 && !reached.Contains("Create part"); i++)
            {
                await page.Keyboard.PressAsync("Tab");
                var focus = await page.EvaluateAsync<string>("() => { const e = document.activeElement; return e ? ((e.closest('nav') ? 'nav:' : '') + (e.innerText || e.getAttribute('aria-label') || e.tagName).trim()) : ''; }");
                reached.Add(focus);
                if (await page.EvaluateAsync<bool>("() => document.activeElement?.matches('.rail-group-header[aria-expanded=false]') === true"))
                {
                    await page.Keyboard.PressAsync("Enter");
                    await Expect(page.Locator(".rail-group-header:focus")).ToHaveAttributeAsync("aria-expanded", "true");
                }
            }
            foreach (var link in new[] { "Home", "Parts", "Documents", "Changes", "My tasks", "Access check" })
                Assert.Contains("nav:" + link, reached);
            Assert.Contains("Create part", reached);
            // Enter on the focused primary button submits (the empty form is refused by the browser, which is the point: it is operable).
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create part", Exact = true })).ToBeFocusedAsync();
        });
    }

    [E2EFact]
    public async Task The_parts_list_and_a_part_page_do_not_scroll_sideways_at_phone_width()
    {
        await using var session = new BrowserSession(fx, nameof(The_parts_list_and_a_part_page_do_not_scroll_sideways_at_phone_width));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var alice = await session.SignInAsync("alice", width: 375, height: 812);
            foreach (var path in new[] { "/parts", "/parts/" + m.GimbalBracket, "/parts/" + m.TopAssembly, "/changes/" + m.EcnDraftInWork })
            {
                await session.GotoAsync(alice, path);
                await alice.Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                if (path.StartsWith("/parts/P", StringComparison.Ordinal) && path.EndsWith(m.TopAssembly, StringComparison.Ordinal))
                {
                    await BrowserSession.OpenTabAsync(alice.Page, "Structure");
                    await Expect(alice.Page.GetByRole(AriaRole.Treegrid)).ToBeVisibleAsync();
                }
                var (scrollWidth, bodyWidth, innerWidth) = await alice.Page.EvaluateAsync<int[]>(
                    "() => [document.documentElement.scrollWidth, document.body.scrollWidth, window.innerWidth]") is [var a, var b, var c] ? (a, b, c) : default;
                var wide = await alice.Page.EvaluateAsync<string[]>(
                    @"() => [...document.querySelectorAll('body *')].filter(e => { if (e.getBoundingClientRect().right <= window.innerWidth + 1) return false;
                        for (let p = e.parentElement; p && p !== document.body; p = p.parentElement) { const o = getComputedStyle(p).overflowX; if (o === 'auto' || o === 'scroll' || o === 'hidden') return false; }
                        return true; }).slice(0, 6).map(e => e.tagName + '.' + e.className + ' ' + Math.round(e.getBoundingClientRect().right))");
                Assert.True(scrollWidth <= innerWidth && bodyWidth <= innerWidth,
                    $"{path}: the page scrolls sideways at 375 px (document {scrollWidth}, body {bodyWidth}, window {innerWidth}). Overflowing: {string.Join(", ", wide)}");
                await BrowserSession.AssertBannersAsync(alice.Page);
            }
        });
    }
}
