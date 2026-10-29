using Bunit;
using Keel.Core;
using Keel.TestSupport;
using Keel.Web.Components.Layout;
using Keel.Web.Components.Shared;
using Keel.Web.Services;
using Keel.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using static Keel.UnitTests.ShellTestKit;

namespace Keel.UnitTests;

/// <summary>bUnit tests of the UX-1 building blocks: navigation, breadcrumbs, stepper, tabs, feedback components and the page frame.</summary>
public sealed class ShellComponentTests : BunitContext
{
    private readonly ManualTimeProvider _clock = new();

    public ShellComponentTests()
    {
        Register(this, Roles.Viewer);
        Services.AddSingleton<TimeProvider>(_clock);
    }

    // ---------------------------------------------------------------- breadcrumbs

    [Fact]
    public void Breadcrumbs_start_at_home_and_the_last_level_is_the_current_page_without_a_link()
    {
        var cut = Render<Breadcrumbs>(p => p.Add(x => x.Items, [new Crumb("Parts", "parts"), new Crumb("P-000123")]));

        Assert.Equal("Breadcrumb", cut.Find("nav").GetAttribute("aria-label"));
        Assert.Equal(["Home", "Parts"], cut.FindAll("a").Select(a => a.TextContent));
        var current = cut.Find("li[aria-current=page]");
        Assert.Equal("P-000123", current.TextContent.Trim());
        Assert.Empty(current.QuerySelectorAll("a"));
    }

    [Fact]
    public void Breadcrumbs_render_nothing_without_levels()
    {
        Assert.Empty(Render<Breadcrumbs>().FindAll("nav"));
    }

    // ---------------------------------------------------------------- navigation rail

