using Bunit;
using Keel.Admin;
using Keel.Core;
using Keel.Web.Components.Shared;
using Keel.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.UnitTests;

/// <summary>bUnit tests of the administration and classification components. They are presentational: they take data and callbacks, so no service is mocked.</summary>
public sealed class AdminComponentTests : BunitContext
{
    public AdminComponentTests()
    {
        // The shared confirmation dialog (focus trap) reaches the browser module; its behavior is not under test here.
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Keel.Web/js/shell.js");
        Services.AddScoped<ShellJs>();
    }

    [Fact]
    public void The_admin_navigation_lists_every_section_with_an_accessible_name()
    {
        var cut = Render<AdminNav>();
        Assert.Equal("Administration", cut.Find("nav").GetAttribute("aria-label"));
        var links = cut.FindAll("a").Select(a => a.TextContent).ToList();
        foreach (var expected in new[] { "Attributes", "Lifecycles", "Types and lists", "Workflow templates", "Classification", "Numbering", "Export and import", "Change history" })
            Assert.Contains(expected, links);
    }

    [Fact]
    public void Validation_messages_are_announced_with_their_roles()
    {
        var cut = Render<ValidationList>(p => p.Add(x => x.Errors, ["A is wrong.", "B is wrong."]).Add(x => x.Warnings, ["Heads up."]).Add(x => x.Effects, ["Added state Q."]));
        Assert.Equal("alert", cut.Find(".error-box").GetAttribute("role"));
        Assert.Contains("2 problems", cut.Find(".error-box strong").TextContent, StringComparison.Ordinal);
        Assert.Equal(2, cut.FindAll(".error-box li").Count);
        Assert.Equal(2, cut.FindAll("[role=status]").Count);
        Assert.Empty(Render<ValidationList>().FindAll("div"));
    }

    private static ClassTree SampleTree()
    {
        var root = new ClassNodeDto(Guid.NewGuid(), "MECH", "Mechanical", "", null, "/MECH/", 1, 0, "active");
        var child = new ClassNodeDto(Guid.NewGuid(), "FAST", "Fasteners", "", root.Id, "/MECH/FAST/", 2, 0, "active");
        var retired = new ClassNodeDto(Guid.NewGuid(), "OLD", "Old", "", root.Id, "/MECH/OLD/", 2, 1, "retired");
        return new ClassTree([root, child, retired], []);
    }

