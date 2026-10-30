using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Keel.E2E;

/// <summary>One signed-in browser identity: its own context (cookies, Keycloak session) and page, with console output captured.</summary>
public sealed class UserPage
{
    public required string User { get; init; }
    public required IBrowserContext Context { get; init; }
    public required IPage Page { get; init; }
    /// <summary>The host this identity is signed in to (the main host unless a test asked for another).</summary>
    public string? BaseUrl { get; init; }
    /// <summary>When the Keycloak login completed (the <c>auth_time</c> of this session, to within a second).</summary>
    public DateTimeOffset SignedInAt { get; set; }
    public List<string> ConsoleErrors { get; } = [];
    public List<string> PageErrors { get; } = [];
}

/// <summary>
/// Drives the real UI. Each test creates its own session: fresh browser contexts per user, tracing on, and on failure the traces and a screenshot
/// of every page land in <c>TestResults/e2e/&lt;test&gt;/</c>.
/// </summary>
public sealed partial class BrowserSession(E2EFixture fx, string testName) : IAsyncDisposable
{
    private readonly List<UserPage> _users = [];
    private bool _failed;

    public E2EFixture Fx => fx;

    /// <summary>Runs the test body; a failure saves the artifacts before it propagates.</summary>
    public async Task RunAsync(Func<Task> body)
    {
        try { await body(); }
        catch
        {
            _failed = true;
            throw;
        }
    }

