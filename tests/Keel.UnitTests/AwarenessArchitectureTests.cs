using System.Text.RegularExpressions;

namespace Keel.UnitTests;

/// <summary>UX-4 (ADR-0030) rules enforced in CI: pointer-only storage, per-user ownership, read checks before comments, fan-out checks per recipient, nothing rendered while locked.</summary>
public sealed partial class AwarenessArchitectureTests
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

    private static IEnumerable<string> Sources(string folder) =>
        Directory.EnumerateFiles(Path.Combine(Root, "src", folder), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    // Tokenizes the C# string literals of a file (raw triple-quoted strings first), so each SQL statement is examined on its own.
    [GeneratedRegex("(\"\"\"[\\s\\S]*?\"\"\"|\"(?:[^\"\\\\\\n]|\\\\.)*\")")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\b(?:FROM|UPDATE|DELETE FROM)\s+ntf\.(?:notification|watch|preference|kind_preference)\b")]
    private static partial Regex OwnedTable();

    [Fact]
    public void Every_statement_over_a_personal_table_names_the_signed_in_user()
    {
        // Notifications, watches and preferences belong to one user. A statement over them either filters on the user's id or is one of the system statements listed here
        // (the writer, which addresses a recipient it was given, and the fan-out and retention services, which have no signed-in user).
        // EmailBounces.cs: the bounce service works on an address the relay reported and on the administrator's overview, not on a signed-in user's own rows (ADR-0055); its
        // writes and reads are administrator-only or system-driven and every method says so in its first lines.
        var systemFiles = new[] { "NotificationWriter.cs", "FanOutService.cs", "EmailBounces.cs" };
        var checkedStatements = 0;
        foreach (var file in Sources("Keel.Notifications"))
        {
            var name = Path.GetFileName(file);
            foreach (Match literal in StringLiteral().Matches(File.ReadAllText(file)))
            {
                if (!OwnedTable().IsMatch(literal.Value)) continue;
                checkedStatements++;
                if (systemFiles.Contains(name)) continue;
                Assert.True(literal.Value.Contains("recipient_sub", StringComparison.Ordinal) || literal.Value.Contains("user_sub", StringComparison.Ordinal),
                    $"{name}: a statement over a personal table that does not filter on the user:\n{literal.Value}");
            }
        }
        Assert.True(checkedStatements >= 20, $"The rule found only {checkedStatements} statements; the pattern is probably broken.");
        Assert.DoesNotContain("SELECT * FROM ntf.", Read("src", "Keel.Notifications", "NotificationCenter.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void Comment_bodies_are_read_only_by_the_comment_service_and_only_after_the_object_was_read()
    {
        // 1. Only CommentService touches the comment table.
        var offenders = Sources("Keel.Notifications").Where(f => !f.EndsWith("CommentService.cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("ntf.comment", StringComparison.Ordinal)).Select(Path.GetFileName).ToList();
        Assert.True(offenders.Count == 0, "Files other than CommentService read or write comments: " + string.Join(", ", offenders));
        foreach (var other in new[] { "Keel.Web", "Keel.Api" })
            Assert.DoesNotContain(Sources(other).Concat(Directory.EnumerateFiles(Path.Combine(Root, "src", other), "*.razor", SearchOption.AllDirectories)), f => File.ReadAllText(f).Contains("ntf.comment", StringComparison.Ordinal));

        // 2. Every method that reads or writes the table checks the object first (the first statement over ntf.comment comes after the read check).
        var text = Read("src", "Keel.Notifications", "CommentService.cs");
        foreach (var method in new[] { "ListAsync", "CountAsync", "PostAsync", "MentionCandidatesAsync" })
        {
            var start = text.IndexOf($" {method}(", StringComparison.Ordinal);
            Assert.True(start > 0, method);
            var body = text[start..];
            var readCheck = Regex.Match(body, @"ReadableAsync\(|objects\.FindAsync\(");
            var firstSql = body.IndexOf("ntf.comment", StringComparison.Ordinal);
            Assert.True(readCheck.Success && (firstSql < 0 || readCheck.Index < firstSql), $"{method} touches ntf.comment before checking read access to the object");
        }
        var delete = text[text.IndexOf(" DeleteAsync(", StringComparison.Ordinal)..];
        Assert.True(delete.IndexOf("objects.FindAsync(", StringComparison.Ordinal) < delete.IndexOf("UPDATE ntf.comment", StringComparison.Ordinal), "DeleteAsync removes a comment without checking access to its object");
    }

    [Fact]
    public void Comment_audit_events_never_carry_the_text()
    {
        var text = Read("src", "Keel.Notifications", "CommentService.cs");
        foreach (Match m in Regex.Matches(text, @"AuditDetails\.Of\((.*?)\)\s*,?\s*\}", RegexOptions.Singleline))
        {
            Assert.DoesNotMatch(@"""(body|text)""", m.Groups[1].Value);
            Assert.DoesNotMatch(@"\(\s*""[A-Za-z]+""\s*,\s*(text|body)\s*\)", m.Groups[1].Value); // a ("key", text) pair
        }
        Assert.Contains("comment.create", text, StringComparison.Ordinal);
        Assert.Contains("comment.delete", text, StringComparison.Ordinal);
        // The create event is written through the caller's transaction, so it commits (or not) with the comment.
        Assert.Matches(@"audit\.AppendAsync\(tx, new AuditEntry\s*\{[^}]*""comment\.create""", text.Replace("\n", " ", StringComparison.Ordinal));
    }

    [Fact]
    public void Notification_rows_hold_pointers_only()
    {
        var sql = Read("db", "migrations", "0015_awareness.sql");
        var table = Regex.Match(sql, @"CREATE TABLE ntf\.notification \((.*?)\n\);", RegexOptions.Singleline).Groups[1].Value;
        Assert.NotEmpty(table);
        var columns = Regex.Matches(table, @"^\s{4}([a-z_]+)\s+", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["id", "recipient_sub", "kind", "object_type", "object_number", "ref_id", "event_id", "actor_sub", "occurrences", "created_at", "seen_at", "read_at"], columns);
        foreach (var banned in new[] { "title", "name", "text", "body", "description", "message", "markings", "banner", "payload", "detail" })
            Assert.DoesNotContain(columns, c => c.Contains(banned, StringComparison.Ordinal));
        // The watch table is a pointer too.
        var watch = Regex.Match(sql, @"CREATE TABLE ntf\.watch \((.*?)\n\);", RegexOptions.Singleline).Groups[1].Value;
        Assert.DoesNotContain("name", Regex.Matches(watch, @"^\s{4}([a-z_]+)\s+", RegexOptions.Multiline).Select(m => m.Groups[1].Value));
        // The comment table has no marking columns: comments inherit their object's markings dynamically.
        var comment = Regex.Match(sql, @"CREATE TABLE ntf\.comment \((.*?)\n\);", RegexOptions.Singleline).Groups[1].Value;
        Assert.DoesNotContain("export_jurisdiction", comment, StringComparison.Ordinal);
        Assert.Contains("CREATE TRIGGER comment_guard BEFORE INSERT OR UPDATE OR DELETE", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TRIGGER comment_no_truncate BEFORE TRUNCATE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fan_out_checks_every_recipient_against_the_events_markings_and_the_only_writer_of_notifications_is_the_writer()
    {
        var writer = Read("src", "Keel.Notifications", "NotificationWriter.cs");
        Assert.Contains("ObjectAccessResolver.CanRead(person.Subject, request.Markings)", writer, StringComparison.Ordinal);
        Assert.Contains("DirectoryReader.LoadAsync", writer, StringComparison.Ordinal);
        // Only the writer inserts notification rows, so the check cannot be bypassed from elsewhere in the module.
        var inserters = Sources("Keel.Notifications").Where(f => Regex.IsMatch(File.ReadAllText(f), @"INSERT INTO ntf\.notification\b")).Select(Path.GetFileName).ToList();
        Assert.Equal(["NotificationWriter.cs"], inserters);
        var fan = Read("src", "Keel.Notifications", "FanOutService.cs");
        Assert.Contains("writer.WriteAsync(", fan, StringComparison.Ordinal);
        Assert.Contains("created_in_tx < pg_snapshot_xmin(pg_current_snapshot())", fan, StringComparison.Ordinal); // only committed events
        // No other module writes to the ntf schema or reads it: they use INotificationSink.
        foreach (var module in new[] { "Keel.Parts", "Keel.Documents", "Keel.Workflow", "Keel.Change", "Keel.Bom" })
            Assert.DoesNotContain(Sources(module), f => Regex.IsMatch(File.ReadAllText(f), @"\bntf\.\w+"));
    }

    [Fact]
    public void The_bell_exists_only_in_the_unlocked_part_of_the_layout()
    {
        var layout = Read("src", "Keel.Web", "Components", "Layout", "MainLayout.razor");
        var bell = layout.IndexOf("<NotificationBell", StringComparison.Ordinal);
        Assert.True(bell > 0);
        var guard = layout.LastIndexOf("@if (!locked)", bell, StringComparison.Ordinal);
        var elseBranch = layout.IndexOf("else", bell, StringComparison.Ordinal);
        Assert.True(guard > 0 && guard < bell && bell < elseBranch, "The bell must sit inside the @if (!locked) branch of the top bar");
        // The bell reads through CurrentUser, which refuses a locked session, and clears on that refusal.
        var component = Read("src", "Keel.Web", "Components", "Shared", "Awareness", "NotificationBell.razor");
        Assert.Contains("Users.GetAsync()", component, StringComparison.Ordinal);
        Assert.Contains("clears the bell", component, StringComparison.Ordinal);
    }

    [Fact]
    public void Awareness_components_and_pages_reach_data_only_through_the_services_and_never_through_sql()
    {
        var folders = new[] { Path.Combine(Root, "src", "Keel.Web", "Components", "Shared", "Awareness"), Path.Combine(Root, "src", "Keel.Web", "Components", "Pages", "Notifications") };
        var files = folders.SelectMany(f => Directory.EnumerateFiles(f, "*.razor")).ToList();
        Assert.True(files.Count >= 9);
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain("NpgsqlCommand", text, StringComparison.Ordinal);
            Assert.DoesNotContain("NpgsqlDataSource", text, StringComparison.Ordinal);
            Assert.DoesNotContain("@inject NpgsqlDataSource", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ntf.", text, StringComparison.Ordinal);
            Assert.DoesNotContain("MarkupString", text, StringComparison.Ordinal); // comment text and notification text are never rendered as markup
        }
        // The three new pages are real pages inside the shell's contract.
        foreach (var page in new[] { "NotificationsPage.razor", "PreferencesPage.razor", "WatchListPage.razor" })
        {
            var text = File.ReadAllText(Path.Combine(folders[1], page));
            Assert.Contains("@page \"", text, StringComparison.Ordinal);
            Assert.Contains("<PageFrame ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("AllowAnonymous", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Object_pages_carry_the_watch_button_the_activity_and_comments_tabs_and_the_count_loader()
    {
        var pages = new Dictionary<string, string>
        {
            ["Parts/PartPage.razor"] = "Part", ["Documents/DocumentPage.razor"] = "Document", ["Change/ChangePage.razor"] = "Change",
            ["Configuration/BaselineDetailPage.razor"] = "Baseline", ["Mbom/MbomPage.razor"] = "Mbom",
        };
        foreach (var (file, kind) in pages)
        {
            var text = Read("src", "Keel.Web", "Components", "Pages", file);
            Assert.Contains($"<WatchButton Kind=\"@ObjectKinds.{kind}\"", text, StringComparison.Ordinal);
            Assert.Contains($"<CommentCount Kind=\"@ObjectKinds.{kind}\"", text, StringComparison.Ordinal);
            Assert.Contains($"<ActivityFeed Kind=\"@ObjectKinds.{kind}\"", text, StringComparison.Ordinal);
            Assert.Contains($"<CommentThread Kind=\"@ObjectKinds.{kind}\"", text, StringComparison.Ordinal);
            Assert.Contains("AwarenessTabs.Ids", text, StringComparison.Ordinal);
            Assert.Contains("AwarenessTabs.Append(", text, StringComparison.Ordinal);
        }
        Assert.Contains("<WatchedActivityCard", Read("src", "Keel.Web", "Components", "Pages", "Home.razor"), StringComparison.Ordinal);
    }

    [Fact]
    public void Module_hooks_are_optional_and_only_the_notifications_module_owns_the_ntf_schema()
    {
        // The owning modules take the sink as an optional constructor parameter (null means no notifications), so their own tests and the seeder are unaffected.
        foreach (var (file, signature) in new[]
        {
            ("Keel.Parts/PartService.cs", "INotificationSink? notifications = null"), ("Keel.Documents/DocumentService.cs", "INotificationSink? notifications = null"),
            ("Keel.Workflow/WorkflowService.cs", "INotificationSink? notifications = null"), ("Keel.Change/ChangeService.cs", "INotificationSink? notifications = null"),
            ("Keel.Change/ChangeWorkflowGateway.cs", "INotificationSink? notifications = null"),
        })
            Assert.Contains(signature, Read("src", file), StringComparison.Ordinal);
        // The object lookups are registered by the modules that own the objects.
        Assert.Contains("IObjectAccess, ChangeObjectAccess", Read("src", "Keel.Change", "ChangeModule.cs"), StringComparison.Ordinal);
        Assert.Contains("IObjectAccess, BaselineObjectAccess", Read("src", "Keel.Bom", "BomModule.cs"), StringComparison.Ordinal);
        Assert.Contains("IObjectAccess, MbomObjectAccess", Read("src", "Keel.Bom", "BomModule.cs"), StringComparison.Ordinal);
    }
}