    [Fact]
    public void The_class_tree_shows_counts_selection_and_retired_classes_and_reports_clicks()
    {
        string? clicked = null;
        var counts = new Dictionary<string, long> { ["MECH"] = 7, ["FAST"] = 3 };
        var cut = Render<ClassTreeNav>(p => p.Add(x => x.Rows, AdminUi.Rows(SampleTree())).Add(x => x.Selected, "FAST").Add(x => x.Counts, counts)
            .Add(x => x.OnSelect, EventCallback.Factory.Create<string>(this, c => clicked = c)));

        Assert.Equal("tree", cut.Find("ul").GetAttribute("role"));
        var items = cut.FindAll("button[role=treeitem]");
        Assert.Equal(3, items.Count);
        Assert.Equal(["1", "2", "2"], items.Select(i => i.GetAttribute("aria-level")));
        Assert.Equal("true", items[1].GetAttribute("aria-selected"));
        Assert.Equal("false", items[0].GetAttribute("aria-selected"));
        Assert.Contains("7", items[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Retired", items[2].TextContent, StringComparison.Ordinal);
        items[0].Click();
        Assert.Equal("MECH", clicked);
        Assert.Single(Render<ClassTreeNav>(p => p.Add(x => x.Rows, AdminUi.Rows(SampleTree(), activeOnly: true)).Add(x => x.Counts, counts)).FindAll("li.indent-1"));
    }

    [Fact]
    public void The_facet_panel_toggles_values_ranges_and_flags_and_applies_them()
    {
        var selection = new FacetSelection();
        var applied = 0;
        AttributeFacet[] facets =
        [
            new("grade", "Material grade", "enum", null, [new FacetValue("304", 5), new FacetValue("316", 2)], null, null, 7),
            new("diameter", "Diameter", "number", "mm", [], 4, 20, 7),
            new("critical", "Critical", "boolean", null, [new FacetValue("true", 1), new FacetValue("false", 6)], null, null, 7),
        ];
        var cut = Render<FacetPanel>(p => p.Add(x => x.Facets, facets).Add(x => x.Selection, selection).Add(x => x.OnChanged, EventCallback.Factory.Create(this, () => applied++)));

        Assert.Equal(["Material grade", "Diameter (mm)", "Critical"], cut.FindAll("legend").Select(l => l.TextContent));
        Assert.Contains("5", cut.FindAll("input[type=checkbox]")[0].ParentElement!.TextContent, StringComparison.Ordinal); // the count is shown
        cut.FindAll("input[type=checkbox]")[1].Change(true);
        cut.FindAll(".range input")[0].Change("6");
        cut.FindAll(".range input")[1].Change("12");
        cut.FindAll("input[type=radio]")[1].Change(true); // "Yes"
        cut.Find("form").Submit();

        var filters = selection.ToFilters();
        Assert.Equal(1, applied);
        Assert.Equal(["316"], filters.Single(f => f.Attribute == "grade").AnyOf);
        Assert.Equal((6m, 12m), (filters.Single(f => f.Attribute == "diameter").Min, filters.Single(f => f.Attribute == "diameter").Max));
        Assert.True(filters.Single(f => f.Attribute == "critical").Flag);

        cut.FindAll("button").Single(b => b.TextContent == "Clear filters").Click();
        Assert.True(selection.IsEmpty);
        Assert.Equal(2, applied);
        Assert.Contains("Choose a class", Render<FacetPanel>(p => p.Add(x => x.Facets, []).Add(x => x.Selection, selection)).Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_lifecycle_editor_makes_existing_keys_read_only_and_adds_and_removes_rows()
    {
        var draft = LifecycleDraft.From(new LifecycleDefinition("part-default", "Standard", "part", 1, "published",
            [new("InWork", "In Work", 10, true, false, false), new("Released", "Released", 20, false, true, false)], [new("InWork", "Released", "workflow")]));
        var cut = Render<LifecycleEditor>(p => p.Add(x => x.Draft, draft).Add(x => x.Usage, new Dictionary<string, long> { ["InWork"] = 4 }));

        Assert.Equal(2, cut.FindAll(".lifecycle-states tbody tr").Count);
        Assert.Equal(2, cut.FindAll(".lifecycle-states code").Count); // existing keys are not editable
        Assert.Contains("4", cut.FindAll(".lifecycle-states tbody tr")[0].TextContent, StringComparison.Ordinal);
        Assert.Equal(["InWork"], cut.FindAll("input[type=radio][checked]").Select(_ => "InWork"));

        cut.FindAll("button").Single(b => b.TextContent == "Add state").Click();
        Assert.Equal(3, cut.FindAll(".lifecycle-states tbody tr").Count);
        Assert.Single(cut.FindAll("input[aria-label='State key']")); // the new row's key is editable
        cut.FindAll("button").Single(b => b.TextContent == "Add transition").Click();
        Assert.Equal(2, cut.FindAll(".lifecycle-transitions tbody tr").Count);
        cut.FindAll(".lifecycle-states button").Single(b => b.TextContent == "Remove").Click();
        Assert.Equal(2, cut.FindAll(".lifecycle-states tbody tr").Count);
        foreach (var th in cut.FindAll("th")) Assert.False(string.IsNullOrWhiteSpace(th.TextContent) && th.QuerySelector(".visually-hidden") is null);
    }

    [Fact]
    public void The_import_plan_table_summarizes_changes_and_flags_blocked_items()
    {
        ConfigChange[] changes = [new("attributes/part", "diameter", "create", "new"), new("classification", "B", "blocked", "use Move"), new("lists/unit", "yd", "change", "label")];
        var cut = Render<ConfigChangeTable>(p => p.Add(x => x.Changes, changes).Add(x => x.Unchanged, 9));
        Assert.Equal(3, cut.FindAll("tbody tr").Count);
        Assert.Contains("1 to create, 1 to change, 0 to retire or restore, 1 blocked, 9 unchanged.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("kind-blocked", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Nothing to do", Render<ConfigChangeTable>(p => p.Add(x => x.Changes, [])).Markup, StringComparison.Ordinal);
    }

    private static AttributeDefinitionDto Def(string name, bool retired = false, long inUse = 0) =>
        new("part", name, name.ToUpperInvariant(), "number", "mm", null, false, null, 0, null, null, null, 1, 100, retired, 1, inUse);

    [Fact]
    public void The_attribute_editor_creates_edits_and_retires_with_a_reason_and_shows_server_messages()
    {
        var saved = new List<(AttributeDefinitionInput Input, bool Create)>();
        var retired = new List<(string Name, bool Retired, string Reason)>();
        var cut = Render<AttributeEditor>(p => p
            .Add(x => x.Definitions, [Def("diameter", inUse: 5), Def("old", retired: true)])
            .Add(x => x.Save, (input, create) =>
            {
                saved.Add((input, create));
                return Task.FromResult(input.Name == "bad" ? Outcome<AttributeChange>.Invalid(["The name is bad."]) : Outcome<AttributeChange>.Ok(new AttributeChange(Def(input.Name), ["Take note."])));
            })
            .Add(x => x.Retire, (name, r, reason) => { retired.Add((name, r, reason)); return Task.FromResult(Outcome<AttributeChange>.Ok(new AttributeChange(Def(name, r), []))); }));

        // Table: headers, rules, stored values, status.
        Assert.Equal(2, cut.FindAll("tbody tr").Count);
        Assert.Contains("range 1 to 100", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Retired", cut.FindAll("tbody tr")[1].TextContent, StringComparison.Ordinal);

        // Create: a server validation message appears in an alert.
        cut.Find("form input").Change("bad");
        cut.FindAll("form input")[1].Change("Bad");
        cut.Find("form").Submit();
        Assert.Contains("The name is bad.", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        cut.Find("form input").Change("gauge");
        cut.Find("form").Submit();
        Assert.Equal(("gauge", true), (saved[^1].Input.Name, saved[^1].Create));
        Assert.Contains("Take note.", cut.Markup, StringComparison.Ordinal);

        // Retire opens the shared confirmation dialog; its reason is required before the confirm button works.
        cut.FindAll("tbody tr")[0].QuerySelectorAll("button")[1].Click();
        Assert.Equal("Retire attribute DIAMETER?", cut.Find("[role=dialog] h2").TextContent);
        Assert.Contains("5 stored value(s) stay readable", cut.Find("[role=dialog] .dialog-body").TextContent, StringComparison.Ordinal);
        cut.Find("[data-confirm-ok]").Click();
        Assert.Empty(retired); // no reason, no change
        Assert.Contains("Enter a reason", cut.Find("[role=dialog] [role=alert]").TextContent, StringComparison.Ordinal);
        cut.Find("[data-confirm-reason]").Input("Replaced by coating");
        cut.Find("[data-confirm-ok]").Click();
        Assert.Equal(("diameter", true, "Replaced by coating"), retired.Single());
        Assert.Empty(cut.FindAll("[role=dialog]"));

        // Edit: the name is read-only and the save is an update.
        cut.FindAll("tbody tr")[0].QuerySelectorAll("button")[0].Click();
        Assert.True(cut.Find("form input").HasAttribute("readonly"));
        cut.Find("form").Submit();
        Assert.Equal(("diameter", false), (saved[^1].Input.Name, saved[^1].Create));
    }
}
