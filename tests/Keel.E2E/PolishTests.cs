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
