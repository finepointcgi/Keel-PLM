using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// UX-4 through the real browser (ADR-0030): a mention reaches the other person's bell without a reload and lands on the comment, the person who may not read the object is
/// not offered in the mention list and sees nothing, the activity feed tells the story, watching, preferences, phone width, the keyboard, and the idle lock removing the bell.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed partial class AwarenessTests(E2EFixture fx)
{
    private static async Task<int> UnreadAsync(IPage page)
    {
        var label = await page.Locator("#bell-button").GetAttributeAsync("aria-label") ?? "";
        var m = UnreadLabel().Match(label);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    [GeneratedRegex(@"Notifications, (\d+) unread")]
    private static partial Regex UnreadLabel();

    [E2EFact]
    public async Task A_mention_raises_the_other_users_bell_without_a_reload_and_leads_to_the_comment_while_lena_is_never_offered_nor_told()
    {
        await using var session = new BrowserSession(fx, nameof(A_mention_raises_the_other_users_bell_without_a_reload_and_leads_to_the_comment_while_lena_is_never_offered_nor_told));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var bracket = "/parts/" + m.GimbalBracket;
            // Everybody is signed in and idle on the home page: their circuits are live.
            var bob = await session.SignInAsync("bob");
            await session.GotoAsync(bob, "/");
            var lena = await session.SignInAsync("lena");
            await session.GotoAsync(lena, "/");
            var alice = await session.SignInAsync("alice");
            var bobBefore = await UnreadAsync(bob.Page);
            var lenaBefore = await UnreadAsync(lena.Page);

            // ---- alice opens the Comments tab of the bracket and mentions bob
            await session.GotoAsync(alice, bracket);
            var page = alice.Page;
            await BrowserSession.OpenTabAsync(page, "Comments");
            var box = page.Locator("#comment-new");
            await Expect(box).ToBeVisibleAsync();
            await box.FillAsync("Please check the wall thickness @b");
            var suggestions = page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "People you can mention" });
            await Expect(suggestions.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "bob" })).ToBeVisibleAsync();
            // Lena is not a U.S. person: she cannot read this ITAR part, so she is never suggested (not even when her name is typed in full).
            await box.FillAsync("cc @lena");
            await Expect(suggestions).ToHaveCountAsync(0);
            await box.FillAsync("Please check the wall thickness @b");
            await suggestions.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "bob" }).ClickAsync();
            await Expect(box).ToHaveValueAsync("Please check the wall thickness @bob ");
            await box.PressSequentiallyAsync("before Friday. cc @lena");
            await BrowserSession.ClickAsync(page, "Post comment");

            // The new comment is announced, focused and counted.
            await Expect(page.Locator(".comment-body", new PageLocatorOptions { HasText = "Please check the wall thickness" })).ToBeVisibleAsync();
            await Expect(page.Locator(".visually-hidden[aria-live]").Filter(new LocatorFilterOptions { HasText = "Comment posted." })).ToBeAttachedAsync();
            await Expect(page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Comments" })).ToContainTextAsync("1");
            await Expect(page.Locator(".comment .mention", new PageLocatorOptions { HasText = "@bob" })).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);

            // ---- bob's bell grows by one without a reload (the hub signals his circuit; the 60-second poll is only the fallback)
            await Expect(bob.Page.Locator("#bell-button")).ToHaveAttributeAsync("aria-label", $"Notifications, {bobBefore + 1} unread", new LocatorAssertionsToHaveAttributeOptions { Timeout = 20_000 });
            await Expect(bob.Page.Locator("#bell-announce")).ToContainTextAsync("unread notification");
            // Lena was not told: her bell did not move, and she cannot open the part.
            Assert.Equal(lenaBefore, await UnreadAsync(lena.Page));
            await session.GotoAsync(lena, bracket);
            await Expect(lena.Page.GetByText("was not found, or you are not authorized")).ToBeVisibleAsync();
            Assert.DoesNotContain("wall thickness", await lena.Page.ContentAsync(), StringComparison.Ordinal);

            // ---- bob opens the flyout with the keyboard, follows the notification and lands on the comment
            await session.GotoAsync(bob, "/");
            var bell = bob.Page.Locator("#bell-button");
            await bell.FocusAsync();
            await bell.PressAsync("Enter");
            var panel = bob.Page.GetByRole(AriaRole.Group, new PageGetByRoleOptions { Name = "Latest notifications" });
            await Expect(panel).ToBeVisibleAsync();
            var item = panel.Locator("li.notif.unread", new LocatorLocatorOptions { HasText = "mentioned you in a comment" }).First;
            await Expect(item).ToContainTextAsync(m.GimbalBracket);
            await Expect(item.Locator(".chip")).ToBeVisibleAsync(); // the marking of the object named in the title
            await bob.Page.Keyboard.PressAsync("Escape");
            await Expect(panel).ToHaveCountAsync(0);
            await Expect(bell).ToBeFocusedAsync();
            await bell.PressAsync("Enter");
            await item.Locator("a.notif-title").ClickAsync();
            await bob.Page.WaitForURLAsync(new Regex(@"/parts/.*\?comment=.*#comments"));
            await Expect(bob.Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Comments" })).ToHaveAttributeAsync("aria-selected", "true");
            await Expect(bob.Page.Locator("article.comment.target")).ToContainTextAsync("Please check the wall thickness");
            await Expect(bob.Page.Locator("#bell-button")).ToHaveAttributeAsync("aria-label", $"Notifications, {bobBefore} unread"); // opening it marked it read
            await BrowserSession.AssertBannersAsync(bob.Page);

            // bob replies; the thread is a list with a reply button and heading structure
            await Expect(bob.Page.Locator("ol.comment-thread > li").First).ToBeVisibleAsync();
            await bob.Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Reply to Alice Example" }).ClickAsync();
            await Expect(bob.Page.Locator("#reply-box")).ToBeFocusedAsync();
            await bob.Page.Locator("#reply-box").FillAsync("Checked: 3 mm is right.");
            await BrowserSession.ClickAsync(bob.Page, "Post reply");
            await Expect(bob.Page.Locator("ol.comment-replies .comment-body")).ToContainTextAsync("3 mm is right");

            // ---- the activity feed of the bracket tells the story
            await BrowserSession.OpenTabAsync(alice.Page, "Activity");
            await alice.Page.ReloadAsync();
            await BrowserSession.OpenTabAsync(alice.Page, "Activity");
            var feed = alice.Page.Locator("ol.activity-list");
            await Expect(feed).ToContainTextAsync("alice commented");
            await Expect(feed).ToContainTextAsync("bob commented");
            var text = await feed.InnerTextAsync();
            Assert.DoesNotMatch(@"\b(part|workflow|comment|document)\.[a-z_]+\b", text); // no raw action codes
            Assert.DoesNotContain("viewed the part", text, StringComparison.Ordinal); // views are not in the feed
            Assert.DoesNotContain("Please check the wall thickness", text, StringComparison.Ordinal); // and neither is the comment text
            Assert.Empty(alice.PageErrors);
            Assert.Empty(bob.PageErrors);
        });
    }

    [E2EFact]
    public async Task Watching_the_home_card_the_watch_list_and_the_preferences_work_and_mentions_cannot_be_turned_off()
    {
        await using var session = new BrowserSession(fx, nameof(Watching_the_home_card_the_watch_list_and_the_preferences_work_and_mentions_cannot_be_turned_off));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var ada = await session.SignInAsync("ada");
            var page = ada.Page;
            await session.GotoAsync(ada, "/parts/" + m.EndCap);

            // The watch button sits next to the favorite star and toggles.
            var watch = page.Locator("[data-action=watch]");
            await Expect(page.Locator("[data-action=favorite]")).ToBeVisibleAsync();
            await Expect(watch).ToBeVisibleAsync();
            if (await watch.GetAttributeAsync("aria-pressed") == "true") await watch.ClickAsync(); // start from "not watching"
            await Expect(watch).ToHaveAttributeAsync("aria-pressed", "false");
            await watch.ClickAsync();
            await Expect(watch).ToHaveAttributeAsync("aria-pressed", "true");
            await page.Locator("select.watch-level").SelectOptionAsync("decisions");

            // Watching shows on the Home card and in the list, with the level.
            await session.GotoAsync(ada, "/");
            await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Activity in things I watch" })).ToBeVisibleAsync();
            await session.GotoAsync(ada, "/watching");
            var row = page.Locator("tr", new PageLocatorOptions { HasText = m.EndCap });
            await Expect(row).ToBeVisibleAsync();
            await Expect(row.Locator("select[data-action=level]")).ToHaveValueAsync("decisions");
            await BrowserSession.AssertBannersAsync(page);

            // Stop watching from the list.
            await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Stop watching " + m.EndCap }).ClickAsync();
            await Expect(page.Locator("tr", new PageLocatorOptions { HasText = m.EndCap })).ToHaveCountAsync(0);

            // Preferences: mentions and tasks are locked with the reason; another kind can be switched off and stays off after a reload.
            await session.GotoAsync(ada, "/notifications/preferences");
            var mention = page.GetByLabel("Tell me in the application: Mentioned in a comment");
            await Expect(mention).ToBeDisabledAsync();
            await Expect(mention).ToBeCheckedAsync();
            await Expect(page.GetByText("Someone asked for you by name: this cannot be turned off.")).ToBeVisibleAsync();
            await Expect(page.GetByLabel("Tell me in the application: Approval task assigned to you")).ToBeDisabledAsync();
            var changed = page.GetByLabel("Tell me in the application: Watched object changed", new PageGetByLabelOptions { Exact = true });
            await changed.UncheckAsync();
            await page.ReloadAsync();
            await Expect(changed).Not.ToBeCheckedAsync();
            await changed.CheckAsync();
            await page.ReloadAsync();
            await Expect(changed).ToBeCheckedAsync();
            await BrowserSession.AssertBannersAsync(page);
        });
    }

    [E2EFact]
    public async Task The_notifications_page_filters_marks_read_and_shows_only_neutral_text_for_what_the_user_may_no_longer_read()
    {
        await using var session = new BrowserSession(fx, nameof(The_notifications_page_filters_marks_read_and_shows_only_neutral_text_for_what_the_user_may_no_longer_read));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            // Lena (not a U.S. person) has notifications about the ITAR bracket, which she can never read, and one about an open fastener she can.
            var lena = await session.SignInAsync("lena"); // signing in puts her in the directory
            var page = lena.Page;
            await InsertNotificationAsync("lena", "watched_changed", "part", m.GimbalBracket);
            await InsertNotificationAsync("lena", "comment_mention", "part", m.GimbalBracket);
            await InsertNotificationAsync("lena", "watched_changed", "part", m.OpenFastener);
            await session.GotoAsync(lena, "/notifications");

            await Expect(page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Unread" })).ToBeVisibleAsync();
            foreach (var tab in new[] { "All", "Unread", "Mentions", "Tasks", "Watching" })
                await Expect(page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = tab, Exact = false }).First).ToBeVisibleAsync();

            // The ITAR rows are neutral: no number, no name, no link; the open fastener reads normally.
            var neutral = page.Locator("li.notif[data-restricted=true]");
            await Expect(neutral.First).ToContainTextAsync("A notification you can no longer view");
            Assert.True(await neutral.CountAsync() >= 2);
            Assert.Equal(0, await neutral.Locator("a").CountAsync());
            var html = await page.Locator("main").InnerHTMLAsync();
            Assert.DoesNotContain(m.GimbalBracket, html, StringComparison.Ordinal);
            Assert.DoesNotContain("Gimbal bracket", html, StringComparison.Ordinal);
            await Expect(page.Locator("li.notif", new PageLocatorOptions { HasText = m.OpenFastener })).ToBeVisibleAsync();

            // The Mentions tab never shows a neutral row (it would reveal the kind); the badge counted every unread row once.
            await BrowserSession.OpenTabAsync(page, "Mentions");
            await Expect(page.Locator("li.notif[data-restricted=true]")).ToHaveCountAsync(0);
            await BrowserSession.OpenTabAsync(page, "All");

            // Bulk: select one and mark it read; then mark everything read.
            var unreadBefore = await UnreadAsync(page);
            Assert.True(unreadBefore >= 3);
            await page.Locator("li.notif.unread .notif-select").First.CheckAsync();
            await BrowserSession.ClickAsync(page, "Mark selected as read");
            await Expect(page.Locator("#bell-button")).ToHaveAttributeAsync("aria-label", $"Notifications, {unreadBefore - 1} unread");
            await BrowserSession.ClickAsync(page, "Mark all as read");
            await Expect(page.Locator("#bell-button")).ToHaveAttributeAsync("aria-label", "Notifications, none unread");
            await BrowserSession.OpenTabAsync(page, "Unread");
            await Expect(page.GetByText("You are all caught up")).ToBeVisibleAsync();
            await BrowserSession.AssertBannersAsync(page);
        });
    }

    [E2EFact]
    public async Task The_bell_the_comments_and_the_notifications_page_fit_a_phone_and_work_by_keyboard()
    {
        await using var session = new BrowserSession(fx, nameof(The_bell_the_comments_and_the_notifications_page_fit_a_phone_and_work_by_keyboard));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var bob = await session.SignInAsync("bob", width: 375, height: 800);
            var page = bob.Page;
            await InsertNotificationAsync("bob", "watched_changed", "part", m.OpenFastener);
            await session.GotoAsync(bob, "/notifications");
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), "no horizontal scroll on the notifications page");

            await page.Locator("#bell-button").ClickAsync();
            var panel = page.Locator("#bell-panel");
            await Expect(panel).ToBeVisibleAsync();
            var box = await panel.BoundingBoxAsync();
            Assert.True(box!.X >= 0 && box.X + box.Width <= 376, $"the flyout must fit a 375 px phone (x {box.X}, width {box.Width})");
            Assert.True(box.Height > 80);
            await page.Keyboard.PressAsync("Escape");

            await session.GotoAsync(bob, "/parts/" + m.OpenFastener);
            await BrowserSession.OpenTabAsync(page, "Comments");
            await Expect(page.Locator("#comment-new")).ToBeVisibleAsync();
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), "no horizontal scroll on the comments tab");
            await BrowserSession.OpenTabAsync(page, "Activity");
            await Expect(page.Locator("#activity-heading")).ToBeVisibleAsync();
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), "no horizontal scroll on the activity tab");
            await BrowserSession.AssertBannersAsync(page);
        });
    }

    [E2EFact]
    public async Task A_locked_session_has_no_bell_and_no_notification_text_in_the_page()
    {
        await fx.EnsureLockHostAsync();
        await using var session = new BrowserSession(fx, nameof(A_locked_session_has_no_bell_and_no_notification_text_in_the_page));
        await session.RunAsync(async () =>
        {
            var m = fx.Manifest;
            var ada = await session.SignInAsync("ada", baseUrl: fx.LockHostUrl);
            var page = ada.Page;
            await InsertNotificationAsync("ada", "watched_changed", "part", m.OpenFastener);
            await session.GotoAsync(ada, "/");
            await Expect(page.Locator("#bell-button")).ToBeVisibleAsync();
            Assert.True(await UnreadAsync(page) >= 1);
            await page.Locator("#bell-button").ClickAsync();
            await Expect(page.Locator("#bell-panel")).ToContainTextAsync(m.OpenFastener);

            // No input from here: the server locks the session and the bell, its flyout and the notification text leave the DOM.
            var dialog = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Session locked" });
            await Expect(dialog).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
            Assert.Equal(0, await page.Locator("#bell-button").CountAsync());
            Assert.Equal(0, await page.Locator("#bell-panel").CountAsync());
            Assert.Equal(0, await page.Locator("li.notif").CountAsync());
            var html = await page.ContentAsync();
            Assert.DoesNotContain(m.OpenFastener, html, StringComparison.Ordinal);
            Assert.DoesNotContain("unread", html, StringComparison.Ordinal);
        });
    }

    private async Task InsertNotificationAsync(string user, string kind, string objectType, string number)
    {
        await using var cmd = fx.AppDb.CreateCommand(
            "INSERT INTO ntf.notification (id, recipient_sub, kind, object_type, object_number, created_at) " +
            "SELECT @id, sub, @kind, @type, @number, now() FROM identity.user_directory WHERE lower(username) = @user");
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("type", objectType);
        cmd.Parameters.AddWithValue("number", number);
        cmd.Parameters.AddWithValue("user", user);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync()); // the user must have signed in once (the directory)
    }
}
