using Bunit;
using Keel.Core;
using Keel.Notifications;
using Keel.Web.Components.Shared.Awareness;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using static Keel.UnitTests.AwarenessTestKit;

namespace Keel.UnitTests;

/// <summary>UX-4 (ADR-0030): the bell, the notification list, the comment thread, the watch button and the activity feed.</summary>
public sealed class AwarenessComponentTests : BunitContext
{
    private readonly FakeCenter center;
    private readonly FakeWatches watches;
    private readonly FakeComments comments;
    private readonly FakeFeed feed;
    private readonly NotificationHub hub;

    public AwarenessComponentTests()
    {
        _ = ShellTestKit.Register(this, Roles.Viewer, Roles.Author);
        // ShellTestKit registered the fakes (first registration wins): take them back out of the container.
        center = (FakeCenter)Services.GetRequiredService<INotificationCenter>();
        watches = (FakeWatches)Services.GetRequiredService<IWatchService>();
        comments = (FakeComments)Services.GetRequiredService<ICommentService>();
        feed = (FakeFeed)Services.GetRequiredService<IActivityFeed>();
        hub = (NotificationHub)Services.GetRequiredService<INotificationHub>();
    }

    // ---------------------------------------------------------------- bell

    [Fact]
    public void The_bell_shows_the_unread_count_and_its_accessible_name_includes_it()
    {
        center.Unread = 3;
        var cut = Render<NotificationBell>();

        var button = cut.Find("#bell-button");
        Assert.Equal("Notifications, 3 unread", button.GetAttribute("aria-label"));
        Assert.Equal("3", cut.Find(".bell-count").TextContent);
        Assert.Equal("true", cut.Find(".bell-count").GetAttribute("aria-hidden")); // the name carries the count; the badge is decoration
        Assert.Equal("false", button.GetAttribute("aria-expanded"));
        Assert.Equal("bell-panel", button.GetAttribute("aria-controls"));
    }

    [Fact]
    public void With_nothing_unread_there_is_no_badge_and_the_name_says_so()
    {
        var cut = Render<NotificationBell>();
        Assert.Equal("Notifications, none unread", cut.Find("#bell-button").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll(".bell-count"));
    }

    [Fact]
    public void A_large_count_is_capped_in_the_badge_but_not_in_the_name()
    {
        center.Unread = 250;
        var cut = Render<NotificationBell>();
        Assert.Equal("99+", cut.Find(".bell-count").TextContent);
        Assert.Equal("Notifications, 250 unread", cut.Find("#bell-button").GetAttribute("aria-label"));
    }

