using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Keel.E2E;

/// <summary>
/// Writes the screenshots of the user manual (docs/manual/images) from the real app and the synthetic demo data of the E2E fixture. It is not a test of behaviour: it
/// runs only with <c>KEEL_E2E=1</c> and <c>KEEL_MANUAL_SCREENSHOTS=1</c> (otherwise it returns at once), at a fixed 1440x900 viewport in the light theme, plus a few
/// dark, high-contrast, compact and 375 px examples. A shot that cannot be taken is logged to <c>TestResults/e2e/manual-shots.log</c> and the run goes on; the manual
/// lists what is missing. Run: <c>KEEL_E2E=1 KEEL_MANUAL_SCREENSHOTS=1 dotnet test --project tests/Keel.E2E --filter-class "*ManualScreenshotsTests"</c>.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class ManualScreenshotsTests(E2EFixture fx)
{
    private readonly StringBuilder _log = new();
    private int _taken;
    private string _dir = "";

    private static bool On => Environment.GetEnvironmentVariable("KEEL_MANUAL_SCREENSHOTS") == "1";

    private async Task ShotAsync(IPage page, string name)
    {
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForTimeoutAsync(700);
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(_dir, name + ".png") });
        _taken++;
    }

    /// <summary>Opens a page as a user, optionally acts on it, then takes the shot. Any failure is logged and skipped.</summary>
    private async Task GoAsync(BrowserSession session, UserPage up, string path, string name, Func<IPage, Task>? act = null)
    {
        try
        {
            await session.GotoAsync(up, path);
            if (act is not null) await act(up.Page);
            await ShotAsync(up.Page, name);
        }
        catch (Exception e)
        {
            _log.AppendLine($"FAILED {name} ({path}): {e.Message.Split('\n')[0]}");
        }
    }

    private async Task TryAsync(string name, Func<Task> body)
    {
        try { await body(); }
        catch (Exception e) { _log.AppendLine($"FAILED {name}: {e.Message.Split('\n')[0]}"); }
    }

    private static async Task ClickIfAsync(IPage page, string name, bool exact = true)
    {
        var button = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = name, Exact = exact });
        if (await button.CountAsync() > 0) { await button.First.ClickAsync(); await page.WaitForTimeoutAsync(600); }
    }

    private static async Task ExpandAsync(IPage p)
    {
        await ClickIfAsync(p, "Expand all");
        await p.WaitForTimeoutAsync(800);
        await p.EvaluateAsync("() => { const g = document.querySelector('.bom-grid'); if (g) g.scrollIntoView({ block: 'start' }); }");
        await p.WaitForTimeoutAsync(400);
    }

    private static async Task<string?> FirstHrefAsync(IPage page, string selector)
    {
        var link = page.Locator(selector).First;
        return await link.CountAsync() == 0 ? null : await link.GetAttributeAsync("href");
    }

    private static async Task ThemeAsync(IPage page, string theme)
    {
        await page.Locator("#user-menu-button").ClickAsync();
        await page.Locator($"[data-theme-choice={theme}]").ClickAsync();
        await page.Keyboard.PressAsync("Escape");
        await page.WaitForTimeoutAsync(1200);
    }

    [E2EFact(Timeout = 1_800_000)]
    public async Task The_manual_screenshots_are_written_on_request()
    {
        if (!On) return;
        _dir = Path.Combine(fx.RepoRoot, "docs", "manual", "images");
        Directory.CreateDirectory(_dir);
        var m = fx.Manifest;
        await using var session = new BrowserSession(fx, nameof(The_manual_screenshots_are_written_on_request));
        try
        {
            await session.RunAsync(async () =>
            {
                // ---- the sign-in screen (no session): the identity provider's own page
                await TryAsync("signin", async () =>
                {
                    var ctx = await fx.Browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1440, Height = 900 } });
                    var p = await ctx.NewPageAsync();
                    await p.GotoAsync(fx.BaseUrl + "/");
                    await p.WaitForSelectorAsync("#password");
                    await ShotAsync(p, "signin-login-page");
                    await ctx.CloseAsync();
                });

                // Logins that must be stale by the time they sign (step-up) come first.
                var ada = await session.SignInAsync("ada", 1440, 900);
                var bob = await session.SignInAsync("bob", 1440, 900);
                var alice = await session.SignInAsync("alice", 1440, 900);
                var ap = alice.Page;

                await SourcingSeed.EnsureAsync(fx);
                await ComplianceSeed.EnsureAsync(fx);
                await RecordsSeed.EnsureAsync(fx);
                await ProcessSeed.EnsureAsync(fx);
                await VariantsSeed.EnsureAsync(fx);
                var proposal = await CadE2ESeed.EnsureAsync(fx);
                QualityData? q = null;
                await TryAsync("quality-data", async () => q = await QualityRecords.EnsureAsync(fx, session, alice));
                ProgramData? prog = null;
                await TryAsync("program-data", async () => prog = await ProgramRecords.EnsureAsync(fx, session, alice));

                // ---- navigation and shell
                await GoAsync(session, alice, "/", "shell-home");
                await GoAsync(session, alice, "/parts", "shell-rail-groups", async p =>
                {
                    foreach (var g in new[] { "Quality", "Sourcing", "Output" })
                    {
                        var header = p.Locator($".rail-group[aria-label='{g}'] .rail-group-header");
                        if (await header.CountAsync() > 0 && await header.GetAttributeAsync("aria-expanded") == "false") await header.ClickAsync();
                    }
                });
                await GoAsync(session, alice, "/", "shell-user-menu", async p => { await p.Locator("#user-menu-button").ClickAsync(); await p.WaitForTimeoutAsync(400); });
                await ap.Keyboard.PressAsync("Escape");
                await GoAsync(session, alice, "/", "shell-rail-collapsed", async p => await p.Locator("[data-action=collapse-nav]").ClickAsync());
                await TryAsync("expand-rail", async () => { await ap.Locator("[data-action=collapse-nav]").ClickAsync(); await ap.WaitForTimeoutAsync(400); });
                await GoAsync(session, alice, "/", "shell-command-palette", async p =>
                {
                    await p.Keyboard.PressAsync("Control+k");
                    await p.Keyboard.TypeAsync("bracket");
                    await p.WaitForTimeoutAsync(1200);
                });
                await ap.Keyboard.PressAsync("Escape");
                await GoAsync(session, alice, "/search?q=bracket", "shell-search-results");
                await GoAsync(session, alice, "/parts/" + m.GimbalBracket, "shell-theme-dark", async p => await ThemeAsync(p, "dark"));
                await TryAsync("hc", async () => { await ThemeAsync(ap, "high-contrast"); await ShotAsync(ap, "shell-theme-high-contrast"); });
                await TryAsync("light", async () => await ThemeAsync(ap, "light"));
                await GoAsync(session, alice, "/parts", "shell-density-compact", async p =>
                {
                    await p.Locator("#user-menu-button").ClickAsync();
                    await p.Locator("[data-density-choice=compact]").ClickAsync();
                    await p.Keyboard.PressAsync("Escape");
                    await p.WaitForTimeoutAsync(800);
                });
                await TryAsync("comfortable", async () =>
                {
                    await ap.Locator("#user-menu-button").ClickAsync();
                    await ap.Locator("[data-density-choice=comfortable]").ClickAsync();
                    await ap.Keyboard.PressAsync("Escape");
                });
                await GoAsync(session, alice, "/profile", "access-my-access");
                await GoAsync(session, alice, "/access", "access-check");
                await GoAsync(session, alice, "/tools/style-guide", "shell-style-guide");

                // ---- parts and BOMs
                await GoAsync(session, alice, "/parts", "parts-list");
                await GoAsync(session, alice, "/parts/new", "parts-new");
                await GoAsync(session, alice, "/parts/" + m.GimbalBracket, "parts-overview");
                foreach (var (tab, name) in new[] { ("structure", "structure-bracket"), ("documents", "documents-tab"), ("changes", "changes-tab"), ("review", "review-tab"), ("classification", "classification-tab"), ("history", "history-tab"), ("timeline", "timeline-tab"), ("security", "security-tab"), ("sourcing", "sourcing-tab"), ("quality", "quality-tab"), ("compliance", "compliance-tab"), ("trace", "trace-tab"), ("comments", "comments-tab"), ("activity", "activity-tab") })
                    await GoAsync(session, alice, $"/parts/{m.GimbalBracket}?tab={tab}", "parts-" + name);
                await GoAsync(session, alice, $"/parts/{m.TopAssembly}?tab=structure", "bom-structure-expanded", async p => await ExpandAsync(p));
                await GoAsync(session, alice, $"/parts/{m.TopAssembly}?tab=structure", "bom-structure-treemap", async p => { await p.GetByLabel("Treemap").CheckAsync(); await p.WaitForTimeoutAsync(1500); await p.Mouse.MoveAsync(700, 600); await p.Mouse.WheelAsync(0, 450); await p.WaitForTimeoutAsync(500); });
                await GoAsync(session, alice, $"/parts/{m.GimbalAssembly}/where-used", "bom-where-used");
                await GoAsync(session, alice, $"/parts/{m.PayloadAssembly}?tab=structure", "bom-restricted-line", async p => await ExpandAsync(p));
                await GoAsync(session, alice, $"/parts/{m.GimbalAssembly}/compare", "bom-compare");
                await GoAsync(session, alice, $"/parts/{m.PartnerAssembly}/compare-units", "bom-compare-units");
                await GoAsync(session, alice, $"/parts/{m.PartnerAssembly}?tab=structure", "bom-alternates-effectivity", async p => await ExpandAsync(p));
                await GoAsync(session, alice, "/parts/classification", "parts-classification-search");
                await GoAsync(session, alice, "/options", "variants-list");
                await GoAsync(session, alice, $"/options/{m.TopAssembly}", "variants-model");
                await GoAsync(session, alice, $"/options/{m.TopAssembly}?tab=rules", "variants-rules");
                await GoAsync(session, alice, $"/options/{m.TopAssembly}/compare?left={Uri.EscapeDataString(VariantsSeed.Left)}&right={Uri.EscapeDataString(VariantsSeed.Right)}", "variants-compare");
                await GoAsync(session, alice, $"/parts/{m.TopAssembly}/variants", "variants-part-variants");
                await GoAsync(session, alice, $"/parts/{m.TopAssembly}?tab=structure&variant={Uri.EscapeDataString(VariantsSeed.Left)}", "variants-150-structure", async p => await ExpandAsync(p));
                await GoAsync(session, alice, "/mboms", "mbom-list");
                await GoAsync(session, alice, "/mboms/" + (m.Extras?.ReleasedMbom ?? "MB-000001"), "mbom-released");
                await GoAsync(session, alice, "/mboms/" + (m.Extras?.StaleMbom ?? "MB-000002") + "?tab=reconciliation", "mbom-reconciliation");
                await GoAsync(session, alice, "/mboms/generate?part=" + m.TopAssembly, "mbom-generate");
                await GoAsync(session, alice, "/mboms/" + (m.Extras?.ReleasedMbom ?? "MB-000001") + "/side-by-side", "mbom-side-by-side");

                // ---- documents, vault, CAD
                await GoAsync(session, alice, "/documents", "documents-list");
                await GoAsync(session, alice, "/documents/new", "documents-new");
                await GoAsync(session, alice, "/documents/" + m.BracketDrawing, "documents-overview");
                foreach (var (tab, name) in new[] { ("versions", "versions"), ("usedby", "used-by"), ("cad", "cad"), ("history", "history"), ("review", "review"), ("security", "security") })
                    await GoAsync(session, alice, $"/documents/{m.BracketDrawing}?tab={tab}", "documents-" + name);
                if (m.Models is { } models)
                {
                    await GoAsync(session, alice, "/documents/" + models.BracketModel, "documents-model-overview");
                    await GoAsync(session, alice, $"/documents/{models.BracketModel}?tab=versions", "documents-model-versions");
                    await TryAsync("model-compare", async () =>
                    {
                        await session.GotoAsync(alice, $"/documents/{models.BracketModel}?tab=versions");
                        await ap.Locator("[data-testid='compare-go']").ClickAsync();
                        await ap.Locator("[data-testid='compare-result']").WaitForAsync();
                        await ap.Locator("[data-testid='compare-result']").ScrollIntoViewIfNeededAsync();
                        await ShotAsync(ap, "documents-model-compare");
                        await ap.Locator("[data-testid='open-visual-compare']").ClickAsync();
                        await ap.Locator("body[data-viewer-state=ready]").WaitForAsync(new LocatorWaitForOptions { Timeout = 90_000 });
                        await ShotAsync(ap, "documents-model-visual-compare");
                    });
                }
                await GoAsync(session, alice, "/cad/bom-proposals", "cad-proposals-list");
                await GoAsync(session, alice, "/cad/bom-proposals/" + proposal, "cad-proposal-review");

                // ---- baselines
                await GoAsync(session, alice, "/baselines", "baselines-list");
                await GoAsync(session, alice, "/baselines/" + (m.Extras?.DesignBaseline ?? "BL-000001"), "baselines-detail");
                await GoAsync(session, alice, "/baselines/" + (m.Extras?.DesignBaseline ?? "BL-000001") + "?tab=items", "baselines-items");
                await GoAsync(session, alice, "/baselines/compare", "baselines-compare");
                await GoAsync(session, alice, "/baselines/new", "baselines-new");

                // ---- change management
                await GoAsync(session, alice, "/changes", "changes-list");
                await GoAsync(session, alice, "/changes/" + m.EcnDraftInWork, "changes-ecn-overview");
                await GoAsync(session, alice, $"/changes/{m.EcnDraftInWork}?tab=impact", "changes-impact-analysis", async p =>
                {
                    await p.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Run impact analysis" }).ClickAsync();
                    await p.Locator("section[aria-labelledby='impact-heading']").GetByText("place").First.WaitForAsync();
                    await p.WaitForTimeoutAsync(800);
                });
                await GoAsync(session, alice, $"/changes/{m.EcnDraftInWork}?tab=impactmap", "changes-impact-map");
                await GoAsync(session, alice, $"/changes/{m.EcnDraftInWork}?tab=tasks", "changes-tasks");
                await GoAsync(session, alice, $"/changes/{m.EcnUnderReview}?tab=review", "changes-review-workflow-map");
                await GoAsync(session, alice, "/changes/" + m.EcrAwaitingCcb, "changes-ecr");
                await GoAsync(session, alice, "/changes/" + m.ProblemReportOpen, "changes-problem-report");
                await GoAsync(session, alice, "/changes/new/ecr", "changes-new-ecr");
                await GoAsync(session, alice, $"/parts/{m.PayloadAssembly}?tab=review", "workflow-submit-dialog", async p =>
                {
                    await ClickIfAsync(p, "Submit for review");
                    await p.WaitForTimeoutAsync(500);
                });
                await GoAsync(session, alice, "/tasks", "workflow-my-tasks");
                await GoAsync(session, ada, "/workflows", "workflow-list");
                await TryAsync("workflow-detail", async () =>
                {
                    var href = await FirstHrefAsync(ada.Page, "a[href^='workflows/']");
                    if (href is not null) await GoAsync(session, ada, "/" + href.TrimStart('/'), "workflow-detail");
                });

                // ---- tasks and e-signatures (ada: stale login, so the step-up screen appears)
                await TryAsync("ada-task", async () =>
                {
                    await session.GotoAsync(ada, "/tasks");
                    await ShotAsync(ada.Page, "workflow-ada-tasks");
                    var href = await FirstHrefAsync(ada.Page, "a[href^='tasks/']");
                    if (href is null) { _log.AppendLine("FAILED workflow-sign-task: ada has no open task"); return; }
                    await session.GotoAsync(ada, "/" + href.TrimStart('/'));
                    await BrowserSession.WaitUntilLoginIsStaleAsync(ada);
                    await ada.Page.ReloadAsync();
                    await ada.Page.WaitForTimeoutAsync(1500);
                    await ShotAsync(ada.Page, "workflow-sign-task-stale-login");
                    var sign = ada.Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Reviewed" });
                    if (await sign.CountAsync() > 0)
                    {
                        await sign.First.ClickAsync();
                        await ada.Page.WaitForURLAsync(new Regex(@"/realms/keel/"));
                        await ada.Page.WaitForSelectorAsync("#password");
                        await ShotAsync(ada.Page, "workflow-step-up-keycloak");
                    }
                });
                await GoAsync(session, bob, "/tasks", "workflow-bob-tasks");

                // ---- export control
                var lena = await session.SignInAsync("lena", 1440, 900);
                await GoAsync(session, lena, "/parts", "markings-lena-parts");
                await GoAsync(session, lena, "/parts/" + m.GimbalBracket, "markings-lena-not-found");
                await GoAsync(session, lena, "/parts/" + m.SparesKit + "?tab=structure", "markings-lena-redacted-lines", async p => await ExpandAsync(p));
                var farid = await session.SignInAsync("farid", 1440, 900);
                await GoAsync(session, farid, "/parts/" + m.PartnerPlate, "markings-farid-plate");
                await GoAsync(session, alice, "/parts/" + m.GimbalBracket + "?tab=security", "markings-security-tab");
                await GoAsync(session, alice, "/access", "markings-access-check", async p =>
                {
                    await p.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Check access" }).ClickAsync();
                    await p.WaitForTimeoutAsync(800);
                });

                // ---- quality
                await GoAsync(session, alice, "/quality/ncrs", "quality-ncr-list");
                await GoAsync(session, alice, "/quality/capas", "quality-capa-list");
                await GoAsync(session, alice, "/quality/deviations", "quality-deviation-list");
                await GoAsync(session, alice, "/quality/new/ncr", "quality-new-ncr");
                if (q is not null)
                {
                    await GoAsync(session, alice, "/quality/ncrs/" + q.Ncr, "quality-ncr-overview");
                    await GoAsync(session, alice, $"/quality/ncrs/{q.Ncr}?tab=disposition", "quality-ncr-disposition");
                    await GoAsync(session, alice, $"/quality/ncrs/{q.Ncr}?tab=actions", "quality-ncr-containment");
                    await GoAsync(session, alice, "/quality/capas/" + q.Capa, "quality-capa-overview");
                    await GoAsync(session, alice, $"/quality/capas/{q.Capa}?tab=analysis", "quality-capa-analysis");
                    await GoAsync(session, alice, $"/quality/capas/{q.Capa}?tab=verification", "quality-capa-verification");
                    await GoAsync(session, alice, "/quality/deviations/" + q.Deviation, "quality-deviation-overview");
                    await GoAsync(session, alice, $"/quality/deviations/{q.Deviation}?tab=scope", "quality-deviation-scope");
                    await GoAsync(session, alice, $"/parts/{q.Part}?tab=quality", "quality-part-tab");
                }

                // ---- sourcing and the portal
                await GoAsync(session, alice, "/suppliers", "sourcing-suppliers");
                await GoAsync(session, alice, "/suppliers/new", "sourcing-new-supplier");
                await GoAsync(session, alice, "/suppliers/" + SourcingSeed.SupplierA, "sourcing-supplier-overview");
                await GoAsync(session, alice, $"/suppliers/{SourcingSeed.SupplierA}?tab=certifications", "sourcing-supplier-certifications");
                await GoAsync(session, alice, $"/suppliers/{SourcingSeed.SupplierA}?tab=parts", "sourcing-supplier-parts");
                await GoAsync(session, alice, "/requests", "sourcing-requests");
                await GoAsync(session, alice, "/requests/new", "sourcing-new-request");
                await GoAsync(session, alice, "/requests/" + SourcingSeed.Request, "sourcing-request-detail");
                await GoAsync(session, alice, "/sourcing/entries/" + SourcingSeed.Entry, "sourcing-entry");
                await GoAsync(session, alice, $"/sourcing/where-sourced?supplier={SourcingSeed.SupplierA}", "sourcing-where-sourced");
                await GoAsync(session, alice, "/sourcing/coverage", "sourcing-coverage");
                await GoAsync(session, alice, $"/parts/{m.OpenFastener}?tab=sourcing", "sourcing-part-tab");
                var alpha = await session.SignInAsync("sup-alpha", 1440, 900);
                await GoAsync(session, alpha, "/portal", "portal-my-requests");
                await GoAsync(session, alpha, "/portal/requests/" + SourcingSeed.Request, "portal-request");

                // ---- manufacturing process data
                await GoAsync(session, alice, "/process/plans", "process-plans");
                await GoAsync(session, alice, "/process/plans/" + ProcessSeed.Plan, "process-plan-overview");
                await GoAsync(session, alice, $"/process/plans/{ProcessSeed.Plan}?tab=routing", "process-plan-routing");
                await GoAsync(session, alice, $"/process/plans/{ProcessSeed.Plan}?tab=flow", "process-plan-flow");
                await GoAsync(session, alice, $"/process/plans/{ProcessSeed.Plan}/shop-floor", "process-shop-floor");
                await GoAsync(session, alice, "/process/tools", "process-tools");
                await GoAsync(session, alice, "/process/tools/" + ProcessSeed.Tool, "process-tool");
                await GoAsync(session, alice, "/process/work-centers", "process-work-centers");

                // ---- requirements
                await GoAsync(session, alice, "/requirements", "requirements-list");
                await GoAsync(session, alice, "/requirements/new", "requirements-new");
                var spec = m.Extras?.RequirementSpec;
                if (spec is not null)
                {
                    await GoAsync(session, alice, "/requirements/" + spec, "requirements-spec-overview");
                    foreach (var tab in new[] { "requirements", "trace", "verification", "coverage" })
                        await GoAsync(session, alice, $"/requirements/{spec}?tab={tab}", "requirements-spec-" + tab);
                }

                // ---- compliance
                await GoAsync(session, alice, $"/compliance?mode=ebom&number={m.TopAssembly}", "compliance-rollup");
                await GoAsync(session, alice, "/compliance/declarations", "compliance-declarations");
                await GoAsync(session, alice, "/compliance/declarations/" + ComplianceSeed.FastenerDeclaration, "compliance-declaration");
                await GoAsync(session, alice, "/compliance/substances", "compliance-substances");
                await GoAsync(session, alice, "/compliance/materials", "compliance-materials");
                await GoAsync(session, alice, "/compliance/lists", "compliance-lists");
                await GoAsync(session, alice, "/compliance/lists/rohs", "compliance-list-rohs");
                await GoAsync(session, alice, $"/parts/{m.OpenFastener}?tab=compliance", "compliance-part-tab");

                // ---- records
                var rex = await session.SignInAsync("rex", 1440, 900);
                await GoAsync(session, rex, "/records", "records-home");
                await GoAsync(session, rex, "/records/schedules", "records-schedules");
                await GoAsync(session, rex, "/records/holds", "records-holds");
                await GoAsync(session, rex, "/records/holds/" + RecordsSeed.Hold, "records-hold-detail");
                await GoAsync(session, rex, "/records/disposition", "records-disposition");
                await GoAsync(session, rex, "/records/disposition/" + RecordsSeed.Request, "records-disposition-request");
                await GoAsync(session, rex, "/records/archives", "records-archives");
                await GoAsync(session, rex, "/records/certificates", "records-certificates");

                // ---- programs
                await GoAsync(session, alice, "/programs", "programs-list");
                await GoAsync(session, alice, "/programs/new", "programs-new");
                if (prog is not null)
                {
                    await GoAsync(session, alice, "/programs/" + prog.Code, "programs-dashboard");
                    foreach (var tab in new[] { "schedule", "timeline", "deliverables" })
                        await GoAsync(session, alice, $"/programs/{prog.Code}?tab={tab}", "programs-" + tab);
                    await GoAsync(session, alice, "/programs/gates/" + prog.Gate, "programs-gate");
                }

                // ---- output and bulk work
                await GoAsync(session, alice, "/output/reports", "output-reports", async p =>
                {
                    var run = p.Locator("[data-testid='report-run']");
                    if (await run.CountAsync() > 0) { await run.ClickAsync(); await p.WaitForTimeoutAsync(1200); await p.Mouse.MoveAsync(700, 600); await p.Mouse.WheelAsync(0, 350); await p.WaitForTimeoutAsync(400); }
                });
                await GoAsync(session, alice, "/output/packages", "output-packages");
                await GoAsync(session, alice, "/output/bom", "output-bom-excel");
                await GoAsync(session, alice, "/output/mass", "output-mass-change", async p =>
                {
                    await p.Locator("[data-testid='mass-numbers']").FillAsync($"{m.GimbalBracket}\n{m.EndCap}\nP-999999");
                    await p.Locator("[data-testid='mass-operation']").SelectOptionAsync("favorite");
                    await p.Locator("[data-testid='mass-preview']").ClickAsync();
                    await p.WaitForSelectorAsync("[data-testid='mass-result']");
                    await p.Locator("[data-testid='mass-result']").ScrollIntoViewIfNeededAsync();
                    await p.Mouse.MoveAsync(700, 600); await p.Mouse.WheelAsync(0, 250); await p.WaitForTimeoutAsync(400);
                });
                await GoAsync(session, alice, "/tdp", "tdp-history");

                // ---- notifications and activity
                await GoAsync(session, alice, "/notifications", "awareness-notifications");
                await GoAsync(session, alice, "/notifications/preferences", "awareness-preferences");
                await GoAsync(session, alice, "/watching", "awareness-watching");
                await GoAsync(session, alice, "/parts/" + m.GimbalBracket, "awareness-bell", async p =>
                {
                    var bell = p.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Notifications", Exact = false }).First;
                    if (await bell.CountAsync() > 0) await bell.ClickAsync();
                    await p.WaitForTimeoutAsync(500);
                });
                await ap.Keyboard.PressAsync("Escape");

                // ---- audit and integrity
                var audrey = await session.SignInAsync("audrey", 1440, 900);
                await GoAsync(session, audrey, "/audit", "audit-log");
                await GoAsync(session, audrey, "/audit", "audit-verify-chain", async p =>
                {
                    await ClickIfAsync(p, "Verify chain");
                    await p.WaitForTimeoutAsync(2500);
                });

                // ---- administration
                foreach (var (path, name) in new[]
                {
                    ("/admin", "admin-home"), ("/admin/attributes", "admin-attributes"), ("/admin/lifecycles", "admin-lifecycles"), ("/admin/lists", "admin-lists"),
                    ("/admin/numbering", "admin-numbering"), ("/admin/reasons", "admin-reasons"), ("/admin/classification", "admin-classification"),
                    ("/admin/workflow-templates", "admin-workflow-templates"), ("/admin/config", "admin-config-export-import"), ("/admin/history", "admin-history"),
                    ("/admin/supply-types", "admin-supply-types"), ("/admin/requirement-link-types", "admin-requirement-link-types"),
                    ("/admin/integration/sinks", "admin-integration-sinks"), ("/admin/integration/profiles", "admin-integration-profiles"),
                    ("/admin/integration/outbox", "admin-integration-outbox"), ("/admin/integration/deliveries", "admin-integration-deliveries"),
                    ("/admin/integration/tdp-categories", "admin-integration-tdp-categories"), ("/admin/migration", "admin-migration"),
                })
                    await GoAsync(session, ada, path, name);
                await GoAsync(session, ada, "/workflows", "admin-workflows");

                // ---- phone width (375 px)
                var phone = await session.SignInAsync("alice", 375, 812);
                await GoAsync(session, phone, "/", "mobile-home");
                await GoAsync(session, phone, "/parts/" + m.GimbalBracket, "mobile-part");
                await GoAsync(session, phone, "/parts", "mobile-menu", async p =>
                {
                    var menu = p.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Menu", Exact = false }).First;
                    if (await menu.CountAsync() > 0) { await menu.ClickAsync(); await p.WaitForTimeoutAsync(500); }
                });

                // ---- the idle session lock (a host with a short idle timeout)
                await TryAsync("session-lock", async () =>
                {
                    await fx.EnsureLockHostAsync();
                    var locked = await session.SignInAsync("audrey", 1440, 900, fx.LockHostUrl);
                    await session.GotoAsync(locked, "/documents/" + m.SystemSpecification);
                    await locked.Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Lock now" }).ClickAsync();
                    var dialog = locked.Page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Session locked" });
                    await dialog.WaitForAsync();
                    await ShotAsync(locked.Page, "shell-session-locked");
                });
            });
        }
        finally
        {
            _log.AppendLine($"Screenshots written: {_taken}");
            Directory.CreateDirectory(fx.ResultsDir);
            await File.WriteAllTextAsync(Path.Combine(fx.ResultsDir, "manual-shots.log"), _log.ToString());
            Console.Error.WriteLine("[manual] " + _log);
        }
    }
}
