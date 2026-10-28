using System.Text.RegularExpressions;
using Bunit;
using Keel.Core;
using Keel.Integration;
using Keel.Security;
using Keel.Web.Components.Shared;

namespace Keel.UnitTests;

/// <summary>Rules for the integration module (ADR-0025): marked-table queries, module isolation, no stray egress, admin pages.</summary>
public sealed partial class IntegrationArchitectureTests
{
    private static IEnumerable<string> SourceFiles(string module) =>
        Directory.EnumerateFiles(Path.Combine(JsonSchemaLiteTests.RepoRoot(), "src", module), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>
    /// A statement that reads the outbox or the TDP record lists marked rows. Anything a person or service account sees applies the read filter; the
    /// dispatcher (DeliveryStore) is a system component with no end user, and the administration view of deliveries selects metadata only (no
    /// payload, no markings, no aggregate number).
    /// </summary>
    [Fact]
    public void Reads_of_the_outbox_and_the_tdp_record_apply_the_read_filter_or_belong_to_the_dispatcher()
    {
        var offenders = new List<string>();
        var checkedStatements = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(JsonSchemaLiteTests.RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in MarkedIntegrationTable().Matches(text))
            {
                checkedStatements++;
                var statement = text.Substring(Math.Max(0, m.Index - 700), Math.Min(1400, text.Length - Math.Max(0, m.Index - 700)));
                if (file.EndsWith("DeliveryStore.cs", StringComparison.Ordinal)) continue;
                if (statement.Contains("filter.Sql}", StringComparison.Ordinal)) continue;
                if (file.EndsWith("IntegrationAdminService.cs", StringComparison.Ordinal) && !m.Value.Contains("payload", StringComparison.OrdinalIgnoreCase)) continue;
                offenders.Add($"{Path.GetRelativePath(JsonSchemaLiteTests.RepoRoot(), file)}: {m.Value.Trim()}");
            }
        }
        Assert.True(checkedStatements >= 6, "The rule found too few statements; the pattern is probably broken.");
        Assert.True(offenders.Count == 0, "Unfiltered reads of integration.outbox / integration.tdp_export:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_integration_module_never_reads_other_modules_tables()
    {
        var offenders = SourceFiles("Keel.Integration").SelectMany(f => OtherModuleTables().Matches(File.ReadAllText(f)).Select(m => $"{Path.GetFileName(f)}: {m.Value}")).ToList();
        Assert.True(offenders.Count == 0, "Keel.Integration touches other modules' tables (use the Keel.Core interfaces):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Only_the_webhook_sink_makes_outbound_http_calls_and_no_address_is_hard_coded()
    {
        foreach (var file in SourceFiles("Keel.Integration"))
        {
            var text = File.ReadAllText(file);
            if (!file.EndsWith("Sinks.cs", StringComparison.Ordinal))
                Assert.DoesNotContain("HttpClient", text, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"https?://(?!json-schema\.org|keel\.invalid)[a-z0-9.-]+\.[a-z]{2,}", text.Replace("https://erp.", "", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Integration_administration_pages_require_the_administrator_policy_and_use_the_page_frame()
    {
        var dir = Path.Combine(JsonSchemaLiteTests.RepoRoot(), "src", "Keel.Web", "Components", "Pages", "Integration");
        Assert.Contains("@attribute [Authorize(Policy = Policies.Admin)]", File.ReadAllText(Path.Combine(dir, "_Imports.razor")), StringComparison.Ordinal);
        var pages = Directory.EnumerateFiles(dir, "*.razor").Where(f => File.ReadAllText(f).Contains("@page \"", StringComparison.Ordinal)).ToList();
        Assert.True(pages.Count >= 6);
        foreach (var page in pages)
        {
            var text = File.ReadAllText(page);
            Assert.Contains("<PageFrame", text, StringComparison.Ordinal);
            // Only the export history is for every user (it is filtered by the reader's markings); everything else is administrator-only.
            if (!page.EndsWith("TdpHistoryPage.razor", StringComparison.Ordinal)) Assert.Contains("/admin/integration/", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Sink_configuration_has_no_place_for_secret_values()
    {
        // The only keys a webhook accepts name secrets, never hold them; the validator rejects anything else (SinkConfigurationTests covers the rejection).
        var source = File.ReadAllText(Path.Combine(JsonSchemaLiteTests.RepoRoot(), "src", "Keel.Integration", "Sinks.cs"));
        Assert.Matches(@"key is not \(""url"" or ""secretName"" or ""secondarySecretName"" or ""clientCertName"" or ""caBundleName"" or ""timeoutSeconds""\)", source);
        Assert.DoesNotContain("password", source, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"(FROM|JOIN)\s+integration\.(outbox|tdp_export)\b[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex MarkedIntegrationTable();

    [GeneratedRegex(@"\b(plm|chg|cfg|wf|identity|mig)\.[a-z_]+", RegexOptions.IgnoreCase)]
    private static partial Regex OtherModuleTables();
}

/// <summary>bUnit tests of the integration components. They are presentational (they take the computed plan), so no service is mocked.</summary>
public sealed class IntegrationComponentTests : BunitContext
{
    private static Markings Itar => Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"], controllingOffice: "ACME",
        distributionReason: "r", program: "ORCA").Value;

    private static TdpPlan Plan(params TdpRefusal[] refusals) => new(new TdpRequest("part", "P-000001", null, "dod"), "dod", "DodContractor", "P-000001", "A", Itar.BannerText(), Itar,
        [
            new TdpElement("cover-sheet", "OTHER", "cover-sheet.txt", null, null, null, null, null, null, null, null, true),
            new TdpElement("document", "DRAWINGS", "documents/DRAWINGS/D-000001-A/a.pdf", "D-000001", "A", "Drawing", Guid.NewGuid(), "a.pdf", 10, "ab", "Released", true),
            new TdpElement("document", "MODELS", "documents/MODELS/D-000002-A/b.step", "D-000002", "A", "Model", Guid.NewGuid(), "b.step", 10, "cd", "Released", true),
            new TdpElement("document", "OTHER", "documents/OTHER/D-000003-A/c.txt", "D-000003", "A", "Notes", Guid.NewGuid(), "c.txt", 10, "ef", "Released", false),
        ], refusals, 2, 3, 4, 1, ["P-000001 A"]);

    [Fact]
    public void A_permitted_plan_shows_markings_counts_and_categories_without_a_refusal()
    {
        var cut = Render<TdpPlanView>(p => p.Add(x => x.Plan, Plan()));
        Assert.Contains("CUI//EXPT", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("2 part(s), 4 structure line(s), 3 document(s)", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("1 document(s) have no category", cut.Markup, StringComparison.Ordinal);
        var categories = cut.FindAll("ul.categories li").Select(li => li.TextContent).ToList();
        Assert.Contains("Drawings: 1 document(s)", categories);
        Assert.Contains("Models: 1 document(s)", categories);
        Assert.Contains("Other technical data: 1 document(s)", categories);
        Assert.Empty(cut.FindAll(".refusals"));
    }

    [Fact]
    public void A_refused_plan_announces_the_reasons_with_counts_and_names_no_objects()
    {
        var cut = Render<TdpPlanView>(p => p.Add(x => x.Plan, Plan(new TdpRefusal("unreadable.documents", 2, "Linked documents are not readable by the exporter; a package must be complete."),
            new TdpRefusal(OutboundRelease.UsPersonRequired, 5, OutboundRelease.Describe(OutboundRelease.UsPersonRequired)))));
        var alert = cut.Find(".refusals");
        Assert.Equal("alert", alert.GetAttribute("role"));
        Assert.Contains("complete or it is refused", alert.TextContent, StringComparison.Ordinal);
        var items = alert.QuerySelectorAll("li").Select(li => li.TextContent).ToList();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.EndsWith("(2)", StringComparison.Ordinal));
        Assert.Contains(items, i => i.EndsWith("(5)", StringComparison.Ordinal));
    }

    [Fact]
    public void The_integration_navigation_links_every_administration_page_and_the_export_history()
    {
        var cut = Render<IntegrationNav>();
        Assert.Equal("Integration", cut.Find("nav").GetAttribute("aria-label"));
        var links = cut.FindAll("a").Select(a => a.GetAttribute("href")).ToList();
        foreach (var expected in new[] { "admin/integration/sinks", "admin/integration/profiles", "admin/integration/deliveries", "admin/integration/outbox", "admin/integration/tdp-categories", "tdp" })
            Assert.Contains(expected, links);
    }
}