    [Fact]
    public void The_flyout_lists_the_latest_notifications_and_offers_mark_all_read_and_see_all()
    {
        center.Unread = 2;
        center.Items.AddRange([Item("P-000015", "Bracket"), Item("D-000002", "Drawing"), Item("P-000009", "Cover", read: true)]);
        var cut = Render<NotificationBell>();

        cut.Find("#bell-button").Click();

        Assert.Equal("true", cut.Find("#bell-button").GetAttribute("aria-expanded"));
        Assert.Equal(3, cut.FindAll(".bell-panel .notif").Count);
        Assert.Contains("P-000015 · Bracket", cut.Find(".bell-panel").TextContent, StringComparison.Ordinal);
        Assert.Equal("notifications", cut.Find(".bell-foot a").GetAttribute("href"));
        Assert.Contains("See all notifications", cut.Find(".bell-foot a").TextContent, StringComparison.Ordinal);
        Assert.Equal(2, cut.FindAll(".notif.unread").Count);

        cut.Find("[data-action=mark-all-read]").Click();

        Assert.Equal(1, center.MarkAllCalls);
        Assert.Empty(cut.FindAll(".bell-count"));
        Assert.Contains("All notifications marked as read", cut.Find("#bell-announce").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Opening_a_notification_marks_it_read()
    {
        center.Unread = 1;
        var item = Item("P-000015", "Bracket");
        center.Items.Add(item);
        var cut = Render<NotificationBell>();
        cut.Find("#bell-button").Click();

        cut.Find(".bell-panel a.notif-title").Click();

        Assert.Equal([item.Id], center.MarkedRead);
        Assert.Equal("Notifications, none unread", cut.Find("#bell-button").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll(".bell-panel")); // the flyout closes when an item is followed
    }

    [Fact]
    public void A_restricted_notification_shows_only_the_neutral_text()
    {
        center.Unread = 1;
        center.Items.Add(Restricted());
        var cut = Render<NotificationBell>();
        cut.Find("#bell-button").Click();

        var row = cut.Find(".bell-panel .notif");
        Assert.Contains("A notification you can no longer view", row.TextContent, StringComparison.Ordinal);
        Assert.Empty(row.QuerySelectorAll("a")); // no link, so no number, no name
        Assert.Empty(row.QuerySelectorAll(".chip"));
        Assert.Equal("true", row.GetAttribute("data-restricted"));
    }

    [Fact]
    public void The_badge_follows_new_notifications_without_a_reload_and_announces_them_politely_without_taking_focus()
    {
        var cut = Render<NotificationBell>();
        Assert.Equal("", cut.Find("#bell-announce").TextContent);
        var live = cut.Find("#bell-announce");
        Assert.Equal("polite", live.GetAttribute("aria-live"));

        center.Unread = 2; // a notification was written for this user; the hub tells the circuit
        hub.Publish("sub-alice", TimeSpan.Zero);

        cut.WaitForAssertion(() => Assert.Equal("Notifications, 2 unread", cut.Find("#bell-button").GetAttribute("aria-label")), TimeSpan.FromSeconds(3));
        Assert.Equal("2 unread notifications", cut.Find("#bell-announce").TextContent);
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "focusSelector" || i.Identifier == "focusElement"); // never steals the focus
    }

    [Fact]
    public async Task The_poll_is_the_fallback_when_no_signal_arrives()
    {
        var cut = Render<NotificationBell>(p => p.Add(x => x.PollInterval, TimeSpan.FromMilliseconds(150)));
        center.Unread = 4; // written on another node: no signal reaches this one

        await Task.Delay(100);
        cut.WaitForAssertion(() => Assert.Equal("Notifications, 4 unread", cut.Find("#bell-button").GetAttribute("aria-label")), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void A_locked_session_clears_the_bell_and_shows_nothing()
    {
        center.Unread = 5;
        center.Items.Add(Item("P-000015", "Bracket"));
        var cut = Render<NotificationBell>();
        cut.Find("#bell-button").Click();
        Assert.NotEmpty(cut.FindAll(".notif"));

        center.ThrowLocked = true; // CurrentUser refuses while the session is locked; the next refresh finds out
        hub.Publish("sub-alice", TimeSpan.Zero);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".notif")), TimeSpan.FromSeconds(3));
        Assert.Empty(cut.FindAll(".bell-panel"));
        Assert.Empty(cut.FindAll(".bell-count"));
        Assert.DoesNotContain("Bracket", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bell_stops_listening_when_it_is_removed_from_the_page()
    {
        var cut = Render<NotificationBell>();
        Assert.Equal(1, hub.SubscriberCount("sub-alice"));
        Renderer.DisposeComponents(); // the page was left
        Assert.Equal(0, hub.SubscriberCount("sub-alice"));
    }

    // ---------------------------------------------------------------- notification list

    [Fact]
    public void The_list_marks_unread_rows_in_words_shows_counters_and_selects_rows()
    {
        var read = Item("P-000001", "One", read: true);
        var unread = Item("P-000002", "Two", occurrences: 3, text: "Changed (by alice)");
        var picked = new List<(Guid, bool)>();
        var cut = Render<NotificationList>(p => p.Add(x => x.Items, [read, unread]).Add(x => x.Selectable, true).Add(x => x.ShowMarkRead, true)
            .Add(x => x.OnSelect, EventCallback.Factory.Create<(Guid Id, bool Selected)>(this, c => picked.Add((c.Id, c.Selected)))));

        var rows = cut.FindAll(".notif");
        Assert.Contains("Unread.", rows[1].TextContent, StringComparison.Ordinal); // the dot is not the only cue
        Assert.DoesNotContain("Unread.", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Changed (by alice) (3 times)", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("button")); // only the unread row has "Mark read"

        rows[1].QuerySelector("input[type=checkbox]")!.Change(true);
        Assert.Equal([(unread.Id, true)], picked);
        Assert.NotNull(cut.Find("time[datetime]").GetAttribute("title"));
    }

    // ---------------------------------------------------------------- watch button

    [Fact]
    public void The_watch_button_toggles_and_offers_a_level_only_while_watching()
    {
        var cut = Render<WatchButton>(p => p.Add(x => x.Kind, "part").Add(x => x.Number, "P-000015"));
        var button = cut.Find("[data-action=watch]");
        Assert.Equal("false", button.GetAttribute("aria-pressed"));
        Assert.Equal("Watch P-000015", button.GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll(".watch-level"));

        button.Click();

        Assert.Equal(WatchLevel.All, watches.Watched[("part", "P-000015")]);
        Assert.Equal("true", cut.Find("[data-action=watch]").GetAttribute("aria-pressed"));
        Assert.Equal("Stop watching P-000015", cut.Find("[data-action=watch]").GetAttribute("aria-label"));

        cut.Find("select.watch-level").Change("decisions");
        Assert.Equal(WatchLevel.Decisions, watches.Watched[("part", "P-000015")]);

        cut.Find("[data-action=watch]").Click();
        Assert.Empty(watches.Watched);
        Assert.Empty(cut.FindAll(".watch-level"));
    }

    [Fact]
    public void The_watch_button_stays_off_when_the_service_refuses()
    {
        watches.Deny = true;
        var cut = Render<WatchButton>(p => p.Add(x => x.Kind, "part").Add(x => x.Number, "P-000015"));
        cut.Find("[data-action=watch]").Click();
        Assert.Equal("false", cut.Find("[data-action=watch]").GetAttribute("aria-pressed"));
        Assert.Empty(watches.Watched);
    }

    [Fact]
    public void The_watch_button_starts_in_the_stored_state()
    {
        watches.Watched[("document", "D-000002")] = WatchLevel.Decisions;
        var cut = Render<WatchButton>(p => p.Add(x => x.Kind, "document").Add(x => x.Number, "D-000002"));
        Assert.Equal("true", cut.Find("[data-action=watch]").GetAttribute("aria-pressed"));
        Assert.Equal("decisions", cut.Find("select.watch-level").GetAttribute("value") ?? cut.Find("select.watch-level option[selected]").GetAttribute("value"));
    }

    // ---------------------------------------------------------------- comments

    private static CommentView Comment(string author, string body, bool removed = false, bool canDelete = false, DateTimeOffset? at = null, IReadOnlyList<CommentView>? replies = null) =>
        new(Guid.CreateVersion7(), null, author, "sub-" + author.ToLowerInvariant(), at ?? DateTimeOffset.UtcNow, removed, body, removed ? DateTimeOffset.UtcNow : null, removed ? "Admin" : null, null, canDelete, replies ?? []);

    private IRenderedComponent<CommentThread> RenderThread(string number = "P-000015") =>
        Render<CommentThread>(p => p.Add(x => x.Kind, "part").Add(x => x.Number, number));

    [Fact]
    public void Comment_text_is_only_ever_rendered_as_encoded_text()
    {
        const string payload = "<img src=x onerror=alert(1)><script>alert(2)</script> <a href=\"javascript:alert(3)\">click</a> & \"quotes\" <b>bold</b>";
        comments.Threads.Add(Comment("Alice", payload));
        var cut = RenderThread();

        Assert.Empty(cut.FindAll(".comment-body img"));
        Assert.Empty(cut.FindAll("script"));
        Assert.Empty(cut.FindAll(".comment-body a"));
        Assert.Empty(cut.FindAll(".comment-body b"));
        Assert.Equal(payload, cut.Find(".comment-body").TextContent); // shown literally, character for character
        Assert.DoesNotContain("<script", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Mentions_are_highlighted_and_object_numbers_link_only_when_the_viewer_can_read_them()
    {
        comments.Chips["P-000015"] = new ObjectChip("part", "P-000015", "Bracket", "UNCLASSIFIED", "parts/P-000015");
        comments.Threads.Add(Comment("Alice", "Hi @bob, see P-000015 and P-000099 (secret) and ECN-000002."));
        var cut = RenderThread();

        Assert.Equal("@bob", cut.Find(".comment-body .mention").TextContent);
        var chip = cut.Find(".comment-body a.ref-chip");
        Assert.Equal("parts/P-000015", chip.GetAttribute("href"));
        Assert.Single(cut.FindAll(".comment-body a")); // P-000099 and ECN-000002 stay plain text: they are not resolved, so nothing about them is shown
        Assert.Contains("P-000099", cut.Find(".comment-body").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_thread_has_headings_a_list_and_a_reply_button_per_comment()
    {
        var reply = Comment("Bob", "A reply");
        comments.Threads.Add(Comment("Alice", "First", replies: [reply]));
        comments.Threads.Add(Comment("Carol", "Second"));
        var cut = RenderThread();

        Assert.Equal("Comments (2)", cut.Find("#comments-heading").TextContent);
        Assert.Single(cut.FindAll("ol.comment-thread"));
        Assert.Single(cut.FindAll("ol.comment-replies"));
        Assert.Equal(3, cut.FindAll("article.comment").Count);
        Assert.Equal(3, cut.FindAll("article.comment h4").Count);
        var replyButtons = cut.FindAll("[data-action=reply]");
        Assert.Equal(2, replyButtons.Count); // one per top-level comment: replies go one level deep
        Assert.Equal("Reply to Alice", replyButtons.First(b => b.GetAttribute("aria-label")!.Contains("Alice", StringComparison.Ordinal)).GetAttribute("aria-label"));
    }

    [Fact]
    public void Posting_clears_the_draft_announces_it_and_moves_focus_to_the_new_comment()
    {
        var cut = RenderThread();
        cut.Find("#comment-new").Input("Looks good to me");
        Assert.False(cut.Find("[data-action=post-comment]").HasAttribute("disabled"));

        cut.Find("form.composer").Submit();

        Assert.Equal([("Looks good to me", (Guid?)null)], comments.Posted);
        Assert.Equal("", cut.Find("#comment-new").GetAttribute("value") ?? "");
        Assert.Equal("Comment posted.", cut.Find(".visually-hidden[aria-live]").TextContent);
        var posted = comments.Threads.Single();
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "focusSelector" && i.Arguments.Contains("#c-" + posted.Id.ToString("D")));
        Assert.Contains("Looks good to me", cut.Find("#c-" + posted.Id.ToString("D")).TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_comment_cannot_be_posted()
    {
        var cut = RenderThread();
        Assert.True(cut.Find("[data-action=post-comment]").HasAttribute("disabled"));
        cut.Find("#comment-new").Input("   ");
        Assert.True(cut.Find("[data-action=post-comment]").HasAttribute("disabled"));
        cut.Find("form.composer").Submit();
        Assert.Empty(comments.Posted);
    }

    [Fact]
    public void A_reply_goes_under_its_comment_with_a_focused_box()
    {
        var parent = Comment("Alice", "Question?");
        comments.Threads.Add(parent);
        var cut = RenderThread();

        cut.Find("[data-action=reply]").Click();
        cut.Find("#reply-box").Input("Answer");
        cut.Find("form.reply").Submit();

        Assert.Equal([("Answer", (Guid?)parent.Id)], comments.Posted);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "focusSelector" && i.Arguments.Contains("#reply-box"));
        Assert.Single(cut.FindAll("ol.comment-replies article"));
    }

    [Fact]
    public void The_draft_survives_switching_tabs()
    {
        var first = RenderThread();
        first.Find("#comment-new").Input("half a thought @al");
        Renderer.DisposeComponents(); // the tab panel is replaced when another tab is selected

        var again = RenderThread();

        Assert.Equal("half a thought @al", again.Find("#comment-new").GetAttribute("value"));
        var other = Render<CommentThread>(p => p.Add(x => x.Kind, "part").Add(x => x.Number, "P-000099")); // another object has its own draft
        Assert.Equal("", other.Find("#comment-new").GetAttribute("value") ?? "");
    }

    [Fact]
    public void Typing_an_at_sign_offers_only_the_people_the_service_returns_and_inserts_the_login()
    {
        comments.Candidates.AddRange([new MentionCandidate("alice", "Alice Example"), new MentionCandidate("alan", "Alan Other")]);
        var cut = RenderThread();

        cut.Find("#comment-new").Input("Thanks @al");

        var buttons = cut.FindAll(".mention-list button");
        Assert.Equal(2, buttons.Count);
        Assert.Contains("Alice Example", buttons[0].TextContent, StringComparison.Ordinal);

        buttons[0].Click();

        Assert.Equal("Thanks @alice ", cut.Find("#comment-new").GetAttribute("value"));
        Assert.Empty(cut.FindAll(".mention-list button"));

        cut.Find("#comment-new").Input("Thanks @zed"); // nobody eligible by that name: no suggestions at all
        Assert.Empty(cut.FindAll(".mention-list button"));
    }

    [Fact]
    public void A_removed_comment_is_a_tombstone_and_hides_the_text()
    {
        comments.Threads.Add(Comment("Alice", "secret text", removed: true));
        comments.Threads[0] = comments.Threads[0] with { Body = null };
        var cut = RenderThread();

        var text = cut.Find("article.comment").TextContent;
        Assert.Contains("Comment removed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret text", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-action=reply]"));
        Assert.Equal("Comments (0)", cut.Find("#comments-heading").TextContent);
    }

    [Fact]
    public void An_auditor_can_read_the_removed_text()
    {
        comments.IsAuditor = true;
        comments.CanPost = false;
        comments.Threads.Add(Comment("Alice", "the removed words", removed: true));
        var cut = RenderThread();

        Assert.Contains("the removed words", cut.Find("details.comment-audit .comment-body").TextContent, StringComparison.Ordinal);
        Assert.Contains("Removed text (auditor view)", cut.Find("details.comment-audit summary").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("#comment-new")); // an auditor reads, never writes
    }

    [Fact]
    public void Removing_a_comment_asks_for_a_reason_first()
    {
        var mine = Comment("Alice", "oops", canDelete: true);
        comments.Threads.Add(mine);
        var cut = RenderThread();

        cut.Find("[data-action=remove-comment]").Click();
        Assert.NotNull(cut.Find("[role=dialog]"));
        Assert.Empty(comments.Deleted);

        cut.Find("[data-confirm-reason]").Input("posted on the wrong part");
        cut.Find("[data-confirm-ok]").Click();

        Assert.Equal([(mine.Id, "posted on the wrong part")], comments.Deleted);
        Assert.Contains("Comment removed", cut.Find("article.comment").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Someone_who_cannot_read_the_object_gets_no_comments_at_all()
    {
        comments.NotFound = true;
        comments.Threads.Add(Comment("Alice", "marked words"));
        var cut = RenderThread();

        Assert.Contains("not available", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("marked words", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("#comment-new"));
    }

    [Fact]
    public void A_reader_without_the_role_sees_the_comments_but_no_box()
    {
        comments.CanPost = false;
        comments.Threads.Add(Comment("Alice", "visible"));
        var cut = RenderThread();
        Assert.Empty(cut.FindAll("#comment-new"));
        Assert.Contains("viewer role or higher", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-action=reply]"));
    }

    [Fact]
    public void The_order_can_be_switched_and_a_deep_link_highlights_its_comment()
    {
        var older = Comment("Alice", "older", at: DateTimeOffset.UtcNow.AddHours(-2));
        var newer = Comment("Bob", "newer", at: DateTimeOffset.UtcNow.AddHours(-1));
        comments.Threads.AddRange([older, newer]);
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo($"/parts/P-000015?comment={older.Id:D}#comments");
        var cut = RenderThread();

        Assert.Equal(["Bob", "Alice"], cut.FindAll("article.comment h4").Select(h => h.TextContent.Split(' ')[0])); // newest first
        Assert.Contains("target", cut.Find("#c-" + older.Id.ToString("D")).ClassList);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "scrollToId" && i.Arguments.Contains("c-" + older.Id.ToString("D")));

        cut.Find("[data-action=sort-comments]").Click();
        Assert.Equal(["Alice", "Bob"], cut.FindAll("article.comment h4").Select(h => h.TextContent.Split(' ')[0]));
    }

    // ---------------------------------------------------------------- activity feed

    private static ActivityEntry Entry(string sentence, string category = "version", bool view = false, params ActivityDetail[] details) =>
        new(1, DateTimeOffset.UtcNow.AddMinutes(-5), "alice", sentence, category, view, details, null);

    [Fact]
    public void The_activity_feed_reads_as_sentences_with_times_and_expandable_details_and_no_action_codes()
    {
        feed.Entries.Add(Entry("alice checked in version A.2", "version", false, new ActivityDetail("Version", "A.2", null), new ActivityDetail("Comment", "tightened the tolerance", null)));
        feed.Entries.Add(Entry("bob signed and approved", "review"));
        var cut = Render<ActivityFeed>(p => p.Add(x => x.Kind, "part").Add(x => x.Number, "P-000015"));

        Assert.Equal(2, cut.FindAll(".activity-item").Count);
        Assert.Contains("alice checked in version A.2", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("5 min ago", cut.Find(".activity-item time").TextContent, StringComparison.Ordinal);
        Assert.Contains("UTC", cut.Find(".activity-item time").GetAttribute("title"), StringComparison.Ordinal);
        var details = cut.Find("details.activity-details");
        Assert.Contains("Show details", details.QuerySelector("summary")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("tightened the tolerance", details.TextContent, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("details.activity-details")); // no details, no expander
        Assert.DoesNotMatch(@"\b(part|workflow|document)\.[a-z_]+\b", cut.Find(".activity-list").TextContent);
        Assert.Empty(cut.FindAll("#activity-heading ~ label")); // an ordinary viewer has no toggle
    }

    [Fact]
    public void Only_an_auditor_gets_the_views_toggle_and_it_asks_the_service_for_views()
    {
        feed.IsAuditor = true;
        feed.Entries.Add(Entry("alice checked in version A.2"));
        feed.Entries.Add(Entry("carol viewed the part", "view", view: true));
        var cut = Render<ActivityFeed>(p => p.Add(x => x.Kind, "part").Add(x => x.Number, "P-000015"));

        Assert.Single(cut.FindAll(".activity-item"));
        cut.Find("[data-action=include-views]").Change(true);

        Assert.Equal(2, cut.FindAll(".activity-item").Count);
        Assert.Equal([false, true], feed.IncludeReadsRequests);
        Assert.Contains("view", cut.FindAll(".activity-item")[1].ClassList);
    }

    [Fact]
    public void The_activity_of_an_unreadable_object_is_an_error_without_rows()
    {
        feed.NotFound = true;
        feed.Entries.Add(Entry("alice did something"));
        var cut = Render<ActivityFeed>(p => p.Add(x => x.Kind, "part").Add(x => x.Number, "P-000015"));
        Assert.Contains("not available", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("alice did something", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_empty_feed_says_what_will_appear()
    {
        var cut = Render<ActivityFeed>(p => p.Add(x => x.Kind, "part").Add(x => x.Number, "P-000015"));
        Assert.Contains("No activity yet", cut.Markup, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- tabs and count

    [Fact]
    public void Object_pages_get_an_activity_tab_and_a_comments_tab_with_the_count()
    {
        var own = new List<Keel.Web.Components.Shared.TabInfo> { new("overview", "Overview") };
        var tabs = AwarenessTabs.Append(own, 7);
        Assert.Equal(["overview", "activity", "comments"], tabs.Select(t => t.Id));
        Assert.Equal(7, tabs[^1].Count);
        Assert.Null(AwarenessTabs.Append(own, null)[^1].Count);
    }

    [Fact]
    public void The_comment_count_loader_reports_the_live_count_and_renders_nothing()
    {
        comments.Threads.AddRange([Comment("Alice", "a"), Comment("Bob", "b"), Comment("Carol", "c", removed: true)]);
        int? reported = -1;
        var cut = Render<CommentCount>(p => p.Add(x => x.Kind, "part").Add(x => x.Number, "P-000015").Add(x => x.OnCount, EventCallback.Factory.Create<int?>(this, c => reported = c)));
        Assert.Equal(2, reported);
        Assert.Equal("", cut.Markup.Trim());
    }
}
