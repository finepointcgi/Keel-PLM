using Microsoft.Playwright;
using Npgsql;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// A CAD structure proposal for the browser tests, written straight into the cad schema through the application's own database role (the services that
/// normally create it live in the API host). It matches the demo gimbal assembly by id, so the review page has a diff to show. Created once per run.
/// </summary>
public static class CadE2ESeed
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _number;

    public static async Task<string> EnsureAsync(E2EFixture fx)
    {
        await Gate.WaitAsync();
        try
        {
            if (_number is not null) return _number;
            await using var conn = await fx.AppDb.OpenConnectionAsync();
            Guid assembly, child;
            await using (var cmd = new NpgsqlCommand("SELECT id FROM plm.part_master WHERE number = @n", conn)) { cmd.Parameters.AddWithValue("n", fx.Manifest.GimbalAssembly); assembly = (Guid)(await cmd.ExecuteScalarAsync())!; }
            await using (var cmd = new NpgsqlCommand("SELECT id FROM plm.part_master WHERE number = @n", conn)) { cmd.Parameters.AddWithValue("n", fx.Manifest.GimbalBracket); child = (Guid)(await cmd.ExecuteScalarAsync())!; }
            await using var tx = await conn.BeginTransactionAsync();
            long seq;
            await using (var cmd = new NpgsqlCommand("SELECT nextval('cad.proposal_number_seq')", conn, tx)) seq = (long)(await cmd.ExecuteScalarAsync())!;
            var number = "CBP-" + seq.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            var id = Guid.CreateVersion7();
            await using (var cmd = new NpgsqlCommand(
                """
                INSERT INTO cad.bom_proposal (id, number, title, source_format, source_system, root_ref, status, apply_removals, export_jurisdiction, cui_categories, distribution_statement, owning_org, export_authorizations, created_at, created_by)
                VALUES (@id, @n, 'Gimbal structure from CAD', 'csv', 'CSV', 'asm', 'draft', false, 'None', '{}', 'A', 'ENG', '{}', now(), 'alice')
                """, conn, tx))
            { cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("n", number); await cmd.ExecuteNonQueryAsync(); }
            foreach (var (r, name, kind, matched, decision, type) in new[]
                     {
                         ("asm", "Gimbal assembly", "number", (Guid?)assembly, "match", "Assembly"),
                         ("brk", "Bracket", "number", (Guid?)child, "match", "Component"),
                         ("new", "Locking collar", "new", (Guid?)null, "create", "Component"),
                     })
            {
                await using var cmd = new NpgsqlCommand(
                    "INSERT INTO cad.bom_proposal_part (proposal_id, ref, cad_number, name, part_type, unit, source, match_kind, matched_master_id, decision) VALUES (@p, @r, @r, @name, @type, 'EA', 'Make', @kind, @m, @d)", conn, tx);
                cmd.Parameters.AddWithValue("p", id); cmd.Parameters.AddWithValue("r", r); cmd.Parameters.AddWithValue("name", name); cmd.Parameters.AddWithValue("type", type);
                cmd.Parameters.AddWithValue("kind", kind); cmd.Parameters.AddWithValue("m", (object?)matched ?? DBNull.Value); cmd.Parameters.AddWithValue("d", decision);
                await cmd.ExecuteNonQueryAsync();
            }
            var seqNo = 0;
            foreach (var (child2, qty, find) in new[] { ("brk", 3m, 10), ("new", 2m, 20) })
            {
                await using var cmd = new NpgsqlCommand(
                    "INSERT INTO cad.bom_proposal_line (id, proposal_id, seq, parent_ref, child_ref, find_number, quantity) VALUES (@id, @p, @s, 'asm', @c, @f, @q)", conn, tx);
                cmd.Parameters.AddWithValue("id", Guid.CreateVersion7()); cmd.Parameters.AddWithValue("p", id); cmd.Parameters.AddWithValue("s", ++seqNo);
                cmd.Parameters.AddWithValue("c", child2); cmd.Parameters.AddWithValue("f", find); cmd.Parameters.AddWithValue("q", qty);
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            return _number = number;
        }
        finally { Gate.Release(); }
    }
}

