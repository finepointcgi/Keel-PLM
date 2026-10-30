using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Bunit;
using Keel.Core;
using Keel.Parts;
using Keel.Web.Components.Shared;
using Keel.Web.Services;
using Keel.Workspace;
using Microsoft.AspNetCore.Components;

namespace Keel.UnitTests;

/// <summary>ADR 0061: the rules behind the simplified screens (defaults, edit advice, navigation) and the new components.</summary>
public sealed class SimplifyFlowsUnitTests : BunitContext
{
    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)",
        cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;

    private static Subject Alice(params string[] programs) => new()
    {
        Id = "sub-alice", DisplayName = "Alice", UsPerson = true, Org = "ENG",
        Programs = [.. programs], Roles = [Roles.Author],
    };

    private static PartDetail Part(string state = "InWork", bool locked = false, bool latest = true, CheckoutView? checkout = null, bool canModify = true, string[]? denied = null)
    {
        var rev = new RevisionView(Guid.NewGuid(), "A", state, state == "InWork" ? "In Work" : state, latest, locked, 1, DateTimeOffset.UnixEpoch, "alice", DateTimeOffset.UnixEpoch, "alice");
        var it = new IterationView(Guid.NewGuid(), "A", 1, "A.1", "d", [], null, DateTimeOffset.UnixEpoch, "alice");
        return new PartDetail(Guid.NewGuid(), "P-000001", "Bracket", "Component", "EA", "Make", "part-default", Itar, Itar.BannerText(), rev, it, [rev], [it], checkout, [],
            new PartPermissions(canModify, false, denied ?? []), DateTimeOffset.UnixEpoch, "alice");
    }

    // ---------------------------------------------------------------- defaults

    [Fact]
    public void A_default_is_the_last_choice_then_the_common_value_then_the_first_of_the_list()
    {
        IReadOnlyList<string> types = ["Assembly", "Component", "Standard"];
        var last = new Dictionary<string, string> { [LastChoiceKeys.PartType] = "Standard" };
        Assert.Equal("Standard", QuickDefaults.Pick(types, last, LastChoiceKeys.PartType, "Component"));
        Assert.Equal("Component", QuickDefaults.Pick(types, new Dictionary<string, string>(), LastChoiceKeys.PartType, "Component"));
        Assert.Equal("Assembly", QuickDefaults.Pick(["Assembly", "Standard"], new Dictionary<string, string>(), LastChoiceKeys.PartType, "Component"));
        // a last choice that an administrator has since retired is not offered again
        Assert.Equal("Component", QuickDefaults.Pick(types, new Dictionary<string, string> { [LastChoiceKeys.PartType] = "Retired" }, LastChoiceKeys.PartType, "Component"));
    }

    [Fact]
    public void New_markings_start_from_the_persons_own_program_and_organization()
    {
        var m = QuickDefaults.Markings(Alice("KRAKEN", "ORCA"));
        Assert.Equal("KRAKEN", m.Program);
        Assert.Equal("ENG", m.OwningOrg);
        Assert.True(m.ToMarkings().IsSuccess);
        Assert.Contains("PROGRAM KRAKEN", QuickDefaults.ChipText(m), StringComparison.Ordinal);
        Assert.Equal("", QuickDefaults.Markings(Alice()).Program); // no program: none is invented
        m.ExportJurisdiction = ExportJurisdiction.Ear; // EAR needs an ECCN: the chip says the markings are not complete yet
        Assert.Equal("Markings incomplete", QuickDefaults.ChipText(m));
    }

    [Fact]
    public void The_link_type_defaults_to_describes_and_ignores_unknown_values()
    {
        Assert.Equal("describes", QuickDefaults.DefaultLinkType(new Dictionary<string, string>()));
        Assert.Equal("references", QuickDefaults.DefaultLinkType(new Dictionary<string, string> { [LastChoiceKeys.LinkType] = "references" }));
        Assert.Equal("describes", QuickDefaults.DefaultLinkType(new Dictionary<string, string> { [LastChoiceKeys.LinkType] = "nonsense" }));
    }

    // ---------------------------------------------------------------- edit advice

    [Fact]
    public void Edit_advice_names_the_reason_and_only_an_unlocked_free_part_can_start()
    {
        Assert.Equal(PartEditState.CanEdit, SimpleFlows.Advise(Part()).State);
        Assert.True(SimpleFlows.Advise(Part()).CanStart);

        var mine = SimpleFlows.Advise(Part(checkout: new CheckoutView("sub-alice", "alice", DateTimeOffset.UnixEpoch, true, "d", [])));
        Assert.Equal(PartEditState.Resume, mine.State);
        Assert.True(mine.CanStart);

        var other = SimpleFlows.Advise(Part(checkout: new CheckoutView("sub-bob", "Bob", DateTimeOffset.UnixEpoch, false, null, null)));
        Assert.Equal(PartEditState.CheckedOutByOther, other.State);
        Assert.Contains("Bob is editing this part", other.Message, StringComparison.Ordinal);
        Assert.False(other.CanStart);

        var released = SimpleFlows.Advise(Part("Released", locked: true));
        Assert.Equal(PartEditState.NeedsChange, released.State);
        Assert.Contains("change order", released.Message, StringComparison.Ordinal);

        Assert.Equal(PartEditState.NotLatest, SimpleFlows.Advise(Part(latest: false)).State);
        var readOnly = SimpleFlows.Advise(Part(canModify: false, denied: ["role.missing"]));
        Assert.Equal(PartEditState.ReadOnly, readOnly.State);
        Assert.Contains("Your roles do not permit", readOnly.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_cannot_be_added_to_a_released_part_and_the_message_says_why()
    {
        Assert.Null(SimpleFlows.LinkBlocker(Part()));
        Assert.Contains("through a change order", SimpleFlows.LinkBlocker(Part("Released", locked: true)), StringComparison.Ordinal);
        Assert.Contains("is editing this part", SimpleFlows.LinkBlocker(Part(checkout: new CheckoutView("sub-bob", "Bob", DateTimeOffset.UnixEpoch, false, null, null))), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- navigation (Simple view)

    [Fact]
    public void The_simple_view_hides_the_advanced_groups_and_keeps_what_the_roles_add()
    {
        var viewer = NavModel.For([Roles.Viewer], simple: true);
        Assert.Equal(["Work", "Product data", "Change"], viewer.Select(g => g.Title));
        Assert.Equal(["parts", "documents", "mboms"], viewer.Single(g => g.Title == "Product data").Items.Select(i => i.Href));

        var admin = NavModel.For([Roles.Admin, Roles.Auditor], simple: true);
        Assert.Equal(["Work", "Product data", "Change", "Records", "Configuration", "Administration", "Tools"], admin.Select(g => g.Title));
        Assert.Equal(["audit"], admin.Single(g => g.Title == "Tools").Items.Select(i => i.Href)); // only the audit log of Tools, for the auditor
        Assert.Equal(admin.SelectMany(g => g.Items).Count(), admin.SelectMany(g => g.Items).Select(i => i.Href).Distinct().Count());

        // The default view is unchanged, and Simple never adds a link the roles did not already allow.
        Assert.Equal(NavModel.For([Roles.Viewer]).Select(g => g.Title), NavModel.For([Roles.Viewer], simple: false).Select(g => g.Title));
        var allHrefs = NavModel.For([Roles.Viewer]).SelectMany(g => g.Items).Select(i => i.Href).ToHashSet();
        Assert.All(viewer.SelectMany(g => g.Items), i => Assert.Contains(i.Href, allHrefs));
        Assert.DoesNotContain(viewer.SelectMany(g => g.Items), i => i.Href is "admin" or "audit" or "workflows");
    }

    [Fact]
    public void Simple_view_is_part_of_the_display_preferences_and_normalizes()
    {
        Assert.False(UserPreferences.Default.SimpleView);
        Assert.True(UserPreferences.Normalize("dark", "compact", true, true).SimpleView);
        Assert.Equal(new UserPreferences("dark", "compact", true), UserPreferences.Normalize("dark", "compact", true)); // old callers still compile and get false
    }

    // ---------------------------------------------------------------- glossary

    [Fact]
    public void Every_glossary_term_has_a_plain_sentence_and_no_tooltip_is_used()
    {
        Assert.All(Glossary.Terms, t => Assert.True(Glossary.Explain(t)!.Length > 30));
        var cut = Render<GlossaryHint>(p => p.Add(x => x.Term, "working copy"));
        Assert.Contains("private draft", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("title=", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(Render<GlossaryHint>(p => p.Add(x => x.Term, "no such term")).Markup.Trim());
    }

    // ---------------------------------------------------------------- components

    [Fact]
    public void The_markings_chip_shows_the_default_and_Change_opens_the_full_form()
    {
        var input = QuickDefaults.Markings(Alice("ORCA"));
        var cut = Render<MarkingsDefaultPicker>(p => p.Add(x => x.Value, input));
        Assert.Contains("PROGRAM ORCA", cut.Find(".chip").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("fieldset")); // collapsed: no fields to fill
        cut.Find("[data-markings-change]").Click();
        Assert.NotEmpty(cut.FindAll("fieldset select"));
        Assert.Equal("true", cut.Find("[data-markings-change]").GetAttribute("aria-expanded"));
        cut.Find("[data-markings-change]").Click();
        Assert.Empty(cut.FindAll("fieldset"));
    }

    [Fact]
    public void The_picker_searches_as_you_type_offers_blocked_items_with_a_reason_and_keeps_the_choice()
    {
        PickItem? chosen = null;
        var queries = new List<string>();
        Task<IReadOnlyList<PickItem>> Search(string q)
        {
            queries.Add(q);
            return Task.FromResult<IReadOnlyList<PickItem>>([new("P-000001", "Bracket", Itar.BannerText()), new("P-000002", "Housing", Itar.BannerText(), "Released: documents are added through a change order.", true)]);
        }
        var cut = Render<ObjectPicker>(p => p.Add(x => x.Id, "t").Add(x => x.Label, "Part").Add(x => x.Noun, "parts").Add(x => x.Search, Search)
            .Add(x => x.SelectedChanged, EventCallback.Factory.Create<PickItem?>(this, v => chosen = v)));

        Assert.Contains("Type a number", cut.Find("[role=status]").TextContent, StringComparison.Ordinal);
        cut.Find("input").Input("brack");
        Assert.Equal(["brack"], queries);
        var options = cut.FindAll("[data-picker-option]");
        Assert.Equal(2, options.Count);
        Assert.False(options[0].HasAttribute("disabled"));
        Assert.True(options[1].HasAttribute("disabled"));
        Assert.Contains("change order", options[1].TextContent, StringComparison.Ordinal);

        options[0].Click();
        Assert.Equal("P-000001", chosen!.Number);
        Assert.Empty(cut.FindAll("input"));
        Assert.Contains("P-000001", cut.Find(".picker-chosen").TextContent, StringComparison.Ordinal);
        cut.Find("[data-picker-change]").Click();
        Assert.Null(chosen);
        Assert.NotEmpty(cut.FindAll("input"));
    }

    [Fact]
    public void A_released_part_shows_one_line_and_a_Start_change_button_that_prefills_the_part()
    {
        var cut = Render<PartEditBanner>(p => p.Add(x => x.Part, Part("Released", locked: true)));
        Assert.Contains("change order", cut.Find("[data-edit-banner] p").TextContent, StringComparison.Ordinal);
        var start = cut.Find("[data-action=start-change]");
        Assert.EndsWith("?part=P-000001", start.GetAttribute("href"), StringComparison.Ordinal);
        Assert.Contains("changes/new/", start.GetAttribute("href"), StringComparison.Ordinal);

        Assert.Empty(Render<PartEditBanner>(p => p.Add(x => x.Part, Part())).Markup.Trim()); // nothing to explain when editing is possible
        var noRight = Render<PartEditBanner>(p => p.Add(x => x.Part, Part("Released", locked: true, canModify: false)));
        Assert.Empty(noRight.FindAll("[data-action=start-change]")); // no right to change: no button that would only be refused
    }
}