    [Fact]
    public void The_rail_groups_links_and_shows_only_what_the_roles_allow()
    {
        var viewer = Render<RailNav>(p => p.Add(x => x.Groups, NavModel.For([Roles.Viewer])));
        Assert.Equal("Main", viewer.Find("nav").GetAttribute("aria-label"));
        Assert.Equal(["Work", "Product data", "Change", "Tools"], viewer.FindAll("[role=group]").Select(g => g.GetAttribute("aria-label")));
        Assert.DoesNotContain("Administration", viewer.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Audit log", viewer.Markup, StringComparison.Ordinal);

        var admin = Render<RailNav>(p => p.Add(x => x.Groups, NavModel.For([Roles.Admin, Roles.Auditor])));
        Assert.Contains("Administration", admin.FindAll("[role=group]").Select(g => g.GetAttribute("aria-label")));
        Assert.Contains("Audit log", admin.Markup, StringComparison.Ordinal);
        Assert.All(admin.FindAll("a.rail-link"), a => Assert.NotEmpty(a.QuerySelectorAll("svg.icon use")));
        Assert.All(admin.FindAll("a.rail-link"), a => Assert.False(string.IsNullOrEmpty(a.GetAttribute("title")))); // the tooltip of a collapsed rail
    }

    [Fact]
    public void The_rail_collapse_button_reports_its_state_and_raises_the_change()
    {
        bool? requested = null;
        var cut = Render<RailNav>(p => p.Add(x => x.Groups, NavModel.For([])).Add(x => x.Collapsed, false).Add(x => x.OnToggle, EventCallback.Factory.Create<bool>(this, v => requested = v)));
        var toggle = cut.Find("[data-action=collapse-nav]");
        Assert.Equal("false", toggle.GetAttribute("aria-pressed"));

        toggle.Click();

        Assert.True(requested);
    }

    // ---------------------------------------------------------------- stepper

    private static readonly LifecycleState[] States =
    [
        new("InWork", "In Work", 10, true, false), new("UnderReview", "Under Review", 20, false, true),
        new("Released", "Released", 30, false, true), new("Obsolete", "Obsolete", 40, false, true),
    ];

    [Fact]
    public void The_stepper_marks_the_current_state_done_states_locked_states_and_reachable_ones()
    {
        var cut = Render<Stepper>(p => p.Add(x => x.States, States).Add(x => x.Current, "UnderReview").Add(x => x.Reachable, new HashSet<string> { "Released", "InWork" }));

        Assert.Equal(4, cut.FindAll("li").Count);
        Assert.Equal("step", cut.Find("li.current").GetAttribute("aria-current"));
        Assert.Equal("UnderReview", cut.Find("li.current").GetAttribute("data-state"));
        Assert.Contains("done", cut.Find("[data-state=InWork]").ClassList);
        Assert.Contains("next", cut.Find("[data-state=Released]").ClassList);
        Assert.Contains("locked: content cannot be edited", cut.Find("[data-state=Released]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("locked", cut.Find("[data-state=InWork]").TextContent, StringComparison.Ordinal);
        Assert.Contains("(current state)", cut.Find("li.current").TextContent, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- tabs

    private static readonly TabInfo[] Tabs = [new("overview", "Overview"), new("documents", "Documents", 3), new("history", "History")];

    [Fact]
    public void Tabs_follow_the_aria_tabs_pattern_and_arrow_keys_move_between_them()
    {
        string? changed = null;
        var cut = Render<TabSet>(p => p.Add(x => x.Tabs, Tabs).Add(x => x.Active, "overview").Add(x => x.ActiveChanged, EventCallback.Factory.Create<string>(this, v => changed = v))
            .AddChildContent("<p id=\"panel-body\">body</p>"));

        Assert.Equal("tablist", cut.Find(".tablist").GetAttribute("role"));
        var tabs = cut.FindAll("[role=tab]");
        Assert.Equal(["true", "false", "false"], tabs.Select(t => t.GetAttribute("aria-selected")));
        Assert.Equal(["0", "-1", "-1"], tabs.Select(t => t.GetAttribute("tabindex"))); // roving tab index: one stop in the tab order
        Assert.Equal("tabpanel-overview", cut.Find("[role=tabpanel]").Id);
        Assert.Equal("tab-overview", cut.Find("[role=tabpanel]").GetAttribute("aria-labelledby"));
        Assert.Equal("3", cut.Find("#tab-documents .count").TextContent);

        cut.Find("#tab-overview").KeyDown(Key.Right);

        Assert.Equal("documents", changed);
        Assert.Equal("true", cut.Find("#tab-documents").GetAttribute("aria-selected"));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "setHash" && (string?)i.Arguments[0] == "documents"); // deep-linkable, without a navigation
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "focusSelector" && (string?)i.Arguments[0] == "#tab-documents");

        cut.Find("#tab-documents").KeyDown(Key.End);
        Assert.Equal("history", changed);
        cut.Find("#tab-history").KeyDown(Key.Right); // wraps
        Assert.Equal("overview", changed);
    }

    [Fact]
    public void A_tab_can_be_named_by_the_address_fragment()
    {
        string[] ids = ["overview", "documents"];
        Assert.Equal("documents", TabSet.FromUri("http://localhost/parts/P-1#documents", ids, "overview"));
        Assert.Equal("overview", TabSet.FromUri("http://localhost/parts/P-1#nope", ids, "overview"));
        Assert.Equal("overview", TabSet.FromUri("http://localhost/parts/P-1", ids, "overview"));
    }

    // ---------------------------------------------------------------- toasts

    [Fact]
    public void Toasts_go_to_a_polite_or_an_assertive_live_region_by_severity_and_never_take_focus()
    {
        var toasts = Services.GetRequiredService<ToastService>();
        var cut = Render<ToastContainer>();

        cut.InvokeAsync(() => { toasts.Success("Checked in."); toasts.Error("Could not save."); });

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Checked in.", cut.Find("[data-toast-region=polite]").TextContent, StringComparison.Ordinal);
            Assert.Contains("Could not save.", cut.Find("[data-toast-region=assertive]").TextContent, StringComparison.Ordinal);
        });
        Assert.Equal("polite", cut.Find("[data-toast-region=polite]").GetAttribute("aria-live"));
        Assert.Equal("assertive", cut.Find("[data-toast-region=assertive]").GetAttribute("aria-live"));
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier is "focusSelector" or "focusElement" or "trapFocus"); // announced, never focused
        Assert.Empty(cut.FindAll("[autofocus]"));
        Assert.Empty(cut.FindAll("[tabindex]"));
    }

