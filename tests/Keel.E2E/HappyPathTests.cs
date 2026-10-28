using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Seed;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// The Phase 1 definition of done through the browser, as different people: alice authors an ITAR part with a document and a BOM and submits
/// it; ada reviews and bob approves, both signing after a Keycloak step-up; the part is released; alice changes it with an ECN whose impact
/// panel shows where it is used; ada and bob release the ECN (revision B Released, A Obsolete); audrey verifies the audit chain.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed partial class HappyPathTests(E2EFixture fx)
{
    [E2EFact]
    public async Task Part_document_BOM_review_with_step_up_release_ECN_impact_and_audit_through_the_browser()
    {
        await using var session = new BrowserSession(fx, nameof(Part_document_BOM_review_with_step_up_release_ECN_impact_and_audit_through_the_browser));
        await session.RunAsync(async () =>
        {
            var suffix = Guid.NewGuid().ToString("N")[..6];
            var m = fx.Manifest;

            // The signers log in first: their logins age while alice works, so the first signature of each needs a step-up.
            var ada = await session.SignInAsync("ada");
            var bob = await session.SignInAsync("bob");
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;

            // ---- 1. an ITAR part with an attribute (the default markings of the form are ITAR USML XI(d), program ORCA)
            var partName = $"E2E bracket {suffix}";
            var part = await CreatePartAsync(session, alice, partName, "Component", material: "7075-T6");
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = partName, Level = 2 })).ToBeVisibleAsync();
            await Expect(BrowserSession.TopBanner(page)).ToHaveTextAsync("CUI//EXPT · ITAR · DISTRIBUTION D");
            await Expect(page.Locator("dl.facts").First).ToContainTextAsync("7075-T6");
            await BrowserSession.AssertBannersAsync(page);

            // ---- 2. a document uploaded and linked to the part
            await session.GotoAsync(alice, "/documents/new");
            await BrowserSession.FillAsync(page.GetByLabel("Name *"), $"E2E bracket drawing {suffix}");
            var pdf = Path.Combine(Path.GetTempPath(), $"e2e-drawing-{suffix}.pdf");
            await File.WriteAllBytesAsync(pdf, SyntheticPdf.Create("E2E bracket drawing", ["Dimensions are synthetic.", "Material 7075-T6."]));
            await page.Locator("input[type=file]").SetInputFilesAsync(pdf);
            await BrowserSession.ClickAsync(page, "Create document");
            await page.WaitForURLAsync(DocumentUrl(), new PageWaitForURLOptions { Timeout = 60_000 });
            var document = page.Url[(page.Url.LastIndexOf('/') + 1)..];
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Download (marked)" })).ToBeVisibleAsync();

            await session.GotoAsync(alice, "/parts/" + part);
            await BrowserSession.ClickAsync(page, "Check out");
            await Expect(page.GetByText("You have this part checked out")).ToBeVisibleAsync();
            await BrowserSession.FillAsync(page.GetByLabel("Document number"), document);
            await BrowserSession.ClickAsync(page, "Add link");
            await Expect(page.GetByLabel("Document number")).ToHaveValueAsync("");
            await BrowserSession.FillAsync(page.GetByLabel("Check-in comment"), "Drawing linked");
            await BrowserSession.ClickAsync(page, "Check in");
            await Expect(page.Locator(".badges")).ToContainTextAsync("Version A.2");
            await Expect(page.Locator("#linked-docs-heading").Locator("xpath=..")).ToContainTextAsync(document);

            // ---- 3. a two-level BOM: the child (the part above) already exists; a new assembly uses it
            var assemblyName = $"E2E sensor head {suffix}";
            var assembly = await CreatePartAsync(session, alice, assemblyName, "Assembly", material: "Assembly");
            await BrowserSession.ClickAsync(page, "Check out");
            await Expect(page.GetByText("You have this part checked out")).ToBeVisibleAsync();
            await AddBomLineAsync(page, part, "2");
            await AddBomLineAsync(page, m.OpenFastener, "8");
            await BrowserSession.FillAsync(page.GetByLabel("Check-in comment"), "Structure");
            await BrowserSession.ClickAsync(page, "Check in");
            await Expect(page.Locator(".badges")).ToContainTextAsync("Version A.2");
            await Expect(page.Locator(".bom-grid").GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = part })).ToBeVisibleAsync();
            await Expect(page.Locator(".bom-grid").GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = m.OpenFastener })).ToBeVisibleAsync();

            // ---- 4. submit the part for review (reviewer ada, approver bob)
            await session.GotoAsync(alice, "/parts/" + part);
            await BrowserSession.ClickAsync(page, "Submit for review");
            await BrowserSession.ChooseParticipantsAsync(page, new() { ["Technical reviewer"] = "ada", ["Approvers"] = "bob" });
            await BrowserSession.ClickAsync(page, "Start review");
            await Expect(page.GetByText("Review in progress")).ToBeVisibleAsync();
            await Expect(page.Locator(".badges")).ToContainTextAsync("Under Review");
            // The release lock: while under review the part cannot be checked out.
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Check out", Exact = true })).ToHaveCountAsync(0);

            // ---- 5. ada signs the technical review: her login is older than the window, so Keycloak asks for her password again
            var adaTask = await session.OpenTaskPathAsync(ada, "Technical review", part);
            await session.SignTaskAsync(ada, adaTask, "Reviewed", expectStepUp: true, comment: "Dimensions checked.");
            // ---- 6. bob signs the approval the same way; the part is released
            var bobTask = await session.OpenTaskPathAsync(bob, "Approval", part);
            await session.SignTaskAsync(bob, bobTask, "Approved", expectStepUp: true);
            await Expect(bob.Page.GetByText("the part is released")).ToBeVisibleAsync();

            await session.GotoAsync(alice, "/parts/" + part);
            await Expect(page.Locator(".badges")).ToContainTextAsync("Released");
            await Expect(page.Locator("ul.signatures")).ToContainTextAsync("Signed by Ada Admin (ada)");
            await Expect(page.Locator("ul.signatures")).ToContainTextAsync("Signed by Bob Approver (bob)");

            // ---- 7. an ECN that revises the released part, with the impact panel
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Request change" }).ClickAsync();
            await BrowserSession.FillAsync(page.GetByLabel("Title *"), $"Thicker wall {suffix}");
            await BrowserSession.FillAsync(page.GetByLabel("Description"), "Increase wall thickness after a vibration test.");
            await BrowserSession.ClickAsync(page, "Create change notice");
            await page.WaitForURLAsync(ChangeUrl());
            var ecn = page.Url[(page.Url.LastIndexOf('/') + 1)..];
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = ecn, Level = 2 })).ToBeVisibleAsync();
            await Expect(page.Locator(".badges")).ToContainTextAsync("Class II");
            await BrowserSession.ClickAsync(page, "Run impact analysis");
            var impact = page.Locator("section[aria-labelledby='impact-heading']");
            await Expect(impact.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = assembly })).ToBeVisibleAsync();
            await Expect(impact).ToContainTextAsync("1 place used");
            await Expect(impact).ToContainTextAsync("1 linked document");
            await Expect(impact.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = document })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);

            // revision B is in work; alice edits and checks it in
            await session.GotoAsync(alice, "/parts/" + part);
            await Expect(page.Locator(".badges")).ToContainTextAsync("In Work");
            await Expect(page.GetByText($"Revision B was created by change notice {ecn}")).ToBeVisibleAsync();
            await BrowserSession.ClickAsync(page, "Check out");
            await BrowserSession.FillAsync(page.GetByLabel("Description"), "Wall thickness increased from 2 mm to 3 mm.");
            await BrowserSession.FillAsync(page.GetByLabel("Check-in comment"), "Thicker wall");
            await BrowserSession.ClickAsync(page, "Check in");
            await Expect(page.Locator(".badges")).ToContainTextAsync("Version B.2");

            // submit, review, approve: the notice releases B and obsoletes A in one step
            await session.GotoAsync(alice, "/changes/" + ecn);
            await BrowserSession.ClickAsync(page, "Submit for review");
            await BrowserSession.ChooseParticipantsAsync(page, new() { ["Technical reviewer"] = "ada", ["Approvers"] = "bob" });
            await BrowserSession.ClickAsync(page, "Start review");
            await Expect(page.GetByText("Review in progress")).ToBeVisibleAsync();
            await session.SignTaskAsync(ada, await session.OpenTaskPathAsync(ada, "Technical review", ecn), "Reviewed", expectStepUp: false);
            await session.SignTaskAsync(bob, await session.OpenTaskPathAsync(bob, "Approval", ecn), "Approved", expectStepUp: false);

            await session.GotoAsync(alice, "/changes/" + ecn);
            await Expect(page.Locator(".badges")).ToContainTextAsync("Released");
            await session.GotoAsync(alice, "/parts/" + part);
            await Expect(page.Locator(".badges")).ToContainTextAsync("Released");
            await Expect(page.Locator(".badges")).ToContainTextAsync("Version B.2");
            var revisions = page.Locator("nav[aria-label='Revisions']");
            await Expect(revisions).ToContainTextAsync("Rev B (Released)");
            await Expect(revisions).ToContainTextAsync("Rev A (Obsolete)");

            // ---- 8. the audit log: audrey verifies the chain and sees the steps
            var audrey = await session.SignInAsync("audrey");
            await session.GotoAsync(audrey, "/audit");
            await BrowserSession.ClickAsync(audrey.Page, "Verify chain");
            await Expect(audrey.Page.GetByRole(AriaRole.Status)).ToContainTextAsync("Chain valid:");
            await Expect(audrey.Page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions { Name = "workflow.sign" }).First).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(audrey.Page);

            // every step of this scenario is in the log, in order, attributed to the right people
            var events = await new AuditQueries(fx.AppDb).ListAsync(new AuditQuery(ObjectId: part, Limit: 500));
            string[] inOrder = ["part.create", "part.checkout", "part.link_document", "part.checkin", "workflow.start", "workflow.sign", "workflow.sign", "workflow.complete", "part.checkout", "part.checkin"];
            var cursor = -1;
            var actions = events.Select(e => e.Action).ToList();
            foreach (var expected in inOrder)
            {
                var found = actions.FindIndex(cursor + 1, a => a == expected);
                Assert.True(found > cursor, $"'{expected}' is missing after position {cursor}: {string.Join(", ", actions)}");
                cursor = found;
            }
            Assert.Contains(await new AuditQueries(fx.AppDb).ListAsync(new AuditQuery(ObjectId: ecn, Limit: 500)), e => e.Action == "change.release");
            Assert.Contains(events, e => e.Action == "workflow.sign" && e.Payload.Contains("\"actor\"", StringComparison.Ordinal));
            await using var conn = await fx.AppDb.OpenConnectionAsync();
            var verdict = await AuditVerifier.VerifyAsync(conn, []);
            Assert.True(verdict.Failures.All(f => f.Contains("unknown key", StringComparison.Ordinal)), string.Join("; ", verdict.Failures));
            // The only refusals are the two signatures that needed a step-up (audited as denied, then retried after the new login).
            var denied = events.Where(e => e.Outcome == AuditOutcome.Denied).ToList();
            Assert.Equal(2, denied.Count);
            Assert.All(denied, e => { Assert.Equal("workflow.sign", e.Action); Assert.Contains("stepup.required", e.Payload, StringComparison.Ordinal); });
        });
    }

    private static async Task<string> CreatePartAsync(BrowserSession session, UserPage up, string name, string type, string material)
    {
        var page = up.Page;
        await session.GotoAsync(up, "/parts/new");
        await BrowserSession.FillAsync(page.GetByLabel("Name *"), name);
        await page.Locator("label").Filter(new LocatorFilterOptions { HasTextRegex = new Regex(@"^\s*Type") }).Locator("select").SelectOptionAsync(type);
        await BrowserSession.FillAsync(page.GetByLabel("Material", new PageGetByLabelOptions { Exact = true }), material);
        await BrowserSession.ClickAsync(page, "Create part");
        await page.WaitForURLAsync(PartUrl());
        return page.Url[(page.Url.LastIndexOf('/') + 1)..];
    }

    private static async Task AddBomLineAsync(IPage page, string child, string quantity)
    {
        await BrowserSession.FillAsync(page.GetByLabel("Child part number"), child);
        await BrowserSession.FillAsync(page.GetByLabel("Quantity *"), quantity);
        await BrowserSession.ClickAsync(page, "Add line");
        await Expect(page.Locator("table.bom-edit")).ToContainTextAsync(child);
    }

    [GeneratedRegex(@"/parts/P-\d+$")]
    private static partial Regex PartUrl();

    [GeneratedRegex(@"/documents/D-\d+$")]
    private static partial Regex DocumentUrl();

    [GeneratedRegex(@"/changes/ECN-\d+$")]
    private static partial Regex ChangeUrl();
}
