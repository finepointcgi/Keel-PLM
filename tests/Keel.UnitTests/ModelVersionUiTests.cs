using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Keel.Core;
using Keel.Documents.Marking;
using Keel.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.UnitTests;

/// <summary>The standalone compare document and the architecture rules of model versioning (increment 9.1, ADR-0046).</summary>
public sealed partial class ModelVersionUiTests
{
    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)",
        cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;

    private static readonly Guid FileA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid FileB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static ViewerCompareModel Ready(Markings m) => new()
    {
        Banner = m.BannerText(), Title = "Compare A.1 and A.2", Status = "ready", Notices = MarkingStatements.Notices(m), DocumentNumber = "D-000042", DocumentName = "Bracket",
        A = new ViewerCompareSide("A.1", "bracket.stl", new string('a', 64), 1000, ViewerCompareUi.MeshUrl("D-000042", FileA), "12 triangles · 20 × 40 × 30 units"),
        B = new ViewerCompareSide("A.2", "bracket.stl", new string('b', 64), 1100, ViewerCompareUi.MeshUrl("D-000042", FileB), "12 triangles · 20 × 40 × 32 units"),
        Summary = "Taller by 2.0 units, volume up 3.0 %", BackUrl = "/documents/D-000042?tab=versions", MaxTriangles = 1_000_000,
    };

    private static async Task<AngleSharp.Dom.IDocument> RenderAsync(ViewerCompareModel model)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var html = await ViewerCompareUi.RenderAsync(services, model);
        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        return new HtmlParser().ParseDocument(html);
    }

    [Fact]
    public async Task The_compare_document_carries_both_banners_the_statement_both_versions_and_the_frame_note()
    {
        var doc = await RenderAsync(Ready(Itar));
        var banners = doc.QuerySelectorAll("[data-viewer-banner]");
        Assert.Equal(2, banners.Length);
        Assert.All(banners, b => Assert.Equal(Itar.BannerText(), b.TextContent));
        Assert.Equal(["top", "bottom"], banners.Select(b => b.GetAttribute("data-position")!).ToArray());
        Assert.Contains("DISTRIBUTION STATEMENT D", doc.QuerySelector("[data-viewer-distribution]")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Taller by 2.0 units, volume up 3.0 %", doc.QuerySelector("[data-compare-summary]")!.TextContent, StringComparison.Ordinal);
        var body = doc.Body!;
        Assert.Equal($"/viewer/mesh/documents/D-000042/files/{FileA:D}", body.GetAttribute("data-mesh-a"));
        Assert.Equal($"/viewer/mesh/documents/D-000042/files/{FileB:D}", body.GetAttribute("data-mesh-b"));
        Assert.Equal("A.1", body.GetAttribute("data-label-a"));
        Assert.Contains("no automatic registration", doc.QuerySelector("[data-compare-frame-note]")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("version A.1", doc.QuerySelector(".compare-key")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(doc.QuerySelector("a.viewer-back"));
        Assert.Equal("polite", doc.QuerySelector("#viewer-status")!.GetAttribute("aria-live"));
    }

    [Fact]
    public async Task The_compare_document_has_one_external_module_script_and_no_inline_script_or_style()
    {
        var doc = await RenderAsync(Ready(Itar));
        var scripts = doc.QuerySelectorAll("script").ToList();
        var script = Assert.Single(scripts);
        Assert.Equal("module", script.GetAttribute("type"));
        Assert.EndsWith("/compare.js", script.GetAttribute("src"), StringComparison.Ordinal);
        Assert.Empty(script.TextContent);
        Assert.Empty(doc.QuerySelectorAll("style"));
        Assert.Empty(doc.QuerySelectorAll("[style]"));
    }

    [Fact]
    public async Task A_version_that_cannot_be_shown_gets_a_message_both_banners_and_no_script()
    {
        var model = new ViewerCompareModel { Banner = Itar.BannerText(), Title = "Visual comparison not available", Status = "unavailable", Message = "The visual comparison supports STL, OBJ and 3MF models that Keel could read.", Notices = MarkingStatements.Notices(Itar), DocumentNumber = "D-000042" };
        var doc = await RenderAsync(model);
        Assert.Equal(2, doc.QuerySelectorAll("[data-viewer-banner]").Length);
        Assert.Equal("alert", doc.QuerySelector(".viewer-error")!.GetAttribute("role"));
        Assert.Contains("STL, OBJ and 3MF", doc.QuerySelector(".viewer-error")!.TextContent, StringComparison.Ordinal);
        Assert.Empty(doc.QuerySelectorAll("script"));
        Assert.Null(doc.QuerySelector("#viewer-stage"));
    }

    [Fact]
    public async Task The_not_found_compare_page_uses_the_neutral_banner_and_discloses_nothing()
    {
        var model = new ViewerCompareModel { Banner = "UNCLASSIFIED", Title = "Comparison not available", Status = "not-found", Message = "Document D-000042 or its versions were not found, or you are not authorized to see them." };
        var doc = await RenderAsync(model);
        Assert.All(doc.QuerySelectorAll("[data-viewer-banner]"), b => Assert.Equal("UNCLASSIFIED", b.TextContent));
        Assert.Null(doc.QuerySelector(".viewer-facts"));
        Assert.Null(doc.QuerySelector("[data-viewer-notices]"));
        Assert.Empty(doc.QuerySelectorAll("script"));
    }

    [Fact]
    public void Urls_are_built_from_ids_and_escaped_numbers()
    {
        Assert.Equal($"/viewer/compare/documents/D-1?a={FileA:D}&b={FileB:D}", ViewerCompareUi.PageUrl("D-1", FileA, FileB));
        Assert.Equal($"downloads/documents/D-1/versions.zip?ids={FileA:D},{FileB:D}", DocumentUi.VersionsZipUrl("D-1", [FileA, FileB]));
        Assert.StartsWith("/viewer/mesh/documents/D%2F1/", ViewerCompareUi.MeshUrl("D/1", FileA), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- architecture rules

    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void Every_model_version_route_requires_a_session_and_the_compare_banner_comes_from_the_comparison()
    {
        var text = File.ReadAllText(Path.Combine(Repo(), "src", "Keel.Api", "Endpoints", "ModelVersionEndpoints.cs"));
        Assert.Contains("MapGroup(\"/api/v1/documents\").RequireAuthorization(Policies.AnyPlmRole)", text, StringComparison.Ordinal);
        Assert.Contains("MapGroup(\"/viewer\").RequireAuthorization(Policies.AnyPlmRole)", text, StringComparison.Ordinal);
        Assert.Contains("app.MapGet(DocumentUi.VersionsZipRoute, Zip).RequireAuthorization(Policies.AnyPlmRole)", text, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(text, @"Banner = c\.Banner"));
        Assert.Equal(1, Regex.Count(text, @"Banner = ViewerEndpoints\.NeutralBanner"));
        Assert.DoesNotContain("Banner = \"", text, StringComparison.Ordinal);
        // every mapped route lives in one of the three authorized groups
        Assert.Equal(0, Regex.Count(text, @"\bapp\.Map(Get|Post|Put|Delete)\((?!DocumentUi\.VersionsZipRoute)"));
    }

    [Fact]
    public void Measurements_are_written_by_the_documents_module_only_and_the_app_role_cannot_change_them()
    {
        var src = Path.Combine(Repo(), "src");
        var writers = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("INTO mdl.iteration_metrics", StringComparison.Ordinal)).Select(Path.GetFileName).ToList();
        Assert.Equal(["DocumentService.Models.cs"], writers);
        var sql = File.ReadAllText(Path.Combine(Repo(), "db", "migrations", "0028_model_versions.sql"));
        Assert.Contains("GRANT SELECT, INSERT ON mdl.iteration_metrics TO keel_app", sql, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"GRANT[^;]*(UPDATE|DELETE)[^;]*mdl\.", sql);
        Assert.Contains("BEFORE UPDATE OR DELETE ON mdl.iteration_metrics", sql, StringComparison.Ordinal);
        // no API accepts geometry numbers: the request models of the endpoints have no metric fields
        var endpoints = File.ReadAllText(Path.Combine(src, "Keel.Api", "Endpoints", "ModelVersionEndpoints.cs"));
        Assert.DoesNotContain("ModelMetrics", endpoints, StringComparison.Ordinal);
    }

    [Fact]
    public void The_version_actions_authorize_on_the_document_and_audit_every_outcome()
    {
        var text = File.ReadAllText(Path.Combine(Repo(), "src", "Keel.Documents", "DocumentService.Models.cs"));
        // reads: Read first, a denial is audited and answered with not found
        Assert.True(Regex.Count(text, @"AccessPolicy\.Evaluate\(subject, head\.Markings, PlmAction\.Read\)") >= 3);
        Assert.True(Regex.Count(text, @"AuditOutcome\.Denied") >= 3);
        // writes: through the document's own row-locked mutation, which authorizes Modify and audits success and denial
        foreach (var action in new[] { "VersionUploadAction", "VersionRestoreAction", "ReviseAction" })
            Assert.Contains($"MutateAsync(subject, number, {action}", text, StringComparison.Ordinal);
        Assert.Contains("PrecheckNewVersionAsync", text, StringComparison.Ordinal);
        // the audit details never carry measured values
        Assert.DoesNotMatch(@"\(""(triangles|volume|fingerprint|geometry|signedVolume)""", text);
    }

    [Fact]
    public void The_mesh_readers_have_no_recursion_into_themselves()
    {
        // a hostile file must not be able to deepen the stack: the readers use loops and explicit stacks only
        foreach (var file in new[] { "StlReader.cs", "ObjReader.cs", "ThreeMfReader.cs" })
        {
            var text = File.ReadAllText(Path.Combine(Repo(), "src", "Keel.Core", "Geometry", file));
            foreach (Match m in Regex.Matches(text, @"static \w[\w<>\[\], ?]* (\w+)\("))
            {
                var name = m.Groups[1].Value;
                var body = text[(m.Index + m.Length)..];
                var next = Regex.Match(body, @"\n    (public|private|internal) static ");
                var scope = next.Success ? body[..next.Index] : body;
                Assert.DoesNotMatch($@"(?<![\w.]){name}\(", scope);
            }
        }
    }
}
