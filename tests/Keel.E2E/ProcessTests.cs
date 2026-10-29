using Microsoft.Playwright;
using Npgsql;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// Synthetic manufacturing process data (8.7, ADR-0042) for the demo: a work center, a calibrated gauge and a Released process plan with one routing, written with plain SQL as the
/// application role on the released demo MBOM. Idempotent.
/// </summary>
public static class ProcessSeed
{
    public const string Plan = "PP-E2E01";
    public const string Tool = "TL-E2E01";
    public const string WorkCenter = "WC-E2E";

    public static async Task EnsureAsync(E2EFixture fx)
    {
        var mbom = fx.Manifest.Extras?.ReleasedMbom ?? throw new InvalidOperationException("The demo MBOM is missing.");
        await using var conn = await fx.AppDb.OpenConnectionAsync();
        async Task<int> Run(string sql, params (string, object)[] p)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v);
            return await cmd.ExecuteNonQueryAsync();
        }
        await Run("INSERT INTO proc.work_center (id, plant, code, name, resource_kind, is_active, updated_at, updated_by) VALUES (gen_random_uuid(), 'PLANT-A', @c, 'Assembly bench', 'station', true, now(), 'e2e') ON CONFLICT DO NOTHING", ("c", WorkCenter));
        await Run(
            """
            INSERT INTO proc.tool (id, number, name, kind, plant, requires_calibration, interval_days, last_calibrated_on, due_on, export_jurisdiction, cui_categories, distribution_statement, owning_org, export_authorizations, created_at, created_by)
            VALUES (gen_random_uuid(), @n, 'Torque screwdriver', 'gauge', 'PLANT-A', true, 180, current_date - 20, current_date + 160, 'None', '{}', 'A', 'ENG', '{}', now(), 'e2e') ON CONFLICT DO NOTHING
            """, ("n", Tool));
        var content =
            """
            {"routings":[{"code":"PRIMARY","name":"Primary routing","isPrimary":true,"condition":null,"note":null,"operations":[
              {"id":"11111111-1111-4111-8111-111111111111","number":10,"name":"Fasten the cover","workCenter":"WC-E2E","setupMinutes":5,"runMinutes":1.5,"condition":null,"inspection":true,"inspectionText":"Check the torque mark","components":[],"tools":[],
               "steps":[{"id":"22222222-2222-4222-8222-222222222222","kind":"warning","text":"Disconnect the supply before you start.","tools":[],"attachments":[]},
                        {"id":"33333333-3333-4333-8333-333333333333","kind":"step","text":"Tighten the six screws in a star pattern.","tools":[],"attachments":[]},
                        {"id":"44444444-4444-4444-8444-444444444444","kind":"quality","text":"Record the torque in the traveler.","tools":[],"attachments":[]}]},
              {"id":"55555555-5555-4555-8555-555555555555","number":20,"name":"Final check","workCenter":null,"setupMinutes":null,"runMinutes":2,"condition":null,"inspection":false,"inspectionText":null,"components":[],"tools":[],"steps":[]}]}]}
            """;
        var inserted = await Run(
            """
            INSERT INTO proc.plan (id, number, mbom_id, mbom_number, part_number, part_name, plant, description, export_jurisdiction, eccn, usml_category, cui_categories, distribution_statement, controlling_office, distribution_reason, program, owning_org, export_authorizations, created_at, created_by)
            SELECT gen_random_uuid(), @plan, mm.id, mm.number, pm.number, pm.name, mm.plant, 'Synthetic plan', mm.export_jurisdiction, mm.eccn, mm.usml_category, mm.cui_categories, mm.distribution_statement, mm.controlling_office, mm.distribution_reason, mm.program, mm.owning_org, mm.export_authorizations, now(), 'e2e'
            FROM mbom.mbom mm JOIN plm.part_master pm ON pm.id = mm.part_master_id WHERE mm.number = @mbom ON CONFLICT DO NOTHING
            """, ("plan", Plan), ("mbom", mbom));
        if (inserted == 0) return;
        await Run(
            """
            INSERT INTO proc.plan_revision (id, plan_id, revision, lifecycle_state, state_changed_at, state_changed_by, is_latest, latest_iteration, bound_mbom_revision, bound_mbom_sha, bound_at, bound_by, created_at, created_by)
            SELECT gen_random_uuid(), p.id, 'A', 'InWork', now(), 'e2e', true, 1, r.revision, i.content_sha256, now(), 'e2e', now(), 'e2e'
            FROM proc.plan p JOIN mbom.mbom mm ON mm.id = p.mbom_id JOIN mbom.revision r ON r.mbom_id = mm.id AND r.lifecycle_state = 'Released' JOIN mbom.iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            WHERE p.number = @plan ORDER BY length(r.revision) DESC, r.revision DESC LIMIT 1
            """, ("plan", Plan));
        await Run(
            """
            INSERT INTO proc.plan_iteration (id, revision_id, iteration, is_open, content, content_sha256, comment, created_at, created_by)
            SELECT gen_random_uuid(), r.id, 1, false, @content::jsonb, repeat('a', 64), 'Seed', now(), 'e2e' FROM proc.plan_revision r JOIN proc.plan p ON p.id = r.plan_id WHERE p.number = @plan
            """, ("plan", Plan), ("content", content));
        await Run("UPDATE proc.plan_revision SET lifecycle_state = 'UnderReview' WHERE plan_id = (SELECT id FROM proc.plan WHERE number = @plan)", ("plan", Plan));
        await Run("UPDATE proc.plan_revision SET lifecycle_state = 'Released' WHERE plan_id = (SELECT id FROM proc.plan WHERE number = @plan)", ("plan", Plan));
    }
}

