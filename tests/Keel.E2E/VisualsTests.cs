using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// UX-3 through the real browser (ADR-0029): the impact map of ECN-000001, the BOM treemap of P-000001, the structure diff of P-000005 across two units, the timelines of
/// P-000015 and P-000016, the workflow map of ECN-000002, each at 375 px, with the keyboard, in the dark and high-contrast themes, and hidden by the idle lock.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class VisualsTests(E2EFixture fx)
{
    private static string Hex(string css) => css.Trim();

    private static (int R, int G, int B) Rgb(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        return (int.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture), int.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture), int.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>The module has attached when it has fitted the picture (the transform of the content layer is set); keys sent earlier would be lost.</summary>
    private static Task WaitForBehaviourAsync(IPage page) =>
        Expect(page.Locator("[data-vis-content]").First).ToHaveAttributeAsync("transform", new Regex("translate"));

    private static string Scale(string? transform) => Regex.Match(transform ?? "", @"scale\(([\d.]+)\)").Groups[1].Value;

    private static Task<string> ActiveLabel(IPage page) => page.EvaluateAsync<string>("document.activeElement?.getAttribute('aria-label') ?? ''");

    private static async Task AssertNoSidewaysScroll(IPage page) =>
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth + 1"), "The page scrolls sideways.");

    private static async Task ChooseThemeAsync(IPage page, string user, string theme)
    {
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = char.ToUpperInvariant(user[0]) + user[1..] }).ClickAsync();
        await page.Locator($"[data-theme-choice={theme}]").ClickAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", theme);
    }

    [E2EFact]
    public async Task The_impact_map_of_ecn_1_shows_the_part_and_its_parents_moves_with_the_keyboard_and_has_a_table_legend_and_svg_export()
    {
        await using var session = new BrowserSession(fx, nameof(The_impact_map_of_ecn_1_shows_the_part_and_its_parents_moves_with_the_keyboard_and_has_a_table_legend_and_svg_export));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/changes/" + m.EcnDraftInWork);
            await BrowserSession.OpenTabAsync(page, "Impact map");
            var nodes = page.Locator("g.vis-node");
            await Expect(nodes.First).ToBeVisibleAsync();
            await WaitForBehaviourAsync(page);
            await Expect(page.Locator("svg.vis-svg")).ToHaveAttributeAsync("role", "group");

            // The change at the left, the affected part with its revisions, and the assemblies that use it.
            await Expect(nodes.Filter(new LocatorFilterOptions { HasTextString = m.EcnDraftInWork })).ToHaveCountAsync(1);
            var affected = nodes.Filter(new LocatorFilterOptions { HasTextString = m.GimbalBracket });
            await Expect(affected.First).ToContainTextAsync("A → B");
            await Expect(affected.First).ToHaveAttributeAsync("aria-label", new Regex("^Affected part"));
            await Expect(nodes.Filter(new LocatorFilterOptions { HasTextString = m.GimbalAssembly })).ToHaveCountAsync(1);
            await Expect(nodes.Filter(new LocatorFilterOptions { HasTextString = m.TopAssembly })).ToHaveCountAsync(1);
            // The legend names every kind (shape and word), and nothing of the KRAKEN part leaks into the page.
            var legend = page.Locator(".vis-legend");
            await Expect(legend).ToContainTextAsync("Restricted item");
            await Expect(legend).ToContainTextAsync("Affected part");
            await Expect(legend.Locator("svg")).Not.ToHaveCountAsync(0);
            Assert.DoesNotContain(m.KrakenPart, await page.ContentAsync(), StringComparison.Ordinal);
            await BrowserSession.AssertBannersAsync(page);

            // Keyboard: focus the map, arrow keys follow the connection, Enter opens in this tab, Ctrl+Enter in a new tab.
            await page.Locator("g.vis-node[tabindex='0']").FocusAsync();
            Assert.Contains(m.EcnDraftInWork, await ActiveLabel(page), StringComparison.Ordinal);
            await page.Keyboard.PressAsync("ArrowRight");
            Assert.StartsWith("Affected part", await ActiveLabel(page), StringComparison.Ordinal);
            Assert.Contains(m.GimbalBracket, await ActiveLabel(page), StringComparison.Ordinal);
            await Expect(page.Locator(".vis-tip")).ToBeVisibleAsync(); // the tooltip follows focus as well as the pointer
            await page.Keyboard.PressAsync("ArrowRight");
            Assert.Contains("Assembly", await ActiveLabel(page), StringComparison.Ordinal);
            await page.Keyboard.PressAsync("ArrowLeft");
            Assert.StartsWith("Affected part", await ActiveLabel(page), StringComparison.Ordinal);
            await page.Keyboard.PressAsync("Space");
            await Expect(page.Locator(".vis-side")).ToContainTextAsync(m.GimbalBracket);
            // Zoom and fit with the keyboard change the transform of the content layer.
            var before = await page.Locator("[data-vis-content]").GetAttributeAsync("transform");
            await page.Keyboard.PressAsync("+");
            Assert.NotEqual(before, await page.Locator("[data-vis-content]").GetAttributeAsync("transform"));
            await page.Keyboard.PressAsync("0");
            Assert.Equal(Scale(before), Scale(await page.Locator("[data-vis-content]").GetAttributeAsync("transform"))); // fitted again (the offset can differ when the side panel changed the width)
            var popup = await page.Context.RunAndWaitForPageAsync(() => page.Keyboard.PressAsync("Control+Enter"));
            await popup.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            Assert.Contains("/parts/" + m.GimbalBracket, popup.Url, StringComparison.Ordinal);
            await popup.CloseAsync();
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForURLAsync(url => url.Contains("/parts/" + m.GimbalBracket, StringComparison.Ordinal));

            // Back on the map: the table shows the same items, with links.
            await session.GotoAsync(alice, "/changes/" + m.EcnDraftInWork + "#impactmap");
            await Expect(page.Locator("g.vis-node").First).ToBeVisibleAsync();
            var count = await page.Locator("g.vis-node").CountAsync();
            await page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Impact map view" }).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Table" }).ClickAsync();
            await Expect(page.Locator("svg.vis-svg")).ToHaveCountAsync(0);
            var rows = page.Locator("table[aria-label='Impact map as a table'] tbody tr");
            await Expect(rows).ToHaveCountAsync(count);
            await Expect(rows.Filter(new LocatorFilterOptions { HasTextString = m.GimbalBracket }).Locator("a").First).ToHaveAttributeAsync("href", new Regex("parts/" + m.GimbalBracket));

            // The SVG export is a same-origin download with the banner and the distribution statement burned in.
            var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Export SVG" }).ClickAsync());
            Assert.EndsWith("_impact-map.svg", download.SuggestedFilename, StringComparison.Ordinal);
            var svg = await File.ReadAllTextAsync(await download.PathAsync() ?? throw new InvalidOperationException("no download"));
            var banner = (await BrowserSession.TopBanner(page).InnerTextAsync()).Trim();
            Assert.Contains("DISTRIBUTION STATEMENT", svg, StringComparison.Ordinal);
            Assert.Contains(m.GimbalBracket, svg, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", svg, StringComparison.Ordinal);
            Assert.DoesNotContain(m.KrakenPart, svg, StringComparison.Ordinal);
            Assert.Contains(banner.Split(" · ")[0], svg, StringComparison.Ordinal); // the CUI or UNCLASSIFIED part of the banner is in the picture
            Assert.Empty(alice.PageErrors);

            // Lena may not read the notice at all: the map (like the page) says "not found" and shows no restricted name.
            var lena = await session.SignInAsync("lena");
            await session.GotoAsync(lena, "/changes/" + m.EcnDraftInWork);
            await Expect(lena.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("not found");
            Assert.DoesNotContain(m.GimbalBracket, await lena.Page.ContentAsync(), StringComparison.Ordinal);
            Assert.Equal(404, await lena.Page.EvaluateAsync<int>("async (p) => (await fetch(p, { credentials: 'same-origin' })).status", $"/ui/changes/{m.EcnDraftInWork}/impact-map.svg"));
        });
    }

    [E2EFact]
    public async Task The_impact_map_at_375_px_offers_the_table_first_and_a_fit_button_and_stays_inside_its_box()
    {
        await using var session = new BrowserSession(fx, nameof(The_impact_map_at_375_px_offers_the_table_first_and_a_fit_button_and_stays_inside_its_box));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var phone = await session.SignInAsync("alice", width: 375, height: 812);
            var page = phone.Page;
            await page.GotoAsync(fx.BaseUrl + "/changes/" + m.EcnDraftInWork + "#impactmap");
            await Expect(page.Locator("table[aria-label='Impact map as a table']")).ToBeVisibleAsync(); // the table alternative first
            await Expect(page.Locator("svg.vis-svg")).ToHaveCountAsync(0);
            await AssertNoSidewaysScroll(page);
            await page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Impact map view" }).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Map" }).ClickAsync();
            await Expect(page.Locator("svg.vis-svg")).ToBeVisibleAsync();
            var fit = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Fit", Exact = true });
            await Expect(fit).ToBeVisibleAsync();
            var box = await page.Locator("div.vis-viewport").BoundingBoxAsync();
            Assert.True(box is { Width: <= 375 }, "The diagram is wider than the phone.");
            await AssertNoSidewaysScroll(page);
            var before = await page.Locator("[data-vis-content]").GetAttributeAsync("transform");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Zoom in" }).ClickAsync();
            Assert.NotEqual(before, await page.Locator("[data-vis-content]").GetAttributeAsync("transform"));
            await fit.ClickAsync();
            Assert.Equal(Scale(before), Scale(await page.Locator("[data-vis-content]").GetAttributeAsync("transform")));
            Assert.Empty(phone.PageErrors);
        });
    }

    [E2EFact]
    public async Task The_treemap_of_p_1_drills_down_with_breadcrumb_and_keyboard_and_has_a_table_and_marking_patterns()
    {
        await using var session = new BrowserSession(fx, nameof(The_treemap_of_p_1_drills_down_with_breadcrumb_and_keyboard_and_has_a_table_and_marking_patterns));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/parts/" + m.TopAssembly);
            await BrowserSession.OpenTabAsync(page, "Structure");
            await page.GetByLabel("Treemap", new PageGetByLabelOptions { Exact = true }).CheckAsync();
            var tiles = page.Locator("g.vis-tile");
            await Expect(tiles.First).ToBeVisibleAsync();
            await WaitForBehaviourAsync(page);
            var tileCount = await tiles.CountAsync();
            Assert.InRange(tileCount, 3, 400);
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.Locator("nav.tm-crumbs")).ToContainTextAsync(m.TopAssembly);

            // Keyboard: focus a tile, move to its sibling, Enter drills into an assembly; Backspace goes up.
            await page.Locator("g.vis-tile[tabindex='0']").FocusAsync();
            await page.Keyboard.PressAsync("ArrowRight");
            Assert.False(string.IsNullOrEmpty(await ActiveLabel(page)));
            var drillable = page.Locator("g.vis-tile.vis-d1[role='button']").First;
            await Expect(drillable).ToBeVisibleAsync();
            await drillable.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(page.Locator("nav.tm-crumbs button")).ToHaveCountAsync(1);
            await page.Keyboard.PressAsync("Backspace");
            await Expect(page.Locator("nav.tm-crumbs button")).ToHaveCountAsync(0);
            await drillable.DispatchEventAsync("click"); // the tile's own nested tiles cover its middle, so the click is sent to the tile itself
            await Expect(page.Locator("nav.tm-crumbs button")).ToHaveCountAsync(1);
            await page.Locator("nav.tm-crumbs button").ClickAsync();

            // Colour by markings: patterns and the legend name the classes; the KRAKEN line is a hatched Restricted block with no identifier.
            await page.GetByLabel("Colour and pattern show").SelectOptionAsync("Markings");
            await Expect(page.Locator(".vis-legend")).ToContainTextAsync("ITAR (crosshatch)");
            await Expect(page.Locator("g.vis-tile.vis-k-itar").First).ToBeVisibleAsync();
            Assert.DoesNotContain(m.KrakenPart, await page.Locator("div.tm").InnerTextAsync(), StringComparison.Ordinal);

            // The table alternative.
            await page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Structure picture" }).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Table" }).ClickAsync();
            await Expect(page.Locator("table.tm-table tbody tr").First).ToBeVisibleAsync();
            await Expect(page.Locator("svg.vis-svg")).ToHaveCountAsync(0);
            Assert.Empty(alice.PageErrors);
        });
    }

    [E2EFact]
    public async Task The_treemap_at_375_px_offers_the_table_first()
    {
        await using var session = new BrowserSession(fx, nameof(The_treemap_at_375_px_offers_the_table_first));
        await session.RunAsync(async () =>
        {
            var phone = await session.SignInAsync("alice", width: 375, height: 812);
            var page = phone.Page;
            await page.GotoAsync(fx.BaseUrl + "/parts/" + fx.Manifest.TopAssembly + "#structure");
            await page.GetByLabel("Treemap", new PageGetByLabelOptions { Exact = true }).CheckAsync();
            await Expect(page.Locator("table.tm-table")).ToBeVisibleAsync();
            await Expect(page.Locator("svg.vis-svg")).ToHaveCountAsync(0);
            await AssertNoSidewaysScroll(page);
            await page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Structure picture" }).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Treemap" }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Fit", Exact = true })).ToBeVisibleAsync();
            await AssertNoSidewaysScroll(page);
        });
    }

    [E2EFact]
    public async Task The_structure_diff_of_two_units_shows_added_and_removed_lines_with_icon_and_word_and_keyboard_navigation()
    {
        await using var session = new BrowserSession(fx, nameof(The_structure_diff_of_two_units_shows_added_and_removed_lines_with_icon_and_word_and_keyboard_navigation));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            // P-000005 has two alternates for find number 30 by serial: screws M5 up to serial 100, M4 from 101.
            await session.GotoAsync(alice, "/parts/P-000005/compare-units");
            await page.GetByLabel("Unit A: End-item context").FillAsync("ORCA-POD");
            await page.GetByLabel("Unit A: Serial number").FillAsync("100");
            await page.GetByLabel("Unit B: End-item context").FillAsync("ORCA-POD");
            await page.GetByLabel("Unit B: Serial number").FillAsync("101");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Compare units" }).ClickAsync();
            await Expect(page.Locator("table.data").First).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Comparison view" }).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Side by side" }).ClickAsync();

            var rows = page.Locator("div.diff-row[data-change]");
            await Expect(rows.First).ToBeVisibleAsync();
            await Expect(page.Locator("button.chip-toggle.diff-added")).ToContainTextAsync("Added");
            await Expect(page.Locator("button.chip-toggle.diff-removed")).ToContainTextAsync("Removed");
            var count = await rows.CountAsync();
            Assert.True(count >= 2, "expected an added and a removed screw");
            // Every changed row says what happened in words, next to an icon.
            for (var i = 0; i < count; i++)
            {
                var kind = rows.Nth(i).Locator(".diff-kind");
                await Expect(kind).ToContainTextAsync(new Regex("Added|Removed|Changed"));
                await Expect(kind.Locator("svg.icon")).ToHaveCountAsync(1);
            }
            await BrowserSession.AssertBannersAsync(page);

            // Next and previous change by button and by key.
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Next change" }).ClickAsync();
            await Expect(page.Locator("div.toolbar").GetByText(new Regex("^Change 1 of "))).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("n");
            await Expect(page.Locator("div.toolbar").GetByText(new Regex("^Change 2 of "))).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("p");
            await Expect(page.Locator("div.toolbar").GetByText(new Regex("^Change 1 of "))).ToBeVisibleAsync();
            Assert.Empty(alice.PageErrors);

            // Phone width: the panes scroll inside their own box.
            var phone = await session.SignInAsync("alice", width: 375, height: 812);
            await phone.Page.GotoAsync(fx.BaseUrl + "/parts/P-000005/compare-units");
            await phone.Page.GetByLabel("Unit A: End-item context").FillAsync("ORCA-POD");
            await phone.Page.GetByLabel("Unit A: Serial number").FillAsync("100");
            await phone.Page.GetByLabel("Unit B: End-item context").FillAsync("ORCA-POD");
            await phone.Page.GetByLabel("Unit B: Serial number").FillAsync("101");
            await phone.Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Compare units" }).ClickAsync();
            await phone.Page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Comparison view" }).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Side by side" }).ClickAsync();
            await Expect(phone.Page.Locator("div.diff-row[data-change]").First).ToBeVisibleAsync();
            await AssertNoSidewaysScroll(phone.Page);
        });
    }

    [E2EFact]
    public async Task The_timelines_show_the_life_of_p_15_and_the_effectivity_entry_of_p_16_in_a_list_and_on_a_time_line()
    {
        await using var session = new BrowserSession(fx, nameof(The_timelines_show_the_life_of_p_15_and_the_effectivity_entry_of_p_16_in_a_list_and_on_a_time_line));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/parts/" + m.GimbalBracket);
            await BrowserSession.OpenTabAsync(page, "Timeline");
            var items = page.Locator("li.tl-item");
            await Expect(items.First).ToBeVisibleAsync();
            await Expect(items.First).ToContainTextAsync("Revision A created");
            await Expect(page.Locator("li.tl-item", new PageLocatorOptions { HasTextString = "signed:" }).First).ToBeVisibleAsync();
            await Expect(page.Locator("li.tl-item", new PageLocatorOptions { HasTextString = "Listed in " + m.EcnDraftInWork })).ToBeVisibleAsync();
            await Expect(page.Locator("li.tl-item a", new PageLocatorOptions { HasTextString = "Revision B created" })).ToHaveAttributeAsync("href", new Regex(@"revision=B"));
            await BrowserSession.AssertBannersAsync(page);

            // Filter chips and the time line view.
            var chips = page.Locator("button.chip-toggle");
            await chips.Filter(new LocatorFilterOptions { HasTextString = "Signatures" }).ClickAsync();
            await Expect(page.Locator("li.tl-item", new PageLocatorOptions { HasTextString = "signed:" })).ToHaveCountAsync(0);
            await chips.Filter(new LocatorFilterOptions { HasTextString = "Signatures" }).ClickAsync();
            await page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Timeline view" }).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Time line" }).ClickAsync();
            await Expect(page.Locator("g.tl-node").First).ToBeVisibleAsync();
            await WaitForBehaviourAsync(page);
            await page.Locator("g.tl-node[tabindex='0']").FocusAsync();
            await page.Keyboard.PressAsync("ArrowRight");
            Assert.Contains("UTC", await ActiveLabel(page), StringComparison.Ordinal);

            // The yoke: revision B effective from serial 101 through ECN-000004.
            await session.GotoAsync(alice, "/parts/P-000016");
            await BrowserSession.OpenTabAsync(page, "Timeline");
            var change = page.Locator("li.tl-item", new PageLocatorOptions { HasTextString = "Listed in ECN-000004" });
            await Expect(change).ToBeVisibleAsync();
            await Expect(change).ToContainTextAsync("effective");
            await Expect(change).ToContainTextAsync("101");

            // The timeline of a document and of a manufacturing BOM exist too.
            await session.GotoAsync(alice, "/documents/" + m.BracketDrawing);
            await BrowserSession.OpenTabAsync(page, "Timeline");
            await Expect(page.Locator("li.tl-item").First).ToBeVisibleAsync();

            // Lena may not read the bracket, so there is no timeline: "not found", the same as a missing part.
            var lena = await session.SignInAsync("lena");
            await session.GotoAsync(lena, "/parts/" + m.GimbalBracket);
            await Expect(lena.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("not found");
            Assert.Empty(alice.PageErrors);

            var phone = await session.SignInAsync("alice", width: 375, height: 812);
            await phone.Page.GotoAsync(fx.BaseUrl + "/parts/" + m.GimbalBracket + "#timeline");
            await Expect(phone.Page.Locator("li.tl-item").First).ToBeVisibleAsync();
            await AssertNoSidewaysScroll(phone.Page);
        });
    }

    [E2EFact]
    public async Task The_workflow_map_of_ecn_2_shows_the_steps_and_the_pending_approver()
    {
        await using var session = new BrowserSession(fx, nameof(The_workflow_map_of_ecn_2_shows_the_steps_and_the_pending_approver));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/changes/" + m.EcnUnderReview);
            await BrowserSession.OpenTabAsync(page, "Review & release");
            var map = page.Locator("ol.wm");
            await Expect(map).ToBeVisibleAsync();
            await Expect(map.Locator("li.wm-stage").First).ToBeVisibleAsync();
            await Expect(map).ToContainTextAsync("Pending");
            await Expect(map.Locator(".wm-avatar").First).ToBeVisibleAsync();
            await Expect(map).ToContainTextAsync(new Regex("All \\d+ must approve|must approve|Any one approval"));
            await BrowserSession.AssertBannersAsync(page);

            // The workflow page and the approver's task page show it too.
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Workflow details" }).First.ClickAsync();
            await Expect(page.Locator("ol.wm")).ToBeVisibleAsync();
            var phone = await session.SignInAsync("alice", width: 375, height: 812);
            await phone.Page.GotoAsync(fx.BaseUrl + "/changes/" + m.EcnUnderReview + "#review");
            await Expect(phone.Page.Locator("ol.wm")).ToBeVisibleAsync();
            await AssertNoSidewaysScroll(phone.Page);
            Assert.Empty(alice.PageErrors);
        });
    }

    [E2EFact]
    public async Task Dark_and_high_contrast_themes_keep_the_focus_visible_on_the_pictures()
    {
        await using var session = new BrowserSession(fx, nameof(Dark_and_high_contrast_themes_keep_the_focus_visible_on_the_pictures));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/changes/" + m.EcnDraftInWork + "#impactmap");
            await Expect(page.Locator("g.vis-node").First).ToBeVisibleAsync();
            await WaitForBehaviourAsync(page);
            foreach (var theme in new[] { "dark", "high-contrast" })
            {
                await ChooseThemeAsync(page, "alice", theme);
                await Expect(page.Locator("g.vis-node").First).ToBeVisibleAsync();
                await WaitForBehaviourAsync(page);
                await Expect(page.Locator("#user-menu-button")).ToBeFocusedAsync(); // the menu returns focus to its button; wait for that before moving focus to the map
                await page.Locator("g.vis-node[tabindex='0']").FocusAsync();
                await Expect(page.Locator("g.vis-node[tabindex='0']")).ToBeFocusedAsync();
                await page.Keyboard.PressAsync("ArrowRight"); // keyboard focus, so :focus-visible applies
                Assert.True(await page.EvaluateAsync<bool>("() => document.activeElement?.classList.contains('vis-node') === true"), "focus is not on a node: " + await page.EvaluateAsync<string>("() => document.activeElement?.outerHTML.slice(0, 160) ?? 'none'"));
                var (focus, stroke) = await page.EvaluateAsync<string[]>(
                    "() => { const n = document.activeElement; return [getComputedStyle(document.documentElement).getPropertyValue('--c-focus'), getComputedStyle(n.querySelector('.vis-shape')).stroke]; }") is { Length: 2 } r ? (r[0], r[1]) : throw new InvalidOperationException("no focus");
                var (red, green, blue) = Rgb(Hex(focus));
                Assert.Equal($"rgb({red}, {green}, {blue})", stroke); // the focused item is outlined in the theme's focus colour
                var outline = await page.EvaluateAsync<string>("() => getComputedStyle(document.activeElement).outlineStyle");
                Assert.NotEqual("none", outline);
                // The labels stay the theme's text colour on the item's fill.
                var text = await page.EvaluateAsync<string[]>("() => { const t = document.querySelector('.vis-label'); return [getComputedStyle(t).fill, getComputedStyle(document.documentElement).getPropertyValue('--c-text')]; }");
                var (tr, tg, tb) = Rgb(Hex(text[1]));
                Assert.Equal($"rgb({tr}, {tg}, {tb})", text[0]);
            }
            await ChooseThemeAsync(page, "alice", "light");
            Assert.Empty(alice.PageErrors);
        });
    }

    [E2EFact]
    public async Task An_idle_session_lock_removes_the_pictures_from_the_page()
    {
        await fx.EnsureLockHostAsync();
        await using var session = new BrowserSession(fx, nameof(An_idle_session_lock_removes_the_pictures_from_the_page));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var alice = await session.SignInAsync("alice", baseUrl: fx.LockHostUrl);
            var page = alice.Page;
            await page.GotoAsync(fx.LockHostUrl + "/changes/" + m.EcnDraftInWork + "#impactmap");
            await Expect(page.Locator("g.vis-node").First).ToBeVisibleAsync();
            var dialog = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Session locked" });
            await Expect(dialog).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
            var html = await page.ContentAsync();
            Assert.DoesNotContain("vis-svg", html, StringComparison.Ordinal);
            Assert.DoesNotContain("vis-node", html, StringComparison.Ordinal);
            Assert.DoesNotContain(m.GimbalBracket, html, StringComparison.Ordinal);
            Assert.DoesNotContain(m.EcnDraftInWork, html.Replace(m.EcnDraftInWork + "#", "", StringComparison.Ordinal).Replace("changes/" + m.EcnDraftInWork, "", StringComparison.Ordinal), StringComparison.Ordinal);
        });
    }

    [E2EFact]
    public async Task Screenshots_of_the_pictures_for_the_docs()
    {
        await using var session = new BrowserSession(fx, nameof(Screenshots_of_the_pictures_for_the_docs));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var dir = Path.Combine(fx.RepoRoot, "docs", "ux", "screenshots");
            Directory.CreateDirectory(dir);
            var alice = await session.SignInAsync("alice", width: 1200, height: 800);
            var page = alice.Page;
            async Task Shot(ILocator target, string name) => await target.ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(dir, name + ".png") });

            await session.GotoAsync(alice, "/changes/" + m.EcnDraftInWork + "#impactmap");
            await Expect(page.Locator("g.vis-node").First).ToBeVisibleAsync();
            await page.Locator("g.vis-node", new PageLocatorOptions { HasTextString = m.GimbalBracket }).First.ClickAsync();
            await Shot(page.Locator("#tabpanel-impactmap"), "impact-map");

            await session.GotoAsync(alice, "/parts/" + m.TopAssembly + "#structure");
            await page.GetByLabel("Treemap", new PageGetByLabelOptions { Exact = true }).CheckAsync();
            await Expect(page.Locator("g.vis-tile").First).ToBeVisibleAsync();
            await Shot(page.Locator("div.tm"), "bom-treemap");

            await session.GotoAsync(alice, "/parts/P-000005/compare-units");
            await page.GetByLabel("Unit A: End-item context").FillAsync("ORCA-POD");
            await page.GetByLabel("Unit A: Serial number").FillAsync("100");
            await page.GetByLabel("Unit B: End-item context").FillAsync("ORCA-POD");
            await page.GetByLabel("Unit B: Serial number").FillAsync("101");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Compare units" }).ClickAsync();
            await page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Comparison view" }).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Side by side" }).ClickAsync();
            await Expect(page.Locator("div.diff-row[data-change]").First).ToBeVisibleAsync();
            await Shot(page.Locator("section.diff"), "structure-diff");

            await session.GotoAsync(alice, "/parts/" + m.GimbalBracket + "#timeline");
            await Expect(page.Locator("li.tl-item").First).ToBeVisibleAsync();
            await Shot(page.Locator("#tabpanel-timeline"), "timeline");

            await session.GotoAsync(alice, "/changes/" + m.EcnUnderReview + "#review");
            await Expect(page.Locator("ol.wm")).ToBeVisibleAsync();
            await Shot(page.Locator("section.wm-wrap"), "workflow-map");
        });
    }
}
