using Bunit;
using Keel.Web.Components.Pages.Workflow;
using Keel.Workflow;
using Microsoft.AspNetCore.Components;

namespace Keel.UnitTests;

/// <summary>Component tests (bUnit) for the sign panel and the start-review dialog (ADR-0014).</summary>
public sealed class WorkflowComponentTests : BunitContext
{
    private static readonly ReasonCodeView[] ApprovalCodes =
    [
        new("Approved", "Approved", "approve", false),
        new("ApprovedWithComments", "Approved with comments", "approve", true),
        new("Rejected", "Rejected", "reject", true),
    ];

    private IRenderedComponent<SignPanel> RenderSign(Action<SignChoice> onSign, bool canSign = true, bool stepUp = false, string comment = "", string[]? blocked = null) =>
        Render<SignPanel>(p => p
            .Add(x => x.ReasonCodes, ApprovalCodes)
            .Add(x => x.CanSign, canSign)
            .Add(x => x.StepUpRequired, stepUp)
            .Add(x => x.Comment, comment)
            .Add(x => x.BlockedReasons, blocked ?? [])
            .Add(x => x.OnSign, EventCallback.Factory.Create<SignChoice>(this, onSign)));

    [Fact]
    public void Sign_panel_offers_one_button_per_meaning()
    {
        var cut = RenderSign(_ => { });
        Assert.Equal(["Approved", "Approved with comments", "Rejected"], cut.FindAll("button").Select(b => b.TextContent));
        Assert.Contains("danger", cut.FindAll("button")[2].ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void Approving_without_a_comment_signs_immediately()
    {
        SignChoice? chosen = null;
        var cut = RenderSign(c => chosen = c);
        cut.FindAll("button")[0].Click();
        Assert.Equal(new SignChoice("Approved", null), chosen);
    }

    [Fact]
    public void Rejecting_requires_a_comment_before_anything_is_sent()
    {
        SignChoice? chosen = null;
        var cut = RenderSign(c => chosen = c);

        cut.FindAll("button")[2].Click();
        Assert.Null(chosen);
        Assert.Contains("Enter a comment to sign as \"Rejected\"", cut.Find("#sign-validation").TextContent, StringComparison.Ordinal);

        cut.Find("textarea").Input("Mass exceeds the allocation");
        cut.FindAll("button")[2].Click();
        Assert.Equal(new SignChoice("Rejected", "Mass exceeds the allocation"), chosen);
        Assert.Empty(cut.FindAll("#sign-validation"));
    }

    [Fact]
    public void A_comment_restored_after_step_up_is_shown_and_sent()
    {
        SignChoice? chosen = null;
        var cut = RenderSign(c => chosen = c, comment: "Kept across re-authentication");
        Assert.Equal("Kept across re-authentication", cut.Find("textarea").GetAttribute("value"));
        cut.FindAll("button")[1].Click();
        Assert.Equal(new SignChoice("ApprovedWithComments", "Kept across re-authentication"), chosen);
    }

    [Fact]
    public void Stale_authentication_is_explained_before_signing()
    {
        var cut = RenderSign(_ => { }, stepUp: true);
        Assert.Contains("sign in again", cut.Find(".notice").TextContent, StringComparison.Ordinal);
        Assert.Contains("kept", cut.Find(".notice").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_task_that_cannot_be_signed_shows_why_and_no_buttons()
    {
        var cut = RenderSign(_ => { }, canSign: false, blocked: ["sod.submitter"]);
        Assert.Empty(cut.FindAll("button"));
        Assert.Contains("separation of duties", cut.Find("p").TextContent, StringComparison.Ordinal);

        var redacted = RenderSign(_ => { }, canSign: false, blocked: ["task.not_eligible"]);
        Assert.Contains("no longer eligible", redacted.Find("p").TextContent, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- start-review dialog

    private static readonly TemplateView Template = new("part-release-review", 1, "Part release review", null, "part", true,
        [new SlotView("reviewer", "Technical reviewer", "plm-viewer", 1, 1), new SlotView("approvers", "Approvers", "plm-approver", 1, 3)],
        DateTimeOffset.UtcNow, "system", new System.Text.Json.Nodes.JsonObject());

    private static readonly DirectoryUserView Rita = new("sub-rita", "rita", "Rita Reviewer", ["plm-viewer"]);
    private static readonly DirectoryUserView Bob = new("sub-bob", "bob", "Bob Approver", ["plm-viewer", "plm-approver"]);
    private static readonly DirectoryUserView Ann = new("sub-ann", "ann", "Ann Approver", ["plm-approver"]);

    private IRenderedComponent<StartReviewDialog> RenderDialog(Action<StartSelection> onStart, List<string>? searches = null) =>
        Render<StartReviewDialog>(p => p
            .Add(x => x.Template, Template)
            .Add(x => x.Search, (slot, text) =>
            {
                searches?.Add($"{slot.Name}:{text}");
                IReadOnlyList<DirectoryUserView> found = slot.Name == "reviewer" ? [Rita] : [Bob, Ann];
                return Task.FromResult(found);
            })
            .Add(x => x.OnStart, EventCallback.Factory.Create<StartSelection>(this, onStart)));

    [Fact]
    public void Dialog_lists_the_participant_groups_with_their_limits()
    {
        var cut = RenderDialog(_ => { });
        var legends = cut.FindAll("legend").Select(l => l.TextContent).ToList();
        Assert.Contains("exactly 1", legends[0], StringComparison.Ordinal);
        Assert.Contains("1 to 3", legends[1], StringComparison.Ordinal);
        Assert.Contains("approver", legends[1], StringComparison.Ordinal);
        Assert.True(cut.FindAll("button").First(b => b.TextContent == "Start review").HasAttribute("disabled"));
    }

    private static void Pick(IRenderedComponent<StartReviewDialog> cut, string slotLabel, string who, string search = "x")
    {
        cut.Find($"input[aria-label='Find {slotLabel}']").Change(search);
        cut.Find($"button[aria-label='Search {slotLabel}']").Click();
        cut.Find($"button[aria-label='Add {who}']").Click();
    }

    [Fact]
    public void Start_is_enabled_only_when_every_group_has_enough_people_and_sends_their_ids()
    {
        StartSelection? started = null;
        var searches = new List<string>();
        var cut = RenderDialog(s => started = s, searches);
        var start = () => cut.FindAll("button").First(b => b.TextContent == "Start review");

        Pick(cut, "Technical reviewer", "rita", "ri");
        Assert.True(start().HasAttribute("disabled"));
        Pick(cut, "Approvers", "bob");
        Pick(cut, "Approvers", "ann");
        Assert.False(start().HasAttribute("disabled"));
        // The eligible people of each group are listed when the dialog opens (an empty search), then the typed searches.
        Assert.Equal(["reviewer:", "approvers:", "reviewer:ri", "approvers:x", "approvers:x"], searches);

        cut.Find("textarea").Change("Please check the tolerances");
        start().Click();
        Assert.NotNull(started);
        Assert.Equal("part-release-review", started.TemplateId);
        Assert.Equal(["sub-rita"], started.Participants["reviewer"]);
        Assert.Equal(["sub-bob", "sub-ann"], started.Participants["approvers"]);
        Assert.Equal("Please check the tolerances", started.Comment);
    }

    [Fact]
    public void The_maximum_is_enforced_and_chosen_people_can_be_removed()
    {
        var cut = RenderDialog(_ => { });
        Pick(cut, "Technical reviewer", "rita");
        // Rita is chosen; the single-reviewer group is full.
        Assert.Contains("Rita Reviewer", cut.Find("ul[aria-label='Chosen Technical reviewer']").TextContent, StringComparison.Ordinal);
        cut.Find("button[aria-label='Remove rita']").Click();
        Assert.Empty(cut.FindAll("ul[aria-label='Chosen Technical reviewer']"));
        Assert.False(cut.Find("button[aria-label='Add rita']").HasAttribute("disabled"));
    }

    [Fact]
    public void Server_errors_are_shown_in_the_dialog()
    {
        var cut = Render<StartReviewDialog>(p => p
            .Add(x => x.Template, Template)
            .Add(x => x.Search, (_, _) => Task.FromResult<IReadOnlyList<DirectoryUserView>>([]))
            .Add(x => x.Error, "Approvers: lena is not eligible to see this object"));
        Assert.Contains("lena is not eligible", cut.Find("p.error").TextContent, StringComparison.Ordinal);
    }
}
