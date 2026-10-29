using System.Text.RegularExpressions;
using Keel.Audit;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// The idle session lock in a real browser (NIST SP 800-171 3.1.10 / 3.1.11, ADR-0026), against a host with a 12 second idle timeout: the warning appears,
/// the session locks, the page's data is gone from the DOM (not hidden under an overlay), actions are refused by the server, unlocking needs the Keycloak login
/// and returns to the same page, "Lock now" works, and a session that stays locked is ended.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class SessionLockTests(E2EFixture fx)
{
    private static Task<int> FetchStatusAsync(IPage page, string path) =>
        page.EvaluateAsync<int>("async (p) => (await fetch(p, { credentials: 'same-origin' })).status", path);

    private async Task<string> BracketFileDownloadPathAsync()
    {
        await using var cmd = fx.AppDb.CreateCommand(
            "SELECT m.number, f.id FROM plm.document_file f JOIN plm.document_iteration i ON i.id = f.iteration_id JOIN plm.document_revision r ON r.id = i.revision_id JOIN plm.document_master m ON m.id = r.master_id WHERE m.number = @n LIMIT 1");
        cmd.Parameters.AddWithValue("n", fx.Manifest.BracketDrawing);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return $"/downloads/documents/{reader.GetString(0)}/files/{reader.GetGuid(1)}";
    }

    // Pages through the whole log (the awareness tests add enough events that one page of 1000 no longer reaches the newest).
    private async Task<List<AuditRecord>> SessionEventsAsync()
    {
        var all = new List<AuditRecord>();
        long after = 0;
        while (true)
        {
            var page = await new AuditQueries(fx.AppDb).ListAsync(new AuditQuery(AfterSeq: after, Limit: 1000));
            all.AddRange(page.Where(e => e.Action.StartsWith("session.", StringComparison.Ordinal)));
            if (page.Count < 1000) return all;
            after = page[^1].Seq;
        }
    }

    [E2EFact]
    public async Task An_idle_session_locks_hides_its_data_refuses_actions_and_unlocks_only_through_the_idp()
    {
        await fx.EnsureLockHostAsync();
        await using var session = new BrowserSession(fx, nameof(An_idle_session_locks_hides_its_data_refuses_actions_and_unlocks_only_through_the_idp));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var alice = await session.SignInAsync("alice", baseUrl: fx.LockHostUrl);
            var page = alice.Page;
            var partPath = "/parts/" + m.GimbalBracket;
            var download = await BracketFileDownloadPathAsync();
            var eventsBefore = (await SessionEventsAsync()).Count;

            await session.GotoAsync(alice, partPath);
            await Expect(page.GetByText("Gimbal bracket").First).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.Locator("[data-session-hint]")).ToContainTextAsync("Session expires in");
            Assert.Equal(200, await FetchStatusAsync(page, download)); // while the session is active the same request is served

            // No input from here on: the server's timer warns, then locks.
            await Expect(page.Locator("[data-session-warning]")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 20_000 });
            await Expect(page.Locator("[data-session-warning]")).ToContainTextAsync("Locking in");
            var dialog = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Session locked" });
            await Expect(dialog).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 20_000 });

            // (a) Not an overlay: the page is gone from the DOM. Nothing of the part, its markings or its banners is left to read, copy or script.
            var html = await page.ContentAsync();
            foreach (var secret in new[] { m.GimbalBracket, "Gimbal bracket", "7075-T6", "ITAR", "USML", "ORCA", "Critical safety", "alice@", "Where used" })
                Assert.DoesNotContain(secret, html, StringComparison.Ordinal);
            Assert.Equal(0, await page.Locator("nav[aria-label='Main']").CountAsync());
            Assert.Equal(0, await page.Locator("[data-position]").CountAsync());
            Assert.Equal("true", await dialog.GetAttributeAsync("aria-modal"));
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign in to unlock" })).ToBeVisibleAsync();

            // (b) The server refuses service calls of the session, whatever a script sends: the very request that worked a moment ago.
            Assert.Equal(423, await FetchStatusAsync(page, download));
            Assert.Equal(423, await FetchStatusAsync(page, "/exports/anything"));

            // Keyboard: focus starts in the dialog and Tab never leaves it.
            for (var i = 0; i < 6; i++)
            {
                await page.Keyboard.PressAsync("Tab");
                Assert.True(await page.EvaluateAsync<bool>("() => !!document.activeElement && !!document.activeElement.closest('#session-lock')"), $"focus left the dialog after Tab #{i + 1}");
            }
            await page.Keyboard.PressAsync("Shift+Tab");
            Assert.True(await page.EvaluateAsync<bool>("() => !!document.activeElement.closest('#session-lock')"));

            // Phone width: the lock screen fits without sideways scrolling.
            await page.SetViewportSizeAsync(390, 800);
            await Expect(dialog).ToBeVisibleAsync();
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), "no horizontal scroll at phone width");
            var box = await dialog.BoundingBoxAsync();
            Assert.InRange(box!.Width, 200, 390);
            await page.SetViewportSizeAsync(1280, 900);

            // (c) Unlocking is a real login at Keycloak, and it returns to the same page with its data (re-read under the user's access).
            await BrowserSession.ClickAsync(page, "Sign in to unlock");
            await page.WaitForURLAsync(new Regex(@"/realms/keel/"));
            await BrowserSession.SubmitKeycloakAsync(page, "alice");
            await page.WaitForURLAsync(url => url.StartsWith(fx.LockHostUrl + partPath, StringComparison.Ordinal));
            await Expect(page.GetByText("Gimbal bracket").First).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
            await Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
            Assert.Equal(200, await FetchStatusAsync(page, download));

            // (d) The trail: lock (idle) and unlock (step-up), in order, naming the user, never the session id.
            var events = (await SessionEventsAsync()).Skip(eventsBefore).ToList();
            Assert.Contains(events, e => e.Action == "session.lock" && e.Payload.Contains("idle", StringComparison.Ordinal));
            var unlock = Assert.Single(events, e => e.Action == "session.unlock");
            Assert.Contains("step-up", unlock.Payload, StringComparison.Ordinal);
            Assert.True(events.FindIndex(e => e.Action == "session.lock") < events.FindIndex(e => e.Action == "session.unlock"));
            Assert.Empty(alice.PageErrors);
        });
    }

    private string LockHostLogPath => Path.Combine(fx.ResultsDir, "api-lock.log");

    private string LockHostLogFrom(long offset)
    {
        using var stream = new FileStream(LockHostLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(Math.Min(offset, stream.Length), SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private long LockHostLogLength() => File.Exists(LockHostLogPath) ? new FileInfo(LockHostLogPath).Length : 0;

    [E2EFact]
    public async Task Reloading_a_locked_session_shows_the_lock_dialog_without_page_data_or_a_circuit_error()
    {
        await fx.EnsureLockHostAsync();
        await using var session = new BrowserSession(fx, nameof(Reloading_a_locked_session_shows_the_lock_dialog_without_page_data_or_a_circuit_error));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var logFrom = LockHostLogLength();
            var alice = await session.SignInAsync("alice", baseUrl: fx.LockHostUrl);
            var page = alice.Page;
            var partPath = "/parts/" + m.GimbalBracket;
            await session.GotoAsync(alice, partPath);
            await Expect(page.GetByText("Gimbal bracket").First).ToBeVisibleAsync();

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Lock now" }).ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Session locked" });
            await Expect(dialog).ToBeVisibleAsync();

            // A reload starts a new circuit for a session that is already locked: the lock dialog is the first thing rendered, the part page never runs.
            await page.ReloadAsync();
            await Expect(dialog).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 20_000 });
            await Expect(page.Locator("[data-lock-countdown]")).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign in to unlock" })).ToBeVisibleAsync();
            await Task.Delay(1500); // a circuit that was going to die has done so by now
            await Expect(dialog).ToBeVisibleAsync();

            var html = await page.ContentAsync();
            foreach (var secret in new[] { m.GimbalBracket, "Gimbal bracket", "7075-T6", "ITAR", "USML", "ORCA", "Critical safety", "alice@", "Where used" })
                Assert.DoesNotContain(secret, html, StringComparison.Ordinal);
            Assert.Equal(0, await page.Locator("nav[aria-label='Main']").CountAsync());
            Assert.Equal(0, await page.Locator("[data-position]").CountAsync());
            Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync(), "the error banner of a dead circuit is not shown");

            // The page is still usable: unlocking is the IdP login and returns to the same page.
            await BrowserSession.ClickAsync(page, "Sign in to unlock");
            await page.WaitForURLAsync(new Regex(@"/realms/keel/"));
            await BrowserSession.SubmitKeycloakAsync(page, "alice");
            await page.WaitForURLAsync(url => url.StartsWith(fx.LockHostUrl + partPath, StringComparison.Ordinal));
            await Expect(page.GetByText("Gimbal bracket").First).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);

            // No circuit error in the browser console or in the server log.
            Assert.Empty(alice.PageErrors);
            Assert.DoesNotContain(alice.ConsoleErrors, e => e.Contains("circuit", StringComparison.OrdinalIgnoreCase) || e.Contains("SessionLocked", StringComparison.Ordinal));
            var log = LockHostLogFrom(logFrom);
            Assert.DoesNotContain("SessionLockedException", log, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception in circuit", log, StringComparison.Ordinal);
            Assert.DoesNotContain("ObjectDisposedException", log, StringComparison.Ordinal);
        });
    }

    [E2EFact]
    public async Task Lock_now_locks_at_once_and_a_session_that_stays_locked_is_ended()
    {
        await fx.EnsureLockHostAsync();
        await using var session = new BrowserSession(fx, nameof(Lock_now_locks_at_once_and_a_session_that_stays_locked_is_ended));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var audrey = await session.SignInAsync("audrey", baseUrl: fx.LockHostUrl);
            var page = audrey.Page;
            var eventsBefore = (await SessionEventsAsync()).Count;
            await session.GotoAsync(audrey, "/documents/" + m.SystemSpecification);
            await Expect(page.Locator("[data-position='top']")).ToBeVisibleAsync();

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Lock now" }).ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Session locked" });
            await Expect(dialog).ToBeVisibleAsync();
            Assert.DoesNotContain("System specification", await page.ContentAsync(), StringComparison.OrdinalIgnoreCase);
            await Expect(page.Locator("[data-lock-countdown]")).ToBeVisibleAsync();

            // It stays locked: input does not revive it (the server ignores activity while locked).
            await page.Mouse.MoveAsync(200, 200);
            await page.Keyboard.PressAsync("Shift");
            await Task.Delay(2000);
            await Expect(dialog).ToBeVisibleAsync();

            // Nobody signs in: after the grace the session is over, the circuit leaves for the sign-out endpoint, and the app cookie is gone.
            await page.WaitForURLAsync(url => !url.StartsWith(fx.LockHostUrl + "/documents", StringComparison.Ordinal), new PageWaitForURLOptions { Timeout = 60_000 });
            var logout = page.Locator("#kc-logout");
            if (page.Url.Contains("/realms/keel/", StringComparison.Ordinal) && await logout.CountAsync() > 0) await logout.ClickAsync();
            var cookies = await audrey.Context.CookiesAsync([fx.LockHostUrl]);
            Assert.DoesNotContain(cookies, c => c.Name == "keel.session" && !string.IsNullOrEmpty(c.Value));
            await page.GotoAsync(fx.LockHostUrl + "/documents/" + m.SystemSpecification);
            await page.WaitForSelectorAsync("#password"); // a full login again: the old session cannot come back

            // Only this test's user: the idle-lock test's session on the same host can still lock itself while this test runs.
            static bool IsAudrey(Keel.Audit.AuditRecord e) => e.Payload.Contains("\"name\":\"audrey\"", StringComparison.Ordinal);
            var trail = (await SessionEventsAsync()).Skip(eventsBefore).Where(IsAudrey).Select(e => e.Action).ToList();
            // The lock state is shared through the database, so another host's idle audrey session (an earlier test) can end during this test: it adds its own
            // expiry pair, never a second manual lock. This session's trail is the lock, then an expiry, then the termination, in that order.
            var ends = trail.Where(a => a is "session.lock" or "session.lock_expired" or "session.terminated_idle").ToList();
            Assert.Single(ends, a => a == "session.lock");
            var lockAt = ends.IndexOf("session.lock");
            var expiredAt = ends.IndexOf("session.lock_expired", lockAt);
            Assert.True(expiredAt > lockAt, "the lock is followed by its expiry");
            Assert.True(ends.IndexOf("session.terminated_idle", expiredAt) > expiredAt, "the expiry is followed by the termination");
            var manual = (await SessionEventsAsync()).Skip(eventsBefore).Where(IsAudrey).First(e => e.Action == "session.lock");
            Assert.Contains("manual", manual.Payload, StringComparison.Ordinal);
        });
    }
}
