using Bunit;
using Keel.Audit;
using Keel.Change;
using Keel.Core;
using Keel.Parts;
using Keel.Web.Components.Pages.Change;
using Keel.Web.Services;
using Keel.Workflow;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Keel.UnitTests;

/// <summary>
/// Component tests (bUnit) for the change pages' panels. The panels take their data as parameters, so these render with real services that
/// are never asked to touch a database; everything that needs data from the database is covered by the integration tests.
/// </summary>
public sealed class ChangeComponentTests : BunitContext
{
    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)",
        cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;

    public ChangeComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var ds = NpgsqlDataSource.Create("Host=unused.invalid;Database=none");
        var audit = new PostgresAuditLog(ds, TimeProvider.System);
        Services.AddSingleton(new ChangeService(ds, audit, [], TimeProvider.System));
        Services.AddSingleton(new PartService(ds, audit, TimeProvider.System));
        Services.AddSingleton(new WorkflowService(ds, audit, [], new WorkflowOptions(), TimeProvider.System));
        Services.AddSingleton(new ImpactAnalysisService(ds, audit, [], [], []));
        Services.AddScoped<CurrentUser>();
        AddAuthorization().SetAuthorized("alice");
    }

    private static AffectedObjectView Row(string? key, bool redacted = false, string action = ChangeActions.Revise, string? resulting = "B") =>
        new(Guid.NewGuid(), redacted, "part", key, key is null ? null : "Bracket", redacted ? null : "A", redacted ? null : "Released", action, resulting, resulting is null ? null : "In Work",
            redacted ? null : Itar.BannerText(), DateTimeOffset.UnixEpoch, "alice");

    private static TaskView Task(string number, string state = "Open", bool start = false, bool complete = false, bool cancel = false, bool overdue = false) =>
        new(Guid.NewGuid(), number, $"Task {number}", "Details", state, state, "bob", "Bob Example", new DateOnly(2026, 11, 1), overdue, null, null, DateTimeOffset.UnixEpoch, null,
            start, complete, cancel);

    private static ChangeDetail Detail(string type = ChangeTypes.Ecn, string state = "Draft", bool editable = true, bool canModify = true, bool final = false,
        IReadOnlyList<AffectedObjectView>? affected = null, IReadOnlyList<TaskView>? tasks = null) =>
        new(Guid.NewGuid(), type, $"{type}-000001", "Revise bracket", "Description", "High", type == ChangeTypes.Pr ? null : "II", "Reason", "Alice Example", "alice", state, state, !editable,
            final, Itar, Markings.AggregateBanner([Itar]), null, [], affected ?? [], tasks ?? [], [], [], new ChangePermissions(canModify, false, editable, []), null, null, 0,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [], [Itar]);

    // ---------------------------------------------------------------- affected objects

    [Fact]
    public void Affected_objects_show_links_the_resulting_revision_and_a_placeholder_for_what_the_reader_may_not_see()
    {
        var cut = Render<AffectedPanel>(p => p.Add(x => x.Detail, Detail(affected: [Row("P-000001"), Row(null, redacted: true, action: ChangeActions.None, resulting: null)])));
        var rows = cut.FindAll("tbody tr");
        Assert.Equal(2, rows.Count);
        Assert.Contains("P-000001", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Equal("parts/P-000001?revision=A", rows[0].QuerySelector("a")!.GetAttribute("href"));
        Assert.Equal("parts/P-000001?revision=B", rows[0].QuerySelectorAll("a")[1].GetAttribute("href"));
        Assert.Contains("Revise", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("CUI//EXPT", rows[0].TextContent, StringComparison.Ordinal);
        // The placeholder discloses only the action: no number, name, revision, state, markings or links.
        Assert.Contains("Restricted object", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("Reference only", rows[1].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Bracket", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Empty(rows[1].QuerySelectorAll("a"));
        Assert.Empty(rows[1].QuerySelectorAll(".chip"));
    }

    [Fact]
    public void An_editable_change_offers_removal_and_a_picker_while_a_locked_one_does_not()
    {
        var editable = Render<AffectedPanel>(p => p.Add(x => x.Detail, Detail(affected: [Row("P-000001")])));
        Assert.Single(editable.FindAll("button[aria-label='Remove P-000001']"));
        Assert.NotEmpty(editable.FindAll("input[aria-label='Find a part to add']"));

        var locked = Render<AffectedPanel>(p => p.Add(x => x.Detail, Detail(state: "UnderReview", editable: false, affected: [Row("P-000001")])));
        Assert.Empty(locked.FindAll("button[aria-label='Remove P-000001']"));
        Assert.Empty(locked.FindAll("input[aria-label='Find a part to add']"));

        var viewer = Render<AffectedPanel>(p => p.Add(x => x.Detail, Detail(canModify: false, affected: [Row("P-000001")])));
        Assert.Empty(viewer.FindAll("button"));

        Assert.Contains("No objects listed", Render<AffectedPanel>(p => p.Add(x => x.Detail, Detail())).Markup, StringComparison.Ordinal);
        Assert.Contains("bound to this notice", editable.Markup, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- tasks

    [Fact]
    public void Task_buttons_follow_what_the_service_says_the_reader_may_do()
    {
        var detail = Detail(state: "Released", tasks: [Task("CT-000001", start: true, complete: true, cancel: true), Task("CT-000002", "Done"), Task("CT-000003", complete: true, overdue: true)]);
        var cut = Render<ChangeTasksPanel>(p => p.Add(x => x.Detail, detail));
        var rows = cut.FindAll("tbody tr");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Start", "Complete", "Cancel task"], rows[0].QuerySelectorAll("button").Select(b => b.TextContent));
        Assert.Empty(rows[1].QuerySelectorAll("button"));
        Assert.Equal(["Complete"], rows[2].QuerySelectorAll("button").Select(b => b.TextContent));
        Assert.Contains("Overdue since 2026-11-01", rows[2].TextContent, StringComparison.Ordinal);
        Assert.Contains("Due 2026-11-01", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Bob Example (bob)", rows[0].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_new_task_form_needs_a_title_and_an_assignee_and_is_hidden_when_nothing_may_be_added()
    {
        var cut = Render<ChangeTasksPanel>(p => p.Add(x => x.Detail, Detail(state: "Released")));
        var create = cut.FindAll("button").Single(b => b.TextContent == "Create task");
        Assert.True(create.HasAttribute("disabled"));
        cut.Find("input[maxlength='200']").Change("Update the work instruction");
        cut.Find("input[list='assignee-options']").Input("b"); // fewer than two characters: no directory lookup yet
        Assert.True(cut.FindAll("button").Single(b => b.TextContent == "Create task").HasAttribute("disabled") is false);

        Assert.Empty(Render<ChangeTasksPanel>(p => p.Add(x => x.Detail, Detail(state: "Implemented", final: true))).FindAll("button"));
        Assert.Empty(Render<ChangeTasksPanel>(p => p.Add(x => x.Detail, Detail(canModify: false))).FindAll("button"));
        Assert.Contains("No change tasks", Render<ChangeTasksPanel>(p => p.Add(x => x.Detail, Detail(final: true))).Markup, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- impact analysis

    [Fact]
    public void The_impact_panel_runs_only_on_request_and_offers_the_levels()
    {
        var cut = Render<ImpactPanel>(p => p.Add(x => x.Number, "ECN-000001").Add(x => x.OnShown, EventCallback.Factory.Create<IReadOnlyList<Markings>>(this, _ => { })));
        Assert.Equal(["Run impact analysis"], cut.FindAll("button").Select(b => b.TextContent));
        Assert.Equal(["1", "2", "3", "5", "10"], cut.FindAll("select option").Select(o => o.TextContent));
        Assert.Empty(cut.FindAll("article"));
        Assert.Contains("placeholders", cut.Markup, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    [Fact]
    public void Links_labels_and_wording()
    {
        Assert.Equal("changes/ECN-000001", ChangeUi.Href("ECN-000001"));
        Assert.Equal("parts/P-000001?revision=B", ChangeUi.PartHref("P-000001", "B"));
        Assert.Equal("parts/P-000001", ChangeUi.PartHref("P-000001"));
        Assert.Equal("changes/new/ecn", ChangeUi.NewHref("ECN"));
        Assert.Equal(("ecr", "ecn", null), (ChangeUi.ReviewObjectType("ECR"), ChangeUi.ReviewObjectType("ECN"), ChangeUi.ReviewObjectType("PR")));
        Assert.Equal(["Revise", "Obsolete", "Reference only"], ChangeActions.All.Select(ChangeUi.ActionLabel));
        Assert.Equal("state-underreview", ChangeUi.StateClass("UnderReview"));
        Assert.Equal("No due date", ChangeUi.DueText(null, false));
        Assert.Equal("Overdue since 2026-01-02", ChangeUi.DueText(new DateOnly(2026, 1, 2), true));
        Assert.Equal("Not found, or you are not authorized to see it.", ChangeUi.Message(Outcome<int>.NotFound()));
    }

    [Fact]
    public void Merged_banners_cover_both_inputs_and_never_lose_a_control()
    {
        const string part = "CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA";
        const string change = "CUI//CTI/EXPT · ITAR · DISTRIBUTION E";
        const string bom = "CUI//EXPT · EAR 9A610.X · DISTRIBUTION B";
        Assert.Equal(part, BannerMerge.Union(part, UiText.Unclassified, null));
        Assert.Equal(part, BannerMerge.Union(part, part));
        Assert.Equal(UiText.Unclassified, BannerMerge.Union(null, UiText.Unclassified));
        Assert.Equal("CUI//CTI/EXPT · ITAR · DISTRIBUTION E · PROGRAM ORCA", BannerMerge.Union(part, change));
        Assert.Equal("CUI//CTI/EXPT · ITAR · EAR 9A610.X · DISTRIBUTION E · PROGRAM ORCA", BannerMerge.Union(part, change, bom));
        Assert.Equal("CUI//EXPT · ITAR · EAR 9A610.X · DISTRIBUTION D · PROGRAM ORCA", BannerMerge.Union(bom, part));
        // The merge agrees with Markings.AggregateBanner for aggregate banners of the same data.
        var a = Markings.Create(ExportJurisdiction.Ear, DistributionStatement.C, "ENG", eccn: "3A001", cuiCategories: ["EXPT"], controllingOffice: "x", distributionReason: "y").Value;
        Assert.Equal(Markings.AggregateBanner([a, Itar]), BannerMerge.Union(Markings.AggregateBanner([a]), Markings.AggregateBanner([Itar])).Replace(" · PROGRAM ORCA", "", StringComparison.Ordinal));
    }
}
