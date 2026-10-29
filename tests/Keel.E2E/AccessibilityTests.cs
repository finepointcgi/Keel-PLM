using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Keel.Audit;
using Keel.Documents;
using Keel.Documents.Vault;
using Keel.Seed;
using Keel.TestSupport;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>
/// Automated accessibility audit (gap wave D, ADR-0034): the vendored axe-core (tests/Keel.E2E/vendor/axe-core, test-only, never shipped) is injected
/// into every major page through the DevTools protocol (so the strict CSP stays in force) and must report no violation of the WCAG 2.0/2.1 A and AA
/// rules (the basis of Section 508) in the light, dark and high-contrast themes at desktop width, and in light and high-contrast at 375 px.
/// What axe cannot judge stays on the manual list in docs/ux/accessibility.md.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class AccessibilityTests(E2EFixture fx)
{
    /// <summary>Recorded in tests/Keel.E2E/vendor/axe-core/VENDOR.md. A different file fails here until the record is updated on purpose.</summary>
    public const string AxeSha256 = "b511cd9dec01c76f4b2ad1723b66b6db37d4c2eb4ed199076e1829d9ee7b75e3";

    private static readonly string[] Tags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "best-practice"];

    [Fact]
    public void The_vendored_axe_core_is_the_recorded_release()
    {
        var path = AxePath();
        Assert.Equal(AxeSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        Assert.Contains("axe v4.10.2", File.ReadAllText(path)[..400], StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "LICENSE")), "The licence text of the vendored library must stay beside it.");
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "VENDOR.md")), "VENDOR.md records version, licence and hash.");
    }

    [Fact]
    public void Axe_is_vendored_for_tests_only_and_not_part_of_any_shipped_output()
    {
        var root = fx.RepoRoot;
        // Nothing under src/ may carry the library, and the container image build must not copy tests/.
        var carried = Directory.EnumerateFiles(Path.Combine(root, "src"), "axe*.js", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal)).ToList();
        Assert.True(carried.Count == 0, "axe must not be under src/: " + string.Join(", ", carried));
        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));
        Assert.DoesNotContain("tests/Keel.E2E/vendor", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("COPY tests", dockerfile, StringComparison.Ordinal);
    }

    [E2EFact(Timeout = 1_500_000)]
    public Task Every_major_page_passes_the_audit_in_the_light_theme() => AuditAsync("light", 1280, nameof(Every_major_page_passes_the_audit_in_the_light_theme));

    [E2EFact(Timeout = 1_500_000)]
    public Task Every_major_page_passes_the_audit_in_the_dark_theme() => AuditAsync("dark", 1280, nameof(Every_major_page_passes_the_audit_in_the_dark_theme));

    [E2EFact(Timeout = 1_500_000)]
    public Task Every_major_page_passes_the_audit_in_the_high_contrast_theme() => AuditAsync("high-contrast", 1280, nameof(Every_major_page_passes_the_audit_in_the_high_contrast_theme));

    [E2EFact(Timeout = 1_500_000)]
    public Task Every_major_page_passes_the_audit_at_375_px() => AuditAsync("light", 375, nameof(Every_major_page_passes_the_audit_at_375_px));

    [E2EFact(Timeout = 1_500_000)]
    public Task Every_major_page_passes_the_audit_at_375_px_in_high_contrast() => AuditAsync("high-contrast", 375, nameof(Every_major_page_passes_the_audit_at_375_px_in_high_contrast));

    // ------------------------------------------------------------------------------------------------------------------------------

    private sealed record Target(string Path, string User, string? Wait = null);

    private List<Target> Targets(DemoExtrasView x, QualityData q)
    {
        var m = fx.Manifest;
        var t = new List<Target>();
        void Add(string user, params string[] paths) { foreach (var p in paths) t.Add(new Target(p, user)); }

        Add("alice", "/", "/parts", "/parts?q=bracket", "/parts/new", "/parts/classification", "/documents", "/documents/new", "/changes", "/changes/new/ecr",
            "/baselines", "/baselines/new", "/baselines/compare", "/mboms", "/mboms/generate", "/tasks", "/workflows", "/search?q=" + m.GimbalBracket, "/search?q=bracket&kind=part",
            "/notifications", "/notifications?tab=unread", "/notifications/preferences", "/watching", "/profile", "/access", "/tdp", "/not-found", "/access-denied");

        foreach (var tab in new[] { "overview", "structure", "documents", "changes", "review", "classification", "history", "timeline", "security", "activity", "comments", "trace" })
            Add("alice", $"/parts/{m.GimbalBracket}?tab={tab}");
        Add("alice", $"/parts/{m.TopAssembly}?tab=structure", $"/parts/{m.TopAssembly}?tab=timeline", $"/parts/{m.GimbalBracket}/where-used", $"/parts/{m.TopAssembly}/where-used",
            $"/parts/{m.GimbalBracket}/compare", "/parts/P-000005/compare-units", $"/parts/{m.AvionicsAssemblyUnderReview}?tab=review", $"/parts/{m.OpenFastener}");

        foreach (var tab in new[] { "overview", "usedby", "history", "timeline", "security", "activity", "comments", "trace" })
            Add("alice", $"/documents/{m.SystemSpecification}?tab={tab}");
        // CAD integration (ADR-0035): the CAD tab of a document, the proposals list and the review page.
        var cadProposal = CadE2ESeed.EnsureAsync(fx).GetAwaiter().GetResult();
        Add("alice", $"/documents/{m.SystemSpecification}?tab=cad", "/cad/bom-proposals", $"/cad/bom-proposals/{cadProposal}");

        foreach (var tab in new[] { "overview", "affected", "impact", "impactmap", "tasks", "review", "security", "history", "activity", "comments", "trace" })
            Add("alice", $"/changes/{m.EcnDraftInWork}?tab={tab}");
        Add("alice", $"/changes/{m.EcnUnderReview}?tab=review", $"/changes/{m.EcrAwaitingCcb}", $"/changes/{m.ProblemReportOpen}");

        foreach (var tab in new[] { "overview", "review", "items", "activity", "comments" })
            Add("alice", $"/baselines/{x.DesignBaseline}?tab={tab}");
        Add("alice", $"/baselines/compare?from={x.DesignBaseline}&to={x.ReleaseBaseline}");

        foreach (var tab in new[] { "overview", "lines", "reconciliation", "review", "structure", "process", "timeline", "security", "activity", "comments" })
            Add("alice", $"/mboms/{x.ReleasedMbom}?tab={tab}");
        Add("alice", $"/mboms/{x.StaleMbom}?tab=reconciliation", $"/mboms/{x.ReleasedMbom}/compare", $"/mboms/{x.ReleasedMbom}/side-by-side");

        // Requirements and traceability (8.3, ADR-0038): the demo specification, every tab, the list, the creation page and the administrator's link types.
        var spec = fx.Manifest.Extras?.RequirementSpec ?? throw new InvalidOperationException("The demo requirement specification is missing.");
        Add("alice", "/requirements", "/requirements/new", $"/requirements?q={spec}");
        foreach (var tab in new[] { "overview", "requirements", "trace", "verification", "coverage", "exchange", "review", "timeline", "security", "activity", "comments" })
            Add("alice", $"/requirements/{spec}?tab={tab}");
        Add("ada", "/admin/requirement-link-types");

        // Suppliers, approved lists, requests and the supplier portal (8.2, ADR-0037): the synthetic rows come from SourcingSeed.
        Add("alice", "/suppliers", "/suppliers/new", "/requests", "/requests/new", "/sourcing/where-sourced", "/sourcing/coverage",
            $"/sourcing/where-sourced?supplier={SourcingSeed.SupplierA}", $"/sourcing/coverage?mode=ebom&number={m.TopAssembly}", $"/requests/{SourcingSeed.Request}",
            $"/sourcing/entries/{SourcingSeed.Entry}", $"/parts/{m.OpenFastener}?tab=sourcing", $"/parts/{m.GimbalBracket}?tab=sourcing");
        foreach (var tab in new[] { "overview", "certifications", "contacts", "parts", "quality", "review", "activity", "comments" })
            Add("alice", $"/suppliers/{SourcingSeed.SupplierA}?tab={tab}");
        Add("ada", $"/suppliers/{SourcingSeed.SupplierA}?tab=portal");
        // Product compliance (8.5, ADR-0040): the part tab, the roll-up (table, causes and treemap), declarations, regulatory lists, substances and materials; the rows come from ComplianceSeed.
        Add("alice", $"/parts/{m.OpenFastener}?tab=compliance", $"/parts/{m.GimbalBracket}?tab=compliance", "/compliance", $"/compliance?mode=ebom&number={m.TopAssembly}", "/compliance/declarations",
            $"/compliance/declarations/{ComplianceSeed.FastenerDeclaration}", $"/compliance/declarations/{ComplianceSeed.BracketDeclaration}", "/compliance/lists", "/compliance/lists/rohs",
            "/compliance/lists/rohs/versions/1", "/compliance/lists/conflict_minerals/versions/1", "/compliance/substances", "/compliance/substances/SUB-000001", "/compliance/materials");
        Add("ada", "/compliance/lists/rohs", "/compliance/substances", "/compliance/materials");
        Add("sup-alpha", "/portal", $"/portal/requests/{SourcingSeed.Request}");

        // Options and variants (8.4, ADR-0039): the seed gives the top assembly two options, rules and two released variants.
        VariantsSeed.EnsureAsync(fx).GetAwaiter().GetResult();
        Add("alice", "/options", $"/options/{m.TopAssembly}", $"/options/{m.TopAssembly}?tab=rules", $"/options/{m.TopAssembly}?tab=variants",
            $"/options/{m.TopAssembly}/compare?left={Uri.EscapeDataString(VariantsSeed.Left)}&right={Uri.EscapeDataString(VariantsSeed.Right)}", $"/parts/{m.TopAssembly}/variants",
            $"/parts/{m.TopAssembly}?tab=structure&variant={Uri.EscapeDataString(VariantsSeed.Left)}");
        // Records management (8.6, ADR-0041): schedules, holds, disposition, certificates and archives, and the hold chip on an object's page. The rows come from RecordsSeed.
        RecordsSeed.EnsureAsync(fx).GetAwaiter().GetResult();
        Add("rex", "/records", "/records/schedules", $"/records/schedules/{RecordsSeed.ScheduleVersion}", "/records/holds", $"/records/holds/{RecordsSeed.Hold}", "/records/disposition",
            $"/records/disposition/{RecordsSeed.Request}", "/records/archives", "/records/certificates", $"/documents/{m.SystemSpecification}", "/");
        // 8.1 quality: the three lists, the three forms, every tab of the three record pages and the quality tab of a part
        Add("alice", "/quality/ncrs", "/quality/capas", "/quality/deviations", "/quality/new/ncr", "/quality/new/capa", "/quality/new/deviation", $"/parts/{q.Part}?tab=quality");
        foreach (var tab in new[] { "overview", "disposition", "actions", "links", "review", "history", "activity", "comments" }) Add("alice", $"/quality/ncrs/{q.Ncr}?tab={tab}");
        foreach (var tab in new[] { "overview", "analysis", "actions", "verification", "links", "review", "history", "activity", "comments" }) Add("alice", $"/quality/capas/{q.Capa}?tab={tab}");
        foreach (var tab in new[] { "overview", "scope", "links", "review", "history", "activity", "comments" }) Add("alice", $"/quality/deviations/{q.Deviation}?tab={tab}");

        // Manufacturing process data (8.7, ADR-0042): plans, every tab, the shop floor view, tools and the administrator's work centers.
        ProcessSeed.EnsureAsync(fx).GetAwaiter().GetResult();
        foreach (var tab in new[] { "overview", "routing", "flow", "matrix", "review", "timeline", "activity", "comments" })
            Add("alice", $"/process/plans/{ProcessSeed.Plan}?tab={tab}");
        Add("alice", "/process/plans", $"/process/plans/{ProcessSeed.Plan}/shop-floor", "/process/tools", $"/process/tools/{ProcessSeed.Tool}", $"/process/tools/{ProcessSeed.Tool}?tab=calibration", $"/process/tools/{ProcessSeed.Tool}?tab=used");
        Add("ada", "/process/work-centers");

        Add("ada", "/admin", "/admin/attributes", "/admin/classification", "/admin/config", "/admin/history", "/admin/integration/deliveries", "/admin/integration/outbox",
            "/admin/integration/profiles", "/admin/integration/sinks", "/admin/integration/tdp-categories", "/admin/lifecycles", "/admin/lists", "/admin/migration",
            "/admin/numbering", "/admin/reasons", "/admin/supply-types", "/admin/workflow-templates", "/audit", "/access", "/parts", "/");
        return t;
    }

    /// <summary>The identifiers the seeding agent recorded for the extras (baselines, MBOMs).</summary>
    private sealed record DemoExtrasView(string DesignBaseline, string ReleaseBaseline, string ReleasedMbom, string StaleMbom);

    private async Task AuditAsync(string theme, int width, string testName)
    {
        await using var session = new BrowserSession(fx, testName);
        await session.RunAsync(async () =>
        {
            await SourcingSeed.EnsureAsync(fx);
            await ComplianceSeed.EnsureAsync(fx);
            var e = fx.Manifest.Extras ?? throw new InvalidOperationException("The demo extras are missing.");
            var extras = new DemoExtrasView(e.DesignBaseline, e.ReleaseBaseline, e.ReleasedMbom, e.StaleMbom);
            var axe = await File.ReadAllTextAsync(AxePath());
            var findings = new List<string>();
            var audited = 0;
            var users = new Dictionary<string, UserPage>();
            var originalTheme = new Dictionary<string, string>();
            try
            {
                foreach (var name in new[] { "alice", "ada", "sup-alpha", "rex" })
                {
                    var up = await session.SignInAsync(name, 1280, 900);
                    originalTheme[name] = await up.Page.EvaluateAsync<string>("document.documentElement.getAttribute('data-theme')");
                    await ChooseThemeAsync(up.Page, name, theme);
                    await up.Page.SetViewportSizeAsync(width, width == 375 ? 812 : 900);
                    users[name] = up;
                }

                var quality = await QualityRecords.EnsureAsync(fx, session, users["alice"]);
                foreach (var target in Targets(extras, quality))
                {
                    var up = users[target.User];
                    audited += await AuditPageAsync(up.Page, fx.BaseUrl + target.Path, $"{theme}/{width}px {target.User} {target.Path}", axe, findings, expectBanners: !target.Path.StartsWith("/not-found", StringComparison.Ordinal));
                }

                // Pages whose ids are only known at run time: the first open task and the first workflow, and the file viewer.
                var alice = users["alice"];
                foreach (var list in new[] { "/tasks", "/workflows" })
                {
                    await alice.Page.GotoAsync(fx.BaseUrl + list);
                    await Expect(alice.Page.Locator("h1")).ToBeVisibleAsync();
                    var link = alice.Page.Locator($"main a[href^='{list}/']").First;
                    if (await link.CountAsync() == 0) continue;
                    var href = await link.GetAttributeAsync("href");
                    audited += await AuditPageAsync(alice.Page, fx.BaseUrl + href, $"{theme}/{width}px alice {href}", axe, findings, expectBanners: true);
                }

                audited += await AuditOverlaysAsync(users["alice"].Page, users["ada"].Page, theme, width, axe, findings);

                var png = await CreateImageDocumentAsync();
                audited += await AuditViewerAsync(alice.Page, png, $"{theme}/{width}px alice viewer", axe, findings);
            }
            finally
            {
                foreach (var (name, up) in users)
                {
                    try
                    {
                        await up.Page.SetViewportSizeAsync(1280, 900);
                        await ChooseThemeAsync(up.Page, name, originalTheme[name]);
                    }
                    catch (PlaywrightException) { /* best effort: the next test chooses its own theme */ }
                }
            }

            Assert.True(audited > 100, $"Only {audited} page states were audited; the page list has shrunk.");
            Assert.True(findings.Count == 0, $"{findings.Count} accessibility violation(s) in {audited} page states:\n" + string.Join("\n", findings));
        });
    }

    private static async Task ChooseThemeAsync(IPage page, string user, string theme)
    {
        // A supplier user has no Home page (it needs a PLM role): "/" answers with the static access-denied page, which has no circuit, so the menu there never
        // opens. Go to the page the user lands on: the portal for a supplier, Home for everyone else.
        var origin = page.Url.Split('/', 4).Take(3).Aggregate((a, b) => a + "/" + b);
        await page.GotoAsync(origin + (user.StartsWith("sup-", StringComparison.Ordinal) ? "/portal" : "/"));
        await Expect(page.Locator("h1").First).ToBeVisibleAsync();
        await page.Locator("#user-menu-button").ClickAsync();
        try { await page.Locator($"[data-theme-choice={theme}]").ClickAsync(new LocatorClickOptions { Timeout = 8000 }); }
        catch (TimeoutException)
        {
            var state = await page.EvaluateAsync<string>("() => JSON.stringify({ url: location.href, expanded: document.querySelector('#user-menu-button')?.getAttribute('aria-expanded'), choices: [...document.querySelectorAll('[data-theme-choice]')].map(e => e.getAttribute('data-theme-choice')), panel: !!document.querySelector('#user-menu-panel') })");
            throw new InvalidOperationException($"{user}: the theme choice '{theme}' did not appear: {state}");
        }
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", theme);
    }

    /// <summary>Loads the page, waits until it has settled, checks the banners and runs axe; also in every other view of a view switch. Returns the number of states audited.</summary>
    private static async Task<int> AuditPageAsync(IPage page, string url, string label, string axe, List<string> findings, bool expectBanners)
    {
        await page.GotoAsync(url);
        await Expect(page.Locator("h1").First).ToBeVisibleAsync();
        await SettleAsync(page);
        if (expectBanners) await BrowserSession.AssertBannersAsync(page);
        var count = 1;
        await RunAxeAsync(page, label, axe, findings);

        var alternatives = page.Locator(".view-switch button[aria-pressed=false]");
        var n = await alternatives.CountAsync();
        for (var i = 0; i < n; i++)
        {
            var button = page.Locator(".view-switch button[aria-pressed=false]").Nth(0);
            var view = await button.GetAttributeAsync("data-view");
            await button.ClickAsync();
            await Expect(page.Locator($".view-switch button[data-view={view}]").First).ToHaveAttributeAsync("aria-pressed", "true");
            await SettleAsync(page);
            await RunAxeAsync(page, label + " [view " + view + "]", axe, findings);
            count++;
        }
        return count;
    }

    /// <summary>Overlays the page-by-page walk never opens: the command palette with results, the user menu, the notification bell, and a confirmation dialog.</summary>
    private async Task<int> AuditOverlaysAsync(IPage alice, IPage ada, string theme, int width, string axe, List<string> findings)
    {
        var count = 0;
        var label = $"{theme}/{width}px";
        await alice.GotoAsync(fx.BaseUrl + "/parts");
        await Expect(alice.Locator("h1")).ToBeVisibleAsync();

        var userMenu = alice.Locator("#user-menu-button");
        await userMenu.ClickAsync();
        await Expect(alice.Locator("#user-menu-panel")).ToBeVisibleAsync();
        await RunAxeAsync(alice, label + " alice user menu open", axe, findings);
        count++;
        await alice.Keyboard.PressAsync("Escape");
        await Expect(alice.Locator("#user-menu-panel")).ToHaveCountAsync(0);

        await alice.Locator("#bell-button").ClickAsync();
        await Expect(alice.Locator("#bell-panel")).ToBeVisibleAsync();
        await RunAxeAsync(alice, label + " alice notification bell open", axe, findings);
        count++;
        await alice.Keyboard.PressAsync("Escape");

        // The palette: opened by its button (the keyboard shortcut is covered by UxTests), with a query so that result rows are on screen.
        await alice.Locator("[data-action=open-palette]").ClickAsync();
        var dialog = alice.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Command palette" });
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Combobox).FillAsync(fx.Manifest.GimbalBracket);
        await Expect(dialog.GetByRole(AriaRole.Option).First).ToBeVisibleAsync();
        await RunAxeAsync(alice, label + " alice command palette with results", axe, findings);
        count++;
        await alice.Keyboard.PressAsync("Escape");
        await Expect(dialog).ToHaveCountAsync(0);

        // A confirmation dialog (shared by every destructive action).
        ILocator? retire = null;
        foreach (var path in new[] { "/admin/reasons", "/admin/lists", "/admin/supply-types" })
        {
            await ada.GotoAsync(fx.BaseUrl + path);
            await Expect(ada.Locator("h1")).ToBeVisibleAsync();
            await ada.WaitForTimeoutAsync(500);
            var candidate = ada.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Retire" }).First;
            if (await candidate.CountAsync() > 0) { retire = candidate; break; }
        }
        Assert.NotNull(retire);
        await retire!.ClickAsync();
        await Expect(ada.GetByRole(AriaRole.Dialog)).ToBeVisibleAsync();
        await RunAxeAsync(ada, label + " ada confirm dialog open", axe, findings);
        count++;
        await ada.Keyboard.PressAsync("Escape");
        await Expect(ada.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
        return count;
    }

    private async Task<int> AuditViewerAsync(IPage page, string documentNumber, string label, string axe, List<string> findings)
    {
        await page.GotoAsync(fx.BaseUrl + "/documents/" + documentNumber);
        var view = page.Locator("a[data-action=view]").First;
        await Expect(view).ToBeVisibleAsync();
        await view.ClickAsync();
        await Expect(page.Locator("body[data-viewer-state=ready]")).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 90_000 });
        await BrowserSession.AssertBannersAsync(page);
        await RunAxeAsync(page, label, axe, findings);
        return 1;
    }

    private static async Task SettleAsync(IPage page)
    {
        // Blazor renders in the circuit: loading skeletons disappear when the data is in; visuals attach their behaviour afterwards.
        try { await Expect(page.Locator(".skeleton")).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 }); }
        catch (PlaywrightException) { /* a page that keeps a skeleton is reported by axe or by the banner check */ }
        if (await page.Locator("svg.vis-svg").CountAsync() > 0)
        {
            try { await Expect(page.Locator("[data-vis-content]").First).ToHaveAttributeAsync("transform", new System.Text.RegularExpressions.Regex("translate"), new LocatorAssertionsToHaveAttributeOptions { Timeout = 10_000 }); }
            catch (PlaywrightException) { /* table-first layouts have no fitted picture */ }
        }
        await page.WaitForTimeoutAsync(250);
    }

    internal static async Task RunAxeAsync(IPage page, string label, string axe, List<string> findings)
    {
        // Evaluated through the protocol: injecting a <script> element would be (rightly) refused by the strict CSP.
        await page.EvaluateAsync(axe);
        var tags = JsonSerializer.Serialize(Tags);
        var json = await page.EvaluateAsync<JsonElement>($$"""
            async () => {
              const r = await axe.run(document, { runOnly: { type: 'tag', values: {{tags}} }, resultTypes: ['violations'] });
              return r.violations.map(v => ({ id: v.id, impact: v.impact, help: v.help, nodes: v.nodes.slice(0, 4).map(n => ({ target: n.target.join(' '), html: n.html.slice(0, 160), why: (n.failureSummary || '').split('\n').slice(0, 3).join(' ') })) }));
            }
            """);
        foreach (var v in json.EnumerateArray())
        {
            var sb = new StringBuilder();
            sb.Append("  [").Append(label).Append("] ").Append(v.GetProperty("id").GetString()).Append(" (").Append(v.GetProperty("impact").GetString()).Append("): ").Append(v.GetProperty("help").GetString());
            foreach (var n in v.GetProperty("nodes").EnumerateArray())
                sb.Append("\n      ").Append(n.GetProperty("target").GetString()).Append(" :: ").Append(n.GetProperty("html").GetString()).Append(" :: ").Append(n.GetProperty("why").GetString());
            findings.Add(sb.ToString());
        }
    }

    private async Task<string> CreateImageDocumentAsync()
    {
        var alice = RealmUsers.Load()["alice"];
        var docs = new DocumentService(fx.AppDb, new PostgresAuditLog(fx.AppDb, TimeProvider.System), TimeProvider.System, new ObjectVault(fx.Vault));
        var markings = Keel.Core.Markings.Create(Keel.Core.ExportJurisdiction.Itar, Keel.Core.DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"],
            controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;
        var created = await docs.CreateAsync(alice, new CreateDocumentCommand("E2E accessibility picture", "Drawing", markings, "Synthetic image for the accessibility audit"),
            new FileUpload("a11y.png", "application/octet-stream", new MemoryStream(ViewerSamples.Png())), null);
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        return created.Value!.Number;
    }

    private static string AxePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "Keel.E2E", "vendor", "axe-core", "axe.min.js");
    }
}
