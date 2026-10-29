using Keel.Core;

namespace Keel.Web.Services;

/// <summary>
/// An entry of the command palette that is not a search result: a place to go or a command to run. <see cref="Href"/> is set for navigation; commands
/// (<c>cmd:…</c> ids) are run by the palette component.
/// </summary>
public sealed record PaletteAction(string Id, string Label, string Group, string Icon, string? Href, string Keywords = "");

/// <summary>
/// The actions the palette offers, filtered by what the user may do (their roles). Like the navigation, filtering is a convenience: the pages and services
/// refuse the action when the role is missing. Admin-only places are not even listed for others.
/// </summary>
public static class PaletteActions
{
    public const string ToggleTheme = "cmd:toggle-theme";
    public const string LockNow = "cmd:lock-now";
    public const string SignOut = "cmd:sign-out";

    public static IReadOnlyList<PaletteAction> For(IReadOnlyCollection<string> roles, bool canLock)
    {
        var canCreate = roles.Contains(Roles.Author) || roles.Contains(Roles.Admin);
        var list = new List<PaletteAction>
        {
            new("go:home", "Go to Home", "Go to", "home", "", "dashboard start"),
            new("go:tasks", "Go to My tasks", "Go to", "tasks", "tasks", "inbox review sign approve"),
            new("go:parts", "Go to Parts", "Go to", "part", "parts", "components assemblies"),
            new("go:documents", "Go to Documents", "Go to", "document", "documents", "drawings specifications"),
            new("go:mboms", "Go to Manufacturing BOMs", "Go to", "mbom", "mboms", "mbom plant"),
            new("go:baselines", "Go to Baselines", "Go to", "baseline", "baselines", "configuration"),
            new("go:changes", "Go to Changes", "Go to", "change", "changes", "ecn ecr problem report"),
            new("go:suppliers", "Go to Suppliers", "Go to", "folder", "suppliers", "vendors manufacturers sources certifications approved"),
            new("go:access", "Go to Access check", "Go to", "access", "access", "permission can i see"),
            new("go:profile", "Go to My access", "Go to", "user", "profile", "profile roles clearance"),
        };
        if (roles.Contains(Roles.Auditor)) list.Add(new("go:audit", "Go to Audit log", "Go to", "audit", "audit", "events verify chain"));
        if (roles.Contains(Roles.Admin)) list.Add(new("go:admin", "Go to Administration", "Go to", "admin", "admin", "settings configuration"));
        if (canCreate)
        {
            list.Add(new("new:part", "New part", "Create", "plus", "parts/new", "create add"));
            list.Add(new("new:document", "New document", "Create", "plus", "documents/new", "create add upload"));
            list.Add(new("new:change", "New change request", "Create", "plus", "changes/new/ECR", "create ecr engineering change"));
        }
        list.Add(new(ToggleTheme, "Toggle theme", "Commands", "theme", null, "dark light contrast appearance"));
        if (canLock) list.Add(new(LockNow, "Lock now", "Commands", "lock", null, "lock screen session"));
        list.Add(new(SignOut, "Sign out", "Commands", "logout", null, "log out logout"));
        return list;
    }

    /// <summary>Actions whose label or keywords contain every word of the query (case-insensitive). An empty query matches all.</summary>
    public static IReadOnlyList<PaletteAction> Filter(IEnumerable<PaletteAction> actions, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return actions.Where(a => words.All(w => a.Label.Contains(w, StringComparison.OrdinalIgnoreCase) || a.Keywords.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
    }
}

/// <summary>Per-circuit open/close state of the command palette, so the top bar button and the keyboard shortcut open the same dialog.</summary>
public sealed class PaletteState
{
    public bool IsOpen { get; private set; }
    public event Action? Changed;

    public void Open()
    {
        if (IsOpen) return;
        IsOpen = true;
        Changed?.Invoke();
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        Changed?.Invoke();
    }
}
