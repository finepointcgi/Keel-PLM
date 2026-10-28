using Bunit;
using Keel.Bom;
using Keel.Core;
using Keel.Web.Components.Pages.Bom;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Keel.UnitTests;

public sealed class BomGridComponentTests : BunitContext
{
    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)",
        cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;
    private static readonly Markings Open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;

    public BomGridComponentTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static BomLine Line(string path, bool hasChildren = false, bool redacted = false, bool unresolved = false, Markings? markings = null, decimal qty = 2)
    {
        var parts = path.Split('.');
        var find = int.Parse(parts[^1], System.Globalization.CultureInfo.InvariantCulture);
        var m = redacted ? null : markings ?? Open;
        return new BomLine(Guid.NewGuid(), null, parts.Length, path, find, qty, "EA", qty * 3, redacted, unresolved, false, hasChildren && !redacted,
            redacted ? null : Guid.NewGuid(), redacted ? null : $"P-{path}", redacted ? null : $"Part {path}", redacted ? null : "Component",
            redacted || unresolved ? null : "A", redacted || unresolved ? null : 1, redacted ? null : "InWork", redacted ? null : "In Work", [], null,
            m?.BannerText(), m);
    }

    private IRenderedComponent<BomTreeGrid> Grid(IReadOnlyList<BomLine> lines, Func<BomLine, Task<IReadOnlyList<BomLine>>>? load = null, Action<IReadOnlyList<BomLine>>? visible = null) =>
        Render<BomTreeGrid>(p => p
            .Add(x => x.Lines, lines)
            .Add(x => x.LoadChildren, load)
            .Add(x => x.OnVisibleChanged, EventCallback.Factory.Create<IReadOnlyList<BomLine>>(this, rows => visible?.Invoke(rows))));

    [Fact]
    public void Only_a_window_of_a_large_structure_is_rendered()
    {
        var lines = Enumerable.Range(1, 10_000).Select(i => Line((i * 10).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
        var cut = Grid(lines);

        var rendered = cut.FindAll("[role=row][data-path]");
        Assert.InRange(rendered.Count, 1, 200); // the Virtualize window, not 10,000 rows
        Assert.Equal("10001", cut.Find("[role=treegrid]").GetAttribute("aria-rowcount")); // the full size is still announced
        Assert.Equal("10", rendered[0].GetAttribute("data-path"));
    }

    [Fact]
    public void Row_shows_part_columns_and_a_restricted_row_shows_only_quantities()
    {
        var cut = Grid([Line("10", markings: Itar), Line("20", redacted: true, qty: 4)]);
        var rows = cut.FindAll("[role=row][data-path]");

        Assert.Contains("P-10", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Part 10", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("A.1", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("In Work", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("ITAR USML XI(d)", rows[0].QuerySelector(".chip")!.TextContent, StringComparison.Ordinal);

        Assert.Contains("restricted", rows[1].ClassName, StringComparison.Ordinal);
        Assert.Contains("Restricted item", rows[1].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("P-20", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Null(rows[1].QuerySelector(".chip"));
        Assert.Null(rows[1].QuerySelector("a")); // no link to a part the user cannot see
        Assert.Null(rows[1].QuerySelector("button")); // and nothing to expand
        Assert.Contains("4", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("12", rows[1].TextContent, StringComparison.Ordinal); // extended quantity still shown
    }

    [Fact]
    public void Unresolved_lines_are_flagged_not_dropped()
    {
        var cut = Grid([Line("10", unresolved: true)]);
        Assert.Contains("No released revision", cut.Find("[role=row][data-path]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expanding_loads_one_level_lazily_and_collapsing_removes_the_subtree()
    {
        var loaded = new List<string>();
        var cut = Grid([Line("10", hasChildren: true), Line("20")], line =>
        {
            loaded.Add(line.Path);
            return Task.FromResult<IReadOnlyList<BomLine>>([Line($"{line.Path}.10", hasChildren: true), Line($"{line.Path}.20")]);
        });
        Assert.Equal(2, cut.FindAll("[role=row][data-path]").Count);
        Assert.Equal("false", cut.Find("[data-path='10']").GetAttribute("aria-expanded"));
        Assert.Null(cut.Find("[data-path='20']").GetAttribute("aria-expanded")); // leaf

        await cut.Find("[data-path='10'] button.twisty").ClickAsync(new MouseEventArgs());
        Assert.Equal(["10"], loaded);
        Assert.Equal(["10", "10.10", "10.20", "20"], cut.FindAll("[role=row][data-path]").Select(r => r.GetAttribute("data-path")!));
        Assert.Equal("true", cut.Find("[data-path='10']").GetAttribute("aria-expanded"));
        Assert.Equal("2", cut.Find("[data-path='10.10']").GetAttribute("aria-level"));

        await cut.Find("[data-path='10.10'] button.twisty").ClickAsync(new MouseEventArgs());
        Assert.Equal(["10", "10.10"], loaded);
        Assert.Equal(6, cut.FindAll("[role=row][data-path]").Count);

        await cut.Find("[data-path='10'] button.twisty").ClickAsync(new MouseEventArgs()); // collapse the top line: whole subtree goes
        Assert.Equal(["10", "20"], cut.FindAll("[role=row][data-path]").Select(r => r.GetAttribute("data-path")!));
        Assert.Equal(["10", "10.10"], loaded); // collapsing never reloads
    }

    [Fact]
    public async Task Keyboard_right_expands_left_collapses_and_arrows_move_the_focus_stop()
    {
        var cut = Grid([Line("10", hasChildren: true), Line("20")],
            line => Task.FromResult<IReadOnlyList<BomLine>>([Line($"{line.Path}.10")]));

        await cut.Find("[data-path='10']").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowRight" });
        Assert.Equal(3, cut.FindAll("[role=row][data-path]").Count);
        await cut.Find("[data-path='10']").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        Assert.Equal("0", cut.Find("[data-path='10.10']").GetAttribute("tabindex"));
        Assert.Equal("-1", cut.Find("[data-path='10']").GetAttribute("tabindex"));
        await cut.Find("[data-path='10.10']").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowLeft" }); // no children: moves to the parent
        Assert.Equal("0", cut.Find("[data-path='10']").GetAttribute("tabindex"));
        await cut.Find("[data-path='10']").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowLeft" });
        Assert.Equal(2, cut.FindAll("[role=row][data-path]").Count);
        await cut.Find("[data-path='10']").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        Assert.Equal(3, cut.FindAll("[role=row][data-path]").Count);
    }

    [Fact]
    public async Task The_displayed_rows_are_reported_so_the_page_banner_is_their_aggregate()
    {
        IReadOnlyList<BomLine> seen = [];
        var cut = Grid([Line("10", hasChildren: true), Line("20", redacted: true)],
            line => Task.FromResult<IReadOnlyList<BomLine>>([Line($"{line.Path}.10", markings: Itar)]), rows => seen = rows);

        Assert.Equal("UNCLASSIFIED · DISTRIBUTION A", BomTreeModel.Banner(Open, seen));
        await cut.Find("[data-path='10'] button.twisty").ClickAsync(new MouseEventArgs());
        Assert.Equal(3, seen.Count);
        Assert.Equal("CUI//EXPT · ITAR · DISTRIBUTION D", BomTreeModel.Banner(Open, seen));
        // Restricted rows contribute nothing to the banner, and collapsing takes the controlled row away again.
        await cut.Find("[data-path='10'] button.twisty").ClickAsync(new MouseEventArgs());
        Assert.Equal("UNCLASSIFIED · DISTRIBUTION A", BomTreeModel.Banner(Open, seen));
    }

    [Fact]
    public void Tree_model_expand_and_collapse_are_pure_list_operations()
    {
        var rows = new List<BomLine> { Line("10", hasChildren: true), Line("20", hasChildren: true), Line("30") };
        var expanded = BomTreeModel.Expand(rows, 1, [Line("20.10"), Line("20.20")]);
        Assert.Equal(["10", "20", "20.10", "20.20", "30"], expanded.Select(r => r.Path));
        Assert.True(BomTreeModel.IsExpanded(expanded, 1));
        Assert.False(BomTreeModel.IsExpanded(expanded, 0));
        Assert.Equal(2, BomTreeModel.SubtreeSize(expanded, 1));
        Assert.Equal(["10", "20", "30"], BomTreeModel.Collapse(expanded, 1).Select(r => r.Path));
        Assert.False(BomTreeModel.CanExpand(Line("40", hasChildren: true, redacted: true)));
    }
}
