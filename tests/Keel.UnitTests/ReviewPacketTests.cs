using System.Text.RegularExpressions;

namespace Keel.UnitTests;

/// <summary>
/// The review packets under docs/review (increment 9.3) must exist, say that conclusions are for counsel, and never record an outcome other than
/// "pending" unless a reviewer and a date are given.
/// </summary>
public sealed class ReviewPacketTests
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

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    [Fact]
    public void The_readme_and_the_packets_exist_and_leave_conclusions_to_counsel()
    {
        foreach (var f in new[] { "README.md", "legal-open-source.md", "export-control.md", "data-handling-and-security-facts.md" })
        {
            var path = Path.Combine(Root, "docs", "review", f);
            Assert.True(File.Exists(path), $"docs/review/{f} is missing");
            Assert.Contains("to decide", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_legal_review_page_links_the_packets_and_stays_pending()
    {
        var legal = Read("docs", "third-party", "legal-review.md");
        Assert.Contains("../review/README.md", legal, StringComparison.Ordinal);
        Assert.Contains("../review/legal-open-source.md", legal, StringComparison.Ordinal);
        Assert.Contains("../review/export-control.md", legal, StringComparison.Ordinal);
        Assert.Contains("no legal sign-off", legal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_status_row_is_pending_unless_it_names_a_reviewer_and_a_date()
    {
        var lines = Read("docs", "review", "README.md").ReplaceLineEndings("\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim() == "## Status");
        Assert.True(start >= 0, "README has no '## Status' section");
        var rows = new List<string[]>();
        for (var i = start + 1; i < lines.Length && !lines[i].StartsWith("## ", StringComparison.Ordinal); i++)
        {
            var l = lines[i].Trim();
            if (!l.StartsWith('|') || Regex.IsMatch(l, @"^\|[\s\-|:]+$")) continue;
            rows.Add(l.Trim('|').Split('|').Select(c => c.Trim()).ToArray());
        }
        Assert.True(rows.Count > 1, "status table has no rows");
        Assert.Equal(["Packet", "Reviewer", "Date", "Outcome"], rows[0]);
        foreach (var r in rows.Skip(1))
        {
            Assert.Equal(4, r.Length);
            if (string.Equals(r[3], "pending", StringComparison.OrdinalIgnoreCase)) continue;
            Assert.True(r[1].Length > 0 && r[1] != "-", $"'{r[0]}' claims '{r[3]}' without a reviewer");
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", r[2]);
        }
    }
}
