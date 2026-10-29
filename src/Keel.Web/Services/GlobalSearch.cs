using System.Globalization;
using Keel.Bom;
using Keel.Change;
using Keel.Documents;
using Keel.Parts;
using Keel.Sourcing;
using Keel.Workspace;

namespace Keel.Web.Services;

/// <summary>One result of the command palette or the search page. It is always built from what a module service returned to the signed-in user.</summary>
public sealed record SearchHit(string Kind, string Number, string Name, string StateLabel, string Banner, string Href, int Rank);

/// <summary>A page of results of one object kind with the number of objects the user may read that match (never a count of hidden ones).</summary>
public sealed record KindResults(string Kind, IReadOnlyList<SearchHit> Hits, long Total, string Banner);

/// <summary>What the palette and the search page ask for. The implementation reads through the module services only (access filter in their SQL).</summary>
public interface IGlobalSearch
{
    /// <summary>Searches the kinds named by the query's prefix (all kinds without one), merged and ranked: exact number, number prefix, then the service's text order.</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(string rawQuery, int perKindLimit, CancellationToken ct);

    /// <summary>One page of one kind, for the search page's tabs.</summary>
    Task<KindResults> SearchKindAsync(string kind, string text, int limit, int offset, CancellationToken ct);

    /// <summary>The user's recently viewed objects that they can still read (shown when the palette opens with nothing typed).</summary>
    Task<IReadOnlyList<SearchHit>> RecentAsync(int take, CancellationToken ct);
}

/// <summary>A search string with its optional typed prefix: <c>p:</c> parts, <c>d:</c> documents, <c>c:</c>/<c>ecn:</c>/<c>ecr:</c>/<c>pr:</c> changes, <c>b:</c> baselines, <c>mb:</c> manufacturing BOMs, <c>s:</c> suppliers.</summary>
public sealed record ParsedQuery(string Text, IReadOnlyList<string> Kinds, string? ChangeType)
{
    public const int MinLength = 2;

    public static ParsedQuery Parse(string? raw)
    {
        var text = (raw ?? "").Trim();
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon is > 0 and <= 4)
        {
            var prefix = text[..colon].ToLowerInvariant();
            var rest = text[(colon + 1)..].Trim();
            switch (prefix)
            {
                case "p": return new(rest, [ObjectKinds.Part], null);
                case "d": return new(rest, [ObjectKinds.Document], null);
                case "c": return new(rest, [ObjectKinds.Change], null);
                case "ecn": return new(rest, [ObjectKinds.Change], "ECN");
                case "ecr": return new(rest, [ObjectKinds.Change], "ECR");
                case "pr": return new(rest, [ObjectKinds.Change], "PR");
                case "b": return new(rest, [ObjectKinds.Baseline], null);
                case "mb": return new(rest, [ObjectKinds.Mbom], null);
                case "s": return new(rest, [ObjectKinds.Supplier], null);
            }
        }
        return new(text, ObjectKinds.All, null);
    }

    /// <summary>Exact number match ranks 0, a number prefix 1, anything else 2 (then the service's own order).</summary>
    public static int RankOf(string number, string text)
    {
        if (text.Length == 0) return 2;
        if (string.Equals(number, text, StringComparison.OrdinalIgnoreCase)) return 0;
        return number.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }
}

/// <summary>Display text for state codes that modules return unformatted (parts and documents list their state code, not its label).</summary>
public static class StateText
{
    public static string Label(string state) => state switch
    {
        "InWork" => "In Work",
        "UnderReview" => "Under Review",
        _ when state.Length == 0 => state,
        _ => AddSpaces(state),
    };

