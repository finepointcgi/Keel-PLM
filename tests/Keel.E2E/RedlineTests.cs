using System.Text;
using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Compare;
using Keel.Documents.Vault;
using Keel.Seed;
using Microsoft.Playwright;
using UglyToad.PdfPig;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>Synthetic documents for the redline tests and the manual screenshots: a specification with two text versions, a second specification, and two binary files.</summary>
internal sealed record RedlineData(string Spec, Guid SpecV1, Guid SpecV2, string Other, string ModelA, string ModelB)
{
    private static RedlineData? cached;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public const string Banner = "CUI//EXPT · ITAR · DISTRIBUTION D · PROGRAM ORCA"; // the aggregate banner of the pair (the union, so no USML category)

    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"],
        controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;

    public const string V1 = """
        Sensor pod interface specification

        1. Scope
        This specification defines the mechanical interface between the sensor pod and the airframe.

        2. Mounting
        The pod shall be mounted with four M6 bolts. The mounting plate is 4 mm thick.
        Torque shall be 9 Nm.

        3. Environment
        The pod shall operate from -20 C to 55 C and survive 40 g shock.
        """;

    public const string V2 = """
        Sensor pod interface specification

        1. Scope
        This specification defines the mechanical and electrical interface between the sensor pod and the airframe.

        2. Mounting
        The pod shall be mounted with six M6 bolts. The mounting plate is 6 mm thick.
        Torque shall be 9 Nm.

        3. Environment
        The pod shall operate from -40 C to 55 C and survive 60 g shock.
        """;

    public static async Task<RedlineData> EnsureAsync(E2EFixture fx)
    {
        await Gate.WaitAsync();
        try
        {
            if (cached is not null) return cached;
            var alice = RealmUsers.Load()["alice"];
            var docs = new DocumentService(fx.AppDb, new PostgresAuditLog(fx.AppDb, TimeProvider.System), TimeProvider.System, new ObjectVault(fx.Vault));
            FileUpload Text(string text) => new("interface-spec.txt", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes(text)));
            var spec = await docs.CreateAsync(alice, new CreateDocumentCommand("Sensor pod interface specification", "Specification", Itar, "Synthetic specification for the redline tests"), Text(V1), null);
            Assert.True(spec.IsOk, string.Join("; ", spec.Errors));
            var up = await docs.UploadNewVersionAsync(alice, spec.Value!.Number, Text(V2), "Six bolts, thicker plate, wider temperature range", false, null, null);
            Assert.True(up.IsOk, string.Join("; ", up.Errors));
            var other = await docs.CreateAsync(alice, new CreateDocumentCommand("Pod environmental note", "Specification", Itar, "Synthetic note"), Text(V1.Replace("40 g", "50 g", StringComparison.Ordinal)), null);
            Assert.True(other.IsOk, string.Join("; ", other.Errors));
            async Task<string> Model(string name, byte[] bytes)
            {
                var r = await docs.CreateAsync(alice, new CreateDocumentCommand(name, "CAD", Itar, "Synthetic model"), new FileUpload(name.ToLowerInvariant().Replace(' ', '-') + ".dwg", "application/octet-stream", new MemoryStream(bytes)), null);
                Assert.True(r.IsOk, string.Join("; ", r.Errors));
                return r.Value!.Number;
            }
            var a = await Model("Drawing model A", [1, 2, 3, 4, 5]);
            var b = await Model("Drawing model B", [1, 2, 3, 4, 9, 9]);
            var versions = (await docs.ListCompareVersionsAsync(alice, spec.Value.Number, null)).Value!.Versions;
            return cached = new RedlineData(spec.Value.Number, versions[0].IterationId, versions[1].IterationId, other.Value!.Number, a, b);
        }
        finally
        {
            Gate.Release();
        }
    }

    public string SpecPage => $"/compare?left={Spec}&leftVersion={SpecV1}&right={Spec}&rightVersion={SpecV2}";
}

