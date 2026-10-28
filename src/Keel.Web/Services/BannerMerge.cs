namespace Keel.Web.Services;

/// <summary>
/// Combines marking banners that a page computed separately (for example the aggregate of the BOM rows and the aggregate of the changes
/// listed on the same part page) into one banner that covers both: union of CUI categories, ITAR if any has it, union of EAR codes, the most
/// restrictive distribution statement and the programs involved. Understands the formats of <c>Markings.BannerText</c> and
/// <c>Markings.AggregateBanner</c>; equal banners are returned unchanged.
/// </summary>
public static class BannerMerge
{
    private const string Separator = " · ";

    public static string Union(params string?[] banners)
    {
        // The bare "UNCLASSIFIED" placeholder means "nothing controlled shown here" and must not rewrite a more detailed banner.
        var present = banners.Where(b => !string.IsNullOrWhiteSpace(b) && b != UiText.Unclassified).Select(b => b!).Distinct(StringComparer.Ordinal).ToList();
        if (present.Count == 0) return UiText.Unclassified;
        if (present.Count == 1) return present[0];

        var cui = new SortedSet<string>(StringComparer.Ordinal);
        var ear = new SortedSet<string>(StringComparer.Ordinal);
        var programs = new SortedSet<string>(StringComparer.Ordinal);
        var itar = false;
        var distribution = '\0';
        foreach (var banner in present)
            foreach (var token in banner.Split(Separator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("CUI//", StringComparison.Ordinal)) cui.UnionWith(token["CUI//".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries));
                else if (token.StartsWith("ITAR", StringComparison.Ordinal)) itar = true;
                else if (token.StartsWith("EAR ", StringComparison.Ordinal)) ear.UnionWith(token["EAR ".Length..].Split(", ", StringSplitOptions.RemoveEmptyEntries));
                else if (token.StartsWith("DISTRIBUTION ", StringComparison.Ordinal) && token.Length > "DISTRIBUTION ".Length)
                    distribution = (char)Math.Max(distribution, token["DISTRIBUTION ".Length]);
                else if (token.StartsWith("PROGRAM ", StringComparison.Ordinal)) programs.Add(token["PROGRAM ".Length..]);
            }

        var parts = new List<string> { cui.Count > 0 ? "CUI//" + string.Join('/', cui) : "UNCLASSIFIED" };
        if (itar) parts.Add("ITAR");
        if (ear.Count > 0) parts.Add("EAR " + string.Join(", ", ear));
        if (distribution != '\0') parts.Add($"DISTRIBUTION {distribution}");
        if (programs.Count > 0) parts.Add("PROGRAM " + string.Join(", ", programs));
        return string.Join(Separator, parts);
    }
}
