using System.Text.RegularExpressions;

namespace Keel.UnitTests;

/// <summary>
/// GAP-A-3: every page has one h1 (the frame's) and a heading outline with no skipped level. The check reads the Razor sources: a page's outline is its own
/// headings in source order, with each component tag expanded to that component's outline (so a panel that starts at h3 is only fine under an h2).
/// Dialogs are their own context (modal) and are left out; the object header replaces the frame's h1, it does not add one.
/// </summary>
public sealed partial class HeadingOutlineTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }

    [GeneratedRegex(@"<h([1-6])[\s>]|<([A-Z][A-Za-z0-9]*)[\s/>]")]
    private static partial Regex Token();

    private static string Strip(string text) =>
        Regex.Replace(Regex.Replace(text, @"@\*.*?\*@", "", RegexOptions.Singleline), @"<!--.*?-->", "", RegexOptions.Singleline);

    private static Dictionary<string, string> Components() =>
        Directory.EnumerateFiles(Path.Combine(Root, "src", "Keel.Web"), "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .GroupBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private static List<int> Outline(string file, IReadOnlyDictionary<string, string> components, int depth = 0)
    {
        var text = Strip(File.ReadAllText(file));
        var levels = new List<int>();
        foreach (Match m in Token().Matches(text))
        {
            if (m.Groups[1].Success) { levels.Add(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)); continue; }
            var name = m.Groups[2].Value;
            if (depth > 5 || name == "ObjectHeader" || !components.TryGetValue(name, out var path) || path == file) continue;
            if (File.ReadAllText(path).Contains("role=\"dialog\"", StringComparison.Ordinal)) continue; // a modal is its own heading context
            levels.AddRange(Outline(path, components, depth + 1));
        }
        return levels;
    }

    private static string? Problem(List<int> outline, bool page)
    {
        if (page && outline.Count(l => l == 1) != 1) return $"{outline.Count(l => l == 1)} h1 elements (expected exactly one, the page frame's)";
        var prev = 0;
        for (var i = 0; i < outline.Count; i++)
        {
            // A page starts at h1; a component only has to be consistent in itself, so its first heading sets the base.
            if (prev == 0 && !page) prev = outline[i] - 1;
            if (outline[i] > prev + 1) return $"h{outline[i]} follows h{prev} (outline h{string.Join(" h", outline)})";
            prev = outline[i];
        }
        return null;
    }

    [Fact]
    public void Every_page_has_one_h1_and_an_outline_without_skipped_levels()
    {
        var components = Components();
        var offenders = new List<string>();
        foreach (var (name, path) in components)
        {
            if (!Strip(File.ReadAllText(path)).Contains("@page \"", StringComparison.Ordinal)) continue;
            if (Problem(Outline(path, components), page: true) is { } why) offenders.Add($"{Path.GetRelativePath(Root, path)}: {why}");
        }
        Assert.True(offenders.Count == 0, "Heading outline problems:\n" + string.Join("\n", offenders.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void Components_do_not_skip_heading_levels_inside_themselves()
    {
        var components = Components();
        var offenders = new List<string>();
        foreach (var (_, path) in components)
        {
            if (Strip(File.ReadAllText(path)).Contains("@page \"", StringComparison.Ordinal)) continue;
            if (Problem(Outline(path, components), page: false) is { } why) offenders.Add($"{Path.GetRelativePath(Root, path)}: {why}");
        }
        Assert.True(offenders.Count == 0, "Heading outline problems:\n" + string.Join("\n", offenders.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void The_outline_check_itself_catches_a_skipped_level()
    {
        Assert.NotNull(Problem([1, 3], page: true));
        Assert.NotNull(Problem([1, 2, 4], page: true));
        Assert.Null(Problem([1, 2, 3, 3, 2, 3], page: true));
        Assert.NotNull(Problem([1, 1], page: true));
        Assert.Null(Problem([3, 4, 3], page: false));
    }
}