    public async Task<UserPage> SignInAsync(string user, int? width = null, int? height = null, string? baseUrl = null)
    {
        if (baseUrl != fx.LockHostUrl) await fx.StopLockHostAsync();
        var context = await fx.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width ?? 1280, Height = height ?? 900 },
            AcceptDownloads = true,
        });
        context.SetDefaultTimeout(30_000);
        await context.Tracing.StartAsync(new TracingStartOptions { Screenshots = true, Snapshots = true, Sources = false });
        var page = await context.NewPageAsync();
        var host = baseUrl ?? fx.BaseUrl;
        var up = new UserPage { User = user, Context = context, Page = page, BaseUrl = host };
        page.Console += (_, m) => { if (m.Type == "error") up.ConsoleErrors.Add(m.Text); };
        page.PageError += (_, e) => up.PageErrors.Add(e);
        _users.Add(up);

        await page.GotoAsync(host + "/");
        await SubmitKeycloakAsync(page, user);
        await page.WaitForURLAsync(url => url.StartsWith(host, StringComparison.Ordinal));
        await Expect(page.Locator("nav[aria-label='Main']")).ToBeAttachedAsync();
        await Expect(page.Locator("main#main")).ToBeVisibleAsync();
        // The shell is rendered by the Blazor circuit, so the navigation in the page means the circuit is live (at phone width it is a drawer, not visible).
        up.SignedInAt = DateTimeOffset.UtcNow;
        return up;
    }

    /// <summary>Fills the Keycloak login form (also the re-authentication form of a step-up, which may or may not repeat the username).</summary>
    public static async Task SubmitKeycloakAsync(IPage page, string user)
    {
        await page.WaitForSelectorAsync("#password");
        var username = page.Locator("#username");
        if (await username.CountAsync() > 0 && await username.IsVisibleAsync() && await username.IsEditableAsync()) await username.FillAsync(user);
        await page.Locator("#password").FillAsync($"keel-dev-{user}");
        await page.Locator("#kc-login").ClickAsync();
    }

    public async Task GotoAsync(UserPage up, string path)
    {
        await up.Page.GotoAsync((up.BaseUrl ?? fx.BaseUrl) + path);
        await Expect(up.Page.Locator("h1")).ToBeVisibleAsync();
    }

    /// <summary>Types into a Blazor-bound field and leaves it, so the <c>change</c> event that carries the value to the server fires.</summary>
    public static async Task FillAsync(ILocator field, string value)
    {
        await field.FillAsync(value);
        await field.PressAsync("Tab");
    }

    public static async Task ClickAsync(IPage page, string name, bool exact = true) =>
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = name, Exact = exact }).ClickAsync();

    /// <summary>Opens the "More" menu of a part page (the step-by-step check-out and check-in controls); does nothing when it is already open.</summary>
    public static async Task OpenMoreAsync(IPage page)
    {
        var menu = page.Locator("details.more-menu").First;
        if (await menu.GetAttributeAsync("open") is null) await menu.Locator("summary").ClickAsync();
        await Expect(menu).ToHaveAttributeAsync("open", "");
    }

    /// <summary>Opens a tab of an object page (Overview, Structure, Documents, Changes, Review &amp; release, ...) and waits until it is selected.</summary>
    public static async Task OpenTabAsync(IPage page, string name)
    {
        var tab = page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = name });
        await tab.ClickAsync();
        await Expect(tab).ToHaveAttributeAsync("aria-selected", "true");
    }

    /// <summary>
    /// A link of the navigation rail, opening its group first when the group is closed (the rail groups are disclosure buttons and a closed group renders no links;
    /// the group of the current page and the first group are open by themselves). Pass the group's title as the rail shows it.
    /// </summary>
    public static async Task<ILocator> RailLinkAsync(IPage page, string group, string link)
    {
        var header = page.Locator($".rail-group[aria-label='{group}'] .rail-group-header");
        await Expect(page.Locator("nav#main-nav")).ToBeVisibleAsync();
        if (await header.CountAsync() > 0 && await header.GetAttributeAsync("aria-expanded") == "false")
        {
            await header.ClickAsync();
            await Expect(header).ToHaveAttributeAsync("aria-expanded", "true");
        }
        return page.GetByRole(AriaRole.Navigation, new PageGetByRoleOptions { Name = "Main" }).GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = link, Exact = true });
    }

    public static ILocator TopBanner(IPage page) => page.Locator("[data-position='top']");
    public static ILocator BottomBanner(IPage page) => page.Locator("[data-position='bottom']");

    /// <summary>The page's marking banners are present, non-empty and identical top and bottom.</summary>
    public static async Task AssertBannersAsync(IPage page)
    {
        await Expect(TopBanner(page)).ToBeVisibleAsync();
        await Expect(BottomBanner(page)).ToBeVisibleAsync();
        // A page that loads its data after the first render updates both banners together; read them again until they agree (the two reads are not atomic).
        string top = "", bottom = "";
        for (var attempt = 0; attempt < 40; attempt++)
        {
            top = (await TopBanner(page).InnerTextAsync()).Trim();
            bottom = (await BottomBanner(page).InnerTextAsync()).Trim();
            if (top == bottom && !string.IsNullOrWhiteSpace(top)) break;
            await Task.Delay(250);
        }
        Assert.False(string.IsNullOrWhiteSpace(top), "The top marking banner is empty.");
        Assert.Equal(top, bottom);
    }

    /// <summary>Waits until the login is older than the step-up window so the next signature must re-authenticate.</summary>
    public static async Task WaitUntilLoginIsStaleAsync(UserPage up)
    {
        var due = up.SignedInAt.AddSeconds(E2EFixture.StepUpSeconds + 2);
        var wait = due - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
    }

    /// <summary>
    /// Signs the open task at <paramref name="taskPath"/> with the given meaning. When <paramref name="expectStepUp"/> the login is first made
    /// stale and the Keycloak login screen must appear; the signer signs in again, lands back on the task and signs.
    /// </summary>
    public async Task SignTaskAsync(UserPage up, string taskPath, string meaning, bool expectStepUp, string? comment = null)
    {
        var page = up.Page;
        await GotoAsync(up, taskPath);
        if (comment is not null) await FillAsync(page.GetByLabel("Comment", new PageGetByLabelOptions { Exact = false }), comment);
        if (expectStepUp)
        {
            await WaitUntilLoginIsStaleAsync(up);
            await page.ReloadAsync();
            await Expect(page.GetByText("you will be asked to sign in again")).ToBeVisibleAsync();
            if (comment is not null) await FillAsync(page.GetByLabel("Comment", new PageGetByLabelOptions { Exact = false }), comment);
            await ClickAsync(page, meaning);
            await page.WaitForURLAsync(new Regex(@"/realms/keel/"));
            await SubmitKeycloakAsync(page, up.User);
            await page.WaitForURLAsync(url => url.StartsWith(fx.BaseUrl, StringComparison.Ordinal));
            await Expect(page.GetByText("You signed in again.")).ToBeVisibleAsync();
            if (comment is not null) await FillAsync(page.GetByLabel("Comment", new PageGetByLabelOptions { Exact = false }), comment);
        }
        await ClickAsync(page, meaning);
        // A fresh login signs straight away; a stale one must not have been sent to Keycloak a second time.
        await Expect(page.GetByRole(AriaRole.Status).Filter(new LocatorFilterOptions { HasTextRegex = SignedMessage() })).ToBeVisibleAsync();
    }

    /// <summary>The path of the first open task of the user whose name starts with <paramref name="taskName"/>.</summary>
    public async Task<string> OpenTaskPathAsync(UserPage up, string taskName, string objectKey)
    {
        await GotoAsync(up, "/tasks");
        var row = up.Page.Locator("tr", new PageLocatorOptions { HasText = objectKey }).Filter(new LocatorFilterOptions { Has = up.Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = taskName }) });
        await Expect(row.First).ToBeVisibleAsync();
        var href = await row.First.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = taskName }).GetAttributeAsync("href");
        return "/" + href!.TrimStart('/');
    }

    /// <summary>Chooses a reviewer and approver in the shared "Submit for review" dialog (parts, ECRs and ECNs use the same one).</summary>
    public static async Task ChooseParticipantsAsync(IPage page, Dictionary<string, string> slotToUser)
    {
        foreach (var (slotLabel, user) in slotToUser)
        {
            await FillAsync(page.GetByLabel($"Find {slotLabel}"), user);
            await ClickAsync(page, $"Search {slotLabel}");
            await ClickAsync(page, $"Add {user}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        var dir = Path.Combine(fx.ResultsDir, Sanitize(testName));
        foreach (var up in _users)
        {
            try
            {
                if (_failed)
                {
                    Directory.CreateDirectory(dir);
                    await up.Page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(dir, $"{up.User}.png"), FullPage = true });
                    await File.WriteAllTextAsync(Path.Combine(dir, $"{up.User}-console.txt"),
                        string.Join('\n', up.ConsoleErrors.Select(e => "console: " + e).Concat(up.PageErrors.Select(e => "pageerror: " + e))) + $"\nurl: {up.Page.Url}\n");
                    await up.Context.Tracing.StopAsync(new TracingStopOptions { Path = Path.Combine(dir, $"{up.User}-trace.zip") });
                }
                else await up.Context.Tracing.StopAsync();
            }
            catch (PlaywrightException) { /* the page or context is already gone */ }
            await up.Context.CloseAsync();
        }
    }

    private static string Sanitize(string name) => UnsafeChars().Replace(name, "_");

    [GeneratedRegex(@"[^A-Za-z0-9_.-]")]
    private static partial Regex UnsafeChars();

    [GeneratedRegex(@"^Signed\.")]
    private static partial Regex SignedMessage();
}

