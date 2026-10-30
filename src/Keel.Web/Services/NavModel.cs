using System.Security.Claims;
using Keel.Core;

namespace Keel.Web.Services;

/// <summary>One link of the navigation rail.</summary>
public sealed record NavItem(string Label, string Href, string Icon, bool ExactMatch = false);

/// <summary>A titled group of links. The group is shown only when at least one link is (links are filtered by the user's roles).</summary>
public sealed record NavGroup(string Title, IReadOnlyList<NavItem> Items);

/// <summary>
/// The grouped navigation (ADR-0028): what the rail shows, decided from the roles of the user. Hiding a link is a convenience; every page and every
/// service call still checks the role (the Administration pages carry the administrator policy, the audit page the auditor policy).
/// </summary>
public static class NavModel
{
    /// <summary>Groups the Simple view (ADR 0061) hides. Everything in them stays reachable by search, the command palette and the Home page; role groups are untouched.</summary>
    public static readonly IReadOnlySet<string> AdvancedGroups = new HashSet<string>(StringComparer.Ordinal) { "Programs", "Quality", "Sourcing", "Output" };

    /// <summary>Links of "Product data" the Simple view keeps.</summary>
    private static readonly HashSet<string> SimpleProductLinks = new HashSet<string>(StringComparer.Ordinal) { "parts", "documents", "mboms" };

    /// <summary>
    /// The navigation for the Simple view: the everyday groups (Work, Parts, Documents, Manufacturing BOMs, Changes) and the groups the person's roles add
    /// (Records, Configuration, Administration, the audit log). A display preference only: no page or service decides anything from it.
    /// </summary>
    public static IReadOnlyList<NavGroup> For(IReadOnlyCollection<string> roles, bool simple)
    {
        var all = For(roles);
        if (!simple) return all;
        var result = new List<NavGroup>();
        foreach (var g in all)
        {
            if (AdvancedGroups.Contains(g.Title)) continue;
            if (g.Title == "Product data") result.Add(g with { Items = [.. g.Items.Where(i => SimpleProductLinks.Contains(i.Href))] });
            else if (g.Title == "Tools") { var audit = g.Items.Where(i => i.Href == "audit").ToList(); if (audit.Count > 0) result.Add(g with { Items = audit }); }
            else result.Add(g);
        }
        return result;
    }

    public static IReadOnlyList<NavGroup> For(IReadOnlyCollection<string> roles)
    {
        var admin = roles.Contains(Roles.Admin);
        var auditor = roles.Contains(Roles.Auditor);
        var groups = new List<NavGroup>
        {
            new("Work", [new("Home", "", "home", ExactMatch: true), new("My tasks", "tasks", "tasks")]),
            new("Product data", [new("Parts", "parts", "part"), new("Documents", "documents", "document"), new("Compare documents", "compare", "document"), new("Manufacturing BOMs", "mboms", "mbom"), new("Process plans", "process/plans", "mbom"), new("Tools and gauges", "process/tools", "folder"), new("Requirements", "requirements", "requirement"), new("Options and variants", "options", "config"), new("CAD structures", "cad/bom-proposals", "part"), new("Compliance", "compliance", "check"), new("Baselines", "baselines", "baseline")]),
            new("Change", [new("Changes", "changes", "change")]),
            new("Programs", [new("Programs", "programs", "program")]),
            new("Quality", [new("Nonconformance reports", "quality/ncrs", "ncr"), new("Corrective actions", "quality/capas", "capa"), new("Deviations and waivers", "quality/deviations", "deviation")]),
            new("Sourcing", [new("Suppliers", "suppliers", "folder"), new("Supplier requests", "requests", "inbox")]),
            new("Output", [new("Reports", "output/reports", "audit"), new("Release packages", "output/packages", "document"), new("Verify a package", "output/verify", "check"), new("BOM in Excel", "output/bom", "document"), new("Mass change", "output/mass", "tasks")]),
        };
        // 8.6 records management: records managers, administrators and auditors (the pages and services check the role again)
        if (roles.Contains(Roles.RecordsManager) || admin || auditor)
            groups.Add(new("Records", [new("Records management", "records", "folder", ExactMatch: true), new("Retention schedules", "records/schedules", "config"), new("Legal holds", "records/holds", "lock"), new("Disposition", "records/disposition", "tasks"), new("Archives", "records/archives", "folder")]));
        if (admin)
        {
            groups.Add(new("Configuration", [new("Workflows", "workflows", "workflow"), new("Lifecycles", "admin/lifecycles", "config"), new("Classification", "admin/classification", "folder")]));
            groups.Add(new("Administration", [new("Administration", "admin", "admin", ExactMatch: true), new("Migration", "admin/migration", "migration"), new("Integration", "admin/integration/sinks", "integration")]));
        }
        var tools = new List<NavItem> { new("Access check", "access", "access"), new("Technical data packages", "tdp", "link"), new("Style guide", "tools/style-guide", "theme") };
        if (auditor) tools.Add(new("Audit log", "audit", "audit"));
        groups.Add(new("Tools", tools));
        return groups;
    }

    /// <summary>The navigation of an external supplier user (8.2): the portal and nothing else.</summary>
    public static IReadOnlyList<NavGroup> ForSupplier() => [new("Supplier portal", [new("My requests", "portal", "inbox", ExactMatch: true), new("My scorecard", "portal/scorecard", "quality")])];

    public static IReadOnlyList<string> RolesOf(ClaimsPrincipal user) => user.FindAll("roles").Select(c => c.Value).ToList();
}
