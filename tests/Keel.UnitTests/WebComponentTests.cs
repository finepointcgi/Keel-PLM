using System.Security.Claims;
using System.Text.Json.Nodes;
using Bunit;
using Keel.Core;
using Keel.Parts;
using Keel.Web.Components.Shared;
using Keel.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Sections;

namespace Keel.UnitTests;

public sealed class WebComponentTests : BunitContext
{
    private static readonly AttributeDefinition[] Defs =
    [
        new("material", "Material", "string", null, null, false, 120, 1),
        new("mass", "Mass", "number", "kg", null, true, null, 2),
        new("critical_item", "Critical item", "boolean", null, null, false, null, 3),
        new("criticality", "Criticality", "enum", null, ["Standard", "Major"], false, null, 4),
    ];

    /// <summary>Stands in for MainLayout: the two banner outlets plus a page body.</summary>
    private sealed class FrameHost : ComponentBase
    {
        [Parameter] public string Banner { get; set; } = "";

        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            b.OpenComponent<SectionOutlet>(0);
            b.AddAttribute(1, nameof(SectionOutlet.SectionName), PageFrame.TopBanner);
            b.CloseComponent();
            b.OpenComponent<PageFrame>(2);
            b.AddAttribute(3, nameof(PageFrame.Banner), Banner);
            b.AddAttribute(4, nameof(PageFrame.Title), "Part");
            b.AddAttribute(5, nameof(PageFrame.ChildContent), (RenderFragment)(c => c.AddMarkupContent(0, "<p>body</p>")));
            b.CloseComponent();
            b.OpenComponent<SectionOutlet>(6);
            b.AddAttribute(7, nameof(SectionOutlet.SectionName), PageFrame.BottomBanner);
            b.CloseComponent();
        }
    }

    [Fact]
    public void Page_frame_places_marking_banner_at_top_and_bottom_of_layout()
    {
        var cut = Render<FrameHost>(p => p.Add(x => x.Banner, "CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D"));
        var banners = cut.FindAll("[role=note]");
        Assert.Equal(2, banners.Count);
        Assert.Equal(["top", "bottom"], banners.Select(b => b.GetAttribute("data-position")));
        Assert.All(banners, b =>
        {
            Assert.Equal("CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D", b.TextContent);
            Assert.Contains("tone-cui", b.ClassName, StringComparison.Ordinal);
        });
        Assert.Equal("Part", cut.Find("h1").TextContent);
    }

    [Fact]
    public void Unclassified_banner_uses_unclassified_tone()
    {
        var cut = Render<MarkingBanner>(p => p.Add(x => x.Text, "UNCLASSIFIED · DISTRIBUTION A").Add(x => x.Position, "top"));
        Assert.Contains("tone-unclassified", cut.Find("div").ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void Attribute_fields_edit_values_and_clear_blanks()
    {
        var input = new AttributeInput();
        input.Values["material"] = "4340";
        var cut = Render<AttributeFields>(p => p.Add(x => x.Definitions, Defs).Add(x => x.Value, input));

        Assert.Contains("Mass (kg) *", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("input")[1].Change("1.5");
        cut.Find("input[type=checkbox]").Change(true);
        cut.Find("select").Change("Major");
        cut.FindAll("input")[0].Change("");

        Assert.Equal("""{"mass":"1.5","critical_item":true,"criticality":"Major"}""", input.ToJson(Defs).ToJsonString());
    }

    [Fact]
    public void Markings_fields_disable_fields_that_do_not_apply()
    {
        var m = new MarkingsInput { ExportJurisdiction = ExportJurisdiction.None };
        var cut = Render<MarkingsFields>(p => p.Add(x => x.Value, m));
        var disabled = cut.FindAll("input[disabled]").Select(i => i.GetAttribute("placeholder") ?? "").ToList();
        Assert.Equal(3, disabled.Count); // ECCN, USML, export authorizations
    }
}

public sealed class WebServicesTests
{
    [Fact]
    public void Effective_markings_drop_fields_of_other_jurisdictions()
    {
        var input = new MarkingsInput { ExportJurisdiction = ExportJurisdiction.Ear, Eccn = "9E610", UsmlCategory = "XI(d)", ExportAuthorizations = "TAA-1, ,DSP5-2" };
        var m = input.Effective().ToMarkings();
        Assert.True(m.IsSuccess, string.Join(";", m.Errors));
        Assert.Null(m.Value.UsmlCategory);
        Assert.Equal(["DSP5-2", "TAA-1"], m.Value.ExportAuthorizations);
        var back = MarkingsInput.From(m.Value);
        Assert.Equal(("DSP5-2, TAA-1", "9E610", ""), (back.ExportAuthorizations, back.Eccn, back.UsmlCategory));
    }

    [Fact]
    public void Attribute_values_round_trip_and_format()
    {
        var input = AttributeInput.From(new JsonObject { ["mass"] = 0.84m, ["critical_item"] = true, ["material"] = "Ti" });
        Assert.Equal("0.84", input.Values["mass"]);
        Assert.Equal(("Mass", "0.84 kg"), AttributeInput.Format([new("mass", "Mass", "number", "kg", null, false, null, 1)], "mass", JsonValue.Create(0.84m)));
        Assert.Equal(("critical_item", "Yes"), AttributeInput.Format([], "critical_item", JsonValue.Create(true)));
    }

    [Fact]
    public void Session_expires_at_recorded_lifetime()
    {
        var now = DateTimeOffset.UtcNow;
        ClaimsPrincipal P(long exp) => new(new ClaimsIdentity([new Claim(KeelUi.SessionExpiresClaim, exp.ToString(System.Globalization.CultureInfo.InvariantCulture))], "test"));
        Assert.True(SessionRevalidatingStateProvider.IsValid(P(now.AddMinutes(5).ToUnixTimeSeconds()), now));
        Assert.False(SessionRevalidatingStateProvider.IsValid(P(now.AddMinutes(-1).ToUnixTimeSeconds()), now));
        Assert.False(SessionRevalidatingStateProvider.IsValid(new ClaimsPrincipal(new ClaimsIdentity()), now));
    }

    [Fact]
    public void A_cookie_without_a_session_id_is_stale_and_refused_so_the_user_signs_in_again()
    {
        static ClaimsPrincipal P(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));
        Assert.True(KeelUi.IsStaleSession(P(new Claim("sub", "u1"))));                                         // issued before the lock feature
        Assert.True(KeelUi.IsStaleSession(P(new Claim("sub", "u1"), new Claim(KeelUi.SessionIdClaim, ""))));  // empty id
        Assert.False(KeelUi.IsStaleSession(P(new Claim("sub", "u1"), new Claim(KeelUi.SessionIdClaim, "abc"))));
        Assert.False(KeelUi.IsStaleSession(new ClaimsPrincipal(new ClaimsIdentity())));                        // anonymous is not "stale", just unauthenticated
        Assert.False(KeelUi.IsStaleSession(null));
    }

    [Fact]
    public void Reason_codes_are_explained()
    {
        Assert.Equal("With the new markings: You are not a member of the object's program.", UiText.Reason("new_markings:program.not_member"));
        Assert.Contains("CTI", UiText.Reason("cui.category_missing:CTI"), StringComparison.Ordinal);
        Assert.Equal("unknown.code", UiText.Reason("unknown.code"));
        Assert.Equal("Not found, or you are not authorized to see it.", UiText.Message(Outcome<int>.NotFound()));
    }
}