/// <summary>
/// The redline as people use it (increment 9.5, ADR-0053), in a real browser: the comparison page with its marked insertions and deletions, change navigation by button and
/// by key, inline and side by side, review comments (add, resolve), PDF and HTML export carrying the banners, the honest "not comparable" result, the access rule on both
/// documents, the review hint on the document, and an accessibility audit of the page in every theme and at 375 px.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class RedlineTests(E2EFixture fx)
{
    private static readonly Regex Number = new(@"(\d+)");

    private static async Task<int> CountOfAsync(ILocator locator, string testId) => int.Parse((await locator.GetByTestId(testId).InnerTextAsync()).Trim(), System.Globalization.CultureInfo.InvariantCulture);

    [E2EFact(Timeout = 600_000)]
    public async Task Two_versions_are_marked_navigated_commented_resolved_and_exported_with_the_banners()
    {
        await using var session = new BrowserSession(fx, nameof(Two_versions_are_marked_navigated_commented_resolved_and_exported_with_the_banners));
        await session.RunAsync(async () =>
        {
            var data = await RedlineData.EnsureAsync(fx);
            var alice = await session.SignInAsync("alice", 1440, 900);
            var page = alice.Page;
            await session.GotoAsync(alice, data.SpecPage);
            await Expect(page.GetByTestId("compare-result")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
            await BrowserSession.AssertBannersAsync(page);
            Assert.Equal(RedlineData.Banner, (await BrowserSession.TopBanner(page).InnerTextAsync()).Trim());

            // Summary counts: the specification changed in five places ("mechanical and electrical", "four" -> "six", "4" -> "6", "-20" -> "-40", "40" -> "60").
            var summary = page.GetByTestId("compare-summary");
            Assert.Equal(5, await CountOfAsync(summary, "count-changes"));
            Assert.True(await CountOfAsync(summary, "count-inserted") >= 5);
            Assert.True(await CountOfAsync(summary, "count-deleted") >= 4);

            // Insertions and deletions are marked by more than colour: element, underline or strike-through, and visible marks.
            var inline = page.GetByTestId("redline-inline");
            await Expect(inline.Locator("ins")).Not.ToHaveCountAsync(0);
            await Expect(inline.Locator("del")).Not.ToHaveCountAsync(0);
            Assert.Equal("underline", (await inline.Locator("ins").First.EvaluateAsync<string>("e => getComputedStyle(e).textDecorationLine")).Split(' ')[0]);
            Assert.Equal("line-through", (await inline.Locator("del").First.EvaluateAsync<string>("e => getComputedStyle(e).textDecorationLine")).Split(' ')[0]);
            await Expect(inline.Locator("ins .rl-mark").First).ToHaveTextAsync("[+");
            await Expect(inline.Locator("del .rl-mark").First).ToHaveTextAsync("[−");
            await Expect(inline.Locator("ins", new LocatorLocatorOptions { HasText = "electrical" })).ToBeVisibleAsync();
            await Expect(inline.Locator("del", new LocatorLocatorOptions { HasText = "four" })).ToBeVisibleAsync();

            // Next / previous by button.
            var status = page.GetByTestId("change-status");
            await Expect(status).ToHaveTextAsync("5 changes");
            await page.GetByTestId("next-change").ClickAsync();
            await Expect(status).ToHaveTextAsync("Change 1 of 5");
            await Expect(inline.Locator(".rl-current").First).ToBeVisibleAsync();
            await page.GetByTestId("next-change").ClickAsync();
            await Expect(status).ToHaveTextAsync("Change 2 of 5");
            await page.GetByTestId("prev-change").ClickAsync();
            await Expect(status).ToHaveTextAsync("Change 1 of 5");
            await page.GetByTestId("prev-change").ClickAsync();
            await Expect(status).ToHaveTextAsync("Change 5 of 5"); // wraps

            // By keyboard: focus the redline, N and P.
            await inline.FocusAsync();
            await page.Keyboard.PressAsync("n");
            await Expect(status).ToHaveTextAsync("Change 1 of 5");
            await page.Keyboard.PressAsync("n");
            await page.Keyboard.PressAsync("n");
            await Expect(status).ToHaveTextAsync("Change 3 of 5");
            await page.Keyboard.PressAsync("p");
            await Expect(status).ToHaveTextAsync("Change 2 of 5");

            // Side by side: deletions on the left, insertions on the right, same change selected.
            await page.GetByTestId("mode-side").CheckAsync();
            var left = page.GetByTestId("redline-left");
            var right = page.GetByTestId("redline-right");
            await Expect(left).ToBeVisibleAsync();
            await Expect(right).ToBeVisibleAsync();
            await Expect(left.Locator("del", new LocatorLocatorOptions { HasText = "four" })).ToBeVisibleAsync();
            await Expect(left.Locator("ins")).ToHaveCountAsync(0);
            await Expect(right.Locator("ins", new LocatorLocatorOptions { HasText = "six" })).ToBeVisibleAsync();
            await Expect(right.Locator("del")).ToHaveCountAsync(0);
            await Expect(status).ToHaveTextAsync("Change 2 of 5");
            await page.GetByTestId("next-change").ClickAsync();
            await Expect(status).ToHaveTextAsync("Change 3 of 5");
            await page.GetByTestId("mode-inline").CheckAsync();

            // Comment on change 3, reply as bob later; resolve.
            await Expect(page.GetByTestId("no-threads")).ToBeVisibleAsync();
            await page.GetByTestId("comment-body").FillAsync("Is a 6 mm plate really needed?");
            await page.GetByTestId("comment-add").ClickAsync();
            var thread = page.GetByTestId("thread").First;
            await Expect(thread).ToContainTextAsync("Is a 6 mm plate really needed?");
            await Expect(thread).ToContainTextAsync("Open");
            await Expect(page.GetByTestId("compare-summary")).ToContainTextAsync("1 open of 1");
            await thread.GetByRole(AriaRole.Textbox).FillAsync("Yes: the shock case governs.");
            await thread.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Reply" }).ClickAsync();
            await Expect(thread).ToContainTextAsync("Yes: the shock case governs.");
            await thread.GetByTestId("resolve").ClickAsync();
            await Expect(page.GetByTestId("thread").First).ToHaveAttributeAsync("data-resolved", "true");
            await Expect(page.GetByTestId("compare-summary")).ToContainTextAsync("0 open of 1");
            // A second, unresolved comment on change 1 so the document's review shows an open thread.
            await page.GetByTestId("next-change").ClickAsync();
            await page.GetByTestId("comment-body").FillAsync("Please state the interface connector.");
            await page.GetByTestId("comment-add").ClickAsync();
            await Expect(page.GetByTestId("compare-summary")).ToContainTextAsync("1 open of 2");

            // Export: the PDF carries the banner on every page and the comments; the HTML is a self-contained file with both banners.
            var pdfDownload = await page.RunAndWaitForDownloadAsync(() => page.GetByTestId("export-pdf").ClickAsync());
            var pdfPath = Path.Combine(fx.ResultsDir, "redline-export.pdf");
            await pdfDownload.SaveAsAsync(pdfPath);
            using (var pdf = PdfDocument.Open(pdfPath))
            {
                Assert.All(pdf.GetPages(), p => Assert.True(Regex.Count(p.Text, "DISTRIBUTION D") >= 2, $"banner at both ends of page {p.Number}"));
                var all = string.Concat(pdf.GetPages().Select(p => p.Text));
                Assert.Contains("Is a 6 mm plate really needed?", all);
                Assert.Contains("[+", all);
                Assert.Contains("DISTRIBUTION STATEMENT D", all);
            }
            var htmlDownload = await page.RunAndWaitForDownloadAsync(() => page.GetByTestId("export-html").ClickAsync());
            var htmlPath = Path.Combine(fx.ResultsDir, "redline-export.html");
            await htmlDownload.SaveAsAsync(htmlPath);
            var html = await File.ReadAllTextAsync(htmlPath);
            Assert.Equal(2, Regex.Count(html, "class=\"banner "));
            Assert.Contains("<ins ", html);
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);

            // The review flows into the document: the Review & release tab counts the open thread and links to it.
            await session.GotoAsync(alice, $"/documents/{data.Spec}?tab=review");
            await Expect(page.GetByTestId("redline-hint")).ToContainTextAsync("1 open comment thread");
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Redline review started by" }).First.ClickAsync();
            await page.WaitForURLAsync(new Regex("/compare\\?left="));
            await Expect(page.GetByTestId("compare-result")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
            await BrowserSession.AssertBannersAsync(page);
            // The Compare tab lists the review.
            await session.GotoAsync(alice, $"/documents/{data.Spec}?tab=compare");
            await Expect(page.GetByTestId("review-table")).ToContainTextAsync("1 open of 2");
        });
    }

    [E2EFact(Timeout = 300_000)]
    public async Task Files_that_are_not_text_get_an_honest_result_and_the_picker_finds_versions()
    {
        await using var session = new BrowserSession(fx, nameof(Files_that_are_not_text_get_an_honest_result_and_the_picker_finds_versions));
        await session.RunAsync(async () =>
        {
            var data = await RedlineData.EnsureAsync(fx);
            var alice = await session.SignInAsync("alice", 1440, 900);
            var page = alice.Page;
            await session.GotoAsync(alice, $"/compare?left={data.ModelA}&right={data.ModelB}");
            await Expect(page.GetByTestId("compare-not-comparable")).ToContainTextAsync("Content not comparable", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
            await Expect(page.GetByTestId("compare-facts")).ToContainTextAsync("SHA-256");
            await Expect(page.GetByTestId("compare-facts")).ToContainTextAsync("5 bytes");
            await Expect(page.GetByTestId("compare-facts")).ToContainTextAsync("6 bytes");
            await Expect(page.GetByTestId("redline-inline")).ToHaveCountAsync(0);
            await BrowserSession.AssertBannersAsync(page);

            // The picker: type two numbers, find versions, compare.
            await session.GotoAsync(alice, "/compare");
            await page.GetByTestId("left-number").FillAsync(data.Spec);
            await page.GetByTestId("left-find").ClickAsync();
            await page.GetByTestId("right-number").FillAsync(data.Other);
            await page.GetByTestId("right-find").ClickAsync();
            await Expect(page.GetByTestId("left-version")).ToBeVisibleAsync();
            await Expect(page.GetByTestId("right-version")).ToBeVisibleAsync();
            await page.GetByTestId("compare-go").ClickAsync();
            await page.WaitForURLAsync(new Regex("/compare\\?left="));
            await Expect(page.GetByTestId("compare-result")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
            await Expect(page.GetByTestId("compare-summary")).ToContainTextAsync("Changes");
            Assert.Contains(RedlineData.Banner, (await BrowserSession.TopBanner(page).InnerTextAsync()));
        });
    }

    [E2EFact(Timeout = 300_000)]
    public async Task Someone_who_cannot_read_both_documents_sees_nothing_of_either()
    {
        await using var session = new BrowserSession(fx, nameof(Someone_who_cannot_read_both_documents_sees_nothing_of_either));
        await session.RunAsync(async () =>
        {
            var data = await RedlineData.EnsureAsync(fx);
            var lena = await session.SignInAsync("lena", 1440, 900);
            await session.GotoAsync(lena, data.SpecPage);
            var problem = lena.Page.GetByTestId("compare-problem");
            await Expect(problem).ToContainTextAsync("not found, or you are not authorized to see both", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
            var body = await lena.Page.Locator("body").InnerTextAsync();
            Assert.DoesNotContain("Sensor pod interface specification", body, StringComparison.Ordinal);
            Assert.DoesNotContain("M6 bolts", body, StringComparison.Ordinal);
            Assert.Equal("UNCLASSIFIED", (await BrowserSession.TopBanner(lena.Page).InnerTextAsync()).Trim());
        });
    }

    [E2EFact(Timeout = 900_000)]
    public async Task The_compare_page_has_no_accessibility_violations_in_any_theme_or_at_phone_width()
    {
        await using var session = new BrowserSession(fx, nameof(The_compare_page_has_no_accessibility_violations_in_any_theme_or_at_phone_width));
        await session.RunAsync(async () =>
        {
            var data = await RedlineData.EnsureAsync(fx);
            var axe = await File.ReadAllTextAsync(AxePath());
            var findings = new List<string>();
            foreach (var width in new[] { 1440, 375 })
            {
                var user = await session.SignInAsync("alice", width, width == 375 ? 812 : 900);
                var page = user.Page;
                foreach (var theme in new[] { "light", "dark", "high-contrast" })
                {
                    await session.GotoAsync(user, "/");
                    await Expect(page.Locator("h1").First).ToBeVisibleAsync();
                    await SetThemeAsync(page, theme);
                    foreach (var (path, label) in new[] { (data.SpecPage, "result"), ("/compare", "picker"), ($"/compare?left={data.ModelA}&right={data.ModelB}", "not comparable"), ($"/documents/{data.Spec}?tab=compare", "compare tab") })
                    {
                        await page.GotoAsync(fx.BaseUrl + path);
                        await Expect(page.Locator("h1").First).ToBeVisibleAsync();
                        if (label == "result")
                        {
                            await Expect(page.GetByTestId("compare-result")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
                            await page.GetByTestId("next-change").ClickAsync(); // a current change is outlined
                        }
                        else await page.WaitForTimeoutAsync(1200);
                        await BrowserSession.AssertBannersAsync(page);
                        await AccessibilityTests.RunAxeAsync(page, $"{theme}/{width}px {label}", axe, findings);
                        if (label == "result")
                        {
                            await page.GetByTestId("mode-side").CheckAsync();
                            await page.WaitForTimeoutAsync(400);
                            await AccessibilityTests.RunAxeAsync(page, $"{theme}/{width}px result side by side", axe, findings);
                        }
                    }
                }
                await SetThemeAsync(page, "light");
            }
            Assert.True(findings.Count == 0, "Accessibility violations on the compare page:\n" + string.Join("\n", findings));
        });
    }

    private static async Task SetThemeAsync(IPage page, string theme)
    {
        await page.Locator("#user-menu-button").ClickAsync();
        await page.Locator($"[data-theme-choice={theme}]").ClickAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", theme);
    }

    private static string AxePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "Keel.E2E", "vendor", "axe-core", "axe.min.js");
    }
}
