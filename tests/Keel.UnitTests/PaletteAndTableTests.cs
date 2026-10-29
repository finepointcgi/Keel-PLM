using Bunit;
using Keel.Core;
using Keel.Web.Components.Layout;
using Keel.Web.Components.Shared;
using Keel.Web.Services;
using Keel.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using static Keel.UnitTests.ShellTestKit;

namespace Keel.UnitTests;

/// <summary>The command palette (keyboard behavior with a fake search provider) and the shared data table (sorting, column chooser, density, states).</summary>
public sealed class PaletteAndTableTests : BunitContext
{
    private readonly FakePreferenceStore _store;
    private readonly FakeSearch _search;

    public PaletteAndTableTests()
    {
        (_store, _search) = Register(this, Roles.Viewer);
        Services.AddSingleton<TimeProvider>(TimeProvider.System);
    }

    private IRenderedComponent<CommandPalette> OpenPalette(string[]? roles = null)
    {
        if (roles is not null) Services.AddSingleton<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(new FixedAuth(Principal(roles)));
        var cut = Render<CommandPalette>(p => p.Add(x => x.DebounceMs, 5));
        cut.InvokeAsync(() => Services.GetRequiredService<PaletteState>().Open());
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[role=dialog]")));
        return cut;
    }

    private static string[] Labels(IRenderedComponent<CommandPalette> cut) => cut.FindAll("[role=option] .name").Select(n => n.TextContent.Trim()).ToArray();

    // ---------------------------------------------------------------- palette

