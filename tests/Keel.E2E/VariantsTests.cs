using Microsoft.Playwright;
using Npgsql;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>Synthetic option model of the demo top assembly (two options, a rule, two released variants), written with plain SQL as the application role. Idempotent.</summary>
public static class VariantsSeed
{
    public const string Left = "Turbo radar";
    public const string Right = "Electric camera";

    public static async Task EnsureAsync(E2EFixture fx)
    {
        await using var conn = await fx.AppDb.OpenConnectionAsync();
        async Task Run(string sql, params (string, object)[] p)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v);
            await cmd.ExecuteNonQueryAsync();
        }
        var top = fx.Manifest.TopAssembly;
        await Run("INSERT INTO var.model (product_master_id, created_at, created_by, updated_at, updated_by) SELECT id, now(), 'e2e', now(), 'e2e' FROM plm.part_master WHERE number = @n ON CONFLICT DO NOTHING", ("n", top));
        foreach (var (fk, fn) in new[] { ("Engine", "Engine"), ("Payload", "Payload") })
            await Run("INSERT INTO var.option_family (id, product_master_id, key, name, created_at, created_by) SELECT gen_random_uuid(), id, @k, @name, now(), 'e2e' FROM plm.part_master WHERE number = @n ON CONFLICT DO NOTHING", ("n", top), ("k", fk), ("name", fn));
        foreach (var (fk, vk, vn) in new[] { ("Engine", "A", "Turbo"), ("Engine", "B", "Electric"), ("Payload", "X", "Camera"), ("Payload", "Y", "Radar") })
            await Run("INSERT INTO var.option_value (id, family_id, key, name, created_at, created_by) SELECT gen_random_uuid(), f.id, @k, @name, now(), 'e2e' FROM var.option_family f JOIN plm.part_master m ON m.id = f.product_master_id WHERE m.number = @n AND f.key = @f ON CONFLICT DO NOTHING",
                ("n", top), ("f", fk), ("k", vk), ("name", vn));
        foreach (var (name, e, p) in new[] { (Left, "A", "Y"), (Right, "B", "X") })
        {
            await Run("INSERT INTO var.variant (id, product_master_id, name, state, created_at, created_by, released_at, released_by) SELECT gen_random_uuid(), id, @v, 'Draft', now(), 'e2e', NULL, NULL FROM plm.part_master WHERE number = @n ON CONFLICT DO NOTHING", ("n", top), ("v", name));
            foreach (var (fk, vk) in new[] { ("Engine", e), ("Payload", p) })
                await Run(
                    """
                    INSERT INTO var.variant_selection (variant_id, family_id, value_id)
                    SELECT v.id, f.id, x.id FROM var.variant v JOIN plm.part_master m ON m.id = v.product_master_id JOIN var.option_family f ON f.product_master_id = m.id AND f.key = @f
                    JOIN var.option_value x ON x.family_id = f.id AND x.key = @k WHERE m.number = @n AND v.name = @v AND NOT EXISTS (SELECT 1 FROM var.variant_selection s WHERE s.variant_id = v.id AND s.family_id = f.id)
                    ON CONFLICT DO NOTHING
                    """, ("n", top), ("v", name), ("f", fk), ("k", vk));
            await Run("UPDATE var.variant SET state = 'Released', released_at = now(), released_by = 'e2e' WHERE name = @v AND state = 'Draft' AND product_master_id = (SELECT id FROM plm.part_master WHERE number = @n)", ("n", top), ("v", name));
        }
    }
}

/// <summary>Options and variants as people use them (ADR-0039): the list, the model, a rule that is refused, a variant picked on the structure, and the pages at 375 px.</summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class VariantsTests(E2EFixture fx)
{
    [E2EFact]
    public async Task Alice_reads_the_options_and_adds_a_rule_and_the_pages_have_their_banners_at_phone_width()
    {
        await using var session = new BrowserSession(fx, nameof(Alice_reads_the_options_and_adds_a_rule_and_the_pages_have_their_banners_at_phone_width));
        await session.RunAsync(async () =>
        {
            await VariantsSeed.EnsureAsync(fx);
            var alice = await session.SignInAsync("alice");
            var page = alice.Page;
            var top = fx.Manifest.TopAssembly;
            await session.GotoAsync(alice, "/options");
            await Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = top, Exact = true })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);

            await session.GotoAsync(alice, $"/options/{top}");
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByText("valid combination").First).ToBeVisibleAsync();
            await BrowserSession.OpenTabAsync(page, "Rules");
            await page.GetByLabel("Option *").SelectOptionAsync("Engine|B");
            await page.GetByLabel("Camera").Last.CheckAsync();
            await BrowserSession.ClickAsync(page, "Add rule");
            await Expect(page.GetByText("Rule added.")).ToBeVisibleAsync();

            // a rule that breaks the model or a released variant is refused in plain words
            await page.GetByLabel("Option *").SelectOptionAsync("Engine|A");
            await page.GetByLabel("Radar").Last.CheckAsync();
            await page.GetByLabel("Kind *").SelectOptionAsync("Excludes");
            await BrowserSession.ClickAsync(page, "Add rule");
            await Expect(page.Locator("p.error")).ToContainTextAsync(new System.Text.RegularExpressions.Regex("inconsistent|released variant"));

            await session.GotoAsync(alice, $"/options/{top}/compare?left={Uri.EscapeDataString(VariantsSeed.Left)}&right={Uri.EscapeDataString(VariantsSeed.Right)}");
            await BrowserSession.AssertBannersAsync(page);
            await session.GotoAsync(alice, $"/parts/{top}/variants");
            await BrowserSession.AssertBannersAsync(page);
            await session.GotoAsync(alice, $"/parts/{top}?variant={Uri.EscapeDataString(VariantsSeed.Left)}");
            await Expect(page.GetByLabel("Variant", new PageGetByLabelOptions { Exact = true }).First).ToBeVisibleAsync();

            await page.SetViewportSizeAsync(375, 800);
            await session.GotoAsync(alice, $"/options/{top}");
            await BrowserSession.AssertBannersAsync(page);
            Assert.False(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth + 1"), "no horizontal page scroll at 375 px");
        });
    }

    [E2EFact]
    public async Task A_person_without_the_program_gets_the_not_found_answer_for_a_hidden_product()
    {
        await using var session = new BrowserSession(fx, nameof(A_person_without_the_program_gets_the_not_found_answer_for_a_hidden_product));
        await session.RunAsync(async () =>
        {
            var up = await session.SignInAsync("alice");
            await session.GotoAsync(up, "/options/P-999999");
            await Expect(up.Page.GetByText("was not found")).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(up.Page);
        });
    }
}
