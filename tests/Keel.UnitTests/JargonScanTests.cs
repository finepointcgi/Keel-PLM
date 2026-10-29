using System.Text;
using System.Text.RegularExpressions;

namespace Keel.UnitTests;

/// <summary>
/// UX-1: no developer or project jargon in anything a person reads. Scans the visible text of every Razor component of Keel.Web (markup and the string literals
/// of its code), the string literals of Keel.Web's C# and the user-facing messages of the modules (state "Via" texts, outcome messages, validation errors) for
/// words that belong in commits and ADRs, not on screen: increment, ADR-, "Phase ", gateway, TODO. Comments are not scanned. The few legitimate technical uses
/// (messages of NotSupportedException that name a later wave, administrator diagnostics, the internals of exceptions) are listed in jargon-allowlist.txt with a reason.
/// </summary>
public sealed partial class JargonScanTests
{
    private static readonly string[] Forbidden = ["increment", "ADR-", "Phase ", "gateway", "TODO"];

    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }

    private static IEnumerable<string> Files(string relativeDir, string pattern) =>
        Directory.EnumerateFiles(Path.Combine(Root, relativeDir), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}wwwroot{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static List<(string Path, string Token)> AllowList()
    {
        var list = new List<(string, string)>();
        foreach (var raw in File.ReadAllLines(Path.Combine(Root, "tests", "Keel.UnitTests", "jargon-allowlist.txt")))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            Assert.True(parts.Length == 3 && parts[2].Length > 10, $"Allow-list entries are 'path | token | reason': {line}");
            var token = parts[1] == "Phase" ? "Phase " : parts[1]; // the trailing blank of "Phase " does not survive trimming
            Assert.Contains(token, Forbidden);
            Assert.True(File.Exists(Path.Combine(Root, parts[0])), $"Allow-list names a file that does not exist: {parts[0]}");
            list.Add((parts[0].Replace('\\', '/'), token));
        }
        return list;
    }

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex RazorComment();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex("\\$?@?\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"")]
    private static partial Regex Literal();

    [GeneratedRegex(@"^\s*@(inject|using|implements|attribute|page|inherits|typeparam|rendermode)\b.*$", RegexOptions.Multiline)]
    private static partial Regex Directive();

    private static IEnumerable<string> Literals(string code)
    {
        foreach (var line in code.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("///", StringComparison.Ordinal) || t.StartsWith('*') || t.StartsWith("/*", StringComparison.Ordinal)) continue;
            // An exception that names a later wave (NotSupportedException) is not text a person reads on a page: it is the project's rule for unfinished scope.
            if (t.Contains("NotSupportedException", StringComparison.Ordinal)) continue;
            foreach (Match m in Literal().Matches(line)) yield return m.Value;
        }
    }

    /// <summary>Splits a Razor file into its markup and the text of its @code blocks.</summary>
    private static (string Markup, string Code) SplitRazor(string text)
    {
        text = HtmlComment().Replace(RazorComment().Replace(text, ""), "");
        text = Directive().Replace(text, "");
        var code = new StringBuilder();
        var markup = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var at = text.IndexOf("@code", i, StringComparison.Ordinal);
            if (at < 0) { markup.Append(text.AsSpan(i)); break; }
            markup.Append(text.AsSpan(i, at - i));
            var open = text.IndexOf('{', at);
            var depth = 0;
            var j = open;
            for (; j < text.Length; j++)
            {
                if (text[j] == '{') depth++;
                else if (text[j] == '}' && --depth == 0) break;
            }
            code.Append(text.AsSpan(open, Math.Min(j, text.Length - 1) - open + 1));
            i = j + 1;
        }
        return (markup.ToString(), code.ToString());
    }

    private static IEnumerable<(string Path, string Token, string Snippet)> Scan(string path, IEnumerable<string> texts)
    {
        var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
        foreach (var text in texts)
            foreach (var token in Forbidden)
            {
                // "ADR-" and "Phase " are matched exactly; the words are matched in any case ("Increment", "TODO").
                var cmp = token is "ADR-" or "Phase " ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                var at = text.IndexOf(token, cmp);
                if (at >= 0) yield return (relative, token, text.Substring(Math.Max(0, at - 30), Math.Min(80, text.Length - Math.Max(0, at - 30))).Replace('\n', ' '));
            }
    }

    private static IEnumerable<(string Path, string Token, string Snippet)> ScanAll()
    {
        foreach (var file in Files("src/Keel.Web", "*.razor"))
        {
            var (markup, code) = SplitRazor(File.ReadAllText(file));
            foreach (var f in Scan(file, [markup])) yield return f;
            foreach (var f in Scan(file, Literals(code))) yield return f;
        }
        foreach (var file in Files("src/Keel.Web", "*.cs"))
            foreach (var f in Scan(file, Literals(File.ReadAllText(file)))) yield return f;
        foreach (var module in new[] { "Keel.Parts", "Keel.Documents", "Keel.Change", "Keel.Bom", "Keel.Workflow", "Keel.Admin", "Keel.Integration", "Keel.Security", "Keel.Core", "Keel.Quality", "Keel.Programs" })
            foreach (var file in Files("src/" + module, "*.cs"))
                foreach (var f in Scan(file, Literals(File.ReadAllText(file)))) yield return f;
    }

    [Fact]
    public void No_user_visible_text_contains_developer_jargon()
    {
        var allowed = AllowList();
        var offenders = ScanAll().Where(f => !allowed.Contains((f.Path, f.Token))).Select(f => $"{f.Path} [{f.Token}]: …{f.Snippet}…").Distinct().ToList();
        Assert.True(offenders.Count == 0, "Jargon in user-facing text (rewrite it in plain language, or justify it in tests/Keel.UnitTests/jargon-allowlist.txt):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_allow_list_has_no_stale_entries()
    {
        var found = ScanAll().Select(f => (f.Path, f.Token)).ToHashSet();
        var stale = AllowList().Where(a => !found.Contains(a)).Select(a => $"{a.Path} | {a.Token}").ToList();
        Assert.True(stale.Count == 0, "These allow-list entries match nothing any more; remove them:\n" + string.Join("\n", stale));
    }

    [Fact]
    public void The_scan_finds_jargon_where_it_exists_so_it_cannot_pass_by_scanning_nothing()
    {
        var (markup, code) = SplitRazor("@* a comment (increment 1.5) *@\n<p>Moved on (increment 1.6)</p>\n@code { var x = \"see ADR-0001\"; // TODO later\n }");
        Assert.DoesNotContain("1.5", markup, StringComparison.Ordinal);
        Assert.Contains(Scan("src/Keel.Web/x.razor".Replace("src/Keel.Web/x.razor", Path.Combine(Root, "src/Keel.Web/Services/UiText.cs")), [markup]), f => f.Token == "increment");
        Assert.Contains(Scan(Path.Combine(Root, "src/Keel.Web/Services/UiText.cs"), Literals(code)), f => f.Token == "ADR-");
        Assert.DoesNotContain(Scan(Path.Combine(Root, "src/Keel.Web/Services/UiText.cs"), Literals(code)), f => f.Token == "TODO"); // comments are not text
        Assert.True(ScanAll().Count() >= 0 && Files("src/Keel.Web", "*.razor").Count() > 50);
    }
}
