using System.Reflection;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Bunit;
using Keel.Core;
using Keel.Core.Viewing;
using Keel.Documents;
using Keel.Web.Components.Shared;
using Keel.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.UnitTests;

/// <summary>The standalone viewer document and the "View" links (increment 6.0, ADR-0027).</summary>
public sealed partial class ViewerUiTests : BunitContext
{
    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)",
        cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;

    private static readonly Markings Open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;

    private static ViewerPageModel ModelFor(Markings m, string status = "ready") => new()
    {
        Banner = m.BannerText(),
        Title = "housing.stl",
        Status = status,
        Message = status == "ready" ? null : "This file is too large to preview (120 MiB; the limit for STL model files is 100 MiB). Download it instead.",
        Notices = Keel.Documents.Marking.MarkingStatements.Notices(m),
        DocumentNumber = "D-000042",
        FileName = "housing.stl",
        FormatLabel = "STL model",
        FormatId = "stl",
        ViewerType = "model3d",
        SizeBytes = 3007,
        Sha256 = new string('a', 64),
        IterationLabel = "A.2",
        BackUrl = "/documents/D-000042",
        DownloadUrl = "/downloads/documents/D-000042/files/00000000-0000-0000-0000-000000000001",
        MetaUrl = "/viewer/meta/documents/D-000042/files/00000000-0000-0000-0000-000000000001",
        ContentUrl = "/viewer/content/documents/D-000042/files/00000000-0000-0000-0000-000000000001",
        MaxTriangles = 5_000_000,
    };

    private static async Task<AngleSharp.Dom.IDocument> RenderAsync(ViewerPageModel model)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var html = await ViewerUi.RenderAsync(services, model);
        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        return new HtmlParser().ParseDocument(html);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_standalone_document_draws_the_objects_banner_top_and_bottom_and_the_distribution_statement(bool itar)
    {
        var m = itar ? Itar : Open;
        var doc = await RenderAsync(ModelFor(m));

        var banners = doc.QuerySelectorAll("[data-viewer-banner]");
        Assert.Equal(2, banners.Length);
        Assert.Equal(["top", "bottom"], banners.Select(b => b.GetAttribute("data-position")).ToArray());
        Assert.All(banners, b => Assert.Equal(m.BannerText(), b.TextContent));
        Assert.Equal(itar, banners[0].ClassList.Contains("tone-cui"));
        Assert.Equal("region", banners[0].GetAttribute("role"));

        var statement = doc.QuerySelector("[data-viewer-distribution]")!;
        Assert.StartsWith("DISTRIBUTION STATEMENT " + m.DistributionStatement, statement.TextContent, StringComparison.Ordinal);
        Assert.Contains("Back to document", doc.QuerySelector("a.viewer-back")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("/documents/D-000042", doc.QuerySelector("a.viewer-back")!.GetAttribute("href"));
        Assert.NotNull(doc.QuerySelector("#viewer-stage"));
        Assert.Equal("viewer.js", doc.QuerySelector("script[type=module]")!.GetAttribute("src")!.Split('/').Last());
    }

    [Fact]
    public async Task The_document_is_csp_clean_no_inline_script_style_or_handlers()
    {
        var doc = await RenderAsync(ModelFor(Itar));
        Assert.All(doc.QuerySelectorAll("script"), s => Assert.False(string.IsNullOrEmpty(s.GetAttribute("src")), "inline script"));
        Assert.Empty(doc.QuerySelectorAll("style"));
        Assert.Empty(doc.QuerySelectorAll("[style]"));
        Assert.Empty(doc.QuerySelectorAll("iframe, frame, object, embed, form"));
        foreach (var e in doc.All) Assert.DoesNotContain(e.Attributes, a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase));
        Assert.All(doc.QuerySelectorAll("[src], [href]"), e => Assert.DoesNotMatch(@"^https?://", e.GetAttribute("src") ?? e.GetAttribute("href") ?? ""));
        Assert.Null(doc.QuerySelector("script[src*='blazor']"));
    }

    [Theory]
    [InlineData("too-large", "Too large to preview")]
    [InlineData("no-preview", "No preview for this type")]
    [InlineData("blocked", "Preview not available")]
    public async Task Files_that_cannot_be_previewed_show_a_clear_panel_with_facts_the_banner_and_the_download_action(string status, string heading)
    {
        var doc = await RenderAsync(ModelFor(Itar, status));
        Assert.Equal(heading, doc.QuerySelector(".viewer-panel h2")!.TextContent);
        Assert.Contains("Download it instead", doc.QuerySelector(".viewer-panel [role=status]")!.TextContent, StringComparison.Ordinal);
        Assert.Equal(2, doc.QuerySelectorAll("[data-viewer-banner]").Length);
        Assert.Contains("housing.stl", doc.QuerySelector(".viewer-details")!.TextContent, StringComparison.Ordinal);
        Assert.Contains(new string('a', 64), doc.QuerySelector(".viewer-details")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(doc.QuerySelector("a.viewer-button[download]"));
        Assert.Null(doc.QuerySelector("script")); // no viewer script when there is nothing to show
        Assert.Null(doc.QuerySelector("#viewer-stage"));
    }

    [Fact]
    public async Task Not_found_page_discloses_nothing_and_still_carries_both_banners()
    {
        var doc = await RenderAsync(new ViewerPageModel { Banner = "UNCLASSIFIED", Title = "File not available", Status = "not-found", Message = "Document D-000042 or its file was not found, or you are not authorized to see it." });
        Assert.Equal(2, doc.QuerySelectorAll("[data-viewer-banner]").Length);
        Assert.Equal("alert", doc.QuerySelector(".viewer-error")!.GetAttribute("role"));
        Assert.Null(doc.QuerySelector(".viewer-facts"));
        Assert.Null(doc.QuerySelector("a.viewer-button"));
    }

    // ---------------------------------------------------------------- View links

    private static DocumentFileView File(string name, string mediaType = "application/pdf") =>
        new(Guid.CreateVersion7(), "primary", name, mediaType, 2048, new string('b', 64), DateTimeOffset.UnixEpoch, "alice");

    [Fact]
    public void File_list_offers_View_next_to_Download_only_when_downloading_is_allowed_and_badges_previewable_types()
    {
        var pdf = File("housing.pdf");
        var cut = Render<DocumentFileList>(p => p.Add(x => x.Files, [pdf, File("loads.csv", "text/csv"), File("bracket.STEP", "application/octet-stream")]).Add(x => x.DownloadNumber, "D-000001"));
        var views = cut.FindAll("a[data-action=view]");
        Assert.Equal(3, views.Count);
        Assert.Equal($"/viewer/documents/D-000001/files/{pdf.Id:D}", views[0].GetAttribute("href"));
        Assert.All(views, a => { Assert.Equal("_top", a.GetAttribute("target")); Assert.Equal("false", a.GetAttribute("data-enhance-nav")); });
        Assert.Equal(["PDF", "3D"], cut.FindAll(".badge.file-type").Select(b => b.TextContent).ToArray());

        // The same list for a user who may not download shows neither action.
        var noDownload = Render<DocumentFileList>(p => p.Add(x => x.Files, [pdf]));
        Assert.Empty(noDownload.FindAll("a[data-action=view]"));
        // And the working copy (no download number) has no View either.
        Assert.Empty(noDownload.FindAll("a[download]"));
    }

    [Fact]
    public void Linked_documents_open_their_primary_file_in_the_viewer_at_the_pinned_revision()
    {
        var link = new PartDocumentLinkView(Guid.CreateVersion7(), "describes", false, false,
            new LinkedDocumentView("D-000001", "Bracket drawing", "Drawing", "B", 1, "Released", Open.BannerText(), Open), "B", false, DateTimeOffset.UnixEpoch, "alice");
        var references = new PartDocumentLinkView(Guid.CreateVersion7(), "references", false, false,
            new LinkedDocumentView("D-000002", "Spec", "Specification", "A", 1, "Released", Open.BannerText(), Open), null, false, DateTimeOffset.UnixEpoch, "alice");
        var hidden = new PartDocumentLinkView(Guid.CreateVersion7(), "references", false, true, null, null, false, null, null);
        var cut = Render<DocumentLinkList>(p => p.Add(x => x.Links, [link, references, hidden]));
        var hrefs = cut.FindAll("a[data-action=view-primary]").Select(a => a.GetAttribute("href")).ToArray();
        Assert.Equal(["/viewer/documents/D-000001/primary?revision=B", "/viewer/documents/D-000002/primary"], hrefs);
    }

    // ---------------------------------------------------------------- architecture rules for standalone viewer pages

    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File_Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
        static bool File_Exists(string p) => System.IO.File.Exists(p);
    }

    /// <summary>
    /// A standalone viewer document is not a Blazor page (no @page, so no PageFrame): it must therefore draw the marking banner itself, at the top and the
    /// bottom, with text taken from the model's banner, plus the distribution statement, and must stay free of inline script and style.
    /// </summary>
    [Fact]
    public void Standalone_viewer_documents_render_both_banners_from_the_model_and_the_distribution_statement()
    {
        var folder = Path.Combine(Repo(), "src", "Keel.Web", "Components", "Pages", "Viewer");
        var files = Directory.EnumerateFiles(folder, "*.razor").ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var text = System.IO.File.ReadAllText(file);
            var name = Path.GetFileName(file);
            Assert.DoesNotContain("@page", text, StringComparison.Ordinal);
            Assert.DoesNotContain("<PageFrame", text, StringComparison.Ordinal);
            Assert.DoesNotContain("@rendermode", text, StringComparison.Ordinal);
            Assert.True(Regex.Count(text, @"data-position=""(top|bottom)""[^>]*data-viewer-banner[^>]*>@Model\.Banner<") == 2, $"{name} must render the top and the bottom banner from Model.Banner.");
            Assert.Contains("data-position=\"top\"", text, StringComparison.Ordinal);
            Assert.Contains("data-position=\"bottom\"", text, StringComparison.Ordinal);
            Assert.Contains("data-viewer-distribution", text, StringComparison.Ordinal);
            Assert.Contains("Back to document", text, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"<script(?![^>]*\bsrc=)", text);
            Assert.DoesNotMatch(@"<style|\sstyle=""", text);
        }
    }

    /// <summary>The host takes a real file's banner from the object's markings (ViewableFile.Banner is Markings.BannerText() of the documents module), never from a literal.</summary>
    [Fact]
    public void Viewer_endpoints_take_the_banner_from_the_described_file_and_every_route_requires_a_session()
    {
        var text = System.IO.File.ReadAllText(Path.Combine(Repo(), "src", "Keel.Api", "Endpoints", "ViewerEndpoints.cs"));
        Assert.Contains("Banner = f.Banner,", text, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(text, @"\bBanner = f\.Banner"));
        Assert.Equal(1, Regex.Count(text, "Banner = \"")); // the neutral banner of the not-found page is the only literal
        Assert.Contains("MapGroup(\"/viewer\").RequireAuthorization(Policies.AnyPlmRole)", text, StringComparison.Ordinal);
        Assert.Equal(4, Regex.Count(text, @"viewer\.MapGet\("));

        var source = System.IO.File.ReadAllText(Path.Combine(Repo(), "src", "Keel.Documents", "DocumentService.View.cs"));
        Assert.Contains("head.Markings.BannerText()", source, StringComparison.Ordinal);
        Assert.Contains("AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Download)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Viewer_module_references_only_core_security_and_audit()
    {
        var refs = Assembly.Load("Keel.Viewer").GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("Keel.", StringComparison.Ordinal)).ToList();
        Assert.Empty(refs.Except(["Keel.Core", "Keel.Security", "Keel.Audit"]));
        Assert.Contains("Keel.Core", refs);
    }
}
