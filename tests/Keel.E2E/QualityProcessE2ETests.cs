using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>The numbers of the quality records the browser tests create through the pages themselves (once per run).</summary>
public sealed record QualityData(string Ncr, string Capa, string Deviation, string Part);

/// <summary>Creates a nonconformance report, a corrective action and a deviation as alice through the real pages, once per run, for every test that needs them.</summary>
public static class QualityRecords
{
    private static readonly ConditionalWeakTable<E2EFixture, Task<QualityData>> Cache = [];
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<QualityData> EnsureAsync(E2EFixture fx, BrowserSession session, UserPage alice)
    {
        await Gate.WaitAsync();
        try
        {
            if (!Cache.TryGetValue(fx, out var task))
            {
                task = CreateAsync(fx, session, alice);
                Cache.Add(fx, task);
            }
            return await task;
        }
        finally { Gate.Release(); }
    }

    private static async Task<QualityData> CreateAsync(E2EFixture fx, BrowserSession session, UserPage alice)
    {
        var page = alice.Page;
        var part = fx.Manifest.GimbalBracket;

        await session.GotoAsync(alice, "/quality/new/ncr");
        await BrowserSession.FillAsync(page.GetByLabel("Title *"), "Burr on the mounting face");
        await BrowserSession.FillAsync(page.GetByLabel("Part number *"), part);
        await page.GetByLabel("Quantity affected *").FillAsync("4");
        await BrowserSession.FillAsync(page.GetByLabel("Lot number"), "LOT-42");
        await BrowserSession.FillAsync(page.GetByLabel("Defect description"), "Synthetic defect found at incoming inspection.");
        await page.GetByLabel("Defect description").BlurAsync(); // the part's markings were taken when the part number lost focus
        await BrowserSession.ClickAsync(page, "Create");
        await page.WaitForURLAsync(new Regex(@"/quality/ncrs/NCR-\d+$"), new PageWaitForURLOptions { Timeout = 60_000 });
        var ncr = page.Url[(page.Url.LastIndexOf('/') + 1)..];

        await session.GotoAsync(alice, "/quality/new/capa");
        await BrowserSession.FillAsync(page.GetByLabel("Title *"), "Recurring burr at incoming inspection");
        await BrowserSession.FillAsync(page.GetByLabel("Problem statement"), "Synthetic problem statement.");
        await BrowserSession.ClickAsync(page, "Create");
        await page.WaitForURLAsync(new Regex(@"/quality/capas/CAPA-\d+$"), new PageWaitForURLOptions { Timeout = 60_000 });
        var capa = page.Url[(page.Url.LastIndexOf('/') + 1)..];

        await session.GotoAsync(alice, "/quality/new/deviation");
        await BrowserSession.FillAsync(page.GetByLabel("Title *"), "Alternate surface finish");
        await BrowserSession.FillAsync(page.GetByLabel("Justification"), "Synthetic justification.");
        await BrowserSession.ClickAsync(page, "Create");
        await page.WaitForURLAsync(new Regex(@"/quality/deviations/DEV-\d+$"), new PageWaitForURLOptions { Timeout = 60_000 });
        var deviation = page.Url[(page.Url.LastIndexOf('/') + 1)..];
        await BrowserSession.OpenTabAsync(page, "Scope");
        await BrowserSession.FillAsync(page.GetByLabel("Part number *"), part);
        await BrowserSession.ClickAsync(page, "Add to the scope");
        await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = part })).ToBeVisibleAsync();
        return new QualityData(ncr, capa, deviation, part);
    }
}