    [Fact]
    public void The_palette_is_a_modal_combobox_over_a_listbox_with_a_focus_trap_and_the_shortcuts_registered()
    {
        var cut = OpenPalette();

        var dialog = cut.Find("[role=dialog]");
        Assert.Equal("true", dialog.GetAttribute("aria-modal"));
        var input = cut.Find("input[role=combobox]");
        Assert.Equal("palette-list", input.GetAttribute("aria-controls"));
        Assert.Equal("listbox", cut.Find("#palette-list").GetAttribute("role"));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "registerShortcuts"); // Ctrl/Cmd+K and "/" are listened for in the browser module
        var trap = Assert.Single(JSInterop.Invocations, i => i.Identifier == "trapFocus");
        Assert.Equal("[data-palette-input]", trap.Arguments[1]);
    }

    [Fact]
    public void With_nothing_typed_it_offers_recent_items_and_the_places_the_roles_allow()
    {
        _search.Recents = [Hit(ObjectKinds.Part, "P-000001", "Gimbal bracket")];
        var cut = OpenPalette();

        cut.WaitForAssertion(() => Assert.Contains("P-000001 · Gimbal bracket", Labels(cut)));
        Assert.Contains("Go to Parts", Labels(cut));
        Assert.Contains("Toggle theme", Labels(cut));
        Assert.Contains("Sign out", Labels(cut));
        // a viewer can neither create things nor administer
        Assert.DoesNotContain("New part", Labels(cut));
        Assert.DoesNotContain("Go to Administration", Labels(cut));
        Assert.DoesNotContain("Go to Audit log", Labels(cut));
    }

    [Fact]
    public void Authors_see_the_create_actions()
    {
        var author = OpenPalette([Roles.Author]);
        Assert.Contains("New part", Labels(author));
        Assert.Contains("New document", Labels(author));
        Assert.Contains("New change request", Labels(author));
    }

    [Fact]
    public void Administrators_and_auditors_see_their_places()
    {
        var admin = OpenPalette([Roles.Admin, Roles.Auditor]);
        Assert.Contains("Go to Administration", Labels(admin));
        Assert.Contains("Go to Audit log", Labels(admin));
    }

    [Fact]
    public void Typing_searches_after_a_pause_and_lists_number_name_kind_state_and_marking_chip()
    {
        _search.Handler = (q, ct) => Task.FromResult<IReadOnlyList<SearchHit>>([Hit(ObjectKinds.Part, "P-000123", "Bracket", "CUI//EXPT · ITAR · DISTRIBUTION D"), Hit(ObjectKinds.Document, "D-000009", "Bracket drawing")]);
        var cut = OpenPalette();

        cut.Find("input[role=combobox]").Input("brack");

        cut.WaitForAssertion(() => Assert.Contains("P-000123 · Bracket", Labels(cut)));
        var first = cut.FindAll("[role=option]").First(o => o.TextContent.Contains("P-000123", StringComparison.Ordinal));
        Assert.Contains("Released", first.TextContent, StringComparison.Ordinal);
        Assert.Contains("CUI//EXPT", first.QuerySelector(".chip")!.TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(first.QuerySelectorAll("svg.icon"));
        Assert.Contains(cut.FindAll("li.group-label"), g => g.TextContent == "Parts");
        Assert.Contains(cut.FindAll("li.group-label"), g => g.TextContent == "Documents");
        Assert.Contains(Labels(cut), l => l.StartsWith("See all results for", StringComparison.Ordinal));
    }

    [Fact]
    public void A_single_character_does_not_search_and_a_typed_prefix_reaches_the_provider_as_typed()
    {
        var cut = OpenPalette();
        cut.Find("input[role=combobox]").Input("p");
        Thread.Sleep(60);
        Assert.Empty(_search.Queries);

        cut.Find("input[role=combobox]").Input("ecn: wall");
        cut.WaitForAssertion(() => Assert.Contains("ecn: wall", _search.Queries));
    }

    [Fact]
    public void A_newer_keystroke_cancels_the_older_request_so_stale_results_never_show()
    {
        _search.Handler = async (q, ct) =>
        {
            if (q == "slow")
            {
                await Task.Delay(500, ct);
                return [Hit(ObjectKinds.Part, "P-000001", "Stale result")];
            }
            return [Hit(ObjectKinds.Part, "P-000002", "Fresh result")];
        };
        var cut = OpenPalette();
        var input = cut.Find("input[role=combobox]");

        input.Input("slow");
        Thread.Sleep(40); // the slow request is in flight
        input.Input("fast");

        cut.WaitForAssertion(() => Assert.Contains("P-000002 · Fresh result", Labels(cut)));
        Thread.Sleep(600);
        Assert.DoesNotContain("P-000001 · Stale result", Labels(cut));
    }

    [Fact]
    public void Arrow_keys_move_the_selection_enter_opens_it_and_escape_closes_the_palette()
    {
        _search.Handler = (q, ct) => Task.FromResult<IReadOnlyList<SearchHit>>([Hit(ObjectKinds.Part, "P-000010", "First"), Hit(ObjectKinds.Part, "P-000011", "Second")]);
        var cut = OpenPalette();
        var input = cut.Find("input[role=combobox]");
        input.Input("part");
        cut.WaitForAssertion(() => Assert.Contains("P-000011 · Second", Labels(cut)));

        Assert.Equal("true", cut.FindAll("[role=option]")[0].GetAttribute("aria-selected"));
        Assert.Equal("palette-option-0", cut.Find("input[role=combobox]").GetAttribute("aria-activedescendant"));
        cut.Find("input[role=combobox]").KeyDown(Key.Down);
        Assert.Equal("true", cut.FindAll("[role=option]")[1].GetAttribute("aria-selected"));
        Assert.Equal("palette-option-1", cut.Find("input[role=combobox]").GetAttribute("aria-activedescendant"));
        cut.Find("input[role=combobox]").KeyDown(Key.Up);
        cut.Find("input[role=combobox]").KeyDown(Key.Up); // wraps to the last entry
        Assert.Equal("true", cut.FindAll("[role=option]")[^1].GetAttribute("aria-selected"));

        cut.Find("input[role=combobox]").KeyDown(Key.Down); // back to the first
        cut.Find("input[role=combobox]").KeyDown(Key.Down);
        cut.Find("input[role=combobox]").KeyDown(Key.Enter);

        Assert.EndsWith("/parts/P-000011", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[role=dialog]")));

        cut.InvokeAsync(() => Services.GetRequiredService<PaletteState>().Open());
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[role=dialog]")));
        cut.Find("[role=dialog]").KeyDown(Key.Escape);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[role=dialog]")));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "releaseTrap"); // the focus returns to what opened it
    }

    [Fact]
    public void Control_enter_opens_the_selection_in_a_new_tab_and_the_palette_stays_open()
    {
        _search.Handler = (q, ct) => Task.FromResult<IReadOnlyList<SearchHit>>([Hit(ObjectKinds.Document, "D-000042", "Spec")]);
        var cut = OpenPalette();
        cut.Find("input[role=combobox]").Input("spec");
        cut.WaitForAssertion(() => Assert.Contains("D-000042 · Spec", Labels(cut)));

        cut.Find("input[role=combobox]").KeyDown(new KeyboardEventArgs { Key = "Enter", CtrlKey = true });
        Thread.Sleep(20);

        var call = Assert.Single(JSInterop.Invocations, i => i.Identifier == "openInNewTab");
        Assert.EndsWith("/documents/D-000042", (string?)call.Arguments[0], StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("[role=dialog]"));
    }

    [Fact]
    public void The_toggle_theme_command_changes_and_saves_the_theme()
    {
        var cut = OpenPalette();
        cut.Find("input[role=combobox]").Input("toggle");
        cut.WaitForAssertion(() => Assert.Equal("Toggle theme", Labels(cut)[0]));

        cut.Find("input[role=combobox]").KeyDown(Key.Enter);

        cut.WaitForAssertion(() => Assert.Equal(ThemeNames.Light, _store.Preferences.Theme));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "applyPreferences" && (string?)i.Arguments[0] == "light");
    }

    [Fact]
    public void A_provider_failure_shows_a_calm_message_and_leaves_the_palette_usable()
    {
        _search.Handler = (q, ct) => throw new InvalidOperationException("db down");
        var cut = OpenPalette();
        cut.Find("input[role=combobox]").Input("anything");

        cut.WaitForAssertion(() => Assert.Contains("not available right now", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("db down", cut.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("[role=dialog]"));
    }

    // ---------------------------------------------------------------- data table

    private sealed record Row(string Number, string Name, int Qty);

    private static readonly Row[] Rows = [new("P-3", "Charlie", 30), new("P-1", "Alpha", 10), new("P-2", "Bravo", 20)];

    private static IReadOnlyList<DataColumn<Row>> Columns(bool serverSortOnName = false) =>
    [
        new() { Key = "number", Title = "Number", Required = true, SortValue = r => r.Number, Cell = r => b => b.AddMarkupContent(0, $"<a href=\"parts/{r.Number}\">{r.Number}</a>") },
        serverSortOnName ? new() { Key = "name", Title = "Name", ServerSortKey = "name", Text = r => r.Name } : new() { Key = "name", Title = "Name", SortValue = r => r.Name, Text = r => r.Name },
        new() { Key = "qty", Title = "Quantity", SortValue = r => r.Qty, Text = r => r.Qty.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        new() { Key = "note", Title = "Note", DefaultHidden = true, Text = r => "note of " + r.Number },
    ];

    private IRenderedComponent<DataTable<Row>> RenderTable(IReadOnlyList<Row>? items = null, IReadOnlyList<DataColumn<Row>>? columns = null, bool loading = false, string? error = null) =>
        Render<DataTable<Row>>(p => p.Add(x => x.TableId, "demo").Add(x => x.Items, items ?? Rows).Add(x => x.Columns, columns ?? Columns()).Add(x => x.Caption, "Demo rows")
            .Add(x => x.Loading, loading).Add(x => x.Error, error)
            .Add(x => x.Empty, (RenderFragment)(b => b.AddMarkupContent(0, "<p id=\"nothing\">Nothing here</p>"))));

    private static string[] Numbers(IRenderedComponent<DataTable<Row>> cut) => cut.FindAll("tbody tr td:first-child").Select(c => c.TextContent.Trim()).ToArray();

    [Fact]
    public void Sorting_a_header_toggles_ascending_and_descending_and_is_announced_with_aria_sort()
    {
        var cut = RenderTable();
        Assert.Equal(["P-3", "P-1", "P-2"], Numbers(cut)); // the page's own order until a sort is chosen
        Assert.Equal("none", cut.FindAll("th")[1].GetAttribute("aria-sort"));
        Assert.Contains("rows of this page", cut.FindAll("th button.sort")[0].GetAttribute("title"), StringComparison.Ordinal); // the header says the sort is within the page

        cut.FindAll("th button.sort")[0].Click(); // Number
        Assert.Equal(["P-1", "P-2", "P-3"], Numbers(cut));
        Assert.Equal("ascending", cut.FindAll("th")[0].GetAttribute("aria-sort"));
        cut.FindAll("th button.sort")[0].Click();
        Assert.Equal(["P-3", "P-2", "P-1"], Numbers(cut));
        Assert.Equal("descending", cut.FindAll("th")[0].GetAttribute("aria-sort"));

        cut.FindAll("th button.sort")[2].Click(); // Quantity (numeric, not text, order)
        Assert.Equal(["P-1", "P-2", "P-3"], Numbers(cut));
        Assert.Equal("none", cut.FindAll("th")[0].GetAttribute("aria-sort"));
        Assert.Equal("ascending", cut.FindAll("th")[2].GetAttribute("aria-sort"));
        Assert.Contains(_store.Tables, t => t.Key == "demo" && t.Value.SortColumn == "qty");
    }

    [Fact]
    public void A_server_sorted_column_asks_the_page_instead_of_sorting_here()
    {
        (string Key, bool Descending)? asked = null;
        var cut = Render<DataTable<Row>>(p => p.Add(x => x.TableId, "demo").Add(x => x.Items, Rows).Add(x => x.Columns, Columns(serverSortOnName: true))
            .Add(x => x.OnServerSort, EventCallback.Factory.Create<(string Key, bool Descending)>(this, v => asked = v)));

        cut.FindAll("th button.sort").First(b => b.TextContent.Contains("Name", StringComparison.Ordinal)).Click();

        Assert.Equal(("name", false), asked);
        Assert.Equal(["P-3", "P-1", "P-2"], Numbers(cut)); // rows stay as the server returned them
    }

    [Fact]
    public void A_server_sorted_table_shows_the_pages_order_marks_it_with_aria_sort_and_keeps_it_in_the_address()
    {
        var asked = new List<(string Key, bool Descending)>();
        _store.Tables["demo"] = new TablePreferences([], null, "number", true); // an old saved sort does not apply: the order comes from the page
        var columns = new List<DataColumn<Row>>
        {
            new() { Key = "number", Title = "Number", Required = true, ServerSortKey = "number", Text = r => r.Number },
            new() { Key = "name", Title = "Name", ServerSortKey = "name", Text = r => r.Name },
            new() { Key = "qty", Title = "Quantity", SortValue = r => r.Qty, Text = r => r.Qty.ToString(System.Globalization.CultureInfo.InvariantCulture) }, // not whitelisted: not sortable here
        };
        var cut = Render<DataTable<Row>>(p => p.Add(x => x.TableId, "demo").Add(x => x.Items, Rows).Add(x => x.Columns, columns).Add(x => x.ServerSort, new SortSpec("name", true))
            .Add(x => x.OnServerSort, EventCallback.Factory.Create<(string Key, bool Descending)>(this, v => asked.Add(v))));

        Assert.Equal(["P-3", "P-1", "P-2"], Numbers(cut));                        // exactly the order the server returned
        Assert.Equal("descending", cut.FindAll("th")[1].GetAttribute("aria-sort"));
        Assert.Equal("none", cut.FindAll("th")[0].GetAttribute("aria-sort"));
        Assert.Null(cut.FindAll("th")[2].GetAttribute("aria-sort"));              // the quantity column cannot be sorted by the server
        Assert.Empty(cut.FindAll("th")[2].QuerySelectorAll("button.sort"));
        Assert.Contains("Sort all results", cut.FindAll("th button.sort")[0].GetAttribute("title"), StringComparison.Ordinal);

        cut.FindAll("th button.sort")[0].Click(); // another column: ascending
        cut.FindAll("th button.sort")[1].Click(); // the sorted column again: ascending (it is descending now)
        Assert.Equal([("number", false), ("name", false)], asked);
        var setQuery = JSInterop.Invocations.Where(i => i.Identifier == "setQuery").ToList();
        Assert.Equal(2, setQuery.Count);
        Assert.Equal(["sort", "number", "dir", "asc"], setQuery[0].Arguments.Select(a => a?.ToString()));
        Assert.Equal(["sort", "name", "dir", "asc"], setQuery[1].Arguments.Select(a => a?.ToString()));
        Assert.Equal(["P-3", "P-1", "P-2"], Numbers(cut)); // the table never re-sorts the rows itself
    }

    [Fact]
    public void Choosing_the_sorted_column_again_flips_the_direction_for_the_server()
    {
        var asked = new List<(string Key, bool Descending)>();
        var columns = new List<DataColumn<Row>> { new() { Key = "name", Title = "Name", Required = true, ServerSortKey = "name", Text = r => r.Name } };
        var cut = Render<DataTable<Row>>(p => p.Add(x => x.TableId, "demo").Add(x => x.Items, Rows).Add(x => x.Columns, columns).Add(x => x.ServerSort, new SortSpec("name", false))
            .Add(x => x.OnServerSort, EventCallback.Factory.Create<(string Key, bool Descending)>(this, v => asked.Add(v))));
        cut.Find("th button.sort").Click();
        Assert.Equal([("name", true)], asked);
        Assert.Equal("ascending", cut.Find("th").GetAttribute("aria-sort")); // the indicator follows the page's order, which the page updates
        cut.Render(p => p.Add(x => x.ServerSort, new SortSpec("name", true)));
        Assert.Equal("descending", cut.Find("th").GetAttribute("aria-sort"));
    }

    [Fact]
    public void The_column_chooser_hides_and_shows_columns_and_saves_the_choice_per_table()
    {
        var cut = RenderTable();
        Assert.DoesNotContain("Note", cut.FindAll("th").Select(h => h.TextContent), StringComparer.Ordinal); // hidden by default
        cut.Find("[data-action=columns]").Click();
        var boxes = cut.FindAll(".dt-popover input[type=checkbox]");
        Assert.Equal(3, boxes.Count); // the identifying column is not offered
        Assert.DoesNotContain("Number", cut.Find(".dt-popover").TextContent, StringComparison.Ordinal);

        boxes[0].Change(false); // Name off
        cut.FindAll(".dt-popover input[type=checkbox]")[2].Change(true); // Note on

        var headers = cut.FindAll("th").Select(h => h.TextContent.Trim()).ToArray();
        Assert.DoesNotContain("Name", headers);
        Assert.Contains("Note", headers);
        Assert.Contains("name", _store.Tables["demo"].HiddenColumns);
        Assert.DoesNotContain("note", _store.Tables["demo"].HiddenColumns);
    }

    [Fact]
    public void Saved_layout_sort_and_density_are_applied_when_the_table_opens()
    {
        _store.Tables["demo"] = new TablePreferences(["qty"], "compact", "number", true);
        var cut = RenderTable();

        Assert.Equal("compact", cut.Find(".dt").GetAttribute("data-density"));
        Assert.DoesNotContain("Quantity", cut.FindAll("th").Select(h => h.TextContent.Trim()));
        Assert.Equal(["P-3", "P-2", "P-1"], Numbers(cut));
        Assert.Equal("descending", cut.FindAll("th")[0].GetAttribute("aria-sort"));
    }

    [Fact]
    public void The_density_toggle_switches_rows_and_saves()
    {
        var cut = RenderTable();
        Assert.Equal("comfortable", cut.Find(".dt").GetAttribute("data-density"));

        cut.Find("[data-action=density]").Click();

        Assert.Equal("compact", cut.Find(".dt").GetAttribute("data-density"));
        Assert.Equal("true", cut.Find("[data-action=density]").GetAttribute("aria-pressed"));
        Assert.Equal("compact", _store.Tables["demo"].Density);
    }

    [Fact]
    public void Loading_error_and_empty_states_replace_the_table()
    {
        var loading = RenderTable(loading: true);
        Assert.NotEmpty(loading.FindAll(".skeleton"));
        Assert.Empty(loading.FindAll("table"));

        var failed = RenderTable(error: "The list could not be loaded.");
        Assert.Equal("The list could not be loaded.", failed.Find("[role=alert]").TextContent);

        var empty = RenderTable(items: []);
        Assert.Equal("Nothing here", empty.Find("#nothing").TextContent);
        Assert.Empty(empty.FindAll("table"));
    }

    [Fact]
    public void The_table_has_a_caption_a_sticky_header_scope_and_arrow_key_row_navigation()
    {
        var cut = RenderTable();

        Assert.Equal("Demo rows", cut.Find("caption").TextContent);
        Assert.All(cut.FindAll("th"), h => Assert.Equal("col", h.GetAttribute("scope")));
        Assert.Contains("dt-scroll", cut.Find("table").ParentElement!.ClassName, StringComparison.Ordinal); // scrolls inside its own container
        Assert.Equal(["0", "-1", "-1"], cut.FindAll("tbody tr").Select(r => r.GetAttribute("tabindex")));

        cut.Find("tbody").KeyDown(Key.Down);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "focusSelector" && (string?)i.Arguments[0] == "#dt-demo-row-1");
        cut.Find("tbody").KeyDown(Key.End);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "focusSelector" && (string?)i.Arguments[0] == "#dt-demo-row-2");
        cut.Find("tbody").KeyDown(Key.Enter);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "clickFirstLink" && (string?)i.Arguments[0] == "#dt-demo-row-2");
    }
}