    [Fact]
    public void The_toast_queue_is_limited_and_a_toast_can_be_dismissed()
    {
        var toasts = Services.GetRequiredService<ToastService>();
        var cut = Render<ToastContainer>();
        cut.InvokeAsync(() => { for (var i = 1; i <= 6; i++) toasts.Info($"message {i}"); });

        cut.WaitForAssertion(() => Assert.Equal(ToastService.Limit, cut.FindAll("[data-toast]").Count));
        Assert.DoesNotContain("message 1", cut.Markup, StringComparison.Ordinal); // the oldest were displaced
        Assert.Contains("message 6", cut.Markup, StringComparison.Ordinal);

        cut.Find("[data-toast] .dismiss").Click();
        cut.WaitForAssertion(() => Assert.Equal(ToastService.Limit - 1, cut.FindAll("[data-toast]").Count));
    }

    [Fact]
    public void A_toast_dismisses_itself_but_hovering_pauses_the_countdown()
    {
        var toasts = Services.GetRequiredService<ToastService>();
        var cut = Render<ToastContainer>();
        cut.InvokeAsync(() => { toasts.Success("first"); toasts.Error("second"); });
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-toast]").Count));

        cut.Find("[data-toast=success]").MouseEnter();
        _clock.Advance(TimeSpan.FromSeconds(30));
        // the error (12 s) is gone, the hovered confirmation (6 s) is still there
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-toast=error]")));
        Assert.Single(cut.FindAll("[data-toast=success]"));

        cut.Find("[data-toast=success]").MouseLeave();
        _clock.Advance(TimeSpan.FromSeconds(7));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-toast]")));
    }

    [Fact]
    public void Toast_lifetimes_give_problems_more_time_than_confirmations()
    {
        Assert.True(ToastService.Lifetime(ToastSeverity.Error) > ToastService.Lifetime(ToastSeverity.Success));
        Assert.True(ToastService.Lifetime(ToastSeverity.Warning) > ToastService.Lifetime(ToastSeverity.Info));
    }

    // ---------------------------------------------------------------- confirm dialog

    [Fact]
    public void A_destructive_confirmation_is_a_modal_dialog_that_starts_on_cancel_and_traps_focus()
    {
        var cut = Render<ConfirmDialog>(p => p.Add(x => x.Open, true).Add(x => x.Title, "Undo check-out?").Add(x => x.Destructive, true).Add(x => x.ConfirmText, "Undo check-out")
            .AddChildContent("<p>Edits are lost.</p>"));

        var dialog = cut.Find("[role=dialog]");
        Assert.Equal("true", dialog.GetAttribute("aria-modal"));
        Assert.Equal("Undo check-out?", cut.Find("#" + dialog.GetAttribute("aria-labelledby")).TextContent);
        Assert.Contains("Edits are lost.", cut.Find("#" + dialog.GetAttribute("aria-describedby")).TextContent, StringComparison.Ordinal);
        Assert.Contains("danger", cut.Find("[data-confirm-ok]").ClassList);
        var trap = Assert.Single(JSInterop.Invocations, i => i.Identifier == "trapFocus");
        Assert.Equal("[data-confirm-cancel]", trap.Arguments[1]); // the safe choice has the focus first
    }

    [Fact]
    public void Cancel_and_escape_close_the_dialog_without_confirming_and_confirm_confirms()
    {
        var confirmed = 0;
        var cancelled = 0;
        var cut = Render<ConfirmDialog>(p => p.Add(x => x.Open, true).Add(x => x.Title, "Sure?")
            .Add(x => x.OnConfirm, EventCallback.Factory.Create<string?>(this, _ => confirmed++)).Add(x => x.OnCancel, EventCallback.Factory.Create(this, () => cancelled++)));

        cut.Find("[role=dialog]").KeyDown(Key.Escape);
        Assert.Equal(1, cancelled);
        Assert.Equal(0, confirmed);
        Assert.Empty(cut.FindAll("[role=dialog]"));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "releaseTrap"); // focus goes back to where it was

        cut.Render(p => p.Add(x => x.Open, true));
        cut.Find("[data-confirm-ok]").Click();
        Assert.Equal(1, confirmed);
    }

    [Fact]
    public void A_required_reason_is_asked_for_and_passed_on()
    {
        string? received = null;
        var cut = Render<ConfirmDialog>(p => p.Add(x => x.Open, true).Add(x => x.Title, "Set state?").Add(x => x.ReasonLabel, "Reason for this override").Add(x => x.ReasonRequired, true)
            .Add(x => x.OnConfirm, EventCallback.Factory.Create<string?>(this, r => received = r)));

        cut.Find("[data-confirm-ok]").Click();
        Assert.Equal("Enter a reason to continue.", cut.Find("[role=alert]").TextContent);
        Assert.Null(received);

        cut.Find("textarea").Input("Migration correction");
        cut.Find("[data-confirm-ok]").Click();
        Assert.Equal("Migration correction", received);
    }

    // ---------------------------------------------------------------- empty state and skeleton

    [Fact]
    public void An_empty_state_says_what_to_do_next_and_a_skeleton_is_announced_once()
    {
        var empty = Render<EmptyState>(p => p.Add(x => x.Title, "No parts yet").Add(x => x.ActionHref, "parts/new").Add(x => x.ActionText, "New part").AddChildContent("Create the first part."));
        Assert.Equal("No parts yet", empty.Find("h3").TextContent);
        Assert.Equal("parts/new", empty.Find("a.button").GetAttribute("href"));
        Assert.Contains("Create the first part.", empty.Find("p").TextContent, StringComparison.Ordinal);

        var skeleton = Render<Skeleton>(p => p.Add(x => x.Lines, 4).Add(x => x.Label, "Loading parts"));
        Assert.Equal("status", skeleton.Find(".skeleton").GetAttribute("role"));
        Assert.Equal("true", skeleton.Find(".skeleton").GetAttribute("aria-busy"));
        Assert.Equal(4, skeleton.FindAll(".bar").Count);
        Assert.Equal("Loading parts", skeleton.Find(".visually-hidden").TextContent);
    }

    // ---------------------------------------------------------------- page frame

    private static RenderFragment Body => b => b.AddMarkupContent(0, "<p>body</p>");

    [Fact]
    public void A_plain_page_has_one_h1_and_breadcrumbs_and_an_object_page_brings_its_own_h1()
    {
        var plain = Render<PageFrame>(p => p.Add(x => x.Banner, "UNCLASSIFIED").Add(x => x.Title, "Parts").Add(x => x.Crumbs, [new Crumb("Parts")]).Add(x => x.ChildContent, Body));
        Assert.Single(plain.FindAll("h1"));
        Assert.Equal("Parts", plain.Find("h1").TextContent);
        Assert.NotNull(plain.Find("nav[aria-label=Breadcrumb]"));

        var objectPage = Render<PageFrame>(p => p.Add(x => x.Banner, "CUI//EXPT").Add(x => x.Title, "P-000123 · Bracket").Add(x => x.ChildContent, Body)
            .Add(x => x.Header, (RenderFragment)(b =>
            {
                b.OpenComponent<ObjectHeader>(0);
                b.AddAttribute(1, nameof(ObjectHeader.Title), "P-000123 · Bracket");
                b.AddAttribute(2, nameof(ObjectHeader.Banner), "CUI//EXPT · ITAR · DISTRIBUTION D");
                b.CloseComponent();
            })));
        Assert.Single(objectPage.FindAll("h1"));
        Assert.Equal("P-000123 · Bracket", objectPage.Find("h1").TextContent);
        Assert.Equal("-1", objectPage.Find("h1").GetAttribute("tabindex")); // the router moves the focus here after navigation
        Assert.Contains("CUI//EXPT", objectPage.Find(".page-header .chip").TextContent, StringComparison.Ordinal); // the object's own chip is always shown
    }

    // ---------------------------------------------------------------- dashboard card

    [Fact]
    public async Task A_dashboard_card_shows_a_skeleton_then_its_items_and_reports_the_banner_of_what_it_shows()
    {
        var gate = new TaskCompletionSource<CardResult<ResolvedItem>>();
        var reported = new List<(string, string?)>();
        var markings = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;
        var cut = Render<Keel.Web.Components.Pages.Dashboard.ObjectCard>(p => p.Add(x => x.Id, "recent").Add(x => x.Title, "Recently viewed").Add(x => x.Load, () => gate.Task)
            .Add(x => x.OnBanner, EventCallback.Factory.Create<(string Id, string? Banner)>(this, u => reported.Add(u))));
        Assert.NotEmpty(cut.FindAll(".skeleton"));

        gate.SetResult(new CardResult<ResolvedItem>([new ResolvedItem(ObjectKinds.Part, "P-000123", "Bracket", "Released", markings, "parts/P-000123", DateTimeOffset.UnixEpoch)], markings.BannerText()));
        await cut.InvokeAsync(() => { });

        cut.WaitForAssertion(() => Assert.Contains("P-000123 · Bracket", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("parts/P-000123", cut.Find("li a").GetAttribute("href"));
        Assert.Contains("CUI//EXPT", cut.Find("li .chip").TextContent, StringComparison.Ordinal);
        Assert.Equal(("recent", markings.BannerText()), reported.Last());
    }

    [Fact]
    public void A_dashboard_card_fails_on_its_own_and_an_empty_card_guides_the_user()
    {
        var failing = Render<Keel.Web.Components.Pages.Dashboard.ObjectCard>(p => p.Add(x => x.Id, "a").Add(x => x.Title, "A").Add(x => x.Load, () => throw new InvalidOperationException("boom")));
        failing.WaitForAssertion(() => Assert.Contains("could not be loaded", failing.Find("[role=alert]").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("boom", failing.Markup, StringComparison.Ordinal); // no internals on screen

        var empty = Render<Keel.Web.Components.Pages.Dashboard.ObjectCard>(p => p.Add(x => x.Id, "b").Add(x => x.Title, "B").Add(x => x.EmptyTitle, "No favorites yet")
            .Add(x => x.Load, () => Task.FromResult(new CardResult<ResolvedItem>([], UiText.Unclassified))));
        empty.WaitForAssertion(() => Assert.Equal("No favorites yet", empty.Find(".empty h3").TextContent));
    }

    // ---------------------------------------------------------------- theme switch in the user menu

    [Fact]
    public void Choosing_a_theme_in_the_user_menu_applies_and_saves_it()
    {
        var store = (FakePreferenceStore)Services.GetRequiredService<IPreferenceStore>();
        var cut = Render<UserMenu>(p => p.Add(x => x.DisplayName, "Alice Example"));
        Assert.Empty(cut.FindAll("#user-menu-panel"));
        cut.Find("#user-menu-button").Click();
        Assert.Equal("true", cut.Find("#user-menu-button").GetAttribute("aria-expanded"));
        Assert.Equal("true", cut.Find("[data-theme-choice=system]").GetAttribute("aria-pressed"));

        cut.Find("[data-theme-choice=dark]").Click();

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-theme-choice=dark]").GetAttribute("aria-pressed")));
        Assert.Equal("dark", store.Preferences.Theme);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "applyPreferences" && (string?)i.Arguments[0] == "dark");

        cut.Find("[data-density-choice=compact]").Click();
        cut.WaitForAssertion(() => Assert.Equal(DensityNames.Compact, store.Preferences.Density));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "applyPreferences" && (string?)i.Arguments[1] == "compact");
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Sign out", StringComparison.Ordinal) && b.GetAttribute("form") == "signout-form");
    }

    [Fact]
    public async Task Preferences_that_cannot_be_saved_do_not_break_the_shell()
    {
        var shell = Services.GetRequiredService<ShellState>();
        await shell.LoadAsync();
        await shell.SetThemeAsync("high-contrast");
        Assert.Equal("high-contrast", shell.Preferences.Theme);
        await shell.SetThemeAsync("neon"); // unknown values fall back, they never reach the page as attributes
        Assert.Equal("system", shell.Preferences.Theme);
        await shell.ToggleThemeAsync();
        Assert.Equal("light", shell.Preferences.Theme);
        await shell.ToggleThemeAsync();
        Assert.Equal("dark", shell.Preferences.Theme);
        await shell.ToggleThemeAsync();
        Assert.Equal("high-contrast", shell.Preferences.Theme);
    }
}