/// <summary>Quality processes in a real browser (8.1, ADR-0036): the pages, a signed disposition with step-up, a deviation shown on the part and the phone layout.</summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class QualityProcessE2ETests(E2EFixture fx)
{
    [E2EFact]
    public async Task An_NCR_is_reported_contained_signed_by_the_review_board_and_closed()
    {
        await using var session = new BrowserSession(fx, nameof(An_NCR_is_reported_contained_signed_by_the_review_board_and_closed));
        await session.RunAsync(async () =>
        {
            // The signers log in first so their logins are stale by the time they sign.
            var ada = await session.SignInAsync("ada");
            var bob = await session.SignInAsync("bob");
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            var data = await QualityRecords.EnsureAsync(fx, session, alice);

            await session.GotoAsync(alice, "/quality/ncrs/" + data.Ncr);
            await Expect(page.Locator(".badges")).ToContainTextAsync("Open");
            await Expect(page.GetByText("Synthetic defect found at incoming inspection.")).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);

            // containment: ada owns it
            await BrowserSession.OpenTabAsync(page, "Containment");
            await BrowserSession.FillAsync(page.GetByLabel("Owner *"), "ada");
            await BrowserSession.FillAsync(page.GetByLabel("What has to be done *"), "Quarantine lot LOT-42 and sort it.");
            await BrowserSession.ClickAsync(page, "Add action item");
            await Expect(page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions { Name = "ACT-" }).First).ToBeVisibleAsync();

            // the disposition is proposed, then the board signs it
            await BrowserSession.OpenTabAsync(page, "Disposition");
            await page.Locator("#tabpanel-disposition select").SelectOptionAsync("Rework");
            await BrowserSession.FillAsync(page.GetByLabel("Rationale *"), "Deburr within the drawing limits.");
            await BrowserSession.ClickAsync(page, "Save proposal");
            await Expect(page.GetByText("Deburr within the drawing limits.").First).ToBeVisibleAsync();
            await BrowserSession.OpenTabAsync(page, "Review");
            await BrowserSession.ClickAsync(page, "Submit to the review board");
            await BrowserSession.ChooseParticipantsAsync(page, new() { ["Quality reviewer"] = "ada", ["MRB members"] = "bob" });
            await BrowserSession.ClickAsync(page, "Start review");
            await Expect(page.GetByText("Review in progress")).ToBeVisibleAsync();
            await Expect(page.Locator(".badges")).ToContainTextAsync("MRB review");
            await BrowserSession.AssertBannersAsync(page);

            var adaTask = await session.OpenTaskPathAsync(ada, "Quality review", data.Ncr);
            await session.SignTaskAsync(ada, adaTask, "Reviewed", expectStepUp: true, comment: "Report checked.");
            var bobTask = await session.OpenTaskPathAsync(bob, "MRB approval", data.Ncr);
            await session.SignTaskAsync(bob, bobTask, "Approved", expectStepUp: true);
            await BrowserSession.AssertBannersAsync(bob.Page);

            await session.GotoAsync(alice, "/quality/ncrs/" + data.Ncr);
            await Expect(page.Locator(".badges")).ToContainTextAsync("Dispositioned");
            await BrowserSession.OpenTabAsync(page, "Review");
            await Expect(page.Locator("ul.signatures")).ToContainTextAsync("Signed by Bob Approver (bob)");
            await BrowserSession.OpenTabAsync(page, "Activity");
            await Expect(page.GetByText("created the nonconformance report").First).ToBeVisibleAsync();

            // the owner finishes the containment, then the report is closed
            await session.GotoAsync(ada, "/quality/ncrs/" + data.Ncr + "?tab=actions");
            await BrowserSession.ClickAsync(ada.Page, "Start");
            await ada.Page.GetByLabel("Note for ACT-", new PageGetByLabelOptions { Exact = false }).FillAsync("Lot quarantined and sorted.");
            await BrowserSession.ClickAsync(ada.Page, "Complete");
            await Expect(ada.Page.GetByText("Lot quarantined and sorted.").First).ToBeVisibleAsync();
            await session.GotoAsync(alice, "/quality/ncrs/" + data.Ncr);
            await BrowserSession.ClickAsync(page, "Close the report");
            await BrowserSession.FillAsync(page.GetByLabel("How was the disposition carried out? *"), "Reworked and re-inspected.");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Close the report" }).Last.ClickAsync();
            await Expect(page.Locator(".badges")).ToContainTextAsync("Closed");
            await BrowserSession.AssertBannersAsync(page);
        });
    }

    [E2EFact]
    public async Task A_deviation_approved_by_signature_shows_on_the_part_and_on_the_home_card()
    {
        await using var session = new BrowserSession(fx, nameof(A_deviation_approved_by_signature_shows_on_the_part_and_on_the_home_card));
        await session.RunAsync(async () =>
        {
            var bob = await session.SignInAsync("bob");
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            var data = await QualityRecords.EnsureAsync(fx, session, alice);
            // a deviation of its own for this test, so the other test's records are untouched
            await session.GotoAsync(alice, "/quality/new/deviation");
            await BrowserSession.FillAsync(page.GetByLabel("Title *"), "Waiver for the part page");
            await BrowserSession.ClickAsync(page, "Create");
            await page.WaitForURLAsync(new Regex(@"/quality/deviations/DEV-\d+$"), new PageWaitForURLOptions { Timeout = 60_000 });
            var number = page.Url[(page.Url.LastIndexOf('/') + 1)..];
            await BrowserSession.OpenTabAsync(page, "Scope");
            await BrowserSession.FillAsync(page.GetByLabel("Part number *"), data.Part);
            await BrowserSession.FillAsync(page.GetByLabel("Listed serial numbers (comma-separated)"), "SN-1, SN-2");
            await BrowserSession.ClickAsync(page, "Add to the scope");
            await Expect(page.GetByText("serials SN-1, SN-2").First).ToBeVisibleAsync();
            await BrowserSession.OpenTabAsync(page, "Review");
            await BrowserSession.ClickAsync(page, "Submit for approval");
            await BrowserSession.ChooseParticipantsAsync(page, new() { ["Approvers"] = "bob" });
            await BrowserSession.ClickAsync(page, "Start review");
            await Expect(page.GetByText("Review in progress")).ToBeVisibleAsync();

            var task = await session.OpenTaskPathAsync(bob, "Approval", number);
            await session.SignTaskAsync(bob, task, "Approved", expectStepUp: true);

            await session.GotoAsync(alice, "/quality/deviations/" + number);
            await Expect(page.Locator(".badges")).ToContainTextAsync("Active now");
            await session.GotoAsync(alice, $"/parts/{data.Part}?tab=quality");
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = number })).ToBeVisibleAsync();
            await Expect(page.GetByText("Active now").First).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);

            await session.GotoAsync(alice, "/");
            var card = page.Locator("section[aria-labelledby='card-quality']");
            await Expect(card).ToBeVisibleAsync();
            await Expect(card.Locator("[data-count='active-deviations']")).ToHaveTextAsync(new Regex("^[1-9]"));
            await Expect(card.Locator("[data-count='open-ncrs']")).ToHaveTextAsync(new Regex("^[1-9]"));
        });
    }

    [E2EFact]
    public async Task The_quality_pages_fit_a_phone_and_keep_their_banners()
    {
        await using var session = new BrowserSession(fx, nameof(The_quality_pages_fit_a_phone_and_keep_their_banners));
        await session.RunAsync(async () =>
        {
            var desktop = await session.SignInAsync("alice");
            var data = await QualityRecords.EnsureAsync(fx, session, desktop);
            var phone = await session.SignInAsync("alice", width: 375, height: 812);
            foreach (var path in new[]
            {
                "/quality/ncrs", "/quality/capas", "/quality/deviations", "/quality/new/ncr", "/quality/new/capa", "/quality/new/deviation",
                $"/quality/ncrs/{data.Ncr}", $"/quality/ncrs/{data.Ncr}?tab=disposition", $"/quality/ncrs/{data.Ncr}?tab=actions", $"/quality/capas/{data.Capa}", $"/quality/capas/{data.Capa}?tab=analysis",
                $"/quality/capas/{data.Capa}?tab=verification", $"/quality/deviations/{data.Deviation}", $"/quality/deviations/{data.Deviation}?tab=scope", $"/parts/{data.Part}?tab=quality", "/",
            })
            {
                await session.GotoAsync(phone, path);
                await AssertBannersSettledAsync(phone.Page, path);
                Assert.True(await phone.Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"), $"{path} scrolls sideways at 375 px");
            }
            Assert.Empty(phone.PageErrors);
        });
    }

    /// <summary>The banners are the same top and bottom once the page has finished loading its cards (a card that loads later may raise the banner).</summary>
    private static async Task AssertBannersSettledAsync(IPage page, string path)
    {
        for (var i = 0; i < 40; i++)
        {
            var top = (await BrowserSession.TopBanner(page).InnerTextAsync()).Trim();
            var bottom = (await BrowserSession.BottomBanner(page).InnerTextAsync()).Trim();
            if (top.Length > 0 && top == bottom && !(i < 8 && top == "UNCLASSIFIED")) return;
            await Task.Delay(250);
        }
        await BrowserSession.AssertBannersAsync(page);
        _ = path;
    }

    [E2EFact]
    public async Task A_corrective_action_records_its_analysis_actions_and_verification_plan()
    {
        await using var session = new BrowserSession(fx, nameof(A_corrective_action_records_its_analysis_actions_and_verification_plan));
        await session.RunAsync(async () =>
        {
            var bob = await session.SignInAsync("bob");
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/quality/new/capa");
            await BrowserSession.FillAsync(page.GetByLabel("Title *"), "Analysis walk-through");
            await BrowserSession.ClickAsync(page, "Create");
            await page.WaitForURLAsync(new Regex(@"/quality/capas/CAPA-\d+$"), new PageWaitForURLOptions { Timeout = 60_000 });
            var number = page.Url[(page.Url.LastIndexOf('/') + 1)..];

            await BrowserSession.OpenTabAsync(page, "Analysis");
            await BrowserSession.ClickAsync(page, "Edit the analysis");
            await BrowserSession.FillAsync(page.GetByLabel("Why 1?"), "The tool was worn.");
            await BrowserSession.FillAsync(page.GetByLabel("Root cause statement"), "Tool life was not tracked.");
            await BrowserSession.ClickAsync(page, "Save analysis");
            await Expect(page.GetByText("The tool was worn.")).ToBeVisibleAsync();

            await BrowserSession.OpenTabAsync(page, "Actions");
            await BrowserSession.FillAsync(page.GetByLabel("Owner *"), "bob");
            await BrowserSession.FillAsync(page.GetByLabel("What has to be done *"), "Track tool life in the work instruction.");
            await BrowserSession.ClickAsync(page, "Add action item");
            await Expect(page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions { Name = "ACT-" }).First).ToBeVisibleAsync();

            await BrowserSession.OpenTabAsync(page, "Verification");
            await BrowserSession.FillAsync(page.GetByLabel("Method *"), "Inspect the next three lots.");
            await BrowserSession.FillAsync(page.GetByLabel("Success criteria *"), "No burr in three lots.");
            await BrowserSession.ClickAsync(page, "Save plan");
            await Expect(page.GetByText("Inspect the next three lots.").First).ToBeVisibleAsync();

            await BrowserSession.OpenTabAsync(page, "Overview");
            await BrowserSession.ClickAsync(page, "Start the work");
            await Expect(page.Locator(".badges")).ToContainTextAsync("In progress");
            await BrowserSession.OpenTabAsync(page, "Review");
            await Expect(page.GetByText("Closing needs signed verification")).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
            Assert.Equal(number, page.Url[(page.Url.LastIndexOf('/') + 1)..].Split('#')[0].Split('?')[0]);
            Assert.Empty(alice.PageErrors);
            _ = bob;
        });
    }
}