    private static string AddSpaces(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 2);
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && char.IsLower(s[i - 1])) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Global search through the modules' own search methods (ADR-0028): each one applies <c>AccessPolicy.BuildReadFilter</c> in its SQL and audits the search, so
/// nothing the user may not read can appear, and a total is a count of what they may read. No result is cached or stored. Results are merged and ranked here.
/// </summary>
public sealed class GlobalSearchService(CurrentUser users, PartService parts, DocumentService documents, ChangeService changes, BaselineService baselines, MbomService mboms, WorkspaceReads reads, SupplierService? suppliers = null) : IGlobalSearch
{
    public async Task<IReadOnlyList<SearchHit>> RecentAsync(int take, CancellationToken ct) =>
        (await reads.RecentAsync(take, ct)).Items.Select(i => new SearchHit(i.Kind, i.Number, i.Name, i.StateLabel, i.Banner, i.Href, 0)).ToList();

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string rawQuery, int perKindLimit, CancellationToken ct)
    {
        var q = ParsedQuery.Parse(rawQuery);
        if (q.Text.Length < ParsedQuery.MinLength) return [];
        var tasks = q.Kinds.Select(k => SearchKindAsync(k, q.Text, perKindLimit, 0, q.ChangeType, ct)).ToList();
        var results = await Task.WhenAll(tasks);
        return Merge(results.SelectMany(r => r.Hits));
    }

    /// <summary>Exact numbers first, then prefixes, then text matches; kinds keep their fixed order and each service's order inside a rank.</summary>
    public static IReadOnlyList<SearchHit> Merge(IEnumerable<SearchHit> hits) =>
        hits.Select((h, i) => (h, i)).OrderBy(x => x.h.Rank).ThenBy(x => ObjectKinds.All.IndexOf(x.h.Kind)).ThenBy(x => x.i).Select(x => x.h).ToList();

    public Task<KindResults> SearchKindAsync(string kind, string text, int limit, int offset, CancellationToken ct) => SearchKindAsync(kind, text, limit, offset, null, ct);

    private async Task<KindResults> SearchKindAsync(string kind, string text, int limit, int offset, string? changeType, CancellationToken ct)
    {
        var subject = await users.GetAsync();
        var corr = CurrentUser.NewCorrelationId();
        text = text.Trim();
        SearchHit Hit(string number, string name, string state, string banner) =>
            new(kind, number, name, state, banner, ObjectKinds.Path(kind, number), ParsedQuery.RankOf(number, text));
        switch (kind)
        {
            case ObjectKinds.Part:
            {
                var r = await parts.SearchAsync(subject, text, null, limit, offset, corr, ct);
                return new(kind, r.Items.Select(p => Hit(p.Number, p.Name, StateText.Label(p.State), p.Banner)).ToList(), r.Total, r.PageBanner);
            }
            case ObjectKinds.Document:
            {
                var r = await documents.SearchAsync(subject, text, null, null, limit, offset, corr, ct);
                return new(kind, r.Items.Select(d => Hit(d.Number, d.Name, StateText.Label(d.State), d.Banner)).ToList(), r.Total, r.PageBanner);
            }
            case ObjectKinds.Change:
            {
                var r = await changes.SearchAsync(subject, new ChangeSearchQuery(Type: changeType, Text: text, Limit: limit, Offset: offset), corr, ct);
                return new(kind, r.Items.Select(c => Hit(c.Number, c.Title, c.StateLabel, c.Banner)).ToList(), r.Total, r.PageBanner);
            }
            case ObjectKinds.Baseline:
            {
                var o = await baselines.SearchAsync(subject, new BaselineSearch(Text: text.Length == 0 ? null : text, Limit: limit, Offset: offset), corr, ct);
                if (o.Value is not { } page) return new(kind, [], 0, UiText.Unclassified);
                return new(kind, page.Items.Select(b => Hit(b.Number, b.Name, b.StateLabel, b.Banner)).ToList(), page.Total, page.Banner);
            }
            case ObjectKinds.Mbom:
            {
                var o = await mboms.SearchAsync(subject, new MbomSearch(Query: text, Limit: limit, Offset: offset), corr, ct);
                if (o.Value is not { } page) return new(kind, [], 0, UiText.Unclassified);
                return new(kind, page.Items.Select(m => Hit(m.Number, $"{m.PartName} ({m.Plant})", m.StateLabel, m.Banner)).ToList(), page.Total, page.PageBanner);
            }
            case ObjectKinds.Supplier:
            {
                if (suppliers is null) return new(kind, [], 0, UiText.Unclassified);
                var o = await suppliers.SearchAsync(subject, new SupplierSearch(Text: text, Limit: limit, Offset: offset), corr, ct);
                if (o.Value is not { } page) return new(kind, [], 0, UiText.Unclassified);
                return new(kind, page.Items.Select(sp => Hit(sp.Number, sp.Name, sp.StatusLabel, sp.Banner)).ToList(), page.Total, page.Banner);
            }
            default:
                throw new NotSupportedException($"Search of '{kind}' is not available.");
        }
    }

    /// <summary>Formats a total for display ("1 part", "3 parts").</summary>
    public static string Count(long n, string singular, string plural) => string.Create(CultureInfo.InvariantCulture, $"{n} {(n == 1 ? singular : plural)}");
}
