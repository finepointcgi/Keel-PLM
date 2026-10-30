using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Npgsql;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// E-mail verification and bounce handling in the real browser (ADR-0055): a user saves an address, receives the link by mail (an in-process relay), confirms it with the
/// keyboard, a wrong link says one generic thing, and an administrator sees a suppressed address and lifts it, after which the user has to confirm again. Every state is audited with axe.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed partial class EmailVerificationTests(E2EFixture fx)
{
    [GeneratedRegex(@"/notifications/verify-email/([A-Za-z0-9_\-]+)")]
    private static partial Regex LinkPattern();

    private static string AxePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "Keel.E2E", "vendor", "axe-core", "axe.min.js");
    }

    private async Task ExecAsync(string sql, params (string, object)[] parameters)
    {
        await using var cmd = fx.AppDb.CreateCommand(sql);
        foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
        await cmd.ExecuteNonQueryAsync();
    }

    [E2EFact(Timeout = 600_000)]
    public async Task A_user_confirms_the_address_from_the_mailed_link_by_keyboard_and_an_administrator_lifts_a_suppression_so_the_user_confirms_again()
    {
        await using var session = new BrowserSession(fx, nameof(A_user_confirms_the_address_from_the_mailed_link_by_keyboard_and_an_administrator_lifts_a_suppression_so_the_user_confirms_again));
        await session.RunAsync(async () =>
        {
            var axe = await File.ReadAllTextAsync(AxePath());
            var findings = new List<string>();
            async Task Audit(IPage page, string label) => await AccessibilityTests.RunAxeAsync(page, label, axe, findings);
            var address = $"e2e-{Guid.NewGuid().ToString("N")[..8]}@corp.example";

            var alice = await session.SignInAsync("alice");
            var page = alice.Page;

            // ---- save the address: the link goes out and the page says what happens next, in words
            await session.GotoAsync(alice, "/notifications/preferences");
            var before = fx.Smtp.Mail.Count;
            await page.GetByLabel("Send me e-mail when I have a notification").CheckAsync();
            await BrowserSession.FillAsync(page.GetByLabel("My e-mail address"), address);
            await page.Locator("[data-pref=email-save]").FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(page.Locator("[data-pref=email-pending]")).ToContainTextAsync("We sent a link");
            Assert.True(await fx.Smtp.WaitForMailAsync(before + 1, TimeSpan.FromSeconds(15)));
            var mail = fx.Smtp.Mail[^1];
            Assert.Equal([address], mail.Recipients);
            Assert.Equal("Confirm your e-mail address for Keel PLM", mail.Header("Subject"));
            var token = LinkPattern().Match(mail.Data).Groups[1].Value;
            Assert.Equal(43, token.Length);
            await BrowserSession.AssertBannersAsync(page);
            await Audit(page, "preferences, link sent");

            // ---- a wrong link says one generic thing
            await page.GotoAsync(fx.BaseUrl + "/notifications/verify-email/not-a-real-token-0123456789abcdefghijklmn");
            await Expect(page.Locator("h1")).ToBeVisibleAsync();
            await page.Locator("[data-verify=confirm]").FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(page.Locator("[data-verify=invalid]")).ToContainTextAsync("This link is not valid");
            await BrowserSession.AssertBannersAsync(page);
            await Audit(page, "verify page, invalid link");

            // ---- the real link: opening it confirms nothing; the keyboard presses the button
            await page.GotoAsync(fx.BaseUrl + "/notifications/verify-email/" + token);
            await Expect(page.Locator("[data-verify=confirm]")).ToBeVisibleAsync();
            Assert.Equal(0, await page.Locator("[data-verify=confirmed]").CountAsync());
            await Audit(page, "verify page, before confirming");
            await page.Locator("[data-verify=confirm]").FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(page.Locator("[data-verify=confirmed]")).ToContainTextAsync("Your e-mail address is confirmed");
            await Audit(page, "verify page, confirmed");
            await session.GotoAsync(alice, "/notifications/preferences");
            await Expect(page.Locator("[data-pref=email-verified]")).ToContainTextAsync("confirmed");
            await Audit(page, "preferences, confirmed");

            // ---- the relay reported hard bounces for this address: the user is told, in her own words, and mail stops
            await ExecAsync("INSERT INTO ntf.email_address_health (address, hard_bounces, last_event_at, suppressed_at, suppress_reason) VALUES (@a, 2, now(), now(), 'hard_bounce')", ("a", address));
            await ExecAsync("INSERT INTO ntf.email_bounce_event (id, address, kind, source, status_code, diagnostic, received_at) VALUES (@id, @a, 'hard', 'api', '5.1.1', 'user unknown', now())", ("id", Guid.NewGuid()), ("a", address));
            await session.GotoAsync(alice, "/notifications/preferences");
            await Expect(page.Locator("[data-pref=email-suppressed]")).ToContainTextAsync("bounced");
            await Audit(page, "preferences, suppressed");

            // ---- the administrator sees it and lifts it with the keyboard
            var ada = await session.SignInAsync("ada");
            var apage = ada.Page;
            await session.GotoAsync(ada, "/admin/email");
            await BrowserSession.AssertBannersAsync(apage);
            var row = apage.Locator("table[data-email=suppressed] tbody tr", new PageLocatorOptions { HasText = address });
            await Expect(row).ToContainTextAsync("Hard bounces");
            await Expect(apage.Locator("table[data-email=recent] tbody tr", new PageLocatorOptions { HasText = address })).ToContainTextAsync("5.1.1");
            await Audit(apage, "admin e-mail delivery, suppressed address");
            await row.Locator("[data-email=clear]").FocusAsync();
            await apage.Keyboard.PressAsync("Enter");
            await Expect(apage.Locator("[data-confirm-dialog]")).ToBeVisibleAsync();
            await Audit(apage, "admin e-mail delivery, lift dialog");
            await apage.Locator("[data-confirm-ok]").FocusAsync();
            await apage.Keyboard.PressAsync("Enter");
            await Expect(apage.Locator("table[data-email=suppressed] tbody tr", new PageLocatorOptions { HasText = address })).ToHaveCountAsync(0);

            // ---- the user has to confirm again
            await session.GotoAsync(alice, "/notifications/preferences");
            await Expect(page.Locator("[data-pref=email-unverified]")).ToContainTextAsync("not confirmed");
            var again = fx.Smtp.Mail.Count;
            await page.Locator("[data-pref=email-verify]").FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            Assert.True(await fx.Smtp.WaitForMailAsync(again + 1, TimeSpan.FromSeconds(15)));
            var second = LinkPattern().Match(fx.Smtp.Mail[^1].Data).Groups[1].Value;
            Assert.NotEqual(token, second);

            // ---- a non-administrator cannot open the administrator's page
            await alice.Page.GotoAsync(fx.BaseUrl + "/admin/email");
            await Expect(alice.Page.Locator("body")).Not.ToContainTextAsync(address);

            // ---- phone width
            await page.SetViewportSizeAsync(375, 812);
            await session.GotoAsync(alice, "/notifications/preferences");
            await Audit(page, "preferences at 375 px");
            await apage.SetViewportSizeAsync(375, 812);
            await session.GotoAsync(ada, "/admin/email");
            await Audit(apage, "admin e-mail delivery at 375 px");

            Assert.True(findings.Count == 0, $"{findings.Count} accessibility violation(s):\n" + string.Join("\n", findings));
        });
    }
}
