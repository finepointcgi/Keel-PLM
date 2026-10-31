using System.Text.RegularExpressions;
using Keel.TestSupport;
using Microsoft.Playwright;
using Npgsql;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// ADR 0061 through the real browser: quick add part in three interactions, Edit then Save (and Discard), a document created with a link to a part, Add document
/// on a part page, the released-part banner, Simple view, and an axe audit of every new screen state.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class SimplifyFlowsTests(E2EFixture fx)
{
    private static readonly Regex PartUrl = new(@"/parts/P-\d+$");
    private static readonly Regex DocumentUrl = new(@"/documents/D-\d+$");

    /// <summary>Quick add: the part list's Create part link, the name, the Create part button.</summary>
    private static async Task<string> QuickAddAsync(BrowserSession session, UserPage up, string name)
    {
        var page = up.Page;
        await session.GotoAsync(up, "/parts/new");
        await BrowserSession.FillAsync(page.GetByLabel("Name *"), name);
        await BrowserSession.ClickAsync(page, "Create part");
        await page.WaitForURLAsync(PartUrl, new PageWaitForURLOptions { Timeout = 60_000 });
        return page.Url[(page.Url.LastIndexOf('/') + 1)..];
    }

    [E2EFact]
    public async Task Quick_add_part_needs_three_interactions_and_asks_for_what_is_missing_in_plain_words()
    {
        await using var session = new BrowserSession(fx, nameof(Quick_add_part_needs_three_interactions_and_asks_for_what_is_missing_in_plain_words));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;

            // Interaction 1: Create part on the Parts list. 2: the name. 3: Create part.
            await session.GotoAsync(alice, "/parts");
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Create part", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(new Regex(@"/parts/new$"));
            await BrowserSession.FillAsync(page.GetByLabel("Name *"), "E2E quick part one");
            await BrowserSession.ClickAsync(page, "Create part");
            await page.WaitForURLAsync(PartUrl, new PageWaitForURLOptions { Timeout = 60_000 });
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "E2E quick part one", Exact = false, Level = 1 })).ToBeVisibleAsync();
            await Expect(page.Locator(".badges")).ToContainTextAsync("Version A.1");
            await BrowserSession.AssertBannersAsync(page);

            // A blank name is asked for in plain words, next to the field.
            await session.GotoAsync(alice, "/parts/new");
            await BrowserSession.ClickAsync(page, "Create part");
            await Expect(page.GetByText("Enter a name for the part.")).ToBeVisibleAsync();
            await Expect(page.GetByLabel("Name *")).ToHaveAttributeAsync("aria-invalid", "true");

            // The rarely changed things are folded away but one click away: kind, markings (one chip), More details.
            await Expect(page.Locator(".defaults-line .markings-line")).ToContainTextAsync(new System.Text.RegularExpressions.Regex("This part will be an? .+, .+, .+\\."));
            await Expect(page.Locator("[data-markings-default] .chip")).ToBeVisibleAsync();
            await Expect(page.Locator("[data-markings-default] fieldset")).ToHaveCountAsync(0);
            await page.Locator("[data-markings-change]").ClickAsync();
            await Expect(page.Locator("[data-markings-default] fieldset")).ToBeVisibleAsync();
            await Expect(page.GetByLabel("Material", new PageGetByLabelOptions { Exact = true })).ToBeHiddenAsync();
            await page.Locator("details[data-more-details] summary").ClickAsync();
            await Expect(page.GetByLabel("Material", new PageGetByLabelOptions { Exact = true })).ToBeVisibleAsync();

            // A rule of the markings is explained too (ITAR needs a USML category), and the markings editor opens by itself.
            await page.Locator("[data-markings-default] select").First.SelectOptionAsync("Itar");
            await page.Locator("[data-markings-default] label", new PageLocatorOptions { HasText = "USML category" }).Locator("input").FillAsync("");
            await BrowserSession.FillAsync(page.GetByLabel("Name *"), "E2E refused markings");
            await BrowserSession.ClickAsync(page, "Create part");
            await Expect(page.Locator("[data-quick-errors]")).ToContainTextAsync("USML category");

            // Create and add another: the form stays, empties its name and puts the cursor back there.
            await session.GotoAsync(alice, "/parts/new");
            await BrowserSession.FillAsync(page.GetByLabel("Name *"), "E2E quick another A");
            await BrowserSession.ClickAsync(page, "Create and add another");
            await Expect(page.Locator("[data-toast=success]").Last).ToContainTextAsync("Add the next one");
            await Expect(page.GetByLabel("Name *")).ToHaveValueAsync("");
            await Expect(page.GetByLabel("Name *")).ToBeFocusedAsync();
            await BrowserSession.FillAsync(page.GetByLabel("Name *"), "E2E quick another B");
            await BrowserSession.ClickAsync(page, "Create part");
            await page.WaitForURLAsync(PartUrl, new PageWaitForURLOptions { Timeout = 60_000 });

            // The next quick form starts with the type chosen last time.
            await session.GotoAsync(alice, "/parts/new");
            await page.Locator("[data-kind-change]").ClickAsync();
            await page.GetByLabel("Type").SelectOptionAsync("Standard");
            await BrowserSession.FillAsync(page.GetByLabel("Name *"), "E2E standard part");
            await BrowserSession.ClickAsync(page, "Create part");
            await page.WaitForURLAsync(PartUrl, new PageWaitForURLOptions { Timeout = 60_000 });
            await session.GotoAsync(alice, "/parts/new");
            await Expect(page.Locator(".defaults-line .markings-line")).ToContainTextAsync("Standard");
        });
    }

    [E2EFact]
    public async Task Edit_then_Save_is_three_interactions_and_Discard_undoes_the_edit()
    {
        await using var session = new BrowserSession(fx, nameof(Edit_then_Save_is_three_interactions_and_Discard_undoes_the_edit));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            var part = await QuickAddAsync(session, alice, "E2E simple edit");

            // Interaction 1: Edit (checks the part out). 2: change the description. 3: Save (stores and checks in).
            await BrowserSession.ClickAsync(page, "Edit");
            await Expect(page.GetByText("You are editing this part")).ToBeVisibleAsync();
            await BrowserSession.FillAsync(page.GetByLabel("Description"), "Wall thickness 3 mm.");
            await BrowserSession.ClickAsync(page, "Save");
            await Expect(page.Locator("[data-toast=success]").Last).ToContainTextAsync("Saved as version A.2");
            await Expect(page.Locator(".badges")).ToContainTextAsync("Version A.2");
            await Expect(page.GetByText("Wall thickness 3 mm.")).ToBeVisibleAsync();
            await Expect(page.GetByText("You are editing this part")).ToHaveCountAsync(0);

            // With a change note, which the history shows.
            await BrowserSession.ClickAsync(page, "Edit");
            await BrowserSession.FillAsync(page.GetByLabel("Description"), "Wall thickness 4 mm.");
            await BrowserSession.FillAsync(page.GetByLabel("Change note (optional)"), "Stiffer");
            await BrowserSession.ClickAsync(page, "Save");
            await Expect(page.Locator(".badges")).ToContainTextAsync("Version A.3");
            await BrowserSession.OpenTabAsync(page, "History");
            await Expect(page.Locator("table.data")).ToContainTextAsync("Stiffer");

            // Discard changes asks first, then returns to the last saved version.
            await BrowserSession.OpenTabAsync(page, "Overview");
            await BrowserSession.ClickAsync(page, "Edit");
            await BrowserSession.FillAsync(page.GetByLabel("Description"), "Throw this away.");
            await BrowserSession.ClickAsync(page, "Discard changes");
            var dialog = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Discard your changes?" });
            await Expect(dialog).ToBeVisibleAsync();
            await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Discard changes" }).ClickAsync();
            await Expect(page.Locator("[data-toast=success]").Last).ToContainTextAsync("Changes discarded");
            await Expect(page.GetByText("Throw this away.")).ToHaveCountAsync(0);
            await Expect(page.Locator(".badges")).ToContainTextAsync("Version A.3");

            // The step-by-step controls are still there under More.
            await BrowserSession.OpenMoreAsync(page);
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Check out", Exact = true })).ToBeVisibleAsync();
            Assert.False(string.IsNullOrEmpty(part));
        });
    }

    [E2EFact]
    public async Task A_released_part_says_why_it_cannot_be_edited_and_Start_change_fills_in_the_part()
    {
        await using var session = new BrowserSession(fx, nameof(A_released_part_says_why_it_cannot_be_edited_and_Start_change_fills_in_the_part));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            var part = await QuickAddAsync(session, alice, "E2E released for banner");
            // Test set-up only: release the part's revision (the release workflow itself is covered by the happy-path test).
            await using (var conn = await fx.AppDb.OpenConnectionAsync())
            await using (var cmd = new NpgsqlCommand("UPDATE plm.part_revision SET lifecycle_state = 'Released' WHERE master_id = (SELECT id FROM plm.part_master WHERE number = @n)", conn))
            {
                cmd.Parameters.AddWithValue("n", part);
                await cmd.ExecuteNonQueryAsync();
            }
            await session.GotoAsync(alice, "/parts/" + part);
            await Expect(page.Locator("[data-edit-banner]")).ToContainTextAsync("change order");
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Edit", Exact = true })).ToHaveCountAsync(0);
            await Expect(page.Locator("[data-action=add-document]")).ToHaveCountAsync(0);
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Start change", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(new Regex(@"/changes/new/\w+\?part=" + part));
            await Expect(page.Locator("main")).ToContainTextAsync(part);
        });
    }

    [E2EFact]
    public async Task A_document_is_created_linked_to_a_part_and_added_to_a_part_from_the_part_page()
    {
        await using var session = new BrowserSession(fx, nameof(A_document_is_created_linked_to_a_part_and_added_to_a_part_from_the_part_page));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            var part = await QuickAddAsync(session, alice, "E2E part with documents");

            // Interactions: the name, the part (type a number, choose it), Create document.
            await session.GotoAsync(alice, "/documents/new");
            await BrowserSession.FillAsync(page.GetByLabel("Name *"), "E2E linked drawing");
            await page.Locator("[data-picker=link-part] input").FillAsync(part);
            await page.Locator($"[data-picker-option={part}]").ClickAsync();
            await Expect(page.Locator("[data-link-type]")).ToHaveValueAsync("describes");
            await BrowserSession.ClickAsync(page, "Create document");
            await page.WaitForURLAsync(DocumentUrl, new PageWaitForURLOptions { Timeout = 60_000 });
            var first = page.Url[(page.Url.LastIndexOf('/') + 1)..];
            await Expect(page.Locator("[data-toast=success]").Last).ToContainTextAsync("is now linked");

            await session.GotoAsync(alice, "/parts/" + part);
            await Expect(page.Locator(".badges")).ToContainTextAsync("Version A.2"); // the part was saved with the link, nothing was left checked out
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Edit", Exact = true })).ToBeVisibleAsync();
            await BrowserSession.OpenTabAsync(page, "Documents");
            await Expect(page.Locator("#linked-docs-heading").Locator("xpath=..")).ToContainTextAsync(first);

            // A second document, made without a link, is attached from the part page in one dialog.
            await session.GotoAsync(alice, "/documents/new");
            await BrowserSession.FillAsync(page.GetByLabel("Name *"), "E2E loose specification");
            await BrowserSession.ClickAsync(page, "Create document");
            await page.WaitForURLAsync(DocumentUrl, new PageWaitForURLOptions { Timeout = 60_000 });
            var second = page.Url[(page.Url.LastIndexOf('/') + 1)..];

            await session.GotoAsync(alice, "/parts/" + part);
            await page.Locator("[data-action=add-document]").ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Add a document to " + part });
            await Expect(dialog).ToBeVisibleAsync();
            await dialog.Locator("[data-picker=add-doc-existing] input").FillAsync(second);
            await dialog.Locator($"[data-picker-option={second}]").ClickAsync();
            await dialog.Locator("[data-add-link-type]").SelectOptionAsync("references");
            await dialog.Locator("[data-add-ok]").ClickAsync();
            await Expect(page.Locator("[data-toast=success]").Last).ToContainTextAsync($"{second} is now linked to {part}");
            await Expect(page.Locator(".badges")).ToContainTextAsync("Version A.3");

            // And a brand-new document is created and linked from the same dialog.
            await page.Locator("[data-action=add-document]").ClickAsync();
            await dialog.GetByLabel("Create a new document").CheckAsync();
            await BrowserSession.FillAsync(dialog.GetByLabel("Name *"), "E2E drawing from the dialog");
            await dialog.Locator("[data-add-ok]").ClickAsync();
            await Expect(page.Locator("[data-toast=success]").Last).ToContainTextAsync($"is now linked to {part}");
            await Expect(page.Locator(".badges")).ToContainTextAsync("Version A.4");
            await BrowserSession.OpenTabAsync(page, "Documents");
            await Expect(page.Locator("#linked-docs-heading").Locator("xpath=..")).ToContainTextAsync(second);

            // While somebody is editing (here: the same person), the link joins the draft and is applied at Save.
            await BrowserSession.ClickAsync(page, "Edit");
            await page.Locator("[data-action=add-document]").ClickAsync();
            await dialog.GetByLabel("Create a new document").CheckAsync();
            await BrowserSession.FillAsync(dialog.GetByLabel("Name *"), "E2E draft attachment");
            await dialog.Locator("[data-add-ok]").ClickAsync();
            await Expect(page.Locator("[data-toast=success]").Last).ToContainTextAsync("applied when you save");
            await Expect(page.Locator(".badges")).ToContainTextAsync("Version A.4");
            await BrowserSession.ClickAsync(page, "Save");
            await Expect(page.Locator(".badges")).ToContainTextAsync("Version A.5");
        });
    }

    [E2EFact]
    public async Task A_new_part_is_created_with_its_files_in_one_step_and_opens_on_the_viewer()
    {
        await using var session = new BrowserSession(fx, nameof(A_new_part_is_created_with_its_files_in_one_step_and_opens_on_the_viewer));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            var suffix = Guid.NewGuid().ToString("N")[..6];
            await session.GotoAsync(alice, "/parts/new");
            await BrowserSession.FillAsync(page.Locator("[data-quick-name]"), $"E2E gimbal with files {suffix}");

            // Choose the STL, a drawing and one more file together: each becomes a row with a name from the file name and a type from its extension.
            var stl = new FilePayload { Name = $"gimbal_bracket_{suffix}.stl", MimeType = "model/stl", Buffer = ViewerSamples.AsciiStlCube() };
            var pdf = new FilePayload { Name = $"bracket drawing {suffix}.pdf", MimeType = "application/pdf", Buffer = System.Text.Encoding.UTF8.GetBytes("%PDF-1.4 synthetic " + suffix) };
            var extra = new FilePayload { Name = $"extra {suffix}.txt", MimeType = "text/plain", Buffer = System.Text.Encoding.UTF8.GetBytes("extra") };
            await page.Locator("[data-files-input]").SetInputFilesAsync([stl, pdf, extra]);
            var rows = page.Locator(".file-row");
            await Expect(rows).ToHaveCountAsync(3);
            await Expect(rows.Nth(0).Locator("[data-file-name]")).ToHaveValueAsync($"gimbal bracket {suffix}");
            await Expect(rows.Nth(0).Locator("[data-file-type]")).ToHaveValueAsync("CAD");
            await Expect(rows.Nth(1).Locator("[data-file-type]")).ToHaveValueAsync("Drawing");

            // A row that does not belong can be taken out again.
            await rows.Nth(2).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Remove" }).ClickAsync();
            await Expect(rows).ToHaveCountAsync(2);

            await BrowserSession.ClickAsync(page, "Create part");
            await page.WaitForURLAsync(new Regex(@"/parts/P-\d+\?tab=viewer"), new PageWaitForURLOptions { Timeout = 90_000 });
            await Expect(page.Locator("[data-toast=success]").Last).ToContainTextAsync("with 2 document");
            await Expect(page.Locator(".badges")).ToContainTextAsync("Version A.2"); // one save for both documents
            await Expect(page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Viewer" })).ToHaveAttributeAsync("aria-selected", "true");
            await BrowserSession.OpenTabAsync(page, "Documents");
            await Expect(page.Locator("#linked-docs-heading").Locator("xpath=..")).ToContainTextAsync($"gimbal bracket {suffix}");
            await Expect(page.Locator("#linked-docs-heading").Locator("xpath=..")).ToContainTextAsync($"bracket drawing {suffix}");
        });
    }

    [E2EFact]
    public async Task Several_files_are_added_to_an_existing_part_in_one_dialog_and_save_the_part_once()
    {
        await using var session = new BrowserSession(fx, nameof(Several_files_are_added_to_an_existing_part_in_one_dialog_and_save_the_part_once));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            var suffix = Guid.NewGuid().ToString("N")[..6];
            var part = await QuickAddAsync(session, alice, $"E2E part for several files {suffix}");
            await Expect(page.Locator(".badges")).ToContainTextAsync("Version A.1");

            await BrowserSession.ClickAsync(page, "Add document");
            var dialog = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = $"Add a document to {part}" });
            await Expect(dialog).ToBeVisibleAsync();
            await dialog.Locator("[data-add-several]").CheckAsync();
            await dialog.Locator("[data-files-input]").SetInputFilesAsync([
                new FilePayload { Name = $"interface spec {suffix}.pdf", MimeType = "application/pdf", Buffer = System.Text.Encoding.UTF8.GetBytes("%PDF-1.4 spec " + suffix) },
                new FilePayload { Name = $"mount_{suffix}.stl", MimeType = "model/stl", Buffer = ViewerSamples.AsciiStlCube() }]);
            await Expect(dialog.Locator(".file-row")).ToHaveCountAsync(2);
            await Expect(dialog.Locator(".file-row").Nth(0).Locator("[data-file-type]")).ToHaveValueAsync("Specification");
            await Expect(dialog.Locator(".file-row").Nth(1).Locator("[data-file-type]")).ToHaveValueAsync("CAD");
            await dialog.Locator("[data-add-ok]").ClickAsync();

            await Expect(page.Locator("[data-toast]").Last).ToContainTextAsync("2 document(s) linked");
            await Expect(page.Locator(".badges")).ToContainTextAsync("Version A.2");
            await BrowserSession.OpenTabAsync(page, "Viewer"); // the STL was also recorded as the part's model
            await BrowserSession.OpenTabAsync(page, "Documents");
            await Expect(page.Locator("#linked-docs-heading").Locator("xpath=..")).ToContainTextAsync($"interface spec {suffix}");
            await Expect(page.Locator("#linked-docs-heading").Locator("xpath=..")).ToContainTextAsync($"mount {suffix}");
        });
    }

    [E2EFact]
    public async Task Simple_view_hides_the_advanced_navigation_groups_and_is_remembered()
    {
        await using var session = new BrowserSession(fx, nameof(Simple_view_hides_the_advanced_navigation_groups_and_is_remembered));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/");
            var rail = page.GetByRole(AriaRole.Navigation, new PageGetByRoleOptions { Name = "Main" });
            await Expect(rail.GetByRole(AriaRole.Group, new LocatorGetByRoleOptions { Name = "Quality" })).ToBeVisibleAsync();
            await page.Locator("#user-menu-button").ClickAsync();
            await page.Locator("[data-simple-view-choice=simple]").ClickAsync();
            await Expect(rail.GetByRole(AriaRole.Group, new LocatorGetByRoleOptions { Name = "Quality" })).ToHaveCountAsync(0);
            await Expect(rail.GetByRole(AriaRole.Group, new LocatorGetByRoleOptions { Name = "Product data" })).ToBeVisibleAsync();
            // Still reachable: the command palette finds the places that Simple view leaves out of the rail.
            await page.Keyboard.PressAsync("Escape");
            await page.ReloadAsync();
            await Expect(rail.GetByRole(AriaRole.Group, new LocatorGetByRoleOptions { Name = "Quality" })).ToHaveCountAsync(0);
            await page.Locator("#user-menu-button").ClickAsync();
            await Expect(page.Locator("[data-simple-view-choice=simple]")).ToHaveAttributeAsync("aria-pressed", "true");
            await page.Locator("[data-simple-view-choice=all]").ClickAsync();
            await Expect(rail.GetByRole(AriaRole.Group, new LocatorGetByRoleOptions { Name = "Quality" })).ToBeVisibleAsync();
        });
    }

    [E2EFact(Timeout = 600_000)]
    public async Task The_new_screens_pass_the_accessibility_audit()
    {
        await using var session = new BrowserSession(fx, nameof(The_new_screens_pass_the_accessibility_audit));
        await session.RunAsync(async () =>
        {
            var axe = await File.ReadAllTextAsync(AxePath());
            var findings = new List<string>();
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            var part = await QuickAddAsync(session, alice, "E2E axe part");
            var released = await QuickAddAsync(session, alice, "E2E axe released part");
            await using (var conn = await fx.AppDb.OpenConnectionAsync())
            await using (var cmd = new NpgsqlCommand("UPDATE plm.part_revision SET lifecycle_state = 'Released' WHERE master_id = (SELECT id FROM plm.part_master WHERE number = @n)", conn))
            {
                cmd.Parameters.AddWithValue("n", released);
                await cmd.ExecuteNonQueryAsync();
            }

            async Task Audit(string label) => await AccessibilityTests.RunAxeAsync(page, label, axe, findings);

            await session.GotoAsync(alice, "/parts");
            await Audit("parts list with Create part");
            await session.GotoAsync(alice, "/parts/new");
            await Expect(page.GetByLabel("Name *")).ToBeVisibleAsync();
            await Audit("quick add part");
            await BrowserSession.ClickAsync(page, "Create part");
            await Expect(page.GetByText("Enter a name for the part.")).ToBeVisibleAsync();
            await Audit("quick add part with an error");
            await page.Locator("[data-kind-change]").ClickAsync();
            await page.Locator("[data-markings-change]").ClickAsync();
            await page.Locator("details[data-more-details] summary").ClickAsync();
            await Audit("quick add part, every disclosure open");

            await page.Locator("[data-files-input]").SetInputFilesAsync([
                new FilePayload { Name = "audit_bracket.stl", MimeType = "model/stl", Buffer = ViewerSamples.AsciiStlCube() },
                new FilePayload { Name = "audit drawing.pdf", MimeType = "application/pdf", Buffer = System.Text.Encoding.UTF8.GetBytes("%PDF-1.4 audit") }]);
            await Expect(page.Locator(".file-row")).ToHaveCountAsync(2);
            await Audit("quick add part with files chosen");

            await session.GotoAsync(alice, "/documents/new");
            await Expect(page.GetByLabel("Name *")).ToBeVisibleAsync();
            await Audit("new document");
            await page.Locator("[data-picker=link-part] input").FillAsync(part);
            await Expect(page.Locator($"[data-picker-option={part}]")).ToBeVisibleAsync();
            await Audit("new document, part results");
            await page.Locator($"[data-picker-option={part}]").ClickAsync();
            await Audit("new document, part chosen");

            await session.GotoAsync(alice, "/parts/" + part);
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Edit", Exact = true })).ToBeVisibleAsync();
            await Audit("part page, ready to edit");
            await BrowserSession.OpenMoreAsync(page);
            await Audit("part page, More menu open");
            await page.Locator("[data-action=add-document]").ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Add a document to " + part });
            await Expect(dialog).ToBeVisibleAsync();
            await Audit("add document dialog, attach");
            await dialog.GetByLabel("Create a new document").CheckAsync();
            await Audit("add document dialog, create");
            await dialog.Locator("[data-add-cancel]").ClickAsync();
            await BrowserSession.ClickAsync(page, "Edit");
            await Expect(page.GetByLabel("Change note (optional)")).ToBeVisibleAsync();
            await Audit("part page, editing");
            await BrowserSession.ClickAsync(page, "Discard changes");
            await Expect(page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Discard your changes?" })).ToBeVisibleAsync();
            await Audit("discard dialog");
            await page.Keyboard.PressAsync("Escape");
            await session.GotoAsync(alice, "/parts/" + part + "?tab=history");
            await Expect(page.Locator("[data-glossary=version]")).ToBeVisibleAsync();
            await Audit("history with glossary hints");

            await session.GotoAsync(alice, "/parts/" + released);
            await Expect(page.Locator("[data-edit-banner]")).ToBeVisibleAsync();
            await Audit("released part banner");

            await page.GotoAsync(fx.BaseUrl + "/");
            await page.Locator("#user-menu-button").ClickAsync();
            await Expect(page.Locator("[data-simple-view-choice=simple]")).ToBeVisibleAsync();
            await Audit("user menu with navigation choice");

            Assert.True(findings.Count == 0, $"{findings.Count} accessibility violation(s):\n" + string.Join("\n", findings));
        });
    }

    private static string AxePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "Keel.E2E", "vendor", "axe-core", "axe.min.js");
    }
}
