using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// ADR 0057 through the real browser: the language picker in the user menu, the stored per-user language surviving a reload and a new sign-in, Spanish through the
/// converted flows, the pseudo-locale, marking banners that never change, and an axe audit of the converted pages in both (at desktop and phone width, where longer
/// text breaks layouts first). Every test puts the language back to English: the preference is stored for the user.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class LocalizationE2ETests(E2EFixture fx)
{
    private static async Task ChooseLanguageAsync(IPage page, string tag)
    {
        await page.GotoAsync(page.Url.Split('/', 4).Take(3).Aggregate((a, b) => a + "/" + b) + "/");
        await Expect(page.Locator("h1").First).ToBeVisibleAsync();
        await page.Locator("#user-menu-button").ClickAsync();
        await page.Locator($"[data-language-choice={tag}]").ClickAsync();
        // The page loads again in the new language (a new circuit): the pressed state moves and the document language follows.
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", tag == "qps-ploc" ? "en" : tag);
        await Expect(page.Locator("h1").First).ToBeVisibleAsync();
    }

    /// <summary>Puts the language back to English even when the page is in a bad state: the preference is stored for the user and every later test signs in as the same people.</summary>
    private async Task RestoreEnglishAsync(IPage page)
    {
        try { await ChooseLanguageAsync(page, "en"); }
        catch (Exception e) when (e is PlaywrightException or TimeoutException or Xunit.Sdk.XunitException) { /* the stored value is reset below */ }
        await using var cmd = fx.AppDb.CreateCommand("UPDATE ws.preference SET language = 'en' WHERE language <> 'en'");
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SettleAsync(IPage page)
    {
        try { await Expect(page.Locator(".skeleton")).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 }); }
        catch (PlaywrightException) { /* reported by axe or the banner check */ }
        await page.WaitForTimeoutAsync(250);
    }

    [E2EFact]
    public async Task Spanish_is_stored_for_the_person_reaches_the_converted_flows_and_never_touches_the_marking_banners()
    {
        await using var session = new BrowserSession(fx, nameof(Spanish_is_stored_for_the_person_reaches_the_converted_flows_and_never_touches_the_marking_banners));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/parts");
            await BrowserSession.AssertBannersAsync(page);
            var englishBanner = (await page.Locator("[data-position=top]").InnerTextAsync()).Trim();
            Assert.Equal("en", await page.Locator("html").GetAttributeAsync("lang"));
            try
            {
                await ChooseLanguageAsync(page, "es");
                await Expect(page.Locator("#main-nav")).ToHaveAttributeAsync("aria-label", "Principal");

                await session.GotoAsync(alice, "/parts");
                await Expect(page.Locator("h1")).ToHaveTextAsync("Piezas");
                await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Crear pieza", Exact = true })).ToBeVisibleAsync();
                await Expect(page.GetByText(new Regex(@"piezas? a las? que tiene acceso"))).ToBeVisibleAsync();
                await BrowserSession.AssertBannersAsync(page);
                Assert.Equal(englishBanner, (await page.Locator("[data-position=top]").InnerTextAsync()).Trim()); // the legal text is exactly what the policy dictates
                Assert.Equal(englishBanner, (await page.Locator("[data-position=bottom]").InnerTextAsync()).Trim());

                // Quick add part, in Spanish, creates the same part as in English (the data and the audit are the same; only the words differ).
                await session.GotoAsync(alice, "/parts/new");
                await BrowserSession.ClickAsync(page, "Crear pieza");
                await Expect(page.GetByText("Escriba un nombre para la pieza.")).ToBeVisibleAsync();
                await BrowserSession.FillAsync(page.GetByLabel("Nombre *"), "E2E pieza en español");
                await BrowserSession.ClickAsync(page, "Crear pieza");
                await page.WaitForURLAsync(new Regex(@"/parts/P-\d+$"), new PageWaitForURLOptions { Timeout = 60_000 });
                await Expect(page.Locator(".badges")).ToContainTextAsync("Versión A.1");
                await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Editar", Exact = true })).ToBeVisibleAsync();
                await BrowserSession.AssertBannersAsync(page);

                await session.GotoAsync(alice, "/documents");
                await Expect(page.Locator("h1")).ToHaveTextAsync("Documentos");
                await session.GotoAsync(alice, "/documents/new");
                await Expect(page.Locator("h1")).ToHaveTextAsync("Nuevo documento");
                await session.GotoAsync(alice, "/tasks");
                await Expect(page.Locator("h1")).ToHaveTextAsync("Mis tareas");
                await session.GotoAsync(alice, "/");
                await Expect(page.Locator("h1")).ToHaveTextAsync("Inicio");
                await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Acciones rápidas" })).ToBeVisibleAsync();

                // The command palette: Spanish placeholder, Spanish commands, and the English name of a command still finds it.
                await page.Locator("[data-action=open-palette]").ClickAsync();
                await Expect(page.Locator("[data-palette-input]")).ToHaveAttributeAsync("placeholder", new Regex("^Busque piezas"));
                await page.Locator("[data-palette-input]").FillAsync("sign out");
                await Expect(page.Locator("[data-kind=action]").First).ToContainTextAsync("Cerrar sesión");
                await page.Keyboard.PressAsync("Escape");

                // The language is the person's, stored on the server: a reload, a new tab and a new sign-in all read it.
                await page.ReloadAsync();
                await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "es");
                await alice.Page.Context.ClearCookiesAsync();
                var again = await session.SignInAsync("alice");
                await session.GotoAsync(again, "/parts");
                await Expect(again.Page.Locator("h1")).ToHaveTextAsync("Piezas");
                page = again.Page;
            }
            finally
            {
                await RestoreEnglishAsync(page);
            }
            await page.GotoAsync(fx.BaseUrl + "/parts");
            await Expect(page.Locator("h1")).ToHaveTextAsync("Parts");
        });
    }

    [E2EFact(Timeout = 900_000)]
    public async Task The_converted_pages_pass_the_audit_in_Spanish_and_in_the_pseudo_locale_at_desktop_and_phone_width()
    {
        await using var session = new BrowserSession(fx, nameof(The_converted_pages_pass_the_audit_in_Spanish_and_in_the_pseudo_locale_at_desktop_and_phone_width));
        await session.RunAsync(async () =>
        {
            var axe = await File.ReadAllTextAsync(AccessibilityTests.AxePath());
            var findings = new List<string>();
            var audited = 0;
            var alice = await session.SignInAsync("alice", 1280, 900);
            var page = alice.Page;
            try
            {
                foreach (var tag in new[] { "es", "qps-ploc" })
                {
                    await ChooseLanguageAsync(page, tag);
                    foreach (var width in new[] { 1280, 375 })
                    {
                        await page.SetViewportSizeAsync(width, width == 375 ? 812 : 900);
                        foreach (var path in new[] { "/", "/parts", "/parts/new", "/documents", "/documents/new", "/tasks" })
                        {
                            await page.GotoAsync(fx.BaseUrl + path);
                            await Expect(page.Locator("h1").First).ToBeVisibleAsync();
                            await SettleAsync(page);
                            await BrowserSession.AssertBannersAsync(page);
                            await AccessibilityTests.RunAxeAsync(page, $"{tag}/{width}px {path}", axe, findings);
                            audited++;
                            // No horizontal scroll of the page: longer text must wrap or scroll inside its own container, never push the page sideways.
                            var overflow = await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1");
                            if (overflow) findings.Add($"  [{tag}/{width}px {path}] the page scrolls sideways");
                        }
                        await page.Locator("#user-menu-button").ClickAsync();
                        await Expect(page.Locator("#user-menu-panel")).ToBeVisibleAsync();
                        await AccessibilityTests.RunAxeAsync(page, $"{tag}/{width}px user menu open", axe, findings);
                        await page.Keyboard.PressAsync("Escape");
                        await page.Keyboard.PressAsync("Control+K");
                        await Expect(page.Locator("[data-palette]")).ToBeVisibleAsync();
                        await AccessibilityTests.RunAxeAsync(page, $"{tag}/{width}px palette open", axe, findings);
                        await page.Keyboard.PressAsync("Escape");
                        audited += 2;
                    }
                }
            }
            finally
            {
                await page.SetViewportSizeAsync(1280, 900);
                await RestoreEnglishAsync(page);
            }
            Assert.True(audited >= 28, $"Only {audited} page states were audited.");
            Assert.True(findings.Count == 0, $"{findings.Count} finding(s) in {audited} page states:\n" + string.Join("\n", findings));
        });
    }

    [E2EFact]
    public async Task The_language_picker_is_keyboard_operable_and_marks_the_chosen_language()
    {
        await using var session = new BrowserSession(fx, nameof(The_language_picker_is_keyboard_operable_and_marks_the_chosen_language));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/");
            await Expect(page.Locator("h1").First).ToBeVisibleAsync();
            await page.Locator("#user-menu-button").FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(page.Locator("#user-menu-panel")).ToBeVisibleAsync();
            await Expect(page.Locator("[data-language-choice=en]")).ToHaveAttributeAsync("aria-pressed", "true");
            await Expect(page.Locator("[data-language-choice=es]")).ToHaveAttributeAsync("lang", "es");
            try
            {
                await page.Locator("[data-language-choice=es]").FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "es");
                await page.Locator("#user-menu-button").ClickAsync();
                await Expect(page.Locator("[data-language-choice=es]")).ToHaveAttributeAsync("aria-pressed", "true");
            }
            finally
            {
                await RestoreEnglishAsync(page);
            }
        });
    }
}
