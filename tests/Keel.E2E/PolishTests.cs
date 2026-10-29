using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// Visual polish (increment 9.1, ADR-0046): the living style guide renders every section, in every theme, with the marking banners in place; and the
/// before/after documentation screenshots (docs/ux/screenshots/polish-*.png) are written on request (KEEL_SCREENSHOTS=1, KEEL_SHOT_PREFIX=before|after).
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class PolishTests(E2EFixture fx)
{
    private static async Task ChooseThemeAsync(IPage page, string theme)
    {
        await page.Locator("#user-menu-button").ClickAsync();
        await page.Locator($"[data-theme-choice={theme}]").ClickAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", theme);
        await page.WaitForTimeoutAsync(1500);
    }

    [E2EFact]
    public async Task The_style_guide_shows_every_section_with_banners_in_every_theme()
    {
        await using var session = new BrowserSession(fx, nameof(The_style_guide_shows_every_section_with_banners_in_every_theme));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/tools/style-guide");
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Style guide", Level = 1 })).ToBeVisibleAsync();
            foreach (var id in new[] { "sg-colour", "sg-type", "sg-buttons", "sg-chips", "sg-table", "sg-forms", "sg-banners", "sg-states" })
                await Expect(page.Locator("#" + id)).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
            var original = await page.EvaluateAsync<string>("document.documentElement.getAttribute('data-theme')");
            try
            {
                foreach (var theme in new[] { "dark", "high-contrast", "light" })
                {
                    await ChooseThemeAsync(page, theme);
                    await Expect(page.Locator("#sg-buttons")).ToBeVisibleAsync();
                    await BrowserSession.AssertBannersAsync(page);
                }
            }
            finally { await ChooseThemeAsync(page, original); }
        });
    }

    /// <summary>Marking chips wrap only between segments: no segment is wider than its chip (so no word is split or clipped) at 800, 1280 and 375 px, and the page never scrolls sideways.</summary>
    [E2EFact(Timeout = 600_000)]
    public async Task Marking_chips_never_split_a_word_or_overflow_at_three_widths()
    {
        await using var session = new BrowserSession(fx, nameof(Marking_chips_never_split_a_word_or_overflow_at_three_widths));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice", width: 1280, height: 800);
            var page = alice.Page;
            var problems = new List<string>();
            foreach (var width in new[] { 800, 1280, 375 })
            {
                await page.SetViewportSizeAsync(width, 800);
                foreach (var path in new[] { "/parts", "/documents", "/changes", "/baselines", "/mboms", "/" })
                {
                    await session.GotoAsync(alice, path);
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    await page.WaitForTimeoutAsync(500);
                    var bad = await page.EvaluateAsync<string[]>(@"() => {
                        const out = [];
                        for (const chip of document.querySelectorAll('.chip')) {
                            const c = chip.getBoundingClientRect();
                            for (const seg of chip.querySelectorAll('.seg')) {
                                const r = seg.getBoundingClientRect();
                                if (r.right > c.right + 1 || r.width > c.width) out.push(seg.textContent);
                            }
                        }
                        if (document.documentElement.scrollWidth > window.innerWidth + 1) out.push('PAGE SCROLLS SIDEWAYS');
                        return out;
                    }");
                    foreach (var b in bad) problems.Add($"{width}px {path}: {b}");
                }
                await page.Locator("[data-action=open-palette]").ClickAsync();
                await page.Keyboard.TypeAsync("bracket");
                await page.WaitForTimeoutAsync(1200);
                var palette = await page.EvaluateAsync<string[]>("() => [...document.querySelectorAll('.palette-item .chip .seg')].filter(s => s.getBoundingClientRect().right > s.closest('.chip').getBoundingClientRect().right + 1).map(s => s.textContent)");
                foreach (var b in palette) problems.Add($"{width}px palette: {b}");
                await page.Keyboard.PressAsync("Escape");
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        });
    }

    [E2EFact(Timeout = 600_000)]
    public async Task Polish_screenshots_are_written_on_request()
    {
        if (Environment.GetEnvironmentVariable("KEEL_SCREENSHOTS") != "1") return;
        var prefix = Environment.GetEnvironmentVariable("KEEL_SHOT_PREFIX") ?? "after";
        await using var session = new BrowserSession(fx, nameof(Polish_screenshots_are_written_on_request));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var dir = Path.Combine(fx.RepoRoot, "docs", "ux", "screenshots");
            Directory.CreateDirectory(dir);
            var alice = await session.SignInAsync("alice", width: 1280, height: 800);
            var page = alice.Page;
            string current = "light";
            async Task Shot(string name) { if (await page.EvaluateAsync<string>("document.documentElement.getAttribute('data-theme')") != current) await ChooseThemeAsync(page, current); await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(dir, $"polish-{prefix}-{name}.png") }); }
            async Task Open(string path) { await session.GotoAsync(alice, path); await page.WaitForLoadStateAsync(LoadState.NetworkIdle); await page.WaitForTimeoutAsync(600); }

            foreach (var theme in new[] { "light", "dark" })
            {
                current = theme;
                await Open("/");
                await Shot($"home-{theme}");
                await Open("/parts");
                await Shot($"parts-{theme}");
                await Open("/parts/" + m.GimbalBracket);
                await Shot($"part-{theme}");
                if (theme == "light")
                {
                    await Open("/changes/" + m.EcnUnderReview + "?tab=review");
                    await Shot("change-light");
                    if (prefix == "after")
                    {
                        await Open("/tools/style-guide");
                        await Shot("style-guide-light");
                    }
                }
            }
            current = "light";
            await Open("/");
            await ChooseThemeAsync(page, "light");
            var phone = await session.SignInAsync("alice", width: 375, height: 812);
            await phone.Page.GotoAsync(fx.BaseUrl + "/parts/" + m.GimbalBracket);
            await Expect(phone.Page.Locator("h1")).ToBeVisibleAsync();
            await phone.Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await phone.Page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(dir, $"polish-{prefix}-part-phone.png") });
        });
    }
}
