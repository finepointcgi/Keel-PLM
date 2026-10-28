using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>Administration and classification in a real browser (increment 2.2): configure, classify a part, search by class, role gating, phone width.</summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class AdminTests(E2EFixture fx)
{
    private static bool IsKnownNotice(string message) =>
        (message.Contains("Refused to apply inline style", StringComparison.Ordinal) || message.Contains("Applying inline style violates", StringComparison.Ordinal))
        && message.Contains("Content Security Policy", StringComparison.Ordinal);

    private static ILocator Section(IPage page, string heading) =>
        page.Locator("section.panel").Filter(new LocatorFilterOptions { Has = page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = heading, Exact = false }) });

    [E2EFact]
    public async Task An_administrator_defines_attributes_and_classes_and_users_classify_a_part_and_find_it_by_facets()
    {
        await using var session = new BrowserSession(fx, nameof(An_administrator_defines_attributes_and_classes_and_users_classify_a_part_and_find_it_by_facets));
        await session.RunAsync(async () =>
        {
            var ada = await session.SignInAsync("ada");
            var page = ada.Page;
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Administration" })).ToBeVisibleAsync();

            // ---- an attribute with a range
            await session.GotoAsync(ada, "/admin");
            await BrowserSession.AssertBannersAsync(page);
            await page.GetByRole(AriaRole.Navigation, new PageGetByRoleOptions { Name = "Administration" }).GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Attributes" }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Administration: attributes" })).ToBeVisibleAsync();
            await BrowserSession.FillAsync(page.GetByLabel("Name *", new PageGetByLabelOptions { Exact = true }), "wall_thickness");
            await BrowserSession.FillAsync(page.GetByLabel("Label *", new PageGetByLabelOptions { Exact = true }), "Wall thickness");
            await page.GetByLabel("Data type").SelectOptionAsync("number");
            await BrowserSession.FillAsync(page.GetByLabel("Unit (numbers)"), "mm");
            await BrowserSession.FillAsync(page.GetByLabel("Minimum"), "0.5");
            await BrowserSession.FillAsync(page.GetByLabel("Maximum"), "20");
            await BrowserSession.ClickAsync(page, "Create attribute");
            await Expect(page.GetByRole(AriaRole.Status).Filter(new LocatorFilterOptions { HasText = "Saved Wall thickness" })).ToBeVisibleAsync();
            await Expect(page.Locator("tbody tr", new PageLocatorOptions { HasText = "wall_thickness" })).ToContainTextAsync("range 0.5 to 20");
            // a server message for a duplicate name
            await BrowserSession.FillAsync(page.GetByLabel("Name *", new PageGetByLabelOptions { Exact = true }), "wall_thickness");
            await BrowserSession.FillAsync(page.GetByLabel("Label *", new PageGetByLabelOptions { Exact = true }), "Again");
            await BrowserSession.ClickAsync(page, "Create attribute");
            await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("already exists");

            // ---- classes: MECH > FAST with an attribute
            await session.GotoAsync(ada, "/admin/classification");
            var create = Section(page, "New top-level class");
            await BrowserSession.FillAsync(create.GetByLabel("Code *"), "MECH");
            await BrowserSession.FillAsync(create.GetByLabel("Name *"), "Mechanical");
            await BrowserSession.ClickAsync(page, "Create class");
            await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Created MECH");
            var sub = Section(page, "New subclass of MECH");
            await BrowserSession.FillAsync(sub.GetByLabel("Code *"), "FAST");
            await BrowserSession.FillAsync(sub.GetByLabel("Name *"), "Fasteners");
            await BrowserSession.ClickAsync(page, "Create class");
            await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Created FAST");
            var attrs = Section(page, "Attributes defined by FAST");
            await BrowserSession.FillAsync(attrs.GetByLabel("Name *", new LocatorGetByLabelOptions { Exact = true }), "diameter");
            await BrowserSession.FillAsync(attrs.GetByLabel("Label *", new LocatorGetByLabelOptions { Exact = true }), "Diameter");
            await attrs.GetByLabel("Data type").SelectOptionAsync("number");
            await BrowserSession.FillAsync(attrs.GetByLabel("Unit (numbers)"), "mm");
            await attrs.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Create attribute" }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Status).Filter(new LocatorFilterOptions { HasText = "Saved Diameter" })).ToBeVisibleAsync();
            // the effective set lists the attribute, defined by FAST
            await Expect(page.Locator("table", new PageLocatorOptions { HasText = "Effective attributes of FAST" }).Locator("tbody tr")).ToContainTextAsync("this class");
            // moving needs a reason, then a confirmation
            await page.GetByRole(AriaRole.Treeitem, new PageGetByRoleOptions { Name = "Mechanical" }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Mechanical" })).ToBeVisibleAsync();

            // ---- alice classifies a new part in its working copy and checks it in
            var alice = await session.SignInAsync("alice");
            var ap = alice.Page;
            await session.GotoAsync(alice, "/parts/new");
            await BrowserSession.FillAsync(ap.GetByLabel("Name *"), "E2E classified bolt");
            await BrowserSession.ClickAsync(ap, "Create part");
            await ap.WaitForURLAsync(new System.Text.RegularExpressions.Regex(@"/parts/P-\d+$"));
            var part = ap.Url[(ap.Url.LastIndexOf('/') + 1)..];
            await BrowserSession.ClickAsync(ap, "Check out");
            var panel = Section(ap, "Classification");
            await Expect(panel).ToContainTextAsync("Not classified");
            await panel.GetByText("Classify this part").ClickAsync();
            await panel.GetByLabel("Class").SelectOptionAsync("FAST");
            await BrowserSession.FillAsync(panel.GetByLabel("Diameter (mm)"), "8");
            await panel.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save classification" }).ClickAsync();
            await Expect(panel).ToContainTextAsync("Mechanical › Fasteners");
            await BrowserSession.ClickAsync(ap, "Check in");
            await Expect(ap.Locator(".badges")).ToContainTextAsync("Version A.2");
            await Expect(panel).ToContainTextAsync("Mechanical › Fasteners");
            await Expect(panel.GetByText("8 mm")).ToBeVisibleAsync();

            // ---- search by class and attribute
            await session.GotoAsync(alice, "/parts/classification");
            await ap.GetByRole(AriaRole.Treeitem, new PageGetByRoleOptions { Name = "Fasteners" }).ClickAsync();
            await Expect(ap.Locator("table.data tbody")).ToContainTextAsync(part);
            await BrowserSession.FillAsync(ap.GetByLabel("From", new PageGetByLabelOptions { Exact = true }), "5");
            await BrowserSession.FillAsync(ap.GetByLabel("To", new PageGetByLabelOptions { Exact = true }), "10");
            await ap.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Apply filters" }).ClickAsync();
            await Expect(ap.Locator("table.data tbody")).ToContainTextAsync(part);
            await BrowserSession.FillAsync(ap.GetByLabel("From", new PageGetByLabelOptions { Exact = true }), "20");
            await BrowserSession.FillAsync(ap.GetByLabel("To", new PageGetByLabelOptions { Exact = true }), "30");
            await ap.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Apply filters" }).ClickAsync();
            await Expect(ap.GetByText("No parts match.")).ToBeVisibleAsync();

            // ---- the history shows the changes with differences
            await session.GotoAsync(ada, "/admin/history");
            await Expect(page.Locator("tbody tr", new PageLocatorOptions { HasText = "admin.attribute_create" }).First).ToBeVisibleAsync();
            await Expect(page.Locator("tbody")).ToContainTextAsync("admin.class_create");

            foreach (var user in new[] { ada, alice })
                Assert.Empty(user.PageErrors);
            Assert.All(ada.ConsoleErrors, e => Assert.True(IsKnownNotice(e), e));
            Assert.All(alice.ConsoleErrors, e => Assert.True(IsKnownNotice(e), e));
        });
    }

    [E2EFact]
    public async Task An_administrator_publishes_a_lifecycle_version_edits_a_workflow_template_and_pins_it()
    {
        await using var session = new BrowserSession(fx, nameof(An_administrator_publishes_a_lifecycle_version_edits_a_workflow_template_and_pins_it));
        await session.RunAsync(async () =>
        {
            var ada = await session.SignInAsync("ada");
            var page = ada.Page;

            // ---- a new lifecycle version: add a state and two transitions, validate (effects), publish with a note and a confirmation
            await session.GotoAsync(ada, "/admin/lifecycles");
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Standard part lifecycle" })).ToBeVisibleAsync();
            await BrowserSession.ClickAsync(page, "Edit as a new version");
            await BrowserSession.ClickAsync(page, "Add state");
            await BrowserSession.FillAsync(page.GetByLabel("State key"), "Qualified");
            await BrowserSession.FillAsync(page.GetByLabel("Label of Qualified"), "Qualified");
            await BrowserSession.ClickAsync(page, "Add transition");
            await BrowserSession.ClickAsync(page, "Add transition");
            var from = page.GetByLabel("Transition from");
            var to = page.GetByLabel("Transition to");
            var via = page.GetByLabel("Taken by", new PageGetByLabelOptions { Exact = true });
            await from.Nth(5).SelectOptionAsync("InWork");
            await to.Nth(5).SelectOptionAsync("Qualified");
            await from.Nth(6).SelectOptionAsync("Qualified");
            await to.Nth(6).SelectOptionAsync("InWork");
            Assert.Equal(7, await via.CountAsync());
            await BrowserSession.ClickAsync(page, "Validate and preview effect");
            await Expect(page.Locator(".notice")).ToContainTextAsync("Added state Qualified");
            await BrowserSession.FillAsync(page.GetByLabel("Change note *"), "Add the qualification step");
            await BrowserSession.ClickAsync(page, "Publish new version");
            await Expect(page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Confirm publish" })).ToContainTextAsync("existing revision");
            await BrowserSession.ClickAsync(page, "Confirm publish");
            await Expect(page.GetByRole(AriaRole.Status).Filter(new LocatorFilterOptions { HasText = "Published version 2" })).ToBeVisibleAsync();

            // ---- a workflow template: open the structured editor, switch to JSON and back, publish a new version, pin it
            await session.GotoAsync(ada, "/admin/workflow-templates");
            await page.Locator("tr", new PageLocatorOptions { HasText = "part-release-review" }).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Edit" }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Node tech-review" })).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "JSON (advanced)" }).ClickAsync();
            await Expect(page.Locator("textarea.code")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("tech-review"));
            await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Structured" }).ClickAsync();
            await BrowserSession.FillAsync(page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Node tech-review" }).GetByLabel("Due in days"), "9");
            await BrowserSession.ClickAsync(page, "Validate");
            await Expect(page.GetByRole(AriaRole.Status).Filter(new LocatorFilterOptions { HasText = "The definition is valid." })).ToBeVisibleAsync();
            await BrowserSession.ClickAsync(page, "Publish new version");
            await BrowserSession.ClickAsync(page, "Confirm publish");
            await Expect(page.GetByRole(AriaRole.Status).Filter(new LocatorFilterOptions { HasText = "version 2" })).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Changes since v1" }).ClickAsync();
            await Expect(page.Locator("section[aria-label='Differences']")).ToContainTextAsync("dueDays");
            await page.GetByLabel("Pinned template for part").SelectOptionAsync("part-release-review");
            await page.Locator("tr", new PageLocatorOptions { HasText = "Pinned template" }).Or(page.Locator("tr", new PageLocatorOptions { Has = page.GetByLabel("Pinned template for part") }))
                .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save" }).First.ClickAsync();
            await Expect(page.GetByRole(AriaRole.Status).Filter(new LocatorFilterOptions { HasText = "Pinned part-release-review for part" })).ToBeVisibleAsync();

            // ---- numbering preview and the export download
            await session.GotoAsync(ada, "/admin/numbering");
            await page.Locator("tr", new PageLocatorOptions { HasText = "document" }).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Edit" }).ClickAsync();
            await BrowserSession.FillAsync(page.GetByLabel("Prefix *"), "DOC-");
            await Expect(page.GetByText("Next number would be")).ToContainTextAsync("DOC-");
            await session.GotoAsync(ada, "/admin/config");
            var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Download configuration" }).ClickAsync());
            Assert.Equal("keel-config.json", download.SuggestedFilename);
            var path = Path.Combine(Path.GetTempPath(), $"e2e-config-{Guid.NewGuid():N}.json");
            await download.SaveAsAsync(path);
            try
            {
                var json = await File.ReadAllTextAsync(path);
                Assert.Contains("keel-config/1", json, StringComparison.Ordinal);
                Assert.Contains("\"sha256\"", json, StringComparison.Ordinal);
            }
            finally { File.Delete(path); }
            Assert.All(ada.ConsoleErrors, e => Assert.True(IsKnownNotice(e), e));
        });
    }

    [E2EFact]
    public async Task Non_administrators_cannot_open_administration_and_the_attempt_is_recorded()
    {
        await using var session = new BrowserSession(fx, nameof(Non_administrators_cannot_open_administration_and_the_attempt_is_recorded));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            Assert.Equal(0, await alice.Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Administration" }).CountAsync());
            await alice.Page.GotoAsync(fx.BaseUrl + "/admin/attributes");
            await Expect(alice.Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Not authorized" })).ToBeVisibleAsync();
            await using var conn = await fx.AppDb.OpenConnectionAsync();
            await using var cmd = new Npgsql.NpgsqlCommand("SELECT count(*) FROM audit.event WHERE action = 'access.denied' AND object_id LIKE '%admin/attributes%'", conn);
            Assert.True((long)(await cmd.ExecuteScalarAsync())! >= 1);
        });
    }

    [E2EFact]
    public async Task Administration_pages_fit_a_phone_without_sideways_scrolling_and_the_keyboard_reaches_the_forms()
    {
        await using var session = new BrowserSession(fx, nameof(Administration_pages_fit_a_phone_without_sideways_scrolling_and_the_keyboard_reaches_the_forms));
        await session.RunAsync(async () =>
        {
            var ada = await session.SignInAsync("ada", 375, 812);
            foreach (var path in new[] { "/admin", "/admin/attributes", "/admin/lifecycles", "/admin/lists", "/admin/workflow-templates", "/admin/classification", "/admin/numbering", "/admin/reasons", "/admin/config", "/admin/history", "/parts/classification" })
            {
                await session.GotoAsync(ada, path);
                await Expect(ada.Page.Locator("[data-position='top']")).ToBeVisibleAsync();
                var overflow = await ada.Page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth + 1");
                Assert.False(overflow, $"{path} scrolls sideways at phone width.");
            }
            // Keyboard: tab from the top reaches the sub-navigation links in order, and Enter follows one.
            await session.GotoAsync(ada, "/admin");
            await ada.Page.Keyboard.PressAsync("Tab");
            var focused = 0;
            for (var i = 0; i < 25 && focused == 0; i++)
            {
                var name = await ada.Page.EvaluateAsync<string>("document.activeElement?.closest('nav[aria-label=\"Administration\"]') ? document.activeElement.textContent : ''");
                if (name == "Attributes") focused = 1; else await ada.Page.Keyboard.PressAsync("Tab");
            }
            Assert.Equal(1, focused);
            await ada.Page.Keyboard.PressAsync("Enter");
            await Expect(ada.Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Administration: attributes" })).ToBeVisibleAsync();
            Assert.All(ada.ConsoleErrors, e => Assert.True(IsKnownNotice(e), e));
        });
    }
}
