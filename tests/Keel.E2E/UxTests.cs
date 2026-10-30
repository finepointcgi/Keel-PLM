using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>UX-1 through the real browser: Home per role, the command palette, themes, tabs and stepper, toasts, confirm dialogs, sortable tables, phone width and the keyboard.</summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class UxTests(E2EFixture fx)
{
    [E2EFact]
    public async Task Home_shows_the_dashboard_cards_and_only_the_quick_actions_a_role_allows()
    {
        await using var session = new BrowserSession(fx, nameof(Home_shows_the_dashboard_cards_and_only_the_quick_actions_a_role_allows));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            await session.GotoAsync(alice, "/");
            var page = alice.Page;
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Home", Level = 1 })).ToBeVisibleAsync();
            foreach (var card in new[] { "Needs my attention", "Recently viewed", "Favorites", "Changes in my programs", "Recently released", "Quick actions" })
                await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = card, Exact = true })).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Create part", Exact = true })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);

            // Opening an object makes it appear under "Recently viewed".
            await session.GotoAsync(alice, "/parts/" + fx.Manifest.GimbalBracket);
            await session.GotoAsync(alice, "/");
            await Expect(page.Locator("section[aria-labelledby='card-recent'] ul.items li").First).ToBeVisibleAsync(); // the debounce keeps an object viewed a moment ago where it was, so no fixed number is asserted
            await Expect(page.Locator("section[aria-labelledby='card-recent'] .chip").First).ToBeVisibleAsync();

            // A viewer has no create shortcuts.
            var viewer = await session.SignInAsync("lena");
            await session.GotoAsync(viewer, "/");
            await Expect(viewer.Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Quick actions" })).ToBeVisibleAsync();
            await Expect(viewer.Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Create part", Exact = true })).ToHaveCountAsync(0);
            await BrowserSession.AssertBannersAsync(viewer.Page);
        });
    }

    [E2EFact]
    public async Task The_palette_opens_with_the_keyboard_searches_navigates_and_never_shows_what_lena_may_not_read()
    {
        await using var session = new BrowserSession(fx, nameof(The_palette_opens_with_the_keyboard_searches_navigates_and_never_shows_what_lena_may_not_read));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var alice = await session.SignInAsync("alice");
            await session.GotoAsync(alice, "/parts");
            var page = alice.Page;
            var dialog = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Command palette" });
            // The shortcut is registered after the circuit's first render, so a key pressed the instant the page appears can be lost: press until it opens.
            for (var attempt = 0; attempt < 10 && await dialog.CountAsync() == 0; attempt++)
            {
                await page.Keyboard.PressAsync("Control+k");
                await page.WaitForTimeoutAsync(300);
            }
            await Expect(dialog).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Search or run a command" })).ToBeFocusedAsync();
            await page.Keyboard.TypeAsync(m.GimbalBracket);
            await Expect(dialog.Locator("li.group-label", new LocatorLocatorOptions { HasText = "Recent" })).ToHaveCountAsync(0); // the typed search replaced the recent list
            await Expect(dialog.Locator("li.group-label", new LocatorLocatorOptions { HasText = "Parts" })).ToBeVisibleAsync();
            await Expect(dialog.Locator("[role=option][data-kind=part]").First).ToContainTextAsync(m.GimbalBracket);
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForURLAsync(url => url.EndsWith("/parts/" + m.GimbalBracket, StringComparison.Ordinal));
            await Expect(dialog).ToHaveCountAsync(0);

            // "/" opens it too when no field has the focus, Escape closes it and the focus returns.
            await page.Locator("main").PressAsync("/");
            await Expect(dialog).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(dialog).ToHaveCountAsync(0);

            // Lena cannot read the ITAR bracket: the palette finds nothing, exactly as for a number that does not exist.
            var lena = await session.SignInAsync("lena");
            await session.GotoAsync(lena, "/");
            var lenaDialog = lena.Page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Command palette" });
            for (var attempt = 0; attempt < 5 && await lenaDialog.CountAsync() == 0; attempt++)
            {
                await lena.Page.Keyboard.PressAsync("Control+k"); // the shortcut is registered once the circuit is live: retry until it answers
                await Task.Delay(500);
            }
            await Expect(lenaDialog).ToBeVisibleAsync();
            await Expect(lena.Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Search or run a command" })).ToBeFocusedAsync();
            await lena.Page.Keyboard.TypeAsync(m.GimbalBracket);
            await Expect(lenaDialog.GetByRole(AriaRole.Option, new LocatorGetByRoleOptions { Name = "See all results" })).ToBeVisibleAsync();
            await Task.Delay(800);
            Assert.DoesNotContain(m.GimbalBracket, await lenaDialog.InnerTextAsync() is var text && text.Contains("See all results for", StringComparison.Ordinal) ? text.Replace($"“{m.GimbalBracket}”", "") : text, StringComparison.Ordinal);
            // The same through the results page.
            await session.GotoAsync(lena, "/search?q=" + m.GimbalBracket);
            await Expect(lena.Page.GetByText("0 results you can access")).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(lena.Page);
        });
    }

    [E2EFact]
    public async Task The_theme_is_saved_on_the_server_and_comes_back_in_a_fresh_browser()
    {
        await using var session = new BrowserSession(fx, nameof(The_theme_is_saved_on_the_server_and_comes_back_in_a_fresh_browser));
        await session.RunAsync(async () =>
        {
            var carol = await session.SignInAsync("alice");
            var page = carol.Page;
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Alice" }).ClickAsync();
            await page.Locator("[data-theme-choice=dark]").ClickAsync();
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
            await page.Locator("[data-theme-choice=high-contrast]").ClickAsync();
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "high-contrast");
            await BrowserSession.AssertBannersAsync(page); // the banner stays in every theme
            await page.Locator("[data-density-choice=compact]").ClickAsync();
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-density", "compact");

            // A new browser context has no cookies or storage of the first: the stored preference arrives with the first response.
            var again = await session.SignInAsync("alice");
            await Expect(again.Page.Locator("html")).ToHaveAttributeAsync("data-theme", "high-contrast");
            await Expect(again.Page.Locator("html")).ToHaveAttributeAsync("data-density", "compact");
            await again.Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Alice" }).ClickAsync();
            await again.Page.Locator("[data-theme-choice=system]").ClickAsync();
            await again.Page.Locator("[data-density-choice=comfortable]").ClickAsync();
            await Expect(again.Page.Locator("html")).ToHaveAttributeAsync("data-theme", "system");
        });
    }

    [E2EFact]
    public async Task A_part_page_has_a_stepper_deep_linkable_tabs_a_favorite_toast_and_a_confirmed_undo()
    {
        await using var session = new BrowserSession(fx, nameof(A_part_page_has_a_stepper_deep_linkable_tabs_a_favorite_toast_and_a_confirmed_undo));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/parts/" + m.GimbalBracket);
            await Expect(page).ToHaveTitleAsync(new Regex(Regex.Escape(m.GimbalBracket) + @" · .+ · Keel PLM"));
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToContainTextAsync(m.GimbalBracket);
            await Expect(page.Locator(".page-header .chip")).ToBeVisibleAsync();
            await Expect(page.Locator("ol.stepper li.current")).ToHaveCountAsync(1);
            await Expect(page.Locator("ol.stepper li[data-state=Released]")).ToBeVisibleAsync();

            // Tabs: arrow keys move, the address carries the tab and a reload keeps it.
            await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Overview" }).FocusAsync();
            await page.Keyboard.PressAsync("ArrowRight");
            await Expect(page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Structure" })).ToHaveAttributeAsync("aria-selected", "true");
            await BrowserSession.OpenTabAsync(page, "Documents");
            await Expect(page).ToHaveURLAsync(new Regex("#documents$"));
            await page.ReloadAsync();
            await Expect(page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Documents" })).ToHaveAttributeAsync("aria-selected", "true");

            // A toast confirms the favorite without taking the focus.
            var star = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add " + m.GimbalBracket + " to favorites" });
            await star.ClickAsync();
            await Expect(page.Locator("[data-toast-region=polite] [data-toast=success]")).ToContainTextAsync("added to your favorites");
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Remove " + m.GimbalBracket + " from favorites" })).ToBeFocusedAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Remove " + m.GimbalBracket + " from favorites" }).ClickAsync();

            // Undo check-out asks first; Cancel keeps the working copy, the confirmation discards it.
            await session.GotoAsync(alice, "/parts/new");
            await BrowserSession.FillAsync(page.GetByLabel("Name *"), "E2E undo part");
            await BrowserSession.ClickAsync(page, "Create part");
            await page.WaitForURLAsync(new Regex(@"/parts/P-\d+$"));
            await BrowserSession.OpenMoreAsync(page);
            await BrowserSession.ClickAsync(page, "Check out");
            await Expect(page.Locator("[data-toast=success]")).ToContainTextAsync("Checked out");
            await BrowserSession.OpenMoreAsync(page);
            await BrowserSession.ClickAsync(page, "Undo check-out");
            var dialog = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Undo check-out?" });
            await Expect(dialog).ToBeVisibleAsync();
            await Expect(dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cancel" })).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(dialog).ToHaveCountAsync(0);
            await Expect(page.GetByText("You are editing this part")).ToBeVisibleAsync();
            await BrowserSession.OpenMoreAsync(page);
            await BrowserSession.ClickAsync(page, "Undo check-out");
            await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Undo check-out" }).ClickAsync();
            await Expect(page.Locator("[data-toast=success]").Last).ToContainTextAsync("Check-out undone");
            await Expect(page.GetByText("You are editing this part")).ToHaveCountAsync(0);
            await BrowserSession.AssertBannersAsync(page);
        });
    }

    [E2EFact]
    public async Task A_list_sorts_by_header_and_the_column_chooser_is_remembered()
    {
        await using var session = new BrowserSession(fx, nameof(A_list_sorts_by_header_and_the_column_chooser_is_remembered));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/parts");
            var nameHeader = page.GetByRole(AriaRole.Columnheader, new PageGetByRoleOptions { Name = "Name" });
            await Expect(nameHeader).ToHaveAttributeAsync("aria-sort", "none");
            await nameHeader.GetByRole(AriaRole.Button).ClickAsync();
            await Expect(nameHeader).ToHaveAttributeAsync("aria-sort", "ascending");
            var names = await page.Locator("table.data tbody tr td:nth-child(2)").AllInnerTextsAsync();
            Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), names.ToList());
            await nameHeader.GetByRole(AriaRole.Button).ClickAsync();
            await Expect(nameHeader).ToHaveAttributeAsync("aria-sort", "descending");

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Columns" }).ClickAsync();
            await page.GetByLabel("Type", new PageGetByLabelOptions { Exact = true }).UncheckAsync();
            await Expect(page.GetByRole(AriaRole.Columnheader, new PageGetByRoleOptions { Name = "Type" })).ToHaveCountAsync(0);
            await page.ReloadAsync();
            await Expect(page.GetByRole(AriaRole.Columnheader, new PageGetByRoleOptions { Name = "Name" })).ToHaveAttributeAsync("aria-sort", "descending"); // saved with the user
            await Expect(page.GetByRole(AriaRole.Columnheader, new PageGetByRoleOptions { Name = "Type" })).ToHaveCountAsync(0);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Columns" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Reset columns" }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Columnheader, new PageGetByRoleOptions { Name = "Type" })).ToBeVisibleAsync();
        });
    }

    [E2EFact]
    public async Task Home_parts_a_part_page_and_the_palette_fit_a_phone_without_sideways_scrolling()
    {
        await using var session = new BrowserSession(fx, nameof(Home_parts_a_part_page_and_the_palette_fit_a_phone_without_sideways_scrolling));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var alice = await session.SignInAsync("alice", width: 375, height: 812);
            var page = alice.Page;
            foreach (var path in new[] { "/", "/parts", "/parts/" + m.GimbalBracket })
            {
                await session.GotoAsync(alice, path);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await BrowserSession.AssertBannersAsync(page);
                Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"), $"{path} scrolls sideways at 375 px");
            }
            // The rail is a drawer behind the menu button.
            await Expect(page.Locator("nav#main-nav")).ToBeHiddenAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Menu" }).ClickAsync();
            await Expect(page.Locator("nav#main-nav")).ToBeVisibleAsync();
            await page.Locator("nav#main-nav").GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Documents", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(url => url.EndsWith("/documents", StringComparison.Ordinal));
            await Expect(page.Locator("nav#main-nav")).ToBeHiddenAsync();

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Search or jump to" }).ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Command palette" });
            await Expect(dialog).ToBeVisibleAsync();
            var box = await dialog.BoundingBoxAsync();
            Assert.InRange(box!.Width, 200, 375);
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"), "the palette scrolls sideways at 375 px");
        });
    }

    [E2EFact]
    public async Task The_shell_is_usable_by_keyboard_alone()
    {
        await using var session = new BrowserSession(fx, nameof(The_shell_is_usable_by_keyboard_alone));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/parts");
            await page.EvaluateAsync("() => { document.body.setAttribute('tabindex', '-1'); document.body.focus(); document.body.removeAttribute('tabindex'); }");
            await page.Keyboard.PressAsync("Tab");
            // Skip link first: it moves the focus to the main landmark.
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Skip to main content" })).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(page.Locator("main#main")).ToBeFocusedAsync();

            // The user menu opens with Enter, switches a choice with Space and closes with Escape, returning the focus.
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Alice" }).FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(page.Locator("#user-menu-panel")).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("#user-menu-panel")).ToHaveCountAsync(0);
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Alice" })).ToBeFocusedAsync();

            // The palette: open, navigate with arrows, run a command, Lock now stays reachable by keyboard.
            await page.Keyboard.PressAsync("Control+k");
            await Expect(page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Search or run a command" })).ToBeFocusedAsync();
            await page.Keyboard.TypeAsync("go to doc");
            await Expect(page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = "Go to Documents" })).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForURLAsync(url => url.EndsWith("/documents", StringComparison.Ordinal));
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Documents", Level = 1 })).ToBeFocusedAsync(); // focus follows navigation
            await page.Locator("[data-action=lock-now]").FocusAsync();
            await Expect(page.Locator("[data-action=lock-now]")).ToBeFocusedAsync();
        });
    }

    /// <summary>Writes the documentation screenshots (docs/ux/screenshots) from the synthetic demo data. Runs only with KEEL_SCREENSHOTS=1.</summary>
    [E2EFact]
    public async Task Documentation_screenshots_are_written_on_request()
    {
        if (Environment.GetEnvironmentVariable("KEEL_SCREENSHOTS") != "1") return;
        await using var session = new BrowserSession(fx, nameof(Documentation_screenshots_are_written_on_request));
        await session.RunAsync(async () =>
        {
            var dir = Path.Combine(fx.RepoRoot, "docs", "ux", "screenshots");
            Directory.CreateDirectory(dir);
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            async Task Shot(string name) => await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(dir, name + ".png") });
            await session.GotoAsync(alice, "/parts/" + fx.Manifest.GimbalBracket);
            await session.GotoAsync(alice, "/");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Shot("home-light");
            await page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Dark });
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Alice" }).ClickAsync();
            await page.Locator("[data-theme-choice=dark]").ClickAsync();
            await page.Keyboard.PressAsync("Escape");
            await Shot("home-dark");
            await session.GotoAsync(alice, "/parts/" + fx.Manifest.GimbalBracket);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Shot("part-dark");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Alice" }).ClickAsync();
            await page.Locator("[data-theme-choice=high-contrast]").ClickAsync();
            await page.Keyboard.PressAsync("Escape");
            await Shot("part-high-contrast");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Alice" }).ClickAsync();
            await page.Locator("[data-theme-choice=light]").ClickAsync();
            await page.Keyboard.PressAsync("Escape");
            await page.Keyboard.PressAsync("Control+k");
            await Expect(page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Search or run a command" })).ToBeFocusedAsync();
            await page.Keyboard.TypeAsync("bracket");
            await Task.Delay(1200);
            await Shot("palette-light");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Alice" }).ClickAsync().ContinueWith(_ => { });
            var phone = await session.SignInAsync("alice", width: 375, height: 812);
            await phone.Page.GotoAsync(fx.BaseUrl + "/parts/" + fx.Manifest.GimbalBracket);
            await Expect(phone.Page.Locator("h1")).ToBeVisibleAsync();
            await phone.Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await phone.Page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(dir, "part-phone.png") });
        });
    }
}
