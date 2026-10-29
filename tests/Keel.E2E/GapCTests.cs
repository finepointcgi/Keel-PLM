using System.Text.RegularExpressions;
using Keel.Seed;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>Gap wave C in a real browser: a document goes through the same review, step-up signature and release as a part (ADR-0033), with the banners on every page and the strict CSP intact.</summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed partial class GapCTests(E2EFixture fx)
{
    [E2EFact]
    public async Task A_document_is_submitted_signed_with_step_up_and_released_through_its_review_tab()
    {
        await using var session = new BrowserSession(fx, nameof(A_document_is_submitted_signed_with_step_up_and_released_through_its_review_tab));
        await session.RunAsync(async () =>
        {
            var suffix = Guid.NewGuid().ToString("N")[..6];
            // The signers log in first so their logins are stale by the time they sign.
            var ada = await session.SignInAsync("ada");
            var bob = await session.SignInAsync("bob");
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;

            await session.GotoAsync(alice, "/documents/new");
            await BrowserSession.FillAsync(page.GetByLabel("Name *"), $"E2E review drawing {suffix}");
            var pdf = Path.Combine(Path.GetTempPath(), $"e2e-review-{suffix}.pdf");
            await File.WriteAllBytesAsync(pdf, SyntheticPdf.Create("E2E review drawing", ["Dimensions are synthetic."]));
            await page.Locator("input[type=file]").SetInputFilesAsync(pdf);
            await BrowserSession.ClickAsync(page, "Create document");
            await page.WaitForURLAsync(DocumentUrl(), new PageWaitForURLOptions { Timeout = 60_000 });
            var document = page.Url[(page.Url.LastIndexOf('/') + 1)..];

            // The manual "Move to Under Review" button is gone: review is entered through the workflow tab.
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Move to Under Review" })).ToBeDisabledAsync();
            await BrowserSession.OpenTabAsync(page, "Review & release");
            await Expect(page.GetByText("This document has not been submitted for review.")).ToBeVisibleAsync();
            await BrowserSession.ClickAsync(page, "Submit for review");
            await BrowserSession.ChooseParticipantsAsync(page, new() { ["Technical reviewer"] = "ada", ["Approvers"] = "bob" });
            await BrowserSession.ClickAsync(page, "Start review");
            await Expect(page.GetByText("Review in progress")).ToBeVisibleAsync();
            await Expect(page.Locator(".badges")).ToContainTextAsync("Under Review");
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Check out", Exact = true })).ToHaveCountAsync(0);
            await BrowserSession.AssertBannersAsync(page);

            // The task points back at the document, and both signatures need a step-up.
            var adaTask = await session.OpenTaskPathAsync(ada, "Technical review", document);
            await session.SignTaskAsync(ada, adaTask, "Reviewed", expectStepUp: true, comment: "Content checked.");
            var bobTask = await session.OpenTaskPathAsync(bob, "Approval", document);
            await session.SignTaskAsync(bob, bobTask, "Approved", expectStepUp: true);
            await Expect(bob.Page.GetByText("the document is released")).ToBeVisibleAsync();
            await Expect(bob.Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = document }).First).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(bob.Page);

            await session.GotoAsync(alice, "/documents/" + document);
            await Expect(page.Locator(".badges")).ToContainTextAsync("Released");
            await BrowserSession.OpenTabAsync(page, "Review & release");
            await Expect(page.Locator("ul.signatures")).ToContainTextAsync("Signed by Ada Admin (ada)");
            await Expect(page.Locator("ul.signatures")).ToContainTextAsync("Signed by Bob Approver (bob)");
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Check out", Exact = true })).ToHaveCountAsync(0);
            await BrowserSession.OpenTabAsync(page, "Timeline");
            await Expect(page.GetByText("Submitted for review").First).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
        });
    }

    [GeneratedRegex(@"/documents/D-\d+$")]
    private static partial Regex DocumentUrl();
}
