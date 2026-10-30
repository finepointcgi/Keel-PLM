using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Npgsql;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// ADR 0056 through the real browser: an administrator builds a workflow in the steps editor (move, add, preview as the workflow map, save a draft, continue it, publish),
/// and two approvers decide a part review on a 375 px phone: the card list, the review summary that states what is signed, a Keycloak step-up and the signature.
/// Both screens are audited with axe at 375 px.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class WorkflowAdminMobileE2ETests(E2EFixture fx)
{
    private static readonly Regex PartUrl = new(@"/parts/P-\d+$");

    [E2EFact(Timeout = 600_000)]
    public async Task An_administrator_builds_previews_saves_and_publishes_a_workflow_in_the_steps_editor_and_it_passes_axe_at_375_px()
    {
        await using var session = new BrowserSession(fx, nameof(An_administrator_builds_previews_saves_and_publishes_a_workflow_in_the_steps_editor_and_it_passes_axe_at_375_px));
        await session.RunAsync(async () =>
        {
            var suffix = Guid.NewGuid().ToString("N")[..6];
            var id = "e2e-flow-" + suffix;
            var name = "E2E flow " + suffix;
            var axe = await File.ReadAllTextAsync(AccessibilityTests.AxePath());
            var findings = new List<string>();
            var ada = await session.SignInAsync("ada");
            var page = ada.Page;

            await session.GotoAsync(ada, "/admin/workflow-templates");
            await BrowserSession.ClickAsync(page, "New workflow");
            await BrowserSession.FillAsync(page.GetByLabel("Id *"), id);
            await BrowserSession.FillAsync(page.GetByLabel("Name *"), name);

            // The blank workflow starts as a review and an approval. Rename step 1, add a third step for anyone holding a role and move it up with the keyboard.
            var step1 = page.Locator("[data-step='1']");
            await BrowserSession.FillAsync(step1.GetByLabel("Step name"), "Peer check");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add step", Exact = true }).FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            var step3 = page.Locator("[data-step='3']");
            await Expect(step3).ToBeVisibleAsync();
            await BrowserSession.FillAsync(step3.GetByLabel("Step name"), "Quality gate");
            await step3.GetByLabel("Who").SelectOptionAsync("role");
            await Expect(step3.GetByLabel("Name of the group on the start form")).ToHaveCountAsync(0);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Move step 3 up" }).FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Step 2: Quality gate" })).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Step 3: Approval" })).ToBeVisibleAsync();
            // What a step signs as comes from the engine's reason codes.
            await Expect(step1).ToContainTextAsync("They sign as: Reviewed");

            // Validate, then preview as the workflow map.
            await BrowserSession.ClickAsync(page, "Validate");
            await Expect(page.GetByRole(AriaRole.Status).Filter(new LocatorFilterOptions { HasText = "The workflow is valid." })).ToBeVisibleAsync();
            await BrowserSession.ClickAsync(page, "Preview");
            var preview = page.Locator("[data-preview]");
            await Expect(preview).ToContainTextAsync("Peer check");
            await Expect(preview).ToContainTextAsync("Chosen when the review starts");
            await Expect(preview).ToContainTextAsync("Anyone holding approver");
            await Expect(preview.Locator("ol.wm > li")).ToHaveCountAsync(4); // three steps and the release

            // A draft is saved, survives a reload and is continued.
            await BrowserSession.ClickAsync(page, "Save draft");
            await Expect(page.GetByRole(AriaRole.Status).Filter(new LocatorFilterOptions { HasText = "Saved the draft of " + id })).ToBeVisibleAsync();
            await page.ReloadAsync();
            await Expect(page.GetByRole(AriaRole.Region, new PageGetByRoleOptions { Name = "Saved drafts" })).ToContainTextAsync(id);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Continue the draft of " + id }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Step 1: Peer check" })).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Step 2: Quality gate" })).ToBeVisibleAsync();

            // The editor with the preview open passes axe at desktop width and at 375 px.
            await BrowserSession.ClickAsync(page, "Preview");
            await Expect(preview).ToBeVisibleAsync();
            await AccessibilityTests.RunAxeAsync(page, "desktop editor with preview", axe, findings);
            await page.SetViewportSizeAsync(375, 812);
            await Expect(page.Locator("h1")).ToBeVisibleAsync();
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"), "The editor must not scroll sideways at 375 px.");
            await AccessibilityTests.RunAxeAsync(page, "375 px editor with preview", axe, findings);
            await page.SetViewportSizeAsync(1280, 900);

            // Publish: the draft is gone, the version is listed, and a pin keeps the part's default workflow what it was.
            await BrowserSession.ClickAsync(page, "Publish new version");
            await Expect(page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Publish this version?" })).ToBeVisibleAsync();
            await BrowserSession.ClickAsync(page, "Publish version");
            await Expect(page.GetByRole(AriaRole.Status).Filter(new LocatorFilterOptions { HasText = $"Published {name} version 1" })).ToBeVisibleAsync();
            await Expect(page.Locator("table.data").First.Locator("tr", new LocatorLocatorOptions { HasText = id })).ToContainTextAsync("Active");
            Assert.Equal(0, await page.Locator("section[aria-label='Saved drafts']").Locator("tr", new LocatorLocatorOptions { HasText = id }).CountAsync());
            await page.GetByLabel("Pinned template for part").SelectOptionAsync("part-release-review");
            await page.Locator("tr", new PageLocatorOptions { Has = page.GetByLabel("Pinned template for part") }).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save" }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Status).Filter(new LocatorFilterOptions { HasText = "Pinned part-release-review for part" })).ToBeVisibleAsync();

            // The published version is the next version of the definition the editor wrote.
            await using (var conn = await fx.AppDb.OpenConnectionAsync())
            await using (var cmd = new NpgsqlCommand("SELECT (definition->'nodes'->2->>'name') FROM wf.template WHERE id = @id AND version = 1 AND is_active", conn))
            {
                cmd.Parameters.AddWithValue("id", id);
                Assert.Equal("Quality gate", (string?)await cmd.ExecuteScalarAsync());
            }
            Assert.True(findings.Count == 0, string.Join("\n", findings));
        });
    }

    [E2EFact(Timeout = 900_000)]
    public async Task Approvers_decide_a_review_on_a_375_px_phone_from_a_summary_of_exactly_what_they_sign()
    {
        await using var session = new BrowserSession(fx, nameof(Approvers_decide_a_review_on_a_375_px_phone_from_a_summary_of_exactly_what_they_sign));
        await session.RunAsync(async () =>
        {
            var suffix = Guid.NewGuid().ToString("N")[..6];
            var axe = await File.ReadAllTextAsync(AccessibilityTests.AxePath());
            var findings = new List<string>();
            // The signers log in first (on phones): their logins age while alice works, so the first signature of each needs a step-up.
            var ada = await session.SignInAsync("ada", 375, 812);
            var bob = await session.SignInAsync("bob", 375, 812);
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;

            var partName = $"E2E phone bracket {suffix}";
            await session.GotoAsync(alice, "/parts/new");
            await BrowserSession.FillAsync(page.GetByLabel("Name *"), partName);
            await BrowserSession.ClickAsync(page, "Create part");
            await page.WaitForURLAsync(PartUrl, new PageWaitForURLOptions { Timeout = 60_000 });
            var part = page.Url[(page.Url.LastIndexOf('/') + 1)..];
            await BrowserSession.OpenTabAsync(page, "Review & release");
            await BrowserSession.ClickAsync(page, "Submit for review");
            await BrowserSession.ChooseParticipantsAsync(page, new() { ["Technical reviewer"] = "ada", ["Approvers"] = "bob" });
            await BrowserSession.FillAsync(page.GetByLabel("Comment for the reviewers"), "Raised the wall thickness to 2 mm.");
            await BrowserSession.ClickAsync(page, "Start review");
            await Expect(page.GetByText("Review in progress")).ToBeVisibleAsync();

            // ---- ada, on a phone: the card list, then the summary
            await session.GotoAsync(ada, "/approve");
            var card = ada.Page.Locator("[data-testid=approval-card]", new PageLocatorOptions { HasText = part });
            await Expect(card).ToBeVisibleAsync();
            await Expect(card).ToContainTextAsync(partName);
            await Expect(card).ToContainTextAsync("ITAR");
            await BrowserSession.AssertBannersAsync(ada.Page);
            await AssertNoSidewaysScrollAsync(ada.Page);
            await AccessibilityTests.RunAxeAsync(ada.Page, "375 px approvals list", axe, findings);
            var open = card.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Review and decide" });
            Assert.True((await open.BoundingBoxAsync())!.Height >= 56, "The open button is a large target.");
            await open.ClickAsync();
            await ada.Page.WaitForURLAsync(new Regex(@"/approve/[0-9a-f-]{36}$"));

            var summary = ada.Page.Locator("[data-testid=review-summary]");
            await Expect(summary).ToContainTextAsync(part);
            await Expect(summary).ToContainTextAsync(partName);
            await Expect(summary.Locator("[data-testid=rs-revision]")).ToHaveTextAsync("A");
            await Expect(summary).ToContainTextAsync("ITAR USML XI(d)");
            await Expect(ada.Page.Locator("[data-testid=rs-note]")).ToHaveTextAsync("Raised the wall thickness to 2 mm.");
            var shownHash = (await ada.Page.Locator("[data-testid=rs-hash]").InnerTextAsync()).Trim();
            Assert.Matches("^[0-9a-f]{64}$", shownHash);
            await BrowserSession.AssertBannersAsync(ada.Page);
            await AssertNoSidewaysScrollAsync(ada.Page);
            await AccessibilityTests.RunAxeAsync(ada.Page, "375 px approve page", axe, findings);
            // Thumb-reachable: every decision is a full-width button at least 56 px high.
            var decisions = ada.Page.Locator(".decision-buttons button");
            Assert.True(await decisions.CountAsync() >= 1);
            for (var i = 0; i < await decisions.CountAsync(); i++)
            {
                var box = (await decisions.Nth(i).BoundingBoxAsync())!;
                Assert.True(box.Height >= 56, $"Decision button {i} is {box.Height} px high.");
                Assert.True(box.Width >= 280, $"Decision button {i} is {box.Width} px wide.");
            }

            // ---- ada signs with a comment after the step-up the stale login needs
            var adaPath = new Uri(ada.Page.Url).AbsolutePath;
            await session.SignTaskAsync(ada, adaPath, "Reviewed", expectStepUp: true, comment: "Checked on my phone.");
            await Expect(ada.Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Back to approvals" })).ToBeVisibleAsync();
            await AccessibilityTests.RunAxeAsync(ada.Page, "375 px approve page after signing", axe, findings);

            // The hash the phone showed is the hash the signature recorded.
            await using (var conn = await fx.AppDb.OpenConnectionAsync())
            await using (var cmd = new NpgsqlCommand("SELECT encode(content_hash, 'hex') FROM wf.signature WHERE object_key = @k AND signer_username = 'ada' ORDER BY signed_at DESC LIMIT 1", conn))
            {
                cmd.Parameters.AddWithValue("k", part);
                Assert.Equal(shownHash, (string?)await cmd.ExecuteScalarAsync());
            }

            // ---- bob rejects without a comment (refused in words), then approves; the part is released
            await session.GotoAsync(bob, "/approve");
            var bobCard = bob.Page.Locator("[data-testid=approval-card]", new PageLocatorOptions { HasText = part });
            await Expect(bobCard).ToBeVisibleAsync();
            await bobCard.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Review and decide" }).ClickAsync();
            await bob.Page.WaitForURLAsync(new Regex(@"/approve/[0-9a-f-]{36}$"));
            var bobPath = new Uri(bob.Page.Url).AbsolutePath;
            await BrowserSession.ClickAsync(bob.Page, "Rejected");
            await Expect(bob.Page.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions { HasText = "Enter a comment" })).ToBeVisibleAsync();
            await AccessibilityTests.RunAxeAsync(bob.Page, "375 px approve page with a validation message", axe, findings);
            await session.SignTaskAsync(bob, bobPath, "Approved", expectStepUp: true);
            await Expect(bob.Page.GetByText("the part is released")).ToBeVisibleAsync();

            await session.GotoAsync(alice, "/parts/" + part);
            await Expect(page.Locator(".badges")).ToContainTextAsync("Released");
            Assert.True(findings.Count == 0, $"{findings.Count} accessibility violation(s):\n" + string.Join("\n", findings));
        });
    }

    private static async Task AssertNoSidewaysScrollAsync(IPage page) =>
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"), "The page must not scroll sideways at 375 px.");
}