/// <summary>Process plans as people use them (ADR-0042): the list, the plan, the flow diagram and its table, the matrix, the shop floor view with its print layout, a revision edited and checked in, and phone width.</summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class ProcessTests(E2EFixture fx)
{
    [E2EFact]
    public async Task Alice_reads_a_plan_its_flow_and_the_shop_floor_view_which_prints_with_banners_and_fits_a_phone()
    {
        await using var session = new BrowserSession(fx, nameof(Alice_reads_a_plan_its_flow_and_the_shop_floor_view_which_prints_with_banners_and_fits_a_phone));
        await session.RunAsync(async () =>
        {
            await ProcessSeed.EnsureAsync(fx);
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/process/plans");
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = ProcessSeed.Plan, Exact = true })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);

            await session.GotoAsync(alice, $"/process/plans/{ProcessSeed.Plan}");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByText("Written against", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
            await BrowserSession.OpenTabAsync(page, "Routings and instructions");
            await Expect(page.GetByText("Fasten the cover").First).ToBeVisibleAsync();
            await Expect(page.GetByText("Warning (safety):")).ToBeVisibleAsync();
            await BrowserSession.OpenTabAsync(page, "Flow");
            await Expect(page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Routing flow" }).First).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Table", Exact = true }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Table, new PageGetByRoleOptions { Name = "Routing flow as a table" })).ToBeVisibleAsync();
            await BrowserSession.OpenTabAsync(page, "Components");
            await Expect(page.GetByRole(AriaRole.Table, new PageGetByRoleOptions { Name = "Components by operation" })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);

            await session.GotoAsync(alice, $"/process/plans/{ProcessSeed.Plan}/shop-floor");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByText("Disconnect the supply before you start.")).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Print this view" })).ToBeVisibleAsync();
            // The print layout keeps the marking banners (repeated by the stylesheet) and drops the application chrome.
            await page.EmulateMediaAsync(new PageEmulateMediaOptions { Media = Media.Print });
            await Expect(page.Locator(".sf-print-top")).ToBeVisibleAsync();
            await Expect(page.Locator(".sf-print-bottom")).ToBeVisibleAsync();
            Assert.Equal((await page.Locator(".sf-print-top").InnerTextAsync()).Trim(), (await BrowserSession.TopBanner(page).InnerTextAsync()).Trim());
            await Expect(page.Locator(".topbar")).ToBeHiddenAsync();
            await page.EmulateMediaAsync(new PageEmulateMediaOptions { Media = Media.Screen });

            await page.SetViewportSizeAsync(375, 800);
            foreach (var path in new[] { "/process/plans", $"/process/plans/{ProcessSeed.Plan}", $"/process/plans/{ProcessSeed.Plan}/shop-floor", "/process/tools" })
            {
                await session.GotoAsync(alice, path);
                await BrowserSession.AssertBannersAsync(page);
                Assert.False(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth + 1"), $"no horizontal page scroll at 375 px on {path}");
            }
        });
    }

    [E2EFact]
    public async Task Alice_starts_a_revision_edits_the_working_copy_and_checks_it_in_and_the_tool_page_shows_calibration()
    {
        await using var session = new BrowserSession(fx, nameof(Alice_starts_a_revision_edits_the_working_copy_and_checks_it_in_and_the_tool_page_shows_calibration));
        await session.RunAsync(async () =>
        {
            await ProcessSeed.EnsureAsync(fx);
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, $"/process/plans/{ProcessSeed.Plan}");
            await BrowserSession.ClickAsync(page, "Start a new revision");
            await Expect(page.GetByText("checked out by")).ToBeVisibleAsync();
            await BrowserSession.OpenTabAsync(page, "Routings and instructions");
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Edit the working copy" })).ToBeVisibleAsync();
            await BrowserSession.ClickAsync(page, "Add an operation");
            await page.GetByLabel("Description *").Last.FillAsync("Label the unit");
            await BrowserSession.ClickAsync(page, "Save the working copy");
            await Expect(page.GetByText("Saved.")).ToBeVisibleAsync();
            await BrowserSession.OpenTabAsync(page, "Overview");
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Findings" })).ToBeVisibleAsync();
            await BrowserSession.ClickAsync(page, "Check in");
            await Expect(page.GetByText("Checked out")).ToHaveCountAsync(0);
            await BrowserSession.AssertBannersAsync(page);

            await session.GotoAsync(alice, $"/process/tools/{ProcessSeed.Tool}");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByText("In calibration").First).ToBeVisibleAsync();
            await BrowserSession.OpenTabAsync(page, "Calibration");
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Calibration history" })).ToBeVisibleAsync();
            await session.GotoAsync(alice, "/process/plans/PP-999999");
            await Expect(page.GetByText("was not found")).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
        });
    }
}