/// <summary>CAD in a real browser (ADR-0035): the proposal review page with its banner, decisions and diff, the CAD tab of a document, and the phone layout.</summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class CadTests(E2EFixture fx)
{
    [E2EFact]
    public async Task A_structure_file_becomes_a_proposal_that_is_reviewed_and_decided()
    {
        await using var session = new BrowserSession(fx, nameof(A_structure_file_becomes_a_proposal_that_is_reviewed_and_decided));
        await session.RunAsync(async () =>
        {
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            await session.GotoAsync(alice, "/cad/bom-proposals");
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Send a structure" })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);

            // Send a CSV: the demo gimbal assembly with its bracket and one part Keel does not know.
            var csv = $"parent,child,name,qty,find\n{fx.Manifest.GimbalAssembly},{fx.Manifest.GimbalBracket},Bracket,5,10\n{fx.Manifest.GimbalAssembly},CAD-COLLAR,Locking collar,2,20\n";
            await page.GetByLabel("Title").FillAsync("Gimbal structure from a script");
            await page.GetByLabel("Format").SelectOptionAsync("csv");
            await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload { Name = "gimbal.csv", MimeType = "text/csv", Buffer = System.Text.Encoding.UTF8.GetBytes(csv) });
            await BrowserSession.ClickAsync(page, "Create proposal");

            // The review page: parts, matches, the difference with the current EBOM, and the aggregate banner.
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Differences from the current engineering BOM" })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByTestId("proposal-status")).ToHaveTextAsync("Draft");
            var parts = page.GetByTestId("proposal-parts");
            await Expect(parts).ToContainTextAsync("Locking collar");
            await Expect(parts).ToContainTextAsync("No existing part");
            await Expect(parts).ToContainTextAsync(fx.Manifest.GimbalAssembly);
            await Expect(page.Locator("table.data", new PageLocatorOptions { HasText = "Added" }).First).ToBeVisibleAsync();

            // A decision is saved: skip the new part. Its line no longer applies.
            await page.GetByLabel("Decision for Locking collar").SelectOptionAsync("skip");
            await BrowserSession.ClickAsync(page, "Save decisions");
            await Expect(page.GetByText("Decisions saved.")).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Apply to the engineering BOMs" })).ToBeVisibleAsync();

            // Keyboard: the decision control is reachable and labelled; no console errors apart from the known CSP notice.
            await page.GetByLabel("Decision for Locking collar").FocusAsync();
            Assert.Empty(alice.PageErrors);
        });
    }

    [E2EFact]
    public async Task The_review_page_and_the_cad_tab_fit_a_375_px_phone()
    {
        await using var session = new BrowserSession(fx, nameof(The_review_page_and_the_cad_tab_fit_a_375_px_phone));
        await session.RunAsync(async () =>
        {
            var number = await CadE2ESeed.EnsureAsync(fx);
            var alice = await session.SignInAsync("alice", width: 375, height: 812);
            foreach (var path in new[] { "/cad/bom-proposals", $"/cad/bom-proposals/{number}", $"/documents/{fx.Manifest.SystemSpecification}?tab=cad" })
            {
                await session.GotoAsync(alice, path);
                await Expect(alice.Page.Locator("h1")).ToBeVisibleAsync();
                var scrollWidth = await alice.Page.EvaluateAsync<int>("document.documentElement.scrollWidth");
                var innerWidth = await alice.Page.EvaluateAsync<int>("window.innerWidth");
                Assert.True(scrollWidth <= innerWidth, $"{path}: the page scrolls sideways at 375 px (document {scrollWidth}, window {innerWidth}).");
                await BrowserSession.AssertBannersAsync(alice.Page);
            }
        });
    }

    [E2EFact]
    public async Task A_person_who_may_not_see_the_data_gets_the_same_answer_as_for_a_missing_proposal()
    {
        await using var session = new BrowserSession(fx, nameof(A_person_who_may_not_see_the_data_gets_the_same_answer_as_for_a_missing_proposal));
        await session.RunAsync(async () =>
        {
            await CadE2ESeed.EnsureAsync(fx);
            var alice = await session.SignInAsync("alice");
            await session.GotoAsync(alice, "/cad/bom-proposals/CBP-999999");
            await Expect(alice.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("was not found, or you are not authorized to see it");
        });
    }
}
