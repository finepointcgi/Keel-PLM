using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Keel.Core;
using Keel.Notifications;
using Keel.Web.Services;

namespace Keel.UnitTests;

/// <summary>UX-4 (ADR-0030): the pure rules of notifications, comments and the activity feed.</summary>
public sealed partial class AwarenessUnitTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }

    // ---------------------------------------------------------------- kind registry

    [Fact]
    public void Every_notification_kind_has_a_unique_code_a_group_text_and_a_reason_when_it_cannot_be_turned_off()
    {
        Assert.Equal(NotificationKinds.All.Length, NotificationKinds.All.Select(k => k.Code).Distinct().Count());
        foreach (var k in NotificationKinds.All)
        {
            Assert.Matches("^[a-z][a-z_]{1,48}$", k.Code); // the database check constraint
            Assert.Contains(k.Group, new[] { NotificationGroups.Mentions, NotificationGroups.Tasks, NotificationGroups.Watching });
            Assert.False(string.IsNullOrWhiteSpace(k.Label));
            Assert.False(string.IsNullOrWhiteSpace(k.Description));
            // Every sentence names who did it, except the certification notices of the supplier master (8.2) and the overdue and expiry notices of quality records (8.1), which the system raises on its own.
            if (k.Code is not (NotificationKindCodes.SupplierCertExpiring or NotificationKindCodes.SupplierCertExpired or NotificationKindCodes.QualityActionOverdue or NotificationKindCodes.DeviationExpiring or NotificationKindCodes.DeviationExpired)) Assert.Contains("{actor}", k.Sentence, StringComparison.Ordinal);
            Assert.Equal(!k.CanDisable, !string.IsNullOrWhiteSpace(k.LockedReason)); // a reason exactly when it is locked
        }
        // Every code the modules use is a registered kind (no module can raise an unknown kind).
        var codes = typeof(NotificationKindCodes).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal([.. codes.Order()], [.. NotificationKinds.All.Select(k => k.Code).Order()]);
    }

    [Fact]
    public void Mentions_and_tasks_cannot_be_turned_off_and_a_mention_outranks_everything_else()
    {
        foreach (var code in new[] { NotificationKindCodes.CommentMention, NotificationKindCodes.TaskAssigned, NotificationKindCodes.ReviewRequested, NotificationKindCodes.ChangeTaskAssigned })
            Assert.False(NotificationKinds.Find(code)!.CanDisable, code);
        Assert.Equal(NotificationKinds.All.Max(k => k.Priority), NotificationKinds.Find(NotificationKindCodes.CommentMention)!.Priority);
        Assert.Equal(NotificationKinds.All.Count(k => k.Collapsible), NotificationKinds.All.Count(k => k.Code is NotificationKindCodes.WatchedChanged or NotificationKindCodes.WatchedState));
        Assert.Null(NotificationKinds.Find("no_such_kind"));
    }

    [Fact]
    public void A_notification_leads_to_the_object_the_comment_the_task_or_the_review_tab()
    {
        var comment = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var task = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Assert.Equal($"parts/P-000015?comment={comment:D}#comments", NotificationKinds.Href(NotificationKinds.Find(NotificationKindCodes.CommentMention)!, "part", "P-000015", comment));
        Assert.Equal($"tasks/{task:D}", NotificationKinds.Href(NotificationKinds.Find(NotificationKindCodes.ReviewRequested)!, "part", "P-000015", task));
        Assert.Equal("parts/P-000015#review", NotificationKinds.Href(NotificationKinds.Find(NotificationKindCodes.ReviewApproved)!, "part", "P-000015", null));
        Assert.Equal("documents/D-000002#overview", NotificationKinds.Href(NotificationKinds.Find(NotificationKindCodes.ReviewRejected)!, "document", "D-000002", null));
        Assert.Equal("changes/ECN-000002#tasks", NotificationKinds.Href(NotificationKinds.Find(NotificationKindCodes.ChangeTaskAssigned)!, "change", "ECN-000002", null));
        Assert.Equal("parts/P-000015#activity", NotificationKinds.Href(NotificationKinds.Find(NotificationKindCodes.WatchedChanged)!, "part", "P-000015", null));
        Assert.Equal("baselines/BL-000001", NotificationKinds.Href(NotificationKinds.Find(NotificationKindCodes.WatchedEstablished)!, "baseline", "BL-000001", null));
        // The number is escaped: a pointer can never smuggle a path or a query into the link.
        Assert.Equal("parts/P%2F..%2F1", NotificationKinds.Href(NotificationKinds.Find(NotificationKindCodes.WatchedReleased)!, "part", "P/../1", null));
    }

    [Fact]
    public void The_sentence_names_the_actor_or_says_someone()
    {
        var kind = NotificationKinds.Find(NotificationKindCodes.CommentMention)!;
        Assert.Equal("Alice Example mentioned you in a comment", NotificationKinds.Sentence(kind, "Alice Example"));
        Assert.Equal("Someone mentioned you in a comment", NotificationKinds.Sentence(kind, null));
        Assert.Equal("Released (by someone)", NotificationKinds.Sentence(NotificationKinds.Find(NotificationKindCodes.WatchedReleased)!, ""));
    }

    // ---------------------------------------------------------------- dedupe window

    [Theory]
    [InlineData(true, 0, false, 5, true)]    // unread, just now: fold into the existing row
    [InlineData(true, 299, false, 5, true)]  // just inside the five-minute window
    [InlineData(true, 301, false, 5, false)] // just outside
    [InlineData(true, 10, true, 5, false)]   // the user already read it: the next change is news again
    [InlineData(false, 10, false, 5, false)] // kinds that are not collapsible never fold
    [InlineData(true, 10, false, 0, false)]  // a window of zero turns the collapse off
    public void Repeated_changes_collapse_only_into_an_unread_row_inside_the_window(bool collapsible, int ageSeconds, bool read, int windowMinutes, bool expected)
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, NotificationDedupe.ShouldCollapse(collapsible, now - TimeSpan.FromSeconds(ageSeconds), read, now, TimeSpan.FromMinutes(windowMinutes)));
    }

    [Fact]
    public void A_future_dated_row_never_collapses()
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        Assert.False(NotificationDedupe.ShouldCollapse(true, now.AddMinutes(1), false, now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void The_options_are_validated()
    {
        Assert.Empty(new NotificationOptions().Validate());
        Assert.NotEmpty(new NotificationOptions { MaxPerUser = 1 }.Validate());
        Assert.NotEmpty(new NotificationOptions { RetentionDays = -1 }.Validate());
        Assert.NotEmpty(new NotificationOptions { FanOutPollSeconds = 0 }.Validate());
        Assert.Equal(0, new NotificationOptions().RetentionDays); // retention is off unless an operator turns it on
        Assert.Equal(500, new NotificationOptions().MaxPerUser);
    }

    // ---------------------------------------------------------------- comment text

    [Theory]
    [InlineData("Hello @alice and @Bob.", new[] { "alice", "bob" })]
    [InlineData("@alice @alice again", new[] { "alice" })]
    [InlineData("mail me at alice@example.com", new string[0])] // an address is not a mention
    [InlineData("@@alice", new string[0])]
    [InlineData("see (@carol.smith), thanks", new[] { "carol.smith" })]
    [InlineData("trailing punctuation @dave.", new[] { "dave" })]
    [InlineData("<script>alert(1)</script> @eve", new[] { "eve" })]
    public void Mentions_are_found_by_form_alone(string body, string[] expected) => Assert.Equal(expected, CommentText.Mentions(body));

    [Fact]
    public void At_most_ten_people_are_mentioned_in_one_comment()
    {
        var body = string.Join(' ', Enumerable.Range(1, 30).Select(i => $"@user{i}"));
        Assert.Equal(AwarenessLimits.MaxMentionsPerComment, CommentText.Mentions(body).Count);
    }

    [Fact]
    public void Object_numbers_are_recognized_and_nothing_else_looks_like_one()
    {
        Assert.Equal(["P-000015", "ECN-000002"], CommentText.References("Compare P-000015 with ECN-000002, and P-000015 again."));
        Assert.Empty(CommentText.References("version A-1 and the p-000015 in lower case and X-12"));
        var segments = CommentText.Segments("See P-000015, @alice!");
        Assert.Equal([SegmentKind.Text, SegmentKind.Reference, SegmentKind.Text, SegmentKind.Mention, SegmentKind.Text], segments.Select(s => s.Kind));
        Assert.Equal("See P-000015, @alice!", string.Concat(segments.Select(s => s.Text))); // nothing is lost or invented
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   \n  ", false)]
    [InlineData("ok", true)]
    [InlineData("two\nlines\twith a tab", true)]
    [InlineData("bell\u0007character", false)]
    [InlineData("null\0byte", false)]
    public void A_comment_is_non_empty_plain_text(string? body, bool valid) => Assert.Equal(valid, CommentText.Validate(body) is null);

    [Fact]
    public void A_comment_is_at_most_four_thousand_characters()
    {
        Assert.Null(CommentText.Validate(new string('x', 4000)));
        Assert.NotNull(CommentText.Validate(new string('x', 4001)));
        Assert.Equal("a\nb\nc", CommentText.Normalize("  a\r\nb\rc  "));
    }

    [Theory]
    [InlineData("hello @al", "al")]
    [InlineData("hello @", "")]
    [InlineData("@", "")]
    [InlineData("hello @alice and", null)]
    [InlineData("mail alice@exa", null)]
    [InlineData("no mention", null)]
    public void The_suggestion_list_follows_the_mention_being_typed(string text, string? expected) =>
        Assert.Equal(expected, Keel.Web.Components.Shared.Awareness.CommentThread.CurrentMention(text));

    // ---------------------------------------------------------------- fan-out rules

    [Fact]
    public void Outbox_events_map_to_watcher_notifications()
    {
        FanOutRules.Target[] One(string type, string aggregate, string number, JsonObject? payload = null) => [.. FanOutRules.Map(type, aggregate, number, payload)];
        Assert.Equal([new FanOutRules.Target(NotificationKindCodes.WatchedReleased, "part", "P-000001")], One(EventTypes.PartReleased, "part", "P-000001"));
        Assert.Equal([new FanOutRules.Target(NotificationKindCodes.WatchedObsoleted, "document", "D-000001")], One(EventTypes.DocumentObsoleted, "document", "D-000001"));
        Assert.Equal([new FanOutRules.Target(NotificationKindCodes.WatchedApproved, "change", "ECR-000001")], One(EventTypes.ChangeApproved, "ECR", "ECR-000001"));
        Assert.Equal([new FanOutRules.Target(NotificationKindCodes.WatchedReleased, "change", "ECN-000001")], One(EventTypes.ChangeReleased, "ECN", "ECN-000001"));
        // A baseline established for a part also tells the part's watchers; so does a manufacturing BOM released for it.
        var baseline = One(EventTypes.BaselineEstablished, "baseline", "BL-000001", JsonNode.Parse("""{"root":{"number":"P-000010"}}""")!.AsObject());
        Assert.Equal([new FanOutRules.Target(NotificationKindCodes.WatchedEstablished, "baseline", "BL-000001"), new FanOutRules.Target(NotificationKindCodes.WatchedEstablished, "part", "P-000010")], baseline);
        var mbom = One(EventTypes.MbomReleased, "mbom", "MB-000001", JsonNode.Parse("""{"part":{"number":"P-000010"}}""")!.AsObject());
        Assert.Contains(new FanOutRules.Target(NotificationKindCodes.WatchedEstablished, "part", "P-000010"), mbom);
        // The BOM publication events of the ERP contract mean nothing to a watcher.
        Assert.Empty(One(EventTypes.BomPublished, "part", "P-000001"));
        Assert.Empty(One(EventTypes.BomStructurePublished, "part", "P-000001"));
        Assert.Empty(One("something.else", "part", "P-000001"));
    }

    // ---------------------------------------------------------------- hub

    [Fact]
    public async Task The_hub_signals_only_the_subscribed_user_and_stops_after_dispose()
    {
        var hub = new NotificationHub();
        var alice = 0;
        var bob = 0;
        using var a = hub.Subscribe("alice", () => Interlocked.Increment(ref alice));
        var b = hub.Subscribe("bob", () => Interlocked.Increment(ref bob));
        hub.Publish("alice", TimeSpan.Zero);
        hub.Publish("nobody", TimeSpan.Zero);
        Assert.Equal((1, 0), (alice, bob));
        b.Dispose();
        hub.Publish("bob", TimeSpan.Zero);
        Assert.Equal((1, 0), (alice, bob));
        Assert.Equal(0, hub.SubscriberCount("bob"));

        hub.Publish("alice", TimeSpan.FromMilliseconds(30)); // a delayed signal arrives later, not at once
        Assert.Equal(1, alice);
        await Task.Delay(300);
        Assert.Equal(2, alice);
    }

    [Fact]
    public void A_subscriber_that_throws_does_not_stop_the_others()
    {
        var hub = new NotificationHub();
        var heard = 0;
        using var bad = hub.Subscribe("u", () => throw new ObjectDisposedException("circuit"));
        using var good = hub.Subscribe("u", () => heard++);
        hub.Publish("u", TimeSpan.Zero);
        Assert.Equal(1, heard);
    }

    // ---------------------------------------------------------------- activity catalog

    [Fact]
    public void Every_action_of_the_code_base_is_a_feed_event_a_view_or_explicitly_hidden()
    {
        // Every string that looks like an audit action in the sources of the modules. A new action that is in none of the three lists fails here, so it cannot reach the
        // feed as a raw code (and a new read cannot leak into the colleagues' view of what happened).
        var literal = ActionLiteral();
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.EndsWith("ActivityCatalog.cs", StringComparison.Ordinal)))
            foreach (Match m in literal.Matches(File.ReadAllText(file))) found.Add(m.Groups[1].Value);
        Assert.True(found.Count > 150, $"The scan found only {found.Count} actions; the pattern is probably broken.");
        var unknown = found.Where(a => !ActivityCatalog.IsKnown(a)).ToList();
        Assert.True(unknown.Count == 0, "Actions that the activity feed neither shows, nor lists as a view, nor hides on purpose:\n" + string.Join("\n", unknown));
    }

    [Fact]
    public void The_catalog_has_no_duplicates_no_overlaps_and_safe_sentences()
    {
        Assert.Equal(ActivityCatalog.Rules.Count, ActivityCatalog.Rules.Select(r => r.Action).Distinct().Count());
        Assert.Empty(ActivityCatalog.Rules.Select(r => r.Action).Intersect(ActivityCatalog.Views.Keys));
        Assert.DoesNotContain(ActivityCatalog.Rules.Select(r => r.Action), ActivityCatalog.IsHidden); // an event shown cannot also be hidden
        Assert.DoesNotContain(ActivityCatalog.Views.Keys, ActivityCatalog.IsHidden);
        foreach (var r in ActivityCatalog.Rules)
        {
            Assert.False(r.Sentence.Contains('.', StringComparison.Ordinal) && Regex.IsMatch(r.Sentence, @"\b[a-z]+\.[a-z_]+\b"), $"{r.Action}: a raw action code in the sentence");
            // Every key a sentence needs is a declared (safe) field of the rule, and a rule that needs keys has a fallback for when one is missing.
            foreach (var key in r.RequiredKeys) Assert.Contains(r.Fields, f => f.Key == key);
            if (r.RequiredKeys.Any()) Assert.False(string.IsNullOrEmpty(r.Fallback), $"{r.Action} needs a fallback sentence");
            if (r.Fallback is not null) Assert.Empty(new ActivityRule(r.Action, r.Category, r.Fallback, null, []).RequiredKeys);
        }
        // Nothing in the feed shows a body: comment text and downloads are not event fields.
        Assert.DoesNotContain(ActivityCatalog.Rules.SelectMany(r => r.Fields), f => f.Key is "body" or "text" or "sha256");
    }

    [Fact]
    public void Sentences_fall_back_when_a_detail_is_missing_and_never_show_a_placeholder()
    {
        var checkin = ActivityCatalog.Find("part.checkin")!;
        Assert.Equal("alice checked in version A.2", ActivityService.Render(checkin, "alice", new Dictionary<string, string> { ["revision"] = "A.2" }));
        Assert.Equal("alice checked in a new version", ActivityService.Render(checkin, "alice", new Dictionary<string, string>()));
        var transition = ActivityCatalog.Find("part.transition")!;
        Assert.Equal("bob moved the part from In work to Under review", ActivityService.Render(transition, "bob", new Dictionary<string, string> { ["from"] = "In work", ["to"] = "Under review" }));
        foreach (var rule in ActivityCatalog.Rules)
            Assert.DoesNotMatch(@"\{[A-Za-z]+\}", ActivityService.Render(rule, "someone", new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("InWork", "In work")]
    [InlineData("UnderReview", "Under review")]
    [InlineData("Released", "Released")]
    [InlineData("in_work", "In work")]
    public void State_codes_read_as_words(string code, string expected) => Assert.Equal(expected, ActivityCatalog.Prettify(code));

    [Fact]
    public void Audit_payloads_yield_the_login_name_and_only_string_details()
    {
        const string payload = """{"v":1,"action":"part.checkin","actor":{"id":"sub-alice","name":"alice"},"details":{"revision":"A.2","comment":"first","count":null}}""";
        var (actor, details) = ActivityService.ParsePayload(payload, "sub-alice");
        Assert.Equal("alice", actor);
        Assert.Equal("A.2", details["revision"]);
        Assert.Null(details["count"]);
        Assert.Equal(("sub-x", 0), (ActivityService.ParsePayload("not json", "sub-x").Actor, ActivityService.ParsePayload("not json", "sub-x").Details.Count));
    }

    [Fact]
    public void The_markings_of_a_change_are_shown_as_the_banner_they_make_never_as_fields()
    {
        const string before = """{"jurisdiction":"Itar","Eccn":null,"UsmlCategory":"XI(d)","cui":["EXPT"],"distribution":"D","ControllingOffice":"ACME","DistributionReason":"Critical technology","Program":"ORCA","OwningOrg":"ENG","authorizations":[]}""";
        var banner = ActivityService.MarkingsBanner(before);
        Assert.Contains("ITAR", banner, StringComparison.Ordinal);
        Assert.Contains("DISTRIBUTION D", banner, StringComparison.Ordinal);
        Assert.DoesNotContain("{", banner, StringComparison.Ordinal);
        Assert.DoesNotContain("ControllingOffice", banner, StringComparison.Ordinal);
        Assert.Equal("Markings changed", ActivityService.MarkingsBanner("{\"jurisdiction\":\"Nowhere\"}"));
        Assert.Equal("Markings changed", ActivityService.MarkingsBanner("{not json"));
        // A banner text (changes, baselines and manufacturing BOMs record it directly) passes through untouched.
        Assert.Equal("CUI//EXPT · DISTRIBUTION D", ActivityService.Format(new ActivityField("before", "Before"), "CUI//EXPT · DISTRIBUTION D"));
    }

    [Fact]
    public void Runs_of_small_edits_by_one_person_collapse_and_other_events_do_not()
    {
        var t = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        ActivityEntry E(long seq, int minutesAgo, string actor, string category, string sentence) => new(seq, t.AddMinutes(-minutesAgo), actor, sentence, category, false, [], null);
        var collapsed = ActivityCollapse.Collapse(
        [
            E(6, 0, "alice", "edit", "alice edited the working copy"), E(5, 2, "alice", "edit", "alice edited the working copy"), E(4, 4, "alice", "edit", "alice edited the working copy"),
            E(3, 5, "bob", "edit", "bob edited the working copy"), E(2, 6, "alice", "version", "alice checked in version A.2"), E(1, 7, "alice", "version", "alice checked in version A.3"),
        ], ActivityService.CollapseWindow);
        Assert.Equal(["alice edited the working copy (3 times)", "bob edited the working copy", "alice checked in version A.2", "alice checked in version A.3"], collapsed.Select(c => c.Sentence));
        Assert.Equal(6, collapsed[0].Seq); // the newest of the run stays on top
        // Two edits far apart are two lines.
        Assert.Equal(2, ActivityCollapse.Collapse([E(2, 0, "alice", "edit", "x"), E(1, 120, "alice", "edit", "x")], ActivityService.CollapseWindow).Count);
    }

    // ---------------------------------------------------------------- text helpers

    [Theory]
    [InlineData(10, "just now")]
    [InlineData(120, "2 min ago")]
    [InlineData(3 * 3600, "3 h ago")]
    [InlineData(3 * 86400, "2026-10-25")]
    public void Times_are_relative_and_old_ones_are_dates(int ageSeconds, string expected)
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, RelativeTime.Format(now - TimeSpan.FromSeconds(ageSeconds), now));
    }

    [Fact]
    public void Day_groups_say_today_yesterday_or_the_date()
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("Today", RelativeTime.DayHeading(now.AddHours(-3), now));
        Assert.Equal("Yesterday", RelativeTime.DayHeading(now.AddDays(-1), now));
        Assert.Equal("20 October 2026", RelativeTime.DayHeading(now.AddDays(-8), now));
    }

    [Fact]
    public void Drafts_are_kept_per_object_and_reply_and_capped()
    {
        var drafts = new CommentDrafts();
        drafts.Set("part", "P-000001", "half a thought");
        drafts.Set("part", "P-000002", "another");
        var reply = Guid.NewGuid();
        drafts.Set("part", "P-000001", "a reply", reply);
        Assert.Equal("half a thought", drafts.Get("part", "P-000001"));
        Assert.Equal("a reply", drafts.Get("part", "P-000001", reply));
        Assert.Equal("another", drafts.Get("part", "P-000002"));
        drafts.Clear("part", "P-000001");
        Assert.Equal("", drafts.Get("part", "P-000001"));
        drafts.Set("part", "P-000002", ""); // an emptied box forgets the draft
        Assert.Equal("", drafts.Get("part", "P-000002"));
        drafts.Set("part", "P-000003", new string('x', 10_000));
        Assert.Equal(AwarenessLimits.CommentLength, drafts.Get("part", "P-000003").Length);
    }

    [Fact]
    public void Awareness_kinds_map_audit_object_types_and_numbers()
    {
        Assert.Equal("part", AwarenessKinds.AuditObjectType("part", "P-000001"));
        Assert.Equal("ecn", AwarenessKinds.AuditObjectType("change", "ECN-000002"));
        Assert.Equal("pr", AwarenessKinds.AuditObjectType("change", "PR-000003"));
        Assert.Null(AwarenessKinds.AuditObjectType("change", "XX-1"));
        Assert.Equal("change", AwarenessKinds.FromAuditObjectType("ecr"));
        Assert.Null(AwarenessKinds.FromAuditObjectType("session"));
        Assert.Null(AwarenessKinds.FromAuditObjectType(null));
    }

    [GeneratedRegex(@"""((?:part|document|change|workflow|baseline|mbom|bom|comment|activity|watch|access|file|vault|siem|audit|session|workspace|migration|effectivity|classification|admin|integration|tdp)\.[a-z_]+(?:\.[a-z_]+)*)""")]
    private static partial Regex ActionLiteral();
}
