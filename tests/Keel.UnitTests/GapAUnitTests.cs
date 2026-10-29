using System.Text.RegularExpressions;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Documents;
using Keel.Parts;
using Keel.Web.Components.Shared;
using Keel.Workflow;

namespace Keel.UnitTests;

/// <summary>Gap wave A: the sort whitelist, tab deep links, markings equality and the source rules that keep the new behaviour in place.</summary>
public sealed partial class GapAUnitTests
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

    private static string Web => Path.Combine(Root, "src", "Keel.Web");

    // ---------------------------------------------------------------- GAP-A-1: the sort whitelist

    private static readonly SortMap Demo = new(("name", ["lower(m.name)"]), ("version", ["length(r.revision)", "r.revision"]));

    [Fact]
    public void A_sort_key_is_only_ever_a_whitelist_lookup()
    {
        Assert.Null(Demo.Resolve(null, "desc"));
        Assert.Null(Demo.Resolve("  ", null));
        Assert.Equal(new SortSpec("name", false), Demo.Resolve("name", null));
        Assert.Equal(new SortSpec("name", true), Demo.Resolve(" name ", "DESC"));
        foreach (var evil in new[] { "name; DROP TABLE x", "lower(m.name)", "Name", "name,version", "1", "(select 1)", "m.name" })
            Assert.Throws<ArgumentException>(() => Demo.Resolve(evil, "asc"));
        Assert.Throws<ArgumentException>(() => Demo.Resolve("name", "asc; drop"));
        Assert.False(Demo.TryResolve("nope", null, out var spec, out var error));
        Assert.Null(spec);
        Assert.Contains("name, version", error, StringComparison.Ordinal); // the message lists what is allowed
    }

    [Fact]
    public void The_order_by_text_is_built_only_from_whitelisted_expressions_a_direction_keyword_and_the_tie_breaker()
    {
        Assert.Equal("m.number", Demo.OrderBy(null, "m.number", "m.number"));
        Assert.Equal("lower(m.name) ASC NULLS LAST, m.number", Demo.OrderBy(new SortSpec("name", false), "m.number", "m.number"));
        Assert.Equal("length(r.revision) DESC NULLS LAST, r.revision DESC NULLS LAST, m.number", Demo.OrderBy(new SortSpec("version", true), "m.number", "m.number"));
        // A spec made by hand with a key that is not on the list cannot reach SQL.
        Assert.Throws<ArgumentException>(() => Demo.OrderBy(new SortSpec("name; DROP TABLE x", false), "m.number", "m.number"));
        Assert.True(SortSpec.TryParseDirection(null, out var d1) && !d1);
        Assert.True(SortSpec.TryParseDirection("desc", out var d2) && d2);
        Assert.False(SortSpec.TryParseDirection("up", out _));
    }

    [Fact]
    public void Every_list_module_publishes_a_whitelist_with_its_number_key_and_no_expression_takes_input()
    {
        var maps = new Dictionary<string, SortMap>
        {
            ["parts"] = PartService.SortKeys, ["documents"] = DocumentService.SortKeys, ["changes"] = ChangeService.SortKeys,
            ["baselines"] = BaselineService.SortKeys, ["mboms"] = MbomService.SortKeys, ["tasks"] = WorkflowService.SortKeys,
        };
        foreach (var (name, map) in maps)
        {
            Assert.NotEmpty(map.Keys);
            // Expressions are written in code; the sources of the services never put a request value into ORDER BY except through SortMap.OrderBy.
            Assert.All(map.Keys, k => Assert.Matches("^[a-z]+$", k));
            Assert.True(map.Keys.Count >= 4, name);
        }
        var services = new[] { "Keel.Parts/PartService.cs", "Keel.Documents/DocumentService.cs", "Keel.Change/ChangeService.Queries.cs", "Keel.Bom/BaselineService.Queries.cs", "Keel.Bom/MbomService.cs" };
        foreach (var file in services)
        {
            var text = File.ReadAllText(Path.Combine(Root, "src", file));
            Assert.Contains("SortKeys.OrderBy(", text, StringComparison.Ordinal);
            // Whatever is interpolated after ORDER BY is the whitelist builder's output, never a request value.
            Assert.DoesNotMatch(@"ORDER BY \{(?!SortKeys\.OrderBy\()", text);
        }
    }

    [GeneratedRegex(@"ServerSortKey\s*=\s*""([a-z]+)""")]
    private static partial Regex ServerKey();

    [Fact]
    public void Every_server_sort_key_a_list_page_offers_is_on_the_whitelist_of_its_module()
    {
        var pages = new (string File, SortMap Map)[]
        {
            ("Parts/PartsSearch.razor", PartService.SortKeys), ("Documents/DocumentsSearch.razor", DocumentService.SortKeys), ("Change/ChangesList.razor", ChangeService.SortKeys),
            ("Configuration/BaselinesPage.razor", BaselineService.SortKeys), ("Mbom/MbomsPage.razor", MbomService.SortKeys), ("Workflow/MyTasksPage.razor", WorkflowService.SortKeys),
        };
        foreach (var (file, map) in pages)
        {
            var text = File.ReadAllText(Path.Combine(Web, "Components", "Pages", file));
            var keys = ServerKey().Matches(text).Select(m => m.Groups[1].Value).ToList();
            Assert.True(keys.Count >= 3, $"{file} offers server sorting");
            Assert.All(keys, k => Assert.True(map.Allows(k), $"{file}: '{k}' is not a sort key of its module"));
            // The page keeps the order in the address bar and rejects anything else; none of its columns still sorts only the loaded page.
            Assert.Contains("SupplyParameterFromQuery(Name = \"sort\")", text, StringComparison.Ordinal);
            Assert.Contains("SortKeys.TryResolve(SortParam, DirParam", text, StringComparison.Ordinal);
            Assert.Contains("OnServerSort=\"OnSort\" ServerSort=\"sort\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SortValue =", text, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- GAP-A-4: tab deep links

    private static readonly string[] Ids = ["overview", "affected", "impact", "impactmap", "history"];

    [Theory]
    [InlineData("http://localhost/changes/ECN-1?tab=impactmap", "impactmap")]
    [InlineData("http://localhost/changes/ECN-1?tab=impactmap#history", "history")] // the fragment is what selecting a tab writes, so it wins
    [InlineData("http://localhost/changes/ECN-1?x=1&tab=affected&y=2", "affected")]
    [InlineData("http://localhost/changes/ECN-1?tab=nope", "overview")]
    [InlineData("http://localhost/changes/ECN-1?tab=nope#impact", "impact")]
    [InlineData("http://localhost/changes/ECN-1?tab=impactmap#nope", "impactmap")]
    [InlineData("http://localhost/changes/ECN-1?tabs=impactmap", "overview")]
    [InlineData("http://localhost/changes/ECN-1?tab=impact%6Dap", "impactmap")]
    [InlineData("http://localhost/changes/ECN-1#?tab=impactmap", "overview")] // a query that is only text of the fragment is not a query
    [InlineData("http://localhost/changes/ECN-1", "overview")]
    [InlineData("http://localhost/changes/ECN-1?tab=", "overview")]
    public void A_tab_is_selected_by_the_fragment_or_by_the_tab_query_value(string uri, string expected) =>
        Assert.Equal(expected, TabSet.FromUri(uri, Ids, "overview"));

    [Fact]
    public void A_page_takes_the_address_tab_only_for_a_new_address_so_reloading_after_an_action_keeps_the_users_tab()
    {
        string? seen = null;
        var uri = "http://localhost/changes/ECN-1?tab=impactmap";
        Assert.Equal("impactmap", TabSet.OnNavigation(uri, ref seen, Ids, "overview")); // first load
        // The user chooses History (the browser's address changes, the circuit's copy does not); an action reloads the data.
        Assert.Equal("history", TabSet.OnNavigation(uri, ref seen, Ids, "history"));
        // A real navigation to another object or tab applies the new address.
        Assert.Equal("affected", TabSet.OnNavigation("http://localhost/changes/ECN-2?tab=affected", ref seen, Ids, "history"));
        Assert.Equal("overview", TabSet.OnNavigation("http://localhost/changes/ECN-3", ref seen, Ids, "overview"));
    }

    [Fact]
    public void Every_object_page_reads_its_tab_through_the_navigation_rule()
    {
        foreach (var page in new[] { "Parts/PartPage", "Documents/DocumentPage", "Change/ChangePage", "Mbom/MbomPage", "Configuration/BaselineDetailPage" })
        {
            var text = File.ReadAllText(Path.Combine(Web, "Components", "Pages", page + ".razor"));
            Assert.Contains("TabSet.OnNavigation(Nav.Uri, ref seenUri, TabIds, tab)", text, StringComparison.Ordinal);
            Assert.DoesNotContain("TabSet.FromUri(Nav.Uri, TabIds, tab)", text, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- markings equality (found while testing GAP-A-2)

    [Fact]
    public void Markings_built_separately_with_the_same_values_are_equal()
    {
        Markings Make(params string[] cui) => Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: cui,
            controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA", exportAuthorizations: ["TAA-1"]).Value;
        Assert.Equal(Make("EXPT", "CTI"), Make("CTI", "EXPT"));
        Assert.Equal(Make("EXPT", "CTI").GetHashCode(), Make("CTI", "EXPT").GetHashCode());
        Assert.NotEqual(Make("EXPT"), Make("EXPT", "CTI"));
        Assert.NotEqual(Make("EXPT"), Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"],
            controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value);
        Assert.Single(new[] { Make("EXPT"), Make("EXPT") }.Distinct());
    }

    // ---------------------------------------------------------------- GAP-A-3: confirmations

    [Fact]
    public void Administration_confirms_through_the_shared_dialog_and_no_page_builds_its_own_confirm_panel()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Web, "*.razor", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            if (Path.GetFileName(file) == "ConfirmDialog.razor") continue;
            var text = File.ReadAllText(file);
            var rel = Path.GetRelativePath(Root, file);
            if (Regex.IsMatch(text, @"class=""[^""]*\bconfirm\b[^""]*""")) offenders.Add(rel + ": class=\"confirm\" panel");
            if (Regex.IsMatch(text, @"aria-label=""Confirm\b")) offenders.Add(rel + ": inline Confirm group");
            if (Regex.IsMatch(text, @"<button[^>]*class=""danger""[^>]*>\s*@?\(?\s*[^<]*Confirm", RegexOptions.IgnoreCase)) offenders.Add(rel + ": a Confirm button of its own");
        }
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
        // The pages that used to confirm inline now open the dialog.
        foreach (var page in new[] { "Admin/ConfigPage", "Admin/ClassificationAdminPage", "Admin/LifecyclesPage", "Admin/WorkflowTemplatesPage", "Admin/ReasonsPage", "Admin/ListsPage", "Admin/SupplyTypesPage" })
            Assert.Contains("<ConfirmDialog", File.ReadAllText(Path.Combine(Web, "Components", "Pages", page + ".razor")), StringComparison.Ordinal);
        Assert.Contains("<ConfirmDialog", File.ReadAllText(Path.Combine(Web, "Components", "Shared", "AttributeEditor.razor")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_supply_type_page_is_administrator_only_linked_from_the_admin_navigation_and_the_mbom_page_has_a_markings_tab()
    {
        var page = File.ReadAllText(Path.Combine(Web, "Components", "Pages", "Admin", "SupplyTypesPage.razor"));
        Assert.Contains("@attribute [Authorize(Policy = Policies.Admin)]", page, StringComparison.Ordinal);
        Assert.Contains("ReasonRequired=\"true\"", page, StringComparison.Ordinal);
        Assert.Contains("admin/supply-types", File.ReadAllText(Path.Combine(Web, "Components", "Shared", "AdminNav.razor")), StringComparison.Ordinal);
        var mbom = File.ReadAllText(Path.Combine(Web, "Components", "Pages", "Mbom", "MbomPage.razor"));
        Assert.Contains("new(\"security\", \"Security markings\")", mbom, StringComparison.Ordinal);
        Assert.Contains("<MbomMarkingsPanel", mbom, StringComparison.Ordinal);
        Assert.Contains("Mbom.ChangeMarkingsAsync", File.ReadAllText(Path.Combine(Web, "Components", "Pages", "Mbom", "MbomMarkingsPanel.razor")), StringComparison.Ordinal);
    }
}
